namespace ClaudeHomeServer.Telemetry.Incidents;

/// <summary>
/// Настройки разбора инцидентов (секция <c>Telemetry:Incidents</c>, доступ к SigNoz —
/// из <c>Telemetry:Alerts</c>).
///
/// Регистрируются ВСЕГДА — в отличие от <see cref="Alerts.AlertsOptions"/>, у которого
/// <c>IsUsable</c> гасит всю ветку доставки алертов. Причина та же, что у
/// <see cref="TelemetryUiOptions"/>: раздел «Инциденты» обязан уметь ответить
/// «телеметрия не настроена», а не пропасть из DI и уронить резолв контроллера.
/// Поэтому и <c>Telemetry:Alerts:Enabled</c> здесь НЕ учитывается: рассылка push и
/// чтение инцидентов — разные вещи, читать SigNoz осмысленно и на деве, где рассылка
/// намеренно выключена (подписки живут у боевого инстанса).
/// </summary>
public sealed record IncidentsOptions
{
    /// <summary>Внешний адрес SigNoz с base-path — тот же, что у доставки алертов.</summary>
    public string SignozUrl { get; init; } = "http://localhost:3301/telemetry-proxy";

    /// <summary>Ключ API SigNoz. Пусто — раздел отвечает «не настроено».</summary>
    public string? ApiKey { get; init; }

    /// <summary>
    /// Контур ЭТОГО инстанса (<c>Telemetry:Mode</c>). Алерт чужого контура разбирать
    /// по локальным данным нельзя — его чатов здесь нет и не будет (см. isForeignEnvironment).
    /// </summary>
    public string Environment { get; init; } = "dev";

    /// <summary>
    /// Окно разбора вокруг начала инцидента, минуты. Больше окна алерта (10–15 мин),
    /// чтобы в досье попало и то, что происходило до срабатывания порога.
    /// </summary>
    public int WindowMinutes { get; init; } = 60;

    /// <summary>Потолок ожидания одного запроса к SigNoz. Раздел открывается человеком — висеть нельзя.</summary>
    public int TimeoutSeconds { get; init; } = 20;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey)
                                && !string.IsNullOrWhiteSpace(SignozUrl);

    public TimeSpan Window => TimeSpan.FromMinutes(Math.Clamp(WindowMinutes, 5, 1440));

    public TimeSpan Timeout => TimeSpan.FromSeconds(Math.Clamp(TimeoutSeconds, 3, 60));

    public static IncidentsOptions FromConfig(IConfiguration config)
    {
        var alerts = config.GetSection("Telemetry:Alerts");
        var section = config.GetSection("Telemetry:Incidents");
        var url = section.GetValue<string>("SignozUrl")
                  ?? alerts.GetValue<string>("SignozUrl");
        return new IncidentsOptions
        {
            SignozUrl = string.IsNullOrWhiteSpace(url) ? "http://localhost:3301/telemetry-proxy" : url,
            ApiKey = section.GetValue<string>("ApiKey") ?? alerts.GetValue<string>("ApiKey"),
            Environment = config.GetValue<string>("Telemetry:Mode") ?? "dev",
            WindowMinutes = section.GetValue<int?>("WindowMinutes") ?? 60,
            TimeoutSeconds = section.GetValue<int?>("TimeoutSeconds") ?? 20,
        };
    }
}
