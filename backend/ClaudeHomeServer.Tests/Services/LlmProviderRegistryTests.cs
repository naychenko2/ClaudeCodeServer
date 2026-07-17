using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Llm;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace ClaudeHomeServer.Tests.Services;

public class LlmProviderRegistryTests
{
    private static LlmProviderRegistry Create(Dictionary<string, string?>? extra = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["LlmProviders:deepseek:DisplayName"] = "DeepSeek",
            ["LlmProviders:deepseek:AnthropicBaseUrl"] = "https://api.deepseek.com/anthropic",
            ["LlmProviders:deepseek:ApiBaseUrl"] = "https://api.deepseek.com",
            ["LlmProviders:deepseek:ApiKey"] = "sk-test",
            ["LlmProviders:deepseek:SmallModel"] = "deepseek-v4-flash",
            ["LlmProviders:deepseek:Models:0:Id"] = "deepseek-v4-pro",
            ["LlmProviders:deepseek:Models:0:DisplayName"] = "DeepSeek Pro",
            ["LlmProviders:deepseek:Models:0:PriceInMissPer1M"] = "0.5",
            ["LlmProviders:deepseek:Models:0:PriceInHitPer1M"] = "0.1",
            ["LlmProviders:deepseek:Models:0:PriceOutPer1M"] = "1.0",
            ["LlmProviders:deepseek:SupportsImages"] = "false",
            // GLM без ключа — выключен
            ["LlmProviders:glm:DisplayName"] = "GLM",
            ["LlmProviders:glm:AnthropicBaseUrl"] = "https://api.z.ai/api/anthropic",
            ["LlmProviders:glm:ExtraEnv:API_TIMEOUT_MS"] = "3000000",
            ["LlmProviders:glm:Models:0:Id"] = "glm-5.2",
        };
        foreach (var (k, v) in extra ?? []) settings[k] = v;
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        return new LlmProviderRegistry(config);
    }

    [Fact]
    public void ResolveByModel_МодельИзКаталога_НаходитПровайдера()
    {
        Create().ResolveByModel("deepseek-v4-pro")!.Key.Should().Be("deepseek");
    }

    [Fact]
    public void ResolveByModel_ПоПрефиксу_НаходитПровайдера()
    {
        // Модель не из конфига (пришла из GET /models) — резолв по префиксу ключа
        Create().ResolveByModel("deepseek-v5-super")!.Key.Should().Be("deepseek");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("opus")]
    [InlineData("claude-sonnet-5")]
    public void ResolveByModel_РодныеМоделиClaude_Null(string? model)
    {
        Create().ResolveByModel(model).Should().BeNull();
        Create().ProviderKey(model).Should().Be("claude");
    }

    [Theory]
    // Тир-алиас + окно → базовый алиас (надёжен в любом окружении/аккаунте)
    [InlineData("opus[1m]", "opus")]
    [InlineData("OPUS[1M]", "opus")]
    [InlineData("sonnet[1m]", "sonnet")]
    [InlineData("haiku[1m]", "haiku")]
    // Базовые алиасы и обычные модели — без изменений
    [InlineData("opus", "opus")]
    [InlineData("claude-sonnet-5", "claude-sonnet-5")]
    // Полный id с окном и модель стороннего провайдера — НЕ трогаем
    [InlineData("claude-fable-5[1m]", "claude-fable-5[1m]")]
    [InlineData("glm-5.2[1m]", "glm-5.2[1m]")]
    public void StripClaudeWindowAlias_СводитТолькоТирАлиасы(string input, string expected)
    {
        LlmProviderRegistry.StripClaudeWindowAlias(input).Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void StripClaudeWindowAlias_ПустоеБезИзменений(string? input)
    {
        LlmProviderRegistry.StripClaudeWindowAlias(input).Should().Be(input);
    }

    [Fact]
    public void ResolveByModel_ВыключенныйПровайдер_ВсёРавноРезолвится()
    {
        // Иначе guard смены провайдера и сообщение «не настроен» не отличат GLM от Claude
        Create().ResolveByModel("glm-5.2")!.Key.Should().Be("glm");
    }

    [Fact]
    public void BuildCliEnv_Claude_Null()
    {
        Create().BuildCliEnv("sonnet").Should().BeNull();
    }

    [Fact]
    public void BuildCliEnv_DeepSeek_ПолныйНаборEnv()
    {
        var env = Create().BuildCliEnv("deepseek-v4-pro")!;
        env["ANTHROPIC_BASE_URL"].Should().Be("https://api.deepseek.com/anthropic");
        env["ANTHROPIC_AUTH_TOKEN"].Should().Be("sk-test");
        env["ANTHROPIC_API_KEY"].Should().Be("sk-test");
        // Изоляция от OAuth-логина хоста: у каждого провайдера свой профиль CLI
        env["CLAUDE_CONFIG_DIR"].Should().EndWith(Path.Combine("claude-profiles", "deepseek"));
        env["ANTHROPIC_MODEL"].Should().Be("deepseek-v4-pro");
        env["ANTHROPIC_DEFAULT_OPUS_MODEL"].Should().Be("deepseek-v4-pro");
        env["ANTHROPIC_DEFAULT_SONNET_MODEL"].Should().Be("deepseek-v4-pro");
        env["ANTHROPIC_DEFAULT_HAIKU_MODEL"].Should().Be("deepseek-v4-flash");
        env["CLAUDE_CODE_SUBAGENT_MODEL"].Should().Be("deepseek-v4-flash");
    }

    [Fact]
    public void BuildCliEnv_ExtraEnv_Добавляется()
    {
        var env = Create(new() { ["LlmProviders:glm:ApiKey"] = "zai-key" }).BuildCliEnv("glm-5.2")!;
        env["API_TIMEOUT_MS"].Should().Be("3000000");
        // SmallModel не задан — haiku-слот получает основную модель
        env["ANTHROPIC_DEFAULT_HAIKU_MODEL"].Should().Be("glm-5.2");
    }

    [Fact]
    public void BuildCliEnv_ПровайдерБезКлюча_Исключение()
    {
        var act = () => Create().BuildCliEnv("glm-5.2");
        act.Should().Throw<InvalidOperationException>().WithMessage("*не настроен*");
    }

    [Fact]
    public void BuildCliEnv_СинкОбщихНастроекВПрофиль_БезКреденшалов()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "llmreg_" + Guid.NewGuid().ToString("N"));
        var userDir = Path.Combine(tmp, "user-claude");
        Directory.CreateDirectory(Path.Combine(userDir, "rules"));
        File.WriteAllText(Path.Combine(userDir, "CLAUDE.md"), "# память");
        File.WriteAllText(Path.Combine(userDir, "settings.json"), "{}");
        File.WriteAllText(Path.Combine(userDir, "rules", "style.md"), "правила");
        // Креденшалы и транскрипты копироваться НЕ должны
        File.WriteAllText(Path.Combine(userDir, ".credentials.json"), "{\"oauth\":\"секрет\"}");

        try
        {
            var reg = Create(new()
            {
                ["ClaudeUserProfileDir"] = userDir,
                ["DataPath"] = Path.Combine(tmp, "data", "projects.json"),
            });
            var profile = reg.BuildCliEnv("deepseek-v4-pro")!["CLAUDE_CONFIG_DIR"];

            File.Exists(Path.Combine(profile, "CLAUDE.md")).Should().BeTrue();
            File.Exists(Path.Combine(profile, "settings.json")).Should().BeTrue();
            File.Exists(Path.Combine(profile, "rules", "style.md")).Should().BeTrue();
            File.Exists(Path.Combine(profile, ".credentials.json")).Should().BeFalse();
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { }
        }
    }

    [Fact]
    public void ComputeCost_ПоЦенамКонфига()
    {
        // 1M miss-входа + 1M hit-кэша + 1M выхода = 0.5 + 0.1 + 1.0
        var usage = new UsageInfo(1_000_000, 1_000_000, 1_000_000, 0);
        Create().ComputeCost("deepseek-v4-pro", usage).Should().BeApproximately(1.6, 0.0001);
    }

    [Fact]
    public void ComputeCost_БезЦен_Null()
    {
        var usage = new UsageInfo(1000, 1000, 0, 0);
        Create(new() { ["LlmProviders:glm:ApiKey"] = "zai-key" })
            .ComputeCost("glm-5.2", usage).Should().BeNull();
    }

    [Fact]
    public void CapabilitiesFor_ИзКонфига()
    {
        var caps = Create().CapabilitiesFor("deepseek-v4-pro");
        caps.Provider.Should().Be("deepseek");
        caps.DisplayName.Should().Be("DeepSeek");
        caps.SupportsImages.Should().BeFalse();
        caps.SupportsPlanMode.Should().BeTrue();
        caps.SupportsCompact.Should().BeTrue();
    }
}
