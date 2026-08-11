using System.Collections.Concurrent;

namespace ClaudeHomeServer.Services.Llm;

// Кулдаун недоступности провайдера (волна 2 ADR-007): пассивный in-memory наблюдатель —
// запоминает, что провайдер вернул ошибку доставки класса Unreachable/ProviderError (либо
// исчерпал квоту — UsageLimit), и считает его «недоступным» TTL минут, чтобы не биться головой
// о мёртвый эндпоинт в следующих ходах и шагах цепочки. TTL зависит от причины: минуты на
// упавший эндпоинт, час на исчерпанную квоту (см. константы ниже). Не персистируется, в бэкап не едет, warmup-пингов
// сторонних провайдеров не шлёт — только накапливает наблюдения и забывает их по TTL.
//
// Singleton на процесс: ключ провайдера (как в LlmProviderRegistry / ClaudeSubscriptionPool)
// → отметка времени, до которой он считается недоступным. Семантика fail-open: callers сами
// решают, что делать, когда ВСЕ кандидаты в кулдауне (обычно — пробовать несмотря, ведь
// кулдаун — наблюдение, а не запрет).
public sealed class ProviderHealthRegistry
{
    // Сколько минут провайдер считается недоступным после ошибки доставки. Подобрано под
    // типичный цикл «эндпоинт упал → поднялся»: меньше — гоняем мёртвые попытки, больше —
    // пропускаем уже живого. Не настраивается извне: это наблюдение, а не политика.
    public const int CooldownMinutes = 5;

    // Кулдаун исчерпанной квоты стороннего провайдера. Отдельный TTL, потому что событие другого
    // масштаба: «эндпоинт прилёг» лечится минутами, а исчерпанный лимит биллингового цикла живёт
    // часами-сутками — с пятиминутным TTL каждый двенадцатый ход снова стартовал бы на мёртвом
    // провайдере, тратя попытку. Час — компромисс: тариф могут пополнить в любой момент, поэтому
    // кулдаун не бесконечный, а fail-open «все кандидаты в кулдауне → берём остывшего» остаётся.
    public const int QuotaCooldownMinutes = 60;

    private readonly ConcurrentDictionary<string, DateTime> _unavailableUntil = new();

    public void MarkUnavailable(string? key) => MarkUntil(key, CooldownMinutes);

    // Провайдер исчерпал квоту (UsageLimit) — тот же кулдаун, но с длинным TTL. Только для
    // СТОРОННИХ провайдеров: здоровье подписок пула Claude ведёт ClaudeSubscriptionPool
    // (ADR-007 §4.3), дублировать его здесь нельзя.
    public void MarkQuotaExhausted(string? key) => MarkUntil(key, QuotaCooldownMinutes);

    // Отметку только продлеваем: наблюдения приходят лишь на ошибках, поэтому короткий кулдаун
    // «эндпоинт прилёг» не должен обрезать уже стоящий длинный кулдаун исчерпанной квоты.
    private void MarkUntil(string? key, int minutes)
    {
        if (string.IsNullOrWhiteSpace(key)) return;
        var until = DateTime.UtcNow.AddMinutes(minutes);
        _unavailableUntil.AddOrUpdate(key, until, (_, prev) => prev > until ? prev : until);
    }

    // true — провайдер в кулдауне (недоступен до T). Источник времени — UTC, вызывающий
    // его не задаёт: единая точка отсчёта, иначе тесты и прод разошлись бы по часам.
    public bool IsUnavailable(string? key) =>
        !string.IsNullOrWhiteSpace(key)
        && _unavailableUntil.TryGetValue(key, out var until)
        && until > DateTime.UtcNow;
}
