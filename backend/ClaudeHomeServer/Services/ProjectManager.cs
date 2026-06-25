using System.Collections.Concurrent;
using System.Text.Json;
using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services;

public class ProjectManager
{
    private readonly ConcurrentDictionary<string, Project> _projects = new();
    private readonly string _storePath;
    private readonly UserStore _users;
    private readonly AppSettingsService _appSettings;

    public ProjectManager(IConfiguration config, UserStore users, AppSettingsService appSettings)
    {
        _users = users;
        _appSettings = appSettings;
        _storePath = config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json");
        Load();
    }

    public IReadOnlyCollection<Project> GetAll() => _projects.Values.ToList();

    public IReadOnlyCollection<Project> GetByOwner(string userId) =>
        _projects.Values.Where(p => p.OwnerId == userId).ToList();

    public Project? GetById(string id) => _projects.GetValueOrDefault(id);

    public Project? GetByName(string name) =>
        _projects.Values.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    public Project Create(string name, string? rootPath, string userId, string username, bool createDirectory = false)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            var s = _appSettings.Get();
            if (string.IsNullOrWhiteSpace(s.DefaultProjectsPath))
                throw new ArgumentException("Укажите путь к папке или задайте папку по умолчанию в настройках");
            rootPath = Path.Combine(s.DefaultProjectsPath, username, name);
            createDirectory = true;
        }

        if (createDirectory)
            Directory.CreateDirectory(rootPath);
        else if (!Directory.Exists(rootPath))
            throw new DirectoryNotFoundException($"Папка не найдена: {rootPath}");

        var project = new Project { Name = name, RootPath = rootPath, OwnerId = userId };
        _projects[project.Id] = project;
        Save();
        return project;
    }

    public Project Update(string id, string? name, string? rootPath)
    {
        var project = _projects.GetValueOrDefault(id)
            ?? throw new KeyNotFoundException($"Проект не найден: {id}");

        if (name is not null) project.Name = name;
        if (rootPath is not null)
        {
            if (!Directory.Exists(rootPath))
                throw new DirectoryNotFoundException($"Папка не найдена: {rootPath}");
            project.RootPath = rootPath;
        }
        project.UpdatedAt = DateTime.UtcNow;
        Save();
        return project;
    }

    public bool Delete(string id)
    {
        var removed = _projects.TryRemove(id, out _);
        if (removed) Save();
        return removed;
    }

    private void Load()
    {
        if (!File.Exists(_storePath)) return;
        try
        {
            var json = File.ReadAllText(_storePath);
            var list = JsonSerializer.Deserialize<List<Project>>(json);
            if (list is null) return;
            foreach (var p in list)
                _projects[p.Id] = p;
        }
        catch { /* первый запуск или повреждённый файл */ }

        // Миграция: проекты без OwnerId → первый пользователь
        var firstUser = _users.GetFirst();
        if (firstUser is not null)
        {
            var needsSave = false;
            foreach (var p in _projects.Values.Where(p => p.OwnerId is null))
            {
                p.OwnerId = firstUser.Id;
                needsSave = true;
            }
            if (needsSave) Save();
        }
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_storePath)!);
        File.WriteAllText(_storePath, JsonSerializer.Serialize(_projects.Values.ToList()));
    }
}
