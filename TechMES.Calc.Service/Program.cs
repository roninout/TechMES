using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TechMES.Calc.Service;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "TechMES.Calc.Service";
});

builder.Services.AddHostedService<CalcWorker>();

await builder.Build().RunAsync();