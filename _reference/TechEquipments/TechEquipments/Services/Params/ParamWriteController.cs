using CtApi;
using DevExpress.Xpf.Core;
using DevExpress.Xpf.Editors;
using DevExpress.XtraRichEdit.Import.Html;
using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;

namespace TechEquipments
{
    /// <summary>
    /// Вся логика записи параметров из UI (CheckEdit/EditValueChanged и Enter).
    /// MainWindow только проксирует события в этот контроллер.
    /// </summary>
    public sealed class ParamWriteController
    {
        private readonly IEquipmentService _equipmentService;
        private readonly ICtApiService _ctApiService;
        private readonly IAppRuntimeContext _appRuntime;

        private readonly Func<MainTabKind> _getSelectedTab;
        private readonly Func<(string equipName, string equipType, string equipDescription)> _resolveSelectedEquip;
        private readonly Func<object, string> _resolveEquipNameForWrite;
        private readonly Func<bool> _getSuppressWritesFromPolling;
        private readonly Func<bool> _getSuppressWritesFromUiRollback;
        private readonly Action<bool> _setSuppressWritesFromUiRollback;
        private readonly SemaphoreSlim _paramRwGate;
        private readonly Action<DateTime> _setParamReadResumeAtUtc;
        private readonly Action<string> _setBottomText;
        private readonly Func<Window> _getOwnerWindow;
        private readonly Action _endParamFieldEdit;
        private readonly int _requiredWritePrivilege;
        private readonly int _requiredWriteArea;
        private readonly string _requiredUserNameContains;

        public ParamWriteController(
            IEquipmentService equipmentService,
            ICtApiService ctApiService,
            IAppRuntimeContext appRuntime,
            int requiredWritePrivilege,
            int requiredWriteArea,
            string requiredUserNameContains,
            Func<MainTabKind> getSelectedTab,
            Func<(string equipName, string equipType, string equipDescription)> resolveSelectedEquip,
            Func<object, string> resolveEquipNameForWrite,
            Func<bool> getSuppressWritesFromPolling,
            Func<bool> getSuppressWritesFromUiRollback,
            Action<bool> setSuppressWritesFromUiRollback,
            SemaphoreSlim paramRwGate,
            Action<DateTime> setParamReadResumeAtUtc,
            Action<string> setBottomText,
            Func<Window> getOwnerWindow,
            Action endParamFieldEdit)
        {
            _equipmentService = equipmentService;
            _ctApiService = ctApiService;
            _appRuntime = appRuntime;
            _requiredWritePrivilege = requiredWritePrivilege;
            _requiredWriteArea = requiredWriteArea;
            _requiredUserNameContains = (requiredUserNameContains ?? "").Trim();
            _getSelectedTab = getSelectedTab;
            _resolveSelectedEquip = resolveSelectedEquip;
            _resolveEquipNameForWrite = resolveEquipNameForWrite;
            _getSuppressWritesFromPolling = getSuppressWritesFromPolling;
            _getSuppressWritesFromUiRollback = getSuppressWritesFromUiRollback;
            _setSuppressWritesFromUiRollback = setSuppressWritesFromUiRollback;
            _paramRwGate = paramRwGate;
            _setParamReadResumeAtUtc = setParamReadResumeAtUtc;
            _setBottomText = setBottomText;
            _getOwnerWindow = getOwnerWindow;
            _endParamFieldEdit = endParamFieldEdit;
        }

        /// <summary>
        /// PLC: запись значения из UI (SimpleButton и т.п.).
        /// Важно: соблюдаем те же правила, что и для обычных параметров:
        /// - пишем только на вкладке Param
        /// - не пишем, если сейчас прилетает polling update
        /// </summary>
        public async Task WritePlcFromUiAsync(PlcRefRow row, object? newValue)
        {
            // не пишем, если это обновление из polling
            if (_getSuppressWritesFromPolling())
                return;

            // пишем только на вкладке Param
            if (_getSelectedTab() != MainTabKind.Param)
                return;

            await Plc_WriteValueAsync(row, newValue);
        }

        /// <summary>
        /// DevExpress CheckEdit/EditValueChanged (в т.ч. ForceCmd confirm)
        /// </summary>
        public async Task OnEditValueChangedAsync(object sender, EditValueChangedEventArgs e)
        {
            // 0) подавляем записи, если это откат значения из UI (Cancel в confirm)
            if (_getSuppressWritesFromUiRollback())
                return;

            // 1) Не пишем, если это обновление прилетело из polling-READ
            if (_getSuppressWritesFromPolling())
                return;

            // 2) Пишем только на вкладке Param
            if (_getSelectedTab() != MainTabKind.Param)
                return;

            // 3) Нужно имя оборудования
            var equip = _resolveEquipNameForWrite(sender)?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(equip))
                return;

            // 4) Определяем EquipItem из Tag
            if (sender is not FrameworkElement fe)
                return;

            // PLC ветка: ToggleSwitchEdit (Tag = PlcRefRow)
            if (fe.Tag is PlcRefRow plcRow)
            {
                if (_getSuppressWritesFromPolling())
                    return;

                await Plc_WriteValueAsync(plcRow, e.NewValue);
                return;
            }

            if (fe.Tag is not string equipItem || string.IsNullOrWhiteSpace(equipItem))
                return;

            // Confirm только при включении ForceCmd (false -> true)
            if (equipItem.Equals("ForceCmd", StringComparison.OrdinalIgnoreCase))
            {
                bool oldVal = ToBool(e.OldValue);
                bool newVal = ToBool(e.NewValue);

                if (!oldVal && newVal)
                {
                    var res = DevExpress.Xpf.Core.DXMessageBox.Show(
                        _getOwnerWindow(),
                        "Do you really want to enable channel forcing?",
                        "Attention!!!",
                        MessageBoxButton.OKCancel,
                        MessageBoxImage.Warning);

                    if (res != MessageBoxResult.OK)
                    {
                        // Cancel -> откатить чекбокс и не писать в SCADA
                        _setSuppressWritesFromUiRollback(true);
                        try
                        {
                            if (sender is CheckEdit ce)
                                ce.IsChecked = (e.OldValue as bool?) ?? oldVal;
                        }
                        finally
                        {
                            _setSuppressWritesFromUiRollback(false);
                        }

                        return;
                    }
                }
            }

            // 5) Нормализуем значение
            if (!TryNormalizeWriteValue(e.NewValue, out var writeValue))
                return;
            
            await WriteParamAsync(equipName: equip, equipItem: equipItem, writeValue: writeValue, currentValue: e.OldValue, description: equipItem);
        }

        /// <summary>
        /// Enter key write (PreviewKeyDown)
        /// </summary>
        public async Task OnPreviewKeyDownAsync(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter && e.Key != Key.Return)
                return;

            // PLC ветка: если Tag = PlcRefRow
            if (sender is FrameworkElement fePlc && fePlc.Tag is PlcRefRow plcRow)
            {
                var edit = sender as BaseEdit;
                var newVal = edit?.EditValue;

                e.Handled = true;
                await Plc_WriteValueAsync(plcRow, newVal);
                return;
            }

            // 1) Не пишем, если это обновление прилетело из polling-READ
            if (_getSuppressWritesFromPolling())
                return;

            // 2) Пишем только на вкладке Param
            if (_getSelectedTab() != MainTabKind.Param)
                return;

            // 3) Нужно имя оборудования
            var equip = _resolveEquipNameForWrite(sender)?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(equip))
                return;

            // 4) EquipItem из Tag
            if (sender is not FrameworkElement fe || fe.Tag is not string equipItem || string.IsNullOrWhiteSpace(equipItem))
                return;

            // 5) Берём текущее значение из редактора
            object? oldValue = TryGetOldValueFromSender(sender, equipItem);
            object? newValue = (sender as BaseEdit)?.EditValue;
            if (!TryNormalizeWriteValue(newValue, out var writeValue))
                return;

            e.Handled = true;
            _endParamFieldEdit();

            await WriteParamAsync(equipName: equip, equipItem: equipItem, writeValue: writeValue, currentValue: oldValue, description: equipItem);
        }

        /// <summary>
        /// Универсальная запись параметра без DevExpress событий.
        /// </summary>
        public async Task WriteFromUiAsync(string? equipItem, object? newValue, object? currentValue = null)
        {
            if (_getSuppressWritesFromPolling())
                return;

            if (_getSelectedTab() != MainTabKind.Param)
                return;

            var (equipName, _, _) = _resolveSelectedEquip();
            var equip = (equipName ?? "").Trim();

            if (string.IsNullOrWhiteSpace(equip))
                return;

            if (string.IsNullOrWhiteSpace(equipItem))
                return;

            if (!TryNormalizeWriteValue(newValue, out var writeValue))
                return;

            await WriteParamAsync(equipName: equip, equipItem: equipItem, writeValue: writeValue, currentValue: currentValue, description: equipItem);
        }

        // ====== Private helpers ======

        private async Task WriteParamAsync(string equipName, string equipItem, string writeValue, object? currentValue, string? description)
        {
            try
            {
                await _paramRwGate.WaitAsync(CancellationToken.None);
                try
                {
                    // Check SCADA security before the actual write.
                    if (!await EnsureWritePrivilegeAsync())
                        return;

                    // Пауза чтения после записи
                    _setParamReadResumeAtUtc(DateTime.UtcNow.AddMilliseconds(400));

                    _setBottomText($"Write: {equipItem}={writeValue} ...");

                    await _equipmentService.WriteEquipItemAsync(equipName, equipItem, writeValue);

                    // Логируем действие оператора best-effort. Ошибка логирования НЕ должна ломать саму запись.
                    await TrySaveOperatorActionAsync(name: $"{equipName}.{equipItem}", currentValue: currentValue, newValue: writeValue, description: description ?? equipItem);

                    _setBottomText($"Wrote: {equipItem}={writeValue} at {DateTime.Now:HH:mm:ss}");
                }
                finally
                {
                    _paramRwGate.Release();
                }
            }
            catch (Exception ex)
            {
                _setBottomText($"Write error ({equipItem}): {ex.Message}");
            }
        }

        // Запись данных из PLC вкладки
        private async Task Plc_WriteValueAsync(PlcRefRow row, object? newValue)
        {
            if (row == null || !row.IsWritable)
                return;

            if (!TryNormalizeWriteValue(newValue, out var writeValueStr))
                return;

            var equipItem = GetPlcEquipItemForTagInfo(row);

            await _paramRwGate.WaitAsync(CancellationToken.None);
            try
            {
                // Check SCADA security before the actual write.
                if (!await EnsureWritePrivilegeAsync())
                    return;

                var tagName = (row.TagName ?? "").Trim();
                if (string.IsNullOrWhiteSpace(tagName) || tagName.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
                {
                    tagName = (await _equipmentService.ResolveTagNameAsync(row.EquipName, equipItem) ?? "").Trim();
                    row.TagName = tagName;
                }

                if (string.IsNullOrWhiteSpace(tagName) || tagName.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
                    return;

                _setBottomText($"Write: {equipItem}={writeValueStr} ...");

                await _equipmentService.WriteTagNameAsync(tagName, writeValueStr);

                // Логируем действие оператора
                await TrySaveOperatorActionAsync(name: $"{row.EquipName}.{row.RefItem}", currentValue: row.Value, newValue: writeValueStr, description: row.Comment);

                _setBottomText($"Wrote: {equipItem}={writeValueStr} at {DateTime.Now:HH:mm:ss}");
            }
            catch
            {
                // ignore / log if needed
            }
            finally
            {
                _paramRwGate.Release();
            }
        }

        /// <summary>
        /// Определяет, какой equip item использовать для записи PLC-строки.
        /// 
        /// Приоритет:
        /// 1) REFITEM из EquipRefBrowse (если он есть)
        /// 2) legacy-fallback по типу строки
        /// </summary>
        private static string GetPlcEquipItemForTagInfo(PlcRefRow row)
        {
            var refItem = (row?.RefItem ?? "").Trim();
            if (!string.IsNullOrWhiteSpace(refItem) &&
                !refItem.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
            {
                return refItem;
            }

            if (row.Type is PlcTypeCustom.EqMotorStatus or PlcTypeCustom.EqValveStatus)
                return "State";

            return "Value";
        }

        private static bool TryNormalizeWriteValue(object? newValue, out string str)
        {
            str = "";
            if (newValue == null)
                return false;

            if (newValue is bool b)
            {
                str = b ? "1" : "0";
                return true;
            }

            if (newValue is string s)
            {
                s = s.Trim();
                if (s.Length == 0) return false;

                s = s.Replace(',', '.');

                if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i))
                {
                    str = i.ToString(CultureInfo.InvariantCulture);
                    return true;
                }

                if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                {
                    str = d.ToString(CultureInfo.InvariantCulture);
                    return true;
                }

                return false;
            }

            if (newValue is int i2) { str = i2.ToString(CultureInfo.InvariantCulture); return true; }
            if (newValue is double d2) { str = d2.ToString(CultureInfo.InvariantCulture); return true; }

            str = Convert.ToString(newValue, CultureInfo.InvariantCulture) ?? "";
            return str.Length > 0;
        }

        private static bool ToBool(object? v)
        {
            try
            {
                if (v is bool b) return b;
                if (v == null) return false;
                return Convert.ToBoolean(v);
            }
            catch
            {
                return false;
            }
        }

        #region Save actions

        /// <summary>
        /// Best-effort логирование действия оператора в SCADA.
        /// Никогда не ломает основную запись.
        /// </summary>
        private async Task TrySaveOperatorActionAsync(string? name, object? currentValue, object? newValue, string? description)
        {
            try
            {
                var safeName = ToCicodeStringArg(name);
                var safeCurrent = ToCicodeValueArg(currentValue);
                var safeNew = ToCicodeValueArg(newValue);
                var safeDescription = ToCicodeStringArg(description ?? "");
                var safeDeviceName = ToCicodeStringArg(_appRuntime.DeviceName);

                var (_, _, equipDescription) = _resolveSelectedEquip();

                await _ctApiService.TagWriteAsync("sWndTitle", $"\"{equipDescription}\"");
                await _ctApiService.CicodeAsync($"SaveActionOperators({safeName}, {safeCurrent}, {safeNew}, {safeDescription}, {safeDeviceName})");
            }
            catch
            {
                // логирование действия оператора не должно валить основной write-flow
            }
        }

        /// <summary>
        /// Аргумент Cicode как строка: "text"
        /// С экранированием двойных кавычек.
        /// </summary>
        private static string ToCicodeStringArg(string? value)
        {
            var s = (value ?? "").Trim();
            s = s.Replace("\"", "\"\"");
            return $"\"{s}\"";
        }

        /// <summary>
        /// Аргумент Cicode как число/булево/строка.
        /// - bool -> 1/0
        /// - числа -> invariant
        /// - строки с числом -> без кавычек
        /// - всё остальное -> "text"
        /// </summary>
        private static string ToCicodeValueArg(object? value)
        {
            if (value == null)
                return "\"\"";

            if (value is bool b)
                return b ? "1" : "0";

            if (value is byte or sbyte or short or ushort or int or uint or long or ulong)
                return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "0";

            if (value is float or double or decimal)
                return Convert.ToString(value, CultureInfo.InvariantCulture)?.Replace(',', '.') ?? "0";

            var s = Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim() ?? "";

            if (bool.TryParse(s, out var boolParsed))
                return boolParsed ? "1" : "0";

            var numeric = s.Replace(',', '.');
            if (double.TryParse(numeric, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                return numeric;

            return ToCicodeStringArg(s);
        }

        private static object? TryGetOldValueFromSender(object sender, string equipItem)
        {
            // PLC ветка
            if (sender is FrameworkElement fePlc && fePlc.Tag is PlcRefRow plcRow)
                return plcRow.Value;

            // Обычная Param-модель
            if (sender is FrameworkElement fe)
            {
                var dc = fe.DataContext;
                if (dc == null || string.IsNullOrWhiteSpace(equipItem))
                    return null;

                try
                {
                    var prop = dc.GetType().GetProperty(equipItem);
                    if (prop != null && prop.CanRead)
                        return prop.GetValue(dc);
                }
                catch
                {
                    // best-effort
                }
            }

            return null;
        }

        #endregion

        #region Security

        private async Task<bool> EnsureWritePrivilegeAsync()
        {
            await Task.CompletedTask;

            if (_appRuntime.DevicePrivilege > 0)
                return true;

            DXMessageBox.Show("You do not have sufficient rights to modify parameters.","Access denied",
                MessageBoxButton.OK,MessageBoxImage.Warning);

            return false;
        }

        /// <summary>
        /// Checks whether the current SCADA operator is allowed to write:
        /// 1) the operator name must contain the configured token (for example "Tab")
        /// 2) the operator must have the required privilege/area
        /// 
        /// If access is denied, shows an English DX message and returns false.
        /// </summary>
        private async Task<bool> EnsureWriteCitectPrivilegeAsync()
        {
            try
            {
                // type 1 = user name, type 2 = full name
                var userName = (await _ctApiService.UserInfoAsync(1)).Trim();
                var fullName = (await _ctApiService.UserInfoAsync(2)).Trim();

                var displayName = !string.IsNullOrWhiteSpace(fullName) ? fullName : userName;
                if (string.IsNullOrWhiteSpace(displayName))
                    displayName = "Unknown user";

                // 1) Additional check by user name token, e.g. only "Tab user" may write
                if (!string.IsNullOrWhiteSpace(_requiredUserNameContains))
                {
                    var containsRequiredToken =
                        (!string.IsNullOrWhiteSpace(userName) &&
                         userName.IndexOf(_requiredUserNameContains, StringComparison.OrdinalIgnoreCase) >= 0)
                        ||
                        (!string.IsNullOrWhiteSpace(fullName) &&
                         fullName.IndexOf(_requiredUserNameContains, StringComparison.OrdinalIgnoreCase) >= 0);

                    if (!containsRequiredToken)
                    {
                        DevExpress.Xpf.Core.DXMessageBox.Show(
                            _getOwnerWindow(),
                            $"Only user '{_requiredUserNameContains} user' can make changes in this application.\n\nCurrent user: {displayName}",
                            "Access denied",
                            MessageBoxButton.OK,
                            MessageBoxImage.Warning);

                        _setBottomText($"Write denied: only '{_requiredUserNameContains} user' is allowed.");
                        return false;
                    }
                }

                // 2) Standard Plant SCADA privilege check
                var hasPrivilege = await _ctApiService.GetPrivAsync(_requiredWritePrivilege, _requiredWriteArea);
                if (hasPrivilege)
                    return true;

                DevExpress.Xpf.Core.DXMessageBox.Show(
                    _getOwnerWindow(),
                    $"User '{displayName}' does not have sufficient access level to perform this operation.\n\nRequired privilege: {_requiredWritePrivilege}, area: {_requiredWriteArea}.",
                    "Access denied",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);

                _setBottomText($"Write denied: insufficient access for {displayName}");
                return false;
            }
            catch (Exception ex)
            {
                _setBottomText($"Security check error: {ex.Message}");

                DevExpress.Xpf.Core.DXMessageBox.Show(
                    _getOwnerWindow(),
                    "Unable to verify access level. The write operation has been cancelled.",
                    "Security check error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);

                return false;
            }
        }

        #endregion
    }
}