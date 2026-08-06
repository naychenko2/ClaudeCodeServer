using System.Text.Json;
using System.Text.Json.Serialization;
using ClaudeHomeServer.Protocol;

namespace ClaudeHomeServer.Services.Mcp;

/// <summary>Откуда взято наблюдение: из system/init хода или из разовой пробы по кнопке.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum McpObservationSource { Init, Probe }

/// <summary>Последнее известное состояние сервера у владельца.</summary>
public class McpServerStatusEntry
{
    /// <summary>Нормализованный статус — см. <see cref="McpServerStatuses"/>.</summary>
    public string Status { get; set; } = McpServerStatuses.Unknown;
    public DateTime ObservedAt { get; set; } = DateTime.UtcNow;
    public McpObservationSource Source { get; set; }
    /// <summary>Чат, чей ход принёс наблюдение (только для Init).</summary>
    public string? SessionId { get; set; }
    public string? Error { get; set; }
}

/// <summary>
/// Статусы MCP-серверов: значения нормализуются к этому набору — CLI пишет их по-своему
/// («needs auth», «needs_auth»), а UI и проба обязаны говорить об одном и том же одним словом.
/// </summary>
public static class McpServerStatuses
{
    public const string Connected = "connected";
    public const string Failed = "failed";
    /// <summary>Сервер жив, но не пускает: 401 и «needs auth» — это «нужен вход», а не поломка.</summary>
    public const string NeedsAuth = "needs-auth";
    public const string Unknown = "unknown";

    /// <summary>Приводит статус CLI или пробы к набору выше; пустое/незнакомое — unknown.</summary>
    public static string Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return Unknown;
        var value = raw.Trim().ToLowerInvariant().Replace('_', '-').Replace(' ', '-');
        return value switch
        {
            "connected" or "ok" or "ready" => Connected,
            "needs-auth" or "needs-authentication" or "unauthorized" or "auth-required" => NeedsAuth,
            "failed" or "error" or "disconnected" => Failed,
            _ => Unknown,
        };
    }
}

/// <summary>
/// Последний известный статус MCP-серверов владельца — data/mcp-status.json
/// (<c>owner → ключ сервера → наблюдение</c>). Фонового поллинга нет: статус приезжает
/// из system/init каждого хода (бесплатно, вместе со встроенными серверами продукта)
/// либо из разовой пробы по кнопке (<see cref="McpProbeService"/>).
///
/// Файл в архив НЕ едет (исключение в <see cref="Backup.BackupPaths.ShouldInclude"/>):
/// восстановленное наблюдение врёт — оно описывает состояние чужой машины в прошлом.
/// </summary>
public class McpStatusStore
{
    public const string FileName = "mcp-status.json";

    // Наблюдение без смены статуса — обычное дело (init повторяется каждый ход): пишем на
    // диск только значимые изменения, иначе файл переписывался бы десятки раз за час.
    // Свежесть ObservedAt при этом всё равно доезжает — хотя бы раз в этот интервал.
    private static readonly TimeSpan SaveInterval = TimeSpan.FromMinutes(5);

    private readonly string _filePath;
    private readonly Dictionary<string, Dictionary<string, McpServerStatusEntry>> _byOwner;
    private readonly object _lock = new();
    private DateTime _lastSaved = DateTime.MinValue;

    public McpStatusStore(IConfiguration config)
    {
        var dataDir = Path.GetDirectoryName(
            config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json"))!;
        _filePath = Path.Combine(dataDir, FileName);
        _byOwner = JsonFileStore.Load<Dictionary<string, Dictionary<string, McpServerStatusEntry>>>(
            _filePath, JsonOptions) ?? new();
    }

    /// <summary>Наблюдения владельца (снимок: ключ сервера → статус).</summary>
    public IReadOnlyDictionary<string, McpServerStatusEntry> GetByOwner(string ownerId)
    {
        lock (_lock)
            return _byOwner.TryGetValue(ownerId, out var bag)
                ? new Dictionary<string, McpServerStatusEntry>(bag, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, McpServerStatusEntry>(StringComparer.OrdinalIgnoreCase);
    }

    public McpServerStatusEntry? Get(string ownerId, string serverKey)
    {
        lock (_lock)
            return _byOwner.TryGetValue(ownerId, out var bag) && bag.TryGetValue(serverKey, out var entry)
                ? entry : null;
    }

    /// <summary>
    /// Наблюдение из system/init хода: CLI перечисляет ВСЕ поднятые серверы — и встроенные
    /// продуктовые, и записи личного реестра. Пишем как есть, фильтр по реестру не нужен:
    /// имена совпадают с ключами конфига хода.
    /// </summary>
    public void RecordFromInit(string ownerId, string sessionId, IReadOnlyList<McpServerInfo> servers)
    {
        if (servers.Count == 0) return;
        var now = DateTime.UtcNow;
        lock (_lock)
        {
            var changed = false;
            var bag = Bag(ownerId);
            foreach (var server in servers)
            {
                var status = McpServerStatuses.Normalize(server.Status);
                changed |= Apply(bag, server.Name, status, McpObservationSource.Init, sessionId, error: null, now);
            }
            SaveIfNeeded(changed, now);
        }
    }

    /// <summary>Результат разовой пробы сервера.</summary>
    public McpServerStatusEntry RecordProbe(string ownerId, string serverKey, string status, string? error)
    {
        var now = DateTime.UtcNow;
        lock (_lock)
        {
            var bag = Bag(ownerId);
            // Пробу человек запустил руками и ждёт её результата — сохраняем всегда
            Apply(bag, serverKey, status, McpObservationSource.Probe, sessionId: null, error, now);
            Save(now);
            return bag[serverKey];
        }
    }

    /// <summary>
    /// Сервер снят с хода из-за авторизации: токенов нет или рефреш провалился (волна 7).
    /// Источник Init — наблюдение и правда пришло со старта хода, просто до запуска CLI.
    /// Пишем всегда: человек должен увидеть «нужен вход», а не гадать, почему сервера нет.
    /// </summary>
    public McpServerStatusEntry RecordAuthFailure(string ownerId, string serverKey, string error)
    {
        var now = DateTime.UtcNow;
        lock (_lock)
        {
            var bag = Bag(ownerId);
            Apply(bag, serverKey, McpServerStatuses.NeedsAuth, McpObservationSource.Init,
                sessionId: null, error, now);
            Save(now);
            return bag[serverKey];
        }
    }

    /// <summary>Убирает наблюдение (сервер удалён из реестра или сменил ключ).</summary>
    public void Remove(string ownerId, string serverKey)
    {
        lock (_lock)
        {
            if (!_byOwner.TryGetValue(ownerId, out var bag) || !bag.Remove(serverKey)) return;
            Save(DateTime.UtcNow);
        }
    }

    private Dictionary<string, McpServerStatusEntry> Bag(string ownerId)
    {
        if (_byOwner.TryGetValue(ownerId, out var bag))
        {
            // Словарь из файла приходит с дефолтным компаратором — ключи серверов
            // сравниваем без учёта регистра, как везде в реестре
            if (bag.Comparer != StringComparer.OrdinalIgnoreCase)
                _byOwner[ownerId] = bag = new Dictionary<string, McpServerStatusEntry>(bag, StringComparer.OrdinalIgnoreCase);
            return bag;
        }
        return _byOwner[ownerId] = new Dictionary<string, McpServerStatusEntry>(StringComparer.OrdinalIgnoreCase);
    }

    // true — наблюдение значимо изменилось (статус или текст ошибки), файл стоит переписать
    private static bool Apply(Dictionary<string, McpServerStatusEntry> bag, string serverKey,
        string status, McpObservationSource source, string? sessionId, string? error, DateTime now)
    {
        if (!bag.TryGetValue(serverKey, out var entry))
            bag[serverKey] = entry = new McpServerStatusEntry();
        var changed = entry.Status != status || entry.Error != error;
        entry.Status = status;
        entry.Source = source;
        entry.SessionId = sessionId;
        entry.Error = error;
        entry.ObservedAt = now;
        return changed;
    }

    private void SaveIfNeeded(bool changed, DateTime now)
    {
        if (changed || now - _lastSaved >= SaveInterval) Save(now);
    }

    private void Save(DateTime now)
    {
        _lastSaved = now;
        JsonFileStore.Save(_filePath, _byOwner, JsonOptions);
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
}
