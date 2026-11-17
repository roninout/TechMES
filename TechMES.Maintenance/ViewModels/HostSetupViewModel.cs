using System.Collections.ObjectModel;
using System.Globalization;
using TechMES.Contracts.Host;

namespace TechMES.Maintenance.ViewModels;

/// <summary>Редактируемый профиль Host и результаты проверки текущего компьютера.</summary>
public sealed class HostSetupViewModel : ObservableObject
{
    private HostSharedAddressSettings _shared = new();
    private HostControlSettings _control = new();
    private HostWebFailoverSettings _webFailover = new();
    private string _report = "Загрузите настройки установленного Host.";
    private bool _busy;
    private string _prefixLengthText = "24";
    private string _controlPortText = "5117";
    private string _failureThresholdText = "4";
    private string _recoveryThresholdText = "3";

    public HostSharedAddressSettings Shared { get => _shared; set { SetProperty(ref _shared, value); PrefixLengthText = value.PrefixLength.ToString(CultureInfo.InvariantCulture); } }
    public HostControlSettings Control { get => _control; set { SetProperty(ref _control, value); ControlPortText = value.Port.ToString(CultureInfo.InvariantCulture); } }
    public HostWebFailoverSettings WebFailover { get => _webFailover; set { SetProperty(ref _webFailover, value); FailureThresholdText = value.FailureThreshold.ToString(CultureInfo.InvariantCulture); RecoveryThresholdText = value.RecoveryThreshold.ToString(CultureInfo.InvariantCulture); } }

    public string FailureThresholdText { get => _failureThresholdText; set => SetProperty(ref _failureThresholdText, value); }
    public string RecoveryThresholdText { get => _recoveryThresholdText; set => SetProperty(ref _recoveryThresholdText, value); }
    public string PrefixLengthText { get => _prefixLengthText; set => SetProperty(ref _prefixLengthText, value); }
    public string ControlPortText { get => _controlPortText; set => SetProperty(ref _controlPortText, value); }

    public ObservableCollection<HostAdapterInfo> Adapters { get; } = [];
    public string Report { get => _report; set => SetProperty(ref _report, value); }
    public bool Busy { get => _busy; set { SetProperty(ref _busy, value); OnPropertyChanged(nameof(CanOperate)); } }
    public bool CanOperate => !Busy;
    public int HealthPort { get; set; } = 5116;

    /// <summary>Не позволяет ошибке преобразования WPF незаметно оставить прежнее числовое значение.</summary>
    public void ApplyNumericInputs()
    {
        if (!int.TryParse(PrefixLengthText, NumberStyles.None, CultureInfo.InvariantCulture, out var prefix) || prefix is < 1 or > 30) throw new InvalidOperationException("Укажите префикс числом от 1 до 30.");
        if (!int.TryParse(ControlPortText, NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is < 1024 or > 65535 || port == HealthPort) throw new InvalidOperationException("Укажите отдельный порт Host от 1024 до 65535.");
        if (!int.TryParse(FailureThresholdText, NumberStyles.None, CultureInfo.InvariantCulture, out var failures) || failures is < 2 or > 60) throw new InvalidOperationException("Укажите порог отказа WEB от 2 до 60.");
        if (!int.TryParse(RecoveryThresholdText, NumberStyles.None, CultureInfo.InvariantCulture, out var recovery) || recovery is < 2 or > 60) throw new InvalidOperationException("Укажите порог готовности резерва от 2 до 60.");

        Shared.PrefixLength = prefix;
        Control.Port = port;
        WebFailover.FailureThreshold = failures;
        WebFailover.RecoveryThreshold = recovery;
    }
}