using System.Collections.Concurrent;
using System.Text.Json;
using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services;

// Хранилище групп проектов. Схема повторяет ProjectManager: in-memory словарь +
// сериализация в data/groups.json. Группы привязаны к владельцу (OwnerId).
public class ProjectGroupManager
{
    private readonly ConcurrentDictionary<string, ProjectGroup> _groups = new();
    private readonly string _storePath;
    private readonly UserStore _users;
    private readonly Lock _saveLock = new();

    public ProjectGroupManager(IConfiguration config, UserStore users)
    {
        _users = users;
        var dataPath = config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json");
        // Кладём groups.json рядом с projects.json
        _storePath = Path.Combine(Path.GetDirectoryName(dataPath)!, "groups.json");
        Load();
    }

    public IReadOnlyList<ProjectGroup> GetByOwner(string userId) =>
        _groups.Values.Where(g => g.OwnerId == userId).OrderBy(g => g.Order).ToList();

    public ProjectGroup? GetById(string id) => _groups.GetValueOrDefault(id);

    public ProjectGroup Create(string name, string color, string userId)
    {
        var maxOrder = _groups.Values.Where(g => g.OwnerId == userId)
            .Select(g => (int?)g.Order).Max() ?? -1;
        var group = new ProjectGroup
        {
            Name = name,
            Color = color,
            OwnerId = userId,
            Order = maxOrder + 1,
        };
        _groups[group.Id] = group;
        Save();
        return group;
    }

    public ProjectGroup Update(string id, string? name, string? color)
    {
        var group = _groups.GetValueOrDefault(id)
            ?? throw new KeyNotFoundException($"Группа не найдена: {id}");
        if (name is not null) group.Name = name;
        if (color is not null) group.Color = color;
        group.UpdatedAt = DateTime.UtcNow;
        Save();
        return group;
    }

    // Присваивает Order по позиции в orderedIds; группы не из списка сохраняют относительный порядок в конце
    public IReadOnlyList<ProjectGroup> Reorder(string userId, IList<string> orderedIds)
    {
        for (var i = 0; i < orderedIds.Count; i++)
        {
            var g = _groups.GetValueOrDefault(orderedIds[i]);
            if (g is not null && g.OwnerId == userId)
            {
                g.Order = i;
                g.UpdatedAt = DateTime.UtcNow;
            }
        }
        Save();
        return GetByOwner(userId);
    }

    public bool Delete(string id)
    {
        var removed = _groups.TryRemove(id, out _);
        if (removed) Save();
        return removed;
    }

    private void Load()
    {
        if (!File.Exists(_storePath)) return;
        try
        {
            var json = File.ReadAllText(_storePath);
            var list = JsonSerializer.Deserialize<List<ProjectGroup>>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (list is null) return;
            foreach (var g in list)
                _groups[g.Id] = g;
        }
        catch { /* первый запуск или повреждённый файл */ }

        // Миграция: группы без OwnerId → первый пользователь
        var firstUser = _users.GetFirst();
        if (firstUser is not null)
        {
            var needsSave = false;
            foreach (var g in _groups.Values.Where(g => g.OwnerId is null))
            {
                g.OwnerId = firstUser.Id;
                needsSave = true;
            }
            if (needsSave) Save();
        }
    }

    private void Save()
    {
        lock (_saveLock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_storePath)!);
            File.WriteAllText(_storePath, JsonSerializer.Serialize(_groups.Values.ToList()));
        }
    }
}
