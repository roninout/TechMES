using DevExpress.Xpf.Core;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using TechEquipments.ViewModels;
using TechEquipments.Views.Info;
using DevExpress.Pdf;
using DevExpress.Xpf.DocumentViewer;
using DevExpress.Xpf.PdfViewer;
using System.Windows.Threading;

namespace TechEquipments
{
    /// <summary>
    /// Контроллер вкладки Info:
    /// - загрузка карточки
    /// - edit/save
    /// - page switching
    /// - работа с фото / instruction / scheme
    /// - cache PDF in LocalAppData
    /// </summary>
    public sealed class InfoController
    {
        private readonly IEquipInfoService _equipInfoService;
        private readonly IQrScannerService _qrScannerService;

        private readonly InfoViewModel _vm;
        private readonly EquipmentListViewModel _equipmentVm;
        private readonly Window _ownerWindow;
        private readonly Dictionary<long, byte[]> _photoBytesCache = new();

        private int _loadCurrentRequestId;
        private bool _suppressLibrarySelectionSync;

        public InfoController(IEquipInfoService equipInfoService, InfoViewModel vm, EquipmentListViewModel equipmentVm, Window ownerWindow, IQrScannerService qrScannerService)
        {
            _equipInfoService = equipInfoService ?? throw new ArgumentNullException(nameof(equipInfoService));
            _vm = vm ?? throw new ArgumentNullException(nameof(vm));
            _equipmentVm = equipmentVm ?? throw new ArgumentNullException(nameof(equipmentVm));
            _ownerWindow = ownerWindow ?? throw new ArgumentNullException(nameof(ownerWindow));
            _qrScannerService = qrScannerService ?? throw new ArgumentNullException(nameof(qrScannerService));
        }

        private string ResolveSelectedEquipForInfo()
        {
            var text = (_equipmentVm.EquipName ?? "").Trim();

            var sel = (_equipmentVm.SelectedListBoxEquipment?.Equipment ?? "").Trim();
            if (!string.IsNullOrWhiteSpace(sel))
                text = sel;

            return text;
        }

        private static string BuildCapturedPhotoFileName(string? equipName)
        {
            var baseName = (equipName ?? "").Trim();
            if (string.IsNullOrWhiteSpace(baseName))
                baseName = "Equipment";

            foreach (var ch in Path.GetInvalidFileNameChars())
                baseName = baseName.Replace(ch, '_');

            return $"{baseName}_{DateTime.Now:yyyyMMdd_HHmmss}.jpg";
        }

        public async Task LoadCurrentAsync()
        {
            // Версия запроса.
            // Нужна, чтобы более старый async-вызов не перетёр результат нового.
            var requestId = Interlocked.Increment(ref _loadCurrentRequestId);

            var equipName = ResolveSelectedEquipForInfo();

            void ClearInfoUiState()
            {
                _vm.SelectedInfoPhotoFile = null;
                _vm.SelectedInfoInstructionFile = null;
                _vm.SelectedInfoSchemeFile = null;

                // Важно:
                // очищаем checked-combo выбор,
                // иначе он может остаться от предыдущего equipment.
                _vm.SelectedInfoPhotoLibraryIds = new List<object>();
                _vm.SelectedInfoInstructionLibraryIds = new List<object>();
                _vm.SelectedInfoSchemeLibraryIds = new List<object>();

                _vm.CurrentInfoDocumentPreviewPath = null;
                _vm.InfoDocumentMessage = "";
                _vm.IsInfoDocumentExportVisible = false;
                _vm.IsInfoEditMode = false;
            }

            if (string.IsNullOrWhiteSpace(equipName))
            {
                _vm.CurrentEquipInfo = null;
                ClearInfoUiState();
                _vm.InfoStatusText = "";
                return;
            }

            if (!_vm.IsInfoDbConnected)
            {
                _vm.CurrentEquipInfo = EquipmentInfoDto.CreateEmpty(equipName);
                ClearInfoUiState();
                _vm.InfoStatusText = "Info DB is disconnected.";
                return;
            }

            try
            {
                _vm.IsInfoLoading = true;
                _vm.InfoStatusText = $"Loading info: {equipName}...";

                // Сначала читаем карточку в локальную переменную.
                // Не применяем её в UI, пока не убедимся, что этот запрос ещё актуален.
                var info = await _equipInfoService.GetAsync(equipName);

                if (requestId != Volatile.Read(ref _loadCurrentRequestId))
                    return;

                await LoadLibrariesAsync();

                if (requestId != Volatile.Read(ref _loadCurrentRequestId))
                    return;

                _vm.CurrentEquipInfo = info;

                var selectedEquip = _equipmentVm.SelectedListBoxEquipment;
                if (selectedEquip != null && string.Equals((selectedEquip.Equipment ?? "").Trim(), (info.EquipName ?? "").Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    info.IsFavorite = selectedEquip.IsFavorite;
                }

                SyncCheckedSelectionsFromCurrentModel();
                SyncPhotoLibraryFlagsFromCurrentModel();

                // Cache
                foreach (var photo in _vm.CurrentEquipInfo.Photos)
                    PutPhotoToCache(photo);

                WarmupLinkedPhotoLibraryThumbnails();

                _vm.SelectedInfoPhotoFile = info.Photos.FirstOrDefault();
                _vm.SelectedInfoInstructionFile = info.Instructions.FirstOrDefault();
                _vm.SelectedInfoSchemeFile = info.Schemes.FirstOrDefault();

                _vm.SelectedInfoPhotoLibraryFile = null;

                _vm.CurrentInfoDocumentPreviewPath = null;
                _vm.InfoDocumentMessage = "";
                _vm.IsInfoDocumentExportVisible = false;

                _vm.IsInfoEditMode = false;
                _vm.InfoStatusText = $"Info loaded: {equipName}";

                if (_vm.IsInfoDocumentPage)
                {
                    await PrepareCurrentDocumentAsync();

                    // Пока ждали PrepareCurrentDocumentAsync(),
                    // пользователь тоже мог успеть выбрать другое оборудование.
                    if (requestId != Volatile.Read(ref _loadCurrentRequestId))
                        return;
                }
            }
            catch (Exception ex)
            {
                // Старый запрос не должен ломать UI нового запроса.
                if (requestId != Volatile.Read(ref _loadCurrentRequestId))
                    return;

                _vm.CurrentEquipInfo = EquipmentInfoDto.CreateEmpty(equipName);
                ClearInfoUiState();
                _vm.InfoStatusText = $"Info error: {ex.Message}";
            }
            finally
            {
                // Только самый свежий запрос имеет право выключать loading.
                if (requestId == Volatile.Read(ref _loadCurrentRequestId))
                    _vm.IsInfoLoading = false;
            }
        }

        public async Task BeginEditAsync()
        {
            var equipName = ResolveSelectedEquipForInfo();
            if (string.IsNullOrWhiteSpace(equipName))
                return;

            _vm.CurrentEquipInfo ??= EquipmentInfoDto.CreateEmpty(equipName);
            _vm.CurrentEquipInfo.EquipName = equipName;

            await LoadProductCodeOptionsForCurrentEquipmentAsync();

            SyncCheckedSelectionsFromCurrentModel();
            SyncPhotoLibraryFlagsFromCurrentModel();
            WarmupLinkedPhotoLibraryThumbnails();

            var preferredPhotoId =
                _vm.SelectedInfoPhotoFile?.Id
                ?? _vm.CurrentEquipInfo.Photos.FirstOrDefault()?.Id
                ?? 0;

            SelectPhotoLibraryFileById(preferredPhotoId);

            if (_vm.CurrentInfoPage == InfoPageKind.Notes &&
                _vm.SelectedInfoNote == null)
            {
                _vm.SelectedInfoNote = _vm.CurrentEquipInfo.Notes.FirstOrDefault();
            }

            _vm.IsInfoEditMode = true;
            _vm.InfoStatusText = $"Editing info: {equipName}";
        }

        private async Task LoadProductCodeOptionsForCurrentEquipmentAsync()
        {
            _vm.AvailableProductCodeOptions.Clear();

            var typeGroupKey = ResolveSelectedEquipTypeGroupKey();

            if (string.IsNullOrWhiteSpace(typeGroupKey))
                return;

            if (!_vm.IsInfoDbConnected)
                return;

            var options = await _equipInfoService.GetProductCodeOptionsAsync(typeGroupKey);

            foreach (var option in options
                .OrderBy(x => x.Type, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.ProductCode, StringComparer.OrdinalIgnoreCase))
            {
                _vm.AvailableProductCodeOptions.Add(option);
            }
        }

        public async Task SaveAsync(string userName)
        {
            if (_vm.CurrentEquipInfo == null)
                return;

            var equipName = ResolveSelectedEquipForInfo();
            if (string.IsNullOrWhiteSpace(equipName))
                return;

            try
            {
                _vm.IsInfoLoading = true;

                _vm.CurrentEquipInfo.EquipName = equipName;

                NormalizeSortOrder(_vm.CurrentEquipInfo.Photos, equipName);
                NormalizeSortOrder(_vm.CurrentEquipInfo.Instructions, equipName);
                NormalizeSortOrder(_vm.CurrentEquipInfo.Schemes, equipName);

                ValidateNoDuplicates(_vm.CurrentEquipInfo.Photos, "photo");
                ValidateNoDuplicates(_vm.CurrentEquipInfo.Instructions, "instruction");
                ValidateNoDuplicates(_vm.CurrentEquipInfo.Schemes, "scheme");

                await _equipInfoService.SaveAsync(_vm.CurrentEquipInfo);

                var savedNotes = await _equipInfoService.SaveNotesAsync(
                    equipName,
                    _vm.CurrentEquipInfo.Notes,
                    userName);

                ReplaceNoteCollection(_vm.CurrentEquipInfo.Notes, savedNotes);

                _vm.SelectedInfoNote =
                    _vm.CurrentEquipInfo.Notes.FirstOrDefault();

                _vm.CurrentEquipInfo.UpdatedAt = DateTime.Now;

                _vm.IsInfoEditMode = false;
                _vm.InfoStatusText = $"Info saved: {equipName}";

                if (_vm.IsInfoDocumentPage)
                    await PrepareCurrentDocumentAsync();
            }
            catch (Exception ex)
            {
                _vm.InfoStatusText = $"Info save error: {ex.Message}";
                DXMessageBox.Show(_ownerWindow, ex.Message, "Save info",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _vm.IsInfoLoading = false;
            }
        }

        public async Task LoadPhotoFilesAsync()
        {
            if (!_vm.IsInfoEditMode)
                return;

            var equipName = ResolveSelectedEquipForInfo();
            if (string.IsNullOrWhiteSpace(equipName))
                return;

            _vm.CurrentEquipInfo ??= EquipmentInfoDto.CreateEmpty(equipName);
            _vm.CurrentEquipInfo.EquipName = equipName;

            var dlg = new OpenFileDialog
            {
                Title = "Select image files",
                Filter = "Image files (*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff)|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff|All files (*.*)|*.*",
                CheckFileExists = true,
                Multiselect = true
            };

            if (dlg.ShowDialog(_ownerWindow) != true)
                return;

            var equipTypeGroupKey = ResolveSelectedEquipTypeGroupKey();
            if (string.IsNullOrWhiteSpace(equipTypeGroupKey))
                return;

            var addResult = await _equipInfoService.AddFilesToLibraryAsync(
                InfoFileKind.Photo,
                equipTypeGroupKey,
                dlg.FileNames);

            await LoadLibrariesAsync();

            MergeAssetsIntoSelection(_vm.CurrentEquipInfo.Photos, addResult.ResolvedAssets, equipName);
            SyncCheckedSelectionsFromCurrentModel();
            SyncPhotoLibraryFlagsFromCurrentModel();

            // cache
            foreach (var photo in addResult.ResolvedAssets)
                PutPhotoToCache(photo);

            WarmupLinkedPhotoLibraryThumbnails();

            // После Add выбираем именно добавленный/подцепленный файл, а не первый в списке.
            var selectedPhoto = addResult.ResolvedAssets.LastOrDefault(asset => asset != null && asset.Id > 0);

            if (selectedPhoto != null)
            {
                _vm.SelectedInfoPhotoFile = _vm.CurrentEquipInfo.Photos.FirstOrDefault(x => x.Id == selectedPhoto.Id);
                SelectPhotoLibraryFileById(selectedPhoto.Id);
            }
            else
            {
                _vm.SelectedInfoPhotoFile = _vm.CurrentEquipInfo.Photos.FirstOrDefault();
                SelectPhotoLibraryFileById(_vm.SelectedInfoPhotoFile?.Id ?? 0);
            }

            if (addResult.ExistingInLibraryFileNames.Count > 0)
            {
                DXMessageBox.Show(
                    _ownerWindow,
                    "These image files already existed in the shared library and were linked to the equipment:\n\n" +
                    string.Join("\n", addResult.ExistingInLibraryFileNames),
                    "Existing images",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }

            _vm.InfoStatusText = $"Images linked: {_vm.CurrentEquipInfo.Photos.Count}. New in library: {addResult.AddedToLibraryFileNames.Count}.";
        }

        public async Task<InfoImageImportResult> ImportImagesFromFolderAsync(string folderPath, IEnumerable<EquipListBoxItem> equipmentItems, Action<int, int, string>? progress = null, CancellationToken cancellationToken = default)
        {
            var result = new InfoImageImportResult();

            folderPath = (folderPath ?? "").Trim();
            if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
                return result;

            var allEquipments = (equipmentItems ?? Enumerable.Empty<EquipListBoxItem>())
                .Where(x => x != null)
                .Where(x => !x.IsGroup)
                .Where(x => x.TypeGroup != EquipTypeGroup.All)
                .Where(x => x.TypeGroup != EquipTypeGroup.Favorites)
                .Where(x => !string.IsNullOrWhiteSpace(x.Equipment))
                .GroupBy(x => x.Equipment.Trim(), StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();

            if (allEquipments.Count == 0)
                return result;

            var knownTypeNames = allEquipments
                .Select(x => x.TypeGroup.ToString())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var root = new DirectoryInfo(folderPath);

            var importFolders = new List<(string TypeName, string FolderPath)>();

            if (knownTypeNames.Contains(root.Name, StringComparer.OrdinalIgnoreCase))
            {
                // Выбрали сразу папку типа:
                // Motor\*.jpg
                importFolders.Add((root.Name, root.FullName));
            }
            else
            {
                // Выбрали общую папку:
                // Images\Motor\*.jpg
                // Images\VGA\*.jpg
                foreach (var dir in root.GetDirectories())
                {
                    if (knownTypeNames.Contains(dir.Name, StringComparer.OrdinalIgnoreCase))
                        importFolders.Add((dir.Name, dir.FullName));
                    else
                        result.SkippedUnsupportedTypeFolder++;
                }

                if (importFolders.Count == 0)
                    result.SkippedUnsupportedTypeFolder++;
            }

            var jobs = new List<(string TypeName, string FilePath)>();

            foreach (var folder in importFolders)
            {
                var files = Directory
                    .EnumerateFiles(folder.FolderPath, "*.*", SearchOption.TopDirectoryOnly)
                    .Where(IsSupportedImageFile)
                    .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                foreach (var file in files)
                    jobs.Add((folder.TypeName, file));
            }

            progress?.Invoke(0, jobs.Count, "Importing images...");

            for (var i = 0; i < jobs.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var job = jobs[i];
                var fileName = Path.GetFileName(job.FilePath);

                result.ScannedFiles++;

                try
                {
                    var equipmentsOfType = allEquipments
                        .Where(x => string.Equals(x.TypeGroup.ToString(), job.TypeName, StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    var match = FindEquipmentForImageFile(job.FilePath, equipmentsOfType);

                    if (match.Status == ImageFileEquipmentMatchStatus.NoMatch)
                    {
                        result.SkippedNoEquipmentMatch++;
                        progress?.Invoke(i + 1, jobs.Count, $"No equipment match: {fileName}");
                        continue;
                    }

                    if (match.Status == ImageFileEquipmentMatchStatus.Ambiguous)
                    {
                        result.SkippedAmbiguousMatch++;
                        progress?.Invoke(i + 1, jobs.Count, $"Ambiguous match: {fileName}");
                        continue;
                    }

                    if (match.Equipment == null)
                    {
                        result.SkippedNoEquipmentMatch++;
                        progress?.Invoke(i + 1, jobs.Count, $"No equipment match: {fileName}");
                        continue;
                    }

                    var dbResult = await _equipInfoService.ImportPhotoForEquipmentAsync(
                        match.Equipment.Equipment,
                        job.TypeName,
                        job.FilePath,
                        cancellationToken);

                    switch (dbResult.Status)
                    {
                        case InfoPhotoImportDbStatus.AddedToDbAndLinked:
                            result.AddedToDb++;
                            result.AffectedEquipNames.Add(match.Equipment.Equipment);
                            break;

                        case InfoPhotoImportDbStatus.LinkedExisting:
                            result.LinkedExisting++;
                            result.AffectedEquipNames.Add(match.Equipment.Equipment);
                            break;

                        case InfoPhotoImportDbStatus.AlreadyLinked:
                            result.AlreadyLinked++;
                            break;
                    }

                    progress?.Invoke(i + 1, jobs.Count, $"Imported: {fileName}");
                }
                catch (Exception ex)
                {
                    result.Errors++;

                    if (result.ErrorMessages.Count < 10)
                        result.ErrorMessages.Add($"{fileName}: {ex.Message}");

                    progress?.Invoke(i + 1, jobs.Count, $"Error: {fileName}");
                }
            }

            await LoadLibrariesAsync();

            var currentEquipName = ResolveSelectedEquipForInfo();

            if (!string.IsNullOrWhiteSpace(currentEquipName) &&
                result.AffectedEquipNames.Contains(currentEquipName))
            {
                await RefreshCurrentPhotosAfterImageImportAsync(currentEquipName);
            }
            else
            {
                SyncPhotoLibraryFlagsFromCurrentModel();
                WarmupLinkedPhotoLibraryThumbnails();
            }

            _vm.InfoStatusText =
                $"Image import finished. Added: {result.AddedToDb}, linked existing: {result.LinkedExisting}, skipped: {result.AlreadyLinked + result.SkippedNoEquipmentMatch + result.SkippedAmbiguousMatch}.";

            return result;
        }

        private async Task RefreshCurrentPhotosAfterImageImportAsync(string currentEquipName)
        {
            currentEquipName = (currentEquipName ?? "").Trim();
            if (string.IsNullOrWhiteSpace(currentEquipName))
                return;

            var fresh = await _equipInfoService.GetAsync(currentEquipName);

            _vm.CurrentEquipInfo ??= EquipmentInfoDto.CreateEmpty(currentEquipName);
            _vm.CurrentEquipInfo.EquipName = currentEquipName;

            ReplaceCollection(
                _vm.CurrentEquipInfo.Photos,
                fresh.Photos.Select(x => CloneFile(x, currentEquipName)));

            NormalizeSortOrder(_vm.CurrentEquipInfo.Photos, currentEquipName);

            foreach (var photo in _vm.CurrentEquipInfo.Photos)
                PutPhotoToCache(photo);

            SyncCheckedSelectionsFromCurrentModel();
            SyncPhotoLibraryFlagsFromCurrentModel();
            WarmupLinkedPhotoLibraryThumbnails();

            var selectedId = _vm.SelectedInfoPhotoFile?.Id ?? 0;

            _vm.SelectedInfoPhotoFile =
                selectedId > 0
                    ? _vm.CurrentEquipInfo.Photos.FirstOrDefault(x => x.Id == selectedId)
                    : null;

            _vm.SelectedInfoPhotoFile ??= _vm.CurrentEquipInfo.Photos.FirstOrDefault();

            SelectPhotoLibraryFileById(_vm.SelectedInfoPhotoFile?.Id ?? 0);
        }

        private static bool IsSupportedImageFile(string filePath)
        {
            var ext = Path.GetExtension(filePath);

            return ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".png", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".bmp", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".gif", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".tif", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".tiff", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".webp", StringComparison.OrdinalIgnoreCase);
        }

        private enum ImageFileEquipmentMatchStatus
        {
            Found,
            NoMatch,
            Ambiguous
        }

        private sealed class ImageFileEquipmentMatch
        {
            public ImageFileEquipmentMatchStatus Status { get; init; }
            public EquipListBoxItem? Equipment { get; init; }
        }

        private static ImageFileEquipmentMatch FindEquipmentForImageFile(string filePath, IReadOnlyList<EquipListBoxItem> equipmentsOfType)
        {
            var fileStem = Path.GetFileNameWithoutExtension(filePath);

            if (string.IsNullOrWhiteSpace(fileStem))
            {
                return new ImageFileEquipmentMatch
                {
                    Status = ImageFileEquipmentMatchStatus.NoMatch
                };
            }

            // 1. Сначала ищем точное совпадение:
            // File:  S01.H01.M01.jpg
            // Equip: S01.H01.M01
            var exact = equipmentsOfType
                .Where(x => string.Equals(x.Equipment, fileStem, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (exact.Count == 1)
            {
                return new ImageFileEquipmentMatch
                {
                    Status = ImageFileEquipmentMatchStatus.Found,
                    Equipment = exact[0]
                };
            }

            if (exact.Count > 1)
            {
                return new ImageFileEquipmentMatch
                {
                    Status = ImageFileEquipmentMatchStatus.Ambiguous
                };
            }

            // 2. Потом ищем вариант "полное имя equipment + суффикс":
            // File:  S01.H01.M01_Pump.jpg
            // Equip: S01.H01.M01
            var byEquipmentPrefix = equipmentsOfType
                .Where(x => FileStemStartsWithEquipmentName(fileStem, x.Equipment))
                .ToList();

            if (byEquipmentPrefix.Count == 1)
            {
                return new ImageFileEquipmentMatch
                {
                    Status = ImageFileEquipmentMatchStatus.Found,
                    Equipment = byEquipmentPrefix[0]
                };
            }

            if (byEquipmentPrefix.Count > 1)
            {
                return new ImageFileEquipmentMatch
                {
                    Status = ImageFileEquipmentMatchStatus.Ambiguous
                };
            }

            // 3. Потом ищем по корню файла:
            // File:  S01.H01.jpg
            // File:  S01.H01_Pump.jpg
            // File:  S01.H01.Pump.jpg
            // Equip: S01.H01.M01
            var candidates = BuildImageNameCandidates(fileStem);

            foreach (var candidate in candidates)
            {
                var prefix = equipmentsOfType
                    .Where(x => EquipmentStartsWithRoot(x.Equipment, candidate))
                    .ToList();

                if (prefix.Count == 1)
                {
                    return new ImageFileEquipmentMatch
                    {
                        Status = ImageFileEquipmentMatchStatus.Found,
                        Equipment = prefix[0]
                    };
                }

                if (prefix.Count > 1)
                {
                    return new ImageFileEquipmentMatch
                    {
                        Status = ImageFileEquipmentMatchStatus.Ambiguous
                    };
                }
            }

            return new ImageFileEquipmentMatch
            {
                Status = ImageFileEquipmentMatchStatus.NoMatch
            };
        }

        private static List<string> BuildImageNameCandidates(string fileStem)
        {
            var result = new List<string>();

            void Add(string? value)
            {
                value = (value ?? "").Trim();

                if (string.IsNullOrWhiteSpace(value))
                    return;

                if (!result.Contains(value, StringComparer.OrdinalIgnoreCase))
                    result.Add(value);
            }

            Add(fileStem);

            // S01.H01_Pump -> S01.H01
            var underscoreIndex = fileStem.IndexOf('_');
            if (underscoreIndex > 0)
                Add(fileStem[..underscoreIndex]);

            // S01.H01-Pump -> S01.H01
            var dashIndex = fileStem.IndexOf('-');
            if (dashIndex > 0)
                Add(fileStem[..dashIndex]);

            // S01.H01 Pump -> S01.H01
            var spaceIndex = fileStem.IndexOf(' ');
            if (spaceIndex > 0)
                Add(fileStem[..spaceIndex]);

            // S01.H01.Pump -> S01.H01
            var parts = fileStem.Split('.', StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length >= 2)
                Add(parts[0] + "." + parts[1]);

            return result;
        }

        private static bool FileStemStartsWithEquipmentName(string fileStem, string equipmentName)
        {
            fileStem = (fileStem ?? "").Trim();
            equipmentName = (equipmentName ?? "").Trim();

            if (string.IsNullOrWhiteSpace(fileStem) || string.IsNullOrWhiteSpace(equipmentName))
                return false;

            if (!fileStem.StartsWith(equipmentName, StringComparison.OrdinalIgnoreCase))
                return false;

            if (fileStem.Length == equipmentName.Length)
                return true;

            var next = fileStem[equipmentName.Length];

            return next == '_' || next == '-' || next == ' ' || next == '.';
        }

        private static bool EquipmentStartsWithRoot(string equipmentName, string root)
        {
            equipmentName = (equipmentName ?? "").Trim();
            root = (root ?? "").Trim();

            if (string.IsNullOrWhiteSpace(equipmentName) || string.IsNullOrWhiteSpace(root))
                return false;

            if (equipmentName.Equals(root, StringComparison.OrdinalIgnoreCase))
                return true;

            // S01.H01 должен матчить S01.H01.M01,
            // но не должен матчить S01.H010.M01.
            return equipmentName.StartsWith(root + ".", StringComparison.OrdinalIgnoreCase);
        }

        public async Task CapturePhotoFromCameraAsync()
        {
            if (!_vm.IsInfoEditMode)
                return;

            var equipName = ResolveSelectedEquipForInfo();
            if (string.IsNullOrWhiteSpace(equipName))
                return;

            _vm.CurrentEquipInfo ??= EquipmentInfoDto.CreateEmpty(equipName);
            _vm.CurrentEquipInfo.EquipName = equipName;

            var equipTypeGroupKey = ResolveSelectedEquipTypeGroupKey();
            if (string.IsNullOrWhiteSpace(equipTypeGroupKey))
                return;

            string? tempFile = null;

            try
            {
                var cameras = await _qrScannerService.GetAvailableCamerasAsync();
                if (cameras == null || cameras.Count == 0)
                {
                    DXMessageBox.Show(_ownerWindow, "No camera devices were found.", "Capture photo", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                var preferredIndex = await _qrScannerService.GetPreferredCameraIndexAsync();
                if (!cameras.Any(x => x.Index == preferredIndex))
                    preferredIndex = cameras[0].Index;

                var captureWindow = new PhotoCaptureWindow(_qrScannerService, cameras, preferredIndex){Owner = _ownerWindow};

                var ok = captureWindow.ShowDialog();
                if (ok != true)
                    return;

                tempFile = captureWindow.CapturedFilePath;
                if (string.IsNullOrWhiteSpace(tempFile) || !File.Exists(tempFile))
                    return;

                // Переименовываем снимок по шаблону: Equipment_yyyyMMdd_HHmmss.jpg
                var friendlyFileName = BuildCapturedPhotoFileName(equipName);
                var renamedTempFile = Path.Combine(Path.GetDirectoryName(tempFile)!, friendlyFileName);

                // Если вдруг файл с таким именем уже есть в temp-папке, добавим суффикс _01, _02 и т.д.
                if (File.Exists(renamedTempFile))
                {
                    var baseName = Path.GetFileNameWithoutExtension(friendlyFileName);
                    var ext = Path.GetExtension(friendlyFileName);

                    int i = 1;
                    do
                    {
                        renamedTempFile = Path.Combine(Path.GetDirectoryName(tempFile)!, $"{baseName}_{i:00}{ext}");
                        i++;
                    }
                    while (File.Exists(renamedTempFile));
                }

                File.Move(tempFile, renamedTempFile);
                tempFile = renamedTempFile;

                var addResult = await _equipInfoService.AddFilesToLibraryAsync(InfoFileKind.Photo, equipTypeGroupKey, new[] { tempFile });
                await LoadLibrariesAsync();

                MergeAssetsIntoSelection(_vm.CurrentEquipInfo.Photos, addResult.ResolvedAssets, equipName);
                SyncCheckedSelectionsFromCurrentModel();
                SyncPhotoLibraryFlagsFromCurrentModel();

                foreach (var photo in addResult.ResolvedAssets)
                    PutPhotoToCache(photo);

                WarmupLinkedPhotoLibraryThumbnails();

                var selectedPhoto = addResult.ResolvedAssets.LastOrDefault(asset => asset != null && asset.Id > 0);

                if (selectedPhoto != null)
                {
                    _vm.SelectedInfoPhotoFile = _vm.CurrentEquipInfo.Photos.FirstOrDefault(x => x.Id == selectedPhoto.Id);
                    SelectPhotoLibraryFileById(selectedPhoto.Id);
                }
                else
                {
                    _vm.SelectedInfoPhotoFile = _vm.CurrentEquipInfo.Photos.FirstOrDefault();
                    SelectPhotoLibraryFileById(_vm.SelectedInfoPhotoFile?.Id ?? 0);
                }

                if (addResult.ExistingInLibraryFileNames.Count > 0)
                {
                    DXMessageBox.Show(_ownerWindow, "This photo already existed in the shared library and was linked to the equipment:\n\n" + string.Join("\n", addResult.ExistingInLibraryFileNames), "Existing photo", MessageBoxButton.OK, MessageBoxImage.Information);
                }

                _vm.InfoStatusText = $"Photo captured and linked. Total linked images: {_vm.CurrentEquipInfo.Photos.Count}.";
            }
            catch (Exception ex)
            {
                _vm.InfoStatusText = $"Capture photo error: {ex.Message}";
                DXMessageBox.Show(_ownerWindow, ex.Message, "Capture photo", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(tempFile) && File.Exists(tempFile))
                        File.Delete(tempFile);
                }
                catch
                {
                    // ignore temp cleanup failures
                }
            }
        }

        public void RemoveSelectedPhoto()
        {
            if (!_vm.IsInfoEditMode || _vm.CurrentEquipInfo == null)
                return;

            var selected = _vm.SelectedInfoPhotoFile;
            if (selected == null)
                return;

            var list = _vm.CurrentEquipInfo.Photos;
            var index = list.IndexOf(selected);

            if (index >= 0)
                list.RemoveAt(index);

            NormalizeSortOrder(list, _vm.CurrentEquipInfo.EquipName);

            _vm.SelectedInfoPhotoFile =
                index >= 0 && index < list.Count ? list[index] : list.LastOrDefault();

            SyncCheckedSelectionsFromCurrentModel();
            SyncPhotoLibraryFlagsFromCurrentModel();
            SelectPhotoLibraryFileById(_vm.SelectedInfoPhotoFile?.Id ?? 0);

            _vm.InfoStatusText = "Photo removed from current card.";
        }


        private sealed class DocumentImportJob
        {
            public InfoFileKind Kind { get; init; }
            public string TypeGroupKey { get; init; } = "";
            public string FilePath { get; init; } = "";

            public HashSet<string> EquipNames { get; } = new(StringComparer.OrdinalIgnoreCase);

            public HashSet<int> SourceRows { get; } = new();
        }

        private sealed class DocumentImportTarget
        {
            public string EquipName { get; init; } = "";
            public string TypeGroupKey { get; init; } = "";
        }

        private sealed class PhotoImportJob
        {
            public string TypeGroupKey { get; init; } = "";
            public string FilePath { get; init; } = "";

            public HashSet<string> EquipNames { get; } =
                new(StringComparer.OrdinalIgnoreCase);
        }


        private async Task<InfoDocumentImportResult> ImportDocumentsFromExcelAsync(string excelPath, Action<int, int, string>? progress = null, CancellationToken cancellationToken = default)
        {
            const string sheetName = "SCHEME";
            var kind = InfoFileKind.Scheme;

            progress?.Invoke(0, 1, "Reading Excel import file...");

            var result = new InfoDocumentImportResult
            {
                Kind = kind,
                SheetName = sheetName
            };

            var plan = ExcelInfoDocumentImportReader.ReadSchemePlan(excelPath);

            var jobs = new Dictionary<string, DocumentImportJob>(StringComparer.OrdinalIgnoreCase);

            BuildStationSchemeJobs(plan, result, jobs, kind);
            BuildGroupSchemeJobs(plan, result, jobs, kind);
            BuildEquipmentSchemeJobs(plan, result, jobs, kind);

            result.RowsScanned =
                result.StationRowsScanned +
                result.GroupRowsScanned +
                result.EquipmentRowsScanned;

            result.ImportJobs = jobs.Count;

            if (jobs.Count == 0)
            {
                _vm.InfoStatusText =
                    $"Scheme import finished. No import jobs. Missing files: {result.MissingFiles}, missing groups: {result.MissingGroups}, missing equipments: {result.MissingEquipments}.";

                progress?.Invoke(1, 1, "Scheme import completed.");
                return result;
            }

            await ExecuteDocumentImportJobsAsync(
                jobs,
                result,
                progress,
                cancellationToken);

            progress?.Invoke(jobs.Count, jobs.Count, "Refreshing document libraries...");

            await LoadLibrariesAsync();

            var currentEquipName = ResolveSelectedEquipForInfo();

            if (!string.IsNullOrWhiteSpace(currentEquipName) &&
                result.AffectedEquipNames.Contains(currentEquipName))
            {
                await RefreshCurrentDocumentLinksAfterImportAsync(kind, currentEquipName);
            }

            _vm.InfoStatusText =
                $"Scheme import finished. Jobs: {result.ImportJobs}, added: {result.AddedToDb}, updated: {result.UpdatedInDb}, links: {result.LinkedExisting}, errors: {result.Errors}.";

            progress?.Invoke(jobs.Count, jobs.Count, "Scheme import completed.");

            return result;
        }

        private void BuildStationSchemeJobs(InfoDocumentExcelImportPlan plan, InfoDocumentImportResult result, Dictionary<string, DocumentImportJob> jobs, InfoFileKind kind)
        {
            var equipmentsByStation = _equipmentVm.Equipments
                .Where(x => x != null)
                .Where(x => !x.IsGroup)
                .Where(x => !x.IsEquipmentChildNode)
                .Where(x => !string.IsNullOrWhiteSpace(x.Equipment))
                .Where(x => !string.IsNullOrWhiteSpace(x.Station))
                .Where(x => x.TypeGroup != EquipTypeGroup.All)
                .Where(x => x.TypeGroup != EquipTypeGroup.Favorites)
                .GroupBy(x => x.Station.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    g => g.Key,
                    g => g
                        .GroupBy(x => x.Equipment.Trim(), StringComparer.OrdinalIgnoreCase)
                        .Select(x => new DocumentImportTarget
                        {
                            EquipName = x.First().Equipment.Trim(),
                            TypeGroupKey = x.First().TypeGroup.ToString()
                        })
                        .ToList(),
                    StringComparer.OrdinalIgnoreCase);

            foreach (var row in plan.StationRows)
            {
                result.StationRowsScanned++;

                var station = (row.Station ?? "").Trim();

                if (string.IsNullOrWhiteSpace(station) || row.Sources.Count == 0)
                {
                    result.EmptyRowsSkipped++;
                    continue;
                }

                if (!equipmentsByStation.TryGetValue(station, out var targets) ||
                    targets.Count == 0)
                {
                    result.RowsWithoutEquipment++;

                    AddImportMessage(
                        result,
                        $"Station row {row.RowNumber}: no equipment found for station '{station}'.");

                    continue;
                }

                AddDocumentJobsForTargets(
                    row.RowNumber,
                    row.BaseFolder,
                    row.Sources,
                    targets,
                    result,
                    jobs,
                    kind);
            }
        }

        private void BuildGroupSchemeJobs(InfoDocumentExcelImportPlan plan, InfoDocumentImportResult result, Dictionary<string, DocumentImportJob> jobs, InfoFileKind kind)
        {
            var groupsByName = _equipmentVm.Equipments
                .Where(x => x != null)
                .Where(x => x.IsGroup)
                .Where(x => !string.IsNullOrWhiteSpace(x.Equipment))
                .GroupBy(x => x.Equipment.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    g => g.Key,
                    g =>
                    {
                        var item = g.First();

                        return new DocumentImportTarget
                        {
                            EquipName = item.Equipment.Trim(),
                            TypeGroupKey = string.IsNullOrWhiteSpace(item.TypeGroup.ToString())
                                ? EquipTypeGroup.Equipment.ToString()
                                : item.TypeGroup.ToString()
                        };
                    },
                    StringComparer.OrdinalIgnoreCase);

            foreach (var row in plan.GroupRows)
            {
                result.GroupRowsScanned++;

                if (row.Groups.Count == 0 || row.Sources.Count == 0)
                {
                    result.EmptyRowsSkipped++;
                    continue;
                }

                var targets = new List<DocumentImportTarget>();

                foreach (var groupName in row.Groups)
                {
                    if (groupsByName.TryGetValue(groupName, out var target))
                    {
                        targets.Add(target);
                    }
                    else
                    {
                        result.MissingGroups++;

                        AddImportMessage(
                            result,
                            $"Group row {row.RowNumber}: group '{groupName}' was not found.");
                    }
                }

                if (targets.Count == 0)
                    continue;

                // ВАЖНО:
                // Здесь НЕ берём children группы.
                // Линкуем схемы только к самому group equipment.
                AddDocumentJobsForTargets(
                    row.RowNumber,
                    row.BaseFolder,
                    row.Sources,
                    targets,
                    result,
                    jobs,
                    kind);
            }
        }

        private void BuildEquipmentSchemeJobs(InfoDocumentExcelImportPlan plan, InfoDocumentImportResult result, Dictionary<string, DocumentImportJob> jobs, InfoFileKind kind)
        {
            var equipmentsByName = _equipmentVm.Equipments
                .Where(x => x != null)
                .Where(x => !x.IsGroup)
                .Where(x => !x.IsEquipmentChildNode)
                .Where(x => !string.IsNullOrWhiteSpace(x.Equipment))
                .Where(x => x.TypeGroup != EquipTypeGroup.All)
                .Where(x => x.TypeGroup != EquipTypeGroup.Favorites)
                .GroupBy(x => x.Equipment.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    g => g.Key,
                    g =>
                    {
                        var item = g.First();

                        return new DocumentImportTarget
                        {
                            EquipName = item.Equipment.Trim(),
                            TypeGroupKey = item.TypeGroup.ToString()
                        };
                    },
                    StringComparer.OrdinalIgnoreCase);

            foreach (var row in plan.EquipmentRows)
            {
                result.EquipmentRowsScanned++;

                if (row.Equipments.Count == 0 || row.Sources.Count == 0)
                {
                    result.EmptyRowsSkipped++;
                    continue;
                }

                var targets = new List<DocumentImportTarget>();

                foreach (var equipName in row.Equipments)
                {
                    if (equipmentsByName.TryGetValue(equipName, out var target))
                    {
                        targets.Add(target);
                    }
                    else
                    {
                        result.MissingEquipments++;

                        AddImportMessage(
                            result,
                            $"Equipment row {row.RowNumber}: equipment '{equipName}' was not found.");
                    }
                }

                if (targets.Count == 0)
                    continue;

                AddDocumentJobsForTargets(
                    row.RowNumber,
                    row.BaseFolder,
                    row.Sources,
                    targets,
                    result,
                    jobs,
                    kind);
            }
        }

        private void AddDocumentJobsForTargets(int rowNumber, string baseFolder, IReadOnlyCollection<string> sources, IReadOnlyCollection<DocumentImportTarget> targets, InfoDocumentImportResult result, Dictionary<string, DocumentImportJob> jobs, InfoFileKind kind)
        {
            if (sources == null || sources.Count == 0 ||
                targets == null || targets.Count == 0)
                return;

            foreach (var source in sources)
            {
                result.FileReferencesScanned++;

                var pdfPath = ResolveImportSourcePath(baseFolder, source);

                if (!File.Exists(pdfPath))
                {
                    result.MissingFiles++;

                    AddImportMessage(
                        result,
                        $"Row {rowNumber}: file not found: {pdfPath}");

                    continue;
                }

                foreach (var typeGroup in targets
                    .Where(x => !string.IsNullOrWhiteSpace(x.EquipName))
                    .Where(x => !string.IsNullOrWhiteSpace(x.TypeGroupKey))
                    .GroupBy(x => x.TypeGroupKey.Trim(), StringComparer.OrdinalIgnoreCase))
                {
                    var typeGroupKey = typeGroup.Key;

                    var jobKey = BuildDocumentImportJobKey(
                        kind,
                        typeGroupKey,
                        pdfPath);

                    if (!jobs.TryGetValue(jobKey, out var job))
                    {
                        job = new DocumentImportJob
                        {
                            Kind = kind,
                            TypeGroupKey = typeGroupKey,
                            FilePath = pdfPath
                        };

                        jobs[jobKey] = job;
                    }

                    job.SourceRows.Add(rowNumber);

                    foreach (var target in typeGroup)
                        job.EquipNames.Add(target.EquipName.Trim());
                }
            }
        }

        private static string ResolveImportSourcePath(string baseFolder, string source)
        {
            source = (source ?? "").Trim();

            if (string.IsNullOrWhiteSpace(source))
                return "";

            if (Path.IsPathRooted(source))
                return Path.GetFullPath(source);

            baseFolder = (baseFolder ?? "").Trim();

            return Path.GetFullPath(Path.Combine(baseFolder, source));
        }

        private static string BuildDocumentImportJobKey(InfoFileKind kind, string typeGroupKey, string pdfPath)
        {
            return $"{kind}|{typeGroupKey}|{Path.GetFullPath(pdfPath)}"
                .ToLowerInvariant();
        }

        private async Task ExecuteDocumentImportJobsAsync(Dictionary<string, DocumentImportJob> jobs, InfoDocumentImportResult result, Action<int, int, string>? progress, CancellationToken cancellationToken)
        {
            var totalSteps = Math.Max(1, jobs.Count);
            var done = 0;

            progress?.Invoke(0, totalSteps, $"Importing scheme documents: 0/{totalSteps}");

            foreach (var job in jobs.Values
                .OrderBy(x => x.TypeGroupKey, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => Path.GetFileName(x.FilePath), StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var fileName = Path.GetFileName(job.FilePath);

                try
                {
                    var dbResult = await _equipInfoService.ImportDocumentForEquipmentsAsync(
                        job.Kind,
                        job.TypeGroupKey,
                        job.FilePath,
                        job.EquipNames,
                        cancellationToken);

                    if (dbResult.AddedToDb)
                        result.AddedToDb++;

                    if (dbResult.UpdatedInDb)
                        result.UpdatedInDb++;

                    result.LinkedExisting += dbResult.LinksCreated;
                    result.AlreadyLinked += dbResult.AlreadyLinked;

                    foreach (var equipName in dbResult.AffectedEquipNames)
                        result.AffectedEquipNames.Add(equipName);
                }
                catch (Exception ex)
                {
                    result.Errors++;

                    AddImportMessage(
                        result,
                        $"{job.TypeGroupKey}, {fileName}: {ex.Message}");
                }

                done++;

                progress?.Invoke(
                    done,
                    totalSteps,
                    $"Importing scheme documents: {done}/{totalSteps} | {job.TypeGroupKey} | {fileName}");
            }
        }

        private static void AddImportMessage(InfoDocumentImportResult result, string message)
        {
            if (result.ErrorMessages.Count < 15)
                result.ErrorMessages.Add(message);
        }

        private async Task RefreshCurrentDocumentLinksAfterImportAsync(InfoFileKind kind, string currentEquipName)
        {
            currentEquipName = (currentEquipName ?? "").Trim();
            if (string.IsNullOrWhiteSpace(currentEquipName))
                return;

            var fresh = await _equipInfoService.GetAsync(currentEquipName);

            _vm.CurrentEquipInfo ??= EquipmentInfoDto.CreateEmpty(currentEquipName);
            _vm.CurrentEquipInfo.EquipName = currentEquipName;

            if (kind == InfoFileKind.Scheme)
            {
                ReplaceCollection(
                    _vm.CurrentEquipInfo.Schemes,
                    fresh.Schemes.Select(x => CloneFile(x, currentEquipName)));

                _vm.SelectedInfoSchemeFile = _vm.CurrentEquipInfo.Schemes.FirstOrDefault();
            }
            else if (kind == InfoFileKind.Instruction)
            {
                ReplaceCollection(
                    _vm.CurrentEquipInfo.Instructions,
                    fresh.Instructions.Select(x => CloneFile(x, currentEquipName)));

                _vm.SelectedInfoInstructionFile = _vm.CurrentEquipInfo.Instructions.FirstOrDefault();
            }

            SyncCheckedSelectionsFromCurrentModel();

            _vm.CurrentInfoDocumentPreviewPath = null;
            await PrepareCurrentDocumentAsync();
        }

        public async Task<InfoDocumentImportResult?> LoadCurrentDocumentFilesAsync(Action<int, int, string>? progress = null)
        {
            if (!_vm.IsInfoEditMode || !_vm.IsInfoDocumentPage)
                return null;

            var equipName = ResolveSelectedEquipForInfo();
            if (string.IsNullOrWhiteSpace(equipName))
                return null;

            _vm.CurrentEquipInfo ??= EquipmentInfoDto.CreateEmpty(equipName);
            _vm.CurrentEquipInfo.EquipName = equipName;

            var kind = _vm.CurrentInfoPage == InfoPageKind.Scheme
                ? InfoFileKind.Scheme
                : InfoFileKind.Instruction;

            var dlg = new OpenFileDialog
            {
                Title = kind == InfoFileKind.Scheme
                    ? "Select scheme PDF or Excel import file"
                    : "Select instruction PDF or Excel import file",
                Filter =
                    "PDF files (*.pdf)|*.pdf|" +
                    "Excel files (*.xlsx)|*.xlsx|" +
                    "All files (*.*)|*.*",
                CheckFileExists = true,
                Multiselect = true
            };

            if (dlg.ShowDialog(_ownerWindow) != true)
                return null;

            var excelFiles = dlg.FileNames
                .Where(x => string.Equals(Path.GetExtension(x), ".xlsx", StringComparison.OrdinalIgnoreCase))
                .ToList();

            var pdfFiles = dlg.FileNames
                .Where(x => string.Equals(Path.GetExtension(x), ".pdf", StringComparison.OrdinalIgnoreCase))
                .ToList();

            // Если выбрали Excel — запускаем единый импорт.
            if (excelFiles.Count > 0)
            {
                if (kind == InfoFileKind.Scheme)
                {
                    _vm.InfoStatusText = "Importing scheme documents from Excel...";

                    return await ImportDocumentsFromExcelAsync(
                        excelFiles[0],
                        progress);
                }

                if (kind == InfoFileKind.Instruction)
                {
                    _vm.InfoStatusText = "Importing instruction documents from Excel...";

                    return await ImportInstructionDocumentsFromExcelAsync(
                        excelFiles[0],
                        progress);
                }
            }

            // Обычный старый режим: ручное добавление PDF к текущему equipment.
            if (pdfFiles.Count == 0)
                return null;

            var equipTypeGroupKey = ResolveSelectedEquipTypeGroupKey();
            if (string.IsNullOrWhiteSpace(equipTypeGroupKey))
                return null;

            var addResult = await _equipInfoService.AddFilesToLibraryAsync(
                kind,
                equipTypeGroupKey,
                pdfFiles);

            await LoadLibrariesAsync();

            var target = GetModelCollection(kind);
            MergeAssetsIntoSelection(target, addResult.ResolvedAssets, equipName);
            SyncCheckedSelectionsFromCurrentModel();

            var first = target.FirstOrDefault();
            if (kind == InfoFileKind.Instruction)
                _vm.SelectedInfoInstructionFile = first;
            else
                _vm.SelectedInfoSchemeFile = first;

            await PrepareCurrentDocumentAsync();

            if (addResult.ExistingInLibraryFileNames.Count > 0)
            {
                DXMessageBox.Show(
                    _ownerWindow,
                    "These PDF files already existed in the shared library and were linked to the equipment:\n\n" +
                    string.Join("\n", addResult.ExistingInLibraryFileNames),
                    "Existing PDF files",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }

            _vm.InfoStatusText = $"Documents linked: {target.Count}. New in library: {addResult.AddedToLibraryFileNames.Count}.";
            return null;
        }

        public async Task RemoveCurrentDocumentAsync()
        {
            if (!_vm.IsInfoEditMode || !_vm.IsInfoDocumentPage)
                return;

            var selected = GetCurrentSelectedDocument();
            if (selected == null)
                return;

            var list = GetCurrentDocumentCollection();
            var index = list.IndexOf(selected);

            if (index >= 0)
                list.RemoveAt(index);

            NormalizeSortOrder(list, _vm.CurrentEquipInfo?.EquipName ?? "");

            var newSelected =
                index >= 0 && index < list.Count ? list[index] : list.LastOrDefault();

            SetCurrentSelectedDocument(newSelected);
            _vm.CurrentInfoDocumentPreviewPath = null;

            await PrepareCurrentDocumentAsync();

            _vm.InfoStatusText = "Document removed from current card.";

            SyncCheckedSelectionsFromCurrentModel();
        }

        public async Task ShowPageAsync(InfoPageKind page)
        {
            _vm.CurrentInfoPage = page;
            _vm.CurrentInfoDocumentPreviewPath = null;
            _vm.InfoDocumentMessage = "";
            _vm.IsInfoDocumentExportVisible = false;

            if (_vm.IsInfoDocumentPage)
            {
                if (GetCurrentSelectedDocument() == null)
                    SetCurrentSelectedDocument(GetCurrentDocumentCollection().FirstOrDefault());

                await PrepareCurrentDocumentAsync();
            }
        }

        public Task PrepareCurrentDocumentAsync()
        {
            _vm.CurrentInfoDocumentPreviewPath = null;

            if (!_vm.IsInfoDocumentPage)
                return Task.CompletedTask;

            var model = _vm.CurrentEquipInfo;
            if (model == null)
            {
                _vm.InfoDocumentMessage = "No equipment selected.";
                _vm.IsInfoDocumentExportVisible = false;
                return Task.CompletedTask;
            }

            var selected = GetCurrentSelectedDocument();
            if (selected == null)
            {
                _vm.InfoDocumentMessage = _vm.CurrentInfoPage == InfoPageKind.Scheme
                    ? "No scheme file is stored for this equipment."
                    : "No instruction file is stored for this equipment.";

                _vm.IsInfoDocumentExportVisible = false;
                return Task.CompletedTask;
            }

            var equipName = (model.EquipName ?? "").Trim();
            if (string.IsNullOrWhiteSpace(equipName))
            {
                _vm.InfoDocumentMessage = "Equipment name is empty.";
                _vm.IsInfoDocumentExportVisible = false;
                return Task.CompletedTask;
            }

            var expectedPath = GetExpectedDocumentPath(_vm.CurrentInfoPage, selected.EquipTypeGroupKey, selected.FileName);

            if (File.Exists(expectedPath))
            {
                _vm.CurrentInfoDocumentPreviewPath = expectedPath;
                _vm.InfoDocumentMessage = "";
                _vm.IsInfoDocumentExportVisible = false;
                return Task.CompletedTask;
            }

            if (selected.FileData is { Length: > 0 })
            {
                var folderName = _vm.CurrentInfoPage == InfoPageKind.Scheme ? "Schemes" : "Instruction";

                _vm.InfoDocumentMessage = $"File '{selected.FileName}' is stored in DB but not cached locally. " + $"Click 'Export PDF' to save it to the local TechEquipments cache and open it.";

                _vm.IsInfoDocumentExportVisible = true;
                return Task.CompletedTask;
            }

            _vm.InfoDocumentMessage = $"File '{selected.FileName}' is not available in DB.";
            _vm.IsInfoDocumentExportVisible = false;
            return Task.CompletedTask;
        }

        public async Task ExportCurrentDocumentAsync()
        {
            if (!_vm.IsInfoDocumentPage)
                return;

            var model = _vm.CurrentEquipInfo;
            if (model == null)
                return;

            var selected = GetCurrentSelectedDocument();
            if (selected?.FileData == null || selected.FileData.Length == 0)
                return;

            var equipName = (model.EquipName ?? "").Trim();
            if (string.IsNullOrWhiteSpace(equipName))
                return;

            try
            {
                _vm.IsInfoLoading = true;

                var path = await EnsureDocumentCachedFromMemoryAsync(_vm.CurrentInfoPage, selected);

                _vm.CurrentInfoDocumentPreviewPath = path;
                _vm.InfoDocumentMessage = "";
                _vm.IsInfoDocumentExportVisible = false;

                _vm.InfoStatusText = $"PDF exported: {Path.GetFileName(path)}";
            }
            catch (Exception ex)
            {
                _vm.InfoStatusText = $"PDF export error: {ex.Message}";
                DXMessageBox.Show(_ownerWindow, ex.Message, "Export PDF",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _vm.IsInfoLoading = false;
            }
        }

        /// <summary>
        /// Запомнить текущую позицию просмотра PDF:
        /// page + zoom + anchor point в центре viewer.
        /// Сохраняем только по явной кнопке в edit mode.
        /// </summary>
        public async Task RememberCurrentDocumentPositionAsync(PdfViewerControl viewer)
        {
            if (viewer == null)
                return;

            if (!_vm.IsInfoEditMode || !_vm.IsInfoDocumentPage)
                return;

            var model = _vm.CurrentEquipInfo;
            if (model == null)
                return;

            var equipName = (model.EquipName ?? "").Trim();
            if (string.IsNullOrWhiteSpace(equipName))
                return;

            var selected = GetCurrentSelectedDocument();
            if (selected == null)
            {
                DXMessageBox.Show(
                    _ownerWindow,
                    "No PDF document is selected.",
                    "Remember PDF position",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);

                return;
            }

            if (selected.Id <= 0)
            {
                DXMessageBox.Show(
                    _ownerWindow,
                    "This PDF does not have a persistent library ID yet. Save the card and reopen the document first.",
                    "Remember PDF position",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);

                return;
            }

            if (!_vm.IsInfoDocumentViewerVisible || string.IsNullOrWhiteSpace(_vm.CurrentInfoDocumentPreviewPath))
            {
                DXMessageBox.Show(
                    _ownerWindow,
                    "Open the PDF preview first.",
                    "Remember PDF position",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);

                return;
            }

            // Берём якорную точку в центре viewer —
            // это даёт наиболее ожидаемое восстановление "той же области".
            var center = new Point(
                Math.Max(viewer.ActualWidth / 2.0, 1.0),
                Math.Max(viewer.ActualHeight / 2.0, 1.0));

            var position = viewer.ConvertPixelToDocumentPosition(center);
            if (position == null || position.PageNumber <= 0)
            {
                DXMessageBox.Show(
                    _ownerWindow,
                    "Cannot determine the current PDF position.",
                    "Remember PDF position",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);

                return;
            }

            var existing = await _equipInfoService.GetDocumentViewStateAsync(
                equipName,
                _vm.CurrentInfoPage,
                selected.Id);

            if (existing != null)
            {
                var overwrite = DXMessageBox.Show(
                    _ownerWindow,
                    "A saved position already exists for this PDF document.\n\nDo you want to overwrite it?",
                    "Remember PDF position",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (overwrite != MessageBoxResult.Yes)
                    return;
            }

            var state = new EquipmentInfoDocumentViewStateDto
            {
                EquipName = equipName,
                InfoPageKind = _vm.CurrentInfoPage,
                FileId = selected.Id,
                FileName = selected.FileName ?? "",
                PageNumber = position.PageNumber,
                ZoomFactor = viewer.ZoomFactor,
                AnchorX = position.Point.X,
                AnchorY = position.Point.Y
            };

            await _equipInfoService.SaveDocumentViewStateAsync(state);

            _vm.InfoStatusText = $"PDF position saved: {selected.DisplayName}";
        }

        /// <summary>
        /// Восстановить ранее сохранённую позицию просмотра PDF.
        /// Вызывается после загрузки документа в viewer.
        /// </summary>
        public async Task RestoreCurrentDocumentPositionAsync(PdfViewerControl viewer)
        {
            if (viewer == null)
                return;

            if (!_vm.IsInfoDocumentPage)
                return;

            var model = _vm.CurrentEquipInfo;
            if (model == null)
                return;

            var equipName = (model.EquipName ?? "").Trim();
            if (string.IsNullOrWhiteSpace(equipName))
                return;

            var selected = GetCurrentSelectedDocument();
            if (selected == null || selected.Id <= 0)
                return;

            var state = await _equipInfoService.GetDocumentViewStateAsync(
                equipName,
                _vm.CurrentInfoPage,
                selected.Id);

            if (state == null)
                return;

            try
            {
                // 1) Даём viewer закончить внутреннюю initial navigation / layout.
                await viewer.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);

                // Небольшая пауза помогает на первом открытии документа,
                // когда DocumentLoaded уже пришёл, но visual state ещё не стабилизировался.
                await Task.Delay(80);

                // 2) Сначала восстанавливаем zoom и страницу.
                await viewer.Dispatcher.InvokeAsync(() =>
                {
                    if (state.ZoomFactor > 0)
                        viewer.ZoomFactor = state.ZoomFactor;

                    if (state.PageNumber > 0)
                        viewer.CurrentPageNumber = state.PageNumber;
                }, DispatcherPriority.ApplicationIdle);

                // 3) Ещё один короткий проход, чтобы viewer успел принять zoom/page.
                await Task.Delay(50);

                // 4) Теперь уже позиционируем в точную область.
                await viewer.Dispatcher.InvokeAsync(() =>
                {
                    var position = new PdfDocumentPosition(
                        state.PageNumber,
                        new PdfPoint(state.AnchorX, state.AnchorY));

                    viewer.ScrollIntoView(position, ScrollIntoViewMode.Center);
                }, DispatcherPriority.ApplicationIdle);

                _vm.InfoStatusText = $"PDF position restored: {selected.DisplayName}";
            }
            catch (Exception ex)
            {
                _vm.InfoStatusText = $"PDF position restore error: {ex.Message}";
            }
        }

        private ObservableCollection<EquipmentInfoFileDto> GetCurrentDocumentCollection()
        {
            _vm.CurrentEquipInfo ??= EquipmentInfoDto.CreateEmpty(ResolveSelectedEquipForInfo());

            return _vm.CurrentInfoPage switch
            {
                InfoPageKind.Instruction => _vm.CurrentEquipInfo.Instructions,
                InfoPageKind.Scheme => _vm.CurrentEquipInfo.Schemes,
                _ => _vm.CurrentEquipInfo.Instructions
            };
        }

        private EquipmentInfoFileDto? GetCurrentSelectedDocument()
        {
            return _vm.CurrentInfoPage switch
            {
                InfoPageKind.Instruction => _vm.SelectedInfoInstructionFile,
                InfoPageKind.Scheme => _vm.SelectedInfoSchemeFile,
                _ => null
            };
        }

        private void SetCurrentSelectedDocument(EquipmentInfoFileDto? file)
        {
            switch (_vm.CurrentInfoPage)
            {
                case InfoPageKind.Instruction:
                    _vm.SelectedInfoInstructionFile = file;
                    break;

                case InfoPageKind.Scheme:
                    _vm.SelectedInfoSchemeFile = file;
                    break;
            }
        }

        private static void NormalizeSortOrder(ObservableCollection<EquipmentInfoFileDto> files, string equipName)
        {
            if (files == null)
                return;

            for (int i = 0; i < files.Count; i++)
            {
                files[i].EquipName = equipName;
                files[i].SortOrder = i;
            }
        }

        private static void ValidateNoDuplicates(IEnumerable<EquipmentInfoFileDto> files, string sectionName)
        {
            var dup = files
                .Where(x => x != null && !string.IsNullOrWhiteSpace(x.FileHash))
                .GroupBy(x => x.FileHash, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(g => g.Count() > 1);

            if (dup != null)
                throw new InvalidOperationException($"Duplicate {sectionName} files detected in current card.");
        }

        private static string ComputeFileHash(byte[] data)
        {
            var hash = SHA256.HashData(data);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        /// <summary>
        /// Корневая папка локального кэша приложения.
        /// Храним в профиле пользователя, чтобы не требовать admin-прав
        /// при запуске из Program Files / защищённой папки.
        /// </summary>
        private static string GetAppLocalDataFolder()
        {
            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TechEquipments");

            Directory.CreateDirectory(root);
            return root;
        }

        /// <summary>
        /// Локальный кэш PDF инструкций.
        /// </summary>
        private static string GetInstructionFolder()
        {
            var folder = Path.Combine(GetAppLocalDataFolder(), "Instruction");
            Directory.CreateDirectory(folder);
            return folder;
        }

        /// <summary>
        /// Локальный кэш PDF схем.
        /// </summary>
        private static string GetSchemesFolder()
        {
            var folder = Path.Combine(GetAppLocalDataFolder(), "Schemes");
            Directory.CreateDirectory(folder);
            return folder;
        }

        private static string MakeSafeFileName(string text)
        {
            var name = (text ?? "").Trim();
            if (name.Length == 0)
                name = "document";

            var invalid = Path.GetInvalidFileNameChars();
            var safe = new string(name.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());

            if (safe.Length > 120)
                safe = safe.Substring(0, 120);

            return safe;
        }

        private string GetExpectedDocumentPath(InfoPageKind page, string? equipTypeGroupKey, string? originalFileName)
        {
            var rootFolder = page == InfoPageKind.Scheme
                ? GetSchemesFolder()
                : GetInstructionFolder();

            var safeTypeGroup = MakeSafeFileName((equipTypeGroupKey ?? "").Trim());
            if (string.IsNullOrWhiteSpace(safeTypeGroup))
                safeTypeGroup = "Unknown";

            var folder = Path.Combine(rootFolder, safeTypeGroup);
            Directory.CreateDirectory(folder);

            // ВАЖНО:
            // сохраняем оригинальное имя файла без префикса,
            // но разводим кеш по подпапкам типа оборудования.
            var safeName = MakeSafeFileName((originalFileName ?? "").Trim());

            if (string.IsNullOrWhiteSpace(safeName))
                safeName = "document";

            return Path.Combine(folder, safeName);
        }

        private async Task<string> EnsureDocumentCachedFromMemoryAsync(InfoPageKind page, EquipmentInfoFileDto file)
        {
            if (file.FileData == null || file.FileData.Length == 0)
                throw new InvalidOperationException("Selected document has no data.");

            var path = GetExpectedDocumentPath(
                page,
                file.EquipTypeGroupKey,
                file.FileName);

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            if (!File.Exists(path))
                await File.WriteAllBytesAsync(path, file.FileData);

            return path;
        }

        private static EquipmentInfoFileDto CloneFile(EquipmentInfoFileDto src, string equipName = "")
        {
            return new EquipmentInfoFileDto
            {
                Id = src.Id,
                EquipName = equipName,
                EquipTypeGroupKey = src.EquipTypeGroupKey,
                FileName = src.FileName,
                DisplayName = src.DisplayName,
                FileHash = src.FileHash,
                FileData = src.FileData,
                IsLinkedToCurrentEquipment = src.IsLinkedToCurrentEquipment,
                SortOrder = src.SortOrder,
                UpdatedAt = src.UpdatedAt
            };
        }

        private static void ReplaceCollection(ObservableCollection<EquipmentInfoFileDto> target, IEnumerable<EquipmentInfoFileDto> source)
        {
            target.Clear();

            foreach (var item in source ?? Enumerable.Empty<EquipmentInfoFileDto>())
                target.Add(item);
        }

        private static void ReplaceNoteCollection(ObservableCollection<EquipmentInfoNoteDto> target, IEnumerable<EquipmentInfoNoteDto> source)
        {
            target.Clear();

            foreach (var item in source ?? Enumerable.Empty<EquipmentInfoNoteDto>())
                target.Add(item);
        }

        private async Task LoadLibrariesAsync()
        {
            var equipTypeGroupKey = ResolveSelectedEquipTypeGroupKey();

            if (string.IsNullOrWhiteSpace(equipTypeGroupKey))
            {
                _vm.AvailableInfoPhotoLibrary.Clear();
                _vm.AvailableInfoInstructionLibrary.Clear();
                _vm.AvailableInfoSchemeLibrary.Clear();
                return;
            }

            var photos = await _equipInfoService.GetLibraryAsync(InfoFileKind.Photo, equipTypeGroupKey);
            var instructions = await _equipInfoService.GetLibraryAsync(InfoFileKind.Instruction, equipTypeGroupKey);
            var schemes = await _equipInfoService.GetLibraryAsync(InfoFileKind.Scheme, equipTypeGroupKey);

            ReplaceCollection(_vm.AvailableInfoPhotoLibrary, photos);
            ReplaceCollection(_vm.AvailableInfoInstructionLibrary, instructions);
            ReplaceCollection(_vm.AvailableInfoSchemeLibrary, schemes);
        }

        private static List<object>? ToCheckedIds(IEnumerable<EquipmentInfoFileDto>? items)
        {
            var list = items?
                .Where(x => x != null && x.Id > 0)
                .Select(x => (object)x.Id)
                .Distinct()
                .ToList();

            return list is { Count: > 0 } ? list : new List<object>();
        }

        private void SyncCheckedSelectionsFromCurrentModel()
        {
            _suppressLibrarySelectionSync = true;

            try
            {
                _vm.SelectedInfoPhotoLibraryIds = ToCheckedIds(_vm.CurrentEquipInfo?.Photos);
                _vm.SelectedInfoInstructionLibraryIds = ToCheckedIds(_vm.CurrentEquipInfo?.Instructions);
                _vm.SelectedInfoSchemeLibraryIds = ToCheckedIds(_vm.CurrentEquipInfo?.Schemes);
            }
            finally
            {
                _suppressLibrarySelectionSync = false;
            }
        }

        private void SyncPhotoLibraryFlagsFromCurrentModel()
        {
            var linkedById = (_vm.CurrentEquipInfo?.Photos ?? Enumerable.Empty<EquipmentInfoFileDto>())
                .Where(x => x != null && x.Id > 0)
                .GroupBy(x => x.Id)
                .ToDictionary(g => g.Key, g => g.First());

            foreach (var item in _vm.AvailableInfoPhotoLibrary)
            {
                var isLinked = item.Id > 0 && linkedById.ContainsKey(item.Id);
                item.IsLinkedToCurrentEquipment = isLinked;

                // Если это уже linked photo и в linked-модели есть FileData,
                // подкинем их и в library item, чтобы миниатюра сразу была видна.
                if (isLinked &&
                    linkedById.TryGetValue(item.Id, out var linked) &&
                    (item.FileData == null || item.FileData.Length == 0) &&
                    linked.FileData is { Length: > 0 })
                {
                    item.FileData = linked.FileData;
                    item.FileHash = linked.FileHash;
                    item.FileName = linked.FileName;
                    item.UpdatedAt = linked.UpdatedAt;
                    item.EquipTypeGroupKey = linked.EquipTypeGroupKey;
                }
            }
        }

        private void SelectPhotoLibraryFileById(long id)
        {
            if (_vm.AvailableInfoPhotoLibrary.Count == 0)
            {
                _vm.SelectedInfoPhotoLibraryFile = null;
                return;
            }

            _vm.SelectedInfoPhotoLibraryFile =
                (id > 0
                    ? _vm.AvailableInfoPhotoLibrary.FirstOrDefault(x => x.Id == id)
                    : null)
                ?? _vm.AvailableInfoPhotoLibrary.FirstOrDefault();
        }

        private ObservableCollection<EquipmentInfoFileDto> GetLibraryCollection(InfoFileKind kind)
        {
            return kind switch
            {
                InfoFileKind.Photo => _vm.AvailableInfoPhotoLibrary,
                InfoFileKind.Instruction => _vm.AvailableInfoInstructionLibrary,
                InfoFileKind.Scheme => _vm.AvailableInfoSchemeLibrary,
                _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
            };
        }

        private ObservableCollection<EquipmentInfoFileDto> GetModelCollection(InfoFileKind kind)
        {
            _vm.CurrentEquipInfo ??= EquipmentInfoDto.CreateEmpty(ResolveSelectedEquipForInfo());

            return kind switch
            {
                InfoFileKind.Photo => _vm.CurrentEquipInfo.Photos,
                InfoFileKind.Instruction => _vm.CurrentEquipInfo.Instructions,
                InfoFileKind.Scheme => _vm.CurrentEquipInfo.Schemes,
                _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
            };
        }

        private List<object>? GetCheckedIds(InfoFileKind kind)
        {
            return kind switch
            {
                InfoFileKind.Photo => _vm.SelectedInfoPhotoLibraryIds,
                InfoFileKind.Instruction => _vm.SelectedInfoInstructionLibraryIds,
                InfoFileKind.Scheme => _vm.SelectedInfoSchemeLibraryIds,
                _ => null
            };
        }

        private void ApplyCheckedSelectionToModel(InfoFileKind kind)
        {
            if (_vm.CurrentEquipInfo == null)
                return;

            var equipName = (_vm.CurrentEquipInfo.EquipName ?? "").Trim();
            var library = GetLibraryCollection(kind);
            var target = GetModelCollection(kind);
            var checkedIds = GetCheckedIds(kind) ?? new List<object>();

            var selectedIds = checkedIds
                .Select(TryConvertToInt64)
                .Where(x => x > 0)
                .Distinct()
                .ToList();

            // Сохраняем уже существующие linked items,
            // потому что в них может быть FileData, а в library list его нет.
            var existingById = target
                .Where(x => x != null && x.Id > 0)
                .GroupBy(x => x.Id)
                .ToDictionary(g => g.Key, g => g.First());

            var rebuilt = new List<EquipmentInfoFileDto>();

            foreach (var id in selectedIds)
            {
                // 1) Если item уже есть в linked model — берём его,
                // чтобы не потерять FileData.
                if (existingById.TryGetValue(id, out var existing))
                {
                    rebuilt.Add(CloneFile(existing, equipName));
                    continue;
                }

                // 2) Иначе берём из library list
                var libItem = library.FirstOrDefault(x => x.Id == id);
                if (libItem == null)
                    continue;

                rebuilt.Add(CloneFile(libItem, equipName));
            }

            target.Clear();
            foreach (var item in rebuilt)
                target.Add(item);

            NormalizeSortOrder(target, equipName);

            switch (kind)
            {
                case InfoFileKind.Photo:
                    _vm.SelectedInfoPhotoFile = target.FirstOrDefault();
                    break;

                case InfoFileKind.Instruction:
                    _vm.SelectedInfoInstructionFile = target.FirstOrDefault();
                    break;

                case InfoFileKind.Scheme:
                    _vm.SelectedInfoSchemeFile = target.FirstOrDefault();
                    break;
            }
        }

        private async Task ApplyCheckedPhotoSelectionToModelAsync()
        {
            if (_vm.CurrentEquipInfo == null)
                return;

            var equipName = (_vm.CurrentEquipInfo.EquipName ?? "").Trim();
            var target = _vm.CurrentEquipInfo.Photos;
            var checkedIds = _vm.SelectedInfoPhotoLibraryIds ?? new List<object>();

            var selectedIds = checkedIds
                .Select(TryConvertToInt64)
                .Where(x => x > 0)
                .Distinct()
                .ToList();

            var preferredId =
                _vm.SelectedInfoPhotoLibraryFile?.Id
                ?? _vm.SelectedInfoPhotoFile?.Id
                ?? 0;

            var existingById = target
                .Where(x => x != null && x.Id > 0)
                .GroupBy(x => x.Id)
                .ToDictionary(g => g.Key, g => g.First());

            var rebuilt = new List<EquipmentInfoFileDto>();

            foreach (var id in selectedIds)
            {
                if (existingById.TryGetValue(id, out var existing))
                {
                    rebuilt.Add(CloneFile(existing, equipName));
                    continue;
                }

                var fullPhoto = await _equipInfoService.GetLibraryFileByIdAsync(InfoFileKind.Photo, id);
                if (fullPhoto == null)
                    continue;

                rebuilt.Add(CloneFile(fullPhoto, equipName));
            }

            target.Clear();
            foreach (var item in rebuilt)
                target.Add(item);

            NormalizeSortOrder(target, equipName);

            _vm.SelectedInfoPhotoFile =
                preferredId > 0
                    ? target.FirstOrDefault(x => x.Id == preferredId) ?? target.FirstOrDefault()
                    : target.FirstOrDefault();
        }

        private static long TryConvertToInt64(object? value)
        {
            if (value == null)
                return 0;

            return value switch
            {
                long l => l,
                int i => i,
                short s => s,
                string str when long.TryParse(str, out var parsed) => parsed,
                _ => 0
            };
        }

        private static void MergeAssetsIntoSelection(ObservableCollection<EquipmentInfoFileDto> target, IEnumerable<EquipmentInfoFileDto> assets, string equipName)
        {
            var existingIds = target
                .Where(x => x != null && x.Id > 0)
                .Select(x => x.Id)
                .ToHashSet();

            foreach (var asset in assets ?? Enumerable.Empty<EquipmentInfoFileDto>())
            {
                if (asset == null || asset.Id <= 0)
                    continue;

                if (existingIds.Contains(asset.Id))
                    continue;

                target.Add(CloneFile(asset, equipName));
                existingIds.Add(asset.Id);
            }

            NormalizeSortOrder(target, equipName);
        }

        public async Task SyncPhotoSelectionFromLibraryAsync()
        {
            if (_suppressLibrarySelectionSync)
                return;

            if (!_vm.IsInfoEditMode || _vm.CurrentEquipInfo == null)
                return;

            await ApplyCheckedPhotoSelectionToModelAsync();

            _vm.InfoStatusText = "Photo links updated.";
        }

        public async Task OnPhotoLibraryCheckChangedAsync(EquipmentInfoFileDto file)
        {
            if (!_vm.IsInfoEditMode || _vm.CurrentEquipInfo == null)
                return;

            _vm.SelectedInfoPhotoLibraryFile = file;

            _suppressLibrarySelectionSync = true;
            try
            {
                _vm.SelectedInfoPhotoLibraryIds = _vm.AvailableInfoPhotoLibrary
                    .Where(x => x != null && x.Id > 0 && x.IsLinkedToCurrentEquipment)
                    .Select(x => (object)x.Id)
                    .Distinct()
                    .ToList();
            }
            finally
            {
                _suppressLibrarySelectionSync = false;
            }

            await ApplyCheckedPhotoSelectionToModelAsync();

            SyncCheckedSelectionsFromCurrentModel();
            SyncPhotoLibraryFlagsFromCurrentModel();
            WarmupLinkedPhotoLibraryThumbnails();

            if (file.Id > 0)
            {
                _vm.SelectedInfoPhotoFile =
                    _vm.CurrentEquipInfo.Photos.FirstOrDefault(x => x.Id == file.Id)
                    ?? _vm.SelectedInfoPhotoFile;
            }

            await EnsureSelectedPhotoLoadedAsync();

            _vm.InfoStatusText = "Photo links updated.";
        }

        public async Task SyncCurrentDocumentSelectionFromLibraryAsync()
        {
            if (_suppressLibrarySelectionSync)
                return;

            if (!_vm.IsInfoEditMode || _vm.CurrentEquipInfo == null || !_vm.IsInfoDocumentPage)
                return;

            var kind = _vm.CurrentInfoPage == InfoPageKind.Scheme
                ? InfoFileKind.Scheme
                : InfoFileKind.Instruction;

            ApplyCheckedSelectionToModel(kind);

            _vm.CurrentInfoDocumentPreviewPath = null;
            await PrepareCurrentDocumentAsync();

            _vm.InfoStatusText = "Document links updated.";
        }

        private string ResolveSelectedEquipTypeGroupKey()
        {
            var group = _equipmentVm.SelectedListBoxEquipment?.TypeGroup ?? EquipTypeGroup.All;

            return group == EquipTypeGroup.All
                ? ""
                : group.ToString();
        }

        public async Task EnsureSelectedPhotoLoadedAsync()
        {
            var selected = _vm.CurrentPhotoPreviewFile;
            if (selected == null)
                return;

            if (TryRestorePhotoFromCache(selected))
                return;

            if (selected.FileData is { Length: > 0 })
            {
                PutPhotoToCache(selected);
                return;
            }

            if (selected.Id <= 0)
                return;

            try
            {
                var full = await _equipInfoService.GetLibraryFileByIdAsync(InfoFileKind.Photo, selected.Id);
                if (full?.FileData == null || full.FileData.Length == 0)
                {
                    _vm.InfoStatusText = $"Image '{selected.DisplayName}' is not available in DB.";
                    return;
                }

                selected.FileData = full.FileData;
                selected.FileHash = full.FileHash;
                selected.FileName = full.FileName;
                selected.UpdatedAt = full.UpdatedAt;
                selected.EquipTypeGroupKey = full.EquipTypeGroupKey;

                PutPhotoToCache(selected);

                var linked = _vm.CurrentEquipInfo?.Photos.FirstOrDefault(x => x.Id == selected.Id);
                if (linked != null && (linked.FileData == null || linked.FileData.Length == 0))
                {
                    linked.FileData = full.FileData;
                    linked.FileHash = full.FileHash;
                    linked.FileName = full.FileName;
                    linked.UpdatedAt = full.UpdatedAt;
                    linked.EquipTypeGroupKey = full.EquipTypeGroupKey;

                    PutPhotoToCache(linked);
                }

                var lib = _vm.AvailableInfoPhotoLibrary.FirstOrDefault(x => x.Id == selected.Id);
                if (lib != null && (lib.FileData == null || lib.FileData.Length == 0))
                {
                    lib.FileData = full.FileData;
                    lib.FileHash = full.FileHash;
                    lib.FileName = full.FileName;
                    lib.UpdatedAt = full.UpdatedAt;
                    lib.EquipTypeGroupKey = full.EquipTypeGroupKey;

                    PutPhotoToCache(lib);
                }

                _vm.InfoStatusText = $"Image loaded: {selected.DisplayName}";
            }
            catch (Exception ex)
            {
                _vm.InfoStatusText = $"Image load error: {ex.Message}";
            }
        }

        private async Task<InfoDocumentImportResult> ImportInstructionDocumentsFromExcelAsync(string excelPath, Action<int, int, string>? progress = null, CancellationToken cancellationToken = default)
        {
            var result = new InfoDocumentImportResult
            {
                Kind = InfoFileKind.Instruction,
                SheetName = "INSTRUCTION"
            };

            progress?.Invoke(0, 1, "Reading Excel instruction workbook...");

            var plan = ExcelInfoDocumentImportReader.ReadInstructionWorkbook(excelPath);

            result.InstructionEquipmentRowsScanned = plan.InstructionEquipmentRows.Count;
            result.InstructionOrderRowsScanned = plan.InstructionOrderRows.Count;
            result.InstructionSupplierRowsScanned = plan.InstructionSupplierRows.Count;

            result.SuppliersUpserted = await _equipInfoService.UpsertSuppliersAsync(
                plan.InstructionSupplierRows,
                cancellationToken);

            result.OrdersUpserted = await _equipInfoService.UpsertOrdersAsync(
                plan.InstructionOrderRows,
                cancellationToken);

            var productCodes = plan.InstructionEquipmentRows
                .Select(x => x.ProductCode)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var ordersByCode = await _equipInfoService.GetOrdersByProductCodesAsync(
                productCodes,
                cancellationToken);

            var equipmentByName = _equipmentVm.Equipments
                .Where(x => x != null)
                .Where(x => !x.IsGroup)
                .Where(x => !x.IsEquipmentChildNode)
                .Where(x => !string.IsNullOrWhiteSpace(x.Equipment))
                .Where(x => x.TypeGroup != EquipTypeGroup.All)
                .Where(x => x.TypeGroup != EquipTypeGroup.Favorites)
                .GroupBy(x => x.Equipment.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    g => g.Key,
                    g =>
                    {
                        var item = g.First();

                        return new DocumentImportTarget
                        {
                            EquipName = item.Equipment.Trim(),
                            TypeGroupKey = item.TypeGroup.ToString()
                        };
                    },
                    StringComparer.OrdinalIgnoreCase);

            var productLinks = new List<EquipmentProductCodeLinkDto>();
            var pdfJobs = new Dictionary<string, DocumentImportJob>(StringComparer.OrdinalIgnoreCase);
            var imageJobs = new Dictionary<string, PhotoImportJob>(StringComparer.OrdinalIgnoreCase);

            foreach (var row in plan.InstructionEquipmentRows)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var equipName = (row.Equipment ?? "").Trim();
                var productCode = (row.ProductCode ?? "").Trim();

                if (string.IsNullOrWhiteSpace(equipName))
                {
                    result.MissingEquipments++;
                    AddImportMessage(result, $"Instruction row {row.RowNumber}: equipment is empty.");
                    continue;
                }

                if (string.IsNullOrWhiteSpace(productCode))
                {
                    result.ProductCodesEmpty++;
                    continue;
                }

                if (!equipmentByName.TryGetValue(equipName, out var target))
                {
                    result.MissingEquipments++;
                    AddImportMessage(result, $"Instruction row {row.RowNumber}: equipment '{equipName}' was not found.");
                    continue;
                }

                if (!ordersByCode.TryGetValue(productCode, out var order))
                {
                    result.ProductCodesNotFound++;
                    AddImportMessage(result, $"Instruction row {row.RowNumber}: product code '{productCode}' was not found in equip_order.");
                    continue;
                }

                productLinks.Add(new EquipmentProductCodeLinkDto
                {
                    EquipName = target.EquipName,
                    ProductCode = productCode
                });

                AddInstructionPdfJobsForTarget(
                    row.RowNumber,
                    plan.InstructionBaseFolder,
                    order.Sources,
                    target,
                    result,
                    pdfJobs);

                AddInstructionImageJobsForTarget(
                    row.RowNumber,
                    plan.InstructionImagesFolder,
                    order.Images,
                    target,
                    result,
                    imageJobs);
            }

            result.EquipmentInfoUpdated = await _equipInfoService.ApplyProductCodesToEquipmentInfoAsync(
                productLinks,
                cancellationToken);

            foreach (var link in productLinks)
                result.UpdatedEquipmentInfoNames.Add(link.EquipName);

            result.PdfImportJobs = pdfJobs.Count;
            result.ImageImportJobs = imageJobs.Count;

            var totalSteps = Math.Max(1, pdfJobs.Count + imageJobs.Count);
            var done = 0;

            progress?.Invoke(done, totalSteps, "Importing instruction PDFs and images...");

            foreach (var job in pdfJobs.Values
                .OrderBy(x => x.TypeGroupKey, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => Path.GetFileName(x.FilePath), StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var dbResult = await _equipInfoService.ImportDocumentForEquipmentsAsync(
                        InfoFileKind.Instruction,
                        job.TypeGroupKey,
                        job.FilePath,
                        job.EquipNames,
                        cancellationToken);

                    if (dbResult.AddedToDb)
                        result.PdfAddedToDb++;

                    if (dbResult.UpdatedInDb)
                        result.PdfUpdatedInDb++;

                    result.PdfLinksCreated += dbResult.LinksCreated;
                    result.PdfAlreadyLinked += dbResult.AlreadyLinked;

                    foreach (var affected in dbResult.AffectedEquipNames)
                    {
                        result.AffectedEquipNames.Add(affected);
                        result.PdfAffectedEquipNames.Add(affected);
                    }
                }
                catch (Exception ex)
                {
                    result.Errors++;
                    AddImportMessage(result, $"{job.TypeGroupKey}, {Path.GetFileName(job.FilePath)}: {ex.Message}");
                }

                done++;
                progress?.Invoke(done, totalSteps, $"Instruction PDF: {done}/{totalSteps} | {Path.GetFileName(job.FilePath)}");
            }

            foreach (var job in imageJobs.Values
                .OrderBy(x => x.TypeGroupKey, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => Path.GetFileName(x.FilePath), StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var dbResult = await _equipInfoService.ImportPhotoForEquipmentsAsync(
                        job.TypeGroupKey,
                        job.FilePath,
                        job.EquipNames,
                        cancellationToken);

                    if (dbResult.AddedToDb)
                        result.ImageAddedToDb++;

                    if (dbResult.UpdatedInDb)
                        result.ImageUpdatedInDb++;

                    result.ImageLinksCreated += dbResult.LinksCreated;
                    result.ImageAlreadyLinked += dbResult.AlreadyLinked;

                    foreach (var affected in dbResult.AffectedEquipNames)
                    {
                        result.AffectedEquipNames.Add(affected);
                        result.ImageAffectedEquipNames.Add(affected);
                    }
                }
                catch (Exception ex)
                {
                    result.Errors++;
                    AddImportMessage(result, $"{job.TypeGroupKey}, {Path.GetFileName(job.FilePath)}: {ex.Message}");
                }

                done++;
                progress?.Invoke(done, totalSteps, $"Instruction image: {done}/{totalSteps} | {Path.GetFileName(job.FilePath)}");
            }

            progress?.Invoke(totalSteps, totalSteps, "Refreshing Info libraries...");

            await LoadLibrariesAsync();

            var currentEquipName = ResolveSelectedEquipForInfo();

            if (!string.IsNullOrWhiteSpace(currentEquipName) &&
                (result.AffectedEquipNames.Contains(currentEquipName) ||
                 result.UpdatedEquipmentInfoNames.Contains(currentEquipName)))
            {
                await RefreshCurrentInfoAfterInstructionImportAsync(currentEquipName);
            }

            _vm.InfoStatusText =
                $"Instruction import finished. PDFs: {result.PdfLinksCreated}, images: {result.ImageLinksCreated}, info updated: {result.EquipmentInfoUpdated}, errors: {result.Errors}.";

            progress?.Invoke(totalSteps, totalSteps, "Instruction import completed.");

            return result;
        }

        private void AddInstructionPdfJobsForTarget(int rowNumber, string baseFolder, IReadOnlyCollection<string> sources, DocumentImportTarget target, InfoDocumentImportResult result, Dictionary<string, DocumentImportJob> jobs)
        {
            if (sources == null || sources.Count == 0)
                return;

            foreach (var source in sources)
            {
                result.PdfFileReferences++;

                var pdfPath = ExcelInfoDocumentImportReader.ResolveSourcePath(baseFolder, source);

                if (!File.Exists(pdfPath))
                {
                    result.MissingSourceFiles++;
                    AddImportMessage(result, $"Instruction row {rowNumber}: PDF not found: {pdfPath}");
                    continue;
                }

                var jobKey = BuildDocumentImportJobKey(
                    InfoFileKind.Instruction,
                    target.TypeGroupKey,
                    pdfPath);

                if (!jobs.TryGetValue(jobKey, out var job))
                {
                    job = new DocumentImportJob
                    {
                        Kind = InfoFileKind.Instruction,
                        TypeGroupKey = target.TypeGroupKey,
                        FilePath = Path.GetFullPath(pdfPath)
                    };

                    jobs[jobKey] = job;
                }

                job.SourceRows.Add(rowNumber);
                job.EquipNames.Add(target.EquipName);
            }
        }

        private void AddInstructionImageJobsForTarget(int rowNumber, string baseFolder, IReadOnlyCollection<string> images, DocumentImportTarget target, InfoDocumentImportResult result, Dictionary<string, PhotoImportJob> jobs)
        {
            if (images == null || images.Count == 0)
                return;

            foreach (var image in images)
            {
                result.ImageFileReferences++;

                var imagePath = ExcelInfoDocumentImportReader.ResolveSourcePath(baseFolder, image);

                if (!File.Exists(imagePath))
                {
                    result.MissingImageFiles++;
                    AddImportMessage(result, $"Instruction row {rowNumber}: image not found: {imagePath}");
                    continue;
                }

                var jobKey = $"{target.TypeGroupKey}|{Path.GetFullPath(imagePath)}"
                    .ToLowerInvariant();

                if (!jobs.TryGetValue(jobKey, out var job))
                {
                    job = new PhotoImportJob
                    {
                        TypeGroupKey = target.TypeGroupKey,
                        FilePath = Path.GetFullPath(imagePath)
                    };

                    jobs[jobKey] = job;
                }

                job.EquipNames.Add(target.EquipName);
            }
        }

        private async Task RefreshCurrentInfoAfterInstructionImportAsync(string currentEquipName)
        {
            currentEquipName = (currentEquipName ?? "").Trim();
            if (string.IsNullOrWhiteSpace(currentEquipName))
                return;

            var fresh = await _equipInfoService.GetAsync(currentEquipName);

            _vm.CurrentEquipInfo ??= EquipmentInfoDto.CreateEmpty(currentEquipName);
            _vm.CurrentEquipInfo.EquipName = currentEquipName;

            _vm.CurrentEquipInfo.ProductCode = fresh.ProductCode;
            _vm.CurrentEquipInfo.Supplier = fresh.Supplier;
            _vm.CurrentEquipInfo.SupplierLogoCachePath = fresh.SupplierLogoCachePath;
            _vm.CurrentEquipInfo.Description = fresh.Description;

            ReplaceCollection(
                _vm.CurrentEquipInfo.Photos,
                fresh.Photos.Select(x => CloneFile(x, currentEquipName)));

            ReplaceCollection(
                _vm.CurrentEquipInfo.Instructions,
                fresh.Instructions.Select(x => CloneFile(x, currentEquipName)));

            _vm.SelectedInfoPhotoFile =
                _vm.CurrentEquipInfo.Photos.FirstOrDefault();

            _vm.SelectedInfoInstructionFile =
                _vm.CurrentEquipInfo.Instructions.FirstOrDefault();

            SyncCheckedSelectionsFromCurrentModel();
            SyncPhotoLibraryFlagsFromCurrentModel();
            WarmupLinkedPhotoLibraryThumbnails();

            _vm.CurrentInfoDocumentPreviewPath = null;

            if (_vm.CurrentInfoPage == InfoPageKind.Instruction)
                await PrepareCurrentDocumentAsync();
        }

        public async Task ApplyProductCodeFromUiAsync(string? productCode)
        {
            productCode = (productCode ?? "").Trim();

            if (!_vm.IsInfoEditMode)
                return;

            if (_vm.CurrentEquipInfo == null)
                return;

            if (string.IsNullOrWhiteSpace(productCode))
            {
                _vm.CurrentEquipInfo.ProductCode = null;
                _vm.CurrentEquipInfo.Supplier = null;
                _vm.CurrentEquipInfo.Description = null;
                _vm.CurrentEquipInfo.SupplierLogoCachePath = null;
                return;
            }

            if (_vm.AvailableProductCodeOptions.Count == 0)
                await LoadProductCodeOptionsForCurrentEquipmentAsync();

            var selected = _vm.AvailableProductCodeOptions
                .FirstOrDefault(x => string.Equals(
                    (x.ProductCode ?? "").Trim(),
                    productCode,
                    StringComparison.OrdinalIgnoreCase));

            if (selected == null)
                return;

            _vm.CurrentEquipInfo.ProductCode = selected.ProductCode;
            _vm.CurrentEquipInfo.Supplier = selected.Supplier;
            _vm.CurrentEquipInfo.Description = selected.Description;
            _vm.CurrentEquipInfo.SupplierLogoCachePath = selected.SupplierLogoCachePath;

            _vm.InfoStatusText = $"Product code selected: {selected.ProductCode}";
        }

        public void AddNewNote(string userName)
        {
            if (!_vm.IsInfoEditMode)
                return;

            var equipName = ResolveSelectedEquipForInfo();
            if (string.IsNullOrWhiteSpace(equipName))
                return;

            _vm.CurrentEquipInfo ??= EquipmentInfoDto.CreateEmpty(equipName);
            _vm.CurrentEquipInfo.EquipName = equipName;

            var note = EquipmentInfoNoteDto.CreateNew(equipName, userName);

            // Новые сверху.
            _vm.CurrentEquipInfo.Notes.Insert(0, note);
            _vm.SelectedInfoNote = note;

            _vm.InfoStatusText = "New note created.";
        }

        public async Task DeleteSelectedNoteAsync()
        {
            if (!_vm.IsInfoEditMode)
                return;

            var note = _vm.SelectedInfoNote;
            if (note == null)
                return;

            var result = DXMessageBox.Show(
                _ownerWindow,
                "Delete selected note?",
                "Delete note",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes)
                return;

            try
            {
                _vm.IsInfoLoading = true;

                if (note.Id > 0)
                    await _equipInfoService.DeleteNoteAsync(note.Id);

                _vm.CurrentEquipInfo?.Notes.Remove(note);
                _vm.SelectedInfoNote = _vm.CurrentEquipInfo?.Notes.FirstOrDefault();

                _vm.InfoStatusText = "Note deleted.";
            }
            catch (Exception ex)
            {
                _vm.InfoStatusText = $"Delete note error: {ex.Message}";
                DXMessageBox.Show(_ownerWindow, ex.Message, "Delete note",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _vm.IsInfoLoading = false;
            }
        }

        #region Cache

        private void PutPhotoToCache(EquipmentInfoFileDto? file)
        {
            if (file == null || file.Id <= 0 || file.FileData == null || file.FileData.Length == 0)
                return;

            _photoBytesCache[file.Id] = file.FileData;
        }

        private bool TryRestorePhotoFromCache(EquipmentInfoFileDto? file)
        {
            if (file == null || file.Id <= 0)
                return false;

            if (file.FileData is { Length: > 0 })
                return true;

            if (_photoBytesCache.TryGetValue(file.Id, out var bytes) && bytes is { Length: > 0 })
            {
                file.FileData = bytes;
                return true;
            }

            return false;
        }

        private void WarmupLinkedPhotoLibraryThumbnails()
        {
            var linkedById = (_vm.CurrentEquipInfo?.Photos ?? Enumerable.Empty<EquipmentInfoFileDto>())
                .Where(x => x != null && x.Id > 0)
                .GroupBy(x => x.Id)
                .ToDictionary(g => g.Key, g => g.First());

            foreach (var item in _vm.AvailableInfoPhotoLibrary)
            {
                if (item == null || item.Id <= 0)
                    continue;

                // 1. если уже есть в linked model — берем оттуда
                if (linkedById.TryGetValue(item.Id, out var linked) &&
                    linked.FileData is { Length: > 0 })
                {
                    item.FileData = linked.FileData;
                    PutPhotoToCache(linked);
                    PutPhotoToCache(item);
                    continue;
                }

                // 2. если есть в memory cache — берем из него
                TryRestorePhotoFromCache(item);
            }
        }

        #endregion

        #region Delete

        // Полное удаление фото из БД
        public async Task DeleteSelectedPhotoFromDbAsync()
        {
            if (!_vm.IsInfoEditMode || _vm.CurrentEquipInfo == null)
                return;

            var selected =
                _vm.SelectedInfoPhotoLibraryFile
                ?? _vm.SelectedInfoPhotoFile;

            if (selected == null || selected.Id <= 0)
                return;

            var name = string.IsNullOrWhiteSpace(selected.DisplayName)
                ? selected.FileName
                : selected.DisplayName;

            var confirm = DXMessageBox.Show(
                _ownerWindow,
                "This will permanently delete the selected image from the shared database library.\n\n" +
                "The image will also be unlinked from all equipment cards.\n\n" +
                $"File: {name}\n\n" +
                "Do you want to continue?",
                "Delete image from database",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (confirm != MessageBoxResult.Yes)
                return;

            try
            {
                _vm.IsInfoLoading = true;

                var deleted = await _equipInfoService.DeleteLibraryFileAsync(InfoFileKind.Photo, selected.Id);
                if (!deleted)
                {
                    DXMessageBox.Show(
                        _ownerWindow,
                        "The selected image was not found in the database.",
                        "Delete image from database",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);

                    return;
                }

                // Удаляем из текущей карточки
                for (int i = _vm.CurrentEquipInfo.Photos.Count - 1; i >= 0; i--)
                {
                    if (_vm.CurrentEquipInfo.Photos[i].Id == selected.Id)
                        _vm.CurrentEquipInfo.Photos.RemoveAt(i);
                }

                NormalizeSortOrder(_vm.CurrentEquipInfo.Photos, _vm.CurrentEquipInfo.EquipName);

                // Удаляем из общей library-коллекции текущего type group
                for (int i = _vm.AvailableInfoPhotoLibrary.Count - 1; i >= 0; i--)
                {
                    if (_vm.AvailableInfoPhotoLibrary[i].Id == selected.Id)
                        _vm.AvailableInfoPhotoLibrary.RemoveAt(i);
                }

                _vm.SelectedInfoPhotoFile = _vm.CurrentEquipInfo.Photos.FirstOrDefault();
                SelectPhotoLibraryFileById(_vm.SelectedInfoPhotoFile?.Id ?? 0);

                SyncCheckedSelectionsFromCurrentModel();
                SyncPhotoLibraryFlagsFromCurrentModel();
                await EnsureSelectedPhotoLoadedAsync();

                _vm.InfoStatusText = "Image deleted from database.";
            }
            catch (Exception ex)
            {
                _vm.InfoStatusText = $"Delete image error: {ex.Message}";
                DXMessageBox.Show(
                    _ownerWindow,
                    ex.Message,
                    "Delete image from database",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            finally
            {
                _vm.IsInfoLoading = false;
            }
        }

        // Полное удаление PDF из БД
        public async Task DeleteCurrentDocumentFromDbAsync()
        {
            if (!_vm.IsInfoEditMode || !_vm.IsInfoDocumentPage)
                return;

            var selected = GetCurrentSelectedDocument();
            if (selected == null || selected.Id <= 0)
                return;

            var kind = _vm.CurrentInfoPage == InfoPageKind.Scheme
                ? InfoFileKind.Scheme
                : InfoFileKind.Instruction;

            var name = string.IsNullOrWhiteSpace(selected.DisplayName)
                ? selected.FileName
                : selected.DisplayName;

            var confirm = DXMessageBox.Show(
                _ownerWindow,
                "This will permanently delete the selected PDF from the shared database library.\n\n" +
                "The PDF will also be unlinked from all equipment cards.\n\n" +
                $"File: {name}\n\n" +
                "Do you want to continue?",
                "Delete PDF from database",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (confirm != MessageBoxResult.Yes)
                return;

            try
            {
                _vm.IsInfoLoading = true;

                var deleted = await _equipInfoService.DeleteLibraryFileAsync(kind, selected.Id);
                if (!deleted)
                {
                    DXMessageBox.Show(
                        _ownerWindow,
                        "The selected PDF was not found in the database.",
                        "Delete PDF from database",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);

                    return;
                }

                var currentDocs = GetCurrentDocumentCollection();
                for (int i = currentDocs.Count - 1; i >= 0; i--)
                {
                    if (currentDocs[i].Id == selected.Id)
                        currentDocs.RemoveAt(i);
                }

                NormalizeSortOrder(currentDocs, _vm.CurrentEquipInfo?.EquipName ?? "");

                var library = GetLibraryCollection(kind);
                for (int i = library.Count - 1; i >= 0; i--)
                {
                    if (library[i].Id == selected.Id)
                        library.RemoveAt(i);
                }

                var newSelected = currentDocs.FirstOrDefault();
                SetCurrentSelectedDocument(newSelected);

                _vm.CurrentInfoDocumentPreviewPath = null;
                _vm.InfoDocumentMessage = "";
                _vm.IsInfoDocumentExportVisible = false;

                SyncCheckedSelectionsFromCurrentModel();

                await PrepareCurrentDocumentAsync();

                _vm.InfoStatusText = "PDF deleted from database.";
            }
            catch (Exception ex)
            {
                _vm.InfoStatusText = $"Delete PDF error: {ex.Message}";
                DXMessageBox.Show(
                    _ownerWindow,
                    ex.Message,
                    "Delete PDF from database",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            finally
            {
                _vm.IsInfoLoading = false;
            }
        }

        #endregion
    }
}
