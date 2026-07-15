using System.Collections.Concurrent;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Llm;

namespace ClaudeHomeServer.Services;

// Синхронизация персон в файловые сабагенты Claude Code. Раскладка:
//   {dataDir}/persona-agents/{ownerId}/{providerKey|shared}/.claude/agents/{handle}.md
// «shared» — персоны без явной модели (frontmatter без model = inherit, работают у любого
// провайдера); {providerKey} — персоны с явной моделью (подключаются только в сессии того же
// провайдера). Сессия получает эти папки через --add-dir на каждый ход — CLI перечитывает
// файлы при старте процесса, правки персон применяются со следующего хода.
//
// Стор персон — единственный источник истины (one-way): reconcile перезаписывает отличия
// и удаляет посторонние *.md (ручные правки файлов не переживают синк — осознанно, это же
// закрывает переименование handle). События PersonaManager дают мгновенную реакцию,
// ленивый reconcile перед ходом (троттлинг) — страховку.
public sealed class PersonaAgentFileSync
{
    private static readonly TimeSpan SyncTtl = TimeSpan.FromMinutes(5);

    // Имена встроенных агентов CLI: персона с таким handle затёрла бы их для всей сессии —
    // пропускаем с warn (персона остаётся доступной через persona_ask)
    public static readonly string[] ReservedAgentNames =
        ["general-purpose", "explore", "plan", "statusline-setup", "output-style-setup", "claude"];

    // Ключ подпапки персон без явной модели
    public const string SharedDirKey = "shared";

    private readonly string _baseDir;
    private readonly int _filesMax;
    private readonly PersonaManager _personas;
    private readonly LlmProviderRegistry _providers;
    private readonly PersonaBindingsService _bindings;
    private readonly PersonaAgentFileGenerator _generator;
    private readonly ILogger<PersonaAgentFileSync> _log;
    private readonly ConcurrentDictionary<string, DateTime> _lastSync = new();

    public PersonaAgentFileSync(IConfiguration config, PersonaManager personas,
        LlmProviderRegistry providers, PersonaBindingsService bindings,
        PersonaAgentFileGenerator generator, ILogger<PersonaAgentFileSync> log)
    {
        _personas = personas;
        _providers = providers;
        _bindings = bindings;
        _generator = generator;
        _log = log;
        // Каталог — от DataPath, как у всех сторов (фолбэк BaseDirectory/data эфемерен в контейнере)
        var dataDir = Path.GetDirectoryName(
            config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json"))
            ?? Path.Combine(AppContext.BaseDirectory, "data");
        _baseDir = config["PersonaAgentsPath"] ?? Path.Combine(dataDir, "persona-agents");
        _filesMax = int.TryParse(config["Persona:AgentFilesMax"], out var max) && max > 0 ? max : 50;

        // Мгновенная реакция на изменения; полный reconcile страхует пропуски
        personas.OnPersonaCreated += p => Safe(() => SyncPersona(p), "create", p);
        personas.OnPersonaChanged += p => Safe(() => SyncPersona(p), "update", p);
        personas.OnPersonaDeleted += p => Safe(() => RemovePersona(p), "delete", p);
    }

    // Папки для --add-dir хода: провайдер сессии + shared, и при проектной сессии — те же
    // пары с суффиксом проекта (проектные персоны видны только в СВОЁМ проекте, зеркало
    // семантики GetForContext). Лениво реконсилит файлы владельца.
    public IReadOnlyList<string> GetAddDirs(string ownerId, string? sessionModel, string? projectId)
    {
        SyncOwner(ownerId);
        var providerKey = _providers.ProviderKey(sessionModel);
        var keys = new List<string> { providerKey, SharedDirKey };
        if (projectId is not null)
            keys.AddRange([ProjectDirKey(providerKey, projectId), ProjectDirKey(SharedDirKey, projectId)]);
        var dirs = keys.Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(k => OwnerDir(ownerId, k)).ToList();
        foreach (var dir in dirs)
            Directory.CreateDirectory(Path.Combine(dir, ".claude", "agents"));
        return dirs;
    }

    // Персоны владельца, для которых существуют файлы сабагентов: без зарезервированных
    // handle, свежайшие по UpdatedAt в пределах капа (страховка от разрастания листинга Task)
    public IReadOnlyList<Persona> EligiblePersonas(string ownerId) =>
        _personas.GetByOwner(ownerId)
            .Where(p => !IsReserved(p.Handle))
            .Take(_filesMax)
            .ToList();

    // Файл персоны: генерация в целевую подпапку + удаление одноимённых из прочих
    // (персона могла сменить модель/провайдера)
    public void SyncPersona(Persona persona)
    {
        if (IsReserved(persona.Handle))
        {
            _log.LogWarning("Персона @{Handle}: handle совпадает со встроенным агентом CLI — " +
                            "файл сабагента не создаётся (доступна через persona_ask)", persona.Handle);
            return;
        }
        var targetKey = DirKeyFor(persona);
        WriteIfChanged(AgentFilePath(persona.OwnerId, targetKey, persona.Handle), Generate(persona));
        RemoveFromOtherDirs(persona.OwnerId, persona.Handle, keepKey: targetKey);
    }

    public void RemovePersona(Persona persona) =>
        RemoveFromOtherDirs(persona.OwnerId, persona.Handle, keepKey: null);

    // Полный reconcile владельца: ожидаемый набор из стора, запись отличий, удаление лишнего.
    // Троттлинг 5 мин per-owner (паттерн LlmProviderRegistry._lastSync).
    public void SyncOwner(string ownerId, bool force = false)
    {
        var last = _lastSync.GetOrAdd(ownerId, DateTime.MinValue);
        if (!force && (DateTime.UtcNow - last < SyncTtl || !_lastSync.TryUpdate(ownerId, DateTime.UtcNow, last)))
            return;
        if (force) _lastSync[ownerId] = DateTime.UtcNow;

        try
        {
            // Ожидаемое: (подпапка, файл) → контент
            var expected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var persona in EligiblePersonas(ownerId))
                expected[AgentFilePath(ownerId, DirKeyFor(persona), persona.Handle)] = Generate(persona);

            foreach (var (path, content) in expected)
                WriteIfChanged(path, content);

            // Папка эксклюзивно серверная: всё, чего нет в ожидаемом наборе, удаляется
            var ownerRoot = Path.Combine(_baseDir, ownerId);
            if (Directory.Exists(ownerRoot))
                foreach (var file in Directory.EnumerateFiles(ownerRoot, "*.md", SearchOption.AllDirectories))
                    if (!expected.ContainsKey(file))
                        TryDelete(file);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Reconcile файловых сабагентов владельца {Owner} не удался", ownerId);
        }
    }

    // --- внутреннее ---

    private string Generate(Persona persona) =>
        _generator.Generate(persona, _bindings.EffectiveToolEnabled(persona.OwnerId, persona, "web"));

    // Ключ подпапки: провайдер явной модели (или shared без неё) + суффикс проекта
    // у проектных персон — файл виден только сессиям её проекта
    private string DirKeyFor(Persona persona)
    {
        var baseKey = string.IsNullOrWhiteSpace(persona.Model)
            ? SharedDirKey : _providers.ProviderKey(persona.Model);
        return persona is { Scope: PersonaScope.Project, ProjectId: not null }
            ? ProjectDirKey(baseKey, persona.ProjectId) : baseKey;
    }

    private static string ProjectDirKey(string baseKey, string projectId) => $"{baseKey}@{projectId}";

    public static bool IsReserved(string handle) =>
        ReservedAgentNames.Contains(handle, StringComparer.OrdinalIgnoreCase);

    private string OwnerDir(string ownerId, string dirKey) => Path.Combine(_baseDir, ownerId, dirKey);

    private string AgentFilePath(string ownerId, string dirKey, string handle) =>
        Path.Combine(OwnerDir(ownerId, dirKey), ".claude", "agents", handle + ".md");

    private void WriteIfChanged(string path, string content)
    {
        try
        {
            if (File.Exists(path) && File.ReadAllText(path) == content) return;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Не удалось записать файл сабагента {Path}", path);
        }
    }

    private void RemoveFromOtherDirs(string ownerId, string handle, string? keepKey)
    {
        var ownerRoot = Path.Combine(_baseDir, ownerId);
        if (!Directory.Exists(ownerRoot)) return;
        foreach (var dir in Directory.EnumerateDirectories(ownerRoot))
        {
            var dirKey = Path.GetFileName(dir);
            if (keepKey is not null && string.Equals(dirKey, keepKey, StringComparison.OrdinalIgnoreCase))
                continue;
            TryDelete(Path.Combine(dir, ".claude", "agents", handle + ".md"));
        }
    }

    private void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { _log.LogWarning(ex, "Не удалось удалить файл сабагента {Path}", path); }
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
