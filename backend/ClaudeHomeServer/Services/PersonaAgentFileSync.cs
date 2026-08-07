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

    // Справочник профилей категорий делегирования: лежит рядом с .md-агентами, в самой
    // рабочей папке хода ({RootPath}/.claude, {home}/Chats/.claude). В промпт постановки
    // идёт только ссылка на него — абсолютным путём (Read принимает лишь такой).
    public const string CategoryProfilesFileName = "delegation-categories.md";

    // Путь внутри рабочей папки, разделитель '/' (его понимают обе ОС)
    public const string CategoryProfilesRelativePath = ".claude/" + CategoryProfilesFileName;

    // Шапка-маркер «файл наш». В .claude/ рабочей папки может лежать одноимённый файл
    // пользователя — молча затирать чужие данные нельзя даже в служебной папке.
    public const string CategoryProfilesMarker =
        "<!-- ClaudeCodeServer: справочник категорий делегирования, файл перезаписывается автоматически -->";

    private static readonly string CategoryProfilesContent =
        CategoryProfilesMarker + Environment.NewLine + Environment.NewLine
        + Prompts.OmoPrompts.DelegationCategoryProfiles;

    // Заголовок сгенерированного справочника: им начинались файлы, записанные до появления
    // шапки-маркера — их тоже считаем своими, иначе они застыли бы навсегда
    private static readonly string CategoryProfilesLegacyMarker =
        Prompts.OmoPrompts.DelegationCategoryProfiles.Split('\n')[0].Trim();

    private readonly int _filesMax;
    private readonly PersonaManager _personas;
    private readonly ProjectManager _projects;
    private readonly PersonaBindingsService _bindings;
    private readonly PersonaAgentFileGenerator _generator;
    private readonly LlmProviderRegistry _providers;
    private readonly UserStore _users;
    // Домашняя папка владельца ({база по среде}/{username} либо override из конфига)
    private readonly UserHomeResolver _homes;
    private readonly ILogger<PersonaAgentFileSync> _log;
    // Назначения моделей: персона без своей модели пинит тир места «сабагенты-консультанты»
    private readonly Llm.ModelAssignmentResolver? _assignments;
    private readonly string _baseDir;
    private readonly ConcurrentDictionary<string, DateTime> _lastSync = new();

    public PersonaAgentFileSync(IConfiguration config, PersonaManager personas,
        ProjectManager projects, LlmProviderRegistry providers, PersonaBindingsService bindings,
        PersonaAgentFileGenerator generator, UserStore users, AppSettingsService appSettings,
        ILogger<PersonaAgentFileSync> log, Execution.SandboxManager? sandbox = null,
        UserHomeResolver? homes = null,
        Llm.ModelAssignmentResolver? assignments = null)
    {
        _assignments = assignments;
        _homes = homes ?? UserHomeResolver.WithoutOverrides(appSettings, sandbox);
        _personas = personas;
        _projects = projects;
        _providers = providers;
        _bindings = bindings;
        _generator = generator;
        _users = users;
        _log = log;
        _filesMax = int.TryParse(config["Persona:AgentFilesMax"], out var max) && max > 0 ? max : 50;
        var dataDir = Path.GetDirectoryName(
            config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json"))
            ?? Path.Combine(AppContext.BaseDirectory, "data");
        _baseDir = config["PersonaAgentsPath"] ?? Path.Combine(dataDir, "persona-agents");

        personas.OnPersonaCreated += p => Safe(() => SyncPersona(p), "create", p);
        personas.OnPersonaChanged += p => Safe(() => SyncPersona(p), "update", p);
        personas.OnPersonaDeleted += p => Safe(() => RemovePersona(p), "delete", p);
        // Смена handle: удалить .md по СТАРОМУ handle (клон персоны с прежним handle даёт старые
        // пути в ResolvePaths); новые файлы запишет следующий за этим OnPersonaChanged.
        personas.OnPersonaHandleChanged += (p, oldHandle) =>
            Safe(() => RemovePersona(PersonaManager.WithHandle(p, oldHandle)), "rename", p);
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

            // Справочник категорий делегирования — по одному файлу на рабочую папку владельца
            // (от персон не зависит, поэтому пишется отдельно от expected)
            foreach (var root in CategoryProfileRoots(ownerId))
                WriteCategoryProfiles(CategoryProfilesPath(root));

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

    /// <summary>
    /// Абсолютный путь к справочнику категорий в рабочей папке хода — для ссылки в промпте
    /// (Read относительный путь не принимает). Файл при отсутствии пишется сразу, мимо
    /// троттлинга SyncOwner: проект, созданный минуту назад, иначе получил бы битую ссылку.
    /// null — рабочая папка не определилась либо файл занят пользователем: тогда ссылки
    /// в промпте лучше не давать вовсе.
    /// </summary>
    public string? EnsureCategoryProfiles(string ownerId, string? projectId)
    {
        try
        {
            var root = projectId is not null ? _projects.GetById(projectId)?.RootPath : ChatRoot(ownerId);
            if (root is null) return null;
            var path = CategoryProfilesPath(root);
            return WriteCategoryProfiles(path) ? path : null;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Справочник категорий для владельца {Owner} не подготовлен", ownerId);
            return null;
        }
    }

    // --- внутреннее ---

    private static string CategoryProfilesPath(string root) =>
        Path.Combine(root, ".claude", CategoryProfilesFileName);

    // Пишет справочник, если он наш (или его ещё нет). false — чужой файл (не тронут)
    // либо запись не удалась: ссылаться на такой путь нельзя.
    private bool WriteCategoryProfiles(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var existing = File.ReadAllText(path);
                if (!IsOurCategoryProfiles(existing))
                {
                    _log.LogDebug("Файл {Path} создан не нами — справочник категорий не перезаписываю", path);
                    return false;
                }
                if (existing == CategoryProfilesContent) return true;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, CategoryProfilesContent);
            return true;
        }
        catch { return false; }
    }

    private static bool IsOurCategoryProfiles(string text)
    {
        var head = text.TrimStart('﻿', ' ', '\t', '\r', '\n');
        return head.StartsWith(CategoryProfilesMarker, StringComparison.Ordinal)
            || head.StartsWith(CategoryProfilesLegacyMarker, StringComparison.Ordinal);
    }

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

        // Чат вне проекта: {домашняя папка}/Chats/.claude/agents/{handle}.md
        // CLI использует эту папку как cwd для чатов вне проекта, поэтому agent() находит их.
        if (ChatRoot(ownerId) is { } chatRoot)
            yield return Path.Combine(chatRoot, ".claude", "agents", persona.Handle + ".md");

        // Резерв: persona-agents для сессий без проекта и нестандартных cwd (--add-dir).
        // Всегда shared: файлы генерируются без пина модели (сабагент бежит на модели
        // сессии), раскладка по провайдерам потеряла смысл — CleanStale уберёт старые копии
        yield return AgentFilePath(ownerId, SharedDirKey, persona.Handle);
    }

    // Рабочие папки владельца, где ход может искать справочник категорий: проекты,
    // корень чатов вне проекта и резерв persona-agents (он же уходит в --add-dir)
    private IEnumerable<string> CategoryProfileRoots(string ownerId)
    {
        foreach (var p in _projects.GetByOwner(ownerId))
            if (p.RootPath is not null)
                yield return p.RootPath;
        if (ChatRoot(ownerId) is { } chatRoot)
            yield return chatRoot;
        yield return OwnerDir(ownerId, SharedDirKey);
    }

    // Cwd для чатов без проекта: {домашняя папка владельца}/Chats
    // (как SessionManager.ResolveChatRoot — общий резолв в UserHomeResolver)
    private string? ChatRoot(string ownerId)
    {
        try
        {
            var home = _homes.Resolve(_users.GetById(ownerId));
            return home is null ? null : Path.Combine(home, "Chats");
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

    private string Generate(Persona persona)
    {
        var ownerId = persona.OwnerId ?? "";
        return _generator.Generate(persona, new PersonaAgentFileContext(
            _bindings.EffectiveToolEnabled(ownerId, persona, "web"),
            _bindings.EffectiveToolEnabled(ownerId, persona, "tasks"),
            _bindings.EffectiveToolEnabled(ownerId, persona, "notes"),
            _bindings.BuildSubagentIndex(ownerId, persona),
            // Персона без своей модели идёт своим уровнем, без уровня — тиром назначения
            // «сабагенты-консультанты»; резолвер может вернуть и модель стороннего
            // провайдера — ModelAliasFor отсеет её в null (пин только Claude-тиров)
            ModelAliasFor(_providers,
                _assignments?.Resolve(Llm.LocalActionCatalog.SubagentConsultant,
                    _assignments.PersonaModel(persona, ownerId,
                        Llm.LocalActionCatalog.DefaultTierOf(Llm.LocalActionCatalog.SubagentConsultant)), ownerId)
                    ?? persona.Model)));
    }

    // Алиас-тир модели персоны для пина в frontmatter сабагента. Пинится только тир
    // Claude-модели (opus/sonnet/haiku): алиас безопасен у всех провайдеров — Claude-чат
    // резолвит его в настоящий тир, сторонние маппят env-переменными BuildCliEnv в модель
    // сессии. ID сторонних провайдеров и незнакомые Claude-ID не пинятся (null — без пина).
    internal static string? ModelAliasFor(LlmProviderRegistry providers, string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return null;
        if (!string.Equals(providers.ProviderKey(model), "claude", StringComparison.OrdinalIgnoreCase))
            return null;
        var m = model.ToLowerInvariant();
        if (m.Contains("opus")) return "opus";
        if (m.Contains("sonnet")) return "sonnet";
        if (m.Contains("haiku")) return "haiku";
        return null;
    }

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
