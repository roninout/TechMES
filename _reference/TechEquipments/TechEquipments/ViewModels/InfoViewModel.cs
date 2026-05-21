using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace TechEquipments.ViewModels
{
    /// <summary>
    /// Состояние вкладки Info.
    /// Здесь особенно много "чистого UI-state", поэтому её выгодно выносить первой.
    /// </summary>
    public sealed class InfoViewModel : ObservableObject
    {
        private EquipmentInfoDto? _currentEquipInfo;
        public EquipmentInfoDto? CurrentEquipInfo
        {
            get => _currentEquipInfo;
            set
            {
                if (!SetProperty(ref _currentEquipInfo, value))
                    return;

                SelectedInfoNote = value?.Notes.FirstOrDefault();

                Raise(
                    nameof(CurrentInfoDocumentItems),
                    nameof(CurrentInfoSelectedDocumentFile),
                    nameof(CurrentInfoDocumentFileName),
                    nameof(IsInfoDocumentViewerVisible),
                    nameof(IsInfoDocumentMessageVisible));
            }
        }

        private EquipmentInfoFileDto? _selectedInfoPhotoFile;
        public EquipmentInfoFileDto? SelectedInfoPhotoFile
        {
            get => _selectedInfoPhotoFile;
            set
            {
                if (!SetProperty(ref _selectedInfoPhotoFile, value))
                    return;

                Raise(nameof(CurrentPhotoPreviewFile));
            }
        }

        private EquipmentInfoFileDto? _selectedInfoPhotoLibraryFile;
        public EquipmentInfoFileDto? SelectedInfoPhotoLibraryFile
        {
            get => _selectedInfoPhotoLibraryFile;
            set
            {
                if (!SetProperty(ref _selectedInfoPhotoLibraryFile, value))
                    return;

                Raise(nameof(CurrentPhotoPreviewFile));
            }
        }

        private EquipmentInfoFileDto? _selectedInfoInstructionFile;
        public EquipmentInfoFileDto? SelectedInfoInstructionFile
        {
            get => _selectedInfoInstructionFile;
            set
            {
                if (!SetProperty(ref _selectedInfoInstructionFile, value))
                    return;

                if (CurrentInfoPage == InfoPageKind.Instruction)
                {
                    Raise(nameof(CurrentInfoSelectedDocumentFile),
                          nameof(CurrentInfoDocumentFileName));
                }
            }
        }

        private EquipmentInfoFileDto? _selectedInfoSchemeFile;
        public EquipmentInfoFileDto? SelectedInfoSchemeFile
        {
            get => _selectedInfoSchemeFile;
            set
            {
                if (!SetProperty(ref _selectedInfoSchemeFile, value))
                    return;

                if (CurrentInfoPage == InfoPageKind.Scheme)
                {
                    Raise(nameof(CurrentInfoSelectedDocumentFile),
                          nameof(CurrentInfoDocumentFileName));
                }
            }
        }

        private EquipmentInfoNoteDto? _selectedInfoNote;
        public EquipmentInfoNoteDto? SelectedInfoNote
        {
            get => _selectedInfoNote;
            set => SetProperty(ref _selectedInfoNote, value);
        }

        public ObservableCollection<EquipmentInfoFileDto> AvailableInfoPhotoLibrary { get; } = new();
        public ObservableCollection<EquipmentInfoFileDto> AvailableInfoInstructionLibrary { get; } = new();
        public ObservableCollection<EquipmentInfoFileDto> AvailableInfoSchemeLibrary { get; } = new();
        /// <summary> Список Product code из equip_order для текущего типа оборудования. Используется на Info / Images в Edit mode. /// </summary>
        public ObservableCollection<InfoProductCodeOptionDto> AvailableProductCodeOptions { get; } = new();

        private List<object>? _selectedInfoPhotoLibraryIds;
        public List<object>? SelectedInfoPhotoLibraryIds
        {
            get => _selectedInfoPhotoLibraryIds;
            set
            {
                if (!SetProperty(ref _selectedInfoPhotoLibraryIds, value))
                    return;

                if (CurrentInfoPage == InfoPageKind.General)
                    Raise(nameof(CurrentInfoCheckedLibraryIds));
            }
        }

        private List<object>? _selectedInfoInstructionLibraryIds;
        public List<object>? SelectedInfoInstructionLibraryIds
        {
            get => _selectedInfoInstructionLibraryIds;
            set
            {
                if (!SetProperty(ref _selectedInfoInstructionLibraryIds, value))
                    return;

                if (CurrentInfoPage == InfoPageKind.Instruction)
                    Raise(nameof(CurrentInfoCheckedLibraryIds));
            }
        }

        private List<object>? _selectedInfoSchemeLibraryIds;
        public List<object>? SelectedInfoSchemeLibraryIds
        {
            get => _selectedInfoSchemeLibraryIds;
            set
            {
                if (!SetProperty(ref _selectedInfoSchemeLibraryIds, value))
                    return;

                if (CurrentInfoPage == InfoPageKind.Scheme)
                    Raise(nameof(CurrentInfoCheckedLibraryIds));
            }
        }

        private bool _isInfoLoading;
        public bool IsInfoLoading
        {
            get => _isInfoLoading;
            set
            {
                if (!SetProperty(ref _isInfoLoading, value))
                    return;

                Raise(nameof(CanEditInfoButtons));
            }
        }

        private bool _isInfoEditMode;
        public bool IsInfoEditMode
        {
            get => _isInfoEditMode;
            set
            {
                if (!SetProperty(ref _isInfoEditMode, value))
                    return;

                Raise(nameof(IsInfoReadOnly),
                      nameof(CurrentPhotoPreviewFile));
            }
        }

        public bool IsInfoNotesPage => CurrentInfoPage == InfoPageKind.Notes;

        public EquipmentInfoFileDto? CurrentPhotoPreviewFile => IsInfoEditMode
            ? SelectedInfoPhotoLibraryFile
            : SelectedInfoPhotoFile;

        public bool IsInfoReadOnly => !IsInfoEditMode;

        private string _infoStatusText = "";
        public string InfoStatusText
        {
            get => _infoStatusText;
            set => SetProperty(ref _infoStatusText, value);
        }

        public bool CanEditInfoButtons => !IsInfoLoading;

        private InfoPageKind _currentInfoPage = InfoPageKind.General;
        public InfoPageKind CurrentInfoPage
        {
            get => _currentInfoPage;
            set
            {
                if (!SetProperty(ref _currentInfoPage, value))
                    return;

                Raise(
                    nameof(IsInfoGeneralPage),
                    nameof(IsInfoDocumentPage),
                    nameof(IsInfoNotesPage),
                    nameof(CurrentInfoDocumentHeader),
                    nameof(CurrentInfoDocumentItems),
                    nameof(CurrentInfoSelectedDocumentFile),
                    nameof(CurrentInfoDocumentFileName),
                    nameof(CurrentInfoAvailableLibraryItems),
                    nameof(CurrentInfoCheckedLibraryIds),
                    nameof(IsInfoDocumentViewerVisible),
                    nameof(IsInfoDocumentMessageVisible));
            }
        }

        public bool IsInfoGeneralPage => CurrentInfoPage == InfoPageKind.General;

        public bool IsInfoDocumentPage =>
            CurrentInfoPage == InfoPageKind.Instruction ||
            CurrentInfoPage == InfoPageKind.Scheme;

        public string CurrentInfoDocumentHeader =>
            CurrentInfoPage switch
            {
                InfoPageKind.Instruction => "Instruction",
                InfoPageKind.Scheme => "Scheme",
                _ => ""
            };

        public IEnumerable<EquipmentInfoFileDto> CurrentInfoDocumentItems =>
            CurrentInfoPage switch
            {
                InfoPageKind.Instruction => CurrentEquipInfo?.Instructions ?? Enumerable.Empty<EquipmentInfoFileDto>(),
                InfoPageKind.Scheme => CurrentEquipInfo?.Schemes ?? Enumerable.Empty<EquipmentInfoFileDto>(),
                _ => Enumerable.Empty<EquipmentInfoFileDto>()
            };

        public IEnumerable<EquipmentInfoFileDto> CurrentInfoAvailableLibraryItems =>
            CurrentInfoPage switch
            {
                InfoPageKind.Instruction => AvailableInfoInstructionLibrary,
                InfoPageKind.Scheme => AvailableInfoSchemeLibrary,
                _ => Enumerable.Empty<EquipmentInfoFileDto>()
            };

        public List<object>? CurrentInfoCheckedLibraryIds
        {
            get => CurrentInfoPage switch
            {
                InfoPageKind.Instruction => SelectedInfoInstructionLibraryIds,
                InfoPageKind.Scheme => SelectedInfoSchemeLibraryIds,
                _ => null
            };
            set
            {
                switch (CurrentInfoPage)
                {
                    case InfoPageKind.Instruction:
                        SelectedInfoInstructionLibraryIds = value;
                        break;

                    case InfoPageKind.Scheme:
                        SelectedInfoSchemeLibraryIds = value;
                        break;
                }

                Raise(nameof(CurrentInfoCheckedLibraryIds));
            }
        }

        public EquipmentInfoFileDto? CurrentInfoSelectedDocumentFile
        {
            get => CurrentInfoPage switch
            {
                InfoPageKind.Instruction => SelectedInfoInstructionFile,
                InfoPageKind.Scheme => SelectedInfoSchemeFile,
                _ => null
            };
            set
            {
                switch (CurrentInfoPage)
                {
                    case InfoPageKind.Instruction:
                        SelectedInfoInstructionFile = value;
                        break;

                    case InfoPageKind.Scheme:
                        SelectedInfoSchemeFile = value;
                        break;
                }

                Raise(nameof(CurrentInfoSelectedDocumentFile),
                      nameof(CurrentInfoDocumentFileName));
            }
        }

        public string CurrentInfoDocumentFileName =>
            CurrentInfoSelectedDocumentFile?.FileName ?? "";

        private string? _currentInfoDocumentPreviewPath;
        public string? CurrentInfoDocumentPreviewPath
        {
            get => _currentInfoDocumentPreviewPath;
            set
            {
                if (!SetProperty(ref _currentInfoDocumentPreviewPath, value))
                    return;

                Raise(nameof(IsInfoDocumentViewerVisible),
                      nameof(IsInfoDocumentMessageVisible));
            }
        }

        private string _infoDocumentMessage = "";
        public string InfoDocumentMessage
        {
            get => _infoDocumentMessage;
            set => SetProperty(ref _infoDocumentMessage, value);
        }

        private bool _isInfoDocumentExportVisible;
        public bool IsInfoDocumentExportVisible
        {
            get => _isInfoDocumentExportVisible;
            set
            {
                if (!SetProperty(ref _isInfoDocumentExportVisible, value))
                    return;

                Raise(nameof(IsInfoDocumentViewerVisible),
                      nameof(IsInfoDocumentMessageVisible));
            }
        }

        public bool IsInfoDocumentViewerVisible =>
            IsInfoDocumentPage &&
            !string.IsNullOrWhiteSpace(CurrentInfoDocumentPreviewPath);

        public bool IsInfoDocumentMessageVisible =>
            IsInfoDocumentPage && !IsInfoDocumentViewerVisible;

        private bool _isInfoDbConnected;
        public bool IsInfoDbConnected
        {
            get => _isInfoDbConnected;
            set => SetProperty(ref _isInfoDbConnected, value);
        }
    }
}