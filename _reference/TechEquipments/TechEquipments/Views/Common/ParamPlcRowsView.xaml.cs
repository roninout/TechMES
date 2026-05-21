using System.Windows.Controls;

namespace TechEquipments.Views.Common
{
    /// <summary>
    /// Общий PLC-блок для Param settings.
    /// Используется в разных ParamView, чтобы не дублировать XAML.
    /// </summary>
    public partial class ParamPlcRowsView : UserControl
    {
        public ParamPlcRowsView()
        {
            InitializeComponent();
        }
    }
}
