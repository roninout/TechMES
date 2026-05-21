using System;
using System.Threading;
using System.Windows;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using CtApi;
using TechEquipments.Services.QR;
using Microsoft.EntityFrameworkCore;
using TechEquipments.ViewModels;
using DevExpress.Xpf.Core;

namespace TechEquipments
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        public static IHost AppHost { get; private set; } = null!;

        /// <summary>
        /// Mutex для запрета запуска второго экземпляра приложения.
        /// Global\ - чтобы защита работала не только в рамках одной сессии Windows.
        /// </summary>
        private Mutex? _singleInstanceMutex;

        /// <summary>
        /// Уникальное имя mutex.
        /// Лучше не менять без необходимости, чтобы всегда считалось одним и тем же приложением.
        /// </summary>
        private const string SingleInstanceMutexName = @"Global\TechEquipments_SingleInstance";

        public App()
        {
            AppHost = Host.CreateDefaultBuilder()
                .ConfigureAppConfiguration((context, config) =>
                {
                    config.SetBasePath(AppContext.BaseDirectory);
                    config.AddJsonFile("appsettings.json", optional: true, reloadOnChange: true);
                })
                .ConfigureLogging(logging => logging.ClearProviders())
                .ConfigureServices((context, services) =>
                {
                    // EventPicker DB
                    var connStr = context.Configuration.GetConnectionString("Postgres");
                    services.AddDbContextFactory<PgDbContext>(options => options.UseNpgsql(connStr));

                    // Separate Info/Favorites DB (srd_db)
                    var infoConnStr = context.Configuration.GetConnectionString("PostgresInfo");
                    services.AddDbContextFactory<PgInfoDbContext>(options => options.UseNpgsql(infoConnStr));

                    services.AddSingleton<IAppRuntimeContext, AppRuntimeContext>();

                    services.AddSingleton<IDbService, PgDbService>();
                    services.AddSingleton<IEquipInfoService, EquipInfoService>();
                    services.AddSingleton<IMessageService, MessageService>();

                    services.AddSingleton<CtApiService>();
                    services.AddSingleton<ICtApiService>(sp => sp.GetRequiredService<CtApiService>());
                    services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<CtApiService>());
                    services.AddSingleton<IEquipmentService, EquipmentService>();

                    services.AddSingleton<IUserStateService, JsonUserStateService>();

                    services.AddSingleton<IQrCodeService, QrCodeService>();
                    services.AddSingleton<IQrScannerService, QrScannerService>();

                    services.AddSingleton<MainViewModel>();
                    services.AddTransient<MainWindow>();
                })
                .Build();
        }

        protected override async void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // Проверяем, не запущен ли уже другой экземпляр приложения
            _singleInstanceMutex = new Mutex(initiallyOwned: true, name: SingleInstanceMutexName, createdNew: out bool createdNew);

            if (!createdNew)
            {
                _singleInstanceMutex.Dispose();
                _singleInstanceMutex = null;

                DXMessageBox.Show(
                    "TechEquipments is already running.",
                    "Application already started",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);

                Shutdown();
                return;
            }

            try
            {
                await AppHost.StartAsync();
                AppHost.Services.GetRequiredService<MainWindow>().Show();
            }
            catch (Exception ex)
            {
                ReleaseSingleInstanceMutex();

                DXMessageBox.Show(
                    $"Application startup failed.\n\n{ex.Message}",
                    "Startup error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);

                Shutdown();
            }
        }

        protected override async void OnExit(ExitEventArgs e)
        {
            try
            {
                await AppHost.StopAsync();
                AppHost.Dispose();
            }
            finally
            {
                ReleaseSingleInstanceMutex();
                base.OnExit(e);
            }
        }

        /// <summary>
        /// Безопасно освобождаем mutex при выходе или ошибке старта.
        /// </summary>
        private void ReleaseSingleInstanceMutex()
        {
            if (_singleInstanceMutex == null)
                return;

            try
            {
                _singleInstanceMutex.ReleaseMutex();
            }
            catch
            {
                // Игнорируем, если mutex уже не принадлежит текущему экземпляру
            }

            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
        }
    }
}