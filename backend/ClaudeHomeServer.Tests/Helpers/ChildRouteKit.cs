using ClaudeHomeServer.Services.Llm;
using Microsoft.Extensions.Configuration;

namespace ClaudeHomeServer.Tests.Helpers;

/// <summary>
/// Пул подписок и реестр провайдеров для детского гейта (Т5a, И2′): ChildModeGate.LaunchMismatch сверяет
/// маршрут через ChildRoute, поэтому тестам с ключами вида «pool-key» нужен пул с такими записями (чистый
/// ApiKey). Дополнительно — провайдер deepseek с ценой и glm без цены, оба с ключом.
/// </summary>
internal static class ChildRouteKit
{
    public static (ClaudeSubscriptionPool Pool, LlmProviderRegistry Providers) For(params string[] poolKeys)
    {
        var d = new Dictionary<string, string?>
        {
            ["LlmProviders:deepseek:AnthropicBaseUrl"] = "https://api.deepseek.test/anthropic",
            ["LlmProviders:deepseek:ApiKey"] = "sk-ds",
            ["LlmProviders:deepseek:Models:0:Id"] = "deepseek-v4-pro",
            ["LlmProviders:deepseek:Models:0:PriceInMissPer1M"] = "0.5",
            ["LlmProviders:deepseek:Models:0:PriceOutPer1M"] = "1.0",
            ["LlmProviders:glm:AnthropicBaseUrl"] = "https://api.glm.test/anthropic",
            ["LlmProviders:glm:ApiKey"] = "sk-glm",
            ["LlmProviders:glm:Models:0:Id"] = "glm-5.2",
        };
        foreach (var key in poolKeys) d[$"{ClaudeSubscriptionPool.Section}:{key}:ApiKey"] = "sk-ant-" + key;
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(d).Build();
        return (new ClaudeSubscriptionPool(cfg), new LlmProviderRegistry(cfg));
    }
}
