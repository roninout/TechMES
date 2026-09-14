using System.Net.Http;
using System.Net.Http.Json;
using System.Windows.Threading;
using TechMES.Contracts.Scada;

namespace TechMES.Maintenance;

public partial class MainWindow
{
    private readonly HttpClient _ctApiStatusHttp = new() { Timeout = TimeSpan.FromSeconds(4) };
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

    private void StartCtApiStatusMonitoring()
    {
        if (_ctApiStatusTimer is not null)
            return;

        _ctApiStatusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
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
            CtApiActiveServerText = FormatCtApiActiveServer(health);
        }
        catch (Exception ex)
        {
            if (!_ctApiStatusStop.IsCancellationRequested)
                CtApiActiveServerText = $"Runtime status unavailable ({ex.GetType().Name}).";
        }
        finally
        {
            _ctApiStatusBusy = false;
        }
    }

    private async Task<PlantScadaHealthResponse> ReadCtApiHealthAsync()
    {
        return await _ctApiStatusHttp.GetFromJsonAsync<PlantScadaHealthResponse>(GetRuntimeDiagnosticsBaseUrl().TrimEnd('/') + "/api/scada/health", _ctApiStatusStop.Token)
            ?? throw new InvalidOperationException("Empty Runtime health response.");
    }

    private static string FormatCtApiActiveServer(PlantScadaHealthResponse health)
    {
        var state = health.Redundancy;

        if (state is null)
            return $"{health.Provider}: {health.Message}";

        if (state.CheckedAtUtc is null)
            return "Waiting for the first CtApi health check.";

        var active = state.ActiveRole is null ? "Disconnected" : $"{state.ActiveRole}: {(string.IsNullOrEmpty(state.ActiveServer) ? "Local" : state.ActiveServer)}";

        return $"{(IsCtApiSnapshotStale(state) ? "Last known: " : "")}{active}. Checked: {state.CheckedAtUtc.Value.LocalDateTime:dd.MM.yyyy HH:mm:ss}";
    }

    private static bool IsCtApiSnapshotStale(PlantScadaRedundancyState state) => state.CheckedAtUtc is null || DateTimeOffset.UtcNow - state.CheckedAtUtc.Value > TimeSpan.FromSeconds(Math.Max(5, state.HealthPeriodSeconds) * 3d);

    // Checks читает тот же снимок worker-а. Проверки не создают соединения и не пишут теги.
    private async Task AddCtApiRuntimeChecksAsync()
    {
        try
        {
            var health = await ReadCtApiHealthAsync();
            CtApiActiveServerText = FormatCtApiActiveServer(health);

            var state = health.Redundancy;

            if (state is null)
            {
                AddDependencyCheck("CtApi", "Runtime redundancy", "Warning", $"Provider: {health.Provider}. Redundancy diagnostics unavailable; check the deployed Runtime version.");
                return;
            }

            var stale = IsCtApiSnapshotStale(state);

            AddDependencyCheck("CtApi", "Active server", stale ? "Warning" : health.IsConnected ? "OK" : "Error", CtApiActiveServerText + (stale ? " Health snapshot is not current." : ""));

            foreach (var server in new[] { state.Primary, state.Secondary })
            {
                AddDependencyCheck("CtApi", server.Role + " connection", !server.Configured || stale ? "Warning" : server.Connected ? "OK" : "Error", $"{server.Server}: {server.Message}");

                var expectedServer = server.Role == "Primary" ? TypedAppSettings.CtApiServer : TypedAppSettings.CtApiServerSecondary;
                var expectedTag = server.Role == "Primary" ? TypedAppSettings.CtApiPrimaryConnectionTag : TypedAppSettings.CtApiSecondaryConnectionTag;

                if (!string.Equals(server.Server, expectedServer.Trim(), StringComparison.OrdinalIgnoreCase) || !string.Equals(server.ControlTag, expectedTag.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    AddDependencyCheck("CtApi", server.Role + " applied settings", "Warning", "Runtime settings differ from this form. Save/deploy settings and restart Runtime to apply them.");
                }

                var tagStatus = stale ? "Warning" : server.TagReadOk == false || server.TagWriteOk == false ? "Error" : server.TagReadOk == true && server.TagWriteOk == true ? "OK" : "Warning";

                AddDependencyCheck("CtApi", server.Role + " control tag", tagStatus, $"{server.ControlTag}; read: {server.TagValue ?? "—"}; {server.TagMessage}");
            }
        }
        catch (Exception ex)
        {
            AddDependencyCheck("CtApi", "Runtime redundancy", "Error", ex.Message);
        }
    }
}