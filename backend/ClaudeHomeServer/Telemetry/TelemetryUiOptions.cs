namespace ClaudeHomeServer.Telemetry;

/// <summary>
/// Настройки встроенного раздела «Телеметрия» — проброса SigNoz UI через CCS
/// (секция <c>Telemetry:Ui</c>).
///
/// Проброс идёт same-origin: фронт грузит <c>&lt;iframe src="/telemetry-proxy/"&gt;</c>,
/// а бэкенд форвардит запросы на локальный SigNoz. Так телеметрия доступна и удалённо
/// (с телефона через PWA), а не только на хост-машине, где крутится docker. Раздел и
/// проброс видны ТОЛЬКО админам — телеметрия админская, как и алерты.
///
/// Регистрируется ВСЕГДА (в отличие от <see cref="Alerts.AlertsOptions"/>), даже когда
/// выключена: контроллер статуса должен уметь ответить <c>configured:false</c>, а
/// middleware проброса — отдать 503, а не пропасть из pipeline.
/// </summary>
public sealed record TelemetryUiOptions
{
    /// <summary>
    /// Включён ли раздел и проброс. Выключено — раздел показывает заглушку
    /// «настрой, администратор», а проброс <c>/telemetry-proxy/**</c> отвечает 503.
    /// </summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// Куда форвардить — хостовый адрес SigNoz UI (порт из overlay
    /// <c>docker-compose.observability.yml</c>, по умолчанию проброшен на 3301).
    /// Внутренний, а не внешний: браузер ходит на наш origin <c>/telemetry-proxy/</c>,
    /// а серверный форвард идёт уже сюда. Дефолт совпадает с
    /// <see cref="Alerts.AlertsOptions.SignozUrl"/>, но задаётся отдельно — проброс UI
    /// осмыслен и когда доставка алертов выключена.
    /// </summary>
    public string InternalUrl { get; init; } = "http://127.0.0.1:3301";

    public static TelemetryUiOptions FromConfig(IConfiguration config)
    {
        var section = config.GetSection("Telemetry:Ui");
        var url = section.GetValue<string>("InternalUrl");
        return new TelemetryUiOptions
        {
            Enabled = section.GetValue<bool>("Enabled"),
            // Пустую строку в конфиге тоже откатываем к дефолту: пустой InternalUrl
            // означал бы форвард в никуда, а не «отключено».
            InternalUrl = string.IsNullOrWhiteSpace(url) ? "http://127.0.0.1:3301" : url,
        };
    }
}
