using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.RemoteCommands;

namespace ClaudeHomeServer.Tests.Services.RemoteCommands;

/// <summary>
/// Фейковый шелл: пульт исполняет объявленные команды на машине, и в тестах настоящие
/// процессы рождаться не должны вовсе. Ответ на каждую команду задаёт тест.
/// </summary>
public sealed class FakeShellCommandRunner : IShellCommandRunner
{
    private readonly object _lock = new();
    private readonly List<string> _ran = [];

    /// <summary>Что отвечать на завершающиеся команды (Start/Stop/Status).</summary>
    public Func<string, Task<ShellRunResult>> Handler { get; set; } =
        _ => Task.FromResult(ShellRunResult.Exited(0, ""));

    /// <summary>Что отдавать на спавн долгоживущего процесса.</summary>
    public Func<string, ShellSpawnResult> SpawnHandler { get; set; } =
        _ => new ShellSpawnResult(new FakeShellProcess(), null);

    /// <summary>Все команды в порядке исполнения — по ним видно, что пульт вызвал, а что нет.</summary>
    public IReadOnlyList<string> Ran { get { lock (_lock) return _ran.ToList(); } }

    /// <summary>Токены, доехавшие до раннера: по ним видно, не прокинут ли в команду
    /// <c>RequestAborted</c> — обрыв соединения не должен убивать дерево процесса.</summary>
    public IReadOnlyList<CancellationToken> Tokens { get { lock (_lock) return _tokens.ToList(); } }

    private readonly List<CancellationToken> _tokens = [];

    public Task<ShellRunResult> RunAsync(string command, string? workingDir, int timeoutSeconds,
        OutputRingBuffer? sink, CancellationToken ct)
    {
        lock (_lock) { _ran.Add(command); _tokens.Add(ct); }
        return Handler(command);
    }

    public ShellSpawnResult Spawn(string command, string? workingDir, OutputRingBuffer? sink)
    {
        lock (_lock) _ran.Add(command);
        return SpawnHandler(command);
    }
}

/// <summary>Фейковый долгоживущий процесс: «умирает» только по команде теста.</summary>
public sealed class FakeShellProcess : IShellProcess
{
    private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool HasExited { get; private set; }
    public bool Killed { get; private set; }
    public bool Disposed { get; private set; }

    /// <summary>Процесс завершился сам (как умерший сразу после запуска демон).</summary>
    public void Exit()
    {
        HasExited = true;
        _exited.TrySetResult();
    }

    // Ожидание строго через событие: никаких Task.Delay — на слабом CI они флакают
    public async Task WaitForExitAsync(CancellationToken ct)
    {
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var registration = ct.Register(() => cancelled.TrySetResult());
        await Task.WhenAny(_exited.Task, cancelled.Task);
    }

    public void KillTree()
    {
        Killed = true;
        Exit();
    }

    public void Dispose() => Disposed = true;
}
