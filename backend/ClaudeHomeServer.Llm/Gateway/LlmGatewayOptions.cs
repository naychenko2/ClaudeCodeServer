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

    // Выход наружу собственного трафика CLI устройства (задача 2.9).
    public EgressGatewayOptions Egress { get; set; } = new();
}

public sealed class EgressGatewayOptions
{
    public static readonly IReadOnlyList<int> DefaultPorts = [443];

    // false — туннель отвечает 403 с причиной; прямого выхода с устройства взамен нет.
    public bool Enabled { get; set; }

    // Порты назначения. Пусто — только 443. Массив не инициализирован намеренно: биндер
    // дописал бы значения конфига к значению по умолчанию, а не заменил его.
    public int[]? AllowedPorts { get; set; }

    public IReadOnlyList<int> EffectivePorts => AllowedPorts is { Length: > 0 } ports ? ports : DefaultPorts;
}
