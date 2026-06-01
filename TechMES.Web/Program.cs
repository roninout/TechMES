using Radzen;
using Microsoft.Net.Http.Headers;
using TechMES.Web.Clients;
using TechMES.Web.Components;
using TechMES.Web.Settings;
using TechMES.Web.State;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddDebug();

// Blazor Web App + Interactive Server.
// Компоненты выполняются на сервере, а браузер общается с ними через SignalR-соединение.
builder.Services
    .AddRazorComponents()
    .AddInteractiveServerComponents()
    .AddHubOptions(options =>
    {
        options.MaximumReceiveMessageSize = 1024 * 1024;
    });

builder.Services.AddAntiforgery();

// Сервисы Radzen: нужны для Dialog, Notification, ContextMenu и других UI-компонентов.
builder.Services.AddRadzenComponents();

// Настройки адреса Runtime Service берём из appsettings.json.
builder.Services.Configure<RuntimeServiceOptions>(builder.Configuration.GetSection("RuntimeService"));
builder.Services.Configure<ParamUiOptions>(builder.Configuration.GetSection("Param"));

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

// Клиент Info-модуля: карточка оборудования, описание и notes.
builder.Services.AddScoped<InfoApiClient>();

// Клиент Param-модуля. WEB вызывает Runtime.Service, а детали CtApi остаются за границей backend-а.
builder.Services.AddScoped<ParamApiClient>();
builder.Services.AddScoped<EventLogApiClient>();
builder.Services.AddScoped<SoeApiClient>();

// Клиент для отображения состояния Runtime.Service в верхней панели WEB.
builder.Services.AddScoped<RuntimeStatusApiClient>();

// Состояние выбранного оборудования для текущего WEB-клиента.
// Это scoped-сервис: у каждой вкладки/сессии Blazor Server будет свой выбор.
builder.Services.AddScoped<SelectedEquipmentState>();
builder.Services.AddScoped<EquipmentFooterState>();
builder.Services.AddScoped<QrScannerState>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseAntiforgery();
app.MapStaticAssets();

app.MapGet("/api/runtime/info/files/{kind}/{id:long}", ProxyRuntimeInfoFileAsync);

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

/// <summary>
/// Проксирует файлы Info-модуля через WEB-приложение.
/// Браузер получает изображения/PDF с того же origin, поэтому планшетам не нужен прямой доступ к Runtime.Service.
/// </summary>
static async Task<IResult> ProxyRuntimeInfoFileAsync(
    string kind,
    long id,
    IHttpClientFactory httpClientFactory,
    HttpContext httpContext,
    CancellationToken ct)
{
    if (id <= 0)
        return Results.BadRequest();

    var client = httpClientFactory.CreateClient("RuntimeService");
    var runtimePath = $"api/info/files/{Uri.EscapeDataString(kind)}/{id}";

    using var request = new HttpRequestMessage(HttpMethod.Get, runtimePath);
    ForwardConditionalCacheHeaders(httpContext, request);

    using var response = await client.SendAsync(
        request,
        HttpCompletionOption.ResponseHeadersRead,
        ct);

    if (response.StatusCode == System.Net.HttpStatusCode.NotModified)
    {
        CopyRuntimeFileHeaders(httpContext, response);
        return Results.StatusCode(StatusCodes.Status304NotModified);
    }

    if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        return Results.NotFound();

    if (!response.IsSuccessStatusCode)
        return Results.StatusCode((int)response.StatusCode);

    CopyRuntimeFileHeaders(httpContext, response);

    var contentType = response.Content.Headers.ContentType?.ToString()
        ?? "application/octet-stream";
    var fileBytes = await response.Content.ReadAsByteArrayAsync(ct);
    var entityTag = TryReadEntityTag(response);
    var lastModified = response.Content.Headers.LastModified;

    return Results.File(
        fileBytes,
        contentType,
        enableRangeProcessing: true,
        lastModified: lastModified,
        entityTag: entityTag);
}

/// <summary>
/// Передает Runtime.Service условные cache-заголовки браузера.
/// Это позволяет backend-у вернуть 304 и не пересылать фото/PDF повторно.
/// </summary>
static void ForwardConditionalCacheHeaders(
    HttpContext httpContext,
    HttpRequestMessage request)
{
    if (httpContext.Request.Headers.TryGetValue(HeaderNames.IfNoneMatch, out var ifNoneMatch))
    {
        request.Headers.TryAddWithoutValidation(
            HeaderNames.IfNoneMatch,
            ifNoneMatch.ToArray());
    }

    if (httpContext.Request.Headers.TryGetValue(HeaderNames.IfModifiedSince, out var ifModifiedSince))
    {
        request.Headers.TryAddWithoutValidation(
            HeaderNames.IfModifiedSince,
            ifModifiedSince.ToArray());
    }
}

/// <summary>
/// Копирует важные file-заголовки Runtime.Service в ответ WEB-прокси.
/// </summary>
static void CopyRuntimeFileHeaders(
    HttpContext httpContext,
    HttpResponseMessage response)
{
    if (TryGetHeader(response, HeaderNames.CacheControl, out var cacheControl))
        httpContext.Response.Headers.CacheControl = cacheControl;

    if (TryGetHeader(response, HeaderNames.ContentDisposition, out var contentDisposition))
        httpContext.Response.Headers.ContentDisposition = contentDisposition;
}

/// <summary>
/// Безопасно читает ETag из ответа Runtime.Service.
/// </summary>
static EntityTagHeaderValue? TryReadEntityTag(HttpResponseMessage response)
{
    if (!TryGetHeader(response, HeaderNames.ETag, out var etag))
        return null;

    return EntityTagHeaderValue.TryParse(etag, out var parsed)
        ? parsed
        : null;
}

/// <summary>
/// Ищет HTTP-заголовок как в заголовках ответа, так и в заголовках content-а.
/// </summary>
static bool TryGetHeader(
    HttpResponseMessage response,
    string headerName,
    out string value)
{
    if (response.Headers.TryGetValues(headerName, out var responseValues)
        || response.Content.Headers.TryGetValues(headerName, out responseValues))
    {
        value = string.Join(", ", responseValues);
        return !string.IsNullOrWhiteSpace(value);
    }

    value = string.Empty;
    return false;
}
