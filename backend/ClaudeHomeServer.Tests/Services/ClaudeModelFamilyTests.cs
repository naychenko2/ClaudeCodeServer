using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace ClaudeHomeServer.Tests.Services;

// Храним только семейство Claude: версию выбирает CLI по алиасу, окно 1M — сервер при запуске.
public class ClaudeModelFamilyTests : IDisposable
{
    private readonly string _tempDir =
        Path.Combine(Path.GetTempPath(), "model_family_tests_" + Guid.NewGuid().ToString("N"));

    public ClaudeModelFamilyTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    [Theory]
    // Версионные id и алиасы с окном — к семейству
    [InlineData("claude-fable-5-1[1m]", "fable")]
    [InlineData("claude-fable-5[1m]", "fable")]
    [InlineData("claude-opus-4-8", "opus")]
    [InlineData("claude-opus-5", "opus")]
    [InlineData("claude-opus-5-5[1m]", "opus")]
    [InlineData("claude-sonnet-5", "sonnet")]
    [InlineData("claude-3-5-sonnet-20241022", "sonnet")]
    [InlineData("claude-haiku-4-5-20251001", "haiku")]
    [InlineData("opus[1m]", "opus")]
    [InlineData("OPUS[1M]", "opus")]
    [InlineData("fable[1m]", "fable")]
    [InlineData(" Sonnet ", "sonnet")]
    // Уже семейство — без изменений (идемпотентность на каноне)
    [InlineData("opus", "opus")]
    [InlineData("fable", "fable")]
    [InlineData("haiku", "haiku")]
    // default, чужие и незнакомые id — не трогаем
    [InlineData("default", "default")]
    [InlineData("glm-5.2", "glm-5.2")]
    [InlineData("glm-5.2[1m]", "glm-5.2[1m]")]
    [InlineData("kimi-k3", "kimi-k3")]
    [InlineData("deepseek/deepseek-v4-pro", "deepseek/deepseek-v4-pro")]
    [InlineData("anthropic/claude-opus-4", "anthropic/claude-opus-4")]
    [InlineData("claude-mythos-5", "claude-mythos-5")]
    [InlineData("opusplan", "opusplan")]
    [InlineData("", "")]
    [InlineData(null, null)]
    public void Canonicalize_СводитКСемейству(string? input, string? expected)
    {
        var once = ClaudeModelFamily.Canonicalize(input);
        once.Should().Be(expected);
        ClaudeModelFamily.Canonicalize(once).Should().Be(once, "повторное сведение ничего не меняет");
    }

    [Fact]
    public void Supports1M_УHaikuНет_УОстальныхЕсть()
    {
        ClaudeModelFamily.All.Select(f => f.Alias).Should().Equal("opus", "fable", "sonnet", "haiku");
        ClaudeModelFamily.All.Where(f => f.Supports1M).Select(f => f.Alias)
            .Should().BeEquivalentTo("opus", "fable", "sonnet");
    }

    private static LlmProviderRegistry Registry() => new(TestConfig.Build(new Dictionary<string, string?>
    {
        ["LlmProviders:glm:DisplayName"] = "GLM",
        ["LlmProviders:glm:AnthropicBaseUrl"] = "https://api.z.ai/api/anthropic",
        ["LlmProviders:glm:ApiKey"] = "sk-glm",
        ["LlmProviders:glm:Models:0:Id"] = "glm-5.2",
        // Сторонний провайдер, объявивший id вида claude-* (прокси/агрегатор): не сводим
        ["LlmProviders:proxy:DisplayName"] = "Proxy",
        ["LlmProviders:proxy:AnthropicBaseUrl"] = "https://proxy.example/anthropic",
        ["LlmProviders:proxy:ApiKey"] = "sk-proxy",
        ["LlmProviders:proxy:Models:0:Id"] = "claude-opus-4-8-proxy",
    }));

    [Theory]
    [InlineData("glm-5.2")]
    [InlineData("claude-opus-4-8-proxy")]
    public void CanonicalizeModel_МодельСтороннегоПровайдера_НеТрогает(string model)
    {
        Registry().CanonicalizeModel(model).Should().Be(model);
    }

    [Fact]
    public void CanonicalizeModel_РоднойClaude_Сводит()
    {
        Registry().CanonicalizeModel("claude-fable-5-1[1m]").Should().Be("fable");
    }

    [Theory]
    [InlineData("opus")]
    [InlineData("fable")]
    [InlineData("sonnet")]
    [InlineData("haiku")]
    public void ModelTierAlias_ПинитСемейство(string family)
    {
        Registry().ModelTierAlias(family).Should().Be(family);
        Registry().ModelTierAlias($"claude-{family}-9-9").Should().Be(family);
    }

    [Theory]
    [InlineData("fable")]
    [InlineData("fable[1m]")]
    public void IsNativeClaudeModel_УзнаётFable(string model)
    {
        LlmProviderRegistry.IsNativeClaudeModel(model).Should().BeTrue();
    }

    // Семейство ни в одной форме не уходит в ANTHROPIC_MODEL: CLI резолвит алиас только во
    // флаге --model, а из env он уезжает в API сырым id («issue with the selected model»).
    public static TheoryData<string> AllFamilyForms()
    {
        var data = new TheoryData<string>();
        foreach (var f in ClaudeModelFamily.All)
        {
            data.Add(f.Alias);
            data.Add(f.WindowAlias);
        }
        data.Add("default");
        return data;
    }

    [Theory]
    [MemberData(nameof(AllFamilyForms))]
    public void Env_СемействоНикогдаНеСтавитAnthropicModel(string model)
    {
        var registry = Registry();
        registry.BuildCliEnv(model).Should().BeNull("родной Claude env провайдера не получает");
        var oauth = registry.BuildOAuthCliEnv("second", "tok", model: model)!;
        oauth.Should().NotContainKey("ANTHROPIC_MODEL");
        oauth.Should().NotContainKey("ANTHROPIC_DEFAULT_OPUS_MODEL");
    }

    [Fact]
    public void BuildCliEnv_СтороннийПровайдер_МаппитАлиасFable()
    {
        var env = Registry().BuildCliEnv("glm-5.2")!;
        env["ANTHROPIC_DEFAULT_FABLE_MODEL"].Should().Be("glm-5.2");
    }

    private ClaudeSubscriptionPool Pool(params (string Key, bool Supports1M)[] subs)
    {
        var dict = new Dictionary<string, string?> { ["DataPath"] = Path.Combine(_tempDir, "projects.json") };
        foreach (var (key, s1m) in subs)
        {
            dict[$"{ClaudeSubscriptionPool.Section}:{key}:OAuthToken"] = "token-" + key;
            dict[$"{ClaudeSubscriptionPool.Section}:{key}:Supports1M"] = s1m ? "true" : "false";
        }
        return new ClaudeSubscriptionPool(new ConfigurationBuilder().AddInMemoryCollection(dict).Build());
    }

    [Theory]
    [InlineData("opus", "opus[1m]")]
    [InlineData("fable", "fable[1m]")]
    [InlineData("sonnet", "sonnet[1m]")]
    [InlineData("haiku", "haiku")]
    [InlineData("haiku[1m]", "haiku")]
    [InlineData("opus[1m]", "opus[1m]")]
    [InlineData("glm-5.2", "glm-5.2")]
    [InlineData("claude-mythos-5", "claude-mythos-5")]
    [InlineData(null, null)]
    public void LaunchModel_ПулС1M_ДописываетОкно(string? model, string? expected)
    {
        Pool(("max", true)).LaunchModel(model).Should().Be(expected);
        Pool().LaunchModel(model).Should().Be(expected, "пустой пул — локальный Claude, окно есть");
    }

    [Theory]
    [InlineData("opus", "opus")]
    [InlineData("opus[1m]", "opus")]
    [InlineData("fable", "fable")]
    [InlineData("haiku", "haiku")]
    public void LaunchModel_ПулБез1M_ОкнаНет(string model, string expected)
    {
        Pool(("pro", false)).LaunchModel(model).Should().Be(expected);
    }

    [Fact]
    public void LaunchModel_КлючПодпискиХода_РешаетЕёПлан()
    {
        var pool = Pool(("max", true), ("pro", false));
        pool.LaunchModel("opus").Should().Be("opus[1m]", "без ключа — хоть одна живая подписка тянет");
        pool.LaunchModel("opus", "max").Should().Be("opus[1m]");
        pool.LaunchModel("sonnet", "pro").Should().Be("sonnet", "ход на Pro не получает [1m]");
        pool.LaunchModel("haiku", "max").Should().Be("haiku");
        LlmProviderRegistry.ClaudeContextWindow(pool.LaunchModel("sonnet", "pro")).Should().Be(200_000);
        LlmProviderRegistry.ClaudeContextWindow(pool.LaunchModel("opus", "max")).Should().Be(1_000_000);
    }
}
