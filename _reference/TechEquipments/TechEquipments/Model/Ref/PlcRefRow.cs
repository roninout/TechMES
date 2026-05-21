using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace TechEquipments
{
    /// <summary>
    /// Одна строка PLC refs (для PLC области в Param):
    /// - EquipName: значение из REFEQUIP
    /// - RefItem:   значение из REFITEM (какой именно item читать/писать)
    /// - Type:      тип из CUSTOM1 (EqNumW/EqCheckRW/...)
    /// - Title:     текст слева ("Equip: Comment")
    /// - TagName:   кэш результата TagInfo(...,0)
    /// - Value:     текущее значение тега (double?)
    /// </summary>
    public sealed class PlcRefRow : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        public string EquipName { get; }

        private string _refItem = "";
        /// <summary>
        /// Какой equip item реально связан у этой PLC-строки.
        /// Например: Value / State / Opened / Closed / ...
        /// </summary>
        public string RefItem
        {
            get => _refItem;
            private set
            {
                value = (value ?? "").Trim();
                if (_refItem == value) return;
                _refItem = value;
                OnPropertyChanged();
            }
        }

        public PlcTypeCustom Type { get; private set; }

        public string Comment { get; private set; }

        private string _tagName = "";
        public string TagName
        {
            get => _tagName;
            set
            {
                value = (value ?? "").Trim();
                if (_tagName == value) return;
                _tagName = value;
                OnPropertyChanged();
            }
        }

        private string _unit = "";
        public string Unit
        {
            get => _unit;
            set
            {
                value = (value ?? "").Trim();
                if (_unit == value) return;
                _unit = value;
                OnPropertyChanged();
            }
        }

        private double? _value;
        public double? Value
        {
            get => _value;
            set
            {
                if (_value == value) return;
                _value = value;
                OnPropertyChanged();
            }
        }

        private bool _valueForced;
        /// <summary>
        /// True => показываем "Forced" перед квадратом (для EqDigital/EqDigitalInOut).
        /// </summary>
        public bool ValueForced
        {
            get => _valueForced;
            set
            {
                if (_valueForced == value) return;
                _valueForced = value;
                OnPropertyChanged();
            }
        }

        private bool _forceCmd;
        /// <summary>
        /// Флаг команды форсирования для PLC digital rows.
        /// Теперь именно его используем в стиле мигания "Forced".
        /// </summary>
        public bool ForceCmd
        {
            get => _forceCmd;
            set
            {
                if (_forceCmd == value) return;
                _forceCmd = value;
                OnPropertyChanged();
            }
        }

        private string _forcedTagName = "";
        public string ForcedTagName
        {
            get => _forcedTagName;
            set
            {
                value = (value ?? "").Trim();
                if (_forcedTagName == value) return;
                _forcedTagName = value;
                OnPropertyChanged();
            }
        }

        /// <summary>Какие типы разрешаем редактировать (можно расширять).</summary>
        public bool IsWritable => Type is
                   PlcTypeCustom.EqCheck
                or PlcTypeCustom.EqCheckRW
                or PlcTypeCustom.EqNumW
                or PlcTypeCustom.EqButton
                or PlcTypeCustom.EqButtonUp
                or PlcTypeCustom.EqButtonDown
                or PlcTypeCustom.EqButtonMode
                or PlcTypeCustom.EqButtonStartStop;

        /// <summary>Текст слева.</summary>
        public string Title => string.IsNullOrWhiteSpace(Comment) ? EquipName : $"{EquipName}:    {Comment}";

        public PlcRefRow(string equipName, string refItem, PlcTypeCustom type, string comment)
        {
            EquipName = (equipName ?? "").Trim();
            RefItem = refItem ?? "";
            Type = type;
            Comment = (comment ?? "").Trim();
        }

        public void UpdateMeta(string refItem, PlcTypeCustom type, string comment)
        {
            RefItem = refItem ?? "";
            Type = type;
            Comment = (comment ?? "").Trim();

            OnPropertyChanged(nameof(Type));
            OnPropertyChanged(nameof(Comment));
            OnPropertyChanged(nameof(IsWritable));
            OnPropertyChanged(nameof(Title));
        }

        public void UpdateValue(double? v) => Value = v;

        public static PlcTypeCustom ParseCustom(string? sCustom)
        {
            var s = (sCustom ?? "").Trim();
            if (s.Length == 0) return PlcTypeCustom.Unknown;

            return Enum.TryParse(s, ignoreCase: true, out PlcTypeCustom e) ? e : PlcTypeCustom.Unknown;
        }

        private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}