using System.Collections.Concurrent;
using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services;

// Хранит метки синхронизации (общие для всех устройств) в data/sync.json.
// Метки на сервере → один и тот же набор офлайн-файлов на любом устройстве.
public class SyncService
{
    private readonly ConcurrentDictionary<string, List<SyncMark>> _marks = new();
    private readonly string _storePath;
    private readonly Lock _lock = new();

    public SyncService(IConfiguration config)
    {
        var dataDir = Path.GetDirectoryName(
            config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json"))
            ?? Path.Combine(AppContext.BaseDirectory, "data");
        _storePath = Path.Combine(dataDir, "sync.json");
        Load();
    }

    public IReadOnlyList<SyncMark> GetMarks(string projectId) =>
        _marks.TryGetValue(projectId, out var list) ? list.ToList() : [];

    // path == "" + isDirectory == true → синхронизация всего проекта (корневая метка)
    public void Add(string projectId, string path, bool isDirectory)
    {
        path = Normalize(path);
        lock (_lock)
        {
            var list = _marks.GetOrAdd(projectId, _ => []);
            if (!list.Any(m => string.Equals(m.Path, path, StringComparison.OrdinalIgnoreCase)))
                list.Add(new SyncMark(path, isDirectory));
            // Папка покрывает всё содержимое — убираем ставшие избыточными метки потомков.
            // Корневая метка ("") покрывает весь проект → убираем все остальные.
            if (isDirectory)
                list.RemoveAll(m => !string.Equals(m.Path, path, StringComparison.OrdinalIgnoreCase)
                    && (path.Length == 0 || m.Path.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase)));
            Save();
        }
    }

    public void Remove(string projectId, string path)
    {
        path = Normalize(path);
        lock (_lock)
        {
            if (_marks.TryGetValue(projectId, out var list) &&
                list.RemoveAll(m => string.Equals(m.Path, path, StringComparison.OrdinalIgnoreCase)) > 0)
                Save();
        }
    }

    // Состояние синхронизации пути: "direct" (помечен сам), "inherited" (через папку-предок
    // или корневую метку всего проекта), null.
    public string? GetSyncState(string projectId, string path)
    {
        if (!_marks.TryGetValue(projectId, out var list) || list.Count == 0) return null;
        path = Normalize(path);
        if (list.Any(m => string.Equals(m.Path, path, StringComparison.OrdinalIgnoreCase)))
            return "direct";
        // Корневая метка ("") синхронизирует весь проект
        if (path.Length != 0 && list.Any(m => m.IsDirectory && m.Path.Length == 0))
            return "inherited";
        if (list.Any(m => m.IsDirectory && m.Path.Length != 0
                && path.StartsWith(m.Path + "/", StringComparison.OrdinalIgnoreCase)))
            return "inherited";
        return null;
    }

    private static string Normalize(string p) => p.Replace('\\', '/').Trim('/');

    private void Load()
    {
        var data = JsonFileStore.Load<Dictionary<string, List<SyncMark>>>(_storePath);
        if (data is null) return;
        foreach (var (projectId, list) in data)
            _marks[projectId] = list;
    }

    private void Save()
    {
        try
        {
            JsonFileStore.Save(_storePath, _marks.ToDictionary(kv => kv.Key, kv => kv.Value));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[SyncService] Не удалось сохранить {_storePath}: {ex.Message}");
        }
    }
}
