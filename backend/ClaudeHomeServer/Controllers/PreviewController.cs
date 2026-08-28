using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ClaudeHomeServer.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Controllers;

[ApiController]
[Authorize]
public class PreviewController : ControllerBase
{
    private readonly ProjectManager _projects;
    private readonly DevServerService _devServer;
    private readonly ProjectServiceDiscovery _discovery;
    private readonly LaunchConfigService _launch;
    private readonly ExternalPreviewRouter _external;
    private readonly DevServerPortMemory _portMemory;
    private readonly ExternalPreviewStore _links;
    private readonly JwtService _jwt;
    private readonly ILogger<PreviewController> _log;

    public PreviewController(ProjectManager projects, DevServerService devServer,
        ProjectServiceDiscovery discovery, LaunchConfigService launch,
        ExternalPreviewRouter external, ExternalPreviewStore links, JwtService jwt,
        DevServerPortMemory portMemory, ILogger<PreviewController> log)
    {
        _projects = projects;
        _devServer = devServer;
        _discovery = discovery;
        _launch = launch;
        _external = external;
        _portMemory = portMemory;
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

    /// <summary>Список запускаемых сервисов проекта (инференс из манифестов + сохранённые) с runtime-статусом.</summary>
    [HttpGet("/api/projects/{projectId}/services")]
    public async Task<IActionResult> Services(string projectId)
    {
        var project = OwnedProject(projectId);
        if (project is null) return Forbid();

        var discovered = await _discovery.DiscoverAsync(project);
        var running = _devServer.GetRunning(projectId, UserId).ToDictionary(r => r.ServiceId);
        var activeId = _devServer.GetActiveServiceId(projectId, UserId)
            ?? _devServer.GetActiveExternal(projectId)?.ServiceId;

        // Сервисы с известным портом, которых мы не запускали, пробуем на слух: порт
        // отвечает — значит сервис поднят снаружи (Rider, терминал, второй инстанс).
        // Скана слушающих портов машины при этом не делаем: щупаем только свои порты.
        var external = await ProbeExternalAsync(projectId, discovered, running);

        var services = new List<ServiceDto>();
        var covered = new HashSet<string>();
        var byId = discovered.ToDictionary(s => s.Id);
        foreach (var s in discovered)
        {
            covered.Add(s.Id);
            if (s.Members is { Length: > 0 })
            {
                services.Add(GroupDto(s, byId, running, external));
                continue;
            }
            running.TryGetValue(s.Id, out var run);
            var isExternal = run is null && external.ContainsKey(s.Id);
            services.Add(new ServiceDto(s.Id, s.Name, s.Source, s.Command, s.Args, s.Cwd,
                s.SuggestedPort, s.AutoPort, s.Saved,
                run?.Status ?? (isExternal ? "external" : "idle"),
                run?.Port ?? (isExternal ? external[s.Id] : null),
                run?.Error, s.Members));
        }
        // Запущенные сервисы, которых нет в инференсе (напр. кастомная разовая команда).
        foreach (var run in running.Values)
        {
            if (covered.Contains(run.ServiceId)) continue;
            services.Add(new ServiceDto(run.ServiceId, run.Name, "custom", "", [], null,
                null, false, false, run.Status, run.Port, run.Error));
        }

        return Ok(new { services, activeServiceId = activeId });
    }

    /// <summary>
    /// Статус группы — производная от участников: запущена, только когда живы все;
    /// «starting», пока поднимается хоть один; ошибка первого упавшего видна целиком.
    /// Порт берём у первого участника, которому есть что показать в превью.
    /// </summary>
    private static ServiceDto GroupDto(ProjectServiceInfo group, Dictionary<string, ProjectServiceInfo> byId,
        Dictionary<string, RunningServiceInfo> running, Dictionary<string, int> external)
    {
        var members = group.Members!.Where(byId.ContainsKey).ToArray();
        var states = members.Select(id =>
        {
            running.TryGetValue(id, out var run);
            return run?.Status ?? (external.ContainsKey(id) ? "external" : "idle");
        }).ToList();

        var status = states.Count == 0 ? "idle"
            : states.Any(s => s == "error") ? "error"
            : states.Any(s => s == "starting") ? "starting"
            // Все участники подняты вне продукта — группе тоже нечего запускать и останавливать,
            // а превью ей назначается внешним эндпоинтом: своего процесса в реестре у неё нет,
            // и обычный active вернул бы прокси пустоту («Dev-сервер не запущен»)
            : states.All(s => s == "external") ? "external"
            : states.All(s => s is "started" or "external") ? "started"
            // Часть поднята, часть нет — это не «запускается»: без своего статуса
            // группа выглядела бы вечно стартующей
            : states.Any(s => s is "started" or "external") ? "partial"
            : "idle";

        // Тем же правилом, что и резолв для превью: последний участник — это агрегатор
        var port = members
            .Reverse()
            .Select(id => running.TryGetValue(id, out var r) ? r.Port : (external.TryGetValue(id, out var ep) ? ep : (int?)null))
            .FirstOrDefault(p => p is > 0);
        var error = members
            .Select(id => running.TryGetValue(id, out var r) ? r.Error : null)
            .FirstOrDefault(e => !string.IsNullOrEmpty(e));

        return new ServiceDto(group.Id, group.Name, group.Source, group.Command, group.Args, group.Cwd,
            group.SuggestedPort, group.AutoPort, group.Saved, status, port, error, members);
    }

    /// <summary>
    /// Id сервисов, чей порт слушается кем-то со стороны. Пробуем параллельно и только
    /// те порты, которые вычислил discovery: чужие порты машины нас не касаются.
    /// </summary>
    private async Task<Dictionary<string, int>> ProbeExternalAsync(
        string projectId, List<ProjectServiceInfo> discovered, Dictionary<string, RunningServiceInfo> running)
    {
        // Порт берём из конфигурации, а если её там нет — из памяти прошлых запусков.
        // Второй источник нужен ровно после перезапуска продукта: реестр процессов пуст,
        // сами дев-серверы живы, и без него панель предложила бы запустить сервис поверх
        // собственного вчерашнего процесса (см. DevServerPortMemory).
        var candidates = discovered
            .Where(s => !running.ContainsKey(s.Id))
            .Select(s => (s.Id, Port: s.SuggestedPort is > 0 ? s.SuggestedPort : _portMemory.Get(projectId, s.Id)))
            .Where(c => c.Port is > 0)
            .ToList();
        if (candidates.Count == 0) return [];

        var results = await Task.WhenAll(candidates.Select(async c =>
            (c.Id, c.Port, Listening: await LoopbackResolver.IsListeningAsync(c.Port!.Value))));
        return results.Where(r => r.Listening).ToDictionary(r => r.Id, r => r.Port!.Value);
    }

    /// <summary>
    /// Показать в превью сервис, поднятый вне продукта.
    ///
    /// Порт НЕ принимается от клиента: он берётся из конфигурации сервиса этого проекта.
    /// Иначе эндпоинт превратился бы в туннель на любой localhost-порт хоста (соседний
    /// инстанс продукта, Dify, чужая админка) — под авторизацией владельца проекта.
    /// </summary>
    [HttpPost("/api/projects/{projectId}/preview/active-external")]
    public async Task<IActionResult> SetActiveExternal(string projectId, [FromBody] PreviewActiveRequest req)
    {
        var project = OwnedProject(projectId);
        if (project is null) return Forbid();
        if (string.IsNullOrWhiteSpace(req.ServiceId))
            return BadRequest(new { error = "serviceId не указан" });

        var known = await _discovery.DiscoverAsync(project);
        var svc = known.FirstOrDefault(s => s.Id == req.ServiceId);
        if (svc is null) return NotFound(new { error = "Сервис не найден" });

        // Правило выбора порта (в том числе у составной конфигурации) — одно на весь продукт
        var port = await _discovery.ResolvePortAsync(project, svc.Id);
        if (port is not > 0)
            return BadRequest(new { error = "У сервиса не задан порт — непонятно, где он слушает" });

        if (!await LoopbackResolver.IsListeningAsync(port.Value))
            return BadRequest(new { error = $"На порту {port} никто не слушает" });

        _devServer.SetActiveExternal(projectId, svc.Id, port.Value);
        _log.LogInformation("Проект {ProjectId}: превью указывает на внешний сервис {ServiceId} (:{Port})",
            projectId, svc.Id, port);
        return Ok(new { activeServiceId = svc.Id, port });
    }

    [HttpPost("/api/projects/{projectId}/preview/start")]
    public async Task<IActionResult> Start(string projectId, [FromBody] PreviewStartRequest req)
    {
        var project = OwnedProject(projectId);
        if (project is null) return Forbid();

        // Составной запуск: команды у группы нет, поднимаем каждого участника его
        // собственной конфигурацией. Порт возвращаем первого, кому есть что показать.
        if (!string.IsNullOrWhiteSpace(req.ServiceId))
        {
            var known = await _discovery.DiscoverAsync(project);
            var group = known.FirstOrDefault(s => s.Id == req.ServiceId && s.Members is { Length: > 0 });
            if (group != null) return await StartGroupAsync(projectId, group, known);
        }

        if (string.IsNullOrWhiteSpace(req.Command))
            return BadRequest(new { error = "Команда не указана" });

        var serviceId = string.IsNullOrWhiteSpace(req.ServiceId)
            ? "custom-" + Guid.NewGuid().ToString("N")[..8]
            : req.ServiceId!;
        var name = string.IsNullOrWhiteSpace(req.Name) ? req.Command : req.Name!;

        var result = await _devServer.StartAsync(projectId, UserId, serviceId, name,
            req.Command, req.Args ?? [], req.Cwd, req.Port, req.AutoPort, req.Env);
        return Ok(new { status = result.Status, port = result.Port, error = result.Error, serviceId });
    }

    private async Task<IActionResult> StartGroupAsync(string projectId, ProjectServiceInfo group,
        List<ProjectServiceInfo> known)
    {
        var byId = known.ToDictionary(s => s.Id);
        var results = new List<(string Id, DevServerStartResult Result)>();

        // Последовательно, а не параллельно: у составных конфигураций порядок осмыслен
        // (в Rider следующий шаг ждёт порта предыдущего), да и лог старта читается ровнее
        foreach (var id in group.Members!)
        {
            if (!byId.TryGetValue(id, out var member)) continue;
            // Участник уже поднят снаружи — запускать нечего, иначе упрёмся в занятый порт
            if (member.SuggestedPort is > 0 &&
                _devServer.GetRunning(projectId, UserId).All(r => r.ServiceId != id) &&
                await LoopbackResolver.IsListeningAsync(member.SuggestedPort.Value))
            {
                _devServer.SetActiveExternal(projectId, id, member.SuggestedPort.Value);
                continue;
            }
            results.Add((id, await _devServer.StartAsync(projectId, UserId, id, member.Name,
                member.Command, member.Args, member.Cwd, member.SuggestedPort, member.AutoPort, member.Env)));
        }

        var failed = results.FirstOrDefault(r => !r.Result.Success);
        var port = results.Select(r => r.Result.Port).FirstOrDefault(p => p is > 0);
        return Ok(new
        {
            status = failed.Id != null ? "error" : results.Count == 0 ? "idle" : "started",
            port,
            error = failed.Result?.Error,
            serviceId = group.Id,
        });
    }

    [HttpPost("/api/projects/{projectId}/preview/stop")]
    public async Task<IActionResult> Stop(string projectId, [FromBody] PreviewStopRequest? req)
    {
        var project = OwnedProject(projectId);
        if (project is null) return Forbid();
        if (string.IsNullOrWhiteSpace(req?.ServiceId))
            return BadRequest(new { error = "serviceId не указан" });

        // Группу останавливаем целиком: её участники — обычные сервисы реестра
        var group = (await _discovery.DiscoverAsync(project))
            .FirstOrDefault(s => s.Id == req.ServiceId && s.Members is { Length: > 0 });
        foreach (var id in group?.Members ?? [req.ServiceId!])
            await _devServer.StopAsync(projectId, UserId, id);

        return Ok(new { status = "stopped" });
    }

    [HttpGet("/api/projects/{projectId}/preview/status")]
    public IActionResult Status(string projectId)
    {
        if (OwnedProject(projectId) is null) return Forbid();
        var running = _devServer.GetRunning(projectId, UserId);
        var activeId = _devServer.GetActiveServiceId(projectId, UserId);
        return Ok(new { running, activeServiceId = activeId });
    }

    /// <summary>Назначить активный для превью сервис (на его порт указывает iframe).</summary>
    [HttpPost("/api/projects/{projectId}/preview/active")]
    public IActionResult SetActive(string projectId, [FromBody] PreviewActiveRequest req)
    {
        if (OwnedProject(projectId) is null) return Forbid();
        if (string.IsNullOrWhiteSpace(req.ServiceId))
            return BadRequest(new { error = "serviceId не указан" });
        _devServer.SetActivePreview(projectId, req.ServiceId);
        return Ok(new { activeServiceId = req.ServiceId });
    }

    /// <summary>Прочитать .claude/launch.json проекта.</summary>
    [HttpGet("/api/projects/{projectId}/launch-config")]
    public async Task<IActionResult> GetLaunchConfig(string projectId)
    {
        var project = OwnedProject(projectId);
        if (project is null) return Forbid();
        var configs = await _launch.ReadAsync(project);
        return Ok(new { configurations = configs });
    }

    /// <summary>Записать .claude/launch.json проекта.</summary>
    [HttpPut("/api/projects/{projectId}/launch-config")]
    public async Task<IActionResult> PutLaunchConfig(string projectId, [FromBody] LaunchConfigPutRequest req)
    {
        var project = OwnedProject(projectId);
        if (project is null) return Forbid();
        await _launch.WriteAsync(project, req.Configurations ?? []);
        _discovery.Invalidate(projectId);
        return Ok(new { configurations = req.Configurations ?? [] });
    }
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

public record ServiceDto(
    string Id, string Name, string Source, string Command, string[] Args, string? Cwd,
    int? SuggestedPort, bool AutoPort, bool Saved,
    string Status, int? RunningPort, string? Error,
    // Составной запуск: id входящих сервисов (у обычного сервиса — null)
    string[]? Members = null);

public record PreviewStartRequest(
    string Command, string[]? Args = null, int? Port = null,
    string? ServiceId = null, string? Name = null, string? Cwd = null,
    bool AutoPort = false, Dictionary<string, string>? Env = null);

public record PreviewStopRequest(string? ServiceId);
public record PreviewActiveRequest(string ServiceId);
public record LaunchConfigPutRequest(List<LaunchConfigEntry>? Configurations);
