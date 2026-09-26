using ClaudeHomeServer.DeviceAgent.Supervision;

namespace ClaudeHomeServer.DeviceAgent.Tests.Supervision;

/// <summary>Часы без настоящего времени: ожидание сдвигает «сейчас» и будит фейковых детей.</summary>
internal sealed class FakeClock : ISupervisorClock
{
    public DateTimeOffset Now { get; private set; } = new(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);

    public List<TimeSpan> Delays { get; } = [];

    public event Action? Advanced;

    /// <summary>Отменить супервизор, когда фейковое время дойдёт до отметки.</summary>
    public (DateTimeOffset At, CancellationTokenSource Stop)? StopAt { get; set; }

    public void Advance(TimeSpan by)
    {
        Now += by;
        Advanced?.Invoke();
        if (StopAt is { } stop && Now >= stop.At) stop.Stop.Cancel();
    }

    public Task Delay(TimeSpan delay, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Delays.Add(delay);
        Advance(delay);
        return Task.CompletedTask;
    }

    public async Task<T> WaitAsync<T>(Task<T> task, CancellationToken ct)
    {
        for (var step = 0; !task.IsCompleted; step++)
        {
            ct.ThrowIfCancellationRequested();
            if (step > 1_000_000) throw new TimeoutException("фейковый дочерний не выходит");
            Advance(TimeSpan.FromSeconds(1));
        }
        return await task;
    }
}

/// <summary>Как ведёт себя дочерний версии: когда пишет healthy, когда и с каким кодом выходит.</summary>
internal sealed record ChildScript(TimeSpan? HealthyAfter = null, TimeSpan? ExitAfter = null, int ExitCode = 0, Action? OnExit = null)
{
    public static ChildScript Healthy => new(HealthyAfter: TimeSpan.FromSeconds(2));
    public static ChildScript Broken => new();
    public static ChildScript Crash(int code = 1) => new(ExitAfter: TimeSpan.Zero, ExitCode: code);
}

internal sealed class FakeChild : ISupervisedChild
{
    private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public FakeChild(int id, string version) => (Id, Version) = (id, version);

    public int Id { get; }
    public string Version { get; }
    public bool Killed { get; private set; }
    public bool Stopped { get; private set; }
    public Task<int> Exited => _exit.Task;

    public void Exit(int code) => _exit.TrySetResult(code);

    public Task StopAsync(TimeSpan grace)
    {
        Stopped = true;
        Exit(0);
        return Task.CompletedTask;
    }

    public void Kill()
    {
        Killed = true;
        Exit(-9);
    }

    public void Dispose() { }
}

/// <summary>Фейковый запуск: поведение по версии, журнал запусков, остановка теста после N-го старта.</summary>
internal sealed class FakeLauncher(FakeClock clock, Func<string, ChildScript> script) : IChildLauncher
{
    private int _nextId = 1000;

    public List<FakeChild> Started { get; } = [];
    public IEnumerable<string> Versions => Started.Select(c => c.Version);

    /// <summary>Отменить супервизор, когда запусков станет столько.</summary>
    public (int Count, CancellationTokenSource Stop)? StopAt { get; set; }

    public ISupervisedChild Start(string executable, string workingDirectory, string healthyFile)
    {
        var version = Path.GetFileName(workingDirectory);
        var child = new FakeChild(++_nextId, version);
        Started.Add(child);
        var behaviour = script(version);
        var startedAt = clock.Now;

        void Tick()
        {
            if (child.Exited.IsCompleted) return;
            var elapsed = clock.Now - startedAt;
            if (behaviour.HealthyAfter is { } h && elapsed >= h && !File.Exists(healthyFile))
                File.WriteAllText(healthyFile, "ok");
            if (behaviour.ExitAfter is { } e && elapsed >= e)
            {
                behaviour.OnExit?.Invoke();
                child.Exit(behaviour.ExitCode);
            }
        }

        clock.Advanced += Tick;
        Tick();
        if (StopAt is { } stop && Started.Count >= stop.Count) stop.Stop.Cancel();
        return child;
    }
}

/// <summary>Временная установка: versions/{v}/ с «бинарём», указатели и маркеры.</summary>
internal sealed class TempInstall : IDisposable
{
    public TempInstall(params string[] versions)
    {
        Root = Directory.CreateTempSubdirectory("agent-layout-").FullName;
        Layout = new AgentLayout(Root);
        foreach (var v in versions) AddVersion(v);
    }

    public string Root { get; }
    public AgentLayout Layout { get; }

    public void AddVersion(string version)
    {
        Directory.CreateDirectory(Layout.VersionDir(version));
        File.WriteAllText(Layout.ExeOf(version), "fake");
    }

    public void MarkHealthy(string version) => File.WriteAllText(Layout.HealthyOf(version), "ok");

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { }
    }
}
