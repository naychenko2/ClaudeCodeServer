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
            // Запись с ключом основной подписки задаёт только её DisplayName (читает
            // UsageController) — участником ротации не становится
            if (child.Key == PrimaryKey) continue;
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

    /// <summary>Выбрать ключ подписки для новой сессии: наименее загруженный аккаунт.</summary>
    /// Жёстко отсекаются исчерпанные (rejected/100% без overage) и аккаунты без доступа
    /// к запрошенной модели (Opus есть не на всех планах — CLI на таком аккаунте падает
    /// «issue with the selected model»); среди оставшихся — минимальная утилизация
    /// 5h-окна (при равенстве — случайный). Все способные исчерпаны — минимум из
    /// способных: лучше упереться в лимит на правильном аккаунте, чем гарантированно
    /// упасть на неправильном.
    public string Pick(string? model = null)
    {
        if (_subscriptions.Count == 0)
            return PrimaryKey;

        var candidates = new List<string>(_subscriptions.Count + 1);

        if (!IsExhausted(PrimaryKey) && SupportsModel(PrimaryKey, model))
            candidates.Add(PrimaryKey);

        foreach (var sub in _subscriptions)
            if (!IsExhausted(sub.Key) && SupportsModel(sub.Key, model))
                candidates.Add(sub.Key);

        if (candidates.Count > 0) return LeastLoaded(candidates);

        var capable = AllKeys().Where(k => SupportsModel(k, model)).ToList();
        return LeastLoaded(capable.Count > 0 ? capable : AllKeys());
    }

    // Модель требует Opus-тира (алиасы opus/opus[1m] и полные id claude-opus-*)
    public static bool RequiresOpus(string? model) =>
        !string.IsNullOrWhiteSpace(model) && model.Contains("opus", StringComparison.OrdinalIgnoreCase);

    /// <summary>Аккаунт может обслужить модель: для Opus-тира — только SupportsOpus-планы.</summary>
    /// Ключи вне пула (сторонние провайдеры deepseek/glm) не наша забота — true.
    public bool SupportsModel(string key, string? model)
    {
        if (!RequiresOpus(model)) return true;
        if (key == PrimaryKey) return true; // основная — полный план владельца
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

    // Основная + все дополнительные подписки.
    private List<string> AllKeys()
    {
        var keys = new List<string>(_subscriptions.Count + 1) { PrimaryKey };
        foreach (var sub in _subscriptions)
            keys.Add(sub.Key);
        return keys;
    }

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
