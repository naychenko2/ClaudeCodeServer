using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Composition;

namespace ClaudeHomeServer.Services.ProjectServices;

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
/// <summary>Остановка процесса, поднятого вне продукта. Confirm — согласие гасить чужой.</summary>
public record StopExternalRequest(string? ServiceId, bool Confirm = false);
public record PreviewActiveRequest(string ServiceId);
public record LaunchConfigPutRequest(List<LaunchConfigEntry>? Configurations);

/// <summary>Ответ маршрута раздела «Сервисы»: код и тело. Хост переводит его в свой тип ответа.</summary>
public sealed record ProjectServicesResult(int Status, object? Body)
{
    public static ProjectServicesResult Ok(object body) => new(200, body);
    public static ProjectServicesResult Error(int status, string message) => new(status, new { error = message });
}

/// <summary>
/// Маршруты раздела «Сервисы проекта» без привязки к хосту (ADR-016, задача 4.3): их
/// обслуживают две стороны — серверный <c>PreviewController</c> и localhost-API агента
/// устройства. Проверку владельца и отказ G1 делает хост, всё остальное — здесь, один раз.
/// Файлы проекта — через <paramref name="files"/> методов: null — папка на этой машине
/// напрямую (сервер), у агента — его шов с корнями машины и сверкой дескриптора.
/// </summary>
public sealed class ProjectServicesApi(
    DevServerService devServer,
    ProjectServiceDiscovery discovery,
    LaunchConfigService launch,
    DevServerPortMemory portMemory,
    ILogger<ProjectServicesApi> log)
{
    /// <summary>Список запускаемых сервисов проекта (инференс из манифестов + сохранённые) с runtime-статусом.</summary>
    public async Task<ProjectServicesResult> ServicesAsync(Project project, string userId, IProjectFiles? files = null)
    {
        var discovered = await discovery.DiscoverAsync(project, files);
        var running = devServer.GetRunning(project.Id, userId).ToDictionary(r => r.ServiceId);
        var activeId = devServer.GetActiveServiceId(project.Id, userId)
            ?? devServer.GetActiveExternal(project.Id)?.ServiceId;

        // Сервисы с известным портом, которых мы не запускали, пробуем на слух: порт
        // отвечает — значит сервис поднят снаружи (Rider, терминал, второй инстанс).
        // Скана слушающих портов машины при этом не делаем: щупаем только свои порты.
        var external = await ProbeExternalAsync(project.Id, discovered, running);

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

        return ProjectServicesResult.Ok(new { services, activeServiceId = activeId });
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
            .Select(s => (s.Id, Port: s.SuggestedPort is > 0 ? s.SuggestedPort : portMemory.Get(projectId, s.Id)))
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
    /// Иначе эндпоинт превратился бы в туннель на любой localhost-порт машины (соседний
    /// инстанс продукта, Dify, чужая админка) — под авторизацией владельца проекта.
    /// </summary>
    public async Task<ProjectServicesResult> SetActiveExternalAsync(Project project, PreviewActiveRequest req, IProjectFiles? files = null)
    {
        if (string.IsNullOrWhiteSpace(req.ServiceId))
            return ProjectServicesResult.Error(400, "serviceId не указан");

        var known = await discovery.DiscoverAsync(project, files);
        var svc = known.FirstOrDefault(s => s.Id == req.ServiceId);
        if (svc is null) return ProjectServicesResult.Error(404, "Сервис не найден");

        // Правило выбора порта (в том числе у составной конфигурации) — одно на весь продукт
        var port = await discovery.ResolvePortAsync(project, svc.Id, files);
        if (port is not > 0)
            return ProjectServicesResult.Error(400, "У сервиса не задан порт — непонятно, где он слушает");

        if (!await LoopbackResolver.IsListeningAsync(port.Value))
            return ProjectServicesResult.Error(400, $"На порту {port} никто не слушает");

        devServer.SetActiveExternal(project.Id, svc.Id, port.Value);
        log.LogInformation("Проект {ProjectId}: превью указывает на внешний сервис {ServiceId} (:{Port})",
            project.Id, svc.Id, port);
        return ProjectServicesResult.Ok(new { activeServiceId = svc.Id, port });
    }

    /// <param name="cwdAllowed">Проверка рабочего каталога сверх лексической (у агента — реальный
    /// путь под корнями машины: симлинк наружу лексика пропускает). Отказ — тот же ответ, что
    /// у недопустимого каталога в <see cref="DevServerService"/>.</param>
    public async Task<ProjectServicesResult> StartAsync(Project project, string userId, PreviewStartRequest req,
        IProjectFiles? files = null, Func<string?, bool>? cwdAllowed = null)
    {
        // Составной запуск: команды у группы нет, поднимаем каждого участника его
        // собственной конфигурацией. Порт возвращаем первого, кому есть что показать.
        if (!string.IsNullOrWhiteSpace(req.ServiceId))
        {
            var known = await discovery.DiscoverAsync(project, files);
            var group = known.FirstOrDefault(s => s.Id == req.ServiceId && s.Members is { Length: > 0 });
            if (group != null) return await StartGroupAsync(project.Id, userId, group, known, cwdAllowed);
        }

        if (string.IsNullOrWhiteSpace(req.Command))
            return ProjectServicesResult.Error(400, "Команда не указана");

        var serviceId = string.IsNullOrWhiteSpace(req.ServiceId)
            ? "custom-" + Guid.NewGuid().ToString("N")[..8]
            : req.ServiceId!;
        var name = string.IsNullOrWhiteSpace(req.Name) ? req.Command : req.Name!;

        var result = cwdAllowed?.Invoke(req.Cwd) == false ? BadCwd : await devServer.StartAsync(project.Id, userId, serviceId, name,
            req.Command, req.Args ?? [], req.Cwd, req.Port, req.AutoPort, req.Env);
        return ProjectServicesResult.Ok(new { status = result.Status, port = result.Port, error = result.Error, serviceId });
    }

    private static readonly DevServerStartResult BadCwd = new(false, null, "error", "Недопустимый рабочий каталог");

    private async Task<ProjectServicesResult> StartGroupAsync(string projectId, string userId, ProjectServiceInfo group,
        List<ProjectServiceInfo> known, Func<string?, bool>? cwdAllowed)
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
                devServer.GetRunning(projectId, userId).All(r => r.ServiceId != id) &&
                await LoopbackResolver.IsListeningAsync(member.SuggestedPort.Value))
            {
                devServer.SetActiveExternal(projectId, id, member.SuggestedPort.Value);
                continue;
            }
            results.Add((id, cwdAllowed?.Invoke(member.Cwd) == false ? BadCwd : await devServer.StartAsync(projectId, userId, id, member.Name,
                member.Command, member.Args, member.Cwd, member.SuggestedPort, member.AutoPort, member.Env)));
        }

        var failed = results.FirstOrDefault(r => !r.Result.Success);
        var port = results.Select(r => r.Result.Port).FirstOrDefault(p => p is > 0);
        return ProjectServicesResult.Ok(new
        {
            status = failed.Id != null ? "error" : results.Count == 0 ? "idle" : "started",
            port,
            error = failed.Result?.Error,
            serviceId = group.Id,
        });
    }

    public async Task<ProjectServicesResult> StopAsync(Project project, string userId, PreviewStopRequest? req, IProjectFiles? files = null)
    {
        if (string.IsNullOrWhiteSpace(req?.ServiceId))
            return ProjectServicesResult.Error(400, "serviceId не указан");

        // Группу останавливаем целиком: её участники — обычные сервисы реестра
        var group = (await discovery.DiscoverAsync(project, files))
            .FirstOrDefault(s => s.Id == req.ServiceId && s.Members is { Length: > 0 });
        foreach (var id in group?.Members ?? [req.ServiceId!])
            await devServer.StopAsync(project.Id, userId, id);

        return ProjectServicesResult.Ok(new { status = "stopped" });
    }

    /// <summary>
    /// Остановить сервис, поднятый ВНЕ продукта (или переживший его перезапуск).
    ///
    /// Штатный «Стоп» такому не годится: своего объекта процесса у нас нет. Поэтому ищем
    /// владельца порта и гасим его — но с разбором, чей он:
    ///
    /// свой осиротевший (PID совпал с запомненным при запуске) гасится сразу, посторонний —
    /// только с подтверждением человека. Разница существенная: на порту может оказаться не
    /// дев-сервер, а docker-proxy или чужая служба, и убивать такое молча нельзя.
    /// </summary>
    public async Task<ProjectServicesResult> StopExternalAsync(Project project, string userId, StopExternalRequest req,
        IProjectFiles? files = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(req.ServiceId))
            return ProjectServicesResult.Error(400, "serviceId не указан");

        var port = await ResolveServicePortAsync(project, req.ServiceId, userId, files);
        if (port is not > 0)
            return ProjectServicesResult.Error(400, "Не удалось определить порт сервиса");

        var owner = PortOwnerLookup.Find(port.Value);
        if (owner is null)
            return ProjectServicesResult.Error(400, $"Не удалось определить процесс на порту {port}");

        // Самоубийство хоста выглядело бы как «кнопка гасит CCS» — такого не делаем
        if (owner.Pid == Environment.ProcessId)
            return ProjectServicesResult.Error(400, "Этот порт слушает сам ClaudeCodeServer");

        var remembered = portMemory.GetRun(project.Id, req.ServiceId);
        // Свой — только когда СОВПАЛИ и процесс, и порт: номера процессов система выдаёт
        // повторно, и одного PID мало, чтобы считать процесс нашим
        var isOurs = remembered is not null && remembered.Pid == owner.Pid && remembered.Port == port.Value;

        if (!isOurs && !req.Confirm)
        {
            return new ProjectServicesResult(409, new
            {
                needsConfirm = true,
                pid = owner.Pid,
                processName = owner.ProcessName,
                port = port.Value,
                error = "Порт держит процесс, который продукт не запускал",
            });
        }

        try
        {
            var process = System.Diagnostics.Process.GetProcessById(owner.Pid);
            // Дерево целиком: dev-серверы почти всегда поднимают детей (node → vite → esbuild),
            // и смерть одного лишь родителя оставила бы порт занятым
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(ct);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Не удалось остановить процесс {Pid} на порту {Port}", owner.Pid, port);
            return ProjectServicesResult.Error(500, $"Не удалось остановить процесс: {ex.Message}");
        }

        portMemory.Forget(project.Id, req.ServiceId);
        log.LogInformation("Проект {ProjectId}: остановлен внешний процесс {Pid} ({Name}) на порту {Port}",
            project.Id, owner.Pid, owner.ProcessName ?? "?", port);
        return ProjectServicesResult.Ok(new { status = "stopped", pid = owner.Pid });
    }

    public ProjectServicesResult Status(Project project, string userId)
    {
        var running = devServer.GetRunning(project.Id, userId);
        var activeId = devServer.GetActiveServiceId(project.Id, userId);
        return ProjectServicesResult.Ok(new { running, activeServiceId = activeId });
    }

    /// <summary>Назначить активный для превью сервис (на его порт указывает iframe).</summary>
    public ProjectServicesResult SetActive(Project project, PreviewActiveRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.ServiceId))
            return ProjectServicesResult.Error(400, "serviceId не указан");
        devServer.SetActivePreview(project.Id, req.ServiceId);
        return ProjectServicesResult.Ok(new { activeServiceId = req.ServiceId });
    }

    /// <summary>Прочитать .claude/launch.json проекта.</summary>
    public async Task<ProjectServicesResult> GetLaunchConfigAsync(Project project, IProjectFiles? files = null) =>
        ProjectServicesResult.Ok(new { configurations = await launch.ReadAsync(project, files) });

    /// <summary>Записать .claude/launch.json проекта.</summary>
    public async Task<ProjectServicesResult> PutLaunchConfigAsync(Project project, LaunchConfigPutRequest req, IProjectFiles? files = null)
    {
        await launch.WriteAsync(project, req.Configurations ?? [], files);
        discovery.Invalidate(project.Id);
        return ProjectServicesResult.Ok(new { configurations = req.Configurations ?? [] });
    }

    /// <summary>
    /// Порт сервиса для превью: живой процесс важнее конфигурации (автопорт и порт из
    /// вывода в манифестах не значатся), затем конфигурация, затем память прошлых запусков.
    /// У составной конфигурации своего порта нет: идём по участникам с конца — в multilaunch
    /// зависимости поднимаются первыми, а приложение-агрегатор ждёт их и стоит в конце.
    /// Правило одно на весь продукт: «показать снаружи», «остановить чужой» и «показать в
    /// панели» не имеют права указывать на разные порты.
    /// </summary>
    public Task<int?> ResolveServicePortAsync(Project project, string serviceId, string userId, IProjectFiles? files = null) =>
        ResolveServicePortAsync(discovery, devServer, portMemory, project, serviceId, userId, files);

    internal static async Task<int?> ResolveServicePortAsync(ProjectServiceDiscovery discovery, DevServerService devServer,
        DevServerPortMemory portMemory, Project project, string serviceId, string userId, IProjectFiles? files = null)
    {
        var known = await discovery.DiscoverAsync(project, files);
        var svc = known.FirstOrDefault(s => s.Id == serviceId);
        if (svc is null) return null;

        var ids = svc.Members is { Length: > 0 } ? svc.Members.Reverse() : [svc.Id];
        var byId = known.ToDictionary(s => s.Id);
        foreach (var id in ids)
        {
            if (devServer.GetRunningPort(project.Id, id, userId) is > 0 and var running) return running;
            if (byId.TryGetValue(id, out var member) && member.SuggestedPort is > 0) return member.SuggestedPort;
            if (portMemory.Get(project.Id, id) is > 0 and var remembered) return remembered;
        }
        return null;
    }
}
