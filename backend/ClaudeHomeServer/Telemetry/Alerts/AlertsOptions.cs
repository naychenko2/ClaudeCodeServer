namespace ClaudeHomeServer.Telemetry.Alerts;

/// <summary>
/// Настройки доставки алертов (секция <c>Telemetry:Alerts</c>).
///
/// Рассылать push должен ТОЛЬКО тот инстанс, на который подписаны устройства: подписки
/// лежат в <c>data/</c> каждого инстанса отдельно, поэтому у дева и боевого они разные.
/// Включать имеет смысл там, где стоит PWA — обычно только на боевом.
/// </summary>
public sealed record AlertsOptions
{
    public bool Enabled { get; init; }

    /// <summary>Внешний адрес SigNoz — по нему же строятся ссылки в уведомлениях.</summary>
    public string SignozUrl { get; init; } = "http://localhost:3301";

    public string? ApiKey { get; init; }

    public int PollSeconds { get; init; } = 60;

    /// <summary>Без ключа опрос невозможен — тот же принцип, что у провайдера LLM с пустым ApiKey.</summary>
    public bool IsUsable => Enabled && !string.IsNullOrWhiteSpace(ApiKey)
                            && !string.IsNullOrWhiteSpace(SignozUrl);

    public TimeSpan Interval => TimeSpan.FromSeconds(Math.Clamp(PollSeconds, 15, 3600));

    public static AlertsOptions FromConfig(IConfiguration config)
    {
        var section = config.GetSection("Telemetry:Alerts");
        return new AlertsOptions
        {
            Enabled = section.GetValue<bool>("Enabled"),
            SignozUrl = section.GetValue<string>("SignozUrl") ?? "http://localhost:3301",
            ApiKey = section.GetValue<string>("ApiKey"),
            PollSeconds = section.GetValue<int?>("PollSeconds") ?? 60,
        };
    }
}
