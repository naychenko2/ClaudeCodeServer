using System.Text.Json;
using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services;

// JSON-сторадж уведомлений. Per-user: все уведомления одного владельца в одном файле.
// Потокобезопасность через lock (как TaskManager, PushSubscriptionStore).
public class NotificationStore
{
    private readonly string _baseDir;
    private readonly ILogger<NotificationStore> _log;
    private readonly Lock _lock = new();

    // Индекс: userId → List<AppNotification> (lazy-loaded, lru-like — держим всех активных)
    private readonly Dictionary<string, List<AppNotification>> _cache = new();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    // Максимум уведомлений на пользователя (кольцевой буфер — старые вытесняются)
    private const int MaxPerUser = 500;

    public NotificationStore(IConfiguration config, ILogger<NotificationStore> log)
    {
        var dataDir = Path.GetDirectoryName(
            config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json"))!;
        _baseDir = Path.Combine(dataDir, "notifications");
        _log = log;
        Directory.CreateDirectory(_baseDir);
    }

    public async Task<List<NotificationListItem>> GetListAsync(string userId, int limit = 50, int offset = 0)
    {
        var all = await GetAllAsync(userId);
        lock (_lock)
        {
            return all.OrderByDescending(n => n.CreatedAt)
                .Skip(offset).Take(limit)
                .Select(MapToItem)
                .ToList();
        }
    }

    public async Task<NotificationListResponse> GetListWithCountsAsync(string userId,
        string? kind = null, bool? unreadOnly = null,
        int limit = 50, int offset = 0)
    {
        var all = await GetAllAsync(userId);
        lock (_lock)
        {
            var filtered = all.AsEnumerable();

            if (kind is not null && kind != "all")
                filtered = filtered.Where(n => n.Kind == kind);

            if (unreadOnly == true)
                filtered = filtered.Where(n => !n.IsRead);

            var items = filtered.OrderByDescending(n => n.CreatedAt)
                .Skip(offset).Take(limit)
                .Select(MapToItem)
                .ToList();

            return new NotificationListResponse
            {
                Items = items,
                TotalCount = filtered.Count(),
                UnreadCount = all.Count(n => !n.IsRead),
            };
        }
    }

    public async Task<NotificationListItem?> GetByIdAsync(string userId, string id)
    {
        var all = await GetAllAsync(userId);
        lock (_lock)
        {
            var n = all.FirstOrDefault(x => x.Id == id);
            return n is null ? null : MapToItem(n);
        }
    }

    // Добавить уведомление. Если превышен MaxPerUser — удаляются самые старые прочитанные.
    // Возвращает созданное уведомление (для SignalR-броадкаста).
    public async Task<NotificationListItem> AddAsync(string userId, CreateNotificationRequest req)
    {
        var notif = new AppNotification
        {
            OwnerId = userId,
            Kind = req.Kind,
            Type = req.Type,
            Title = req.Title,
            Body = req.Body,
            Url = req.Url,
            ProjectId = req.ProjectId,
            SessionId = req.SessionId,
            TaskId = req.TaskId,
            Source = req.Source,
            Tag = req.Tag,
            PersonaId = req.PersonaId,
            PersonaName = req.PersonaName,
            PersonaRole = req.PersonaRole,
            PersonaColor = req.PersonaColor,
            PersonaHasAvatar = req.PersonaHasAvatar,
            ProjectName = req.ProjectName,
        };

        var all = await GetAllAsync(userId);
        lock (_lock)
        {
            all.Insert(0, notif);

            // Вытеснение старых прочитанных
            if (all.Count > MaxPerUser)
            {
                var toRemove = all.Where(n => n.IsRead).OrderBy(n => n.CreatedAt)
                    .Take(all.Count - MaxPerUser).ToList();
                foreach (var r in toRemove) all.Remove(r);
                // Жёсткий потолок: лавина непрочитанных не должна раздувать файл без предела —
                // срезаем хвост (список новые-сверху, хвост = самые старые)
                if (all.Count > MaxPerUser)
                    all.RemoveRange(MaxPerUser, all.Count - MaxPerUser);
            }

            Save(userId, all);
        }

        return MapToItem(notif);
    }

    public async Task<bool> MarkReadAsync(string userId, string id)
    {
        var all = await GetAllAsync(userId);
        lock (_lock)
        {
            var n = all.FirstOrDefault(x => x.Id == id);
            if (n is null || n.IsRead) return false;
            n.IsRead = true;
            n.ReadAt = DateTime.UtcNow;
            Save(userId, all);
            return true;
        }
    }

    public async Task<int> MarkAllReadAsync(string userId)
    {
        var all = await GetAllAsync(userId);
        lock (_lock)
        {
            var count = all.Count(n => !n.IsRead);
            if (count == 0) return 0;
            var now = DateTime.UtcNow;
            foreach (var n in all.Where(n => !n.IsRead))
            {
                n.IsRead = true;
                n.ReadAt = now;
            }
            Save(userId, all);
            return count;
        }
    }

    // Массовое прочтение по списку id
    public async Task<int> MarkReadBatchAsync(string userId, List<string> ids)
    {
        var all = await GetAllAsync(userId);
        lock (_lock)
        {
            var count = 0;
            var idSet = ids.ToHashSet();
            foreach (var n in all.Where(n => idSet.Contains(n.Id) && !n.IsRead))
            {
                n.IsRead = true;
                n.ReadAt = DateTime.UtcNow;
                count++;
            }
            if (count > 0) Save(userId, all);
            return count;
        }
    }

    public async Task<bool> DeleteAsync(string userId, string id)
    {
        var all = await GetAllAsync(userId);
        lock (_lock)
        {
            var n = all.FirstOrDefault(x => x.Id == id);
            if (n is null) return false;
            all.Remove(n);
            Save(userId, all);
            return true;
        }
    }

    public async Task<int> DeleteBatchAsync(string userId, List<string> ids)
    {
        var all = await GetAllAsync(userId);
        lock (_lock)
        {
            var idSet = ids.ToHashSet();
            var count = all.RemoveAll(n => idSet.Contains(n.Id));
            if (count > 0) Save(userId, all);
            return count;
        }
    }

    // Удалить все прочитанные уведомления
    public async Task<int> DeleteReadAsync(string userId)
    {
        var all = await GetAllAsync(userId);
        lock (_lock)
        {
            var count = all.RemoveAll(n => n.IsRead);
            if (count > 0) Save(userId, all);
            return count;
        }
    }

    // Получение непрочитанных (для бейджа)
    public async Task<int> GetUnreadCountAsync(string userId)
    {
        var all = await GetAllAsync(userId);
        lock (_lock) { return all.Count(n => !n.IsRead); }
    }

    // ======== Internal ========

    private async Task<List<AppNotification>> GetAllAsync(string userId)
    {
        lock (_lock)
        {
            if (_cache.TryGetValue(userId, out var cached))
                return cached;
        }

        var path = GetPath(userId);
        List<AppNotification> list;
        if (File.Exists(path))
        {
            try
            {
                var json = await File.ReadAllTextAsync(path);
                list = JsonSerializer.Deserialize<List<AppNotification>>(json, JsonOpts) ?? [];
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Ошибка чтения уведомлений для {UserId}", userId);
                list = [];
            }
        }
        else
        {
            list = [];
        }

        lock (_lock)
        {
            // Double-check: параллельный первый вызов мог успеть раньше — работаем с его
            // списком, иначе два потока мутируют разные копии и запись теряется (lost update)
            if (_cache.TryGetValue(userId, out var winner)) return winner;
            _cache[userId] = list;
        }
        return list;
    }

    private void Save(string userId, List<AppNotification> list)
    {
        var path = GetPath(userId);
        var json = JsonSerializer.Serialize(list, JsonOpts);
        File.WriteAllText(path, json);

        // Обновляем кэш
        _cache[userId] = list;
    }

    private string GetPath(string userId) =>
        Path.Combine(_baseDir, $"{Sanitize(userId)}.json");

    private static string Sanitize(string s)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Concat(s.Select(c => invalid.Contains(c) ? '_' : c));
    }

    private static NotificationListItem MapToItem(AppNotification n) => new()
    {
        Id = n.Id,
        Kind = n.Kind,
        Type = n.Type,
        Title = n.Title,
        Body = n.Body,
        Url = n.Url,
        ProjectId = n.ProjectId,
        SessionId = n.SessionId,
        TaskId = n.TaskId,
        Source = n.Source,
        Tag = n.Tag,
        PersonaId = n.PersonaId,
        PersonaName = n.PersonaName,
        PersonaRole = n.PersonaRole,
        PersonaColor = n.PersonaColor,
        PersonaHasAvatar = n.PersonaHasAvatar,
        ProjectName = n.ProjectName,
        IsRead = n.IsRead,
        CreatedAt = n.CreatedAt,
        ReadAt = n.ReadAt,
    };
}
