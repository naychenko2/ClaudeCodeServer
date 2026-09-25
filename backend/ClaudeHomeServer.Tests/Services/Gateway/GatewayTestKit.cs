using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Llm.Gateway;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace ClaudeHomeServer.Tests.Services.Gateway;

// Шпион на пуле: считает оба входа выбора. Сторожа G2/G11 требуют «ни разу», а не «не выбрал».
public sealed class SpyPool(IConfiguration config, UsageService? usage = null) : ClaudeSubscriptionPool(config, usage)
{
    public int PickCalls;
    public int PickSetupTokenCalls;

    public override string Pick(string? model = null)
    {
        Interlocked.Increment(ref PickCalls);
        return base.Pick(model);
    }

    public override string? PickSetupToken(string? model = null)
    {
        Interlocked.Increment(ref PickSetupTokenCalls);
        return base.PickSetupToken(model);
    }
}

// IOptionsMonitor с переключаемым значением — «выключение без рестарта».
public sealed class MutableOptionsMonitor<T>(T value) : IOptionsMonitor<T>
{
    public T CurrentValue { get; set; } = value;
    public T Get(string? name) => CurrentValue;
    public IDisposable? OnChange(Action<T, string?> listener) => null;
}

// Пул и реестр провайдеров на временном каталоге. Записи пула: key → (OAuthToken, ApiKey).
public sealed class GatewayTestKit : IDisposable
{
    public string TempDir { get; } = Path.Combine(Path.GetTempPath(), "gw_" + Guid.NewGuid().ToString("N"));
    public IConfiguration Config { get; }
    public UsageService Usage { get; }
    public SpyPool Pool { get; }
    public LlmProviderRegistry Providers { get; }
    public MutableOptionsMonitor<LlmGatewayOptions> Options { get; } =
        new(new LlmGatewayOptions { Enabled = true, AllowSubscriptions = true, AnthropicBaseUrl = "https://anthropic.test" });
    public UpstreamSelector Selector { get; }

    public GatewayTestKit(params (string Key, string? OAuth, string? ApiKey)[] subs)
    {
        Directory.CreateDirectory(TempDir);
        var dict = new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(TempDir, "projects.json"),
            ["ClaudeUserProfileDir"] = Path.Combine(TempDir, "user-claude"),
            ["LlmProviders:deepseek:DisplayName"] = "DeepSeek",
            ["LlmProviders:deepseek:AnthropicBaseUrl"] = "https://deepseek.test/anthropic",
            ["LlmProviders:deepseek:ApiKey"] = "ds-key",
            ["LlmProviders:deepseek:SmallModel"] = "deepseek-v4-flash",
            ["LlmProviders:deepseek:Models:0:Id"] = "deepseek-v4-pro",
            ["LlmProviders:deepseek:Models:1:Id"] = "deepseek-v4-flash",
        };
        foreach (var (key, oauth, apiKey) in subs)
        {
            if (oauth is not null) dict[$"{ClaudeSubscriptionPool.Section}:{key}:OAuthToken"] = oauth;
            if (apiKey is not null) dict[$"{ClaudeSubscriptionPool.Section}:{key}:ApiKey"] = apiKey;
        }
        Config = new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
        Usage = new UsageService(Config);
        Pool = new SpyPool(Config, Usage);
        Providers = new LlmProviderRegistry(Config);
        Selector = new UpstreamSelector(Pool, Providers, Options);
    }

    public void Dispose()
    {
        if (Directory.Exists(TempDir)) Directory.Delete(TempDir, recursive: true);
    }
}
