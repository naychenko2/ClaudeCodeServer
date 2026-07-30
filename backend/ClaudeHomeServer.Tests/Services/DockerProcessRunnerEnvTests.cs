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

    // Значение не важно — важен сам факт появления/отсутствия ключа в итоговом env
    private static IDisposable SystemOAuthToken(string? value)
    {
        var previous = Environment.GetEnvironmentVariable("CLAUDE_CODE_OAUTH_TOKEN");
        Environment.SetEnvironmentVariable("CLAUDE_CODE_OAUTH_TOKEN", value);
        return new Restore(previous);
    }

    private sealed class Restore(string? previous) : IDisposable
    {
        public void Dispose() => Environment.SetEnvironmentVariable("CLAUDE_CODE_OAUTH_TOKEN", previous);
    }

    [Fact]
    public void BuildTurnEnv_ДокладываетТокенПодпискиИзОкруженияБэкенда()
    {
        using var _ = SystemOAuthToken("токен-подписки");
        var runner = CreateRunner("owner-1");

        var env = runner.BuildTurnEnv(specEnv: null);

        env["CLAUDE_CODE_OAUTH_TOKEN"].Should().Be("токен-подписки");
    }

    [Fact]
    public void BuildTurnEnv_НеПеретираетТокенПулаИзSpecEnv()
    {
        using var _ = SystemOAuthToken("токен-подписки-бэкенда");
        var runner = CreateRunner("owner-1");

        var env = runner.BuildTurnEnv(new Dictionary<string, string> { ["CLAUDE_CODE_OAUTH_TOKEN"] = "токен-пула" });

        env["CLAUDE_CODE_OAUTH_TOKEN"].Should().Be("токен-пула");
    }

    [Theory]
    [InlineData("ANTHROPIC_AUTH_TOKEN")]
    [InlineData("ANTHROPIC_API_KEY")]
    public void BuildTurnEnv_НеДокладываетТокенХодуСAuthToken(string key)
    {
        using var _ = SystemOAuthToken("токен-подписки-бэкенда");
        var runner = CreateRunner("owner-1");

        var env = runner.BuildTurnEnv(new Dictionary<string, string> { [key] = "чужой-ключ" });

        env.Should().NotContainKey("CLAUDE_CODE_OAUTH_TOKEN",
            "токен подписки не должен уезжать ходу на чужой эндпоинт/аккаунт");
    }

    [Fact]
    public void BuildTurnEnv_БезТокенаВОкружении_КлючНеПоявляется()
    {
        using var _ = SystemOAuthToken(null);
        var runner = CreateRunner("owner-1");

        var env = runner.BuildTurnEnv(specEnv: null);

        env.Should().NotContainKey("CLAUDE_CODE_OAUTH_TOKEN");
    }
}
