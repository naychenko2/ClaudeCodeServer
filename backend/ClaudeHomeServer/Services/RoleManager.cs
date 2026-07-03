using System.Collections.Concurrent;
using System.Text.Json;
using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services;

// Реестр ролей-собеседников. Роли глобальные (общий пул — «команда»), к проектам
// прикомандировываются через Role.ProjectIds. Персистентность — data/roles.json
// (по образцу ProjectManager/SessionManager: ConcurrentDictionary + Save под локом).
public class RoleManager
{
    private readonly ConcurrentDictionary<string, Role> _roles = new();
    private readonly string _storePath;
    private readonly Lock _saveLock = new();

    // Роли, у которых при загрузке мигрировали старый per-project формат:
    // roleId → прежний единственный ProjectId (нужно RoleMemoryService для переноса памяти)
    private readonly Dictionary<string, string> _migratedLegacyProjects = new();
    public IReadOnlyDictionary<string, string> MigratedLegacyProjects => _migratedLegacyProjects;

    public RoleManager(IConfiguration config)
    {
        var dataDir = Path.GetDirectoryName(
            config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json"))
            ?? Path.Combine(AppContext.BaseDirectory, "data");
        _storePath = Path.Combine(dataDir, "roles.json");
        Load();
    }

    public IReadOnlyCollection<Role> GetAll() =>
        _roles.Values.OrderBy(r => r.CreatedAt).ToList();

    public IReadOnlyCollection<Role> GetByProject(string projectId) =>
        _roles.Values
            .Where(r => r.ProjectIds.Contains(projectId))
            .OrderBy(r => r.CreatedAt)
            .ToList();

    public Role? GetById(string id) => _roles.GetValueOrDefault(id);

    // projectId != null — найм из проекта (роль сразу прикомандировывается);
    // null — глобальный найм из вкладки «Команда» (роль только в пуле).
    public Role Create(string? projectId, string name, string title, string avatar, string color,
        string persona, List<string>? agentNames, string? systemPrompt, string? model, string? effort,
        List<string>? suggestions = null)
    {
        var role = new Role
        {
            ProjectIds = projectId is null ? [] : [projectId],
            Name = name.Trim(),
            Title = title.Trim(),
            Avatar = avatar.Trim(),
            Color = color.Trim(),
            Persona = persona,
            AgentNames = agentNames ?? [],
            SystemPrompt = string.IsNullOrWhiteSpace(systemPrompt) ? null : systemPrompt,
            Model = string.IsNullOrWhiteSpace(model) ? null : model.Trim(),
            Effort = string.IsNullOrWhiteSpace(effort) ? null : effort.Trim(),
            Suggestions = suggestions ?? [],
        };
        _roles[role.Id] = role;
        Save();
        return role;
    }

    public Role? Update(string id, string? name, string? title, string? avatar, string? color,
        string? persona, List<string>? agentNames, string? systemPrompt, string? model, string? effort,
        List<string>? suggestions = null)
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
        if (suggestions is not null) role.Suggestions = suggestions;
        role.UpdatedAt = DateTime.UtcNow;
        Save();
        return role;
    }

    // Прикомандировать роль к проекту («пригласить из пула»). false — роль не найдена.
    public bool Assign(string roleId, string projectId)
    {
        if (!_roles.TryGetValue(roleId, out var role)) return false;
        if (!role.ProjectIds.Contains(projectId))
        {
            role.ProjectIds.Add(projectId);
            role.UpdatedAt = DateTime.UtcNow;
            Save();
        }
        return true;
    }

    // Открепить роль от проекта (удаление из «Команды» проекта). Память роли о проекте
    // при этом НЕ удаляется — при повторном найме роль «вспомнит» проект.
    public bool Unassign(string roleId, string projectId)
    {
        if (!_roles.TryGetValue(roleId, out var role)) return false;
        if (role.ProjectIds.Remove(projectId))
        {
            role.UpdatedAt = DateTime.UtcNow;
            Save();
        }
        return true;
    }

    // Полное удаление роли из пула (память чистит вызывающая сторона — RoleMemoryService)
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
            var list = JsonSerializer.Deserialize<List<StoredRole>>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (list is null) return;

            var migrated = false;
            foreach (var r in list)
            {
                // Миграция старого per-project формата: ProjectId (строка) → ProjectIds (список)
                if (r.ProjectIds.Count == 0 && !string.IsNullOrEmpty(r.ProjectId))
                {
                    r.ProjectIds.Add(r.ProjectId);
                    _migratedLegacyProjects[r.Id] = r.ProjectId;
                    migrated = true;
                }
                _roles[r.Id] = r.ToRole();
            }
            if (migrated) Save();   // сразу пересохраняем в новом формате
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

    // DTO загрузки: принимает и новый формат (ProjectIds), и легаси-поле ProjectId
    private sealed class StoredRole : Role
    {
        public string? ProjectId { get; set; }

        // Копия как чистый Role — чтобы легаси-поле гарантированно не утекло в сохранение
        public Role ToRole() => new()
        {
            Id = Id,
            ProjectIds = ProjectIds,
            Name = Name,
            Title = Title,
            Avatar = Avatar,
            Color = Color,
            Persona = Persona,
            AgentNames = AgentNames,
            SystemPrompt = SystemPrompt,
            Model = Model,
            Effort = Effort,
            Suggestions = Suggestions,
            CreatedAt = CreatedAt,
            UpdatedAt = UpdatedAt,
        };
    }
}
