using ClaudeHomeServer.DeviceAgent.Supervision;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.DeviceAgent.Tests.Supervision;

/// <summary>
/// Супервизор по Р7 на фейковых часах и детях: код 75, бэкофф, откат по healthy, плохая
/// версия не пробуется повторно. Настоящее время не идёт — минуты проходят мгновенно.
/// </summary>
public sealed class AgentSupervisorTests : IDisposable
{
    private readonly FakeClock _clock = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly List<string> _repointed = [];
    private readonly TempInstall _install = new("1.0.0", "2.0.0");

    public void Dispose()
    {
        _stop.Dispose();
        _install.Dispose();
    }

    private AgentLayout Layout => _install.Layout;

    private Task<int> RunAsync(FakeLauncher launcher) =>
        new AgentSupervisor(Layout, launcher, _repointed.Add, NullLogger.Instance, clock: _clock)
            .RunAsync(_stop.Token)
            .WaitAsync(TimeSpan.FromSeconds(30));

    /// <summary>1.0.0 — прошлая здоровая, 2.0.0 — новая активная, ещё себя не доказавшая.</summary>
    private void FreshUpdate()
    {
        _install.MarkHealthy("1.0.0");
        Layout.SetActive("1.0.0");
        Layout.SetActive("2.0.0");
    }

    [Fact]
    public async Task Код_75_поднимает_версию_из_перечитанного_active_без_паузы()
    {
        _install.MarkHealthy("1.0.0");
        Layout.SetActive("1.0.0");
        var launcher = new FakeLauncher(_clock, v => v == "1.0.0"
            // Как обновлятор AD-5: переставил active и вышел с 75
            ? new ChildScript(ExitAfter: TimeSpan.FromSeconds(5), ExitCode: 75, OnExit: () => Layout.SetActive("2.0.0"))
            : ChildScript.Healthy) { StopAt = (2, _stop) };

        (await RunAsync(launcher)).Should().Be(0);

        launcher.Versions.Should().Equal("1.0.0", "2.0.0");
        _clock.Delays.Should().NotContain(d => d >= TimeSpan.FromSeconds(1), "код 75 — не падение, бэкоффа нет");
        Layout.ReadActive().Should().Be("2.0.0");
        Layout.ReadPrevious().Should().Be("1.0.0");
    }

    [Fact]
    public async Task Падения_перезапускаются_с_бэкоффом_до_60_секунд()
    {
        _install.MarkHealthy("1.0.0");
        Layout.SetActive("1.0.0");
        var launcher = new FakeLauncher(_clock, _ => ChildScript.Crash()) { StopAt = (9, _stop) };

        await RunAsync(launcher);

        launcher.Versions.Should().OnlyContain(v => v == "1.0.0");
        _clock.Delays.Select(d => (int)d.TotalSeconds).Should().Equal(1, 2, 4, 8, 16, 32, 60, 60);
    }

    [Fact]
    public async Task Бэкофф_сбрасывается_после_долгой_жизни_дочернего()
    {
        _install.MarkHealthy("1.0.0");
        Layout.SetActive("1.0.0");
        var launcher = new FakeLauncher(_clock, _ => new ChildScript(ExitAfter: TimeSpan.FromMinutes(6), ExitCode: 1))
            { StopAt = (4, _stop) };

        await RunAsync(launcher);

        _clock.Delays.Select(d => (int)d.TotalSeconds).Should().Equal(1, 1, 1);
    }

    [Fact]
    public async Task Версия_без_healthy_за_60_секунд_откатывается_и_помечается_плохой()
    {
        FreshUpdate();
        var launcher = new FakeLauncher(_clock, v => v == "2.0.0" ? ChildScript.Broken : ChildScript.Healthy)
            { StopAt = (2, _stop) };
        var started = _clock.Now;

        await RunAsync(launcher);

        launcher.Versions.Should().Equal("2.0.0", "1.0.0");
        launcher.Started[0].Killed.Should().BeTrue("не доказавший себя дочерний гасится");
        (_clock.Now - started).Should().BeGreaterThanOrEqualTo(TimeSpan.FromSeconds(60)).And.BeLessThan(TimeSpan.FromSeconds(62));
        Layout.ReadActive().Should().Be("1.0.0");
        Layout.IsBad("2.0.0", _clock.Now).Should().BeTrue();
        _repointed.Should().Equal("1.0.0");
    }

    [Fact]
    public async Task Версия_упавшая_до_healthy_откатывается_сразу()
    {
        FreshUpdate();
        var launcher = new FakeLauncher(_clock, v => v == "2.0.0" ? ChildScript.Crash(134) : ChildScript.Healthy)
            { StopAt = (2, _stop) };

        await RunAsync(launcher);

        launcher.Versions.Should().Equal("2.0.0", "1.0.0");
        _clock.Delays.Should().NotContain(d => d >= TimeSpan.FromSeconds(1), "откат — без бэкоффа и без минуты ожидания");
        Layout.ReadActive().Should().Be("1.0.0");
        Layout.IsBad("2.0.0", _clock.Now).Should().BeTrue();
    }

    [Fact]
    public async Task Плохая_версия_не_пробуется_повторно()
    {
        FreshUpdate();
        Layout.MarkBad("2.0.0", _clock.Now.AddHours(-1));
        var launcher = new FakeLauncher(_clock, _ => ChildScript.Healthy) { StopAt = (1, _stop) };

        await RunAsync(launcher);

        launcher.Versions.Should().Equal(["1.0.0"], "версия, откаченная меньше суток назад, не запускается");
        Layout.ReadActive().Should().Be("1.0.0");
        _repointed.Should().Equal("1.0.0");
    }

    [Fact]
    public async Task Через_сутки_плохая_версия_пробуется_снова()
    {
        FreshUpdate();
        Layout.MarkBad("2.0.0", _clock.Now.AddHours(-25));
        var launcher = new FakeLauncher(_clock, _ => ChildScript.Healthy) { StopAt = (1, _stop) };

        await RunAsync(launcher);

        launcher.Versions.Should().Equal("2.0.0");
    }

    [Fact]
    public async Task Откатываться_некуда_версия_продолжает_работать()
    {
        Layout.SetActive("2.0.0");
        var launcher = new FakeLauncher(_clock, _ => ChildScript.Broken);
        _clock.StopAt = (_clock.Now.AddMinutes(5), _stop);

        await RunAsync(launcher);

        launcher.Versions.Should().Equal("2.0.0");
        launcher.Started[0].Killed.Should().BeFalse("без прошлой версии лучше агент, ещё не дозвонившийся до сервера, чем никакого");
        launcher.Started[0].Stopped.Should().BeTrue("остановка супервизора гасит дочерний вежливо");
        Layout.ReadActive().Should().Be("2.0.0");
    }

    [Fact]
    public async Task Здоровая_версия_не_откатывается()
    {
        FreshUpdate();
        _install.MarkHealthy("2.0.0");
        var launcher = new FakeLauncher(_clock, _ => ChildScript.Broken);
        _clock.StopAt = (_clock.Now.AddMinutes(5), _stop);

        await RunAsync(launcher);

        launcher.Versions.Should().Equal("2.0.0");
        launcher.Started[0].Killed.Should().BeFalse();
        Layout.ReadActive().Should().Be("2.0.0");
        _repointed.Should().BeEmpty();
    }

    [Fact]
    public async Task Нет_активной_версии_супервизор_ждёт_а_не_падает()
    {
        var launcher = new FakeLauncher(_clock, _ => ChildScript.Healthy);
        _clock.StopAt = (_clock.Now.AddSeconds(35), _stop);

        (await RunAsync(launcher)).Should().Be(0);

        launcher.Started.Should().BeEmpty();
        _clock.Delays.Should().OnlyContain(d => d == TimeSpan.FromSeconds(10));
    }
}
