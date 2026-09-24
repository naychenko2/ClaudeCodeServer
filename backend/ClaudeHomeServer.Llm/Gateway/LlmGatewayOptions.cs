namespace ClaudeHomeServer.Services.Llm.Gateway;

// Секция LlmGateway (ADR-016, план §2 п. 9). Читается через IOptionsMonitor на каждом
// запросе: выключение не требует рестарта.
public sealed class LlmGatewayOptions
{
    public const string Section = "LlmGateway";

    // Шлюз целиком. false — маршруты /gw/t/{turnId}/llm/** отвечают 404.
    public bool Enabled { get; set; }

    // Подписки Claude через шлюз (только setup-token, ADR-016 §2). false — явный отказ,
    // пул подписок шлюз при этом не спрашивает вовсе.
    public bool AllowSubscriptions { get; set; }

    // Эндпоинт Anthropic для подписок.
    public string AnthropicBaseUrl { get; set; } = "https://api.anthropic.com";
}
