using System;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace TechEquipments
{
    /// <summary>
    /// Одна строка DI/DO связей:
    /// - Equip (EquipListBoxItem)
    /// - ParamModel (DIParam/DOParam)
    /// - вычисляемые свойства для UI (Title/Value/ChanelShort)
    /// </summary>
    public sealed class DiDoRefRow : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        public EquipListBoxItem EquipItem { get; private set; }
        public object ParamModel { get; private set; }

        private bool _valueForced;
        /// <summary>
        /// Legacy/diagnostic flag: фактическое значение форсированного канала.
        /// Можно оставить для совместимости.
        /// </summary>
        public bool ValueForced
        {
            get => _valueForced;
            private set
            {
                if (_valueForced == value) return;
                _valueForced = value;
                OnPropertyChanged();
            }
        }

        private bool _forceCmd;
        /// <summary>
        /// Флаг команды форсирования, который теперь используем в стилях/мигании.
        /// Источник: DIParam.ForceCmd / DOParam.ForceCmd.
        /// </summary>
        public bool ForceCmd
        {
            get => _forceCmd;
            private set
            {
                if (_forceCmd == value) return;
                _forceCmd = value;
                OnPropertyChanged();
            }
        }

        public DiDoRefRow(EquipListBoxItem equipItem, object paramModel)
        {
            EquipItem = equipItem ?? throw new ArgumentNullException(nameof(equipItem));
            ParamModel = paramModel ?? throw new ArgumentNullException(nameof(paramModel));

            // первичная инициализация
            ValueForced = ExtractValueForced(paramModel);
            ForceCmd = ExtractForceCmd(paramModel);
        }

        /// <summary>
        /// "{Equipment}: {Description}"
        /// Description берём из Comment (если есть), иначе Tag.
        /// </summary>
        public string Title
        {
            get
            {
                var eq = (EquipItem.Equipment ?? "").Trim();
                var descr = (EquipItem.Description ?? "").Trim();

                return string.IsNullOrWhiteSpace(descr) ? eq : $"{eq}: {descr}";
            }
        }

        /// <summary>Value из DIParam/DOParam (ожидаем свойство Value).</summary>
        public object? Value => GetProp(ParamModel, "Value");

        /// <summary>Chanel из модели (если реализует IHasChanel) либо свойство Chanel.</summary>
        public string Chanel
        {
            get
            {
                if (ParamModel is IHasChanel hc)
                    return (hc.Chanel ?? "").Trim();

                return System.Convert.ToString(GetProp(ParamModel, "Chanel"), CultureInfo.InvariantCulture)?.Trim() ?? "";
            }
        }

        /// <summary>Короткий chanel: "6.3.4" -> "6.3".</summary>
        public string ChanelShort => FormatChanelShort(Chanel);

        public string EquipName => (EquipItem.Equipment ?? "").Trim();

        /// <summary>
        /// Обновление строки без пересоздания (чтобы UI не мигал).
        /// </summary>
        public void Update(EquipListBoxItem equipItem, object paramModel)
        {
            EquipItem = equipItem;
            ParamModel = paramModel;

            // обновляем форс-флаги
            ValueForced = ExtractValueForced(paramModel);
            ForceCmd = ExtractForceCmd(paramModel);

            OnPropertyChanged(nameof(EquipItem));
            OnPropertyChanged(nameof(ParamModel));
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(Value));
            OnPropertyChanged(nameof(Chanel));
            OnPropertyChanged(nameof(ChanelShort));
            OnPropertyChanged(nameof(EquipName));
        }

        private static object? GetProp(object obj, string name)
        {
            try
            {
                return obj.GetType().GetProperty(name)?.GetValue(obj);
            }
            catch { return null; }
        }

        private static string FormatChanelShort(string raw)
        {
            raw = (raw ?? "").Trim();
            if (raw.Length == 0) return "";
            var parts = raw.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return parts.Length >= 2 ? $"{parts[0]}.{parts[1]}" : raw;
        }

        /// <summary>
        /// Достаём ValueForced из DIParam/DOParam.
        /// </summary>
        private static bool ExtractValueForced(object? model)
        {
            if (model is DIParam di)
                return di.ValueForced;

            if (model is DOParam dout)
                return dout.ValueForced;

            return false;
        }

        /// <summary>
        /// Достаём ForceCmd из DIParam/DOParam.
        /// </summary>
        private static bool ExtractForceCmd(object? model)
        {
            if (model is DIParam di)
                return di.ForceCmd;

            if (model is DOParam dout)
                return dout.ForceCmd;

            return false;
        }
    }
}