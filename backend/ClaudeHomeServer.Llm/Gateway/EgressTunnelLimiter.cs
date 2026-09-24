namespace ClaudeHomeServer.Services.Llm.Gateway;

// Счётчик одновременных туннелей выхода на ход и на устройство. Место берётся до соединения
// наружу и возвращается по концу туннеля; нет места — туннель не открывается вовсе (429).
// Пустые счётчики удаляются, чтобы словарь не рос по числу прошедших ходов.
public sealed class EgressTunnelLimiter
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, int> _turns = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _devices = new(StringComparer.Ordinal);

    // null — потолок хода или устройства исчерпан
    public IDisposable? TryAcquire(string turnId, string deviceId, int maxPerTurn, int maxPerDevice)
    {
        lock (_gate)
        {
            if (_turns.GetValueOrDefault(turnId) >= maxPerTurn || _devices.GetValueOrDefault(deviceId) >= maxPerDevice)
                return null;
            _turns[turnId] = _turns.GetValueOrDefault(turnId) + 1;
            _devices[deviceId] = _devices.GetValueOrDefault(deviceId) + 1;
        }
        return new Lease(this, turnId, deviceId);
    }

    private void Release(string turnId, string deviceId)
    {
        lock (_gate)
        {
            Decrement(_turns, turnId);
            Decrement(_devices, deviceId);
        }
    }

    private static void Decrement(Dictionary<string, int> counters, string key)
    {
        if (!counters.TryGetValue(key, out var count)) return;
        if (count <= 1) counters.Remove(key);
        else counters[key] = count - 1;
    }

    private sealed class Lease(EgressTunnelLimiter owner, string turnId, string deviceId) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) owner.Release(turnId, deviceId);
        }
    }
}
