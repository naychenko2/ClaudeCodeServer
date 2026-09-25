using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.ProjectServices;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Yarp.ReverseProxy.Forwarder;

namespace ClaudeHomeServer.DeviceAgent.Composition;

/// <summary>
/// Превью дев-серверов с машины проекта (задача 4.3): <c>/preview/{projectId}/{**path}</c> на
/// отдельном loopback-порту агента. Отдельный порт — отдельный origin: страница дев-сайта
/// (чужой код из проекта) не оказывается одного origin с localhost-API и не ходит в него
/// как «своя». Наружу машины порт не виден, как и API.
///
/// Вход: iframe заголовок не ставит, поэтому первая загрузка несёт билет превью в адресе
/// (<see cref="DeviceAgentApi.PreviewTicketQuery"/>, выдаёт агент по основному билету),
/// агент кладёт его в куку на путь проекта — её несут подресурсы — и срезает из адреса до
/// проброса: дев-сервер билета не видит. Кука секционированная (Partitioned): живёт только
/// под сайтом веб-морды, чужая страница с тем же iframe её не получит. Сам проброс —
/// <see cref="DevServerPreviewForwarder"/> вертикали, тот же, что у сервера.
/// </summary>
internal sealed class PreviewGate(int port, string serverOrigin, AgentUrlTickets urlTickets,
    AgentProjectFiles files, AgentProjectDirectory directory)
{
    private readonly HttpMessageInvoker _invoker = DevServerPreviewForwarder.CreateInvoker();

    public async Task HandleAsync(HttpContext ctx)
    {
        var request = ctx.Request;
        if (!LocalApi.IsAllowedHost(request.Host, port))
        {
            ctx.Response.StatusCode = StatusCodes.Status421MisdirectedRequest;
            return;
        }

        // Свой origin (запросы самого дев-сайта) или веб-морда; остальным — отказ
        var origin = request.Headers.Origin.ToString();
        if (origin.Length > 0 && !IsOwnOrigin(origin) && !LocalApiOptions.SameOrigin(origin, serverOrigin))
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        if (!DevServerPreviewForwarder.TryParse(request.Path.Value, out var projectId, out var restPath))
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        projectId = Uri.UnescapeDataString(projectId);

        var fromQuery = request.Query[DeviceAgentApi.PreviewTicketQuery].ToString();
        var ticket = fromQuery.Length > 0 ? fromQuery : request.Cookies[DeviceAgentApi.PreviewCookie];
        var grant = urlTickets.Validate(ticket, AgentUrlTickets.PreviewScope);
        if (grant is null || grant.ProjectId != projectId)
        {
            await Deny(ctx, StatusCodes.Status401Unauthorized, "Билет превью недействителен или истёк");
            return;
        }

        // Корень проекта — под корнями машины, как у API: билет не делает чужую папку своей
        var project = new Project { Id = grant.ProjectId, OwnerId = grant.OwnerId, RootPath = grant.RootPath };
        try
        {
            var (root, _) = files.Check(project, "");
            directory.Remember(new Project { Id = grant.ProjectId, OwnerId = grant.OwnerId, RootPath = root });
        }
        catch (Exception e) when (e is UnauthorizedAccessException or DirectoryNotFoundException)
        {
            await Deny(ctx, StatusCodes.Status403Forbidden, e.Message);
            return;
        }

        if (fromQuery.Length > 0)
        {
            ctx.Response.Cookies.Append(DeviceAgentApi.PreviewCookie, fromQuery, CookieFor(projectId));
            request.QueryString = WithoutTicket(request.Query);
        }
        StripCredentials(request);

        await DevServerPreviewForwarder.ForwardAsync(ctx,
            ctx.RequestServices.GetRequiredService<DevServerService>(),
            ctx.RequestServices.GetRequiredService<IHttpForwarder>(),
            _invoker, projectId, restPath);
    }

    /// <summary>
    /// Дев-серверу не уходит ни одна кука и ни одна учётка: браузер прикладывает к loopback
    /// куки ВСЕХ сервисов на 127.0.0.1 (Dify, админки, соседние дев-серверы), а дев-сервер —
    /// код из зависимостей проекта, доверия ему меньше, чем пользователю. Своя авторизация
    /// дев-сайту не нужна — он и так за билетом агента.
    /// </summary>
    internal static void StripCredentials(HttpRequest request)
    {
        request.Headers.Remove("Cookie");
        request.Headers.Remove("Authorization");
        request.Headers.Remove("Proxy-Authorization");
    }

    private bool IsOwnOrigin(string origin) =>
        Uri.TryCreate(origin, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttp
        && LocalApi.IsAllowedHost(new HostString(uri.Authority), port);

    internal static CookieOptions CookieFor(string projectId)
    {
        var options = new CookieOptions
        {
            Path = $"/preview/{Uri.EscapeDataString(projectId)}/",
            HttpOnly = true,
            // iframe на странице веб-морды — межсайтовый контекст: без None кука не поедет,
            // а None требует Secure (на http://127.0.0.1 браузеры его принимают: loopback
            // считается защищённым контекстом)
            SameSite = SameSiteMode.None,
            Secure = true,
            MaxAge = DeviceAgentApi.PreviewTicketLifetime,
        };
        options.Extensions.Add("Partitioned");
        return options;
    }

    private static QueryString WithoutTicket(IQueryCollection query) =>
        QueryString.Create(query
            .Where(q => !string.Equals(q.Key, DeviceAgentApi.PreviewTicketQuery, StringComparison.Ordinal))
            .SelectMany(q => q.Value.Select(v => new KeyValuePair<string, string?>(q.Key, v))));

    private static Task Deny(HttpContext ctx, int status, string message)
    {
        ctx.Response.StatusCode = status;
        return ctx.Response.WriteAsJsonAsync(new { error = message });
    }
}
