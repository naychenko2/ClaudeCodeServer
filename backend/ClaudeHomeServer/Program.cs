using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using ClaudeHomeServer.Hubs;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.TriggerSources;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.StaticFiles;
using Yarp.ReverseProxy.Forwarder;

JwtSecurityTokenHandler.DefaultMapInboundClaims = false;

var builder = WebApplication.CreateBuilder(args);

// Локальные машинно-специфичные переопределения (пути, URL, секреты).
// Файл вне git (.gitignore), у каждого свой. Грузится последним — переопределяет
// appsettings.json и appsettings.{Environment}.json. Необязателен: нет файла — берутся
// дефолты из git (важно, чтобы у брата ничего не отъехало).
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);

// Токен подписки claude CLI (`claude setup-token`) можно держать в appsettings.Local.json —
// удобнее, чем переменная окружения: IDE наследует окружение от родителя (explorer/Toolbox),
// и свежий setx там не виден, пока не перезайдёшь в систему. Кладём его в env процесса, откуда
// его унаследуют все запуски claude.exe (ClaudeSession, OneShotClaudeRunner, ModelCatalogService).
// Явная переменная окружения имеет приоритет — конфиг её не перетирает (важно для docker).
const string OAuthTokenVar = "CLAUDE_CODE_OAUTH_TOKEN";
if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(OAuthTokenVar))
    && builder.Configuration["Claude:OAuthToken"] is { Length: > 0 } oauthToken
    && !string.IsNullOrWhiteSpace(oauthToken))
{
    Environment.SetEnvironmentVariable(OAuthTokenVar, oauthToken);
    // Значение — секрет, печатаем только факт (иначе токен утечёт в логи IDE/CI)
    Console.WriteLine($"[Claude] Токен подписки взят из конфига Claude:OAuthToken ({oauthToken.Length} симв.)");
}

builder.Services.AddControllers()
    .AddJsonOptions(o =>
        o.JsonSerializerOptions.Converters.Add(
            new JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase)));

builder.Services.AddSignalR(o =>
    {
        // Смягчаем разрывы у клиентов с дрожащим каналом (мобильные, засыпающие вкладки):
        // сервер закрывает соединение, если не слышал клиента дольше ClientTimeoutInterval.
        // Дефолт 30 с рвал соединение при коротком замолкании — поднимаем до 60 с
        // (должен быть ≥ 2× клиентского KeepAlive). KeepAlive 15 с — пинги сервера клиенту.
        o.ClientTimeoutInterval = TimeSpan.FromSeconds(60);
        o.KeepAliveInterval = TimeSpan.FromSeconds(15);
        // Медленное рукопожатие на плохом канале не должно ронять подключение
        o.HandshakeTimeout = TimeSpan.FromSeconds(30);
    })
    .AddJsonProtocol(o =>
        o.PayloadSerializerOptions.Converters.Add(
            new JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase)));

builder.Services.AddSingleton<UserStore>();
// Драйверы среды исполнения процессов пользователей (local / docker-песочница)
builder.Services.AddSingleton<ClaudeHomeServer.Services.Execution.SandboxManager>();
builder.Services.AddSingleton<ClaudeHomeServer.Services.Execution.ILauncherFactory,
    ClaudeHomeServer.Services.Execution.LauncherFactory>();
builder.Services.AddSingleton<JwtService>();
builder.Services.AddSingleton<FeatureFlagService>();
builder.Services.AddSingleton<AppSettingsService>();
builder.Services.AddSingleton<ProjectManager>();
builder.Services.AddSingleton<ProjectGroupManager>();
builder.Services.AddSingleton<ProjectEventLogService>();
builder.Services.AddSingleton<PersonaManager>();
builder.Services.AddSingleton<PersonaPromptBuilder>();
builder.Services.AddSingleton<PersonaMemoryService>();
builder.Services.AddSingleton<TeamMemoryService>();
builder.Services.AddSingleton<PersonaBindingsService>();
// Файловые сабагенты-персоны: генерация + синк .md-агентов
// Пул подписок с восстановлением пометок исчерпания из снапшотов usage после рестарта
builder.Services.AddSingleton(sp => new ClaudeSubscriptionPool(
    sp.GetRequiredService<IConfiguration>(), sp.GetRequiredService<UsageService>()));
// Стартовый прогрев утилизации подписок (один пробный ход на аккаунт) — при HasExtra и флаге
builder.Services.AddHostedService<SubscriptionUsageWarmupService>();
builder.Services.AddSingleton<PersonaAgentFileGenerator>();
builder.Services.AddSingleton<PersonaAgentFileSync>();
builder.Services.AddSingleton<FalImageService>();
// Консолидация памяти — singleton + hosted: autolearn ставит заявки через RequestConsolidation
builder.Services.AddSingleton<PersonaMemoryConsolidationService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<PersonaMemoryConsolidationService>());
// Autolearn — singleton + hosted: PersonaAskService пишет память после консультаций напрямую
builder.Services.AddSingleton<PersonaMemoryAutolearnService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<PersonaMemoryAutolearnService>());
// Консолидация памяти команды проекта — singleton + hosted: team-autolearn ставит заявки RequestConsolidation
builder.Services.AddSingleton<TeamMemoryConsolidationService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<TeamMemoryConsolidationService>());
builder.Services.AddHostedService<TeamMemoryAutolearnService>();
// Разовый backfill дефолтных привязок существующим проектным персонам (файлы/заметки/знания)
builder.Services.AddHostedService<PersonaProjectBindingsMigration>();
builder.Services.AddSingleton<TaskManager>();
builder.Services.AddSingleton<TaskAiService>();
builder.Services.AddSingleton<FileService>();
builder.Services.AddSingleton<NotesService>();
builder.Services.AddSingleton<NotesKnowledgeService>();
builder.Services.AddSingleton<NotesAiService>();
builder.Services.AddSingleton<NoteTaskSyncService>();
builder.Services.AddSingleton<UnifiedSearchService>();
builder.Services.AddSingleton<ClaudeHomeServer.Services.Llm.OneShotClaudeRunner>();
// Интерфейс one-shot раннера → тот же singleton (мокируется в тестах)
builder.Services.AddSingleton<ClaudeHomeServer.Services.Llm.IOneShotRunner>(
    sp => sp.GetRequiredService<ClaudeHomeServer.Services.Llm.OneShotClaudeRunner>());
// Общий LLM-резолвер записи памяти (Mem0 ADD/UPDATE/DELETE/NOOP) — авто-путь обоих слоёв памяти
builder.Services.AddSingleton<ClaudeHomeServer.Services.Memory.MemoryWriteResolver>();
// One-shot ответы персон от их лица (persona_ask из MCP персон)
builder.Services.AddSingleton<PersonaAskService>();
builder.Services.AddSingleton<ChangelogService>();
builder.Services.AddSingleton<SyncService>();
builder.Services.AddSingleton<SkillsService>();
builder.Services.AddSingleton<SkillsCliService>();
builder.Services.AddSingleton<SkillTranslationService>();
builder.Services.AddSingleton<PluginSkillLocalizer>();
builder.Services.AddSingleton<SkillSuggestService>();
builder.Services.AddSingleton<SkillGenerationService>();
builder.Services.AddSingleton<FileWatcherService>();
builder.Services.AddSingleton<ConnectionDiagnostics>();
builder.Services.AddSingleton<ChatHistoryService>();
builder.Services.AddSingleton<WorkspaceKnowledgeStore>();
builder.Services.AddSingleton<FalCostService>();
builder.Services.AddSingleton<FalAccountService>();
builder.Services.AddSingleton<UsageService>();
builder.Services.AddSingleton<ClaudeHomeServer.Services.Llm.LlmProviderRegistry>();
builder.Services.AddSingleton<ClaudeHomeServer.Services.Llm.ProviderBalanceService>();
builder.Services.AddSingleton<ClaudeHomeServer.Services.Llm.ILlmSessionAdapterFactory,
    ClaudeHomeServer.Services.Llm.LlmSessionAdapterFactory>();
builder.Services.AddSingleton<BoardService>();
builder.Services.AddSingleton<SessionManager>();
builder.Services.AddSingleton<ModelCatalogService>();
builder.Services.AddSingleton<NotificationStore>();
builder.Services.AddSingleton<NotificationService>();
builder.Services.AddSingleton<PushSubscriptionStore>();
builder.Services.AddSingleton<PushService>();
builder.Services.AddSingleton<TaskExecutionService>();
builder.Services.AddSingleton<SessionSummaryService>();
builder.Services.AddSingleton<ChatTaskExtractionService>();
builder.Services.AddSingleton<DailyBriefingService>();
// Проактивность персон (событийно-управляемый rules-движок): state store, источники и сервис-collaborator
builder.Services.AddSingleton<AutomationStateStore>();
builder.Services.AddSingleton<AutomationRootResolver>();
builder.Services.AddSingleton<MentionTriggerSource>();
builder.Services.AddSingleton<ITriggerSource, TimerTriggerSource>();
builder.Services.AddSingleton<ITriggerSource, FileTriggerSource>();
builder.Services.AddSingleton<ITriggerSource, NoteTriggerSource>();
builder.Services.AddSingleton<ITriggerSource, GitCommitTriggerSource>();
builder.Services.AddSingleton<ITriggerSource, TaskStatusTriggerSource>();
builder.Services.AddSingleton<PersonaAutomationService>();
builder.Services.AddHostedService<TaskSchedulerService>();
builder.Services.AddHostedService<ChatExpiryService>();
builder.Services.AddHostedService<ChatTurnLoggerService>();
builder.Services.AddHostedService<NoteExpiryService>();
// Терминал (PTY) и Preview (dev-server) — под гейтом workspace-destructive
builder.Services.AddSingleton<TerminalService>();
builder.Services.AddSingleton<DevServerService>();
builder.Services.AddSingleton<LaunchConfigService>();
builder.Services.AddSingleton<ProjectServiceDiscovery>();
builder.Services.AddHttpClient("proxy");
builder.Services.AddHttpClient("dify");
builder.Services.AddHttpClient("fal");
builder.Services.AddHttpClient("llm-provider");
builder.Services.AddHttpForwarder();
builder.Services.AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));
builder.Services.Configure<DifyOptions>(builder.Configuration.GetSection(DifyOptions.Section));
builder.Services.AddSingleton<KnowledgeService>();
// Синк «файл проекта ↔ документ БЗ»: singleton + hosted-мост событий хода Claude
// (мост заодно гарантирует инстанцирование синка — подписку на FileService.OnMutated)
builder.Services.AddSingleton<ProjectKnowledgeSyncService>();
builder.Services.AddHostedService<ProjectKnowledgeTurnSync>();
// Каскадная уборка знаний при удалении пользователя (UsersController)
builder.Services.AddSingleton<UserKnowledgeCascade>();

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

// CORS: только белый список origin'ов из конфига (Cors:AllowedOrigins).
// Фронт раздаётся same-origin из wwwroot, поэтому пустой список ничего не ломает.
var corsOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.WithOrigins(corsOrigins)
     .AllowAnyHeader()
     .AllowAnyMethod()
     .AllowCredentials()));

var app = builder.Build();

// Логгер статического парсера workflow-транскриптов (DI туда не дотягивается)
WorkflowAgentParser.Log = app.Services.GetRequiredService<ILoggerFactory>()
    .CreateLogger(nameof(WorkflowAgentParser));
// Логгер резолвера meta-блоков workflow (обогащение input вызова по имени)
ClaudeHomeServer.Services.WorkflowMetaResolver.Log = app.Services.GetRequiredService<ILoggerFactory>()
    .CreateLogger(nameof(ClaudeHomeServer.Services.WorkflowMetaResolver));
// Дополнительные разрешённые корни транскриптов — пути проектов сторонних CLI-провайдеров
// (GLM/DeepSeek используют изолированные профили, транскрипты пишутся не в ~/.claude)
try
{
    var registry = app.Services.GetRequiredService<ClaudeHomeServer.Services.Llm.LlmProviderRegistry>();
    foreach (var dir in registry.GetProviderProjectsDirs())
    {
        WorkflowAgentParser.AddAllowedRoot(dir);
        Console.WriteLine($"[WorkflowAgentParser] разрешён корень провайдера: {dir}");
    }
    // Профили подписок (sub-*) и созданные после старта: разрешаем весь корень
    // claude-profiles по шаблону {key}/projects — иначе WorkflowWatcher у таких
    // сессий молча выключается («Детали недоступны» в блоке Workflow)
    WorkflowAgentParser.ProfilesRoot = registry.ProfilesDir;
    Console.WriteLine($"[WorkflowAgentParser] разрешён корень профилей: {registry.ProfilesDir}");
}
catch (Exception ex)
{
    Console.Error.WriteLine($"[WorkflowAgentParser] не удалось зарегистрировать корни провайдеров: {ex.Message}");
}

// Прогрев сервисов на старте — UserStore печатает предупреждение если создал admin/admin
app.Services.GetRequiredService<UserStore>();
// Фоновый прогрев каталога моделей (опрос claude CLI ~5 с — не задерживаем старт)
_ = Task.Run(() => app.Services.GetRequiredService<ModelCatalogService>().GetModelsAsync());
app.Services.GetRequiredService<JwtService>();
// Синк файловых сабагентов-персон: подписки на события PersonaManager должны встать
// до первых запросов (иначе ранние правки персон не долетят до .md-файлов)
app.Services.GetRequiredService<PersonaAgentFileSync>();

// Однократная миграция @handle персон под контекстное правило: схлопывает лишние суффиксы
// (masha-2 → masha там, где контексты не пересекаются) и чистит старые .md-файлы сабагентов
// по прежнему handle. Маркер-файл — чтобы не гонять на каждом старте. Best-effort: сбой
// миграции не мешает старту.
try
{
    var dataDir = Path.GetDirectoryName(
        app.Configuration["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json"))
        ?? Path.Combine(AppContext.BaseDirectory, "data");
    var marker = Path.Combine(dataDir, "handle-migration-v1.done");
    if (!File.Exists(marker))
    {
        var personaManager = app.Services.GetRequiredService<PersonaManager>();
        var agentSync = app.Services.GetRequiredService<PersonaAgentFileSync>();
        var renamed = personaManager.MigrateContextualHandles();
        // Сначала удалить старые .md по прежнему handle (клон с oldHandle даёт старые пути)
        foreach (var (persona, oldHandle) in renamed)
            try { agentSync.RemovePersona(PersonaManager.WithHandle(persona, oldHandle)); } catch { /* не критично */ }
        // Затем перегенерировать файлы затронутых владельцев под новые handle
        foreach (var owner in renamed.Select(r => r.Persona.OwnerId).Distinct())
            try { agentSync.SyncOwner(owner, force: true); } catch { /* не критично */ }
        Directory.CreateDirectory(dataDir);
        File.WriteAllText(marker, $"{DateTime.UtcNow:O} renamed={renamed.Count}");
        if (renamed.Count > 0)
            Console.WriteLine($"[HandleMigration] контекстные @handle: переименовано персон — {renamed.Count}");
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine($"[HandleMigration] миграция пропущена: {ex.Message}");
}

// Чистка осиротевших temp-конфигов MCP: содержат сервисный токен и могли
// остаться после крэша (штатно удаляются в finally каждого хода)
_ = Task.Run(() =>
{
    try
    {
        foreach (var f in Directory.EnumerateFiles(Path.GetTempPath(), "claude-mcp-*.json"))
            try { if (File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddHours(-6)) File.Delete(f); } catch { }
    }
    catch { /* нет доступа к temp — не критично */ }
});

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

// OnlyOffice DS добавляет версионный префикс к URL ресурсов И Socket.IO WebSocket:
// /9.4.0-hash/web-apps/... и /9.4.0-hash/doc/.../c/?transport=websocket
// IHttpForwarder поддерживает WebSocket upgrade нативно — в отличие от HttpClient.
{
    var dsBase = builder.Configuration
        .GetSection("ReverseProxy:Clusters:onlyoffice:Destinations:default")
        .GetValue<string>("Address") ?? "http://localhost:8090";
    var ooInvoker = new HttpMessageInvoker(new SocketsHttpHandler
    {
        UseProxy = false,
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
        UseCookies = false,
    });

    // no-op SW: заменяем проблемный OO Service Worker нейтральным,
    // который очищает все кеши и не перехватывает запросы.
    // Иначе после первого визита SW кешируется в браузере и начинает
    // перехватывать Analytics.js с ошибкой net::ERR_FAILED.
    const string noOpSw =
        "self.addEventListener('install',e=>e.waitUntil(self.skipWaiting()));" +
        "self.addEventListener('activate',e=>e.waitUntil(" +
        "caches.keys().then(ks=>Promise.all(ks.map(k=>caches.delete(k)))).then(()=>self.clients.claim())" +
        "));";

    app.Use(async (ctx, next) =>
    {
        var path = ctx.Request.Path.Value ?? "";
        if (path.Length > 1 && char.IsDigit(path[1]))
        {
            // Service Worker OO заменяем no-op-ом — избегаем cacheFirst-ошибок в браузере
            if (path.EndsWith("/document_editor_service_worker.js", StringComparison.OrdinalIgnoreCase))
            {
                ctx.Response.ContentType = "application/javascript; charset=utf-8";
                ctx.Response.Headers["Service-Worker-Allowed"] = "/";
                ctx.Response.Headers["Cache-Control"] = "no-store";
                await ctx.Response.WriteAsync(noOpSw);
                return;
            }

            var forwarder = ctx.RequestServices.GetRequiredService<IHttpForwarder>();
            await forwarder.SendAsync(ctx, dsBase, ooInvoker, ForwarderRequestConfig.Empty, HttpTransformer.Default);
            return;
        }
        await next();
    });
}

// Dev-server preview proxy: /preview/{projectId}/{**path} → http://127.0.0.1:{port}
{
    var previewInvoker = new HttpMessageInvoker(new SocketsHttpHandler
    {
        UseProxy = false,
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
        UseCookies = false,
    });

    app.Use(async (ctx, next) =>
    {
        var path = ctx.Request.Path.Value ?? "";
        var match = System.Text.RegularExpressions.Regex.Match(path, @"^/preview/([^/]+)(/.*)?$");
        if (match.Success)
        {
            var projectId = match.Groups[1].Value;
            var restPath = match.Groups[2].Value ?? "/";

            var devServer = ctx.RequestServices.GetRequiredService<DevServerService>();
            // Порт активного для превью сервиса проекта; если ни один не запущен — 503.
            // Аутентификация: iframe внутри приложения, которое уже за [Authorize].
            var port = devServer.GetActivePreviewPort(projectId);
            if (port is null)
            {
                ctx.Response.StatusCode = 503;
                await ctx.Response.WriteAsync("{\"error\":\"Dev-сервер не запущен\"}");
                return;
            }

            var destUrl = $"http://127.0.0.1:{port}{restPath}{ctx.Request.QueryString}";
            var forwarder = ctx.RequestServices.GetRequiredService<IHttpForwarder>();
            await forwarder.SendAsync(ctx, destUrl, previewInvoker,
                ForwarderRequestConfig.Empty, HttpTransformer.Default);
            return;
        }
        await next();
    });
}

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

app.MapReverseProxy();
app.MapControllers();
app.MapHub<SessionHub>("/hubs/session");
app.MapHub<TerminalHub>("/hubs/terminal");

// Graceful shutdown: гасим все живые процессы claude, терминалы и dev-серверы
app.Lifetime.ApplicationStopping.Register(() =>
{
    app.Services.GetRequiredService<SessionManager>().KillAllProcesses();
    app.Services.GetRequiredService<TerminalService>().Dispose();
    app.Services.GetRequiredService<DevServerService>().ShutdownAll();
});

app.Run();

public partial class Program { }
