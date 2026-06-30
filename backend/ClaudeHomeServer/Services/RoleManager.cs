using System.Collections.Concurrent;
using System.Text.Json;
using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services;

// Реестр ролей-собеседников проекта. Персистентность — data/roles.json
// (по образцу ProjectManager/SessionManager: ConcurrentDictionary + Save под локом).
public class RoleManager
{
    private readonly ConcurrentDictionary<string, Role> _roles = new();
    private readonly string _storePath;
    private readonly Lock _saveLock = new();

    public RoleManager(IConfiguration config)
    {
        var dataDir = Path.GetDirectoryName(
            config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json"))
            ?? Path.Combine(AppContext.BaseDirectory, "data");
        _storePath = Path.Combine(dataDir, "roles.json");
        Load();
    }

    public IReadOnlyCollection<Role> GetByProject(string projectId) =>
        _roles.Values
            .Where(r => r.ProjectId == projectId)
            .OrderBy(r => r.CreatedAt)
            .ToList();

    public Role? GetById(string id) => _roles.GetValueOrDefault(id);

    public Role Create(string projectId, string name, string title, string avatar, string color,
        string persona, List<string>? agentNames, string? systemPrompt, string? model, string? effort)
    {
        var role = new Role
        {
            ProjectId = projectId,
            Name = name.Trim(),
            Title = title.Trim(),
            Avatar = avatar.Trim(),
            Color = color.Trim(),
            Persona = persona,
            AgentNames = agentNames ?? [],
            SystemPrompt = string.IsNullOrWhiteSpace(systemPrompt) ? null : systemPrompt,
            Model = string.IsNullOrWhiteSpace(model) ? null : model.Trim(),
            Effort = string.IsNullOrWhiteSpace(effort) ? null : effort.Trim(),
        };
        _roles[role.Id] = role;
        Save();
        return role;
    }

    public Role? Update(string id, string? name, string? title, string? avatar, string? color,
        string? persona, List<string>? agentNames, string? systemPrompt, string? model, string? effort)
    {
        if (!_roles.TryGetValue(id, out var role)) return null;

        if (name is not null) role.Name = name.Trim();
        if (title is not null) role.Title = title.Trim();
        if (avatar is not null) role.Avatar = avatar.Trim();
        if (color is not null) role.Color = color.Trim();
        if (persona is not null) role.Persona = persona;
        if (agentNames is not null) role.AgentNames = agentNames;
        // Пустую строку трактуем как «очистить» опциональные текстовые/модельные поля
        if (systemPrompt is not null) role.SystemPrompt = string.IsNullOrWhiteSpace(systemPrompt) ? null : systemPrompt;
        if (model is not null) role.Model = string.IsNullOrWhiteSpace(model) ? null : model.Trim();
        if (effort is not null) role.Effort = string.IsNullOrWhiteSpace(effort) ? null : effort.Trim();
        role.UpdatedAt = DateTime.UtcNow;
        Save();
        return role;
    }

    public bool Delete(string id)
    {
        var removed = _roles.TryRemove(id, out _);
        if (removed) Save();
        return removed;
    }

    private void Load()
    {
        if (!File.Exists(_storePath)) return;
        try
        {
            var json = File.ReadAllText(_storePath);
            var list = JsonSerializer.Deserialize<List<Role>>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (list is null) return;
            foreach (var r in list)
                _roles[r.Id] = r;
        }
        catch { /* первый запуск или повреждённый файл */ }
    }

    private void Save()
    {
        lock (_saveLock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_storePath)!);
            File.WriteAllText(_storePath, JsonSerializer.Serialize(_roles.Values.ToList()));
        }
    }
}
