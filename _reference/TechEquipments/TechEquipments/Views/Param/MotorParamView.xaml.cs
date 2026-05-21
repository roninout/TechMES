using DevExpress.Xpf.Core;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace TechEquipments.Views.Param
{
    /// <summary>
    /// View для Motor.
    /// Общая логика вынесена в ParamViewBase.
    /// Здесь оставляем только уникальное поведение Motor.
    /// </summary>
    public partial class MotorParamView : ParamViewBase
    {
        // Управление one-shot пульсами Ack/Reset
        private readonly object _pulseLock = new();
        private readonly Dictionary<string, CancellationTokenSource> _pulseByEquipItem = new(StringComparer.OrdinalIgnoreCase);

        // Предыдущее значение Mode, чтобы понимать переход 1->0
        private bool? _lastMode;

        public MotorParamView()
        {
            InitializeComponent();

            DataContextChanged += (_, __) =>
            {
                if (DataContext is MotorModel model)
                    _lastMode = model.Param.Mode;
                else
                    _lastMode = null;
            };
        }

        /// <summary>
        /// Mode: подтверждение только при переходе Automatic -> Service (1 -> 0).
        /// </summary>
        private void Mode_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not ToggleButton tb)
                return;

            if (Host == null)
                return;

            // Фиксируем целевое значение сразу, ДО модального окна.
            // Дальше используем только его, а не tb.IsChecked,
            // потому что во время подтверждения polling может перебиндить UI обратно.
            var newValue = tb.IsChecked == true;

            // Старое значение берём из кэша, а если его нет — из текущей модели.
            var oldValue = _lastMode;

            if (oldValue == null && DataContext is MotorModel model)
                oldValue = model.Param.Mode;

            // Подтверждение только при переходе Automatic -> Service (1 -> 0)
            if (oldValue == true && newValue == false)
            {
                if (!ConfirmModeToService())
                {
                    // Пользователь отменил -> возвращаем UI в прежнее состояние
                    tb.IsChecked = true;
                    _lastMode = true;
                    return;
                }
            }

            // ВАЖНО:
            // пишем зафиксированное newValue, а не текущее tb.IsChecked.
            Host.ParamEditable_WriteFromUi(tb.Tag as string, newValue, oldValue);

            // Обновляем локальный кэш последнего подтверждённого значения
            _lastMode = newValue;
        }

        /// <summary>
        /// Подтверждение перехода в Service mode.
        /// </summary>
        private bool ConfirmModeToService()
        {
            var owner = Window.GetWindow(this);

            const string caption = "Attention!!!";
            const string text = "In service mode, the interlock is OFF!\nAre you sure?";

            var result = owner != null
                ? DXMessageBox.Show(owner, text, caption, MessageBoxButton.OKCancel, MessageBoxImage.Warning)
                : DXMessageBox.Show(text, caption, MessageBoxButton.OKCancel, MessageBoxImage.Warning);

            return result == MessageBoxResult.OK;
        }

        /// <summary>
        /// One-shot кнопки (Ack/Reset):
        /// 1) пишем 1
        /// 2) ждём 3 секунды
        /// 3) пишем 0
        /// Для TimeReset дополнительно просим подтверждение.
        /// </summary>
        private async void Time_PulseButton_Click(object sender, RoutedEventArgs e)
        {
            if (DesignerProperties.GetIsInDesignMode(this))
                return;

            if (sender is not SimpleButton btn)
                return;

            var equipItem = (btn.Tag as string ?? "").Trim();
            if (string.IsNullOrWhiteSpace(equipItem))
                return;

            if (Host == null)
                return;

            // Для Reset показываем подтверждение
            if (string.Equals(equipItem, "TimeReset", StringComparison.OrdinalIgnoreCase))
            {
                if (!ConfirmTimeReset())
                    return;
            }

            CancellationTokenSource cts;

            // Отменяем предыдущий пульс для того же EquipItem
            lock (_pulseLock)
            {
                if (_pulseByEquipItem.TryGetValue(equipItem, out var old))
                {
                    old.Cancel();
                    old.Dispose();
                }

                cts = new CancellationTokenSource();
                _pulseByEquipItem[equipItem] = cts;
            }

            try
            {
                // Мгновенный UI feedback
                btn.Content = "Done";
                btn.IsEnabled = false;

                // Взводим 1
                Host.ParamEditable_WriteFromUi(equipItem, newValue: 1, oldValue: 0);

                // Ждём 3 секунды
                await Task.Delay(TimeSpan.FromSeconds(3), cts.Token);

                // Сбрасываем в 0
                Host.ParamEditable_WriteFromUi(equipItem, newValue: 0, oldValue: 1);
            }
            catch (TaskCanceledException)
            {
                // Нормально: старый pulse отменён новым нажатием
            }
            finally
            {
                var isStillCurrent = false;

                lock (_pulseLock)
                {
                    if (_pulseByEquipItem.TryGetValue(equipItem, out var cur) && ReferenceEquals(cur, cts))
                    {
                        _pulseByEquipItem.Remove(equipItem);
                        isStillCurrent = true;
                    }
                }

                // Возвращаем визуальное состояние под управление стилю
                if (isStillCurrent)
                {
                    btn.ClearValue(ContentControl.ContentProperty);
                    btn.ClearValue(UIElement.IsEnabledProperty);
                }

                cts.Dispose();
            }
        }

        /// <summary>
        /// Подтверждение для TimeReset.
        /// </summary>
        private bool ConfirmTimeReset()
        {
            var owner = Window.GetWindow(this);

            const string caption = "Attention";
            const string text = "Are you sure you want to clear the recorded time intervals of the mechanism's operation?";

            var result = owner != null
                ? DXMessageBox.Show(owner, text, caption, MessageBoxButton.OKCancel, MessageBoxImage.Warning)
                : DXMessageBox.Show(text, caption, MessageBoxButton.OKCancel, MessageBoxImage.Warning);

            return result == MessageBoxResult.OK;
        }
    }
}
