using System.Collections.Concurrent;

namespace ClaudeHomeServer.Services.Llm;

// Кулдаун недоступности провайдера (волна 2 ADR-007): пассивный in-memory наблюдатель —
// запоминает, что провайдер вернул ошибку доставки класса Unreachable/ProviderError (либо
// исчерпал квоту — UsageLimit), и считает его «недоступным» TTL минут, чтобы не биться головой
// о мёртвый эндпоинт в следующих ходах и шагах цепочки. TTL зависит от причины: минуты на
// упавший эндпоинт, час на исчерпанную квоту (см. константы ниже). Не персистируется, в бэкап
// не едет, warmup-пингов сторонних провайдеров не шлёт — только накапливает наблюдения и
// забывает их по TTL либо снимает успешным ходом на этом провайдере (Clear).
//
// Singleton на процесс: ключ провайдера (как в LlmProviderRegistry / ClaudeSubscriptionPool)
// → (дедлайн, причина). Семантика fail-open: callers сами решают, что делать, когда ВСЕ
// кандидаты в кулдауне (обычно — пробовать несмотря, ведь кулдаун — наблюдение, а не запрет).
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

    // (дедлайн кулдауна, его причина). Причина хранится, чтобы стартовая подмена показала
    // пользователю каноническую формулировку («Исчерпан лимит» вместо «Эндпоинт недоступен»),
    // а не универсальную — исчерпанная квота и мёртвый эндпоинт лечатся по-разному.
    private readonly ConcurrentDictionary<string, (DateTime Until, FallbackErrorClass Reason)> _entries = new();

    // Ошибка доставки (Unreachable/ProviderError) — короткий кулдаун. reason хранится для
    // стартовой подмены: фронт отличит «Эндпоинт недоступен» от «Провайдер выключен».
    public void MarkUnavailable(string? key, FallbackErrorClass reason = FallbackErrorClass.Unreachable)
        => MarkUntil(key, CooldownMinutes, reason);

    // Провайдер исчерпал квоту (UsageLimit) — тот же кулдаун, но с длинным TTL. Только для
    // СТОРОННИХ провайдеров: здоровье подписок пула Claude ведёт ClaudeSubscriptionPool
    // (ADR-007 §4.3), дублировать его здесь нельзя.
    public void MarkQuotaExhausted(string? key)
        => MarkUntil(key, QuotaCooldownMinutes, FallbackErrorClass.UsageLimit);

    // Отметку только продлеваем: наблюдения приходят лишь на ошибках, поэтому короткий кулдаун
    // «эндпоинт прилёг» не должен обрезать уже стоящий длинный кулдаун исчерпанной квоты —
    // вместе с дедлайном сохраняем и его причину (более длинное наблюдение важнее).
    private void MarkUntil(string? key, int minutes, FallbackErrorClass reason)
    {
        if (string.IsNullOrWhiteSpace(key)) return;
        var until = DateTime.UtcNow.AddMinutes(minutes);
        _entries.AddOrUpdate(key, (until, reason), (_, prev) => prev.Until > until ? prev : (until, reason));
    }

    // Снять кулдаун успешной попыткой на этом провайдере (ADR-007 §4.3). TTL — это верхняя
    // граница «скорее всего ещё мёртв», а успешный ход — прямой сигнал «уже жив»: без очистки
    // пополенный тариф или поднятий эндпоинт простаивал бы в кулдауне, уводя стартовые подмены
    // на чужие модели. Вызывает ветка успеха по ключу ФАКТИЧЕСКОЙ попытки.
    public void Clear(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return;
        _entries.TryRemove(key, out _);
    }

    // true — провайдер в кулдауне (недоступен до T). Источник времени — UTC, вызывающий
    // его не задаёт: единая точка отсчёта, иначе тесты и прод разошлись бы по часам.
    public bool IsUnavailable(string? key) => UnavailableUntil(key) is not null;

    // Дедлайн кулдауна (UTC) либо null, если ключ не в кулдауне (отметки нет или уже истекла).
    // Публичный — чтобы тесты проверяли TTL напрямую, а не только сам факт IsUnavailable:
    // иначе регрессия «схлопнуть QuotaCooldownMinutes обратно в CooldownMinutes» осталась бы
    // зелёной (IsUnavailable true в обоих случаях, разница в минутах невидна).
    public DateTimeOffset? UnavailableUntil(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        if (!_entries.TryGetValue(key, out var e)) return null;
        if (e.Until <= DateTime.UtcNow) return null;
        return new DateTimeOffset(e.Until, TimeSpan.Zero);
    }

    // Причина кулдауна wire-именем класса — для Reason стартовой подмены. null, если ключ
    // не в кулдауне: вызывающий подставляет свой дефолт.
    public string? UnavailableReason(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        if (!_entries.TryGetValue(key, out var e)) return null;
        if (e.Until <= DateTime.UtcNow) return null;
        return TurnErrorClassifier.WireName(e.Reason);
    }
}
