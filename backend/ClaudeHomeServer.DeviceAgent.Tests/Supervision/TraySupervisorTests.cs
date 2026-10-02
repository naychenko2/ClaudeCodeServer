using ClaudeHomeServer.DeviceAgent.Supervision;
using ClaudeHomeServer.Protocol;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.DeviceAgent.Tests.Supervision;

/// <summary>
/// Трей под <c>supervise</c> (Ш7) на фейковых часах: перезапуск с бэкоффом, «Выйти из агента»,
/// переход на трей новой версии, версия без трея.
/// </summary>
public sealed class TraySupervisorTests : IDisposable
{
    private readonly FakeClock _clock = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly TempInstall _install = new("1.0.0", "2.0.0");

    public TraySupervisorTests()
    {
        foreach (var v in new[] { "1.0.0", "2.0.0" })
            File.WriteAllText(Path.Combine(Layout.VersionDir(v), HandsTrayProcess.ExecutableName), "fake");
        Layout.SetActive("1.0.0");
    }

    public void Dispose()
    {
        _stop.Dispose();
        _install.Dispose();
    }

    private AgentLayout Layout => _install.Layout;

    private Task<bool> RunAsync(FakeTrayLauncher launcher) =>
        new TraySupervisor(Layout, launcher, NullLogger.Instance, clock: _clock)
            .RunAsync(_stop.Token)
            .WaitAsync(TimeSpan.FromSeconds(30));

    [Fact]
    public async Task Упавший_трей_перезапускается_с_бэкоффом()
    {
        var launcher = new FakeTrayLauncher(_clock, _ => (TimeSpan.Zero, 1)) { StopAt = (4, _stop) };

        (await RunAsync(launcher)).Should().BeFalse();

        launcher.Started.Should().HaveCount(4);
        _clock.Delays.Where(d => d >= TimeSpan.FromSeconds(1)).Should().StartWith(
            [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)]);
    }

    [Fact]
    public async Task Выйти_из_агента_в_трее_останавливает_супервизор()
    {
        // Предохранитель: не распознанный выход перезапускал бы трей бесконечно на синхронных часах
        var launcher = new FakeTrayLauncher(_clock, _ => (TimeSpan.FromMinutes(3), HandsTrayProcess.ExitAgentCode)) { StopAt = (3, _stop) };

        (await RunAsync(launcher)).Should().BeTrue("код ExitAgentCode — человек попросил выйти");

        launcher.Started.Should().ContainSingle("после выхода трей не перезапускается");
    }

    [Fact]
    public async Task Смена_активной_версии_поднимает_трей_новой_версии()
    {
        _clock.Advanced += () =>
        {
            if (_clock.Now >= new DateTimeOffset(2026, 9, 26, 12, 0, 10, TimeSpan.Zero) && Layout.ReadActive() == "1.0.0")
                Layout.SetActive("2.0.0");
        };
        var launcher = new FakeTrayLauncher(_clock, _ => (null, 0)) { StopAt = (2, _stop) };

        await RunAsync(launcher);

        launcher.Started.Select(c => c.Version).Should().Equal("1.0.0", "2.0.0");
        launcher.Started[0].Stopped.Should().BeTrue("старый трей держал бы файлы своей версии");
    }

    [Fact]
    public async Task Версия_без_трея_не_мешает_агенту_и_трей_не_запускается()
    {
        File.Delete(Path.Combine(Layout.VersionDir("1.0.0"), HandsTrayProcess.ExecutableName));
        _clock.StopAt = (_clock.Now + TimeSpan.FromMinutes(1), _stop);
        var launcher = new FakeTrayLauncher(_clock, _ => (null, 0));

        (await RunAsync(launcher)).Should().BeFalse();

        launcher.Started.Should().BeEmpty();
    }

    [Fact]
    public async Task Остановка_супервизора_гасит_трей()
    {
        _clock.StopAt = (_clock.Now + TimeSpan.FromMinutes(1), _stop);
        var launcher = new FakeTrayLauncher(_clock, _ => (null, 0));

        (await RunAsync(launcher)).Should().BeFalse();

        launcher.Started.Should().ContainSingle().Which.Stopped.Should().BeTrue();
    }
}

/// <summary>Фейковый запуск трея: через сколько и с каким кодом выходит трей версии (null — живёт).</summary>
internal sealed class FakeTrayLauncher(FakeClock clock, Func<string, (TimeSpan? ExitAfter, int Code)> script) : ITrayLauncher
{
    private int _nextId = 2000;

    public List<FakeChild> Started { get; } = [];

    public (int Count, CancellationTokenSource Stop)? StopAt { get; set; }

    public ISupervisedChild Start(string executable, string workingDirectory)
    {
        File.Exists(executable).Should().BeTrue("трей берётся из каталога версии");
        var version = Path.GetFileName(workingDirectory);
        var child = new FakeChild(++_nextId, version);
        Started.Add(child);
        var (exitAfter, code) = script(version);
        var startedAt = clock.Now;

        void Tick()
        {
            if (!child.Exited.IsCompleted && exitAfter is { } after && clock.Now - startedAt >= after) child.Exit(code);
        }

        clock.Advanced += Tick;
        Tick();
        if (StopAt is { } stop && Started.Count >= stop.Count) stop.Stop.Cancel();
        return child;
    }
}
