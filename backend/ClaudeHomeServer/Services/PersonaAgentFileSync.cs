using System.Collections.Concurrent;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Llm;

namespace ClaudeHomeServer.Services;

// Синхронизация персон в файловые сабагенты Claude Code.
//
// Проектные персоны: {project.RootPath}/.claude/agents/{handle}.md
//   — agent({agentType: "handle"}) в Workflow видит их нативно (CLI сканирует cwd).
//
// Глобальные персоны: пишутся во ВСЕ проекты владельца + резерв persona-agents.
//   — видны в любом проекте пользователя.
//
// Для чатов вне проекта (личные чаты, без project RootPath):
//   — файлы пишутся в persona-agents/{ownerId}/... и подключаются через --add-dir
//   — там работает Task(), agent() не резолвится — но это ок.
//
// Стор персон — единственный источник истины (one-way): reconcile перезаписывает отличия
// и удаляет посторонние *.md. События PersonaManager дают мгновенную реакцию.
public sealed class PersonaAgentFileSync
{
    private static readonly TimeSpan SyncTtl = TimeSpan.FromMinutes(5);

    // Имена встроенных агентов CLI: персона с таким handle затёрла бы их — пропускаем
    public static readonly string[] ReservedAgentNames =
        ["general-purpose", "explore", "plan", "statusline-setup", "output-style-setup", "claude"];

    public const string SharedDirKey = "shared";

    private readonly int _filesMax;
    private readonly PersonaManager _personas;
    private readonly ProjectManager _projects;
    private readonly PersonaBindingsService _bindings;
    private readonly PersonaAgentFileGenerator _generator;
    private readonly LlmProviderRegistry _providers;
    private readonly UserStore _users;
    private readonly AppSettingsService _appSettings;
    private readonly ILogger<PersonaAgentFileSync> _log;
    private readonly string _baseDir;
    private readonly ConcurrentDictionary<string, DateTime> _lastSync = new();

    public PersonaAgentFileSync(IConfiguration config, PersonaManager personas,
        ProjectManager projects, LlmProviderRegistry providers, PersonaBindingsService bindings,
        PersonaAgentFileGenerator generator, UserStore users, AppSettingsService appSettings,
        ILogger<PersonaAgentFileSync> log)
    {
        _personas = personas;
        _projects = projects;
        _providers = providers;
        _bindings = bindings;
        _generator = generator;
        _users = users;
        _appSettings = appSettings;
        _log = log;
        _filesMax = int.TryParse(config["Persona:AgentFilesMax"], out var max) && max > 0 ? max : 50;
        var dataDir = Path.GetDirectoryName(
            config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json"))
            ?? Path.Combine(AppContext.BaseDirectory, "data");
        _baseDir = config["PersonaAgentsPath"] ?? Path.Combine(dataDir, "persona-agents");

        personas.OnPersonaCreated += p => Safe(() => SyncPersona(p), "create", p);
        personas.OnPersonaChanged += p => Safe(() => SyncPersona(p), "update", p);
        personas.OnPersonaDeleted += p => Safe(() => RemovePersona(p), "delete", p);
    }

    // Папки для --add-dir хода: только для чатов БЕЗ проекта (личные сессии).
    // Для проектных сессий файлы уже лежат в .claude/agents/ на cwd проекта.
    public IReadOnlyList<string> GetAddDirs(string ownerId, string? sessionModel, string? projectId)
    {
        SyncOwner(ownerId);
        if (projectId is not null) return []; // проектная сессия — cwd сам подхватит .claude/agents/

        var providerKey = _providers.ProviderKey(sessionModel);
        var dirs = new List<string> { OwnerDir(ownerId, providerKey), OwnerDir(ownerId, SharedDirKey) };
        foreach (var dir in dirs)
            Directory.CreateDirectory(Path.Combine(dir, ".claude", "agents"));
        return dirs;
    }

    public IReadOnlyList<Persona> EligiblePersonas(string ownerId) =>
        _personas.GetByOwner(ownerId)
            .Where(p => !IsReserved(p.Handle))
            .Take(_filesMax)
            .ToList();

    public void SyncPersona(Persona persona)
    {
        if (IsReserved(persona.Handle))
        {
            _log.LogWarning("Персона @{Handle}: handle совпадает со встроенным агентом CLI — файл не создаётся", persona.Handle);
            return;
        }

        var content = Generate(persona);
        var paths = ResolvePaths(persona).ToList();

        foreach (var path in paths)
            WriteIfChanged(path, content);

        // Чистим старые места (там, где файл был, но больше не должен быть)
        CleanStale(persona, paths);
    }

    public void RemovePersona(Persona persona)
    {
        foreach (var path in ResolvePaths(persona))
            TryDelete(path);
    }

    // Полный reconcile владельца. Троттлинг 5 мин.
    public void SyncOwner(string ownerId, bool force = false)
    {
        var last = _lastSync.GetOrAdd(ownerId, DateTime.MinValue);
        if (!force && (DateTime.UtcNow - last < SyncTtl || !_lastSync.TryUpdate(ownerId, DateTime.UtcNow, last)))
            return;
        if (force) _lastSync[ownerId] = DateTime.UtcNow;

        try
        {
            var expected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var persona in EligiblePersonas(ownerId))
                foreach (var path in ResolvePaths(persona))
                    expected[path] = Generate(persona);

            foreach (var (path, content) in expected)
                WriteIfChanged(path, content);

            // Чистка persona-agents: удаляем лишнее
            foreach (var dirKey in new[] { SharedDirKey }.Concat(_providers.All.Select(p => p.Key)))
            {
                var dir = Path.Combine(_baseDir, ownerId, dirKey, ".claude", "agents");
                if (!Directory.Exists(dir)) continue;
                foreach (var file in Directory.EnumerateFiles(dir, "*.md"))
                    if (!expected.ContainsKey(file))
                        TryDelete(file);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Reconcile файловых сабагентов владельца {Owner} не удался", ownerId);
        }
    }

    // --- внутреннее ---

    private IEnumerable<string> ResolvePaths(Persona persona)
    {
        var ownerId = persona.OwnerId ?? "";

        // Проектная персона → только её проект
        if (persona.Scope == PersonaScope.Project && persona.ProjectId is not null)
        {
            var project = _projects.GetById(persona.ProjectId);
            if (project?.RootPath is not null)
                yield return Path.Combine(project.RootPath, ".claude", "agents", persona.Handle + ".md");
            yield break;
        }

        // Глобальная персона → все проекты владельца
        foreach (var p in _projects.GetByOwner(ownerId))
            if (p.RootPath is not null)
                yield return Path.Combine(p.RootPath, ".claude", "agents", persona.Handle + ".md");

        // Чат вне проекта: {DefaultProjectsPath}/{username}/Chats/.claude/agents/{handle}.md
        // CLI использует эту папку как cwd для чатов вне проекта, поэтому agent() находит их.
        if (ChatRoot(ownerId) is { } chatRoot)
            yield return Path.Combine(chatRoot, ".claude", "agents", persona.Handle + ".md");

        // Резерв: persona-agents для сессий без проекта и нестандартных cwd (--add-dir)
        var baseDirKey = string.IsNullOrWhiteSpace(persona.Model)
            ? SharedDirKey : _providers.ProviderKey(persona.Model);
        yield return AgentFilePath(ownerId, baseDirKey, persona.Handle);
    }

    // {DefaultProjectsPath}/{username}/Chats — cwd для чатов без проекта
    private string? ChatRoot(string ownerId)
    {
        try
        {
            var basePath = _appSettings.Get().DefaultProjectsPath;
            if (string.IsNullOrWhiteSpace(basePath)) return null;
            var username = _users.GetById(ownerId)?.Username;
            if (string.IsNullOrWhiteSpace(username)) return null;
            return Path.Combine(basePath, username, "Chats");
        }
        catch { return null; }
    }

    // Удаляет файлы из проектов, где персоны уже быть не должно
    private void CleanStale(Persona persona, IEnumerable<string> keep)
    {
        var keepSet = new HashSet<string>(keep, StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(persona.OwnerId)) return;

        // Чистим persona-agents в других dirKey
        foreach (var dirKey in new[] { SharedDirKey }.Concat(_providers.All.Select(p => p.Key)))
        {
            var path = AgentFilePath(persona.OwnerId, dirKey, persona.Handle);
            if (!keepSet.Contains(path)) TryDelete(path);
        }

        // Если глобальная — чистим проекты, где её не должно быть (= вернули projectId
        // или сменили scope с project на global и надо убрать из чужих проектов)
        foreach (var p in _projects.GetByOwner(persona.OwnerId))
        {
            if (p.Id == persona.ProjectId) continue;
            var path = Path.Combine(p.RootPath ?? "", ".claude", "agents", persona.Handle + ".md");
            if (!keepSet.Contains(path)) TryDelete(path);
        }
    }

    private string AgentFilePath(string ownerId, string dirKey, string handle) =>
        Path.Combine(_baseDir, ownerId, dirKey, ".claude", "agents", handle + ".md");

    private string OwnerDir(string ownerId, string dirKey) =>
        Path.Combine(_baseDir, ownerId, dirKey);

    private string Generate(Persona persona) =>
        _generator.Generate(persona, _bindings.EffectiveToolEnabled(persona.OwnerId ?? "", persona, "web"));

    public static bool IsReserved(string handle) =>
        ReservedAgentNames.Contains(handle, StringComparer.OrdinalIgnoreCase);

    private static void WriteIfChanged(string path, string content)
    {
        try
        {
            if (File.Exists(path) && File.ReadAllText(path) == content) return;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }
        catch { /* не роняем */ }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* не роняем */ }
    }

    private void Safe(Action action, string op, Persona persona)
    {
        try { action(); }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Синк файла сабагента ({Op}) @{Handle} не удался", op, persona.Handle);
        }
    }
}
