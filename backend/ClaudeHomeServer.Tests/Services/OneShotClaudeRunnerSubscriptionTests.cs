using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Spend;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace ClaudeHomeServer.Tests.Services;

// Выбор аккаунта пула подписок для фоновых one-shot вызовов (теги, сводки, память,
// changelog): раньше OneShotClaudeRunner на родной модели Claude ВСЕГДА наследовал
// окружение сервера (BuildCliEnv возвращает env только для стороннего провайдера) —
// пул подписок для фона не существовал, и основной аккаунт бился в лимит при живой
// второй подписке. ResolveEnv — точка выбора; тестируем без запуска процесса (подменить
// его нечем, см. OneShotClaudeRunnerArgsTests).
public class OneShotClaudeRunnerSubscriptionTests : IDisposable
{
    private readonly string _tempDir;

    public OneShotClaudeRunnerSubscriptionTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "oneshot_sub_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    private (OneShotClaudeRunner Runner, ClaudeSubscriptionPool Pool) MkRunner(params string[] subKeys)
    {
        var dict = new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(_tempDir, "projects.json"),
        };
        foreach (var key in subKeys)
            dict[$"{ClaudeSubscriptionPool.Section}:{key}:OAuthToken"] = "token-" + key;
        var config = new ConfigurationBuilder().AddInMemoryCollection(dict).Build();

        var pool = new ClaudeSubscriptionPool(config);
        var providers = new LlmProviderRegistry(config);
        var activity = new SubscriptionActivityTracker();
        var runner = new OneShotClaudeRunner(providers, TestLauncherFactory.Instance, config,
            subscriptionPool: pool, activity: activity);
        return (runner, pool);
    }

    // --- ResolveEnv: выбор аккаунта пула для родной модели Claude ---

    [Fact]
    public void ПулПуст_EnvНеСтроится_ПоведениеКакРаньше()
    {
        var (runner, _) = MkRunner();

        var (env, poolSubKey) = runner.ResolveEnv("sonnet");

        env.Should().BeNull();
        poolSubKey.Should().BeNull();
    }

    [Fact]
    public void ПулСДвумяАккаунтами_ОдинИсчерпан_ВыбираетЗдоровый()
    {
        var (runner, pool) = MkRunner("second", "third");
        pool.MarkExhausted("second", DateTime.UtcNow.AddHours(2));

        var (env, poolSubKey) = runner.ResolveEnv("sonnet");

        poolSubKey.Should().Be("third");
        env.Should().NotBeNull();
        env!["CLAUDE_CONFIG_DIR"].Should().Contain("sub-third");
        env.Should().ContainKey("CLAUDE_CODE_OAUTH_TOKEN");
    }

    [Fact]
    public void СторонийПровайдер_ПулНеУчаствует_BuildCliEnvВетканеТронута()
    {
        var dict = new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(_tempDir, "projects.json"),
            ["LlmProviders:deepseek:ApiKey"] = "sk-test",
            ["LlmProviders:deepseek:AnthropicBaseUrl"] = "https://api.deepseek.com/anthropic",
            ["LlmProviders:deepseek:Models:0:Id"] = "deepseek-chat",
            [$"{ClaudeSubscriptionPool.Section}:second:OAuthToken"] = "token-second",
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
        var pool = new ClaudeSubscriptionPool(config);
        var providers = new LlmProviderRegistry(config);
        var runner = new OneShotClaudeRunner(providers, TestLauncherFactory.Instance, config,
            subscriptionPool: pool);

        var (env, poolSubKey) = runner.ResolveEnv("deepseek-chat");

        // BuildCliEnv нашёл стороннего провайдера раньше проверки пула — пул тут ни при чём
        poolSubKey.Should().BeNull();
        env.Should().NotBeNull();
        env!["ANTHROPIC_BASE_URL"].Should().Be("https://api.deepseek.com/anthropic");
    }

    [Fact]
    public void ПулНеЗадан_ПоведениеКакРаньше()
    {
        // Без subscriptionPool в конструкторе (как большинство одноразовых вызовов
        // в проде до этой фичи) — деградация тихая, без NRE.
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_tempDir, "projects.json"),
            }).Build();
        var runner = new OneShotClaudeRunner(new LlmProviderRegistry(config), TestLauncherFactory.Instance, config);

        var (env, poolSubKey) = runner.ResolveEnv("sonnet");

        env.Should().BeNull();
        poolSubKey.Should().BeNull();
    }

    // --- RecordSpend: атрибуция расхода выбранной подписке, а не всегда "claude" ---

    [Fact]
    public void RecordSpend_АккаунтПула_АтрибутируетсяЕмуАНеClaude()
    {
        var spend = new CollectingSpend();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_tempDir, "projects.json"),
            }).Build();
        var runner = new OneShotClaudeRunner(new LlmProviderRegistry(config), TestLauncherFactory.Instance,
            config, spend: spend);
        var result = new OneShotResult("текст", new OneShotUsage(10, 0, 0, 5, 0.01, "claude-sonnet-5"), 100);

        runner.RecordSpend(result, model: null, ownerId: "u1", label: "notes.tags", poolSubKey: "second");

        var rec = spend.Records.Should().ContainSingle().Subject;
        rec.Provider.Should().Be("second");
        rec.Source.Should().Be(SpendSources.OneShot);
    }

    [Fact]
    public void RecordSpend_БезВыбраннойПодписки_ПоведениеКакРаньше()
    {
        var spend = new CollectingSpend();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_tempDir, "projects.json"),
            }).Build();
        var runner = new OneShotClaudeRunner(new LlmProviderRegistry(config), TestLauncherFactory.Instance,
            config, spend: spend);
        var result = new OneShotResult("текст", new OneShotUsage(10, 0, 0, 5, 0.01, "claude-sonnet-5"), 100);

        runner.RecordSpend(result, model: null, ownerId: "u1", label: "notes.tags", poolSubKey: null);

        spend.Records.Should().ContainSingle().Which.Provider.Should().Be("claude");
    }

    private sealed class CollectingSpend : ISpendCollector
    {
        public List<SpendRecord> Records { get; } = [];
        public void Record(SpendRecord record) => Records.Add(record);
    }
}
