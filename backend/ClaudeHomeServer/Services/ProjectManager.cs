using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services;

public class ProjectManager
{
    // Встроенная часть системного промпта — всегда добавляется, пользователь не редактирует
    public const string BuiltInSystemPrompt =
        "Всегда общайся с пользователем на русском языке. Все сообщения, пояснения, вопросы и итоговые ответы пиши исключительно по-русски, независимо от языка запроса. Технические термины, идентификаторы кода, названия инструментов и команды оставляй в оригинальном виде.\n\n" +
        "Если пользователь просит сгенерировать, нарисовать, создать или изобразить что-либо визуальное (изображение, картинку, рисунок, арт, фото, иллюстрацию), видео, аудио или музыку — используй MCP-сервер fal-ai: подбери модель (recommend_model или search_models), запусти задачу (submit_job / run_model) и верни результат. Никогда не рисуй ASCII-арт вместо настоящей генерации.\n\n";

    private readonly ConcurrentDictionary<string, Project> _projects = new();
    private readonly string _storePath;
    private readonly UserStore _users;
    private readonly AppSettingsService _appSettings;
    // Песочница container-пользователей: их проекты живут только под Sandbox:ProjectsRoot.
    // null — в тестах (все пользователи считаются local)
    private readonly Execution.SandboxManager? _sandbox;
    private readonly Lock _saveLock = new();

    public ProjectManager(IConfiguration config, UserStore users, AppSettingsService appSettings,
        Execution.SandboxManager? sandbox = null)
    {
        _users = users;
        _appSettings = appSettings;
        _sandbox = sandbox;
        _storePath = config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json");
        Load();
    }

    // Container-пользователь заперт в корне песочницы: путь вне него claude не увидит
    // (в контейнер монтируется только Sandbox:ProjectsRoot), а FileService увидел бы хост —
    // расхождение недопустимо
    private void EnsureRootAllowed(string userId, string rootPath)
    {
        if (_sandbox is null) return;
        if (_users.GetById(userId)?.ExecutionEnvironment != ExecutionEnvironments.Container) return;
        var sandboxRoot = _sandbox.Options.ProjectsRoot;
        if (string.IsNullOrWhiteSpace(sandboxRoot)
            || !Path.GetFullPath(rootPath).StartsWith(
                Path.GetFullPath(sandboxRoot), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(
                "Проекты изолированного пользователя должны находиться внутри папки песочницы (Sandbox:ProjectsRoot)");
    }

    public IReadOnlyCollection<Project> GetAll() => _projects.Values.ToList();

    public IReadOnlyCollection<Project> GetByOwner(string userId) =>
        _projects.Values.Where(p => p.OwnerId == userId).ToList();

    public Project? GetById(string id) => _projects.GetValueOrDefault(id);

    public Project? GetByName(string name) =>
        _projects.Values.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    public Project Create(string name, string? rootPath, string userId, string username, bool createDirectory = false, string? groupId = null)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            // База по среде владельца: изолированные — в корне песочницы
            string? basePath;
            if (_sandbox is not null
                && _users.GetById(userId)?.ExecutionEnvironment == ExecutionEnvironments.Container)
            {
                basePath = _sandbox.Options.ProjectsRoot;
                if (string.IsNullOrWhiteSpace(basePath))
                    throw new ArgumentException(
                        "Песочница не настроена: задайте Sandbox:ProjectsRoot в appsettings.Local.json");
            }
            else
            {
                basePath = _appSettings.Get().DefaultProjectsPath;
                if (string.IsNullOrWhiteSpace(basePath))
                    throw new ArgumentException("Укажите путь к папке или задайте папку по умолчанию в настройках");
            }
            rootPath = Path.Combine(basePath, username, name);
            createDirectory = true;
        }
        EnsureRootAllowed(userId, rootPath);

        if (createDirectory)
            Directory.CreateDirectory(rootPath);
        else if (!Directory.Exists(rootPath))
            throw new DirectoryNotFoundException($"Папка не найдена: {rootPath}");

        var project = new Project
        {
            Name = name,
            RootPath = rootPath,
            OwnerId = userId,
            GroupId = string.IsNullOrEmpty(groupId) ? null : groupId,
        };
        _projects[project.Id] = project;
        Save();
        return project;
    }

    public Project Update(string id, string? name, string? rootPath, string? systemPrompt = null,
        bool? showHiddenFiles = null, List<PermissionRule>? permissionRules = null, string? groupId = null,
        bool? toolsEnabled = null)
    {
        var project = _projects.GetValueOrDefault(id)
            ?? throw new KeyNotFoundException($"Проект не найден: {id}");

        if (name is not null) project.Name = name;
        if (rootPath is not null)
        {
            if (!Directory.Exists(rootPath))
                throw new DirectoryNotFoundException($"Папка не найдена: {rootPath}");
            if (project.OwnerId is { } ownerId) EnsureRootAllowed(ownerId, rootPath);
            project.RootPath = rootPath;
        }
        if (systemPrompt is not null) project.SystemPrompt = systemPrompt;
        if (showHiddenFiles is not null) project.ShowHiddenFiles = showHiddenFiles.Value;
        if (toolsEnabled is not null) project.ToolsEnabled = toolsEnabled.Value;
        if (permissionRules is not null) project.PermissionRules = permissionRules.Count == 0 ? null : permissionRules;
        // groupId: null = не менять; "" = убрать из группы; иначе — привязать к группе
        if (groupId is not null) project.GroupId = groupId.Length == 0 ? null : groupId;
        project.UpdatedAt = DateTime.UtcNow;
        Save();
        return project;
    }

    // Кастомные колонки Kanban-доски проекта; пустой список/null → дефолтные 3
    public Project UpdateBoardColumns(string id, List<BoardColumn>? columns)
    {
        var project = _projects.GetValueOrDefault(id)
            ?? throw new KeyNotFoundException($"Проект не найден: {id}");
        project.BoardColumns = columns is null || columns.Count == 0 ? null : columns;
        project.UpdatedAt = DateTime.UtcNow;
        Save();
        return project;
    }

    // Отвязывает все проекты от удаляемой группы (вызывается при удалении группы)
    public void ClearGroup(string groupId)
    {
        var changed = false;
        foreach (var p in _projects.Values.Where(p => p.GroupId == groupId))
        {
            p.GroupId = null;
            changed = true;
        }
        if (changed) Save();
    }

    // Части эффективного системного промпта в порядке отправки:
    // builtin — встроенная константа, user — промпт проекта, auto — автодополнения (Dify, теги).
    // Единственный источник состава промпта: и реальная отправка (BuildSystemPrompt),
    // и просмотр на UI (/effective-prompt) собираются отсюда.
    public static List<SystemPromptPart> GetSystemPromptParts(string? userPrompt, bool hasDify,
        Dictionary<string, List<string>>? documentTags = null)
    {
        var parts = new List<SystemPromptPart> { new("builtin", BuiltInSystemPrompt) };

        if (!string.IsNullOrWhiteSpace(userPrompt))
            parts.Add(new("user", userPrompt));

        if (hasDify)
        {
            var combined = string.Join("\n\n", parts.Select(p => p.Content));
            if (!combined.Contains("mcp__dify__search_knowledge"))
                parts.Add(new("auto",
                    "В этом проекте настроена база знаний Dify. Используй инструмент mcp__dify__search_knowledge для поиска по ней при ответе на вопросы о документации проекта. dataset_id уже настроен — указывать его не нужно.\n\n" +
                    "Если пользователь просит найти, поискать или проверить информацию — используй MCP-сервер Dify (search_knowledge) в первую очередь, до ответа из памяти."));

            var tagInstruction = BuildTagInstruction(documentTags);
            if (!string.IsNullOrEmpty(tagInstruction))
                parts.Add(new("auto", tagInstruction));
        }

        return parts;
    }

    public static string BuildSystemPrompt(string? userPrompt, bool hasDify,
        Dictionary<string, List<string>>? documentTags = null) =>
        string.Join("\n\n", GetSystemPromptParts(userPrompt, hasDify, documentTags).Select(p => p.Content));

    private static string BuildTagInstruction(Dictionary<string, List<string>>? documentTags)
    {
        if (documentTags is null || documentTags.Count == 0) return "";

        // Инвертируем: tag → список путей
        var byTag = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, tags) in documentTags)
            foreach (var tag in tags)
            {
                if (!byTag.TryGetValue(tag, out var list))
                    byTag[tag] = list = [];
                list.Add(path);
            }

        if (byTag.Count == 0) return "";

        var sb = new StringBuilder();
        sb.AppendLine("Теги документов в базе знаний:");
        foreach (var (tag, paths) in byTag.OrderBy(x => x.Key))
            sb.AppendLine($"  тег \"{tag}\": {string.Join(", ", paths)}");
        sb.Append("Если пользователь просит искать по тегу, вызови mcp__dify__search_knowledge, " +
                  "затем оставь только результаты, где segment.document.name входит в список выше для нужного тега.");
        return sb.ToString();
    }

    public bool Delete(string id)
    {
        var removed = _projects.TryRemove(id, out _);
        if (removed) Save();
        return removed;
    }

    private void Load()
    {
        var list = JsonFileStore.Load<List<Project>>(_storePath,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (list is not null)
            foreach (var p in list)
                _projects[p.Id] = p;

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

        // Миграция: очищаем SystemPrompt от встроенных частей — теперь хранится только пользовательский текст
        var needsPromptMigration = false;
        foreach (var p in _projects.Values)
        {
            var original = p.SystemPrompt;

            // Убираем Dify-инструкцию из хранимого промпта (теперь добавляется динамически)
            if (p.SystemPrompt?.Contains("mcp__dify__search_knowledge") == true)
            {
                p.SystemPrompt = null;
            }

            // Старый дефолтный промпт теперь является встроенным — убираем из хранимого значения
            if (p.SystemPrompt == BuiltInSystemPrompt)
                p.SystemPrompt = null;

            // Встроенный fal-ai промпт мог попасть в пользовательскую часть — зачищаем
            var builtIn = BuiltInSystemPrompt.TrimEnd();
            if (p.SystemPrompt?.Contains(builtIn) == true)
            {
                if (p.SystemPrompt.Length <= builtIn.Length + 50)
                {
                    p.SystemPrompt = null;
                }
                else
                {
                    p.SystemPrompt = p.SystemPrompt
                        .Replace("\n\n" + builtIn, "")
                        .Replace(builtIn + "\n\n", "")
                        .Replace(builtIn, "")
                        .Trim();
                    if (string.IsNullOrWhiteSpace(p.SystemPrompt)) p.SystemPrompt = null;
                }
            }

            if (p.SystemPrompt != original)
                needsPromptMigration = true;
        }
        if (needsPromptMigration) Save();
    }

    private void Save()
    {
        lock (_saveLock)
        {
            JsonFileStore.Save(_storePath, _projects.Values.ToList());
        }
    }
}

// Kind: builtin | user | auto
public record SystemPromptPart(string Kind, string Content);
