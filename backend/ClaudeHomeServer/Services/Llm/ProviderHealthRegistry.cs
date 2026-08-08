using System.Collections.Concurrent;

namespace ClaudeHomeServer.Services.Llm;

// Кулдаун недоступности провайдера (волна 2 ADR-007): пассивный in-memory наблюдатель —
// запоминает, что провайдер вернул ошибку доставки класса Unreachable/ProviderError, и
// считает его «недоступным» TTL минут, чтобы не биться головой о мёртвый эндпоинт в
// следующих ходах и шагах цепочки. Не персистируется, в бэкап не едет, warmup-пингов
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

    private readonly ConcurrentDictionary<string, DateTime> _unavailableUntil = new();

    public void MarkUnavailable(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return;
        _unavailableUntil[key] = DateTime.UtcNow.AddMinutes(CooldownMinutes);
    }

    // true — провайдер в кулдауне (недоступен до T). Источник времени — UTC, вызывающий
    // его не задаёт: единая точка отсчёта, иначе тесты и прод разошлись бы по часам.
    public bool IsUnavailable(string? key) =>
        !string.IsNullOrWhiteSpace(key)
        && _unavailableUntil.TryGetValue(key, out var until)
        && until > DateTime.UtcNow;
}
