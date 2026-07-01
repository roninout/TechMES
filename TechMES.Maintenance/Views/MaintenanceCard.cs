using System.Windows.Controls;

namespace TechMES.Maintenance.Views;

/// <summary>
/// Простая карточка Maintenance на базе WPF UI Card.
/// Нужна как переходный контейнер для старых Border-блоков: XAML сохраняет размеры,
/// отступы и bindings, а визуально секция уже рисуется стандартной карточкой WPF UI.
/// </summary>
public sealed class MaintenanceCard : ContentControl
{
}
