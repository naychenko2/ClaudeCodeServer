using System.Collections.Concurrent;
using System.Globalization;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Llm;

namespace ClaudeHomeServer.Services;

// Пул подписок Claude. Позволяет использовать несколько аккаунтов на одном сервере:
// новые чаты направляются на наименее загруженную подписку (по утилизации 5-часового
// окна), аккаунты выше мягкого порога выводятся из ротации заранее; при полном
// исчерпании лимита одной — сервер автоматически переключается на другую.
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
    /// selected model»). Среди оставшихся приоритет аккаунтам «в ротации» (утилизация 5h-окна
    /// ниже мягкого порога) — из них высший тариф (Max 20× > Max 5× > Max > Pro), при равенстве
    /// — минимальная утилизация. Свободных нет (все выше порога) — спилл на них же; все исчерпаны
    /// — из способных по модели берётся тот, чьё окно сбросится РАНЬШЕ (лучше упереться в лимит
    /// на правильном аккаунте, чем гарантированно упасть на неправильном).
    public string Pick(string? model = null) => PickCore(model, deterministic: false);

    /// <summary>Куда фактически ушёл бы новый чат сейчас — цель роутинга для экрана usage.</summary>
    /// Та же логика, что Pick (модель не учитываем), но при равной утилизации берётся первый
    /// по порядку пула вместо случайного — бейдж «цель роутинга» не должен мигать между
    /// равными аккаунтами при каждом обновлении экрана.
    public string PickForDisplay() => PickCore(model: null, deterministic: true);

    private string PickCore(string? model, bool deterministic)
    {
        if (_subscriptions.Count == 0)
            return PrimaryKey;

        var candidates = AllKeys().Where(k => !IsExhausted(k) && !IsAuthDead(k) && SupportsModel(k, model)).ToList();
        if (candidates.Count > 0)
        {
            // Приоритет свободным (ниже порога) — крупный, но перегруженный тариф уступает
            // свободному мелкому; если свободных нет — выбираем среди всех кандидатов.
            var healthy = candidates.Where(k => !IsOverloaded(k)).ToList();
            return PickTopTier(healthy.Count > 0 ? healthy : candidates, deterministic);
        }

        var capable = AllKeys().Where(k => SupportsModel(k, model)).ToList();
        return PickSoonestRecovery(capable.Count > 0 ? capable : AllKeys(), deterministic);
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

    /// <summary>Аккаунт может обслужить модель: для Opus-тира — только SupportsOpus-планы,</summary>
    /// для тир-алиаса с окном [1m] — только Supports1M-планы. Ключи вне пула (сторонние
    /// провайдеры deepseek/glm) не наша забота — true.
    public bool SupportsModel(string key, string? model)
    {
        var sub = _subscriptions.FirstOrDefault(s => s.Key == key);
        // Ключ вне пула — не наша забота (сторонний провайдер): считаем, что тянет.
        if (sub is null) return true;
        if (RequiresOpus(model) && !sub.SupportsOpus) return false;
        if (LlmProviderRegistry.IsClaudeTierWindowAlias(model) && !sub.Supports1M) return false;
        return true;
    }

    /// <summary>Суффикс [1m] тир-алиаса остаётся, если в пуле есть живой кандидат с поддержкой</summary>
    /// 1M-окна; иначе срезается в базовый алиас (деградация в 200K вместо падения хода на
    /// аккаунте без доступа). Не-тир-алиасы (полные id, сторонние провайдеры, обычные модели)
    /// не трогает — их суффикс разбирает сам CLI. Пул пуст (локальный Claude) — модель как есть
    /// (default Supports1M=true). См. коммит 639136c4: срез был лекарством от рулетки «попали
    /// на учётку без 1M-доступа → ход упал»; теперь подписка выбирается по способности (Pick),
    /// а срез остаётся лишь страховкой, когда способных не осталось.
    public string? ResolveWindowAlias(string? model)
    {
        if (!LlmProviderRegistry.IsClaudeTierWindowAlias(model)) return model;
        return HasLive1MCandidate(model)
            ? model
            : LlmProviderRegistry.StripClaudeWindowAlias(model);
    }

    // Есть ли сейчас в пуле живой (не исчерпанный, не auth-dead) аккаунт, способный обслужить
    // тир-алиас с 1M-окном. Пул пуст → локальный Claude (default Supports1M=true) → true.
    private bool HasLive1MCandidate(string? model) =>
        _subscriptions.Count == 0
        || AllKeys().Any(k => !IsExhausted(k) && !IsAuthDead(k) && SupportsModel(k, model));

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
}
