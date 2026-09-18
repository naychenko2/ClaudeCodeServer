using System.Text.RegularExpressions;
using ClaudeHomeServer.Models;
using Microsoft.Extensions.Options;

namespace ClaudeHomeServer.Services.RemoteCommands;

/// <summary>Состояние действия. Источник правды — результат Status-команды, не память процесса.</summary>
public enum RemoteCommandState
{
    Running,
    Stopped,
    /// <summary>Проверить не удалось — это НЕ «остановлено». К unknown всегда идёт причина.</summary>
    Unknown,
}

/// <summary>Строка списка действий: тексты команд наружу не отдаются никогда.</summary>
public sealed record RemoteCommandView(string Key, string Title, string Mode, string State,
    bool Busy, DateTimeOffset? CheckedAt, int? LastExitCode);

public enum RemoteCommandOperationStatus
{
    Ok,
    Failed,
    /// <summary>По действию уже идёт операция — второй start/stop отклонён.</summary>
    Busy,
}

/// <summary>
/// Итог операции: состояние после неё сервис выясняет сам, второй запрос фронту не нужен.
/// <c>CheckedAt</c>/<c>LastExitCode</c> едут тем же ответом — иначе после успешного refresh
/// карточка показывала бы свежий бейдж и «ещё не проверялось» строкой ниже.
/// </summary>
public sealed record RemoteCommandOperation(RemoteCommandOperationStatus Status, string State,
    bool Busy, string? Detail, DateTimeOffset? CheckedAt = null, int? LastExitCode = null);

/// <summary>
/// Пульт удалённых команд: запускает, останавливает и проверяет заранее объявленные действия
/// на машине сервера.
///
/// Реестр действий приходит ТОЛЬКО из конфигурации на диске — по HTTP ездит один лишь ключ.
/// Кривые записи отбрасываются при старте с предупреждением (ключ и причина, без текстов
/// команд), пульт живёт на оставшихся: опечатка в одной строке конфига не должна гасить всё.
///
/// Сервис заодно <see cref="IHostedService"/> — при штатной остановке CCS гасит своих
/// daemon-детей. Иначе сирота появлялся бы при КАЖДОМ рестарте, а не только при аварии.
/// Регистрировать его нужно как синглтон + форвард hosted-регистрации на тот же экземпляр:
/// <c>AddHostedService&lt;T&gt;()</c> создал бы ВТОРОЙ инстанс, и гасил бы детей у пустышки.
/// </summary>
public sealed partial class RemoteCommandsService : IHostedService
{
    private readonly IShellCommandRunner _runner;
    private readonly ILogger<RemoteCommandsService> _log;
    private readonly bool _enabledInConfig;
    private readonly List<ActionRuntime> _actions = [];

    // Пауза, после которой спавн daemon считается удавшимся: процесс, умерший сразу (нет exe,
    // кривые аргументы), обязан быть виден как провал, а не как «запустили». Поле, а не
    // константа, — тесты сжимают его, чтобы не ждать вживую.
    internal int DaemonGraceMs { get; set; } = 2000;

    public RemoteCommandsService(IOptions<RemoteCommandsOptions> options, IShellCommandRunner runner,
        ILogger<RemoteCommandsService> log)
    {
        _runner = runner;
        _log = log;
        var opt = options.Value;
        _enabledInConfig = opt.Enabled;
        foreach (var action in opt.Actions ?? []) TryAdd(action);
    }

    /// <summary>Фича доступна: рубильник включён И осталось хоть одно годное действие.</summary>
    public bool Enabled => _enabledInConfig && _actions.Count > 0;

    /// <summary>Список действий из кэша. Не исполняет НИ ОДНОЙ команды — иначе каждое
    /// монтирование шапки гоняло бы N процессов.</summary>
    public IReadOnlyList<RemoteCommandView> List() =>
        Enabled ? _actions.Select(Snapshot).ToList() : [];

    /// <summary>Накопленный вывод действия; null — ключ неизвестен.</summary>
    public string? Output(string key) => Find(key)?.Output.GetAll();

    /// <summary>Свежая проверка состояния. Gate НЕ берёт: при идущей операции отдаёт кэш с busy.</summary>
    public async Task<RemoteCommandOperation?> RefreshAsync(string key, CancellationToken ct = default)
    {
        var action = Find(key);
        if (action is null) return null;

        if (IsBusy(action)) return Cached(action);

        // Проба сериализована сама с собой: замок операции она по §7 не берёт, но без
        // собственного предохранителя два окна пульта и накликанная «Обновить» на медленном
        // `docker compose ps` копили бы параллельные процессы проверки.
        if (Interlocked.CompareExchange(ref action.ProbeInFlight, 1, 0) != 0) return Cached(action);

        try
        {
            var state = await ProbeAsync(action, ct);
            return new RemoteCommandOperation(RemoteCommandOperationStatus.Ok, Name(state), false,
                action.Detail, action.CheckedAt, action.LastExitCode);
        }
        finally { Interlocked.Exchange(ref action.ProbeInFlight, 0); }
    }

    /// <summary>Ответ из кэша с признаком «идёт работа»: и на чужую операцию, и на чужую пробу.</summary>
    private static RemoteCommandOperation Cached(ActionRuntime action) => new(
        RemoteCommandOperationStatus.Ok, Name(action.State), true, action.Detail,
        action.CheckedAt, action.LastExitCode);

    /// <summary>Запускает действие; null — ключ неизвестен.</summary>
    public Task<RemoteCommandOperation?> StartAsync(string key, string user, CancellationToken ct = default) =>
        OperateAsync(key, user, stop: false, ct);

    /// <summary>Останавливает действие; null — ключ неизвестен.</summary>
    public Task<RemoteCommandOperation?> StopAsync(string key, string user, CancellationToken ct = default) =>
        OperateAsync(key, user, stop: true, ct);

    private async Task<RemoteCommandOperation?> OperateAsync(string key, string user, bool stop, CancellationToken ct)
    {
        var action = Find(key);
        if (action is null) return null;

        if (!action.Gate.Wait(0))
            return new RemoteCommandOperation(RemoteCommandOperationStatus.Busy, Name(action.State), true,
                "По этому действию уже идёт операция.");

        var started = DateTimeOffset.UtcNow;
        var ok = false;
        var state = action.State;
        try
        {
            var (succeeded, detail) = stop
                ? await DoStopAsync(action, ct)
                : await DoStartAsync(action, ct);
            ok = succeeded;

            // Источник правды о состоянии — Status-команда, а не «мы же только что запустили»
            state = await ProbeAsync(action, ct);
            var text = detail ?? action.Detail;

            // Остановку судим по ФАКТУ, как daemon-ветку: уже остановленная служба отвечает
            // на `sc stop` кодом 1062, а `taskkill` — 128, и красная плашка поверх честного
            // «Остановлен» была бы враньём.
            if (stop && !ok && state == RemoteCommandState.Stopped) { ok = true; text = null; }

            return new RemoteCommandOperation(
                ok ? RemoteCommandOperationStatus.Ok : RemoteCommandOperationStatus.Failed,
                Name(state), false, ok ? null : text, action.CheckedAt, action.LastExitCode);
        }
        finally
        {
            // Аудит пишется ВСЕГДА, в том числе когда операция вылетела исключением: команда
            // на машине к этому моменту могла уже отработать, и запись «кто нажал, чем
            // кончилось» важнее прямого потока (инвариант §9.6).
            // В лог идут ключ, операция, пользователь, exit и длительность — но НИКОГДА
            // ни текст команды, ни её вывод (инвариант 6 плана).
            _log.LogInformation(
                "Пульт: {Operation} действия {Key} пользователем {User} — {Result}, состояние {State}, exit={Exit}, {Ms} мс.",
                stop ? "остановка" : "запуск", action.Config.Key, user, ok ? "успех" : "отказ",
                Name(state), action.LastExitCode, (int)(DateTimeOffset.UtcNow - started).TotalMilliseconds);
            action.Gate.Release();
        }
    }

    private async Task<(bool Ok, string? Detail)> DoStartAsync(ActionRuntime action, CancellationToken ct)
    {
        if (action.Mode == RemoteCommandMode.Oneshot)
        {
            var result = await RunAsync(action, action.Config.Start, ct);
            return Judge(result, "Команда запуска");
        }

        // daemon: свой живой ребёнок — уже запущено, второй процесс не плодим
        if (action.Child is { HasExited: false }) return (true, null);
        DropChild(action);

        // Своего ребёнка нет — но это НЕ значит, что процесса нет на машине: он мог пережить
        // рестарт CCS или быть поднят руками, и карточка честно показывает «Запущен» по
        // Status-команде. Спавн вслепую поднял бы ВТОРОЙ экземпляр.
        if (!string.IsNullOrWhiteSpace(action.Config.Status)
            && await ProbeAsync(action, ct) == RemoteCommandState.Running)
            return (true, null);

        var spawn = _runner.Spawn(action.Config.Start, WorkDir(action), action.Output);
        if (spawn.Process is null) return (false, "Не удалось запустить: " + (spawn.Failure ?? "неизвестная причина"));

        action.Child = spawn.Process;

        // Grace: ждём НЕ таймаут, а сам факт смерти. Вышел за паузу — старт провалился,
        // и хвост вывода объяснит почему; пережил её — считаем поднявшимся.
        using var grace = CancellationTokenSource.CreateLinkedTokenSource(ct);
        grace.CancelAfter(DaemonGraceMs);
        await spawn.Process.WaitForExitAsync(grace.Token);

        if (spawn.Process.HasExited)
        {
            DropChild(action);
            return (false, WithTail("Процесс завершился сразу после запуска.", action.Output.TailLines(15)));
        }

        return (true, null);
    }

    private async Task<(bool Ok, string? Detail)> DoStopAsync(ActionRuntime action, CancellationToken ct)
    {
        var stopCommand = action.Config.Stop;

        if (action.Mode == RemoteCommandMode.Oneshot)
        {
            if (string.IsNullOrWhiteSpace(stopCommand))
                return (false, "Команда остановки у этого действия не объявлена.");
            var result = await RunAsync(action, stopCommand, ct);
            return Judge(result, "Команда остановки");
        }

        var child = action.Child;
        var childAlive = child is { HasExited: false };

        // Штатная остановка вежливее kill: сначала объявленная команда. При мёртвом или чужом
        // ребёнке она же добивает сироту, оставшегося от прошлой жизни CCS.
        (bool Ok, string? Detail) byCommand = (true, null);
        if (!string.IsNullOrWhiteSpace(stopCommand))
            byCommand = Judge(await RunAsync(action, stopCommand, ct), "Команда остановки");

        if (!childAlive)
        {
            DropChild(action);
            return string.IsNullOrWhiteSpace(stopCommand)
                ? (true, null) // своего процесса нет, гасить нечего — это не отказ
                : byCommand;
        }

        if (!string.IsNullOrWhiteSpace(stopCommand))
        {
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
            wait.CancelAfter(TimeSpan.FromSeconds(action.Config.EffectiveTimeoutSeconds));
            await child!.WaitForExitAsync(wait.Token);
        }

        // Не вышел (или команды остановки не было) — kill дерева, внутри него WaitForExit(5с)
        if (!child!.HasExited) child.KillTree();

        var dead = child.HasExited;
        DropChild(action);

        // Судим по ФАКТУ: процесс мёртв — остановка удалась, даже если объявленная команда
        // вернула ненулевой код (её работу мог доделать kill).
        return dead
            ? (true, null)
            : (false, "Процесс не удалось остановить — он пережил принудительное завершение.");
    }

    /// <summary>Проверка состояния по правилам §5 плана. Обновляет кэш действия.</summary>
    private async Task<RemoteCommandState> ProbeAsync(ActionRuntime action, CancellationToken ct)
    {
        var statusCommand = action.Config.Status;

        if (string.IsNullOrWhiteSpace(statusCommand))
        {
            // Только daemon (у oneshot Status обязателен — валидация при старте). Живой
            // собственный ребёнок — честный положительный сигнал; его нет — мы правда не знаем.
            var alive = action.Child is { HasExited: false };
            return Remember(action, alive ? RemoteCommandState.Running : RemoteCommandState.Unknown, null,
                alive ? null : "Команда проверки состояния не объявлена, а своего процесса у сервера нет.");
        }

        var result = await RunAsync(action, statusCommand, ct);
        return result.Outcome switch
        {
            ShellRunOutcome.LaunchFailed => Remember(action, RemoteCommandState.Unknown, null,
                "Не удалось выполнить проверку: " + (result.Failure ?? "неизвестная причина")),
            ShellRunOutcome.Timeout => Remember(action, RemoteCommandState.Unknown, null,
                "Проверка состояния не уложилась в отведённое время и была прервана."),
            // 9009 на Windows — «не является внутренней или внешней командой»: это опечатка
            // в конфиге, а не остановленный демон, и врать «остановлено» тут нельзя
            _ when result.ExitCode == 9009 && OperatingSystem.IsWindows() => Remember(action,
                RemoteCommandState.Unknown, result.ExitCode,
                "Команда проверки не найдена на этой машине (код 9009) — похоже на опечатку в конфиге."),
            // 127 — тот же случай на Unix («command not found» у sh): опечатка в конфиге,
            // а не остановленный демон
            _ when result.ExitCode == 127 && !OperatingSystem.IsWindows() => Remember(action,
                RemoteCommandState.Unknown, result.ExitCode,
                "Команда проверки не найдена на этой машине (код 127) — похоже на опечатку в конфиге."),
            _ when result.ExitCode == 0 && Matches(action, result.StdOut) =>
                Remember(action, RemoteCommandState.Running, result.ExitCode, null),
            _ => Remember(action, RemoteCommandState.Stopped, result.ExitCode, null),
        };
    }

    private static bool Matches(ActionRuntime action, string output)
    {
        var pattern = action.Config.StatusRunningPattern;
        return string.IsNullOrEmpty(pattern) || output.Contains(pattern, StringComparison.Ordinal);
    }

    private Task<ShellRunResult> RunAsync(ActionRuntime action, string command, CancellationToken ct) =>
        _runner.RunAsync(command, WorkDir(action), action.Config.EffectiveTimeoutSeconds, action.Output, ct);

    private static (bool Ok, string? Detail) Judge(ShellRunResult result, string what) => result.Outcome switch
    {
        ShellRunOutcome.LaunchFailed => (false, $"{what} не запустилась: {result.Failure}"),
        ShellRunOutcome.Timeout => (false, $"{what} не уложилась в отведённое время и была прервана."),
        _ when result.ExitCode == 0 => (true, null),
        _ => (false, WithTail($"{what} завершилась с кодом {result.ExitCode}.", Tail(result.Output))),
    };

    private static string WithTail(string reason, string tail) =>
        tail.Length > 0 ? reason + "\n" + tail : reason;

    /// <summary>Последние 15 непустых строк вывода — столько показывает карточка действия.</summary>
    private static string Tail(string output) => string.Join("\n", output
        .Split('\n')
        .Select(l => l.TrimEnd('\r'))
        .Where(l => l.Length > 0)
        .TakeLast(15));

    private RemoteCommandState Remember(ActionRuntime action, RemoteCommandState state, int? exitCode, string? detail)
    {
        action.State = state;
        action.CheckedAt = DateTimeOffset.UtcNow;
        action.LastExitCode = exitCode;
        action.Detail = detail;
        return state;
    }

    private static void DropChild(ActionRuntime action)
    {
        action.Child?.Dispose();
        action.Child = null;
    }

    private static string WorkDir(ActionRuntime action)
    {
        var dir = action.Config.WorkingDir;
        if (!string.IsNullOrWhiteSpace(dir)) return dir;
        // Пусто — домашняя папка пользователя сервера: рабочая папка самого процесса CCS
        // (папка публикации) для команд хозяина машины неожиданна.
        return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    private ActionRuntime? Find(string key) => Enabled
        ? _actions.FirstOrDefault(a => string.Equals(a.Config.Key, key, StringComparison.Ordinal))
        : null;

    private static bool IsBusy(ActionRuntime action) => action.Gate.CurrentCount == 0;

    private static RemoteCommandView Snapshot(ActionRuntime a) => new(
        a.Config.Key, a.Config.Title, a.Mode.ToString().ToLowerInvariant(), Name(a.State),
        IsBusy(a), a.CheckedAt, a.LastExitCode);

    private static string Name(RemoteCommandState state) => state.ToString().ToLowerInvariant();

    // ── Валидация конфигурации ───────────────────────────────────────────────────

    [GeneratedRegex("^[a-z0-9-]{1,64}$")]
    private static partial Regex KeyPattern();

    private void TryAdd(RemoteCommandAction action)
    {
        var key = (action.Key ?? "").Trim();

        if (key.Length == 0) { Reject("(без ключа)", "не задан Key"); return; }
        if (!KeyPattern().IsMatch(key)) { Reject(key, "Key не подходит под [a-z0-9-] длиной до 64"); return; }
        if (_actions.Any(a => string.Equals(a.Config.Key, key, StringComparison.Ordinal)))
        { Reject(key, "дубль Key"); return; }
        if (string.IsNullOrWhiteSpace(action.Title)) { Reject(key, "не задан Title"); return; }
        if (string.IsNullOrWhiteSpace(action.Start)) { Reject(key, "не задана команда Start"); return; }
        if (!RemoteCommandAction.TryParseMode(action.Mode, out var mode))
        { Reject(key, "неизвестный Mode (ожидается oneshot или daemon)"); return; }
        if (mode == RemoteCommandMode.Oneshot && string.IsNullOrWhiteSpace(action.Status))
        { Reject(key, "режиму oneshot обязательна команда Status"); return; }

        action.Key = key;
        _actions.Add(new ActionRuntime(action, mode));
    }

    // В предупреждение идут только ключ и причина: тексты команд в логи не попадают.
    private void Reject(string key, string reason) =>
        _log.LogWarning("Пульт: действие {Key} отброшено — {Reason}.", key, reason);

    // ── Жизненный цикл ───────────────────────────────────────────────────────────

    Task IHostedService.StartAsync(CancellationToken cancellationToken)
    {
        if (_enabledInConfig)
            _log.LogInformation("Пульт удалённых команд включён, действий: {Count}.", _actions.Count);
        return Task.CompletedTask;
    }

    /// <summary>Штатная остановка CCS гасит своих daemon-детей — иначе сирота после каждого рестарта.</summary>
    async Task IHostedService.StopAsync(CancellationToken cancellationToken)
    {
        // Детей гасим параллельно: KillTree внутри ждёт выхода до 5 секунд, и три демона
        // подряд съели бы до 15 секунд окна остановки сервера.
        var kills = _actions
            .Where(a => a.Child is { HasExited: false })
            .Select(a => Task.Run(() =>
            {
                _log.LogInformation("Пульт: гашу процесс действия {Key} при остановке сервера.", a.Config.Key);
                try { a.Child!.KillTree(); } catch { /* уже мёртв — ровно то, чего добивались */ }
            }, CancellationToken.None))
            .ToList();

        if (kills.Count > 0) await Task.WhenAll(kills);
        foreach (var action in _actions) DropChild(action);
    }

    /// <summary>Рантайм одного действия: конфиг, буфер вывода, замок операции и кэш состояния.</summary>
    private sealed class ActionRuntime(RemoteCommandAction config, RemoteCommandMode mode)
    {
        public RemoteCommandAction Config { get; } = config;
        public RemoteCommandMode Mode { get; } = mode;
        public OutputRingBuffer Output { get; } = new();
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public RemoteCommandState State { get; set; } = RemoteCommandState.Unknown;
        public DateTimeOffset? CheckedAt { get; set; }
        public int? LastExitCode { get; set; }
        public string? Detail { get; set; }
        public IShellProcess? Child { get; set; }

        /// <summary>Флаг «проба состояния в полёте» (0/1) — поле, а не свойство: его читает
        /// и пишет <see cref="Interlocked"/>.</summary>
        public int ProbeInFlight;
    }
}
