using System.Text.Json;
using ClaudeHomeServer.Services.Deploy;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services;

// Linux-ветка Deploy (ADR-010, шаг 3): Misconfiguration по ОС (AgentDir + скрипт, а не
// TaskName) и честный отказ отката. Отдельный файл от DeployServiceTests: CI на Linux, и
// существующие тесты там явно держат IsLinux=false, а Linux-ветка — здесь.
public class DeployServiceLinuxTests : IDisposable
{
    private readonly string _tempDir;
    private readonly FakeDeployHost _host = new();

    public DeployServiceLinuxTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ccs_deploy_linux_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose() => TestFs.DeleteDirectoryResilient(_tempDir);

    // Хостовые операции подменены: на раннере CI нет ни systemd-run, ни боевого агента.
    // Предмет проверки — решения сервиса (Misconfiguration/откат), а не наличие утилит.
    private sealed class FakeDeployHost : IDeployHost
    {
        public DeployGitSnapshot Snapshot { get; set; } = new("4dc7ddab", [], null);
        public string? WakeError { get; set; }
        public int WakeCalls { get; private set; }

        public Task<DeployGitSnapshot> GitSnapshotAsync(string repoDir, CancellationToken ct = default) =>
            Task.FromResult(Snapshot);

        public Task<string?> WakeAgentAsync(DeployOptions options, string deployId, CancellationToken ct = default)
        {
            WakeCalls++;
            return Task.FromResult(WakeError);
        }

        public IDisposable? TryLockAgent(string releasesDir) => new Lease();
        private sealed class Lease : IDisposable { public void Dispose() { } }
    }

    // Linux-конфиг: IsLinux=true, AgentDir задан. createScript — создать deploy-linux.sh в
    // AgentDir (иначе Misconfiguration). taskName — чтобы проверить, что на Linux он не нужен.
    private DeployService BuildLinux(
        bool enabled = true,
        bool createScript = true,
        string? taskName = "CCS-Deploy-Test",
        Action<Dictionary<string, string?>>? tweak = null)
    {
        var agentDir = Path.Combine(_tempDir, "agent");
        Directory.CreateDirectory(agentDir);
        if (createScript)
        {
            var ops = Path.Combine(agentDir, "scripts", "ops");
            Directory.CreateDirectory(ops);
            File.WriteAllText(Path.Combine(ops, "deploy-linux.sh"), "#!/bin/sh\n");
        }

        var values = new Dictionary<string, string?>
        {
            ["Deploy:Enabled"] = enabled ? "true" : "false",
            ["Deploy:RepoDir"] = Path.Combine(_tempDir, "repo"),
            ["Deploy:AgentDir"] = agentDir,
            ["Deploy:PublishDir"] = Path.Combine(_tempDir, "publish"),
            ["Deploy:StagingDir"] = Path.Combine(_tempDir, "staging"),
            ["Deploy:ReleasesDir"] = Path.Combine(_tempDir, "releases"),
            ["Deploy:TaskName"] = taskName ?? "",
            ["Deploy:IsLinux"] = "true",
        };
        tweak?.Invoke(values);
        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        return new DeployService(config, _host, NullLogger<DeployService>.Instance);
    }

    private string StatePath => Path.Combine(_tempDir, "releases", DeployState.FileName);

    // ---------- Misconfiguration по ОС ----------

    // Linux без скрипта → Misconfigured с понятным текстом: на Linux проверяем AgentDir и
    // deploy-linux.sh, а не TaskName. Мутация: убери проверку File.Exists(script) в
    // Misconfiguration — тест краснеет (Misconfiguration вернёт null → заявка Accepted).
    [Fact]
    public async Task Linux_БезСкрипта_Misconfigured_С_Понятным_Текстом()
    {
        var deploy = BuildLinux(createScript: false);

        var result = await deploy.StartAsync(new DeployStartRequest(), "u1", null);

        result.Status.Should().Be(DeployStartStatus.Misconfigured);
        result.Error.Should().Contain("deploy-linux.sh", "текст называет недостающий скрипт");
        result.Error.Should().Contain("скрипт выкатки не найден", "текст объясняет, что именно не так");
        _host.WakeCalls.Should().Be(0, "до агента не доходим");
    }

    // Linux со скриптом, но БЕЗ TaskName → Accepted: на Linux имя задачи планировщика не
    // требуется (побудка — systemd-run, не schtasks). Мутация: верни проверку TaskName в
    // Linux-ветку Misconfiguration — тест краснеет (станет Misconfigured).
    [Fact]
    public async Task Linux_СоСкриптом_БезTaskName_Accepted()
    {
        var deploy = BuildLinux(createScript: true, taskName: "");

        var result = await deploy.StartAsync(new DeployStartRequest(), "u1", null);

        result.Status.Should().Be(DeployStartStatus.Accepted, "на Linux TaskName не нужен");
        _host.WakeCalls.Should().Be(1, "агент побуждён");
    }

    // ---------- Откат на Linux ----------

    // Linux rollback → Unsupported: отказ ДО проверки снимков, журнал не создан, агент не
    // побуждён. Мутация: убери Linux-ветку в EnqueueAsync (Options.IsLinux → Unsupported) —
    // тест краснеет (станет NoRelease, т.к. снимков нет).
    [Fact]
    public async Task Linux_Rollback_Отказ_ЖурналНеСоздан_АгентНеПобуждён()
    {
        var deploy = BuildLinux(createScript: true);

        var result = await deploy.RollbackAsync(null, "u1", null);

        result.Status.Should().Be(DeployStartStatus.Unsupported);
        result.Error.Should().Contain("Откат из чата на Linux пока не поддержан");
        result.Error.Should().Contain("/opt/ccs/releases");
        File.Exists(StatePath).Should().BeFalse("журнал не пишется при отказе отката");
        _host.WakeCalls.Should().Be(0, "агент не побуждён");
    }
}
