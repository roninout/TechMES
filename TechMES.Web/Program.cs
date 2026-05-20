using Radzen;
using TechMES.Web.Clients;
using TechMES.Web.Components;
using TechMES.Web.Settings;
using TechMES.Web.State;

var builder = WebApplication.CreateBuilder(args);

// Blazor Web App + Interactive Server.
// Компоненты выполняются на сервере, а браузер общается с ними через SignalR-соединение.
builder.Services
    .AddRazorComponents()
    .AddInteractiveServerComponents();

// Сервисы Radzen: нужны для Dialog, Notification, ContextMenu и других UI-компонентов.
builder.Services.AddRadzenComponents();

// Настройки адреса Runtime Service берём из appsettings.json.
builder.Services.Configure<RuntimeServiceOptions>(builder.Configuration.GetSection("RuntimeService"));

// Именованный HttpClient для Runtime Service.
// WEB не знает, где физически БД или CtApi — он вызывает Runtime Service по HTTP.
builder.Services.AddHttpClient("RuntimeService", (sp, client) =>
{
    var options = sp
        .GetRequiredService<Microsoft.Extensions.Options.IOptions<RuntimeServiceOptions>>()
        .Value;

    client.BaseAddress = new Uri(options.BaseUrl);
    client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
});

// Клиент, который инкапсулирует HTTP-запросы к /api/messages.
builder.Services.AddScoped<MessageApiClient>();

// Клиент для получения каталога оборудования из Runtime.Service.
builder.Services.AddScoped<EquipmentApiClient>();

// Клиент для отображения состояния Runtime.Service в верхней панели WEB.
builder.Services.AddScoped<RuntimeStatusApiClient>();

// Состояние выбранного оборудования для текущего WEB-клиента.
// Это scoped-сервис: у каждой вкладки/сессии Blazor Server будет свой выбор.
builder.Services.AddScoped<SelectedEquipmentState>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseAntiforgery();
app.MapStaticAssets();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
