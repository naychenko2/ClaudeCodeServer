using ClaudeHomeServer.Protocol;

namespace ClaudeHomeServer.Services.Llm;

// Итог записи лимита для вызывающего: что делать дальше со своим ходом.
public enum LimitRecordOutcome
{
    // Снимок записан, состояние пула не требует реакции вызывающего.
    Recorded,
    // Подписка только что помечена исчерпанной — вызывающий уводит свой ход на другой аккаунт.
    Exhausted,
    // Событие про окно пришло от отказа ПО МОДЕЛИ либо ротацией владеет адаптер фолбэка:
    // пул не тронут, вызывающему реагировать не на что.
    Suppressed,
}

// Единая точка учёта лимитов подписок (ADR-016, план §2 п. 2). Зовут двое: обработчик
// rate_limit_event хода (SessionManager) и шлюз LLM по заголовкам
// anthropic-ratelimit-unified-*. Второго счётчика нет: состояние живёт в UsageService и
// ClaudeSubscriptionPool, рекордер только решает, как событие их меняет. Реакция на свой ход
// (переезд чата, карточка фолбэка, ротация токена хода) — у вызывающего, по возвращённому итогу.
public sealed class SubscriptionLimitRecorder(
    UsageService usage,
    ClaudeSubscriptionPool pool,
    SubscriptionActivityTracker? activity = null)
{
    // subscriptionKey = null — событие без известного аккаунта: только снимок для экрана.
    // rotationOwnedElsewhere — ротацией этого хода владеет адаптер фолбэка (см. M1 ниже).
    // context — метка для лога (id чата или хода шлюза).
    public LimitRecordOutcome Record(string? subscriptionKey, RateLimitMessage m, string source,
        Func<bool>? rotationOwnedElsewhere = null, string? context = null)
    {
        usage.Record(m.LimitType, m.Utilization, m.Status, m.IsUsingOverage, m.ResetsAt, m.OverageStatus,
            m.OverageResetsAt, subscriptionKey: subscriptionKey, source: source,
            overageDisabledReason: m.OverageDisabledReason);
        activity?.Touch(subscriptionKey);
        if (subscriptionKey is null) return LimitRecordOutcome.Recorded;

        // P31: событие лимита от подписки — доказательство аутентификации (до лимитов запрос
        // не дошёл бы). Снимаем auth-dead независимо от окна и исчерпания: иначе транзитный 401 +
        // перевход (claude setup-token) выключали подписку до рестарта процесса, хотя токен уже
        // починили (блокер ревью P29). По любому окну, не только exhaustion-окну: заголовки
        // лимитов приходят с каждым ответом авторизованного API.
        if (pool.IsAuthDead(subscriptionKey))
        {
            pool.ClearAuthDead(subscriptionKey);
            Console.WriteLine($"[SubscriptionLimits] Подписка «{subscriptionKey}» отвечает ({context}) — снята пометка auth-dead");
        }

        // Состояние пула правим только по известным окнам (IsExhaustionWindow): rejected
        // неизвестного окна — транзитная телеметрия CLI, она попадает в usage для экрана, но
        // ротацию не трогает.
        if (!ClaudeSubscriptionPool.IsExhaustionWindow(m.LimitType))
            return LimitRecordOutcome.Recorded;

        // "rejected" — ход отклонён; utilization >= 1.0 без overage — окно выбрано (с overage
        // ходы ещё проходят).
        if (m.Status == "rejected" || (m.Utilization >= 1.0 && !m.IsUsingOverage))
        {
            // Отказ по НЕДОСТУПНОЙ модели (кредиты модели / нет доступа) только что случился —
            // событие про ОКНО не метит подписку исчерпанной: Sonnet/Opus на ней работают, ложный
            // бан выводил бы её из ротации до сброса окна (инцидент 2026-09-09, чат «Анализ
            // документов ВФЛА»). Окно шире хода: rate_limit_event умеет прийти уже ПОСЛЕ его
            // финала. Пару (подписка × модель) помечает адаптер в ResolveNextTarget. Спрашиваем
            // БЕЗ ключа подписки намеренно: к этому моменту тихая ротация уже переставила
            // провайдера чата на соседний здоровый аккаунт, и вопрос по нему промахнулся бы мимо
            // пометки. Разбор — в HadRecentModelRejection.
            if (pool.HadRecentModelRejection())
                return LimitRecordOutcome.Suppressed;
            // M1: под фолбэк-оркестрацией ротацией владеет адаптер — помечать провайдер
            // исчерпанным тут нельзя: поздний rate_limit от УЖЕ прерванной попытки придёт после
            // ApplyTarget, когда провайдер уже сменён на здоровый, и пометка загубила бы только
            // что выбранную подписку. Провайдер этой попытки адаптер отметит сам.
            if (rotationOwnedElsewhere?.Invoke() == true)
                return LimitRecordOutcome.Suppressed;
            var resetsAt = m.ResetsAt is not null && DateTime.TryParse(m.ResetsAt, out var dt)
                ? (DateTime?)dt.ToUniversalTime() : null;
            pool.MarkExhausted(subscriptionKey, resetsAt);
            return LimitRecordOutcome.Exhausted;
        }

        // Самолечение: живой ход через аккаунт — сильнейший сигнал, что он работает; снимаем
        // пометку, как это делает идл-пинг warmup (RecordAndGuard). Без этого ложный бан висел
        // до resetsAt: активные аккаунты warmup не пингует. Компромисс осознанный: allowed по
        // five_hour снимет пометку и при реально выбранном seven_day — следующий ход тут же
        // перемаркирует, false-negative на минуты дешевле false-positive на сутки.
        if (pool.IsExhausted(subscriptionKey))
        {
            pool.Reset(subscriptionKey);
            Console.WriteLine($"[SubscriptionLimits] Подписка «{subscriptionKey}» отвечает ({context}) — снята пометка исчерпания");
        }
        return LimitRecordOutcome.Recorded;
    }
}
