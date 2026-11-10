using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TechMES.PostgreSql.Control;

if (await PostgreSqlSwitchConsole.TryRunAsync(args))
    return;

if (!OperatingSystem.IsWindows())
    throw new PlatformNotSupportedException("Run the PostgreSQL host on Windows.");

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = args, ContentRootPath = AppContext.BaseDirectory });

builder.Configuration.AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.PostgreSqlControl.json"), optional: true, reloadOnChange: false);

builder.Services.AddWindowsService(options => options.ServiceName = "TechMES.PostgreSql.Agent");

builder.Logging.AddConsole();

builder.Services.AddPostgreSqlNode(builder.Configuration, PostgreSqlHostKind.Standalone);

await builder.Build().RunAsync();