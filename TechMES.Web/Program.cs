using Radzen;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Net.Http.Headers;
using TechMES.Web.Clients;
using TechMES.Web.Components;
using TechMES.Web.Logging;
using TechMES.Web.Security;
using TechMES.Web.Settings;
using TechMES.Web.State;

var builder = WebApplication.CreateBuilder(args);
var windowsAuthenticationEnabled = builder.Configuration.GetValue("WindowsAuthentication:Enabled", false);

// В published-режиме WEB запускается как Windows Service и обслуживается Kestrel.
builder.Host.UseWindowsService(options =>
{
    options.ServiceName = "TechMES.Web";
});

builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddDebug();
builder.Logging.AddSimpleFile(builder.Configuration);

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
builder.Services.AddCascadingAuthenticationState();

// Windows Authentication включается настройкой WindowsAuthentication:Enabled.
// В выключенном режиме WEB продолжает работать как раньше, а Param write использует service-account fallback.
if (windowsAuthenticationEnabled)
{
    builder.Services
        .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
        .AddCookie(options =>
        {
            options.Cookie.Name = "TechMES.WebAuth";
            options.LoginPath = "/";
            options.AccessDeniedPath = "/";
            options.SlidingExpiration = true;
            options.ExpireTimeSpan = TimeSpan.FromHours(12);
        });

    builder.Services.AddAuthorization();
}

// Сервисы Radzen: нужны для Dialog, Notification, ContextMenu и других UI-компонентов.
builder.Services.AddRadzenComponents();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<OptionalWindowsAuthenticationStateProvider>();
builder.Services.AddScoped<AuthenticationStateProvider>(sp =>
    sp.GetRequiredService<OptionalWindowsAuthenticationStateProvider>());
builder.Services.AddScoped<IHostEnvironmentAuthenticationStateProvider>(sp =>
    sp.GetRequiredService<OptionalWindowsAuthenticationStateProvider>());
builder.Services.AddSingleton<WindowsCredentialValidator>();

// Настройки адреса Runtime Service берём из appsettings.json.
builder.Services.Configure<RuntimeServiceOptions>(builder.Configuration.GetSection("RuntimeService"));
builder.Services.Configure<ParamUiOptions>(builder.Configuration.GetSection("Param"));
builder.Services.Configure<MessagesUiOptions>(builder.Configuration.GetSection("Messages"));

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

// HTTPS можно подготовить из TechMES.Maintenance, но редирект оставляем управляемым настройкой.
// Так HTTP остается рабочим fallback-каналом, пока CER-файл не установлен как доверенный на планшетах.
if (app.Configuration.GetValue("HttpsRedirection:Enabled", false))
    app.UseHttpsRedirection();
if (windowsAuthenticationEnabled)
{
    app.UseAuthentication();
    app.UseAuthorization();
}
app.UseAntiforgery();
app.MapStaticAssets();

app.MapGet("/api/health", GetWebHealth);
app.MapGet("/api/auth/me", GetCurrentAuthState);
app.MapGet("/auth/login", (string? returnUrl = null) => Results.Redirect(NormalizeLocalReturnUrl(returnUrl)));
app.MapPost("/auth/login", LoginAsync);
app.MapGet("/auth/logout", Logout);
app.MapGet("/api/server/public-certificate", GetPublicCertificate);
app.MapGet("/api/runtime/info/files/{kind}/{id:long}", ProxyRuntimeInfoFileAsync);

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

/// <summary>
/// Возвращает эффективное состояние входа для Header-кнопки.
/// Read-only cookie специально скрывает Windows identity от UI, чтобы оператор мог смотреть сайт без режима записи.
/// </summary>
static IResult GetCurrentAuthState(HttpContext httpContext)
{
    var readOnlyMode = OptionalWindowsAuthenticationStateProvider.IsReadOnlyRequest(httpContext);
    var isAuthenticated = !readOnlyMode && httpContext.User.Identity?.IsAuthenticated == true;

    return Results.Ok(new
    {
        IsAuthenticated = isAuthenticated,
        UserName = isAuthenticated ? httpContext.User.Identity?.Name : null,
        AuthenticationType = isAuthenticated ? httpContext.User.Identity?.AuthenticationType : null,
        ReadOnly = !isAuthenticated
    });
}

/// <summary>
/// Проверяет введенные в WEB-форму Windows credentials и создает cookie-сессию.
/// Это работает предсказуемее, чем browser Negotiate prompt, особенно при доступе по IP с планшета.
/// </summary>
static async Task<IResult> LoginAsync(
    HttpContext httpContext,
    IConfiguration configuration,
    WindowsCredentialValidator credentialValidator)
{
    var form = await httpContext.Request.ReadFormAsync();
    var returnUrl = form["returnUrl"].ToString();
    var localReturnUrl = NormalizeLocalReturnUrl(returnUrl);

    if (!configuration.GetValue("WindowsAuthentication:Enabled", false))
        return Results.Redirect(localReturnUrl);

    var validation = credentialValidator.Validate(
        form["username"].ToString(),
        form["password"].ToString());

    if (!validation.Success || validation.Principal is null)
        return BuildLoginFailurePage(localReturnUrl, validation.Error ?? "Windows login failed.");

    await httpContext.SignInAsync(
        CookieAuthenticationDefaults.AuthenticationScheme,
        validation.Principal,
        new AuthenticationProperties
        {
            IsPersistent = true,
            AllowRefresh = true,
            ExpiresUtc = DateTimeOffset.UtcNow.AddHours(12)
        });

    httpContext.Response.Cookies.Delete(OptionalWindowsAuthenticationStateProvider.ReadOnlyCookieName);

    return Results.Redirect(localReturnUrl);
}

/// <summary>
/// Переводит текущий браузер в read-only режим.
/// WEB удаляет auth cookie и запоминает read-only выбор отдельной cookie.
/// </summary>
static async Task<IResult> Logout(HttpContext httpContext, string? returnUrl = null)
{
    await httpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);

    httpContext.Response.Cookies.Append(
        OptionalWindowsAuthenticationStateProvider.ReadOnlyCookieName,
        "1",
        new CookieOptions
        {
            HttpOnly = true,
            SameSite = Microsoft.AspNetCore.Http.SameSiteMode.Lax,
            Secure = httpContext.Request.IsHttps,
            Expires = DateTimeOffset.UtcNow.AddDays(30)
        });

    return Results.Redirect(NormalizeLocalReturnUrl(returnUrl));
}

/// <summary>
/// Показывает простую страницу ошибки, если Windows не принял логин/пароль.
/// </summary>
static IResult BuildLoginFailurePage(string returnUrl, string error)
{
    var encodedReturnUrl = System.Net.WebUtility.HtmlEncode(returnUrl);
    var encodedError = System.Net.WebUtility.HtmlEncode(error);

    return Results.Content(
        $$"""
        <!doctype html>
        <html lang="en">
        <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>TechMES login failed</title>
            <style>
                body { font-family: Segoe UI, Arial, sans-serif; margin: 32px; color: #242424; }
                .box { max-width: 560px; border: 1px solid #ddd; border-radius: 8px; padding: 20px; }
                .error { color: #c62828; font-weight: 700; }
                a { display: inline-block; margin-top: 16px; }
            </style>
        </head>
        <body>
            <div class="box">
                <h2>TechMES login failed</h2>
                <p class="error">{{encodedError}}</p>
                <p>Check the Windows user name and password. You can use <b>Web</b>, <b>.\Web</b> or <b>{{System.Net.WebUtility.HtmlEncode(Environment.MachineName)}}\Web</b>.</p>
                <a href="{{encodedReturnUrl}}">Back to TechMES</a>
            </div>
        </body>
        </html>
        """,
        "text/html; charset=utf-8");
}

/// <summary>
/// Защищает redirect после Login/Logout от внешних URL.
/// </summary>
static string NormalizeLocalReturnUrl(string? returnUrl)
{
    if (string.IsNullOrWhiteSpace(returnUrl))
        return "/";

    if (!returnUrl.StartsWith('/'))
        return "/";

    if (returnUrl.StartsWith("//", StringComparison.Ordinal)
        || returnUrl.StartsWith("/\\", StringComparison.Ordinal))
    {
        return "/";
    }

    return returnUrl;
}

/// <summary>
/// Легкий health endpoint самого WEB-приложения.
/// Maintenance использует его вместо корневой Blazor-страницы, чтобы проверка не зависела от prerender/SignalR.
/// </summary>
static IResult GetWebHealth()
{
    return Results.Ok(new
    {
        Service = "TechMES.Web",
        Status = "OK",
        Timestamp = DateTimeOffset.Now
    });
}

/// <summary>
/// Отдает публичный CER-файл HTTPS-сертификата.
/// Браузер может скачать этот файл, но установить его как доверенный сертификат может только пользователь,
/// администратор, Group Policy или MDM-профиль устройства.
/// </summary>
static IResult GetPublicCertificate(IConfiguration configuration)
{
    var certificatePath = configuration["Kestrel:Endpoints:Https:Certificate:PublicPath"];

    if (string.IsNullOrWhiteSpace(certificatePath))
    {
        var pfxPath = configuration["Kestrel:Endpoints:Https:Certificate:Path"];

        if (!string.IsNullOrWhiteSpace(pfxPath))
            certificatePath = Path.ChangeExtension(pfxPath, ".cer");
    }

    if (string.IsNullOrWhiteSpace(certificatePath))
        return Results.NotFound("HTTPS certificate path is not configured.");

    if (!File.Exists(certificatePath))
        return Results.NotFound("Public certificate file was not found.");

    return Results.File(
        certificatePath,
        "application/x-x509-ca-cert",
        Path.GetFileName(certificatePath));
}

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
