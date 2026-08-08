using System.Collections.Concurrent;

namespace ClaudeHomeServer.Services.Llm;

// Наблюдаемая ёмкость окна модели (ADR «Порядок резолва модели…» §2.1). Заявленное в каталоге
// ContextWindow расходится с фактическим лимитом тарифа/эндпоинта: напр. kimi-k3 заявляет 1M,
// но на Coding Plan ход падает с «Prompt is too long» на меньшем контексте. Конфиг врёт — поэтому
// опираемся на наблюдение: при ContextOverflow запоминаем «модель не приняла контекст размера N»
// и при последующих подменах не отправляем на неё ход с контекстом ≥ N.
//
// Singleton на процесс, пассивный in-memory наблюдатель — как ProviderHealthRegistry: не
// персистируется, в бэкап не едет, запросов не шлёт. Ключ — id модели (окно — свойство модели,
// не подписки/аккаунта). TTL порядка часа: тариф режет стабильно, но наблюдение не должно жить
// вечно (модель/тариф могли поменяться). Семантика fail-open: callers сами решают, что делать,
// когда данных нет — пропускают кандидата (лучше попробовать, чем молча сдаться).
public sealed class ContextCapacityRegistry
{
    // Сколько минут наблюдение переполнения считается актуальным. Больше кулдауна
    // недоступности (ProviderHealthRegistry): тариф меняется реже эндпоинта, и повторное
    // попадание в ту же модель в течение часа стоит дешевле, чем холостой ход на ней.
    public const int RetentionMinutes = 60;

    private readonly ConcurrentDictionary<string, Entry> _ceilings = new();

    private sealed record Entry(int OverflowTokens, DateTime ExpiresAt);

    public void RecordOverflow(string? model, int contextTokens)
    {
        if (string.IsNullOrWhiteSpace(model) || contextTokens <= 0) return;
        var now = DateTime.UtcNow;
        // Берём МИНИМАЛЬНОЕ переполнение: самая жёсткая верхняя граница ёмкости. Если модель
        // падала и на 200k, и на 150k — её окно точно < 150k, большее значение неинформативно.
        _ceilings.AddOrUpdate(
            model,
            _ => new Entry(contextTokens, now.AddMinutes(RetentionMinutes)),
            (_, existing) => existing.ExpiresAt <= now || contextTokens < existing.OverflowTokens
                ? new Entry(contextTokens, now.AddMinutes(RetentionMinutes))
                : existing);
    }

    // Наблюдённый потолок ёмкости модели: контекст строго меньшего размера модель МОГЛА бы
    // принять (окно < OverflowTokens). null — наблюдений нет (или они протухли) → fail-open.
    public int? ObservedCeiling(string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return null;
        if (!_ceilings.TryGetValue(model, out var e)) return null;
        if (e.ExpiresAt <= DateTime.UtcNow) { _ceilings.TryRemove(model, out _); return null; }
        return e.OverflowTokens;
    }
}
