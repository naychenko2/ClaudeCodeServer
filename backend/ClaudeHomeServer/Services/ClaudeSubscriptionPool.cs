using System.Collections.Concurrent;
using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services;

// Пул подписок Claude. Позволяет использовать несколько аккаунтов на одном сервере:
// новые чаты случайно распределяются между non-exhausted подписками;
// при исчерпании лимита одной — сервер автоматически переключается на другую.
public class ClaudeSubscriptionPool
{
    public const string Section = "ClaudeSubscriptions";
    public const string PrimaryKey = "claude";

    private readonly IReadOnlyList<ClaudeSubscriptionConfig> _subscriptions;
    // exhaustedKey → resetsAt (UTC, null = пока не сбросится вручную / timeout 60с)
    private readonly ConcurrentDictionary<string, DateTime?> _exhausted = new();

    public ClaudeSubscriptionPool(IConfiguration config)
    {
        var list = new List<ClaudeSubscriptionConfig>();
        foreach (var child in config.GetSection(Section).GetChildren())
        {
            var cfg = child.Get<ClaudeSubscriptionConfig>();
            if (cfg is null) continue;
            cfg.Key = child.Key;
            if (cfg.Enabled)
                list.Add(cfg);
        }
        _subscriptions = list.AsReadOnly();
    }

    /// <summary>Все настроенные дополнительные подписки</summary>
    public IReadOnlyList<ClaudeSubscriptionConfig> All => _subscriptions;

    /// <summary>Есть ли хотя бы одна дополнительная подписка</summary>
    public bool HasExtra => _subscriptions.Count > 0;

    /// <summary>Выбрать ключ подписки для новой сессии: random non-exhausted.</summary>
    /// Возвращает "claude" (основная) если нет дополнительных или все исчерпаны.
    public string Pick()
    {
        if (_subscriptions.Count == 0)
            return PrimaryKey;

        var candidates = new List<string>(_subscriptions.Count + 1);

        if (!IsExhausted(PrimaryKey))
            candidates.Add(PrimaryKey);

        foreach (var sub in _subscriptions)
            if (!IsExhausted(sub.Key))
                candidates.Add(sub.Key);

        if (candidates.Count == 0)
            return PrimaryKey; // все исчерпаны — fallback на основную

        return candidates[Random.Shared.Next(candidates.Count)];
    }

    /// <summary>Пометить подписку как исчерпанную.</summary>
    /// resetsAt — время сброса лимита (из rate_limit_event); null — 60с по умолчанию.
    public void MarkExhausted(string key, DateTime? resetsAt = null)
    {
        if (resetsAt.HasValue && resetsAt.Value.Kind != DateTimeKind.Utc)
            resetsAt = resetsAt.Value.ToUniversalTime();
        _exhausted[key] = resetsAt ?? DateTime.UtcNow.AddSeconds(60);
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
