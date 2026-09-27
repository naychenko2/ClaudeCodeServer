using System.Collections.Concurrent;
using System.Globalization;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Llm;

namespace ClaudeHomeServer.Services.Llm;

// Пул подписок Claude. Позволяет использовать несколько аккаунтов на одном сервере:
// новые чаты направляются на наименее загруженную подписку (по утилизации 5-часового
// окна), аккаунты выше мягкого порога хотя бы по одному из окон (5-часовое или
// недельное) выводятся из ротации заранее; при полном исчерпании лимита одной — сервер
// автоматически переключается на другую.
public class ClaudeSubscriptionPool
{
    public const string Section = "ClaudeSubscriptions";
    // Ключ, под которым идёт ЛОКАЛЬНЫЙ Claude (вход без ключа, по ~/.claude/.credentials.json)
    // — режим, когда в конфиге не настроено ни одной подписки (пул пуст). Если запись с этим
    // ключом задана С токеном (OAuthToken/ApiKey), она становится обычной подпиской пула
    // наравне с остальными — «локальным» тогда не считается.
    public const string PrimaryKey = "claude";

    // Окно лимита, по которому роутим новые чаты (короткое, самое частое ограничение).
    private const string RoutingLimitType = "five_hour";
    // Недельное окно: копится медленно, но упёршись в него, аккаунт банится на сутки-двое.
    private const string WeeklyLimitType = "seven_day";
    private const double DefaultSoftThreshold = 0.8;
    // Порог недельного окна выше пятичасового: 0.8 выводил бы аккаунт из ротации почти
    // за трое суток до сброса — недельная утилизация растёт медленно.
    private const double DefaultWeeklyThreshold = 0.95;

    // TTL пометки «пара (подписка × модель) недоступна» по причине. no_access — свойство
    // тарифа, само не меняется (сутки); out_of_credits — кошелёк кредитов модели, могут
    // пополнить в любой момент (час). Дефолты перекрываются конфигом, значения в часах.
    private const double DefaultNoAccessTtlHours = 24;
    private const double DefaultOutOfCreditsTtlHours = 1;

    // Окно подавления MarkExhausted после отказа ПО МОДЕЛИ (см. HadRecentModelRejection).
    // Минуты — цена ошибки в обе стороны: ложный бан живой подписки стоит до сброса
    // пятичасового окна, а пропущенное настоящее исчерпание чинится сразу двумя путями,
    // не зависящими от этого подавления: адаптер фолбэка метит подписку сам по классу
    // отказа СВОЕГО хода (ResolveNextTarget → MarkExhausted на RateLimit/UsageLimit), а
    // следующее rate_limit_event уже за пределами окна отработает штатно. На идл-пинг
    // warmup здесь ссылаться нельзя: он пингует только аккаунты, простаивающие дольше
    // IdlePingMinutes, — активную подписку он как раз не трогает.
    private static readonly TimeSpan ModelRejectionSuppression = TimeSpan.FromMinutes(1);

    // Без известного времени сброса помечаем на полчаса: пятичасовое окно всё равно
    // не сбросится быстрее, а редкие пробные чаты сами продлят пометку при новом rejected.
    private static readonly TimeSpan DefaultExhaustion = TimeSpan.FromMinutes(30);

    // Разброс сроков сброса, внутри которого исчерпанные аккаунты считаются равными
    // (fallback-выбор «кто воскреснет раньше»).
    private static readonly TimeSpan RecoveryTieWindow = TimeSpan.FromMinutes(1);

    // Окна, исчерпание которых реально выводит аккаунт из ротации: базовые лимиты подписки.
    // Всё остальное, что присылает CLI, — информационное, и его rejected подписку НЕ банит.
    // Инцидент 2026-08-02: по живому аккаунту пришло одиночное rate_limit_event
    // limit_type="seven_day_overage_included" status="rejected" (utilization=null) со сбросом
    // через 5 суток — ходы на том же аккаунте продолжали проходить через полминуты, а пул
    // держал его исчерпанным до resetsAt и ре-отравлялся из снапшотов на каждом рестарте.
    // Per-model окна (seven_day_opus/sonnet/…) сюда тоже не входят: пометка исчерпания
    // глобальная по аккаунту, а такое окно закрывает лишь одну модель — вывод всего
    // аккаунта из ротации был бы ложным для остальных.
    private static readonly HashSet<string> ExhaustionWindows =
        new(StringComparer.OrdinalIgnoreCase) { "five_hour", "seven_day" };

    /// <summary>Окно, по которому rejected/100% означает исчерпание подписки?</summary>
    /// Единая точка для всех, кто маркирует пул (ход в SessionManager, идл-пинг warmup,
    /// восстановление из снапшотов). Неизвестное окно — только снимок в UsageService для
    /// экрана, состояние пула оно не трогает НИ в какую сторону (ни бан, ни снятие бана).
    public static bool IsExhaustionWindow(string? limitType) =>
        limitType is not null && ExhaustionWindows.Contains(limitType);

    private readonly IReadOnlyList<ClaudeSubscriptionConfig> _subscriptions;
    private readonly UsageService? _usage;
    // Аккаунт с утилизацией 5h-окна >= порога выводится из ротации (если есть кто ниже).
    private readonly double _softThreshold;
    // Аккаунт с утилизацией недельного окна >= порога тоже выводится из ротации.
    private readonly double _weeklyThreshold;
    // exhaustedKey → resetsAt (UTC, null = пока не сбросится вручную / DefaultExhaustion)
    private readonly ConcurrentDictionary<string, DateTime?> _exhausted = new();

    // Пометка негодного auth (протухший OAuth/ключ, P29). В отличие от исчерпания, НЕ имеет
    // resetsAt: токен сам не воскреснет до ручного перевхода, поэтому по таймеру подписку в
    // ротацию не возвращаем. Живёт in-memory (снимается рестартом сервера, успешным ходом на
    // этой подписке через Reset либо ручным сбросом), в бэкап не едет. IsAuthDead исключает
    // подписку из кандидатов Pick — пока в пуле есть живая, ход уходит на неё, а не бьётся о
    // мёртвую (инцидент 2026-08-13: половина ходов падала на протухшей подписке при живой второй).
    private readonly ConcurrentDictionary<string, byte> _authDead = new();

    // Пометка «пара (подписка × модель) недоступна»: (ключ подписки, модель) → (дедлайн, причина).
    // В отличие от _exhausted (вся подписка на лимите), режет ТОЛЬКО пару — Sonnet/Opus на той же
    // подписке остаются кандидатами. In-memory, как _exhausted/_authDead (рестарт сервера снимает
    // все пометки — восстановление не требуется: пара, которая всё ещё недоступна, перемаркируется
    // первым же отказом). Ключ — КОРТЕЖ, а не склейка строк: ключи подписок приходят из конфига
    // владельца, и любой разделитель («|» первой редакции) в них когда-нибудь встретится —
    // тогда «a|b» × «opus» и «a» × «b|opus» схлопнулись бы в одну пометку.
    private readonly ConcurrentDictionary<(string Key, string Model), (DateTime Until, FallbackErrorClass Reason)> _modelUnavailable = new();

    // Момент последнего отказа ПО МОДЕЛИ где угодно в пуле (UTC-тики, Interlocked). Нужен одному:
    // подавить MarkExhausted от ПОЗДНЕГО rate_limit_event той же попытки (см. HadRecentModelRejection).
    // Намеренно БЕЗ привязки к подписке: событие приезжает после того, как тихая ротация уже
    // переставила Info.Provider на здоровый аккаунт, и проверка по нему промахивалась бы мимо
    // пометки — ровно тот баг, от которого рядом защищает FallbackTurnActive (блокер ревью).
    private long _lastModelRejectionTicks;

    private readonly double _noAccessTtlHours;
    private readonly double _outOfCreditsTtlHours;

    public ClaudeSubscriptionPool(IConfiguration config, UsageService? usage = null)
    {
        var list = new List<ClaudeSubscriptionConfig>();
        foreach (var child in config.GetSection(Section).GetChildren())
        {
            // Каждая запись с ключом-именем подписки и способом аутентификации (OAuthToken/ApiKey)
            // — равноправный участник пула, включая "claude". Не-подписочные ключи секции
            // (SoftThreshold, WarmupOnStartup, комментарии) не биндятся в объект и отсекаются по
            // Enabled=false. Пул пуст (ни одной подписки) => используется локальный Claude (Pick).
            var cfg = child.Get<ClaudeSubscriptionConfig>();
            if (cfg is null) continue;
            cfg.Key = child.Key;
            if (cfg.Enabled)
                list.Add(cfg);
        }
        _subscriptions = list.AsReadOnly();
        _usage = usage;
        _softThreshold = config.GetValue($"{Section}:SoftThreshold", DefaultSoftThreshold);
        _weeklyThreshold = config.GetValue($"{Section}:WeeklyThreshold", DefaultWeeklyThreshold);
        _noAccessTtlHours = config.GetValue($"{Section}:NoAccessTtlHours", DefaultNoAccessTtlHours);
        _outOfCreditsTtlHours = config.GetValue($"{Section}:OutOfCreditsTtlHours", DefaultOutOfCreditsTtlHours);

        if (usage is not null)
            RestoreFromSnapshots(usage);
    }

    // Пометки исчерпания живут in-memory и теряются при рестарте сервера — восстанавливаем
    // из последних снапшотов usage: окно rejected (или выбрано без overage) со сбросом в будущем.
    // Только по окнам из белого списка (IsExhaustionWindow): иначе одно транзитное rejected
    // неизвестного окна воскресало бы при каждом рестарте до своего далёкого resetsAt.
    // Снимки source="oauth" (SubscriptionOAuthUsageService) исключены: тот пишет status="allowed"
    // ВСЕГДА, независимо от реальной утилизации (эндпоинт отдаёт только проценты, не вердикт
    // accept/reject) — как хронологически последний в группе LimitType он маскировал бы более
    // раннее реальное исчерпание по rejected-ходу/пингу (source="turn"/"probe"), и после рестарта
    // Pick() снова выбирал бы мёртвый по недельному окну аккаунт с виду свежим 5h-окном.
    private void RestoreFromSnapshots(UsageService usage)
    {
        foreach (var (key, snapshots) in usage.GetAllBySubscription())
        {
            foreach (var last in snapshots.Where(s => s.Source != "oauth" && IsExhaustionWindow(s.LimitType))
                .GroupBy(s => s.LimitType).Select(g => g.Last()))
            {
                if (last.Status != "rejected" && !(last.Utilization >= 1.0 && !last.IsUsingOverage))
                    continue;
                if (!DateTime.TryParse(last.ResetsAt, CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var resetsAt))
                    continue;
                if (resetsAt > DateTime.UtcNow)
                    MarkExhausted(key, resetsAt);
            }
        }
    }

    /// <summary>Все настроенные дополнительные подписки</summary>
    public IReadOnlyList<ClaudeSubscriptionConfig> All => _subscriptions;

    /// <summary>Есть ли хотя бы одна дополнительная подписка</summary>
    public bool HasExtra => _subscriptions.Count > 0;

    /// <summary>Выбрать ключ подписки для новой сессии: доступный аккаунт с высшим тарифом.</summary>
    /// Пул пуст (ни одной подписки в конфиге) => PrimaryKey — локальный Claude (вход без ключа).
    /// Иначе выбираем СРЕДИ настроенных подписок (локальный в ротации не участвует). Жёстко
    /// отсекаются исчерпанные (rejected/100% без overage) и аккаунты без доступа к запрошенной
    /// модели (Opus есть не на всех планах — CLI на таком аккаунте падает «issue with the
    /// selected model»). Среди оставшихся приоритет аккаунтам «в ротации» — утилизация 5h-окна
    /// ниже SoftThreshold И утилизация недельного ниже WeeklyThreshold (оба должны быть ниже
    /// своих порогов). Из «в ротации» — высший тариф (Max 20× > Max 5× > Max > Pro), при равенстве
    /// — минимальная 5-часовая утилизация. Свободных нет (все выше хотя бы одного из порогов) —
    /// спилл на них же; все исчерпаны — из способных по модели берётся тот, чьё окно сбросится
    /// РАНЬШЕ (лучше упереться в лимит на правильном аккаунте, чем гарантированно упасть на
    /// неправильном).
    public virtual string Pick(string? model = null) => PickCore(model, deterministic: false);

    /// <summary>Выбор аккаунта для локального хода (ADR-016 §2): только setup-token.</summary>
    /// Те же пороги, тарифы, пометки исчерпания и auth-dead, что у Pick, но набор заранее
    /// сужен до записей с OAuthToken и без ApiKey. null — подходящего нет: ни PrimaryKey
    /// (интерактивный логин), ни API-ключ, ни исчерпанный аккаунт «кто воскреснет раньше»
    /// сюда не попадают — ход без подходящего аккаунта отказывает с причиной. Ротация
    /// посреди хода — повторный вызов после пометки: он остаётся внутри того же набора.
    public virtual string? PickSetupToken(string? model = null)
    {
        var keys = _subscriptions.Where(s => s.IsSetupToken).Select(s => s.Key).ToList();
        // Сердцевина выбора на вырожденных данных умеет вернуть PrimaryKey — сюда он не пройдёт.
        return PickLive(keys, model, deterministic: false) is { } key && keys.Contains(key) ? key : null;
    }

    /// <summary>Токен setup-token аккаунта строго из конфига; null — ключ не setup-token.</summary>
    /// Профиль CLI (.credentials.json) не читается никогда: токен оттуда обновляет серверный
    /// CLI, и в чужих руках он переживёт отзыв доступа.
    public string? SetupTokenOf(string key) =>
        _subscriptions.FirstOrDefault(s => s.Key == key && s.IsSetupToken)?.OAuthToken;

    /// <summary>Куда фактически ушёл бы новый чат сейчас — цель роутинга для экрана usage.</summary>
    /// Та же логика, что Pick (модель не учитываем), но при равной утилизации берётся первый
    /// по порядку пула вместо случайного — бейдж «цель роутинга» не должен мигать между
    /// равными аккаунтами при каждом обновлении экрана.
    public string PickForDisplay() => PickCore(model: null, deterministic: true);

    private string PickCore(string? model, bool deterministic)
    {
        if (_subscriptions.Count == 0)
            return PrimaryKey;

        if (PickLive(AllKeys(), model, deterministic) is { } live)
            return live;

        // Живых кандидатов нет — сверяемся только со способностью ПЛАНА (SupportsModel):
        // временные пометки пар тут fail-open, иначе помеченная пара выбрасывала бы аккаунт
        // из последнего варианта, и Pick возвращал бы заведомо неспособный по тарифу.
        var capable = AllKeys().Where(k => SupportsModel(k, model)).ToList();
        return PickSoonestRecovery(capable.Count > 0 ? capable : AllKeys(), deterministic);
    }

    // Живой кандидат из набора: не исчерпан, не auth-dead, пара с моделью пригодна. null — живых нет.
    private string? PickLive(IReadOnlyList<string> keys, string? model, bool deterministic)
    {
        var candidates = keys.Where(k => !IsExhausted(k) && !IsAuthDead(k) && IsPairUsable(k, model)).ToList();
        if (candidates.Count == 0) return null;
        // Приоритет свободным (ниже порога) — крупный, но перегруженный тариф уступает
        // свободному мелкому; если свободных нет — выбираем среди всех кандидатов.
        var healthy = candidates.Where(k => !IsOverloaded(k)).ToList();
        return PickTopTier(healthy.Count > 0 ? healthy : candidates, deterministic);
    }

    // Из набора ключей — высший тариф, при равенстве тарифа — наименее загруженный.
    private string PickTopTier(IReadOnlyList<string> keys, bool deterministic)
    {
        if (keys.Count == 0) return PrimaryKey;
        return LeastLoaded(TopTier(keys), deterministic);
    }

    // Fallback «все исчерпаны»: среди высшего тарифа берём аккаунт, чья пометка снимется
    // раньше — он скорее воскреснет. Утилизация тут бесполезна (при rejected CLI её часто
    // не присылает, и у всех выходит 0 → случайный выбор гнал новые чаты на мёртвый аккаунт).
    // Близкие сроки (в пределах RecoveryTieWindow) считаем равными: разница в секунды —
    // это порядок прихода событий, а не реальное преимущество, решает загрузка.
    private string PickSoonestRecovery(IReadOnlyList<string> keys, bool deterministic)
    {
        if (keys.Count == 0) return PrimaryKey;
        var top = TopTier(keys);
        var soonest = top.Min(ExhaustedUntil);
        var earliest = top.Where(k => ExhaustedUntil(k) - soonest <= RecoveryTieWindow).ToList();
        return LeastLoaded(earliest, deterministic);
    }

    // Ключи высшего тарифа из набора.
    private List<string> TopTier(IReadOnlyList<string> keys)
    {
        var topRank = keys.Max(TierRank);
        return keys.Where(k => TierRank(k) == topRank).ToList();
    }

    // До какого момента ключ помечен исчерпанным; не помечен — DateTime.MinValue
    // («уже живой», такой аккаунт в fallback предпочтительнее любого помеченного). auth-dead
    // считается MaxValue: токен не воскреснет по таймеру, поэтому в fallback «кто воскреснет
    // раньше» такой аккаунт выбирается последним (уступая любому исчерпанному с resetsAt).
    private DateTime ExhaustedUntil(string key)
    {
        if (IsAuthDead(key)) return DateTime.MaxValue;
        return _exhausted.TryGetValue(key, out var until) && until is not null ? until.Value : DateTime.MinValue;
    }

    // Ранг тарифа подписки из её конфига (Tier). Ключ вне пула — 0 (не задан).
    private int TierRank(string key)
        => ClaudeSubscriptionTier.Rank(_subscriptions.FirstOrDefault(s => s.Key == key)?.Tier);

    /// <summary>Ярлык тарифа аккаунта для UI ("Max 20×", "Pro", …); null — тариф не задан.</summary>
    public string? TierLabel(string key)
        => ClaudeSubscriptionTier.Label(_subscriptions.FirstOrDefault(s => s.Key == key)?.Tier);

    // Модель требует Opus-тира (алиасы opus/opus[1m] и полные id claude-opus-*)
    public static bool RequiresOpus(string? model) =>
        !string.IsNullOrWhiteSpace(model) && model.Contains("opus", StringComparison.OrdinalIgnoreCase);

    /// <summary>Способность ПЛАНА аккаунта: для Opus-тира — только SupportsOpus-планы,</summary>
    /// для тир-алиаса с окном [1m] — только Supports1M-планы. Ключи вне пула (сторонние
    /// провайдеры deepseek/glm) не наша забота — true.
    ///
    /// Тут ТОЛЬКО ручные флаги конфига — свойство тарифа, которое человеку показывают как
    /// «подписка не поддерживает модель». Временные пометки пар (MarkModelUnavailable) сюда
    /// НЕ подмешиваются: с ними тот же текст врал бы — подписка модель поддерживает, просто
    /// пара помечена по одному отказу (находка ревью). Для выбора кандидата спрашивай
    /// <see cref="IsPairUsable"/> — он складывает способность плана и живую пометку.
    public bool SupportsModel(string key, string? model)
    {
        var sub = _subscriptions.FirstOrDefault(s => s.Key == key);
        // Ключ вне пула — не наша забота (сторонний провайдер): считаем, что тянет.
        if (sub is null) return true;
        if (RequiresOpus(model) && !sub.SupportsOpus) return false;
        if (LlmProviderRegistry.IsClaudeTierWindowAlias(model) && !sub.Supports1M) return false;
        return true;
    }

    /// <summary>Пара «подписка × модель» пригодна ПРЯМО СЕЙЧАС: план тянет и пометки нет.</summary>
    /// Вопрос выбора кандидата (Pick, ротация уровня 1, шаг цепочки) — в отличие от
    /// <see cref="SupportsModel"/>, который отвечает на вопрос о свойстве тарифа.
    public bool IsPairUsable(string key, string? model)
        => SupportsModel(key, model) && !IsModelUnavailable(key, model);

    /// <summary>Может ли пул обслужить тир-алиас с окном 1M прямо сейчас?</summary>
    /// Не тир-алиас (полный id, модель стороннего провайдера, обычный алиас) — вопрос не к пулу,
    /// true. Пул пуст (локальный Claude, default Supports1M=true) — тоже true. Иначе: есть живой
    /// (не исчерпанный, не auth-dead) аккаунт, чей ПЛАН тянет 1M.
    ///
    /// Временные пометки пар здесь НЕ участвуют намеренно (блокер ревью): один отказ по
    /// «opus[1m]» на единственной 1M-подписке иначе делал бы окно недоступным ВСЕМ чатам на
    /// сутки TTL — радиус несоразмерен одному отказу. Пометка режет свою пару в выборе
    /// кандидата (IsPairUsable), а не окно у всего инстанса.
    ///
    /// false — сигнал «эта пара не может», а не «ход не состоится»: разбирает его
    /// FallbackLlmSessionAdapter, уводя ход на шаг цепочки; честный отказ
    /// (TurnFailureText.Window1MUnavailable) остаётся только когда не смог ни один шаг.
    public bool CanServeWindow1M(string? model)
    {
        if (!LlmProviderRegistry.IsClaudeTierWindowAlias(model)) return true;
        return _subscriptions.Count == 0
            || AllKeys().Any(k => !IsExhausted(k) && !IsAuthDead(k) && SupportsModel(k, model));
    }

    /// <summary>Аккаунт «в ротации» для новых чатов.</summary>
    /// Выведен, если исчерпан (rejected/100% — жёсткое состояние, `utilization` при rejected
    /// CLI может не прислать), помечен негодным по auth ИЛИ перегружен по одному из окон
    /// (5ч выше мягкого порога либо недельное выше своего). Зеркалит логику Pick, который
    /// исключает таких до сравнения утилизаций.
    public bool IsInRotation(string key)
        => !IsExhausted(key) && !IsAuthDead(key) && !IsOverloaded(key);

    // Аккаунт перегружен хотя бы по одному окну — из ротации выводим (если есть кто свободнее).
    // Недельное окно учитываем наравне с пятичасовым: 99% недельного означает скорый бан,
    // хотя 5ч-окно при этом выглядит свежим.
    private bool IsOverloaded(string key)
        => EffectiveUtilization(key) >= _softThreshold || WeeklyUtilization(key) >= _weeklyThreshold;

    /// <summary>Порог утилизации 5h-окна, выше которого аккаунт считается выведенным из ротации.</summary>
    public double SoftThreshold => _softThreshold;

    /// <summary>Порог утилизации недельного окна, выше которого аккаунт выводится из ротации.</summary>
    public double WeeklyThreshold => _weeklyThreshold;

    /// <summary>Утилизация 5-часового окна аккаунта (0..1) по последнему снимку usage.</summary>
    /// Окно с истёкшим ResetsAt считаем сброшенным (0%), нет данных — тоже 0% (свежий аккаунт).
    public double EffectiveUtilization(string key) => UtilizationOf(key, RoutingLimitType);

    /// <summary>Утилизация недельного окна аккаунта (0..1) по последнему снимку usage.</summary>
    public double WeeklyUtilization(string key) => UtilizationOf(key, WeeklyLimitType);

    // Общая часть чтения снимка по конкретному окну: последний снимок, истёкший ResetsAt → 0,
    // отсутствие данных/утилизации → 0.
    private double UtilizationOf(string key, string limitType)
    {
        if (_usage is null) return 0;
        if (!_usage.GetAllBySubscription().TryGetValue(key, out var snapshots)) return 0;

        var last = snapshots.LastOrDefault(s => s.LimitType == limitType);
        if (last is null) return 0;

        if (DateTime.TryParse(last.ResetsAt, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var resetsAt)
            && resetsAt <= DateTime.UtcNow)
            return 0;

        return last.Utilization ?? 0;
    }

    // Ключ с минимальной утилизацией; при равенстве — случайный среди минимальных
    // (deterministic=true — первый по порядку, для стабильного отображения).
    private string LeastLoaded(IReadOnlyList<string> keys, bool deterministic = false)
    {
        var best = double.MaxValue;
        var winners = new List<string>();
        foreach (var k in keys)
        {
            var u = EffectiveUtilization(k);
            if (u < best - 1e-9)
            {
                best = u;
                winners.Clear();
                winners.Add(k);
            }
            else if (Math.Abs(u - best) <= 1e-9)
            {
                winners.Add(k);
            }
        }
        if (winners.Count == 0) return PrimaryKey;
        return deterministic ? winners[0] : winners[Random.Shared.Next(winners.Count)];
    }

    // Ключи всех настроенных подписок пула (пустой список = пул не настроен, локальный режим).
    private List<string> AllKeys() => _subscriptions.Select(s => s.Key).ToList();

    /// <summary>Пометить подписку как исчерпанную.</summary>
    /// resetsAt — время сброса лимита (из rate_limit_event); null — DefaultExhaustion.
    public void MarkExhausted(string key, DateTime? resetsAt = null)
    {
        if (resetsAt.HasValue && resetsAt.Value.Kind != DateTimeKind.Utc)
            resetsAt = resetsAt.Value.ToUniversalTime();
        _exhausted[key] = resetsAt ?? DateTime.UtcNow.Add(DefaultExhaustion);
    }

    /// <summary>Подписка сейчас на лимите?</summary>
    public bool IsExhausted(string key)
    {
        if (!_exhausted.TryGetValue(key, out var until) || until is null)
            return false;
        if (DateTime.UtcNow >= until.Value)
        {
            _exhausted.TryRemove(key, out _);
            return false;
        }
        return true;
    }

    /// <summary>Сбросить exhaustion вручную (для тестов / админ-действий)</summary>
    public void Reset(string key)
    {
        _exhausted.TryRemove(key, out _);
        _authDead.TryRemove(key, out _);
    }

    /// <summary>Пометить подписку как негодную по auth (протухший OAuth/ключ, P29).</summary>
    /// В отличие от <see cref="MarkExhausted"/>, без срока: токен не воскреснет сам до ручного
    /// перевхода. Снимается <see cref="ClearAuthDead"/>/рестартом сервера, когда аккаунт доказал
    /// работоспособность (пришёл rate_limit_event — значит запрос дошёл до эндпоинта и аутентификация
    /// прошла, до лимитов он бы не дошёл). Полный <see cref="Reset"/> чистит и auth-dead, и exhausted.
    public void MarkAuthDead(string key)
    {
        if (!string.IsNullOrEmpty(key)) _authDead[key] = 1;
    }

    /// <summary>Подписка помечена негодной по auth (протухший токен/ключ)?</summary>
    public bool IsAuthDead(string key)
        => !string.IsNullOrEmpty(key) && _authDead.ContainsKey(key);

    /// <summary>Снять ТОЛЬКО пометку auth-dead, не трогая исчерпание.</summary>
    /// Вызывается там, где аккаунт доказал работоспособность (rate_limit_event от подписки —
    /// в обработчике хода SessionManager и в идл-пинге warmup). До P31 снять auth-dead можно было
    /// только через <see cref="Reset"/>, а он гейтится исчерпанием: auth-dead-подписка не исчерпана
    /// → Reset не зовётся → транзитный 401 выключал платную подписку до рестарта процесса, хотя
    /// токен уже починили (P31, блокер ревью P29).
    public void ClearAuthDead(string key)
    {
        if (!string.IsNullOrEmpty(key)) _authDead.TryRemove(key, out _);
    }

    /// <summary>Пометить пару (подписка, модель) недоступной до истечения TTL причины.</summary>
    /// Не трогает исчерпание подписки: пометка режет ТОЛЬКО пару (SupportsModel), Sonnet/Opus на
    /// той же подписке остаются в ротации. Пустая модель — пары нет, пометка не ставится, но
    /// подавление MarkExhausted всё равно засекается: подписка тут известна всегда, и поздний
    /// rate_limit_event умеет соврать про неё и без имени модели.
    public void MarkModelUnavailable(string key, string? model, FallbackErrorClass reason)
    {
        if (string.IsNullOrEmpty(key)) return;
        Interlocked.Exchange(ref _lastModelRejectionTicks, DateTime.UtcNow.Ticks);
        if (string.IsNullOrWhiteSpace(model)) return;
        _modelUnavailable[PairKey(key, model)] = (DateTime.UtcNow.Add(ModelTtl(reason)), reason);
    }

    /// <summary>Был ли отказ ПО МОДЕЛИ только что (окно ModelRejectionSuppression)?</summary>
    /// Спрашивается ровно в одном месте — перед MarkExhausted в обработчике rate_limit_event хода
    /// (SessionManager). Смысл: отказ «нет доступа к модели»/«кончились кредиты модели» приезжает
    /// от CLI ещё и телеметрией rate_limit_event status=rejected, и она приходит про ОКНО, а про
    /// модель в ней нет ничего. Пришло такое событие ПОСЛЕ финала хода (FallbackTurnActive уже
    /// false) — MarkExhausted пометил бы живую подписку исчерпанной, и её Sonnet/Opus выпали бы
    /// из ротации до сброса окна (инцидент 2026-09-09, чат «Анализ документов ВФЛА»).
    ///
    /// Вопрос БЕЗ подписки — это не небрежность, а лечение гонки (блокер ревью). Ключ спрашивать
    /// не у чего: к моменту позднего события тихая ротация уже переставила Info.Provider на
    /// СОСЕДНИЙ здоровый аккаунт, и проверка по нему промахивалась бы мимо пометки, оставляя
    /// ровно тот ложный бан, ради которого подавление и заводилось. Соседний гард
    /// FallbackTurnActive устроен так же — он тоже не смотрит, чей это ключ.
    ///
    /// Почему признак именно такой, а не поля телеметрии: различить эти два случая в
    /// rate_limit_event НЕЛЬЗЯ. Волна 1 пробовала (rejected + пустая utilization +
    /// overageDisabledReason) — и признак оказался ложным: overageDisabledReason приходит и на
    /// НАСТОЯЩЕМ исчерпании пятичасового окна, когда у аккаунта просто выключен перерасход
    /// (в серверном логе 2026-09-09 — 248 таких строк за сутки от идл-пинга, а он ходит haiku,
    /// у которой кошелька кредитов нет вовсе). Достоверен только текст ошибки хода, который
    /// разбирает TurnErrorClassifier → отсюда пометка ставится там, где класс отказа уже известен.
    ///
    /// Цена ложного подавления ограничена минутой (см. ModelRejectionSuppression) и страхуется
    /// не идл-пингом (тот ходит только к простаивающим аккаунтам), а самим адаптером фолбэка:
    /// исчерпание СВОЕГО хода он метит по классу отказа в ResolveNextTarget.
    public bool HadRecentModelRejection() =>
        DateTime.UtcNow - new DateTime(Interlocked.Read(ref _lastModelRejectionTicks), DateTimeKind.Utc)
            < ModelRejectionSuppression;

    /// <summary>Снять пометку пары вручную (кнопка «проверить заново», волна 2) или успешным</summary>
    /// ходом этой модели на этой подписке (как Reset снимает исчерпание).
    public void ClearModelUnavailable(string key, string? model)
    {
        if (string.IsNullOrEmpty(key) || string.IsNullOrWhiteSpace(model)) return;
        _modelUnavailable.TryRemove(PairKey(key, model), out _);
    }

    /// <summary>Пара сейчас помечена недоступной (TTL ещё не истёк)?</summary>
    /// Истёкшую пометку попутно вычищаем, но ТОЛЬКО ту, которую прочитали: между чтением и
    /// удалением параллельный ход мог поставить свежую (`MarkModelUnavailable`), и слепой
    /// TryRemove по ключу снёс бы её вместе с истёкшей.
    public bool IsModelUnavailable(string key, string? model)
    {
        if (string.IsNullOrEmpty(key) || string.IsNullOrWhiteSpace(model)) return false;
        var pair = PairKey(key, model);
        if (!_modelUnavailable.TryGetValue(pair, out var mark)) return false;
        if (DateTime.UtcNow >= mark.Until)
        {
            RemoveStaleMark(pair, mark);
            return false;
        }
        return true;
    }

    /// <summary>Живые пометки недоступности моделей у одной подписки (для выдачи /api/usage).</summary>
    /// Истёкшие по TTL не отдаются и попутно вычищаются — иначе карточка показывала бы «проверим
    /// снова через час» после того, как срок уже прошёл. Модель — в нормализованном виде ключа
    /// пары (нижний регистр): именно её ждёт обратно эндпоинт сброса.
    public IReadOnlyList<ModelUnavailableMark> ModelUnavailableMarks(string key)
    {
        if (string.IsNullOrEmpty(key)) return [];
        var now = DateTime.UtcNow;
        var result = new List<ModelUnavailableMark>();
        foreach (var (pair, mark) in _modelUnavailable)
        {
            if (!string.Equals(pair.Key, key, StringComparison.Ordinal)) continue;
            if (now >= mark.Until) { RemoveStaleMark(pair, mark); continue; }
            // Причина без wire-имени сюда не приходит (пометку ставят только два класса отказа
            // по модели), но врать конкретикой на всякий случай нельзя: нейтральное имя фронт
            // покажет общей формулировкой, а не «нет доступа на этом плане».
            result.Add(new ModelUnavailableMark(pair.Model,
                TurnErrorClassifier.WireName(mark.Reason) ?? "model_unavailable", mark.Until));
        }
        return result.OrderBy(m => m.Model, StringComparer.Ordinal).ToList();
    }

    // Удалить ИМЕННО истёкшую пометку: TryRemove(KeyValuePair) сравнивает и значение, поэтому
    // свежая пометка, поставленная параллельным ходом между чтением и удалением, уцелеет.
    private void RemoveStaleMark((string Key, string Model) pair,
        (DateTime Until, FallbackErrorClass Reason) stale) =>
        _modelUnavailable.TryRemove(
            new KeyValuePair<(string Key, string Model), (DateTime Until, FallbackErrorClass Reason)>(pair, stale));

    // Модель нормализуется к нижнему регистру: она приходит и от CLI, и из конфига,
    // и из тела запроса на сброс пометки.
    private static (string Key, string Model) PairKey(string key, string model) =>
        (key, model.ToLowerInvariant());

    private TimeSpan ModelTtl(FallbackErrorClass reason) => reason switch
    {
        FallbackErrorClass.ModelOutOfCredits => TimeSpan.FromHours(_outOfCreditsTtlHours),
        _ => TimeSpan.FromHours(_noAccessTtlHours),
    };
}
