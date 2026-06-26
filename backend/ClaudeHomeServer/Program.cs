using System.IdentityModel.Tokens.Jwt;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using ClaudeHomeServer.Hubs;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.StaticFiles;

JwtSecurityTokenHandler.DefaultMapInboundClaims = false;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers()
    .AddJsonOptions(o =>
        o.JsonSerializerOptions.Converters.Add(
            new JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase)));

builder.Services.AddSignalR()
    .AddJsonProtocol(o =>
        o.PayloadSerializerOptions.Converters.Add(
            new JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase)));

builder.Services.AddSingleton<UserStore>();
builder.Services.AddSingleton<JwtService>();
builder.Services.AddSingleton<AppSettingsService>();
builder.Services.AddSingleton<ProjectManager>();
builder.Services.AddSingleton<FileService>();
builder.Services.AddSingleton<SyncService>();
builder.Services.AddSingleton<SkillsService>();
builder.Services.AddSingleton<FileWatcherService>();
builder.Services.AddSingleton<ChatHistoryService>();
builder.Services.AddSingleton<WorkspaceKnowledgeStore>();
builder.Services.AddSingleton<SessionManager>();
builder.Services.AddHttpClient("proxy");
builder.Services.AddHttpClient("dify");
builder.Services.Configure<DifyOptions>(builder.Configuration.GetSection(DifyOptions.Section));
builder.Services.AddSingleton<KnowledgeService>();

// JWT для REST/SignalR; Negotiate (NTLM/Kerberos) для WebDAV (Microsoft Office)
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer()
    .AddNegotiate();
builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<JwtService>((opts, jwt) =>
    {
        opts.TokenValidationParameters = jwt.ValidationParameters;
        opts.Events = new JwtBearerEvents
        {
            OnMessageReceived = ctx =>
            {
                var token = ctx.Request.Query["access_token"].ToString();
                if (!string.IsNullOrWhiteSpace(token)) ctx.Token = token;
                return Task.CompletedTask;
            }
        };
    });
builder.Services.AddAuthorization();

// За reverse-proxy (Caddy/туннель) берём реальный IP клиента из X-Forwarded-For,
// иначе rate-limit считал бы все запросы с адреса прокси как один
builder.Services.Configure<ForwardedHeadersOptions>(o =>
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto);

// Защита /api/auth/login от перебора паролей — фиксированное окно на IP.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("auth-login", ctx =>
    {
        var limit = ctx.RequestServices.GetRequiredService<IConfiguration>()
            .GetValue("Auth:LoginRateLimit", 10);
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

// Прогрев сервисов на старте — UserStore печатает предупреждение если создал admin/admin
app.Services.GetRequiredService<UserStore>();
app.Services.GetRequiredService<JwtService>();

// Однократная миграция: переносим DifyDatasetId/DocumentTags из старых Project-записей в WorkspaceKnowledge
app.Services.GetRequiredService<WorkspaceKnowledgeStore>()
    .MigrateFromProjects(app.Services.GetRequiredService<ProjectManager>().GetAll());

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

// WebDAV — middleware перехватывает /projects/* до роутинга.
// Собственный Basic Auth внутри хендлера, вне JWT pipeline.
// Также отвечает на OPTIONS / (Windows WebClient зондирует корень перед монтированием).
app.Use(async (ctx, next) =>
{
    var path = ctx.Request.Path.Value ?? "";
    if (ctx.Request.Method.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase) && path == "/")
    {
        ctx.Response.StatusCode    = 200;
        ctx.Response.ContentLength = 0;
        ctx.Response.Headers["DAV"]           = "1, 2";
        ctx.Response.Headers["MS-Author-Via"] = "DAV";
        ctx.Response.Headers["Allow"]         = "OPTIONS, GET, HEAD, PUT, DELETE, PROPFIND, PROPPATCH, MKCOL, COPY, MOVE, LOCK, UNLOCK";
        return;
    }
    if (path == "/projects" || path.StartsWith("/projects/", StringComparison.OrdinalIgnoreCase))
    {
        await ClaudeHomeServer.WebDav.WebDavHandler.HandleAsync(ctx);
        return;
    }
    await next(ctx);
});

// Раздача фронтенда: wwwroot/ рядом с exe (prod) или ../../frontend/dist (dev)
var wwwrootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot");
var devDistPath = Path.GetFullPath(Path.Combine(
    Directory.GetCurrentDirectory(), "..", "..", "frontend", "dist"));
var distPath = Directory.Exists(wwwrootPath) ? wwwrootPath : devDistPath;
if (Directory.Exists(distPath))
{
    var fp = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(distPath);

    // index.html и SW-файлы — no-store: браузер всегда берёт свежую версию с сервера.
    // /assets/** — immutable: хэши в именах гарантируют уникальность, кэшируем «вечно».
    Action<StaticFileResponseContext> setCacheHeaders = ctx =>
    {
        var name = ctx.File.Name;
        var headers = ctx.Context.Response.Headers;
        if (name.Equals("index.html", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("sw.js", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("registerSW.js", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".webmanifest", StringComparison.OrdinalIgnoreCase))
        {
            headers.CacheControl = "no-store, no-cache, must-revalidate";
            headers.Pragma = "no-cache";
            headers.Expires = "0";
        }
        else if (ctx.Context.Request.Path.StartsWithSegments("/assets"))
        {
            headers.CacheControl = "public, max-age=31536000, immutable";
        }
    };

    app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = fp });
    app.UseStaticFiles(new StaticFileOptions { FileProvider = fp, OnPrepareResponse = setCacheHeaders });
    // /_api/* — Office/SharePoint-запросы; возвращаем 404 вместо SPA, иначе Word показывает «Нет доступа»
    app.Map("/_api", api => api.Run(ctx => { ctx.Response.StatusCode = 404; return Task.CompletedTask; }));
    app.MapFallbackToFile("index.html", new StaticFileOptions { FileProvider = fp, OnPrepareResponse = setCacheHeaders });
}

app.MapControllers();
app.MapHub<SessionHub>("/hubs/session");

app.Run();

public partial class Program { }
