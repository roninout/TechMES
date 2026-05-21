using DevExpress.XtraRichEdit.Fields.Expression;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.VisualBasic.Devices;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace CtApi
{
    public class CtApiService : ICtApiService, IHostedService
    {
        private readonly CtApi _ctApi;
        private readonly IConfiguration _config;

        /// <summary>
        /// Один gate на все операции с _ctApi:
        /// чтобы heartbeat/reconnect не пересекались с обычными вызовами.
        /// </summary>
        private readonly SemaphoreSlim _apiGate = new(1, 1);

        private CancellationTokenSource? _healthCts;
        private Task? _healthTask;

        private volatile bool _isConnectionAvailable = true;
        public bool IsConnectionAvailable => _isConnectionAvailable;

        public string? LastConnectionError { get; private set; }

        public event Action<bool, string?>? ConnectionStateChanged;

        public CtApiService(IConfiguration config)
        {
            _ctApi = new CtApi();
            _config = config;
        }

        #region IHostedService

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            var path = (_config["CtApi:Path"] ?? "").Trim();
            var ip = (_config["CtApi:Ip"] ?? "").Trim();
            var user = _config["CtApi:User"];
            var password = _config["CtApi:Password"];

            try
            {
                SetCtApiDirectory(path);

                if (string.IsNullOrWhiteSpace(ip))
                    await OpenAsync();
                else
                    await OpenAsync(ip, user, password);

                SetConnectionState(true, null);

                _healthCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                _healthTask = RunHealthMonitorAsync(_healthCts.Token);
            }
            catch (DllNotFoundException ex)
            {
                SetConnectionState(false, $"CtApi.dll or dependency not found. Path: {path}");
                TechLogger.Logger.Error($"CtApiService.StartAsync failed on SetCtApiDirectory. Path={path}.\nException: {ex}");
                throw;
            }
            catch (SEHException ex)
            {
                SetConnectionState(false, $"CtApi native crash on Open. Path: {path}; Ip: {ip}");
                TechLogger.Logger.Error($"CtApiService.StartAsync native crash. Path={path}, Ip={ip}.\nException: {ex}");
                throw;
            }
            catch (Exception ex)
            {
                SetConnectionState(false, $"CtApi start failed. Path: {path}; Ip: {ip}");
                TechLogger.Logger.Error($"CtApiService.StartAsync failed. Path={path}, Ip={ip}.\nException: {ex}");
                throw;
            }
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            try
            {
                _healthCts?.Cancel();
            }
            catch { }

            if (_healthTask != null)
            {
                try
                {
                    await _healthTask;
                }
                catch (OperationCanceledException) { }
                catch { }
            }

            try
            {
                await _apiGate.WaitAsync(cancellationToken);
                try
                {
                    _ctApi.Close();
                }
                finally
                {
                    _apiGate.Release();
                }
            }
            catch
            {
                // ignore on shutdown
            }
        }

        #endregion

        #region ICtApiService

        public void SetCtApiDirectory(string path)
        {
            _ctApi.SetCtApiDirectory(path);
        }

        public async Task OpenAsync(string ip = null, string user = null, string password = null)
        {
            await _apiGate.WaitAsync();
            try
            {
                if (string.IsNullOrEmpty(ip))
                    await _ctApi.OpenAsync(null, null, null, CtOpen.Reconnect);
                else
                    await _ctApi.OpenAsync(ip, user, password, 0);
            }
            finally
            {
                _apiGate.Release();
            }
        }

        public async Task<string> TagReadAsync(string tagName)
        {
            await _apiGate.WaitAsync();
            try
            {
                return await _ctApi.TagReadAsync(tagName);
            }
            finally
            {
                _apiGate.Release();
            }
        }

        public async Task<object> TagWriteAsync(string tagName, string value)
        {
            await _apiGate.WaitAsync();
            try
            {
                await _ctApi.TagWriteAsync(tagName, value);
                return null!;
            }
            finally
            {
                _apiGate.Release();
            }
        }

        public async Task<IEnumerable<Dictionary<string, string>>> FindAsync(string tableName, string filter, string cluster, params string[] propertiesName)
        {
            await _apiGate.WaitAsync();
            try
            {
                return await _ctApi.FindAsync(tableName, filter, cluster, propertiesName);
            }
            finally
            {
                _apiGate.Release();
            }
        }

        public async Task<string> CicodeAsync(string cmd)
        {
            await _apiGate.WaitAsync();
            try
            {
                return await _ctApi.CicodeAsync(cmd);
            }
            finally
            {
                _apiGate.Release();
            }
        }

        public async Task<bool> IsConnected()
        {
            return await ProbeConnectionAsync(CancellationToken.None);
        }

        public string UserInfo(int type)
        {
            return _ctApi.UserInfo(type);
        }

        public async Task<bool> GetPrivAsync(int priv, int area)
        {
            await _apiGate.WaitAsync();
            try
            {
                return await _ctApi.GetPrivAsync(priv, area);
            }
            finally
            {
                _apiGate.Release();
            }
        }

        public async Task<string> LoginAsync(string userName, string password, bool sync = false, string language = "")
        {
            await _apiGate.WaitAsync();
            try
            {
                return await _ctApi.LoginAsync(userName, password, sync, language);
            }
            finally
            {
                _apiGate.Release();
            }
        }

        public async Task<string> LogoutAsync()
        {
            await _apiGate.WaitAsync();
            try
            {
                return await _ctApi.LogoutAsync();
            }
            finally
            {
                _apiGate.Release();
            }
        }

        public async Task<string> UserInfoAsync(int type)
        {
            await _apiGate.WaitAsync();
            try
            {
                return await _ctApi.UserInfoAsync(type);
            }
            finally
            {
                _apiGate.Release();
            }
        }

        public async Task<List<TrnData>> GetTrnData(string tagName, DateTime startTime, DateTime endTime)
        {
            await _apiGate.WaitAsync();
            try
            {
                float period = 1.0f;
                uint displayMode = DisplayMode.Get(Ordering.OldestToNewest, Condense.Mean, Stretch.Raw, 0, BadQuality.Zero, Raw.None);
                int dataMode = 1;

                var data = await _ctApi.TrnQueryAsync(startTime, endTime, period, tagName, displayMode, dataMode, "Cluster1");
                return data.ToList();
            }
            finally
            {
                _apiGate.Release();
            }
        }

        #endregion

        #region Connection monitor

        /// <summary>
        /// Периодическая проверка связи.
        /// Обычные TagRead/TagWrite не трогаем.
        /// </summary>
        private async Task RunHealthMonitorAsync(CancellationToken ct)
        {
            var periodSeconds = Math.Max(1, _config.GetValue("CtApi:HealthCheckPeriodSeconds", 5));
            var failThreshold = Math.Max(1, _config.GetValue("CtApi:HealthCheckFailCount", 3));

            var failCount = 0;

            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(periodSeconds));

            try
            {
                while (await timer.WaitForNextTickAsync(ct))
                {
                    var ok = await ProbeConnectionAsync(ct);

                    if (ok)
                    {
                        failCount = 0;

                        if (!_isConnectionAvailable)
                            SetConnectionState(true, "CtApi connection restored.");

                        continue;
                    }

                    failCount++;

                    // единичный сбой ещё не считаем потерей связи
                    if (failCount < failThreshold)
                        continue;

                    if (_isConnectionAvailable)
                        SetConnectionState(false, "CtApi connection lost.");

                    // Пытаемся переподключиться.
                    await TryReconnectAsync(ct);

                    // После reconnect сразу перепроверим.
                    ok = await ProbeConnectionAsync(ct);

                    if (ok)
                    {
                        failCount = 0;
                        SetConnectionState(true, "CtApi connection restored.");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // normal shutdown
            }
        }

        /// <summary>
        /// Отдельный мягкий probe.
        /// Ничего не бросает наружу.
        /// </summary>
        private async Task<bool> ProbeConnectionAsync(CancellationToken ct)
        {
            try
            {
                await _apiGate.WaitAsync(ct);
                try
                {
                    return await _ctApi.TryProbeConnectionAsync("TagRead(sWndTitle)");
                }
                finally
                {
                    _apiGate.Release();
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Мягкая попытка переподключения.
        /// Ошибка тут не должна валить приложение.
        /// </summary>
        private async Task TryReconnectAsync(CancellationToken ct)
        {
            try
            {
                await _apiGate.WaitAsync(ct);
                try
                {
                    try
                    {
                        _ctApi.Close();
                    }
                    catch
                    {
                        // ignore close errors
                    }

                    var ip = _config["CtApi:Ip"];
                    if (string.IsNullOrWhiteSpace(ip))
                        await _ctApi.OpenAsync(null, null, null, CtOpen.Reconnect);
                    else
                        await _ctApi.OpenAsync(ip, _config["CtApi:User"], _config["CtApi:Password"], 0);
                }
                finally
                {
                    _apiGate.Release();
                }
            }
            catch
            {
                // не бросаем наружу — повторим на следующем heartbeat
            }
        }

        private void SetConnectionState(bool isConnected, string? message)
        {
            var changed =
                _isConnectionAvailable != isConnected ||
                !string.Equals(LastConnectionError ?? "", message ?? "", StringComparison.Ordinal);

            _isConnectionAvailable = isConnected;
            LastConnectionError = isConnected ? null : message;

            if (changed)
                ConnectionStateChanged?.Invoke(isConnected, message);
        }

        #endregion
    }
}