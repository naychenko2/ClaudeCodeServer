using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services;

// Память команды проекта (③-3.4): общее хранилище фактов/договорённостей проекта, из которого
// ВСЕ персоны команды recall'ят наравне с личной памятью — команда учится вместе, а не каждая
// про себя. MVP: JSON-стор data/team-memory.json (ключ «owner:project») + полнотекстовый recall
// (по общим словам); без Dify-векторизации — deliberately simpler, чем персональная память.
public class TeamMemoryService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly ConcurrentDictionary<string, List<TeamMemoryEntry>> _store = new();
    private readonly string _storePath;
    private readonly Lock _saveLock = new();
    private readonly ILogger<TeamMemoryService>? _log;

    public TeamMemoryService(IConfiguration config, ILogger<TeamMemoryService>? log = null)
    {
        _log = log;
        var dataDir = Path.GetDirectoryName(
            config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json"))!;
        _storePath = Path.Combine(dataDir, "team-memory.json");
        Load();
    }

    public IReadOnlyList<TeamMemoryEntry> List(string ownerId, string projectId) =>
        Get(ownerId, projectId);

    public TeamMemoryEntry Add(string ownerId, string projectId, string text)
    {
        var entry = new TeamMemoryEntry
        {
            OwnerId = ownerId,
            ProjectId = projectId,
            Text = text.Trim(),
        };
        lock (_saveLock)
        {
            var list = Get(ownerId, projectId);
            list.Add(entry);
            Save();
        }
        return entry;
    }

    public bool Remove(string ownerId, string projectId, string entryId)
    {
        lock (_saveLock)
        {
            var list = Get(ownerId, projectId);
            var ok = list.RemoveAll(e => e.Id == entryId) > 0;
            if (ok) Save();
            return ok;
        }
    }

    // Полнотекстовый recall: записи, разделяющие слова запроса, топ по перекрытию. null — пусто.
    // MVP-качество: без векторизации; для команды проектов обычно хватает (записей немного).
    public string? BuildRecallBlock(string ownerId, string projectId, string query, int topK = 4)
    {
        List<TeamMemoryEntry> snapshot;
        lock (_saveLock) snapshot = Get(ownerId, projectId).ToList();
        if (snapshot.Count == 0) return null;

        var q = Tokenize(query);
        if (q.Length == 0) return null;
        var ranked = snapshot
            .Select(e => (e, score: Tokenize(e.Text).Count(t => q.Contains(t))))
            .Where(x => x.score > 0)
            .OrderByDescending(x => x.score)
            .Take(topK).ToList();
        if (ranked.Count == 0) return null;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("## Память команды проекта");
        sb.AppendLine("Общие факты и договорённости проекта (помнят все персоны команды):");
        foreach (var (e, _) in ranked)
            sb.AppendLine($"- {e.Text}");
        return sb.ToString();
    }

    private List<TeamMemoryEntry> Get(string ownerId, string projectId) =>
        _store.GetOrAdd(Key(ownerId, projectId), _ => new List<TeamMemoryEntry>());

    private static string Key(string ownerId, string projectId) => $"{ownerId}:{projectId}";

    private static readonly HashSet<string> Stop = new(StringComparer.OrdinalIgnoreCase)
    { "и", "в", "на", "с", "по", "для", "не", "что", "это", "как", "to", "the", "a", "of", "and", "for", "in" };

    private static string[] Tokenize(string s) =>
        s.ToLowerInvariant().Split([' ', ',', '.', ';', ':', '!', '?', '\n', '\r', '\t', '(', ')'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(t => t.Length > 2 && !Stop.Contains(t))
        .Distinct()
        .ToArray();

    private void Load()
    {
        try
        {
            var dict = JsonFileStore.Load<Dictionary<string, List<TeamMemoryEntry>>>(_storePath, JsonOpts);
            if (dict is null) return;
            foreach (var kv in dict) _store[kv.Key] = kv.Value;
        }
        catch (Exception ex) { _log?.LogWarning(ex, "team-memory: не загрузился стор"); }
    }

    private void Save()
    {
        lock (_saveLock)
            JsonFileStore.Save(_storePath, _store.ToDictionary(kv => kv.Key, kv => kv.Value), JsonOpts);
    }
}
