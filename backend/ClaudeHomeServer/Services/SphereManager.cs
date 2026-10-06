using System.Collections.Concurrent;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Spheres;

namespace ClaudeHomeServer.Services;

// Хранилище групп проектов. Схема повторяет ProjectManager: in-memory словарь +
// сериализация в data/groups.json. Группы привязаны к владельцу (OwnerId).
public class SphereManager : ISphereDirectory
{
    private readonly ConcurrentDictionary<string, Sphere> _groups = new();
    private readonly string _storePath;
    private readonly UserStore _users;
    private readonly ProjectManager _projects;
    private readonly FeatureFlagService _flags;
    private readonly Lock _saveLock = new();

    public SphereManager(IConfiguration config, UserStore users, ProjectManager projects, FeatureFlagService flags)
    {
        _users = users;
        _projects = projects;
        _flags = flags;
        var dataPath = config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json");
        // Кладём groups.json рядом с projects.json
        _storePath = Path.Combine(Path.GetDirectoryName(dataPath)!, "groups.json");
        Load();
    }

    public IReadOnlyList<Sphere> GetByOwner(string userId) =>
        _groups.Values.Where(g => g.OwnerId == userId).OrderBy(g => g.Order).ToList();

    public Sphere? GetById(string id) => _groups.GetValueOrDefault(id);

    // Своя сфера владельца; чужая и несуществующая неотличимы
    public Sphere? GetOwned(string id, string ownerId) =>
        _groups.GetValueOrDefault(id) is { } g && g.OwnerId == ownerId ? g : null;

    // Отказ для groupId проекта: пусто/null — «без сферы», иначе сфера обязана быть своей
    public string? GroupRefusal(string ownerId, string? groupId) =>
        string.IsNullOrEmpty(groupId) || GetOwned(groupId, ownerId) is not null
            ? null
            : "Сфера не найдена или принадлежит другому владельцу";

    public bool Enabled(string ownerId) => _flags.IsEnabled(ownerId, FeatureFlagKeys.Spheres);

    public string? SphereOf(string ownerId, string projectId)
    {
        if (!Enabled(ownerId)) return null;
        var groupId = _projects.GetById(projectId) is { } p && p.OwnerId == ownerId ? p.GroupId : null;
        return groupId is not null && GetOwned(groupId, ownerId) is not null ? groupId : null;
    }

    public IReadOnlyList<string> ProjectsOf(string ownerId, string sphereId)
    {
        if (!Enabled(ownerId) || GetOwned(sphereId, ownerId) is null) return [];
        return _projects.GetByOwner(ownerId).Where(p => p.GroupId == sphereId).Select(p => p.Id).ToList();
    }

    public string? SphereName(string ownerId, string sphereId) => GetOwned(sphereId, ownerId)?.Name;

    public Sphere Create(string name, string color, string userId)
    {
        var maxOrder = _groups.Values.Where(g => g.OwnerId == userId)
            .Select(g => (int?)g.Order).Max() ?? -1;
        var group = new Sphere
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

    public Sphere Update(string id, string? name, string? color, string? icon = null, string? charter = null)
    {
        var group = _groups.GetValueOrDefault(id)
            ?? throw new KeyNotFoundException($"Группа не найдена: {id}");
        if (name is not null) group.Name = name;
        if (color is not null) group.Color = color;
        // Пустая строка очищает значок и хартию
        if (icon is not null) group.Icon = icon.Length == 0 ? null : icon;
        if (charter is not null) group.Charter = charter.Length == 0 ? null : charter;
        group.UpdatedAt = DateTime.UtcNow;
        Save();
        return group;
    }

    // Присваивает Order по позиции в orderedIds; группы не из списка сохраняют относительный порядок в конце
    public IReadOnlyList<Sphere> Reorder(string userId, IList<string> orderedIds)
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
        var list = JsonFileStore.Load<List<Sphere>>(_storePath,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (list is not null)
            foreach (var g in list)
                _groups[g.Id] = g;

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
            JsonFileStore.Save(_storePath, _groups.Values.ToList());
        }
    }
}
