using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.ProjectServices;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ClaudeHomeServer.Services.Composition;

namespace ClaudeHomeServer.Controllers;

/// <summary>
/// Раздел «Сервисы проекта» на сервере. Сами маршруты живут в вертикали
/// (<see cref="ProjectServicesApi"/>): их же обслуживает агент устройства для локального
/// проекта (ADR-016, задача 4.3). Здесь — вход: владелец проекта и отказ G1 атрибутом.
/// </summary>
[ProjectCapability(ProjectCapabilityArea.FileBound)]
[ApiController]
[Authorize]
public class PreviewController : ControllerBase
{
    private readonly ProjectManager _projects;
    private readonly ProjectServicesApi _api;
    private readonly ExternalPreviewRouter _external;
    private readonly ExternalPreviewStore _links;
    private readonly JwtService _jwt;
    private readonly ILogger<PreviewController> _log;

    public PreviewController(ProjectManager projects, ProjectServicesApi api,
        ExternalPreviewRouter external, ExternalPreviewStore links, JwtService jwt,
        ILogger<PreviewController> log)
    {
        _projects = projects;
        _api = api;
        _external = external;
        _links = links;
        _jwt = jwt;
        _log = log;
    }

    private string UserId => User.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? "";

    private Models.Project? OwnedProject(string projectId)
    {
        var project = _projects.GetById(projectId);
        return project?.OwnerId == UserId ? project : null;
    }

    private IActionResult Result(ProjectServicesResult r) => StatusCode(r.Status, r.Body);

    /// <summary>Список запускаемых сервисов проекта (инференс из манифестов + сохранённые) с runtime-статусом.</summary>
    [HttpGet("/api/projects/{projectId}/services")]
    public async Task<IActionResult> Services(string projectId) =>
        OwnedProject(projectId) is { } project ? Result(await _api.ServicesAsync(project, UserId)) : Forbid();

    /// <summary>Показать в превью сервис, поднятый вне продукта (порт — из конфигурации, не от клиента).</summary>
    [HttpPost("/api/projects/{projectId}/preview/active-external")]
    public async Task<IActionResult> SetActiveExternal(string projectId, [FromBody] PreviewActiveRequest req) =>
        OwnedProject(projectId) is { } project ? Result(await _api.SetActiveExternalAsync(project, req)) : Forbid();

    [HttpPost("/api/projects/{projectId}/preview/start")]
    public async Task<IActionResult> Start(string projectId, [FromBody] PreviewStartRequest req) =>
        OwnedProject(projectId) is { } project ? Result(await _api.StartAsync(project, UserId, req)) : Forbid();

    [HttpPost("/api/projects/{projectId}/preview/stop")]
    public async Task<IActionResult> Stop(string projectId, [FromBody] PreviewStopRequest? req) =>
        OwnedProject(projectId) is { } project ? Result(await _api.StopAsync(project, UserId, req)) : Forbid();

    /// <summary>Остановить сервис, поднятый ВНЕ продукта: свой осиротевший — сразу, чужой — с подтверждением.</summary>
    [HttpPost("/api/projects/{projectId}/preview/stop-external")]
    public async Task<IActionResult> StopExternal(string projectId, [FromBody] StopExternalRequest req) =>
        OwnedProject(projectId) is { } project
            ? Result(await _api.StopExternalAsync(project, UserId, req, ct: HttpContext.RequestAborted))
            : Forbid();

    [HttpGet("/api/projects/{projectId}/preview/status")]
    public IActionResult Status(string projectId) =>
        OwnedProject(projectId) is { } project ? Result(_api.Status(project, UserId)) : Forbid();

    /// <summary>Назначить активный для превью сервис (на его порт указывает iframe).</summary>
    [HttpPost("/api/projects/{projectId}/preview/active")]
    public IActionResult SetActive(string projectId, [FromBody] PreviewActiveRequest req) =>
        OwnedProject(projectId) is { } project ? Result(_api.SetActive(project, req)) : Forbid();

    /// <summary>Прочитать .claude/launch.json проекта.</summary>
    [HttpGet("/api/projects/{projectId}/launch-config")]
    public async Task<IActionResult> GetLaunchConfig(string projectId) =>
        OwnedProject(projectId) is { } project ? Result(await _api.GetLaunchConfigAsync(project)) : Forbid();

    /// <summary>Записать .claude/launch.json проекта.</summary>
    [HttpPut("/api/projects/{projectId}/launch-config")]
    public async Task<IActionResult> PutLaunchConfig(string projectId, [FromBody] LaunchConfigPutRequest req) =>
        OwnedProject(projectId) is { } project ? Result(await _api.PutLaunchConfigAsync(project, req)) : Forbid();

    // ── Внешний доступ по поддомену (см. ExternalPreviewOptions) ──────────────────

    /// <summary>
    /// Выдать ссылку внешнего доступа на сервис проекта.
    ///
    /// Порт, как и у active-external, выбирает СЕРВЕР по конфигурации сервиса: принимать его
    /// от клиента нельзя — эндпоинт стал бы туннелем на любой localhost-порт машины, да ещё
    /// и опубликованным наружу.
    /// </summary>
    [HttpPost("/api/projects/{projectId}/preview/external-link")]
    public async Task<IActionResult> IssueExternalLink(string projectId, [FromBody] PreviewActiveRequest req)
    {
        var project = OwnedProject(projectId);
        if (project is null) return Forbid();
        // Выключенная фича не должна даже признавать существование эндпоинта
        if (!_external.Options.Enabled) return NotFound();
        if (!_external.Options.IsConfigured)
            return StatusCode(503, new { error = "Внешний доступ не настроен: не задан ExternalPreview:PublicBaseUrl" });
        if (string.IsNullOrWhiteSpace(req.ServiceId))
            return BadRequest(new { error = "serviceId не указан" });

        // Живой процесс важнее конфигурации: автопорт и порт из вывода в манифестах не значатся
        var port = await _external.ResolveServicePortAsync(project, req.ServiceId, UserId);
        if (port is not > 0)
            return BadRequest(new { error = "Не удалось определить порт сервиса: он не запущен и порт не задан в конфигурации" });
        if (!await LoopbackResolver.IsListeningAsync(port.Value))
            return BadRequest(new { error = $"На порту {port} никто не слушает" });

        var jti = Guid.NewGuid().ToString("N");
        // Потолок срока — неделя: ссылка наружу не должна жить дольше памяти о ней
        var lifetime = TimeSpan.FromHours(Math.Clamp(_external.Options.TokenLifetimeHours, 1, 24 * 7));
        var now = DateTimeOffset.UtcNow;
        var token = _jwt.IssuePreviewToken(UserId, projectId, req.ServiceId, jti, lifetime);
        var evicted = _links.Add(new ExternalPreviewLink(jti, UserId, projectId, req.ServiceId, now, now.Add(lifetime)));

        _log.LogInformation("Проект {ProjectId}: выдана внешняя ссылка на сервис {ServiceId} (:{Port}) до {Until}",
            projectId, req.ServiceId, port, now.Add(lifetime));

        return Ok(new
        {
            url = _external.BuildLinkUrl(token),
            jti,
            expiresAt = now.Add(lifetime),
            // Вытеснение потолком — это тоже отзыв, и человек обязан о нём узнать:
            // иначе молча умершая ссылка выглядит как поломка продукта
            evicted = evicted.Select(e => new { e.Jti, e.ProjectId, e.ServiceId }),
        });
    }

    /// <summary>
    /// Живые ссылки владельца — СКВОЗЬ все его проекты, а не только текущий. Панель проектная,
    /// но забытая открытой витрина в соседнем проекте иначе осталась бы невидимой, а это
    /// ровно тот случай, ради которого список и нужен.
    /// </summary>
    [HttpGet("/api/preview/external-links")]
    public IActionResult ExternalLinks()
    {
        var links = _links.ListFor(UserId).Select(l => new
        {
            l.Jti,
            l.ProjectId,
            l.ServiceId,
            l.IssuedAt,
            l.ExpiresAt,
            projectName = _projects.GetById(l.ProjectId)?.Name,
        });
        // Отсюда фронт узнаёт и про сам рубильник: отдельный флаг заводить незачем,
        // список всё равно запрашивается панелью
        return Ok(new
        {
            enabled = _external.Options.Enabled && _external.Options.IsConfigured,
            links,
        });
    }

    /// <summary>Отозвать одну ссылку. Доступ умирает на следующем же запросе поддомена.</summary>
    [HttpDelete("/api/preview/external-links/{jti}")]
    public IActionResult RevokeExternalLink(string jti)
    {
        if (!_links.Revoke(jti, UserId)) return NotFound(new { error = "Ссылка не найдена" });
        _log.LogInformation("Внешняя ссылка {Jti} отозвана владельцем", jti);
        return Ok(new { revoked = 1 });
    }

    /// <summary>Отозвать все свои ссылки разом — «закрыть всё, что торчит наружу».</summary>
    [HttpDelete("/api/preview/external-links")]
    public IActionResult RevokeAllExternalLinks()
    {
        var count = _links.RevokeAll(UserId);
        if (count > 0) _log.LogInformation("Отозвано внешних ссылок: {Count}", count);
        return Ok(new { revoked = count });
    }
}
