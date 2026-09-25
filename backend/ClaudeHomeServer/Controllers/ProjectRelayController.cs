using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Execution;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Controllers;

/// <summary>
/// Ретранслятор чтения для других устройств (ADR-016 §5, задача 5.1): локальный проект открыт
/// не с его машины — чтение идёт через сервер агенту устройства. Маршруты — те же, что у
/// FilesController/GitController, под базой <c>api/projects/{projectId}/relay/</c>; ответ агента
/// пересылается как есть, тело — потоком по мере прихода кадров. Записи здесь нет по
/// построению: перечень операций — <see cref="RelayOperations"/>, все маршруты — GET.
///
/// Сервер к диску не ходит (группа «платформа»): проект, владелец и устройство проверяются
/// ДО обращения к каналу, дальше решает агент своими проверками корней (сторож G7).
/// </summary>
[ProjectCapability(ProjectCapabilityArea.Platform)]
[ApiController]
[Authorize]
[Route("api/projects/{projectId}/" + RelayProtocol.RouteSegment)]
public sealed class ProjectRelayController(
    ProjectManager projects,
    FeatureFlagService flags,
    IDeviceExecChannel? deviceExec = null,
    IDeviceRelayChannel? relay = null) : ControllerBase
{
    private string UserId => User.FindFirstValue(JwtRegisteredClaimNames.Sub)!;

    [HttpGet("files")]
    public Task<IActionResult> List(string projectId, [FromQuery] string? path, CancellationToken ct) =>
        RelayAsync(projectId, "files", (p, r) => r with { Path = path ?? "", ShowHidden = p.ShowHiddenFiles }, ct);

    [HttpGet("files/tree")]
    public Task<IActionResult> Tree(string projectId, [FromQuery] string? path, [FromQuery] bool? showHidden, CancellationToken ct) =>
        RelayAsync(projectId, "files/tree", (p, r) => r with { Path = path ?? "", ShowHidden = showHidden ?? p.ShowHiddenFiles }, ct);

    [HttpGet("files/content")]
    public Task<IActionResult> Content(string projectId, [FromQuery] string path, CancellationToken ct) =>
        RelayAsync(projectId, "files/content", (_, r) => r with { Path = path }, ct);

    [HttpGet("files/stream")]
    public Task<IActionResult> Stream(string projectId, [FromQuery] string path, CancellationToken ct) =>
        RelayAsync(projectId, "files/stream", (_, r) => r with { Path = path }, ct);

    [HttpGet("files/stat")]
    public Task<IActionResult> Stat(string projectId, [FromQuery] string path, CancellationToken ct) =>
        RelayAsync(projectId, "files/stat", (_, r) => r with { Path = path }, ct);

    [HttpGet("files/search")]
    public Task<IActionResult> Search(string projectId, [FromQuery] string? q, CancellationToken ct) =>
        RelayAsync(projectId, "files/search", (_, r) => r with { Query = q ?? "" }, ct);

    [HttpGet("files/diff")]
    public Task<IActionResult> FileDiff(string projectId, [FromQuery] string path, CancellationToken ct) =>
        RelayAsync(projectId, "files/diff", (_, r) => r with { Path = path }, ct);

    [HttpGet("git/status")]
    public Task<IActionResult> GitStatus(string projectId, CancellationToken ct) =>
        RelayAsync(projectId, "git/status", (_, r) => r, ct);

    [HttpGet("git/diff")]
    public Task<IActionResult> GitDiff(string projectId, [FromQuery] string path, [FromQuery] bool staged = false, CancellationToken ct = default) =>
        RelayAsync(projectId, "git/diff", (_, r) => r with { Path = path, Staged = staged }, ct);

    [HttpGet("git/log")]
    public Task<IActionResult> GitLog(string projectId, [FromQuery] int limit = 100, [FromQuery] string? branch = null, CancellationToken ct = default) =>
        RelayAsync(projectId, "git/log", (_, r) => r with { Limit = limit, Branch = branch }, ct);

    [HttpGet("git/commits/{sha}")]
    public Task<IActionResult> Commit(string projectId, string sha, CancellationToken ct) =>
        RelayAsync(projectId, "git/commits/{sha}", (_, r) => r with { Sha = sha }, ct);

    [HttpGet("git/commits/{sha}/diff")]
    public Task<IActionResult> CommitDiff(string projectId, string sha, [FromQuery] string path, CancellationToken ct) =>
        RelayAsync(projectId, "git/commits/{sha}/diff", (_, r) => r with { Sha = sha, Path = path }, ct);

    [HttpGet("git/commits/{sha}/file")]
    public Task<IActionResult> CommitFile(string projectId, string sha, [FromQuery] string path, CancellationToken ct) =>
        RelayAsync(projectId, "git/commits/{sha}/file", (_, r) => r with { Sha = sha, Path = path }, ct);

    private IActionResult Unavailable(string reason) =>
        Conflict(new { error = reason, code = RelayProtocol.UnavailableCode });

    // Всё, что можно решить без устройства, решается до канала: чужой проект, чужой владелец,
    // серверный проект, выключенный флаг, офлайн — отказ, а не таймаут
    private async Task<IActionResult> RelayAsync(string projectId, string template,
        Func<Project, RelayRequest, RelayRequest> args, CancellationToken ct)
    {
        // Сервисный токен лежит в env каждого хода: ретранслятор только для веб-сессии владельца
        if (User.FindFirstValue(JwtService.TokenKindClaim) == JwtService.ServiceTokenKind)
            return StatusCode(StatusCodes.Status403Forbidden,
                new { error = "Файлы локального проекта через сервер открывает только веб-сессия владельца" });

        var project = projects.GetById(projectId);
        if (project is null || project.OwnerId != UserId) return NotFound();
        if (!ProjectCapabilities.IsDeviceBound(project))
            return BadRequest(new { error = ProjectCapabilities.NotDeviceBoundReason });
        if (!flags.IsEnabled(UserId, FeatureFlagKeys.LocalProjects))
            return StatusCode(StatusCodes.Status403Forbidden,
                new { error = "Локальные проекты выключены (экспериментальная функция «Локальные проекты»)" });
        if (relay is null)
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "Канал устройств на сервере выключен" });

        if (ProjectCapabilities.RelayRefusal(project, deviceExec?.GetStatus(UserId, project.DeviceId!)) is { } reason)
            return Unavailable(reason);

        var route = RelayProtocol.Routes.Single(r => r.Template == template);
        var request = args(project, new RelayRequest(route.Operation, project.Id, project.RootPath, route.Variant));

        IDeviceExecStream stream;
        try { stream = await relay.OpenRelayAsync(UserId, project.DeviceId!, ct); }
        catch (DeviceExecRefusedException e) { return Unavailable(e.Message); }

        await using (stream)
            return await ExchangeAsync(stream, request, ct);
    }

    // Кадр запроса туда; обратно заголовок (Info), тело (Stdout) и конец (Exit)
    private async Task<IActionResult> ExchangeAsync(IDeviceExecStream stream, RelayRequest request, CancellationToken ct)
    {
        // Устройство могло закрыть канал раньше, чем ушёл запрос: это обрыв связи, а не 500
        try { await stream.SendAsync(DeviceExecFrameChannel.Control, JsonSerializer.SerializeToUtf8Bytes(request, RelayProtocol.Json), ct); }
        catch (ObjectDisposedException) { return Unavailable("Связь с устройством оборвалась"); }

        var frames = stream.ReadAllAsync(ct).GetAsyncEnumerator(ct);
        try
        {
            RelayResponseHead? head = null;
            while (head is null)
            {
                bool more;
                try { more = await frames.MoveNextAsync().AsTask().WaitAsync(RelayProtocol.ResponseTimeout, ct); }
                catch (TimeoutException)
                {
                    return StatusCode(StatusCodes.Status504GatewayTimeout,
                        new { error = $"Устройство не ответило за {(int)RelayProtocol.ResponseTimeout.TotalSeconds} с" });
                }
                if (!more) return Unavailable("Связь с устройством оборвалась");
                if (frames.Current.Channel == DeviceExecFrameChannel.Info)
                    head = JsonSerializer.Deserialize<RelayResponseHead>(frames.Current.Payload.Span, RelayProtocol.Json);
                else if (frames.Current.Channel == DeviceExecFrameChannel.Exit)
                    return Unavailable("Устройство закрыло запрос без ответа");
            }

            Response.StatusCode = head.Status;
            if (head.ContentType is { Length: > 0 } type) Response.ContentType = type;
            if (head.Length is { } length) Response.ContentLength = length;
            await Response.StartAsync(ct);

            while (await frames.MoveNextAsync())
            {
                if (frames.Current.Channel == DeviceExecFrameChannel.Stdout)
                    await Response.Body.WriteAsync(frames.Current.Payload, ct);
                else if (frames.Current.Channel == DeviceExecFrameChannel.Exit)
                    return new EmptyResult();
            }

            // Заголовок уже ушёл, а тело оборвалось: честно рвём ответ, а не отдаём обрезок как целое
            HttpContext.Abort();
            return new EmptyResult();
        }
        finally
        {
            await frames.DisposeAsync();
        }
    }
}
