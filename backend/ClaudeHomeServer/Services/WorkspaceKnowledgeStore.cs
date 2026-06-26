using System.Collections.Concurrent;
using System.Text.Json;
using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services;

public class WorkspaceKnowledgeStore
{
    private readonly ConcurrentDictionary<string, WorkspaceKnowledge> _store =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly string _storePath;
    private readonly Lock _saveLock = new();

    public WorkspaceKnowledgeStore(IConfiguration config)
    {
        var projectsPath = config["DataPath"]
            ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json");
        var dataDir = Path.GetDirectoryName(projectsPath)
            ?? Path.Combine(AppContext.BaseDirectory, "data");
        _storePath = Path.Combine(dataDir, "workspace-knowledge.json");
        Load();
    }

    public static string NormalizePath(string path) =>
        Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .ToLowerInvariant();

    public WorkspaceKnowledge? GetByPath(string rootPath) =>
        _store.GetValueOrDefault(NormalizePath(rootPath));

    public WorkspaceKnowledge GetOrCreate(string rootPath)
    {
        var key = NormalizePath(rootPath);
        return _store.GetOrAdd(key, _ => new WorkspaceKnowledge { RootPath = rootPath });
    }

    public void Save(WorkspaceKnowledge wk)
    {
        var key = NormalizePath(wk.RootPath);
        wk.UpdatedAt = DateTime.UtcNow;
        _store[key] = wk;
        Persist();
    }

    public void Delete(string rootPath)
    {
        _store.TryRemove(NormalizePath(rootPath), out _);
        Persist();
    }

    // Однократная миграция: переносит DifyDatasetId/DocumentTags из старых Project-записей
    public void MigrateFromProjects(IEnumerable<Project> projects)
    {
        var migrated = false;
        foreach (var p in projects.Where(p => !string.IsNullOrEmpty(p.DifyDatasetId)))
        {
            var key = NormalizePath(p.RootPath);
            _store.GetOrAdd(key, _ =>
            {
                migrated = true;
                return new WorkspaceKnowledge
                {
                    RootPath = p.RootPath,
                    DifyDatasetId = p.DifyDatasetId,
                    DocumentTags = p.DocumentTags,
                    UpdatedAt = DateTime.UtcNow,
                };
            });
        }
        if (migrated) Persist();
    }

    private void Load()
    {
        if (!File.Exists(_storePath)) return;
        try
        {
            var json = File.ReadAllText(_storePath);
            var list = JsonSerializer.Deserialize<List<WorkspaceKnowledge>>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (list is null) return;
            foreach (var wk in list)
                _store[NormalizePath(wk.RootPath)] = wk;
        }
        catch { }
    }

    private void Persist()
    {
        lock (_saveLock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_storePath)!);
            File.WriteAllText(_storePath, JsonSerializer.Serialize(_store.Values.ToList()));
        }
    }
}
