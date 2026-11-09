namespace ClaudeHomeServer.Services.ImageEditor;

// Асинхронные блокировки по ключу с вытеснением: запись живёт, пока есть держатель или ожидающий,
// и удаляется, когда последний отпустил (счётчик ссылок под общим замком) — словарь не растёт вечно.
public sealed class KeyedLocks
{
    private sealed class Entry
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public int Refs;
    }

    private readonly object _sync = new();
    private readonly Dictionary<string, Entry> _entries = new();

    public int Count
    {
        get { lock (_sync) return _entries.Count; }
    }

    public async Task<IDisposable> AcquireAsync(string key, CancellationToken ct = default)
    {
        Entry entry;
        lock (_sync)
        {
            if (!_entries.TryGetValue(key, out entry!)) _entries[key] = entry = new Entry();
            entry.Refs++;
        }
        try
        {
            await entry.Gate.WaitAsync(ct);
        }
        catch
        {
            Unref(key, entry);
            throw;
        }
        return new Releaser(this, key, entry);
    }

    private void Unref(string key, Entry entry)
    {
        lock (_sync)
        {
            if (--entry.Refs == 0) _entries.Remove(key);
        }
    }

    private sealed class Releaser(KeyedLocks owner, string key, Entry entry) : IDisposable
    {
        private int _released;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0) return;
            entry.Gate.Release();
            owner.Unref(key, entry);
        }
    }
}
