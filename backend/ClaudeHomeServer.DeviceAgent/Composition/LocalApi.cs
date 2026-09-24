using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Files;
using ClaudeHomeServer.Services.Git;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ClaudeHomeServer.DeviceAgent.Composition;

/// <summary>Настройки localhost-API: порт и единственный допустимый origin — веб-морда сервера.</summary>
internal sealed record LocalApiOptions(int Port, string ServerOrigin, string AgentVersion)
{
    public static string OriginOf(string serverUrl) => new Uri(serverUrl).GetLeftPart(UriPartial.Authority);
}

/// <summary>
/// localhost-API агента (ADR-016 §5, задача 4.2): браузер на машине проекта работает с
/// файлами и git напрямую. Маршруты и форма ответов — те же, что у серверных
/// <c>FilesController</c>/<c>GitController</c>, фронту меняется только база адреса;
/// под маршрутами — та же вертикаль Files через шов <see cref="IProjectFiles"/> и тот же
/// <see cref="GitService"/>.
///
/// Периметр, по порядку: слушаем только loopback; заголовок <c>Host</c> — только loopback с
/// нашим портом (DNS-rebinding); <c>Origin</c>, если есть, — только origin сервера (CORS и
/// preflight Private Network Access); затем билет сервера, привязанный к проекту маршрута,
/// и корень проекта под разрешёнными корнями машины.
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
        AgentStreamTickets? streamTickets = null)
    {
        streamTickets ??= new AgentStreamTickets();
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
            // Потолок записи плюс запас на JSON-обёртку
            k.Limits.MaxRequestBodySize = new AgentLimits().MaxWriteBytes * 2 + 1024 * 1024;
        });
        configure?.Invoke(builder);

        var app = builder.Build();
        var log = loggers.CreateLogger("ai-home-agent.local-api");

        app.Use((ctx, next) => Perimeter(ctx, next, options));
        app.Use((ctx, next) => Authorize(ctx, next, files, tickets, streamTickets, watchers));
        app.Use((ctx, next) => MapErrors(ctx, next, log));

        app.MapGet("/api/agent/health", () => Results.Ok(new { agent = "ai-home-agent", version = options.AgentVersion }));
        MapFiles(app.MapGroup("/api/projects/{projectId}/files"), files);
        MapStreamTicket(app.MapGroup("/api/projects/{projectId}"), files, streamTickets);
        MapGit(app.MapGroup("/api/projects/{projectId}/git"), files, git);
        return app;
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
            if (!string.Equals(origin, options.ServerOrigin, StringComparison.OrdinalIgnoreCase))
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
            response.Headers.AccessControlAllowHeaders = DeviceAgentApi.TicketHeader + ", Content-Type";
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
        AgentTicketCache tickets, AgentStreamTickets streamTickets, AgentFileWatchers? watchers)
    {
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
            ? streamTickets.Validate(ctx.Request.Query[DeviceAgentApi.StreamTicketQuery], ctx.Request.Query["path"])
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

        var project = new Project { Id = grant.ProjectId, OwnerId = grant.OwnerId, RootPath = grant.RootPath };
        string root;
        try { (root, _) = files.Check(project, ""); }
        catch (AgentPathRefusedException e) { await Error(ctx, StatusCodes.Status403Forbidden, e.Message); return; }
        catch (DirectoryNotFoundException e) { await Error(ctx, StatusCodes.Status404NotFound, e.Message); return; }

        ctx.Items[ProjectItem] = project;
        ctx.Items[RootItem] = root;
        ctx.Items[GrantItem] = grant;
        watchers?.Touch(project.Id, root);
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

    private static void MapStreamTicket(RouteGroupBuilder g, AgentProjectFiles files, AgentStreamTickets streamTickets) =>
        g.MapPost("/" + DeviceAgentApi.StreamTicketRoute, (HttpContext ctx, PathRequest req) =>
        {
            // Путь проходит ту же политику, что и сам поток: на путь наружу билета не будет
            files.Check(ProjectOf(ctx), req.Path);
            var (ticket, expiresAt) = streamTickets.Issue((AgentTicketIntrospection)ctx.Items[GrantItem]!, req.Path);
            return Results.Ok(new { streamTicket = ticket, expiresAt });
        });

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
