using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace TechEquipments.ViewModels
{
    /// <summary>
    /// Базовый класс для ViewModel.
    /// Минимальная реализация INotifyPropertyChanged без сторонних пакетов.
    /// </summary>
    public abstract class ObservableObject : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
                return false;

            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        /// <summary>
        /// Позволяет вручную поднять несколько зависимых PropertyChanged.
        /// </summary>
        protected void Raise(params string[] propertyNames)
        {
            foreach (var name in propertyNames)
                OnPropertyChanged(name);
        }
    }
}