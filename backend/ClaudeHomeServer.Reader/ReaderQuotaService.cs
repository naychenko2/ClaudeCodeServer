using System.Collections.Concurrent;

namespace ClaudeHomeServer.Services.Reader;

/// <summary>
/// Пределы «на владельца» из ADR-005 раздела 5: не более 2 одновременных чтений и 30 в минуту.
/// Не часть таксономии из 15 кодов ошибки — квота отвечает обычным 429, без причины в теле.
/// </summary>
public sealed class ReaderQuotaService(TimeProvider? timeProvider = null)
{
    public const int MaxConcurrentPerOwner = 2;
    public const int MaxPerMinutePerOwner = 30;

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, int> _concurrent = new();
    private readonly ConcurrentDictionary<string, RateWindow> _windows = new();
    private readonly object _concurrentGate = new();

    private sealed class RateWindow
    {
        public int Count;
        public DateTimeOffset WindowStart;
    }

    /// <summary>Занимает слот одновременного чтения. null, если владелец уже держит MaxConcurrentPerOwner.</summary>
    public IDisposable? TryAcquireConcurrency(string ownerId)
    {
        lock (_concurrentGate)
        {
            var current = _concurrent.GetValueOrDefault(ownerId);
            if (current >= MaxConcurrentPerOwner) return null;
            _concurrent[ownerId] = current + 1;
        }

        return new Release(() =>
        {
            lock (_concurrentGate)
            {
                var current = _concurrent.GetValueOrDefault(ownerId);
                if (current <= 1) _concurrent.TryRemove(ownerId, out _);
                else _concurrent[ownerId] = current - 1;
            }
        });
    }

    /// <summary>true — запрос укладывается в 30/мин фиксированное окно владельца.</summary>
    public bool TryAcquireRate(string ownerId)
    {
        var now = _time.GetUtcNow();
        var window = _windows.GetOrAdd(ownerId, _ => new RateWindow { WindowStart = now, Count = 0 });
        lock (window)
        {
            if (now - window.WindowStart >= TimeSpan.FromMinutes(1))
            {
                window.WindowStart = now;
                window.Count = 0;
            }
            if (window.Count >= MaxPerMinutePerOwner) return false;
            window.Count++;
            return true;
        }
    }

    private sealed class Release(Action onDispose) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) onDispose();
        }
    }
}
