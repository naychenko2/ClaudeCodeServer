using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.ProjectServices;
using ClaudeHomeServer.Services.TestRuns;

namespace ClaudeHomeServer.Services.Mcp.Http;

/// <summary>
/// Дев-стенд из чата (start_stand / stop_stand тулсета dev, docs/research/build-stand-progress-2026-10.md,
/// этап 5). Стенд — запись того же реестра <see cref="DevServerService"/>, что у панели «Сервисы»:
/// живёт после хода, виден в панели (логи, превью, «Стоп»), гасится stop_stand, кнопкой, уборщиком
/// и остановкой бэкенда — второго реестра и второго механизма гашения нет.
///
/// Подъём: очередь → сборка (движки вертикали TestRuns: общий конвейер фаз, блокировка дерева,
/// приватные узлы MSBuild) → запуск БЕЗ сборки (<c>dotnet run --no-build</c>) → готов (порт
/// принимает TCP и, если задан health_path, отвечает по HTTP). «Стоп» хода до «готов» гасит и
/// сборку, и стартующий процесс; после «готов» стенд от хода не зависит.
///
/// Склейка двух вертикалей (TestRuns и ProjectServices) — здесь, в Main: друг на друга они не
/// ссылаются (ADR-014). Что запускать, берётся из конфигурации сервиса проекта (манифесты и
/// .claude/launch.json — код репозитория), от модели — только id сервиса или путь к .csproj
/// внутри дерева, порт из 55xx–56xx и путь пробы.
/// </summary>
internal sealed partial class DevStand(
    ProjectRunCaller caller,
    DevServerService? devServer,
    ProjectServiceDiscovery? discovery,
    DotnetBuildService? dotnet,
    NpmBuildService? npm,
    int ceilingSeconds)
{
    // Ожидание порта после сборки: запуск собранного стенда — секунды (замер — 2,3 с), 120 с — запас
    internal static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(120);
    // Меньше этого на запуск не остаётся смысла: отказ «повтори», а не заведомый таймаут
    internal static readonly TimeSpan MinReadyBudget = TimeSpan.FromSeconds(5);
    // Проба health_path: сколько ждать HTTP-ответа после того, как порт начал принимать TCP
    internal static readonly TimeSpan HealthTimeout = TimeSpan.FromSeconds(30);

    private static readonly HttpClient Probe = new(new SocketsHttpHandler
    {
        // Стенд — на loopback: прокси машины ему не нужен и запрос туда не уводит
        UseProxy = false,
        AllowAutoRedirect = false,
    })
    {
        Timeout = TimeSpan.FromSeconds(5),
    };

    public bool Available => devServer is not null && discovery is not null && dotnet is not null && npm is not null;

    // План подъёма: чем запускать и что собрать перед этим
    internal sealed record Plan(string Command, string[] Args, StandBuild? Build);

    // Сборка перед запуском: dotnet build цели или npm run build каталога
    internal sealed record StandBuild(bool IsNpm, string Target);

    public async Task<McpToolCallResult> StartAsync(Session session, Project project, string ownerId,
        JsonObject arguments, string? toolUseId, CancellationToken ct)
    {
        if (!Available)
            return ProjectRunCaller.Deny("Стенды из чата выключены на этом сервере — поднимай панелью «Сервисы».");
        var root = ProjectRunCaller.RootOf(session, project);
        if (!TryReadPort(arguments, out var port, out var error)) return ProjectRunCaller.Deny(error);
        var healthPath = ProjectRunCaller.StringArg(arguments, "health_path");
        if (healthPath is not null && !IsHealthPath(healthPath))
            return ProjectRunCaller.Deny("health_path — путь на стенде вида /health: латиница, цифры и «/._~%?=&-», "
                + "с одного «/» в начале, до 200 символов.");

        var resolved = await ResolveServiceAsync(project, root, ProjectRunCaller.StringArg(arguments, "service"));
        if (resolved.Error is not null) return ProjectRunCaller.Deny(resolved.Error);
        var svc = resolved.Service!;
        var id = StandId(svc.Id, root, project.RootPath);

        // Уже поднят — тот же адрес сразу, без сборки: повторный вызов не плодит второй стенд
        var running = devServer!.GetRunning(project.Id, ownerId).FirstOrDefault(r => r.ServiceId == id);
        if (running is { Status: "started", Port: { } livePort })
            return new McpToolCallResult(ReadyText(running.Name, id, livePort, elapsed: null)
                + (port is { } asked && asked != livePort
                    ? $"\nПорт {asked} не применён: стенд уже слушает {livePort} — чтобы сменить, сначала stop_stand."
                    : ""));
        if (running is { Status: "starting" })
            return ProjectRunCaller.Deny($"Стенд «{running.Name}» уже поднимается (другой вызов или кнопка панели) — "
                + "повтори start_stand через минуту, он вернёт его адрес.");

        if (!TryPlan(svc, root, out var plan, out error)) return ProjectRunCaller.Deny(error);
        if (port is not null && devServer.LaunchesSandboxed(project))
            return ProjectRunCaller.Deny("В песочнице порт стенда выдаётся из её пула — вызови start_stand без port.");

        var name = root == project.RootPath ? svc.Name : $"{svc.Name} · {Path.GetFileName(root)}";
        var origin = new StandOrigin(session.Id, OriginLabel(session));
        return await RaiseAsync(session.Id, toolUseId, project, ownerId, root, id, name, svc, plan, port,
            healthPath, origin, ct);
    }

    private async Task<McpToolCallResult> RaiseAsync(string sessionId, string? toolUseId, Project project,
        string ownerId, string root, string id, string name, ProjectServiceInfo svc, Plan plan, int? port,
        string? healthPath, StandOrigin origin, CancellationToken ct)
    {
        var stages = new TestRunStages(() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        void Progress(TestRunProgress p) => caller.SendProgress(sessionId, toolUseId, p, stages);
        var watch = Stopwatch.StartNew();
        var ceiling = TimeSpan.FromSeconds(ceilingSeconds);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(ceiling);
        var failed = true;
        // Стенд поднят этим вызовом, но ещё не отдан модели: любой выход мимо «готов» (отмена,
        // исключение) обязан его погасить — иначе в реестре остаётся «started», о котором никто не знает
        var raised = false;

        // Отмена человеком («Стоп» хода) и срабатывание потолка вызова — разные причины и разные тексты
        string? Cancelled(string what) =>
            ct.IsCancellationRequested ? $"Подъём стенда остановлен («Стоп»): {what}."
            : limit.IsCancellationRequested ? $"Подъём стенда упёрся в потолок вызова ({ceilingSeconds} с): {what}. "
                + "Повтори start_stand — сборка будет инкрементальной."
            : null;

        try
        {
            if (plan.Build is { } build)
            {
                var built = build.IsNpm
                    ? await npm!.RunAsync(new NpmBuildRequest(project, root, sessionId, build.Target), Progress, limit.Token)
                    : await dotnet!.RunAsync(new DotnetBuildRequest(project, root, sessionId, build.Target), Progress, limit.Token);
                if (built.Refusal is not null || DevToolset.EndedBadly(built))
                {
                    if (Cancelled("сборка прервана") is { } stopped) return ProjectRunCaller.Deny(stopped);
                    var summary = build.IsNpm ? npm!.FormatResult(built) : dotnet!.FormatResult(built);
                    return ProjectRunCaller.Deny("Стенд не поднят: сборка не прошла.\n" + summary);
                }
            }

            var budget = ceiling - watch.Elapsed;
            if (budget < MinReadyBudget)
                return ProjectRunCaller.Deny("Стенд не поднят: сборка съела потолок вызова. Повтори start_stand — "
                    + "следующая сборка будет инкрементальной.");
            Progress(new TestRunProgress("start", "запуск · жду порт"));
            var started = await devServer!.StartAsync(project.Id, ownerId, id, name, plan.Command, plan.Args, svc.Cwd,
                port, autoPort: port is null, svc.Env, budget < ReadyTimeout ? budget : ReadyTimeout,
                root, origin, limit.Token);
            // Тот же сервис уже поднимается параллельно (другой вызов, кнопка панели): реестр отдал
            // «starting» без порта — это не провал, а «повтори позже»
            if (started is { Success: true, Status: "starting" })
                return ProjectRunCaller.Deny($"Стенд «{name}» уже поднимается (другой вызов или кнопка панели) — "
                    + "повтори start_stand через минуту, он вернёт его адрес.");
            if (!started.Success || started.Port is not { } live)
                return ProjectRunCaller.Deny(Cancelled("процесс погашен") ?? $"Стенд не поднялся: {started.Error}");
            raised = true;

            if (healthPath is not null)
            {
                // Проба входит в общий бюджет вызова: потолок не перевалить
                var left = ceiling - watch.Elapsed;
                var healthBudget = left < HealthTimeout ? left : HealthTimeout;
                if (!await WaitHealthyAsync(live, healthPath, healthBudget, limit.Token))
                {
                    await devServer.StopAsync(project.Id, ownerId, id);
                    raised = false;
                    return ProjectRunCaller.Deny(Cancelled("стенд погашен")
                        ?? $"Стенд слушает порт {live}, но {healthPath} не ответил по HTTP за "
                           + $"{Math.Max(0, (int)healthBudget.TotalSeconds)} с — стенд погашен, лог смотри в панели «Сервисы».");
                }
            }

            Progress(new TestRunProgress("ready", $"готов: http://127.0.0.1:{live}"));
            failed = false;
            return new McpToolCallResult(ReadyText(name, id, live, watch.Elapsed));
        }
        catch (OperationCanceledException) when (Cancelled("процесс погашен") is { } stopped)
        {
            return ProjectRunCaller.Deny(stopped);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Сбой запуска (нет dotnet/node, отказ среды) — честный текст модели, а не 500 транспорта
            return ProjectRunCaller.Deny($"Не удалось поднять стенд: {e.Message}");
        }
        finally
        {
            if (failed && raised)
            {
                try { await devServer!.StopAsync(project.Id, ownerId, id); }
                catch (Exception) { /* гашение — страховка: исход вызова уже определён выше */ }
            }
            caller.SendFinal(sessionId, toolUseId, stages, failed, totals: null);
        }
    }

    public async Task<McpToolCallResult> StopAsync(Session session, Project project, string ownerId,
        JsonObject arguments)
    {
        if (!Available)
            return ProjectRunCaller.Deny("Стенды из чата выключены на этом сервере — гаси панелью «Сервисы».");
        var raw = ProjectRunCaller.StringArg(arguments, "service");
        var running = devServer!.GetRunning(project.Id, ownerId);
        // Точный id записи реестра (как его вернул start_stand) — без резолва конфигурации
        var target = running.FirstOrDefault(r => r.ServiceId == raw);
        if (target is null)
        {
            var root = ProjectRunCaller.RootOf(session, project);
            var resolved = await ResolveServiceAsync(project, root, raw);
            if (resolved.Error is not null) return ProjectRunCaller.Deny(resolved.Error);
            var id = StandId(resolved.Service!.Id, root, project.RootPath);
            target = running.FirstOrDefault(r => r.ServiceId == id);
            if (target is null)
                return new McpToolCallResult($"Стенд «{resolved.Service.Name}» не поднят продуктом — гасить нечего. "
                    + "Процессы, запущенные вне продукта, stop_stand не трогает.");
        }
        await devServer.StopAsync(project.Id, ownerId, target.ServiceId);
        return new McpToolCallResult($"Стенд «{target.Name}» (сервис {target.ServiceId}) погашен.");
    }

    private sealed record Resolved(ProjectServiceInfo? Service, string? Error);

    // Сервис по id из «Сервисов» или dotnet-проект по пути к .csproj ОТНОСИТЕЛЬНО дерева
    private async Task<Resolved> ResolveServiceAsync(Project project, string root, string? raw)
    {
        var known = await discovery!.DiscoverAsync(project);
        if (string.IsNullOrWhiteSpace(raw))
            return new(null, "service обязателен: id сервиса из панели «Сервисы» или путь к .csproj. " + ListServices(known));
        if (known.FirstOrDefault(s => s.Id == raw) is { } found)
            return found.Members is { Length: > 0 }
                ? new(null, $"«{found.Name}» — составной запуск: start_stand поднимает по одному сервису, "
                    + "назови участника или подними группу панелью «Сервисы».")
                : new(found, null);
        if (!raw.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            return new(null, $"Сервис «{raw}» не найден. " + ListServices(known));
        if (!TestsToolset.TryResolveInside(root, raw, "dotnet run", out var full, out var relative, out var error))
            return new(null, error);
        if (!File.Exists(full))
            return new(null, $"Проект «{relative}» не найден в дереве чата.");
        return new(new ProjectServiceInfo(
            Id: "stand-" + Slug(relative),
            Name: Path.GetFileNameWithoutExtension(relative),
            Source: "dotnet",
            Command: "dotnet",
            Args: ["run", "--project", relative],
            Cwd: null,
            SuggestedPort: null,
            AutoPort: true,
            Saved: false), null);
    }

    private static string ListServices(List<ProjectServiceInfo> known)
    {
        var ids = known.Where(s => s.Members is not { Length: > 0 }).Take(20)
            .Select(s => $"{s.Id} ({s.Name})").ToList();
        return ids.Count == 0
            ? "Сервисов у проекта не найдено — укажи путь к .csproj."
            : "Доступные сервисы: " + string.Join("; ", ids) + ".";
    }

    // Что запускать и что собрать перед этим. Поддерживаются dotnet run и скрипты npm/pnpm/yarn;
    // прочее (docker compose, make, node-скрипты) — панелью: сборку им продукт не делает
    internal static bool TryPlan(ProjectServiceInfo svc, string root, out Plan plan, out string error)
    {
        plan = null!;
        error = "";
        var tool = Path.GetFileNameWithoutExtension(svc.Command.Trim().Trim('"')).ToLowerInvariant();
        if (tool == "dotnet")
        {
            if (svc.Args is not ["run", ..])
            {
                error = $"«{svc.Name}» запускается не через `dotnet run` ({string.Join(' ', svc.Args)}) — "
                    + "start_stand умеет только dotnet run и скрипты npm; этот сервис подними панелью «Сервисы».";
                return false;
            }
            // Цель сборки — проект запуска: --project (относительно cwd сервиса), иначе сам cwd
            var project = ProjectArg(svc.Args);
            var raw = project is null ? svc.Cwd ?? "."
                : svc.Cwd is null ? project : svc.Cwd.TrimEnd('/') + "/" + project;
            if (!DevToolset.TryResolveDotnetTarget(root, raw, out var target, out error)) return false;
            string[] args = svc.Args.Contains("--no-build")
                ? svc.Args
                : ["run", "--no-build", .. svc.Args[1..]];
            plan = new Plan(svc.Command, args, new StandBuild(false, target ?? ""));
            return true;
        }
        if (tool is "npm" or "pnpm" or "yarn")
        {
            var script = tool == "npm"
                ? svc.Args is ["run", var named, ..] ? named : null
                : svc.Args.FirstOrDefault();
            if (script is null || !NpmBuildService.IsScriptName(script))
            {
                error = $"У «{svc.Name}» не разобрать имя скрипта ({string.Join(' ', svc.Args)}) — подними его панелью «Сервисы».";
                return false;
            }
            // Дев-сервер собирает сам; preview раздаёт готовую сборку — её делаем перед запуском
            // (движок сборки — только npm)
            StandBuild? build = null;
            if (tool == "npm" && script == "preview")
            {
                if (!DevToolset.TryResolveNpmTarget(root, svc.Cwd ?? ".", out var target, out error)) return false;
                build = new StandBuild(true, target ?? "");
            }
            plan = new Plan(svc.Command, svc.Args, build);
            return true;
        }
        error = $"«{svc.Name}» запускается через {svc.Command} — start_stand умеет только dotnet run и скрипты "
            + "npm/pnpm/yarn; этот сервис подними панелью «Сервисы».";
        return false;
    }

    private static string? ProjectArg(string[] args)
    {
        for (var i = 1; i < args.Length; i++)
        {
            if (args[i] == "--") break;
            if (args[i] is "--project" or "-p" && i + 1 < args.Length) return args[i + 1];
            if (args[i].StartsWith("--project=", StringComparison.Ordinal)) return args[i]["--project=".Length..];
        }
        return null;
    }

    // Стенд из worktree — своя запись реестра: тот же сервис корня проекта (панель, соседний чат)
    // он не подменяет и не гасит. Суффикс — хеш пути дерева, стабильный между вызовами
    internal static string StandId(string serviceId, string root, string projectRoot)
    {
        static string Norm(string p) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(p));
        var tree = Norm(root);
        if (string.Equals(tree, Norm(projectRoot), StringComparison.Ordinal)) return serviceId;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(tree)))[..6].ToLowerInvariant();
        return $"{serviceId}-wt-{hash}";
    }

    internal static string OriginLabel(Session session)
    {
        var title = string.IsNullOrWhiteSpace(session.Name) ? "без названия" : session.Name.Trim();
        if (title.Length > 60) title = title[..60] + "…";
        return $"чат «{title}»";
    }

    internal static bool TryReadPort(JsonObject arguments, out int? port, out string error)
    {
        port = null;
        error = "";
        var node = arguments["port"];
        if (node is null) return true;
        if (node is not JsonValue value || !value.TryGetValue<int>(out var number))
        {
            error = "port — целое число из диапазона стендов 5500–5699 (или не указывай — выдам свободный).";
            return false;
        }
        if (DevServerService.StandPortRefusal(number) is { } refusal)
        {
            error = refusal;
            return false;
        }
        port = number;
        return true;
    }

    // Путь пробы: только путь на стенде — ни схемы, ни хоста («//host» уводил бы запрос с loopback)
    internal static bool IsHealthPath(string path) =>
        path.Length <= 200 && !path.StartsWith("//", StringComparison.Ordinal) && HealthPathPattern().IsMatch(path);

    [GeneratedRegex(@"^/[A-Za-z0-9._~%/?=&-]*$", RegexOptions.None, matchTimeoutMilliseconds: 100)]
    private static partial Regex HealthPathPattern();

    // Любой HTTP-ответ, включая 401/404, — стенд жив (как проба стенда у run_tests). Отмена
    // («Стоп», потолок) — не исключение, а false: гасит стенд и пишет текст вызывающий
    internal static async Task<bool> WaitHealthyAsync(int port, string path, TimeSpan timeout, CancellationToken ct)
    {
        var url = new Uri($"http://127.0.0.1:{port}{path}");
        if (url.Host != "127.0.0.1" || url.Port != port) return false;
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            try
            {
                using var response = await Probe.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                return true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return false; }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { }
            try { await Task.Delay(1000, ct); }
            catch (OperationCanceledException) { }
        }
        return false;
    }

    private static string ReadyText(string name, string id, int port, TimeSpan? elapsed) =>
        $"Стенд «{name}» {(elapsed is { } e ? $"готов за {(int)e.TotalMinutes}:{e.Seconds:00}" : "уже поднят")}: "
        + $"http://127.0.0.1:{port} (сервис {id}, порт {port}). Он живёт после хода и виден в панели «Сервисы» "
        + "(логи, превью, «Стоп»); повторный start_stand вернёт этот же адрес без сборки, погасить — "
        + $"stop_stand с service={id}. Для e2e передай адрес в env.PLAYWRIGHT_BASE_URL у run_tests.";

    private static string Slug(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value.ToLowerInvariant())
            sb.Append(ch is >= 'a' and <= 'z' or >= '0' and <= '9' ? ch : '-');
        var slug = Regex.Replace(sb.ToString(), "-{2,}", "-", RegexOptions.None, TimeSpan.FromMilliseconds(100)).Trim('-');
        return slug.Length == 0 ? "svc" : slug;
    }
}
