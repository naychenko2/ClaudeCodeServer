using ClaudeHomeServer.Services.Execution;
using ClaudeHomeServer.Services.Llm;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace ClaudeHomeServer.Tests.Services;

// Окружение дочернего claude-процесса. Проверяем то, что иначе ломается молча: системная
// переменная машины (ANTHROPIC_BASE_URL от мастер-рубильника «весь Claude Code на GLM»,
// забытый setx, чужой эксперимент) наследуется процессом и уводит ход «на Claude» к чужому
// эндпоинту — вместе с токеном подписки, без единой ошибки в логах.
// Процессы здесь не запускаются намеренно: тесты гоняются и на linux-раннере CI, а проверять
// надо правило сборки окружения, а не факт запуска — потому Start распилен на BuildStartInfo.
// Коллекция SystemEnv — общая с DockerProcessRunnerEnvTests: оба манипулируют process-global
// Environment.SetEnvironmentVariable, xunit не должен гонять их параллельно (флаки).
[Collection("SystemEnv")]
public class LocalProcessRunnerEnvTests
{
    private static ProcessSpec Spec(
        IReadOnlyList<string>? clear = null,
        IReadOnlyDictionary<string, string>? env = null) => new()
        {
            FileName = "dummy",
            ClearEnv = clear,
            Env = env,
        };

    // Значения не важны — важно, что переменная исчезает из окружения процесса
    private static IDisposable SystemEnv(string key, string value)
    {
        Environment.SetEnvironmentVariable(key, value);
        return new Restore(key);
    }

    private sealed class Restore(string key) : IDisposable
    {
        public void Dispose() => Environment.SetEnvironmentVariable(key, null);
    }

    [Theory]
    [InlineData("ANTHROPIC_BASE_URL")]
    [InlineData("ANTHROPIC_AUTH_TOKEN")]
    [InlineData("ANTHROPIC_API_KEY")]
    [InlineData("ANTHROPIC_MODEL")]
    [InlineData("CLAUDE_CONFIG_DIR")]
    public void ClearEnv_ВыкидываетСистемнуюПеременнуюИзХода(string key)
    {
        using var _ = SystemEnv(key, "https://чужой-эндпоинт");

        var psi = LocalProcessRunner.BuildStartInfo(Spec(clear: LlmProviderRegistry.ProviderEnvKeys));

        psi.Environment.ContainsKey(key).Should().BeFalse(
            $"{key} задан на машине, но маршрут хода определяет сервер, а не окружение");
    }

    [Fact]
    public void ClearEnv_НеТрогаетТокенПодписки()
    {
        // На CLAUDE_CODE_OAUTH_TOKEN держится вход по подписке: его пробрасывают снаружи
        // (Runner берёт из реестра, docker — из окружения хоста). Выкинь его — и отвалится всё.
        using var _ = SystemEnv("CLAUDE_CODE_OAUTH_TOKEN", "токен-подписки");

        var psi = LocalProcessRunner.BuildStartInfo(Spec(clear: LlmProviderRegistry.ProviderEnvKeys));

        psi.Environment.Should().ContainKey("CLAUDE_CODE_OAUTH_TOKEN");
    }

    [Fact]
    public void Env_СильнееClearEnv_ОверрайдПровайдераПобеждает()
    {
        // Очистка идёт ДО применения Env: ход на стороннем провайдере обязан доехать
        // до его эндпоинта, даже если та же переменная задана на машине.
        using var _ = SystemEnv("ANTHROPIC_BASE_URL", "https://системный");

        var psi = LocalProcessRunner.BuildStartInfo(Spec(
            clear: LlmProviderRegistry.ProviderEnvKeys,
            env: new Dictionary<string, string> { ["ANTHROPIC_BASE_URL"] = "https://api.z.ai/api/anthropic" }));

        psi.Environment["ANTHROPIC_BASE_URL"].Should().Be("https://api.z.ai/api/anthropic");
    }

    [Fact]
    public void БезClearEnv_ПоведениеПрежнее_ПеременнаяНаследуется()
    {
        using var _ = SystemEnv("ANTHROPIC_BASE_URL", "https://системный");

        var psi = LocalProcessRunner.BuildStartInfo(Spec(clear: null));

        psi.Environment["ANTHROPIC_BASE_URL"].Should().Be("https://системный");
    }

    [Fact]
    public void InheritSystemEnv_ВозвращаетНаследование()
    {
        // Аварийный выключатель для машины, где ANTHROPIC_* заданы намеренно (свой шлюз к
        // Anthropic, работа по ANTHROPIC_API_KEY вместо подписки) — без пересборки продукта.
        var registry = Registry(inheritSystemEnv: true);

        registry.EnvKeysToClear.Should().BeEmpty();
    }

    [Fact]
    public void ПоУмолчанию_КлючиПровайдерскогоРежимаЧистятся()
    {
        var registry = Registry(inheritSystemEnv: false);

        registry.EnvKeysToClear.Should().BeEquivalentTo(LlmProviderRegistry.ProviderEnvKeys);
        registry.EnvKeysToClear.Should().NotContain("CLAUDE_CODE_OAUTH_TOKEN");
    }

    private static LlmProviderRegistry Registry(bool inheritSystemEnv)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Claude:InheritSystemEnv"] = inheritSystemEnv ? "true" : "false",
            })
            .Build();
        return new LlmProviderRegistry(config);
    }
}
