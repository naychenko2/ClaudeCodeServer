using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using ClaudeCodeServer.Auth;
using ClaudeCodeServer.Hubs;
using ClaudeCodeServer.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.HttpOverrides;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers()
    .AddJsonOptions(o =>
        o.JsonSerializerOptions.Converters.Add(
            new JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase)));

builder.Services.AddSignalR()
    .AddJsonProtocol(o =>
        o.PayloadSerializerOptions.Converters.Add(
            new JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase)));

builder.Services.AddSingleton<ProjectManager>();
builder.Services.AddSingleton<FileService>();
builder.Services.AddSingleton<SyncService>();
builder.Services.AddSingleton<FileWatcherService>();
builder.Services.AddSingleton<ChatHistoryService>();
builder.Services.AddSingleton<SessionManager>();
builder.Services.AddSingleton<ApiKeyAuthService>();

// Аутентификация по единственному API-ключу (Bearer / X-Api-Key / ?access_token=)
builder.Services.AddAuthentication(ApiKeyAuthService.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(
        ApiKeyAuthService.SchemeName, _ => { });
builder.Services.AddAuthorization();

// За reverse-proxy (Caddy/туннель) берём реальный IP клиента из X-Forwarded-For,
// иначе rate-limit считал бы все запросы с адреса прокси как один
builder.Services.Configure<ForwardedHeadersOptions>(o =>
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto);

// Защита /api/auth/ping от перебора ключа — фиксированное окно на IP.
// Лимит читаем в момент запроса (через DI), чтобы видеть конфигурацию,
// добавленную после старта builder (в т.ч. тестовую).
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("auth-ping", ctx =>
    {
        var limit = ctx.RequestServices.GetRequiredService<IConfiguration>()
            .GetValue("Auth:PingRateLimit", 10);
        return RateLimitPartition.GetFixedWindowLimiter(
            ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = limit,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            });
    });
});

builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.SetIsOriginAllowed(_ => true)
     .AllowAnyHeader()
     .AllowAnyMethod()
     .AllowCredentials()));

var app = builder.Build();

// Прогрев сервиса ключа на старте — печатает сгенерированный ключ в консоль
app.Services.GetRequiredService<ApiKeyAuthService>();

app.UseForwardedHeaders();
// Принудительный HTTPS только для публичного домена naychenko.me;
// доступ из локальной сети по IP остаётся по HTTP (сертификат на IP не выдан)
if (!app.Environment.IsDevelopment())
    app.Use(async (ctx, next) =>
    {
        if (!ctx.Request.IsHttps &&
            ctx.Request.Host.Host.EndsWith("naychenko.me", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Response.Redirect(
                $"https://{ctx.Request.Host.Host}{ctx.Request.PathBase}{ctx.Request.Path}{ctx.Request.QueryString}",
                permanent: false);
            return;
        }
        await next();
    });
app.UseRouting();
app.UseCors();
// UseRateLimiter — после UseRouting, иначе эндпоинт-политика [EnableRateLimiting] не видна
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

// Раздача фронтенда из frontend/dist/ (production / PWA)
var distPath = Path.GetFullPath(Path.Combine(
    Directory.GetCurrentDirectory(), "..", "..", "frontend", "dist"));
if (Directory.Exists(distPath))
{
    var fp = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(distPath);
    app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = fp });
    app.UseStaticFiles(new StaticFileOptions { FileProvider = fp });
    app.MapFallbackToFile("index.html", new StaticFileOptions { FileProvider = fp });
}

app.MapControllers();
app.MapHub<SessionHub>("/hubs/session");

app.Run();

public partial class Program { }
