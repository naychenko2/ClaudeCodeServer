using System.Collections.Concurrent;

namespace ClaudeHomeServer.Services;

// Время последней ФАКТИЧЕСКОЙ активности аккаунта пула подписок: живой ход чата
// (rate_limit_event из SessionManager) или пробный пинг (SubscriptionUsageWarmupService).
// Намеренно НЕ обновляется снимками SubscriptionOAuthUsageService — поллер best-effort
// и не значит, что аккаунт используется прямо сейчас (решение Андрея 2026-07-29: идл-пинг
// должен ориентироваться только на настоящие ходы, иначе простаивающий аккаунт с исправным
// OAuth-токеном никогда не считался бы простаивающим и не пинговался).
public sealed class SubscriptionActivityTracker
{
    private readonly ConcurrentDictionary<string, DateTime> _lastActivityUtc = new();

    public void Touch(string? key)
    {
        if (string.IsNullOrEmpty(key)) return;
        _lastActivityUtc[key] = DateTime.UtcNow;
    }

    // Простаивает ли аккаунт дольше порога (или активности по нему не было вовсе).
    public bool IsIdle(string key, TimeSpan idleThreshold) =>
        !_lastActivityUtc.TryGetValue(key, out var last) || DateTime.UtcNow - last >= idleThreshold;
}
