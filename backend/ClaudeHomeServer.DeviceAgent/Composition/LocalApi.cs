using System.Net;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Execution;
using ClaudeHomeServer.Services.Files;
using ClaudeHomeServer.Services.Git;
using ClaudeHomeServer.Services.ProjectServices;
using ClaudeHomeServer.Services.Skills;
using ClaudeHomeServer.Services.Terminal;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Yarp.ReverseProxy.Forwarder;

namespace ClaudeHomeServer.DeviceAgent.Composition;

/// <summary>Настройки localhost-API: порт и единственный допустимый origin — веб-морда сервера.</summary>
internal sealed record LocalApiOptions(int Port, string ServerOrigin, string AgentVersion)
{
    public static string OriginOf(string serverUrl) => new Uri(serverUrl).GetLeftPart(UriPartial.Authority);

    /// <summary>
    /// Origin запроса — веб-морда сервера. Сравнение точное, кроме одного: loopback-имена
    /// <c>localhost</c>, <c>127.0.0.1</c> и <c>[::1]</c> при равных схеме и порте — один origin
    /// (сопряжение по localhost, а веб-морда открыта по 127.0.0.1). Других послаблений нет.
    /// </summary>
    public bool IsServerOrigin(string origin) => SameOrigin(origin, ServerOrigin);

    internal static bool SameOrigin(string origin, string serverOrigin)
    {
        if (string.Equals(origin, serverOrigin, StringComparison.OrdinalIgnoreCase)) return true;
        if (!TryParseOrigin(origin, out var a) || !TryParseOrigin(serverOrigin, out var b)) return false;
        return string.Equals(a.Scheme, b.Scheme, StringComparison.OrdinalIgnoreCase)
            && a.Port == b.Port
            && IsLoopbackName(a.Host) && IsLoopbackName(b.Host);
    }

    private static bool TryParseOrigin(string value, out Uri uri) =>
        Uri.TryCreate(value, UriKind.Absolute, out uri!)
        && uri.UserInfo.Length == 0 && uri.PathAndQuery == "/" && uri.Fragment.Length == 0
        && value.TrimEnd('/').Equals(uri.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase);

    private static bool IsLoopbackName(string host) =>
        host.ToLowerInvariant() is "localhost" or "127.0.0.1" or "[::1]";
}

/// <summary>
/// Рабочие подсистемы проекта на машине (задача 4.3): терминал, дев-серверы и превью, навыки,
/// вложения чата. <see cref="DataDirectory"/> — служебные данные агента (память портов
/// дев-серверов), <see cref="CliProfile"/> — профиль CLI агента: глобальные навыки те, что
/// видит ход, а не каталог хоста. <see cref="PreviewPort"/> — отдельный loopback-порт превью.
/// </summary>
internal sealed record AgentWorkbench(string DataDirectory, string CliProfile, int PreviewPort, AgentLauncherFactory Launchers);

/// <summary>
/// localhost-API агента (ADR-016 §5, задачи 4.2 и 4.3): браузер на машине проекта работает с
/// файлами, git, терминалом, дев-серверами и навыками напрямую. Маршруты и форма ответов — те
/// же, что у серверных контроллеров (контракт <see cref="DeviceAgentRoutes"/>), фронту
/// меняется только база адреса; под маршрутами — те же вертикали: Files через шов
/// <see cref="IProjectFiles"/>, <see cref="GitService"/>, <see cref="ProjectServicesApi"/>,
/// <see cref="TerminalService"/>, <see cref="SkillsService"/>.
///
/// Периметр, по порядку: слушаем только loopback; заголовок <c>Host</c> — только loopback с
/// нашим портом (DNS-rebinding); <c>Origin</c>, если есть, — только origin сервера (loopback-имена равны; CORS и
/// preflight Private Network Access); затем билет сервера, привязанный к проекту маршрута,
/// и корень проекта под разрешёнными корнями машины. Превью дев-серверов — на отдельном
/// loopback-порту со своим периметром (<see cref="PreviewGate"/>): страница дев-сайта не
/// должна стать одного origin с API.
/// </summary>
internal static class LocalApi
{
    public const string ProjectItem = "agent.project";
    public const string RootItem = "agent.root";
    public const string GrantItem = "agent.grant";

    public static WebApplication Build(
        LocalApiOptions options,
        AgentProjectFiles files,
        GitService git,
        AgentTicketCache tickets,
        AgentFileWatchers? watchers,
        ILoggerFactory loggers,
        Action<WebApplicationBuilder>? configure = null,
        AgentUrlTickets? urlTickets = null,
        AgentWorkbench? workbench = null)
    {
        urlTickets ??= new AgentUrlTickets();
        var directory = new AgentProjectDirectory();
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(loggers);
        builder.Services.AddRoutingCore();
        // Сериализация как у MVC сервера: перечисления строкой в camelCase
        builder.Services.ConfigureHttpJsonOptions(o =>
            o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.Listen(IPAddress.Loopback, options.Port);
            if (workbench is not null) k.Listen(IPAddress.Loopback, workbench.PreviewPort);
            // Потолок записи плюс запас на JSON-обёртку
            k.Limits.MaxRequestBodySize = new AgentLimits().MaxWriteBytes * 2 + 1024 * 1024;
        });
        if (workbench is not null) AddWorkbench(builder, workbench, directory);
        configure?.Invoke(builder);

        var app = builder.Build();
        var log = loggers.CreateLogger("ai-home-agent.local-api");

        if (workbench is not null)
        {
            // Порт превью — свой периметр и только проброс на дев-сервер: API там нет вовсе
            var gate = new PreviewGate(workbench.PreviewPort, options.ServerOrigin, urlTickets, files, directory);
            app.Use((ctx, next) => ctx.Connection.LocalPort == workbench.PreviewPort ? gate.HandleAsync(ctx) : next());
        }
        app.Use((ctx, next) => Perimeter(ctx, next, options));
        app.Use((ctx, next) => Authorize(ctx, next, files, tickets, urlTickets, watchers, directory));
        app.Use((ctx, next) => MapErrors(ctx, next, log));

        app.MapGet("/api/agent/health", () => Results.Ok(new { agent = "ai-home-agent", version = options.AgentVersion }));
        MapFiles(app.MapGroup("/api/projects/{projectId}/files"), files);
        MapStreamTicket(app.MapGroup("/api/projects/{projectId}"), files, urlTickets);
        MapGit(app.MapGroup("/api/projects/{projectId}/git"), files, git);
        if (workbench is not null)
        {
            var project = app.MapGroup("/api/projects/{projectId}");
            MapUrlTickets(project, urlTickets, workbench);
            MapProjectServices(project, files);
            MapSkills(project, files, workbench);
            MapAttachments(project, files);
            app.MapHub<AgentHub>(DeviceAgentApi.HubPath);
        }
        return app;
    }

    // Вертикали рабочих подсистем — теми же регистрациями, что на сервере; реализации швов
    // хоста (проекты, процессы, доставка событий) — агентские
    private static void AddWorkbench(WebApplicationBuilder builder, AgentWorkbench workbench, AgentProjectDirectory directory)
    {
        builder.Configuration["DataPath"] = Path.Combine(workbench.DataDirectory, "projects.json");
        var services = builder.Services;
        services.AddSingleton<IProjectManager>(directory);
        services.AddSingleton<ILauncherFactory>(workbench.Launchers);
        services.AddSingleton<ISandboxPortRange, NoSandboxPorts>();
        services.AddSingleton<AgentHubNotifier>();
        services.AddSingleton<ITerminalHubNotifier>(sp => sp.GetRequiredService<AgentHubNotifier>());
        services.AddSingleton<ISessionBroadcaster>(sp => sp.GetRequiredService<AgentHubNotifier>());
        services.AddSingleton<DevServerPortMemory>();
        services.AddSingleton<DevServerService>();
        services.AddSingleton<LaunchConfigService>();
        services.AddSingleton<ProjectServiceDiscovery>();
        services.AddSingleton<ProjectServicesApi>();
        services.AddSingleton<TerminalService>();
        services.AddSingleton<SkillsService>();
        services.AddHttpForwarder();
        services.AddSignalR().AddJsonProtocol(o =>
            o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));
    }

    // ---------- периметр ----------

    internal static bool IsAllowedHost(HostString host, int port) =>
        host.Port == port && host.Host is "127.0.0.1" or "localhost" or "[::1]";

    private static async Task Perimeter(HttpContext ctx, Func<Task> next, LocalApiOptions options)
    {
        var request = ctx.Request;
        var response = ctx.Response;
        if (!IsAllowedHost(request.Host, options.Port))
        {
            response.StatusCode = StatusCodes.Status421MisdirectedRequest;
            return;
        }

        var origin = request.Headers.Origin.ToString();
        if (origin.Length > 0)
        {
            if (!options.IsServerOrigin(origin))
            {
                response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
            response.Headers.AccessControlAllowOrigin = origin;
            response.Headers.Vary = "Origin";
        }

        if (HttpMethods.IsOptions(request.Method) && request.Headers.ContainsKey("Access-Control-Request-Method"))
        {
            if (origin.Length == 0)
            {
                response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
            response.Headers.AccessControlAllowMethods = "GET, POST, PUT, DELETE";
            // Authorization и X-SignalR-* — рукопожатие хаба (negotiate) клиентом SignalR
            response.Headers.AccessControlAllowHeaders =
                DeviceAgentApi.TicketHeader + ", Content-Type, Authorization, X-Requested-With, X-SignalR-User-Agent";
            response.Headers.AccessControlMaxAge = "600";
            // Private Network Access: публичная страница сервера стучится в loopback — браузер
            // требует явного согласия в ответе на preflight
            if (string.Equals(request.Headers["Access-Control-Request-Private-Network"], "true", StringComparison.OrdinalIgnoreCase))
                response.Headers["Access-Control-Allow-Private-Network"] = "true";
            response.StatusCode = StatusCodes.Status204NoContent;
            return;
        }

        await next();
    }

    private static async Task Authorize(HttpContext ctx, Func<Task> next, AgentProjectFiles files,
        AgentTicketCache tickets, AgentUrlTickets urlTickets, AgentFileWatchers? watchers, AgentProjectDirectory directory)
    {
        if (ctx.Request.Path.StartsWithSegments(DeviceAgentApi.HubPath))
        {
            await AuthorizeHub(ctx, next, files, urlTickets, directory);
            return;
        }

        var segments = ctx.Request.Path.Value?.Split('/', StringSplitOptions.RemoveEmptyEntries) ?? [];
        if (segments is not ["api", "projects", var projectId, ..])
        {
            await next();
            return;
        }

        // Основной билет — только в заголовке. <video src>/<img src> заголовок не ставят: для
        // отдачи потоком в URL едет отдельный узкий билет на один путь (DeviceAgentApi.StreamTicketQuery)
        var ticket = ctx.Request.Headers[DeviceAgentApi.TicketHeader].ToString();
        var grant = ticket.Length == 0 && HttpMethods.IsGet(ctx.Request.Method) && segments is [_, _, _, "files", "stream"]
            ? urlTickets.Validate(ctx.Request.Query[DeviceAgentApi.StreamTicketQuery],
                AgentUrlTickets.StreamScope(ctx.Request.Query["path"].ToString()))
            : await tickets.ValidateAsync(ticket, ctx.RequestAborted);
        if (grant is null)
        {
            await Error(ctx, StatusCodes.Status401Unauthorized, "Билет к агенту недействителен или истёк");
            return;
        }
        if (grant.ProjectId != Uri.UnescapeDataString(projectId))
        {
            await Error(ctx, StatusCodes.Status403Forbidden, "Билет выдан на другой проект");
            return;
        }

        if (await AdmitAsync(ctx, grant, files, directory) is not { } admitted) return;

        ctx.Items[ProjectItem] = admitted.Project;
        ctx.Items[RootItem] = admitted.Root;
        ctx.Items[GrantItem] = grant;
        watchers?.Touch(admitted.Project.Id, admitted.Root);
        await next();
    }

    /// <summary>
    /// Проект гранта, если его корень под корнями машины: для вертикалей рабочих подсистем он
    /// запоминается с РЕАЛЬНЫМ корнем — процессы терминала и дев-серверов стартуют в нём, а не
    /// в пути, который прислал сервер. null — отказ уже записан в ответ.
    /// </summary>
    private static async Task<(Project Project, string Root)?> AdmitAsync(HttpContext ctx, AgentTicketIntrospection grant,
        AgentProjectFiles files, AgentProjectDirectory directory)
    {
        var project = new Project { Id = grant.ProjectId, OwnerId = grant.OwnerId, RootPath = grant.RootPath };
        string root;
        try { (root, _) = files.Check(project, ""); }
        catch (AgentPathRefusedException e) { await Error(ctx, StatusCodes.Status403Forbidden, e.Message); return null; }
        catch (DirectoryNotFoundException e) { await Error(ctx, StatusCodes.Status404NotFound, e.Message); return null; }
        directory.Remember(new Project { Id = grant.ProjectId, OwnerId = grant.OwnerId, RootPath = root });
        return (project, root);
    }

    // Хаб: WebSocket из браузера заголовок не ставит, клиент SignalR кладёт билет в access_token
    // (подключение) или в Authorization (negotiate). Это узкий билет хаба, основной сюда не годится
    private static async Task AuthorizeHub(HttpContext ctx, Func<Task> next, AgentProjectFiles files,
        AgentUrlTickets urlTickets, AgentProjectDirectory directory)
    {
        var auth = ctx.Request.Headers.Authorization.ToString();
        var token = auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? auth["Bearer ".Length..].Trim()
            : ctx.Request.Query["access_token"].ToString();
        if (urlTickets.Validate(token, AgentUrlTickets.HubScope) is not { } grant)
        {
            await Error(ctx, StatusCodes.Status401Unauthorized, "Билет хаба недействителен или истёк");
            return;
        }
        if (await AdmitAsync(ctx, grant, files, directory) is null) return;

        ctx.User = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, grant.OwnerId),
            new Claim(AgentHub.ProjectClaim, grant.ProjectId),
        ], authenticationType: "agent-hub-ticket"));
        await next();
    }

    private static async Task MapErrors(HttpContext ctx, Func<Task> next, ILogger log)
    {
        try { await next(); }
        catch (AgentFileTooLargeException e) { await Error(ctx, StatusCodes.Status413PayloadTooLarge, e.Message); }
        catch (UnauthorizedAccessException e) { await Error(ctx, StatusCodes.Status403Forbidden, e.Message); }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { await Error(ctx, StatusCodes.Status404NotFound, "Не найдено"); }
        catch (GitCommandException e) { await Error(ctx, StatusCodes.Status409Conflict, e.Message); }
        // Коллизия имён при создании: существующий файл не перезаписываем молча
        catch (InvalidOperationException) { await Error(ctx, StatusCodes.Status409Conflict, "exists"); }
        catch (IOException e)
        {
            log.LogWarning(e, "Файловая операция localhost-API упала");
            await Error(ctx, StatusCodes.Status409Conflict, e.Message);
        }
    }

    private static Task Error(HttpContext ctx, int status, string message)
    {
        if (ctx.Response.HasStarted) return Task.CompletedTask;
        ctx.Response.StatusCode = status;
        return ctx.Response.WriteAsJsonAsync(new { error = message });
    }

    private static Project ProjectOf(HttpContext ctx) => (Project)ctx.Items[ProjectItem]!;
    private static string RootOf(HttpContext ctx) => (string)ctx.Items[RootItem]!;

    // ---------- файлы: контракт FilesController ----------

    private static void MapFiles(RouteGroupBuilder g, AgentProjectFiles files)
    {
        g.MapGet("", async (HttpContext ctx, string? path, bool? showHidden) =>
            Results.Ok(await files.ListAsync(ProjectOf(ctx), path ?? "", showHidden ?? false, ctx.RequestAborted)));
        g.MapGet("/tree", async (HttpContext ctx, string? path, bool? showHidden) =>
            Results.Ok(await files.TreeAsync(ProjectOf(ctx), path ?? "", showHidden ?? false, ctx.RequestAborted)));
        g.MapGet("/search", async (HttpContext ctx, string? q) =>
            Results.Ok(await files.SearchAsync(ProjectOf(ctx), q ?? "", ctx.RequestAborted)));
        g.MapGet("/content", async (HttpContext ctx, string path) =>
            Directory.Exists(files.Check(ProjectOf(ctx), path).Full)
                ? Results.NotFound()
                : Results.Ok(await files.GetContentAsync(ProjectOf(ctx), path, ctx.RequestAborted)));
        g.MapPut("/content", async (HttpContext ctx, string path, SaveContentRequest req) =>
        {
            if (Directory.Exists(files.Check(ProjectOf(ctx), path).Full)) return Results.NotFound();
            await files.WriteFileAsync(ProjectOf(ctx), path, req.Content, ctx.RequestAborted);
            return Results.Ok();
        });
        g.MapGet("/diff", async (HttpContext ctx, string path) =>
            Results.Ok(new DiffResponse(await files.GetDiffAsync(ProjectOf(ctx), path, ctx.RequestAborted))));
        g.MapPost("/revert", async (HttpContext ctx, PathRequest req) =>
            await files.RevertFileAsync(ProjectOf(ctx), req.Path, ctx.RequestAborted)
                ? Results.Ok()
                : Results.BadRequest(new { error = "Не удалось откатить (не git-репозиторий?)" }));
        g.MapPost("/create", async (HttpContext ctx, CreateFileRequest req) =>
        {
            await files.CreateFileAsync(ProjectOf(ctx), req.Path, req.Content ?? "", ctx.RequestAborted);
            return Results.Ok();
        });
        g.MapPost("/mkdir", async (HttpContext ctx, PathRequest req) =>
        {
            await files.CreateDirectoryAsync(ProjectOf(ctx), req.Path, ctx.RequestAborted);
            return Results.Ok();
        });
        g.MapPost("/rename", async (HttpContext ctx, RenameRequest req) =>
        {
            await files.RenameAsync(ProjectOf(ctx), req.OldPath, req.NewPath, ctx.RequestAborted);
            return Results.Ok();
        });
        g.MapDelete("", async (HttpContext ctx, string path) =>
        {
            await files.DeleteAsync(ProjectOf(ctx), path, ctx.RequestAborted);
            return Results.NoContent();
        });
        g.MapGet("/stream", async (HttpContext ctx, string path) =>
        {
            var file = await files.OpenReadAsync(ProjectOf(ctx), path, ctx.RequestAborted);
            return Results.Stream(file.Content, FileContentReader.StreamMime(path), enableRangeProcessing: true);
        });
    }

    // ---------- билет потока: только агент, вне контракта FilesController ----------

    private static void MapStreamTicket(RouteGroupBuilder g, AgentProjectFiles files, AgentUrlTickets urlTickets) =>
        g.MapPost("/" + DeviceAgentApi.StreamTicketRoute, (HttpContext ctx, PathRequest req) =>
        {
            // Путь проходит ту же политику, что и сам поток: на путь наружу билета не будет
            files.Check(ProjectOf(ctx), req.Path);
            var (ticket, expiresAt) = urlTickets.IssueStream(GrantOf(ctx), req.Path);
            return Results.Ok(new { streamTicket = ticket, expiresAt });
        });

    private static AgentTicketIntrospection GrantOf(HttpContext ctx) => (AgentTicketIntrospection)ctx.Items[GrantItem]!;
    private static string OwnerOf(HttpContext ctx) => GrantOf(ctx).OwnerId;

    // Проект для вертикалей рабочих подсистем: с реальным корнем (см. AdmitAsync)
    private static Project WorkProject(HttpContext ctx) =>
        new() { Id = ProjectOf(ctx).Id, OwnerId = ProjectOf(ctx).OwnerId, RootPath = RootOf(ctx) };

    private static IResult ToResult(ProjectServicesResult r) =>
        r.Body is null ? Results.StatusCode(r.Status) : Results.Json(r.Body, statusCode: r.Status);

    // ---------- билеты хаба и превью: только агент ----------

    private static void MapUrlTickets(RouteGroupBuilder g, AgentUrlTickets urlTickets, AgentWorkbench workbench)
    {
        g.MapPost("/" + DeviceAgentApi.HubTicketRoute, (HttpContext ctx) =>
        {
            var (ticket, expiresAt) = urlTickets.Issue(GrantOf(ctx), AgentUrlTickets.HubScope, DeviceAgentApi.HubTicketLifetime);
            return Results.Ok(new { hubTicket = ticket, expiresAt });
        });
        g.MapPost("/" + DeviceAgentApi.PreviewTicketRoute, (HttpContext ctx) =>
        {
            var (ticket, expiresAt) = urlTickets.Issue(GrantOf(ctx), AgentUrlTickets.PreviewScope,
                DeviceAgentApi.PreviewTicketLifetime, capByGrant: false);
            var id = Uri.EscapeDataString(ProjectOf(ctx).Id);
            var url = $"http://127.0.0.1:{workbench.PreviewPort}/preview/{id}/?{DeviceAgentApi.PreviewTicketQuery}={ticket}";
            return Results.Ok(new { previewTicket = ticket, expiresAt, url });
        });
    }

    // ---------- сервисы проекта: контракт PreviewController ----------

    private static void MapProjectServices(RouteGroupBuilder g, AgentProjectFiles files)
    {
        static ProjectServicesApi Api(HttpContext ctx) => ctx.RequestServices.GetRequiredService<ProjectServicesApi>();

        // Рабочий каталог дев-сервера — реальный путь под корнем (лексика пропускает симлинк наружу)
        static Func<string?, bool> CwdAllowed(HttpContext ctx, AgentProjectFiles files) => cwd =>
        {
            if (string.IsNullOrWhiteSpace(cwd)) return true;
            try { files.Check(ProjectOf(ctx), cwd); return true; }
            catch (UnauthorizedAccessException) { return false; }
        };

        g.MapGet("/services", async (HttpContext ctx, CancellationToken ct) =>
            ToResult(await Api(ctx).ServicesAsync(WorkProject(ctx), OwnerOf(ctx), files)));
        g.MapPost("/preview/active-external", async (HttpContext ctx, PreviewActiveRequest req) =>
            ToResult(await Api(ctx).SetActiveExternalAsync(WorkProject(ctx), req, files)));
        g.MapPost("/preview/start", async (HttpContext ctx, PreviewStartRequest req) =>
            ToResult(await Api(ctx).StartAsync(WorkProject(ctx), OwnerOf(ctx), req, files, CwdAllowed(ctx, files))));
        g.MapPost("/preview/stop", async (HttpContext ctx, PreviewStopRequest? req) =>
            ToResult(await Api(ctx).StopAsync(WorkProject(ctx), OwnerOf(ctx), req, files)));
        g.MapPost("/preview/stop-external", async (HttpContext ctx, StopExternalRequest req) =>
            ToResult(await Api(ctx).StopExternalAsync(WorkProject(ctx), OwnerOf(ctx), req, files, ctx.RequestAborted)));
        g.MapGet("/preview/status", (HttpContext ctx, CancellationToken ct) =>
            ToResult(Api(ctx).Status(WorkProject(ctx), OwnerOf(ctx))));
        g.MapPost("/preview/active", (HttpContext ctx, PreviewActiveRequest req) =>
            ToResult(Api(ctx).SetActive(WorkProject(ctx), req)));
        g.MapGet("/launch-config", async (HttpContext ctx, CancellationToken ct) =>
            ToResult(await Api(ctx).GetLaunchConfigAsync(WorkProject(ctx), files)));
        g.MapPut("/launch-config", async (HttpContext ctx, LaunchConfigPutRequest req) =>
            ToResult(await Api(ctx).PutLaunchConfigAsync(WorkProject(ctx), req, files)));
    }

    // ---------- навыки и агенты проекта: контракт SkillsController ----------

    private static void MapSkills(RouteGroupBuilder g, AgentProjectFiles files, AgentWorkbench workbench)
    {
        static SkillsService Skills(HttpContext ctx) => ctx.RequestServices.GetRequiredService<SkillsService>();

        // Глобальная часть — профиль CLI агента: ровно то, что увидит ход на этой машине
        g.MapGet("/skills", async (HttpContext ctx, CancellationToken ct) =>
        {
            var skills = Skills(ctx);
            return Results.Ok(new
            {
                skills = skills.GetSkillsInConfigRoot(workbench.CliProfile),
                projectSkills = await skills.GetProjectSkillsAsync(files, WorkProject(ctx), ct),
                agents = await skills.GetProjectAgentsAsync(files, WorkProject(ctx), ct),
                workflows = skills.GetWorkflowsInConfigRoot(workbench.CliProfile),
                plugins = skills.GetPluginSkillsInConfigRoot(workbench.CliProfile),
            });
        });
        g.MapGet("/agents/{agentName}", async (HttpContext ctx, string agentName, CancellationToken ct) =>
            await Skills(ctx).GetAgentContentAsync(files, WorkProject(ctx), agentName, ct) is { } content
                ? Results.Ok(new { content })
                : Results.NotFound());
        g.MapPut("/agents/{agentName}", async (HttpContext ctx, string agentName, SkillContentRequest req) =>
        {
            await Skills(ctx).SaveProjectAgentAsync(files, WorkProject(ctx), agentName, req.Content, ctx.RequestAborted);
            return Results.Ok();
        });
        g.MapPost("/agents", async (HttpContext ctx, CreateSkillRequest req) =>
        {
            if (string.IsNullOrWhiteSpace(req.Name))
                return Results.BadRequest(new { error = "Имя агента не может быть пустым" });
            await Skills(ctx).SaveProjectAgentAsync(files, WorkProject(ctx), req.Name.Trim(), req.Content, ctx.RequestAborted);
            return Results.Ok(new { name = req.Name.Trim() });
        });
    }

    // ---------- вложения чата локального проекта: вместо ChatsController.Upload ----------

    private static void MapAttachments(RouteGroupBuilder g, AgentProjectFiles files) =>
        g.MapPost("/" + DeviceAgentApi.AttachmentsRoute, async (HttpContext ctx) =>
        {
            if (!ctx.Request.HasFormContentType)
                return Results.BadRequest(new { error = "Файл не выбран или пустой" });
            // Заведомо большое тело отбиваем по заголовку, не читая: запас — на обёртку multipart
            if (ctx.Request.ContentLength is { } declared) files.EnsureWritable(declared - MultipartOverheadBytes);
            // Форма сверх малого порога буферизуется на диск, в память файл попадает только ниже потолка
            var form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
            var file = form.Files.GetFile("file");
            if (file is null || file.Length == 0)
                return Results.BadRequest(new { error = "Файл не выбран или пустой" });
            files.EnsureWritable(file.Length);
            if (AttachmentsGitExclude.AttachmentPath(file.FileName) is not { } rel)
                return Results.BadRequest(new { error = "Некорректное имя файла" });

            var project = ProjectOf(ctx);
            // Вложения не должны светиться в git-статусе проекта (как у сервера) — best effort
            try { await AttachmentsGitExclude.EnsureAsync(files, project, ctx.RequestAborted); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }

            // Ровно один буфер размера файла, без роста MemoryStream и копии ToArray
            var content = new byte[file.Length];
            await using (var input = file.OpenReadStream())
                await input.ReadExactlyAsync(content, ctx.RequestAborted);
            await files.WriteFileBytesAsync(project, rel, content, ctx.RequestAborted);
            return Results.Ok(new { path = rel });
        });

    private const long MultipartOverheadBytes = 64 * 1024;

    // ---------- git: контракт GitController (подмножество рабочего дерева) ----------
    // Мутации, как у сервера, отвечают свежим статусом: панель изменений перерисовывается по нему

    private static void MapGit(RouteGroupBuilder g, AgentProjectFiles files, GitService git)
    {
        // Отказ по пути у git-маршрутов сервера — 400 «Недопустимый путь», а не 403 файлов
        g.AddEndpointFilter(async (ctx, next) =>
        {
            try { return await next(ctx); }
            catch (UnauthorizedAccessException) { return Results.BadRequest(new { error = "Недопустимый путь" }); }
        });

        // Путь внутри репозитория проходит ту же политику, что файлы: git сам ссылки не раскрывает
        string Checked(HttpContext ctx, string path)
        {
            files.Check(ProjectOf(ctx), path);
            return path;
        }

        // Параметр ct обязателен: лямбда с одним HttpContext связалась бы как RequestDelegate,
        // и результат молча потерялся бы (пустой 200)
        g.MapGet("/status", async (HttpContext ctx, CancellationToken ct) => Results.Ok(await git.StatusAsync(null, RootOf(ctx), ct)));
        g.MapGet("/diff", async (HttpContext ctx, string path, bool? staged) =>
            Results.Ok(new DiffResponse(await git.DiffFileAsync(null, RootOf(ctx), Checked(ctx, path), staged ?? false, ctx.RequestAborted))));
        g.MapGet("/log", async (HttpContext ctx, int? limit, string? branch) =>
            Results.Ok(await git.LogAsync(null, RootOf(ctx), Math.Clamp(limit ?? 100, 1, 1000), branch, ctx.RequestAborted)));
        g.MapGet("/branches", async (HttpContext ctx, CancellationToken ct) => Results.Ok(await git.BranchesAsync(null, RootOf(ctx), ct)));
        g.MapPost("/stage", async (HttpContext ctx, GitPathRequest body) =>
        {
            await git.StageAsync(null, RootOf(ctx), Checked(ctx, body.Path), ctx.RequestAborted);
            return Results.Ok(await git.StatusAsync(null, RootOf(ctx), ctx.RequestAborted));
        });
        g.MapPost("/unstage", async (HttpContext ctx, GitPathRequest body) =>
        {
            await git.UnstageAsync(null, RootOf(ctx), Checked(ctx, body.Path), ctx.RequestAborted);
            return Results.Ok(await git.StatusAsync(null, RootOf(ctx), ctx.RequestAborted));
        });
        g.MapPost("/discard", async (HttpContext ctx, GitPathRequest body) =>
        {
            await git.DiscardAsync(null, RootOf(ctx), Checked(ctx, body.Path), ctx.RequestAborted);
            return Results.Ok(await git.StatusAsync(null, RootOf(ctx), ctx.RequestAborted));
        });
        g.MapPost("/commit", async (HttpContext ctx, GitCommitRequest body) =>
        {
            if (string.IsNullOrWhiteSpace(body.Message)) return Results.BadRequest(new { error = "Пустое сообщение коммита" });
            var sha = await git.CommitAsync(null, RootOf(ctx), body.Message, body.Amend, ctx.RequestAborted);
            return Results.Ok(new CommitResponse(sha));
        });
    }
}
