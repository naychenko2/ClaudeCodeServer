using System.Collections.Concurrent;

namespace ClaudeHomeServer.Services.Llm;

// Наблюдаемая ёмкость окна модели (ADR «Порядок резолва модели…» §4.4). Заявленное в каталоге
// ContextWindow расходится с фактическим лимитом тарифа/эндпоинта: напр. kimi-k3 заявляет 1M,
// но на Coding Plan ход падает с «Prompt is too long» на меньшем контексте. Конфиг врёт — поэтому
// опираемся на наблюдение: при ContextOverflow запоминаем «модель не приняла контекст размера N»
// и при последующих подменах не отправляем на неё ход с контекстом ≥ N.
//
// Singleton на процесс, пассивный in-memory наблюдатель — как ProviderHealthRegistry: не
// персистируется, в бэкап не едет, запросов не шлёт. Ключ — id модели БЕЗ провайдера: окно —
// свойство модели, а в текущей модели конфига одна модель всегда приходит от одного провайдера
// (ссылку модель→провайдер держит LlmProviderRegistry). TTL порядка часа: тариф режет стабильно,
// но наблюдение не должно жить вечно (модель/тариф могли поменяться). Семантика fail-open:
// callers сами решают, что делать, когда данных нет — пропускают кандидата (лучше попробовать,
// чем молча сдаться).
//
// Храним РАЗМЕР ОТКАЗА (OverflowTokens), а не ёмкость: модель не приняла контекст N — значит
// окно < N. Потребитель (FallbackLlmSessionAdapter.WouldFit) сравнивает СТРОГО: контекст должен
// быть меньше наблюдённого, при равенстве модель упадёт снова (Major 1 ревью).
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

    // Наблюдённый РАЗМЕР ОТКАЗА: контекст строго меньшего размера модель МОГЛА бы принять
    // (окно < OverflowTokens). null — наблюдений нет (или они протухли) → fail-open.
    public int? ObservedCeiling(string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return null;
        if (!_ceilings.TryGetValue(model, out var e)) return null;
        if (e.ExpiresAt > DateTime.UtcNow) return e.OverflowTokens;
        // Протухло — убираем. TryRemove(KeyValuePair), а не TryRemove(key): между TryGetValue
        // и удалением параллельный RecordOverflow мог перезаписать запись свежей (TTL) — снести
        // её по ключу значило бы потерять актуальное наблюдение. По паре удаляем только своё.
        _ceilings.TryRemove(new KeyValuePair<string, Entry>(model, e));
        return null;
    }
}
