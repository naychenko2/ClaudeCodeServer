using ClaudeHomeServer.DeviceAgent.Install;
using ClaudeHomeServer.DeviceAgent.Tests.Supervision;

namespace ClaudeHomeServer.DeviceAgent.Tests.Install;

internal sealed class FakeRunRegistry : IRunRegistry
{
    public string? Value { get; set; }
    public string Location => @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run\AiHomeAgent";
    public string? Get() => Value;
    public void Set(string command) => Value = command;
    public void Delete() => Value = null;
}

internal sealed class FakeDetachedStarter(DetachedStartStatus status) : IDetachedStarter
{
    public List<(string Exe, string[] Args, string Dir)> Calls { get; } = [];

    public DetachedStart Start(string executable, IReadOnlyList<string> args, string workingDirectory)
    {
        Calls.Add((executable, [.. args], workingDirectory));
        return new DetachedStart(status, status == DetachedStartStatus.Started ? 4242 : 0, status == DetachedStartStatus.Failed ? "нет файла" : null);
    }
}

internal sealed class FakeCommandRunner : ICommandRunner
{
    public List<string> Commands { get; } = [];
    public Func<string, int> Code { get; set; } = _ => 0;

    public (int Code, string Output) Run(string file, IReadOnlyList<string> args)
    {
        var line = string.Join(' ', [file, .. args]);
        Commands.Add(line);
        return (Code(line), Code(line) == 0 ? "" : "Failed to connect to bus");
    }
}

/// <summary>Автозапуск (Р6): запись Run и unit systemd --user генерируются и снимаются без реальной ОС.</summary>
public sealed class AutostartTests : IDisposable
{
    private readonly TempInstall _install = new("1.2.0", "1.3.0");
    private readonly string _unitDir = Directory.CreateTempSubdirectory("agent-units-").FullName;

    public void Dispose()
    {
        _install.Dispose();
        Directory.Delete(_unitDir, recursive: true);
    }

    [Fact]
    public void Run_указывает_на_supervise_версии_путём_в_кавычках()
    {
        WindowsRunAutostart.CommandFor(@"C:\Users\Иван Петров\AppData\Local\AiHomeAgent\versions\1.2.0\ai-home-agent.exe")
            .Should().Be(@"""C:\Users\Иван Петров\AppData\Local\AiHomeAgent\versions\1.2.0\ai-home-agent.exe"" supervise");
    }

    [Fact]
    public void Run_регистрируется_переводится_при_откате_и_снимается()
    {
        var registry = new FakeRunRegistry();
        var autostart = new WindowsRunAutostart(_install.Layout, registry, new FakeDetachedStarter(DetachedStartStatus.Started));

        var result = autostart.Register("1.3.0", alwaysOn: false);
        result.Registered.Should().BeTrue();
        registry.Value.Should().Be($"\"{_install.Layout.ExeOf("1.3.0")}\" supervise");

        autostart.Repoint("1.2.0");
        registry.Value.Should().Be($"\"{_install.Layout.ExeOf("1.2.0")}\" supervise");

        autostart.Unregister();
        registry.Value.Should().BeNull();
        autostart.Repoint("1.3.0");
        registry.Value.Should().BeNull("откат не прописывает автозапуск, которого нет (ручной режим, uninstall)");
    }

    [Fact]
    public void Run_always_on_честно_говорит_что_без_входа_агент_не_живёт()
    {
        var autostart = new WindowsRunAutostart(_install.Layout, new FakeRunRegistry(), new FakeDetachedStarter(DetachedStartStatus.Started));
        autostart.Register("1.2.0", alwaysOn: true).Notes.Should().ContainSingle().Which.Should().Contain("пока пользователь вошёл");
    }

    [Fact]
    public void Запрещённый_breakaway_даёт_отложенный_запуск_а_не_ошибку()
    {
        var starter = new FakeDetachedStarter(DetachedStartStatus.BreakawayDenied);
        var autostart = new WindowsRunAutostart(_install.Layout, new FakeRunRegistry(), starter);

        var start = autostart.StartNow("1.2.0");

        start.Status.Should().Be(SupervisorStartStatus.Deferred);
        start.Message.Should().Contain("при следующем входе");
        starter.Calls.Should().ContainSingle().Which.Args.Should().Equal("supervise");
        starter.Calls[0].Exe.Should().Be(_install.Layout.ExeOf("1.2.0"));
    }

    [Fact]
    public void Unit_systemd_запускает_supervise_через_симлинк_current()
    {
        var unit = SystemdUserAutostart.RenderUnit("/home/ivan/.local/share/ai-home-agent/current/ai-home-agent",
            new Dictionary<string, string> { ["XDG_DATA_HOME"] = "/data/ivan" });

        unit.Should().Be("""
            [Unit]
            Description=AI Home: агент устройства

            [Service]
            Type=simple
            ExecStart="/home/ivan/.local/share/ai-home-agent/current/ai-home-agent" supervise
            Restart=on-failure
            RestartSec=5
            Environment="XDG_DATA_HOME=/data/ivan"

            [Install]
            WantedBy=default.target

            """.Replace("\r\n", "\n"));
    }

    [Fact]
    public void Unit_экранирует_то_что_systemd_раскрыл_бы_сам()
    {
        var unit = SystemdUserAutostart.RenderUnit("/home/a b/$HOME/100%/\"q\"/ai-home-agent", new Dictionary<string, string>());
        unit.Should().Contain("""ExecStart="/home/a b/$$HOME/100%%/\"q\"/ai-home-agent" supervise""");

        var act = () => SystemdUserAutostart.RenderUnit("/x\n[Service]\nExecStartPre=/bin/evil", new Dictionary<string, string>());
        act.Should().Throw<ArgumentException>("перевод строки дописал бы в unit свою секцию");
    }

    [Fact]
    public void Systemd_регистрация_пишет_unit_включает_и_запускает()
    {
        var runner = new FakeCommandRunner();
        var autostart = new SystemdUserAutostart(_install.Layout, runner, _unitDir, new Dictionary<string, string>());

        var result = autostart.Register("1.2.0", alwaysOn: false);
        var start = autostart.StartNow("1.2.0");

        result.Registered.Should().BeTrue();
        autostart.ExecutablePath.Should().Be(Path.Combine(_install.Root, "current", ClaudeHomeServer.DeviceAgent.Supervision.SupervisorContract.ExecutableName));
        File.ReadAllText(autostart.UnitFile).Should().Contain($"ExecStart=\"{autostart.ExecutablePath.Replace("\\", "\\\\")}\" supervise");
        start.Status.Should().Be(SupervisorStartStatus.Started);
        runner.Commands.Should().Equal(
            "systemctl --user show-environment",
            "systemctl --user daemon-reload",
            "systemctl --user enable ai-home-agent.service",
            "systemctl --user restart ai-home-agent.service");
    }

    [Fact]
    public void Always_on_включает_linger_а_отказ_polkit_превращает_в_команду_для_человека()
    {
        var runner = new FakeCommandRunner { Code = c => c.StartsWith("loginctl") ? 1 : 0 };
        var autostart = new SystemdUserAutostart(_install.Layout, runner, _unitDir, new Dictionary<string, string>());

        var result = autostart.Register("1.2.0", alwaysOn: true);

        result.Registered.Should().BeTrue("linger не включился, но автозапуск при входе прописан");
        runner.Commands.Should().Contain("loginctl enable-linger");
        result.Notes.Should().ContainSingle().Which.Should().Contain("sudo loginctl enable-linger");
    }

    [Fact]
    public void Без_systemd_user_инструкция_вместо_ошибки()
    {
        var runner = new FakeCommandRunner { Code = c => c.Contains("show-environment") ? 1 : 0 };
        var autostart = new SystemdUserAutostart(_install.Layout, runner, _unitDir, new Dictionary<string, string>());

        var result = autostart.Register("1.2.0", alwaysOn: false);
        var start = autostart.StartNow("1.2.0");

        result.Registered.Should().BeFalse();
        result.Notes.Should().ContainSingle().Which.Should().Contain("supervise");
        start.Status.Should().Be(SupervisorStartStatus.Manual);
        File.Exists(autostart.UnitFile).Should().BeFalse();
        runner.Commands.Should().Equal(["systemctl --user show-environment"], "без менеджера ничего больше не зовётся");
    }

    [Fact]
    public void Systemd_снятие_гасит_сервис_и_удаляет_unit()
    {
        var runner = new FakeCommandRunner();
        var autostart = new SystemdUserAutostart(_install.Layout, runner, _unitDir, new Dictionary<string, string>());
        autostart.Register("1.2.0", alwaysOn: false);
        runner.Commands.Clear();

        autostart.Unregister();

        File.Exists(autostart.UnitFile).Should().BeFalse();
        runner.Commands.Should().Equal("systemctl --user disable --now ai-home-agent.service", "systemctl --user daemon-reload");
    }
}
