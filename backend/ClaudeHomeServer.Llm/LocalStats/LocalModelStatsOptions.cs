namespace ClaudeHomeServer.Services.Llm;

// Секция LocalModelStats (фича «Статистика локальных моделей»). Читается через IOptionsMonitor:
// выключение не требует рестарта.
public sealed class LocalModelStatsOptions
{
    public const string Section = "LocalModelStats";

    // Панель статистики. false — поллер не ходит в /metrics, снимки не обновляются.
    public bool Enabled { get; set; }

    // Базовый интервал опроса /metrics.
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);

    // Интервал, когда движок активен (есть running/waiting запросы).
    public TimeSpan ActivePollInterval { get; set; } = TimeSpan.FromSeconds(1);

    // Интервал, когда движок мёртв (HTTP-ошибка).
    public TimeSpan DownPollInterval { get; set; } = TimeSpan.FromSeconds(15);

    // Окно истории снимков, которое держит поллер в памяти.
    public TimeSpan HistoryWindow { get; set; } = TimeSpan.FromHours(1);

    // TTL кэша снимка KV-кэша: при повторном запросе в пределах TTL не ходим в сеть.
    public TimeSpan GpuCacheTtl { get; set; } = TimeSpan.FromSeconds(2);

    // Файл помощника ccs-gddr6 (температура памяти GDDR6X и hotspot видеокарт, root-служба).
    // Путь по умолчанию — как в deploy/systemd/ccs-gddr6.service (RuntimeDirectory=ccs-gddr6).
    public string Gddr6HelperFile { get; set; } = "/run/ccs-gddr6/temps.json";

    // Окно, за которое считаем дельту счётчика preemptions в снимке Current.
    public TimeSpan PreemptionsWindow { get; set; } = TimeSpan.FromMinutes(15);
}
