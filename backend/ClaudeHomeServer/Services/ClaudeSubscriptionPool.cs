using System.Collections.Concurrent;
using System.Globalization;
using ClaudeHomeServer.Models;

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
    private const double DefaultSoftThreshold = 0.8;

    // Без известного времени сброса помечаем на полчаса: пятичасовое окно всё равно
    // не сбросится быстрее, а редкие пробные чаты сами продлят пометку при новом rejected.
    private static readonly TimeSpan DefaultExhaustion = TimeSpan.FromMinutes(30);

    private readonly IReadOnlyList<ClaudeSubscriptionConfig> _subscriptions;
    private readonly UsageService? _usage;
    // Аккаунт с утилизацией 5h-окна >= порога выводится из ротации (если есть кто ниже).
    private readonly double _softThreshold;
    // exhaustedKey → resetsAt (UTC, null = пока не сбросится вручную / DefaultExhaustion)
    private readonly ConcurrentDictionary<string, DateTime?> _exhausted = new();

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

        if (usage is not null)
            RestoreFromSnapshots(usage);
    }

    // Пометки исчерпания живут in-memory и теряются при рестарте сервера — восстанавливаем
    // из последних снапшотов usage: окно rejected (или выбрано без overage) со сбросом в будущем.
    private void RestoreFromSnapshots(UsageService usage)
    {
        foreach (var (key, snapshots) in usage.GetAllBySubscription())
        {
            foreach (var last in snapshots.GroupBy(s => s.LimitType).Select(g => g.Last()))
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
    /// — минимум из способных по модели: лучше упереться в лимит на правильном аккаунте, чем
    /// гарантированно упасть на неправильном.
    public string Pick(string? model = null)
    {
        if (_subscriptions.Count == 0)
            return PrimaryKey;

        var candidates = AllKeys().Where(k => !IsExhausted(k) && SupportsModel(k, model)).ToList();
        if (candidates.Count > 0)
        {
            // Приоритет свободным (ниже порога) — крупный, но перегруженный тариф уступает
            // свободному мелкому; если свободных нет — выбираем среди всех кандидатов.
            var healthy = candidates.Where(k => EffectiveUtilization(k) < _softThreshold).ToList();
            return PickTopTier(healthy.Count > 0 ? healthy : candidates);
        }

        var capable = AllKeys().Where(k => SupportsModel(k, model)).ToList();
        return PickTopTier(capable.Count > 0 ? capable : AllKeys());
    }

    // Из набора ключей — высший тариф, при равенстве тарифа — наименее загруженный.
    private string PickTopTier(IReadOnlyList<string> keys)
    {
        if (keys.Count == 0) return PrimaryKey;
        var topRank = keys.Max(TierRank);
        var top = keys.Where(k => TierRank(k) == topRank).ToList();
        return LeastLoaded(top);
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

    /// <summary>Аккаунт может обслужить модель: для Opus-тира — только SupportsOpus-планы.</summary>
    /// Ключи вне пула (сторонние провайдеры deepseek/glm) не наша забота — true.
    public bool SupportsModel(string key, string? model)
    {
        if (!RequiresOpus(model)) return true;
        var sub = _subscriptions.FirstOrDefault(s => s.Key == key);
        return sub is null || sub.SupportsOpus;
    }

    /// <summary>Аккаунт «в ротации» для новых чатов.</summary>
    /// Выведен, если исчерпан (rejected/100% — жёсткое состояние, `utilization` при rejected
    /// CLI может не прислать) ИЛИ утилизация 5h-окна выше мягкого порога. Зеркалит логику Pick,
    /// который исключает исчерпанных до сравнения утилизаций.
    public bool IsInRotation(string key) => !IsExhausted(key) && EffectiveUtilization(key) < _softThreshold;

    /// <summary>Порог утилизации 5h-окна, выше которого аккаунт считается выведенным из ротации.</summary>
    public double SoftThreshold => _softThreshold;

    /// <summary>Утилизация 5-часового окна аккаунта (0..1) по последнему снимку usage.</summary>
    /// Окно с истёкшим ResetsAt считаем сброшенным (0%), нет данных — тоже 0% (свежий аккаунт).
    public double EffectiveUtilization(string key)
    {
        if (_usage is null) return 0;
        if (!_usage.GetAllBySubscription().TryGetValue(key, out var snapshots)) return 0;

        var last = snapshots.LastOrDefault(s => s.LimitType == RoutingLimitType);
        if (last is null) return 0;

        if (DateTime.TryParse(last.ResetsAt, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var resetsAt)
            && resetsAt <= DateTime.UtcNow)
            return 0;

        return last.Utilization ?? 0;
    }

    // Ключ с минимальной утилизацией; при равенстве — случайный среди минимальных.
    private string LeastLoaded(IReadOnlyList<string> keys)
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
        return winners.Count == 0 ? PrimaryKey : winners[Random.Shared.Next(winners.Count)];
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
    }
}
