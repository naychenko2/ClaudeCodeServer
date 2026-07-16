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

    // Прибавка важности при повторе факта: дедуп-on-write усиливает существующую запись, а не плодит дубль
    private const double DedupBoost = 0.1;

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

    // Добавить запись командной памяти. Дедуп-on-write (внутри _saveLock): одинаковый текст того же
    // типа не плодим — усиливаем существующую (важность + более полный текст), чтобы авто-захват
    // не засорял общий стор. Старый вызов Add(owner, project, text) остаётся валидным (дефолты).
    public TeamMemoryEntry Add(string ownerId, string projectId, string text,
        TeamMemoryType type = TeamMemoryType.Fact,
        TeamMemorySource source = TeamMemorySource.Manual,
        string? sourceSessionId = null, double? salience = null)
    {
        var trimmed = text.Trim();
        lock (_saveLock)
        {
            var list = Get(ownerId, projectId);
            var dup = list.FirstOrDefault(e => e.Type == type
                && string.Equals(e.Text, trimmed, StringComparison.OrdinalIgnoreCase));
            if (dup is not null)
            {
                var baseSal = salience is null
                    ? dup.Salience
                    : Math.Max(dup.Salience, Math.Clamp(salience.Value, 0.05, 1.0));
                dup.Salience = Math.Clamp(baseSal + DedupBoost, 0.05, 1.0);
                if (trimmed.Length > dup.Text.Length) dup.Text = trimmed;   // более информативная формулировка
                Save();
                return dup;
            }
            var entry = new TeamMemoryEntry
            {
                OwnerId = ownerId,
                ProjectId = projectId,
                Text = trimmed,
                Type = type,
                Source = source,
                SourceSessionId = sourceSessionId,
                Salience = salience is null ? 1.0 : Math.Clamp(salience.Value, 0.05, 1.0),
            };
            list.Add(entry);
            Save();
            return entry;
        }
    }

    // Отредактировать текст записи вручную (UI-редактирование)
    public TeamMemoryEntry? Update(string ownerId, string projectId, string entryId, string text)
    {
        lock (_saveLock)
        {
            var entry = Get(ownerId, projectId).FirstOrDefault(e => e.Id == entryId);
            if (entry is null) return null;
            entry.Text = text.Trim();
            Save();
            return entry;
        }
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

    // Результат recall'а: markdown-блок для промпта + записи, реально попавшие в блок
    // (для манифеста атрибуции F3 — «персона опирается на…», см. SessionManager). Text=null — пусто.
    public sealed record TeamRecallResult(string? Text, IReadOnlyList<TeamMemoryEntry> Used);

    // Полнотекстовый recall: записи, разделяющие слова запроса, топ по перекрытию.
    // MVP-качество: без векторизации; для команды проектов обычно хватает (записей немного).
    public TeamRecallResult BuildRecallBlock(string ownerId, string projectId, string query, int topK = 4)
    {
        List<TeamMemoryEntry> snapshot;
        lock (_saveLock) snapshot = Get(ownerId, projectId).ToList();
        if (snapshot.Count == 0) return new TeamRecallResult(null, []);

        var q = Tokenize(query);
        if (q.Length == 0) return new TeamRecallResult(null, []);
        var ranked = snapshot
            .Select(e => (e, score: Tokenize(e.Text).Count(t => q.Contains(t))))
            .Where(x => x.score > 0)
            .OrderByDescending(x => x.score)
            .Take(topK).ToList();
        if (ranked.Count == 0) return new TeamRecallResult(null, []);

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("## Память команды проекта");
        sb.AppendLine("Общие факты и договорённости проекта (помнят все персоны команды):");
        foreach (var (e, _) in ranked)
            sb.AppendLine($"- {e.Text}");
        return new TeamRecallResult(sb.ToString(), ranked.Select(x => x.e).ToList());
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
