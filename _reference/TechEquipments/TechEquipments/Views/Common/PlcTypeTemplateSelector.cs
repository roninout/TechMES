using System.Windows;
using System.Windows.Controls;

namespace TechEquipments
{
    /// <summary>
    /// Выбирает DataTemplate по PlcTypeCustom.
    /// </summary>
    public sealed class PlcTypeTemplateSelector : DataTemplateSelector
    {
        public DataTemplate ToggleTemplate { get; set; }
        public DataTemplate TextTemplate { get; set; }
        public DataTemplate ButtonTemplate { get; set; }
        public DataTemplate DigitalTemplate { get; set; }
        public DataTemplate EmptyTemplate { get; set; }
        public DataTemplate ValveStatusTemplate { get; set; }
        public DataTemplate MotorStatusTemplate { get; set; }

        public override DataTemplate SelectTemplate(object item, DependencyObject container)
        {
            if (item is not PlcRefRow row)
                return EmptyTemplate;

            return row.Type switch
            {
                PlcTypeCustom.EqCheck or PlcTypeCustom.EqCheckRW or PlcTypeCustom.EqCheckDisplay => ToggleTemplate,
                PlcTypeCustom.EqNumR or PlcTypeCustom.EqNumW => TextTemplate,

                PlcTypeCustom.EqButton or PlcTypeCustom.EqButtonUp or PlcTypeCustom.EqButtonDown
                    or PlcTypeCustom.EqButtonMode or PlcTypeCustom.EqButtonStartStop => ButtonTemplate,

                PlcTypeCustom.EqDigital or PlcTypeCustom.EqDigitalInOut => DigitalTemplate,

                PlcTypeCustom.EqValveStatus => ValveStatusTemplate,
                PlcTypeCustom.EqMotorStatus => MotorStatusTemplate,

                _ => EmptyTemplate
            };
        }
    }
}