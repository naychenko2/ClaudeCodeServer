namespace ClaudeHomeServer.Services.RemoteCommands;

/// <summary>Чем кончился запуск команды в шелле.</summary>
public enum ShellRunOutcome
{
    /// <summary>Запуск состоялся, команда завершилась — смотри <c>ExitCode</c>.</summary>
    Exited,

    /// <summary>Запуск не состоялся вовсе (шелла нет, рабочая папка исчезла) — причина в <c>Failure</c>.</summary>
    LaunchFailed,

    /// <summary>Команда не уложилась в таймаут и была убита вместе с деревом процессов.</summary>
    Timeout,
}

/// <summary>Итог одного завершившегося запуска.</summary>
public sealed record ShellRunResult(ShellRunOutcome Outcome, int ExitCode = 0, string Output = "",
    string? Failure = null, string? StdOutOnly = null)
{
    /// <summary>
    /// Только stdout. По нему судит <c>StatusRunningPattern</c> (§3/§5 плана): команда,
    /// печатающая паттерн в stderr (диагностика, предупреждение), не должна давать ложный
    /// «запущен». Не задан отдельно — весь вывод считается stdout.
    /// </summary>
    public string StdOut => StdOutOnly ?? Output;

    public static ShellRunResult Exited(int code, string output, string? stdout = null) =>
        new(ShellRunOutcome.Exited, code, output, StdOutOnly: stdout);
    public static ShellRunResult Failed(string reason) => new(ShellRunOutcome.LaunchFailed, Failure: reason);
    public static ShellRunResult TimedOut() => new(ShellRunOutcome.Timeout, Failure: "Команда не уложилась в отведённое время и была прервана.");
}

/// <summary>
/// Долгоживущий процесс, которым владеет пульт (режим <c>daemon</c>). Шов над
/// <see cref="System.Diagnostics.Process"/>: тесты подменяют его фейком, реальные
/// процессы в них не рождаются.
/// </summary>
public interface IShellProcess : IDisposable
{
    bool HasExited { get; }

    /// <summary>Ждёт выхода процесса. Отмена токеном не убивает — просто перестаёт ждать.</summary>
    Task WaitForExitAsync(CancellationToken ct);

    /// <summary>Убивает процесс вместе с деревом детей. Уже мёртвый — no-op.</summary>
    void KillTree();
}

/// <summary>Итог спавна долгоживущего процесса.</summary>
public sealed record ShellSpawnResult(IShellProcess? Process, string? Failure);

/// <summary>
/// Запуск строки в системном шелле НА ХОСТЕ сервера. Точка подмены для тестов —
/// как <c>IPowerActions</c> у питания: исполнять настоящие команды в тестах нельзя.
///
/// Сознательно отдельный от <c>IWatchdogCommandRunner</c>: тот исполняет команду в среде
/// владельца (вплоть до контейнера песочницы), а пульт по определению управляет машиной
/// сервера — «sc stop» в контейнере бессмыслен. Общая у них только формула сборки
/// командной строки, и она вынесена в спину (<see cref="ShellCommandLine"/>).
/// </summary>
public interface IShellCommandRunner
{
    /// <summary>
    /// Запускает команду и ждёт её завершения не дольше <paramref name="timeoutSeconds"/>.
    /// Вывод (stdout+stderr) возвращается текстом и, если задан, дублируется в <paramref name="sink"/>.
    /// </summary>
    Task<ShellRunResult> RunAsync(string command, string? workingDir, int timeoutSeconds,
        OutputRingBuffer? sink, CancellationToken ct);

    /// <summary>
    /// Запускает команду и НЕ ждёт её выхода (режим <c>daemon</c>): вывод уходит в
    /// <paramref name="sink"/> по мере поступления, владение процессом — на вызывающем.
    /// </summary>
    ShellSpawnResult Spawn(string command, string? workingDir, OutputRingBuffer? sink);
}
