using System.Text.Json;

namespace ClaudeHomeServer.Services.Dossiers;

// Снятый конспект обсуждения одного чата (ADR-004 §6). Topic — название чата НА МОМЕНТ
// снятия: фиксируется вместе с конспектом, чтобы позднейшее переименование чата не
// двигало путь файла в ветке (полное дерево пересобирается каждым экспортом — путь
// обязан быть стабильным, иначе каждый экспорт плодил бы коммиты). GeneratedAt — якорь
// шапки файла. Наличие записи = «конспект снят»: дедуп для генерации, модель повторно
// не зовётся; отказ модели записи не оставляет — следующий экспорт попробует снова.
public sealed record DossierDiscussionRecord(
    string SessionId, string Topic, string Content, DateTimeOffset GeneratedAt);

// Хранилище конспектов обсуждений: data/dossiers/discussions.json, ключ
// {ownerId}:{projectId}:{sessionId}. Отдельный JSON-стор рядом с паспортами (по образцу
// knowledge.json у DossierStore). Состояние «конспект снят» — наличие записи ВМЕСТЕ с
// контентом, а не маркер в state.json: state.json — плоский словарь HEAD'ов деревьев,
// переписываемый каждым тиком захвата; маркер без контента при этом ломал бы повторный
// экспорт (после паузы опт-out и возврата контент конспекта было бы неоткуда взять),
// а килобайтные тексты раздули бы каждую запись HEAD.
public sealed class DossierDiscussionStore
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly string _path;
    private readonly Lock _lock = new();
    private Dictionary<string, DossierDiscussionRecord> _map;

    public DossierDiscussionStore(IConfiguration config)
    {
        var dataDir = Path.GetDirectoryName(Path.GetFullPath(
            config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json")))!;
        _path = Path.Combine(dataDir, "dossiers", "discussions.json");
        _map = JsonFileStore.Load<Dictionary<string, DossierDiscussionRecord>>(_path, JsonOpts) ?? new();
    }

    private static string Key(string ownerId, string projectId, string sessionId) =>
        $"{ownerId}:{projectId}:{sessionId}";

    public DossierDiscussionRecord? Get(string ownerId, string projectId, string sessionId)
    {
        lock (_lock) return _map.GetValueOrDefault(Key(ownerId, projectId, sessionId));
    }

    public void Set(string ownerId, string projectId, DossierDiscussionRecord record)
    {
        lock (_lock)
        {
            _map[Key(ownerId, projectId, record.SessionId)] = record;
            JsonFileStore.Save(_path, _map, JsonOpts);
        }
    }

    // Все конспекты проекта (для сборки дерева ветки)
    public IReadOnlyList<DossierDiscussionRecord> List(string ownerId, string projectId)
    {
        var prefix = ownerId + ":" + projectId + ":";
        lock (_lock)
            return _map
                .Where(kv => kv.Key.StartsWith(prefix, StringComparison.Ordinal))
                .Select(kv => kv.Value)
                .ToList();
    }
}
