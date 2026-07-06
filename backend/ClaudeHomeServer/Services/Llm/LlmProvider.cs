namespace ClaudeHomeServer.Services.Llm;

public enum LlmProvider { Claude, DeepSeek }

// Провайдер выводится из модели и НЕ персистится: единственный источник правды — Session.Model.
// Смена модели автоматически «переключает» провайдера (для начатых сессий — запрещена в Update).
public static class LlmProviderResolver
{
    public static LlmProvider Resolve(string? model) =>
        model is not null && model.StartsWith("deepseek", StringComparison.OrdinalIgnoreCase)
            ? LlmProvider.DeepSeek
            : LlmProvider.Claude;
}
