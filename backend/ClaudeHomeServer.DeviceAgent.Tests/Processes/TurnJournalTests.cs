using System.Runtime.Versioning;
using ClaudeHomeServer.DeviceAgent.Processes;
using ClaudeHomeServer.DeviceAgent.Tests.Exec;

namespace ClaudeHomeServer.DeviceAgent.Tests.Processes;

/// <summary>Смерть агента не оставляет живых ходов: следующий старт добивает уцелевших.</summary>
public class TurnJournalTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("journal-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [SkippableFact]
    [UnsupportedOSPlatform("windows")]
    public async Task Старт_агента_добивает_ход_переживший_прошлый_запуск_и_убирает_его_следы()
    {
        Skip.If(OperatingSystem.IsWindows(), "на Windows ходы гасит закрытие Job Object вместе с агентом");
        Skip.If(UnixGroupProcess.FindSetsid() is null);
        var work = Directory.CreateDirectory(Path.Combine(_root, "work")).FullName;
        var profile = Path.Combine(_root, "profile");
        var turnDir = Directory.CreateDirectory(Path.Combine(_root, "turns", "t1")).FullName;

        // «Прошлая жизнь»: ход запущен, агент умер, не убрав за собой — процесс брошен
        var orphan = TurnProcess.Start(new TurnLaunch(FakeUnixCli.Write(work), [], work, new Dictionary<string, string>
        {
            ["PATH"] = "/usr/bin:/bin",
            ["CLAUDE_CONFIG_DIR"] = profile,
        }));
        var grandchildFile = Path.Combine(work, "grandchild.pid");
        for (var i = 0; i < 100 && !File.Exists(grandchildFile); i++) await Task.Delay(50);
        await Task.Delay(100);
        var grandchild = int.Parse(File.ReadAllText(grandchildFile).Trim());

        var journal = new TurnJournal(Path.Combine(_root, "journal"));
        journal.Add(new TurnJournal.Entry("t1", orphan.Id, orphan.StartTimeUtc, turnDir));

        var killed = new TurnJournal(Path.Combine(_root, "journal"))
            .SweepLeftovers(new SessionFileJanitor(profile));

        killed.Should().Be(1);
        await orphan.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        for (var i = 0; i < 100 && UnixGroupProcess.IsAlive(grandchild); i++) await Task.Delay(50);
        UnixGroupProcess.IsAlive(grandchild).Should().BeFalse();
        Directory.EnumerateFiles(Path.Combine(profile, "sessions")).Should().BeEmpty();
        Directory.Exists(turnDir).Should().BeFalse();
        journal.ReadAll().Should().BeEmpty();
        orphan.Dispose();
    }

    [SkippableFact]
    public async Task Чужой_процесс_с_тем_же_pid_не_трогается()
    {
        Skip.If(OperatingSystem.IsWindows());
        using var stranger = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("sleep", "30"))!;
        var journal = new TurnJournal(Path.Combine(_root, "journal"));
        // Время старта не совпадает — это не наш ход, pid просто достался другому
        journal.Add(new TurnJournal.Entry("t2", stranger.Id, DateTime.UtcNow.AddHours(-3), null));

        journal.SweepLeftovers(new SessionFileJanitor(Path.Combine(_root, "profile"))).Should().Be(0);
        await Task.Delay(100);
        stranger.HasExited.Should().BeFalse();
        stranger.Kill();
    }

    [Theory]
    [InlineData("1234.json", true)]
    [InlineData("1234.0a93f2dd79eba2fbd4dde0a52929ffa0.key", true)]
    [InlineData("12345.json", false)]
    [InlineData("1234.json.bak", false)]
    [InlineData("1234.a.b.key", false)]
    [InlineData("1234..key", false)]
    [InlineData("123.json", false)]
    public void Уборщик_сессий_берёт_только_точный_pid(string name, bool expected) =>
        SessionFileJanitor.IsSessionFileOf(name, "1234.").Should().Be(expected);

    [Fact]
    public void Уборщик_удаляет_json_и_key_убитого_pid_и_не_трогает_соседей()
    {
        var sessions = Directory.CreateDirectory(Path.Combine(_root, "profile", "sessions")).FullName;
        foreach (var f in new[] { "777.json", "777.abc123.key", "7777.json", "7777.abc.key", "778.json" })
            File.WriteAllText(Path.Combine(sessions, f), "x");

        new SessionFileJanitor(Path.Combine(_root, "profile")).CleanUp(777).Should().Be(2);

        Directory.EnumerateFiles(sessions).Select(Path.GetFileName)
            .Should().BeEquivalentTo("7777.json", "7777.abc.key", "778.json");
    }
}
