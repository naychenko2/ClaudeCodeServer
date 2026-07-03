using System.Text.Json;

namespace ClaudeHomeServer.Services;

// Web-push подписка одного устройства пользователя (браузер/установленная PWA)
public class PushSubscriptionRecord
{
    public string Endpoint { get; set; } = "";
    public string P256dh { get; set; } = "";
    public string Auth { get; set; } = "";
    public string? UserAgent { get; set; }
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
}

// Подписки per-user, несколько устройств на пользователя.
// Хранение — data/push-subscriptions.json (по образцу UserStore: лок + запись файла).
public class PushSubscriptionStore
{
    private readonly string _filePath;
    private Dictionary<string, List<PushSubscriptionRecord>> _byUser = new();
    private readonly object _lock = new();

    public PushSubscriptionStore(IConfiguration config)
    {
        var dataDir = Path.GetDirectoryName(
            config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json"))!;
        _filePath = Path.Combine(dataDir, "push-subscriptions.json");
        Load();
    }

    private void Load()
    {
        if (!File.Exists(_filePath)) return;
        try
        {
            var json = File.ReadAllText(_filePath);
            _byUser = JsonSerializer.Deserialize<Dictionary<string, List<PushSubscriptionRecord>>>(json, JsonOptions) ?? new();
        }
        catch { /* первый запуск или повреждённый файл */ }
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        File.WriteAllText(_filePath, JsonSerializer.Serialize(_byUser, JsonOptions));
    }

    /// <summary>Добавляет или обновляет подписку устройства (ключ — endpoint).</summary>
    public void Upsert(string userId, PushSubscriptionRecord record)
    {
        lock (_lock)
        {
            var list = _byUser.TryGetValue(userId, out var l) ? l : _byUser[userId] = [];
            list.RemoveAll(s => s.Endpoint == record.Endpoint);
            list.Add(record);
            Save();
        }
    }

    /// <summary>Убирает подписку устройства пользователя. true — что-то удалили.</summary>
    public bool Remove(string userId, string endpoint)
    {
        lock (_lock)
        {
            if (!_byUser.TryGetValue(userId, out var list)) return false;
            var removed = list.RemoveAll(s => s.Endpoint == endpoint) > 0;
            if (removed) Save();
            return removed;
        }
    }

    /// <summary>Зачистка мёртвой подписки по endpoint (push-сервис ответил 404/410).</summary>
    public void RemoveByEndpoint(string endpoint)
    {
        lock (_lock)
        {
            var removed = false;
            foreach (var list in _byUser.Values)
                removed |= list.RemoveAll(s => s.Endpoint == endpoint) > 0;
            if (removed) Save();
        }
    }

    /// <summary>Снимок подписок пользователя (итерируется вне лока).</summary>
    public IReadOnlyList<PushSubscriptionRecord> GetByUser(string userId)
    {
        lock (_lock)
            return _byUser.TryGetValue(userId, out var list) ? list.ToList() : [];
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
}
