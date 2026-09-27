using ClaudeHomeServer.DeviceAgent.Supervision;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.DeviceAgent.Tests.Supervision;

/// <summary>
/// Самообновление супервизора на фейках: сменилась active — эстафета супервизору новой версии
/// (автозапуск переписан до неё), пока жив дочерний — никакой эстафеты, отказ нового — остаёмся
/// и повторно не пробуем, битая новая версия откатывается.
/// </summary>
public sealed class SupervisorHandoffTests : IDisposable
{
    private readonly FakeClock _clock = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly List<string> _events = [];
    private readonly TempInstall _install = new("1.0.0", "2.0.0");

    public void Dispose()
    {
        _stop.Dispose();
        _install.Dispose();
    }

    private AgentLayout Layout => _install.Layout;

    private AgentSupervisor Supervisor(FakeLauncher launcher, FakeHandoff handoff, string own = "1.0.0") =>
        new(Layout, launcher, v => _events.Add("repoint:" + v), NullLogger.Instance, clock: _clock, ownVersion: own, handoff: handoff);

    private static Task<int> Run(AgentSupervisor supervisor, CancellationToken stop) =>
        supervisor.RunAsync(stop).WaitAsync(TimeSpan.FromSeconds(30));

    private sealed class FakeHandoff(List<string> events, bool accept) : ISupervisorHandoff
    {
        public List<string> Versions { get; } = [];

        public Task<HandoffResult> HandOffAsync(string version, CancellationToken ct)
        {
            Versions.Add(version);
            events.Add("handoff:" + version);
            return Task.FromResult(accept ? HandoffResult.Ok : HandoffResult.Failed("новый супервизор вышел"));
        }
    }

    [Fact]
    public async Task Смена_active_по_коду_75_передаёт_эстафету_супервизору_новой_версии()
    {
        _install.MarkHealthy("1.0.0");
        Layout.SetActive("1.0.0");
        var launcher = new FakeLauncher(_clock, v => v == "1.0.0"
            ? new ChildScript(ExitAfter: TimeSpan.FromSeconds(5), ExitCode: 75, OnExit: () => Layout.SetActive("2.0.0"))
            : ChildScript.Healthy);
        var handoff = new FakeHandoff(_events, accept: true);
        var supervisor = Supervisor(launcher, handoff);

        (await Run(supervisor, _stop.Token)).Should().Be(0);

        supervisor.HandedOffTo.Should().Be("2.0.0");
        launcher.Versions.Should().Equal(["1.0.0"], "дочерний новой версии поднимает уже новый супервизор");
        _events.Should().Equal(["repoint:2.0.0", "handoff:2.0.0"], "автозапуск смотрит на новую версию до выхода старого");
        Layout.ReadActive().Should().Be("2.0.0");
    }

    [Fact]
    public async Task Пока_дочерний_жив_эстафеты_нет()
    {
        _install.MarkHealthy("1.0.0");
        Layout.SetActive("1.0.0");
        // active сменилась под живым дочерним (идёт ход): супервизор его не трогает
        var launcher = new FakeLauncher(_clock, _ => ChildScript.Healthy);
        _clock.Advanced += () =>
        {
            if (launcher.Started.Count > 0 && Layout.ReadActive() == "1.0.0") Layout.SetActive("2.0.0");
        };
        _clock.StopAt = (_clock.Now.AddMinutes(10), _stop);
        var handoff = new FakeHandoff(_events, accept: true);

        await Run(Supervisor(launcher, handoff), _stop.Token);

        handoff.Versions.Should().BeEmpty("эстафета — только между дочерними");
        launcher.Started.Should().ContainSingle();
        launcher.Started[0].Killed.Should().BeFalse();
        launcher.Started[0].Stopped.Should().BeTrue("дочерний гасится только остановкой супервизора");
    }

    [Fact]
    public async Task Своя_версия_активна_эстафеты_нет()
    {
        _install.MarkHealthy("1.0.0");
        Layout.SetActive("1.0.0");
        var launcher = new FakeLauncher(_clock, _ => ChildScript.Crash()) { StopAt = (3, _stop) };
        var handoff = new FakeHandoff(_events, accept: true);

        await Run(Supervisor(launcher, handoff), _stop.Token);

        handoff.Versions.Should().BeEmpty();
        launcher.Versions.Should().Equal("1.0.0", "1.0.0", "1.0.0");
    }

    [Fact]
    public async Task Новый_супервизор_не_принял_эстафету_старый_работает_и_не_повторяет()
    {
        _install.MarkHealthy("1.0.0");
        Layout.SetActive("1.0.0");
        Layout.SetActive("2.0.0");
        _install.MarkHealthy("2.0.0");
        var launcher = new FakeLauncher(_clock, _ => ChildScript.Crash()) { StopAt = (4, _stop) };
        var handoff = new FakeHandoff(_events, accept: false);
        var supervisor = Supervisor(launcher, handoff);

        await Run(supervisor, _stop.Token);

        handoff.Versions.Should().Equal(["2.0.0"], "отказавший преемник не пробуется на каждом перезапуске — иначе цикл");
        launcher.Versions.Should().Equal(["2.0.0", "2.0.0", "2.0.0", "2.0.0"], "дочерний новой версии поднимает сам старый супервизор");
        supervisor.HandedOffTo.Should().BeNull();
    }

    [Fact]
    public async Task Эстафета_не_принята_а_новая_версия_битая_откат_на_прежнюю()
    {
        _install.MarkHealthy("1.0.0");
        Layout.SetActive("1.0.0");
        Layout.SetActive("2.0.0");
        var launcher = new FakeLauncher(_clock, v => v == "2.0.0" ? ChildScript.Crash(134) : ChildScript.Healthy)
            { StopAt = (2, _stop) };
        var handoff = new FakeHandoff(_events, accept: false);

        await Run(Supervisor(launcher, handoff), _stop.Token);

        launcher.Versions.Should().Equal("2.0.0", "1.0.0");
        Layout.ReadActive().Should().Be("1.0.0");
        Layout.IsBad("2.0.0", _clock.Now).Should().BeTrue();
        _events.Should().Equal("repoint:2.0.0", "handoff:2.0.0", "repoint:1.0.0");
    }

    [Fact]
    public async Task Новый_супервизор_откатил_битую_версию_и_вернул_эстафету_прежнему()
    {
        // Супервизор 2.0.0 уже принял эстафету, его дочерний не стал здоровым
        _install.MarkHealthy("1.0.0");
        Layout.SetActive("1.0.0");
        Layout.SetActive("2.0.0");
        var launcher = new FakeLauncher(_clock, v => v == "2.0.0" ? ChildScript.Broken : ChildScript.Healthy);
        var handoff = new FakeHandoff(_events, accept: true);
        var supervisor = Supervisor(launcher, handoff, own: "2.0.0");

        await Run(supervisor, _stop.Token);

        launcher.Versions.Should().Equal("2.0.0");
        launcher.Started[0].Killed.Should().BeTrue();
        Layout.ReadActive().Should().Be("1.0.0");
        Layout.IsBad("2.0.0", _clock.Now).Should().BeTrue("битая версия сутки не пробуется — цикла туда-обратно нет");
        supervisor.HandedOffTo.Should().Be("1.0.0");
        _events.Should().Equal("repoint:1.0.0", "repoint:1.0.0", "handoff:1.0.0");
    }

    [Fact]
    public async Task Без_своей_версии_эстафеты_нет()
    {
        Layout.SetActive("2.0.0");
        _install.MarkHealthy("2.0.0");
        var launcher = new FakeLauncher(_clock, _ => ChildScript.Healthy) { StopAt = (1, _stop) };
        var handoff = new FakeHandoff(_events, accept: true);
        var supervisor = new AgentSupervisor(Layout, launcher, _ => { }, NullLogger.Instance, clock: _clock, handoff: handoff);

        await Run(supervisor, _stop.Token);

        handoff.Versions.Should().BeEmpty("собранный из исходников супервизор отдавать эстафету некому");
    }

    [Theory]
    [InlineData(new string[0], true, null)]
    [InlineData(new[] { "--takeover", "4242" }, true, 4242)]
    [InlineData(new[] { "--takeover" }, false, null)]
    [InlineData(new[] { "--takeover", "abc" }, false, null)]
    [InlineData(new[] { "--takeover", "0" }, false, null)]
    [InlineData(new[] { "--other" }, false, null)]
    public void Аргументы_supervise(string[] args, bool ok, int? from)
    {
        SupervisorHandoffs.ParseArgs(args).Should().Be((ok, from));
    }
}
