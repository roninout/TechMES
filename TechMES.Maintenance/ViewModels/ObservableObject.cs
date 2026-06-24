using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace TechMES.Maintenance.ViewModels;

/// <summary>
/// Минимальная базовая модель для WPF binding.
/// Не используем внешние MVVM-пакеты, чтобы Maintenance собирался без дополнительных зависимостей.
/// </summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    /// <summary>
    /// Событие WPF, через которое UI узнает об изменении свойства.
    /// </summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Обновляет backing field и уведомляет UI только если значение реально изменилось.
    /// </summary>
    protected bool SetProperty<T>(
        ref T field,
        T value,
        [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    /// <summary>
    /// Явно уведомляет WPF об изменении свойства.
    /// </summary>
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
