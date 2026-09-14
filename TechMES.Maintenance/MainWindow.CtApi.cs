using System.Net.Http;
using System.Net.Http.Json;
using System.Windows.Threading;
using TechMES.Contracts.Scada;

namespace TechMES.Maintenance;

public partial class MainWindow
{
    private readonly HttpClient _ctApiStatusHttp = new()
    {
        Timeout = TimeSpan.FromSeconds(4)
    };

    private readonly CancellationTokenSource _ctApiStatusStop = new();

    private DispatcherTimer? _ctApiStatusTimer;
    private bool _ctApiStatusBusy;
    private string _ctApiActiveServerText = "Not checked.";

    public string CtApiActiveServerText
    {
        get => _ctApiActiveServerText;
        private set
        {
            _ctApiActiveServerText = value;
            OnPropertyChanged(nameof(CtApiActiveServerText));
        }
    }

    public bool CtApiPrimaryOnline { get; private set; }
    public bool CtApiSecondaryOnline { get; private set; }

    public string CtApiPrimaryCheckedText { get; private set; } = "";
    public string CtApiSecondaryCheckedText { get; private set; } = "";

    private void ApplyCtApiHealth(PlantScadaHealthResponse? health)
    {
        var state = health?.Redundancy;
        var fresh = state is not null && !IsCtApiSnapshotStale(state);

        CtApiPrimaryOnline = fresh && state!.Primary.Configured && state.Primary.Connected;
        CtApiSecondaryOnline = fresh && state!.Secondary.Configured && state.Secondary.Connected;

        var text = fresh ? $"Checked: {state!.CheckedAtUtc!.Value.LocalDateTime:dd.MM.yyyy HH:mm:ss}" : "";

        CtApiPrimaryCheckedText = CtApiPrimaryOnline && state!.ActiveRole == "Primary" ? text : "";
        CtApiSecondaryCheckedText = CtApiSecondaryOnline && state!.ActiveRole == "Secondary" ? text : "";
        CtApiActiveServerText = health is null ? "Runtime status unavailable." : FormatCtApiActiveServer(health);

        OnPropertyChanged(nameof(CtApiPrimaryOnline));
        OnPropertyChanged(nameof(CtApiSecondaryOnline));
        OnPropertyChanged(nameof(CtApiPrimaryCheckedText));
        OnPropertyChanged(nameof(CtApiSecondaryCheckedText));
    }

    private void StartCtApiStatusMonitoring()
    {
        if (_ctApiStatusTimer is not null)
            return;

        _ctApiStatusTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(5)
        };

        _ctApiStatusTimer.Tick += OnCtApiStatusTick;

        Closed += (_, _) =>
        {
            _ctApiStatusTimer.Stop();
            _ctApiStatusTimer.Tick -= OnCtApiStatusTick;

            _ctApiStatusStop.Cancel();
            _ctApiStatusStop.Dispose();
            _ctApiStatusHttp.Dispose();
        };

        _ctApiStatusTimer.Start();
        OnCtApiStatusTick(this, EventArgs.Empty);
    }

    private async void OnCtApiStatusTick(object? sender, EventArgs e)
    {
        if (_ctApiStatusBusy || _ctApiStatusStop.IsCancellationRequested)
            return;

        _ctApiStatusBusy = true;

        try
        {
            var health = await ReadCtApiHealthAsync();
            ApplyCtApiHealth(health);
        }
        catch (Exception ex)
        {
            if (!_ctApiStatusStop.IsCancellationRequested)
            {
                ApplyCtApiHealth(null);
                CtApiActiveServerText = $"Runtime status unavailable ({ex.GetType().Name}).";
            }
        }
        finally
        {
            _ctApiStatusBusy = false;
        }
    }

    private async Task<PlantScadaHealthResponse> ReadCtApiHealthAsync()
    {
        var url = GetRuntimeDiagnosticsBaseUrl().TrimEnd('/') + "/api/scada/health";

        return await _ctApiStatusHttp.GetFromJsonAsync<PlantScadaHealthResponse>(url, _ctApiStatusStop.Token) ?? throw new InvalidOperationException("Empty Runtime health response.");
    }

    private static bool IsCtApiSnapshotStale(PlantScadaRedundancyState state)
    {
        return state.CheckedAtUtc is null || DateTimeOffset.UtcNow - state.CheckedAtUtc.Value > TimeSpan.FromSeconds(Math.Max(5, state.HealthPeriodSeconds) * 3d);
    }

    private static string FormatCtApiActiveServer(PlantScadaHealthResponse health)
    {
        var state = health.Redundancy;

        if (state is null)
            return $"{health.Provider}: {health.Message}";

        if (state.CheckedAtUtc is null)
            return "Waiting for the first CtApi health check.";

        var active = state.ActiveRole is null ? "Disconnected" : $"{state.ActiveRole}: " + $"{(string.IsNullOrEmpty(state.ActiveServer) ? "Local" : state.ActiveServer)}";
        var prefix = IsCtApiSnapshotStale(state) ? "Last known: " : "";

        return $"{prefix}{active}. " + $"Checked: {state.CheckedAtUtc.Value.LocalDateTime:dd.MM.yyyy HH:mm:ss}";
    }

    // Diagnostics читает снимок Runtime.
    // Само открытие Checks не создаёт SCADA-соединения и не пишет теги.
    private async Task AddCtApiRuntimeChecksAsync()
    {
        try
        {
            var health = await ReadCtApiHealthAsync();
            ApplyCtApiHealth(health);

            var state = health.Redundancy;

            if (state is null)
            {
                AddDependencyCheck("CtApi", "Runtime redundancy", "Warning", $"Provider: {health.Provider}. " + "Redundancy diagnostics unavailable; " + "check the deployed Runtime version.");
                return;
            }

            var stale = IsCtApiSnapshotStale(state);

            AddDependencyCheck("CtApi", "Active server", stale ? "Warning" : health.IsConnected ? "OK" : "Error", CtApiActiveServerText + (stale ? " Health snapshot is not current." : ""));

            foreach (var server in new[] { state.Primary, state.Secondary })
            {
                AddDependencyCheck("CtApi", server.Role + " connection", !server.Configured || stale ? "Warning" : server.Connected ? "OK" : "Error", $"{server.Server}: {server.Message}");

                var primary = server.Role == "Primary";
                var expectedServer = primary ? TypedAppSettings.CtApiServer : TypedAppSettings.CtApiServerSecondary;
                var expectedWriteTag = primary ? TypedAppSettings.CtApiPrimaryConnectionTag : TypedAppSettings.CtApiSecondaryConnectionTag;
                var expectedReadTag = primary ? TypedAppSettings.CtApiPrimaryStatusTag : TypedAppSettings.CtApiSecondaryStatusTag;

                if (!string.Equals(server.Server, expectedServer.Trim(), StringComparison.OrdinalIgnoreCase) || !string.Equals(server.WriteTag, expectedWriteTag.Trim(), StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(server.ReadTag, expectedReadTag.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    AddDependencyCheck("CtApi", server.Role + " applied settings", "Warning", "Runtime settings differ from this form. " + "Save/deploy settings and restart Runtime to apply them.");
                }

                var writeStatus = stale ? "Warning" : server.WriteTagReadOk == false || server.WriteOk == false ? "Error" : server.WriteOk == true ? "OK" : "Warning";

                AddDependencyCheck("CtApi", server.Role + " Write tag", writeStatus, $"{server.WriteTag}; " + $"read: {server.WriteValue ?? "—"}; " + $"last sent: {server.LastWrittenValue ?? "—"}; " + server.WriteMessage);

                var readStale = server.ReadAtUtc is null || DateTimeOffset.UtcNow - server.ReadAtUtc.Value > TimeSpan.FromSeconds(Math.Max(5, state.HealthPeriodSeconds) * 3d);
                var readStatus = stale ? "Warning" : server.ReadOk == false ? "Error" : readStale ? "Warning" : server.ReadOk == true ? "OK" : "Warning";

                AddDependencyCheck("CtApi", server.Role + " Read status tag", readStatus, $"{server.ReadTag}; " + $"PLC value: {server.ReadValue ?? "—"}; " + server.ReadMessage);
            }
        }
        catch (Exception ex)
        {
            AddDependencyCheck("CtApi", "Runtime redundancy", "Error",ex.Message);
        }
    }
}