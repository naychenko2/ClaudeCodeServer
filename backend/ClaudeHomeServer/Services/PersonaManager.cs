using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services;

// CRUD «олицетворённых агентов» (персон) с изоляцией per-owner. Хранилище — data/personas.json
// (образец: ProjectManager + JsonFileStore). Все запросы фильтруются по OwnerId.
public class PersonaManager
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly ConcurrentDictionary<string, Persona> _personas = new();
    private readonly string _storePath;
    private readonly Lock _saveLock = new();

    public PersonaManager(IConfiguration config)
    {
        _storePath = config["PersonasPath"]
            ?? Path.Combine(AppContext.BaseDirectory, "data", "personas.json");
        Load();
    }

    // Папка с ассетами персон (аватары): data/personas/
    public string AssetsDir => Path.Combine(Path.GetDirectoryName(_storePath)!, "personas");

    public IReadOnlyCollection<Persona> GetByOwner(string userId) =>
        _personas.Values.Where(p => p.OwnerId == userId)
            .OrderByDescending(p => p.UpdatedAt).ToList();

    // Персоны, доступные в контексте: глобальные + привязанные к конкретному проекту
    public IReadOnlyCollection<Persona> GetForContext(string userId, string? projectId) =>
        _personas.Values.Where(p => p.OwnerId == userId
                && (p.Scope == PersonaScope.Global
                    || (p.Scope == PersonaScope.Project && p.ProjectId == projectId)))
            .OrderByDescending(p => p.UpdatedAt).ToList();

    // Персона по id с проверкой владельца (null — нет или чужая)
    public Persona? Get(string id, string userId) =>
        _personas.TryGetValue(id, out var p) && p.OwnerId == userId ? p : null;

    // Персона по id без проверки владельца — ТОЛЬКО для внутренних сервисов (авто-память),
    // где владелец берётся из самой персоны. Не использовать в обработчиках запросов.
    public Persona? GetByIdInternal(string id) => _personas.GetValueOrDefault(id);

    public Persona Create(string userId, string name, string? description, string? systemPrompt,
        string? model, string? effort, PersonaScope scope, string? projectId,
        string? color, string? greeting, bool memoryEnabled)
    {
        var persona = new Persona
        {
            OwnerId = userId,
            Name = string.IsNullOrWhiteSpace(name) ? "Агент" : name.Trim(),
            Description = description,
            SystemPrompt = systemPrompt,
            Model = model,
            Effort = effort,
            Scope = scope,
            ProjectId = scope == PersonaScope.Project ? projectId : null,
            Greeting = greeting,
            MemoryEnabled = memoryEnabled,
            Avatar = new PersonaAvatar { Kind = PersonaAvatarKind.Initials, Color = color },
        };
        persona.Handle = MakeUniqueHandle(persona.Name, userId);
        _personas[persona.Id] = persona;
        Save();
        return persona;
    }

    public Persona Update(string id, string userId, string? name, string? description,
        string? systemPrompt, string? model, string? effort, PersonaScope? scope, string? projectId,
        string? color, string? greeting, bool? memoryEnabled)
    {
        var persona = Get(id, userId)
            ?? throw new KeyNotFoundException($"Персона не найдена: {id}");

        if (name is not null) persona.Name = string.IsNullOrWhiteSpace(name) ? persona.Name : name.Trim();
        if (description is not null) persona.Description = description;
        if (systemPrompt is not null) persona.SystemPrompt = systemPrompt;
        if (model is not null) persona.Model = model.Length == 0 ? null : model;
        if (effort is not null) persona.Effort = effort.Length == 0 ? null : effort;
        if (scope is not null)
        {
            persona.Scope = scope.Value;
            persona.ProjectId = scope.Value == PersonaScope.Project ? projectId : null;
        }
        else if (projectId is not null && persona.Scope == PersonaScope.Project)
        {
            persona.ProjectId = projectId;
        }
        if (color is not null) persona.Avatar.Color = color.Length == 0 ? null : color;
        if (greeting is not null) persona.Greeting = greeting.Length == 0 ? null : greeting;
        if (memoryEnabled is not null) persona.MemoryEnabled = memoryEnabled.Value;
        persona.UpdatedAt = DateTime.UtcNow;
        Save();
        return persona;
    }

    // Установить сгенерированный/загруженный аватар-картинку
    public Persona SetAvatarImage(string id, string userId, string imageFile)
    {
        var persona = Get(id, userId)
            ?? throw new KeyNotFoundException($"Персона не найдена: {id}");
        persona.Avatar.Kind = PersonaAvatarKind.Image;
        persona.Avatar.ImageFile = imageFile;
        persona.UpdatedAt = DateTime.UtcNow;
        Save();
        return persona;
    }

    public bool Delete(string id, string userId)
    {
        var persona = Get(id, userId);
        if (persona is null) return false;
        _personas.TryRemove(id, out _);
        // Чистим ассеты персоны (аватар)
        try
        {
            var dir = Path.Combine(AssetsDir, id);
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch { /* не критично */ }
        Save();
        return true;
    }

    // Уникальный per-owner slug из имени (латиница/цифры/дефис); коллизии — суффиксом -2, -3…
    private string MakeUniqueHandle(string name, string userId)
    {
        var baseSlug = Slugify(name);
        if (baseSlug.Length == 0) baseSlug = "agent";
        var existing = _personas.Values
            .Where(p => p.OwnerId == userId)
            .Select(p => p.Handle)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!existing.Contains(baseSlug)) return baseSlug;
        for (var i = 2; ; i++)
        {
            var candidate = $"{baseSlug}-{i}";
            if (!existing.Contains(candidate)) return candidate;
        }
    }

    private static string Slugify(string s)
    {
        var sb = new StringBuilder();
        var prevDash = false;
        foreach (var ch in s.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch) && ch < 128)
            {
                sb.Append(ch);
                prevDash = false;
            }
            else if (!prevDash && sb.Length > 0)
            {
                sb.Append('-');
                prevDash = true;
            }
        }
        return sb.ToString().Trim('-');
    }

    private void Load()
    {
        var list = JsonFileStore.Load<List<Persona>>(_storePath, JsonOpts);
        if (list is not null)
            foreach (var p in list)
            {
                p.Avatar ??= new PersonaAvatar();
                if (string.IsNullOrEmpty(p.Handle)) p.Handle = MakeUniqueHandle(p.Name, p.OwnerId);
                _personas[p.Id] = p;
            }
    }

    private void Save()
    {
        lock (_saveLock)
            JsonFileStore.Save(_storePath, _personas.Values.ToList(), JsonOpts);
    }
}
