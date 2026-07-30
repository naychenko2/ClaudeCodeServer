using ClaudeHomeServer.Services.Execution;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services;

// Общая коллекция с LocalProcessRunnerEnvTests: оба манипулируют process-global
// Environment.SetEnvironmentVariable, параллельный прогон дал бы флаки.
[CollectionDefinition("SystemEnv")]
public class SystemEnvCollection;

// Правило сборки env хода песочницы (DockerProcessRunner.BuildTurnEnv). Как и в
// LocalProcessRunnerEnvTests, процессы/docker здесь не запускаются — проверяем только
// сборку словаря, поэтому тесты гоняются и на linux-раннере CI без настоящего docker CLI.
[Collection("SystemEnv")]
public class DockerProcessRunnerEnvTests
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "ccs-docker-env-tests-" + Guid.NewGuid().ToString("N"));

    private DockerProcessRunner CreateRunner(string ownerId = "owner-1")
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_tempDir, "projects.json"),
            })
            .Build();
        var sandbox = new SandboxManager(config, NullLogger<SandboxManager>.Instance);
        return new DockerProcessRunner(sandbox, ownerId);
    }

    [Fact]
    public void BuildTurnEnv_ПереписываетПрофильНаПесочный()
    {
        var runner = CreateRunner("owner-1");
        var hostProfile = Path.Combine(_tempDir, "claude-profiles", "sub-claude");

        var env = runner.BuildTurnEnv(new Dictionary<string, string> { ["CLAUDE_CONFIG_DIR"] = hostProfile });

        env["CLAUDE_CONFIG_DIR"].Should().Be($"{SandboxManager.ProfilesMount}/owner-1/sub-claude");
    }

    [Fact]
    public void BuildTurnEnv_БезПрофиля_КлючDefault()
    {
        var runner = CreateRunner("owner-1");

        var env = runner.BuildTurnEnv(specEnv: null);

        env["CLAUDE_CONFIG_DIR"].Should().Be($"{SandboxManager.ProfilesMount}/owner-1/default");
    }
}
