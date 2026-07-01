using System.Windows.Controls;

namespace TechMES.Maintenance.Views;

/// <summary>
/// Единая секция Maintenance, которая заменяет старые GroupBox-блоки.
/// Визуальный шаблон находится в MaintenanceTheme.xaml и строится на WPF UI Card,
/// поэтому страницы получают единый Fluent-вид без переписывания существующих bindings.
/// </summary>
public sealed class MaintenanceSection : HeaderedContentControl
{
}
