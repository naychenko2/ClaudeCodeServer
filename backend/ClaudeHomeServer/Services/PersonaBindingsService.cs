using System.Text;
using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services;

// Привязки персон к источникам знаний и правилам (фича persona-bindings).
// На каждый ход персонной сессии строит блок для системного промпта:
// индекс «[тип] Когда: {условие} → {способ подгрузки}» + выжимки привязок режима
// «всегда» (Always). Плюс единая точка истины по Tool-рубильникам персоны
// (EffectiveToolEnabled: binding приоритетнее Persona.Tools) и валидация привязок.
public class PersonaBindingsService
{
    // Строк индекса в блоке — не больше (защита от раздувания промпта)
    public const int IndexLimit = 12;
    // Always-выжимок на ход — не больше
    public const int AlwaysLimit = 3;

    // Каталог ключей Tool-привязок: возможности персоны + секции workspace.
    // «personas» — валидная цель правила, но рубильником MCP персон не является
    // (persona.Tools его никогда не содержал — гейтить нечем без ломки старых персон).
    public static readonly IReadOnlyDictionary<string, (string Label, string Hint)> ToolCatalog =
        new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase)
        {
            ["tasks"] = ("Задачи", "Система задач (mcp__tasks__*): списки дел, напоминания, доска"),
            ["notes"] = ("Заметки", "База знаний заметок (mcp__notes__*): markdown-vault со связями"),
            ["web"] = ("Веб-поиск", "Встроенные WebSearch/WebFetch — поиск и чтение страниц в интернете"),
            ["personas"] = ("Персоны", "Раздел персон (mcp__personas__*): CRUD и @упоминания"),
            ["projects"] = ("Проекты", "Секция projects workspace: список и карточки проектов владельца"),
            ["chats"] = ("Чаты", "Секция chats workspace: история и отправка сообщений в другие чаты"),
            ["files"] = ("Файлы проектов", "Секция files workspace: чтение/правка файлов любого проекта"),
            ["knowledge"] = ("Базы знаний", "Секция knowledge workspace: семантический поиск по базам проектов"),
            ["destructive"] = ("Удаление (опасно)", "Секция destructive workspace: безвозвратное удаление файлов проектов и чатов (files_delete/chats_delete) — только по явной просьбе пользователя"),
        };

    private readonly PersonaManager _personas;
    private readonly ProjectManager _projects;
    private readonly WorkspaceKnowledgeStore _wkStore;
    private readonly NotesService _notes;
    private readonly NotesKnowledgeService _notesKb;
    private readonly KnowledgeService _knowledge;
    private readonly SkillsService _skills;
    private readonly FeatureFlagService _flags;
    private readonly IConfiguration _config;
    private readonly ILogger<PersonaBindingsService> _log;

    public PersonaBindingsService(PersonaManager personas, ProjectManager projects,
        WorkspaceKnowledgeStore wkStore, NotesService notes, NotesKnowledgeService notesKb,
        KnowledgeService knowledge, SkillsService skills, FeatureFlagService flags,
        IConfiguration config, ILogger<PersonaBindingsService> log)
    {
        _personas = personas;
        _projects = projects;
        _wkStore = wkStore;
        _notes = notes;
        _notesKb = notesKb;
        _knowledge = knowledge;
        _skills = skills;
        _flags = flags;
        _config = config;
        _log = log;
    }

    // --- Tool-рубильники ---

    // Эффективная возможность персоны: Tool-привязка (при включённом флаге) приоритетнее
    // старого Persona.Tools — Mode != Off включает, Off выключает; без привязки — прежняя
    // семантика (null-список = без ограничений). Выключенный флаг = чистый откат к Tools.
    public bool EffectiveToolEnabled(string? ownerId, Persona? persona, string key)
    {
        if (persona is null) return true;
        if (ownerId is not null && _flags.IsEnabled(ownerId, FeatureFlagKeys.PersonaBindings))
        {
            var binding = persona.Bindings?.LastOrDefault(b => b.Type == PersonaBindingType.Tool
                && string.Equals(b.Target, key, StringComparison.OrdinalIgnoreCase));
            if (binding is not null) return binding.Mode != PersonaBindingMode.Off;
        }
        return persona.Tools is null || persona.Tools.Contains(key, StringComparer.OrdinalIgnoreCase);
    }

    // --- Сужение зоны workspace ---

    // Проекты из Project/ProjectPath-привязок (Mode != Off) — для сужения AllowedProjectIds
    // workspace-сервера. null — флаг выключен или таких привязок нет: поведение как раньше
    // (все проекты владельца), сужаем ТОЛЬКО при наличии привязок.
    public IReadOnlyList<string>? BuildFileScopes(string ownerId, Persona? persona)
    {
        if (persona?.Bindings is null || !_flags.IsEnabled(ownerId, FeatureFlagKeys.PersonaBindings))
            return null;
        var ids = persona.Bindings
            .Where(b => b.Mode != PersonaBindingMode.Off
                && b.Type is PersonaBindingType.Project or PersonaBindingType.ProjectPath
                && !string.IsNullOrWhiteSpace(b.Target))
            .Select(b => b.Target)
            .Distinct()
            .ToList();
        return ids.Count == 0 ? null : ids;
    }

    // Датасеты из Knowledge-привязок (Mode != Off) — симметричный хелпер для сужения знаний
    public IReadOnlyList<string>? BuildKnowledgeDatasetIds(string ownerId, Persona? persona)
    {
        if (persona?.Bindings is null || !_flags.IsEnabled(ownerId, FeatureFlagKeys.PersonaBindings))
            return null;
        var ids = persona.Bindings
            .Where(b => b.Mode != PersonaBindingMode.Off
                && b.Type == PersonaBindingType.Knowledge
                && !string.IsNullOrWhiteSpace(b.Target))
            .Select(b => b.Target)
            .Distinct()
            .ToList();
        return ids.Count == 0 ? null : ids;
    }

    // --- Каталог целей ---

    // Известные датасеты владельца: базы знаний его проектов + датасет заметок «{username}:notes».
    // ProjectId != null — датасет проекта (для подсказки «способа» через knowledge_search).
    public IReadOnlyList<(string Id, string Label, string? ProjectId)> KnownDatasets(string ownerId)
    {
        var list = new List<(string, string, string?)>();
        foreach (var p in _projects.GetByOwner(ownerId))
            if (!string.IsNullOrWhiteSpace(p.RootPath)
                && _wkStore.GetByPath(p.RootPath)?.DifyDatasetId is { Length: > 0 } ds)
                list.Add((ds, p.Name, p.Id));
        if (_notesKb.GetDatasetId(ownerId) is { Length: > 0 } notesDs)
            list.Add((notesDs, "Заметки", null));
        return list;
    }

    // --- Блок хода ---

    // Блок «Привязанные знания и правила» для системного промпта хода: индекс активных
    // привязок + выжимки Always-источников по тексту хода. null — флаг выключен,
    // персоны/привязок нет или в индекс ничего не попало. mountedSections — секции
    // workspace, реально смонтированные сессии (типы без своей секции опускаются).
    public async Task<string?> BuildTurnBlockAsync(string ownerId, string personaId, string turnText,
        IReadOnlyList<string> mountedSections)
    {
        if (!_flags.IsEnabled(ownerId, FeatureFlagKeys.PersonaBindings)) return null;
        var persona = _personas.Get(personaId, ownerId);
        if (persona?.Bindings is not { Count: > 0 } bindings) return null;
        var active = bindings.Where(b => b.Mode != PersonaBindingMode.Off).ToList();
        if (active.Count == 0) return null;

        var index = BuildIndex(ownerId, active, mountedSections);
        if (index is null) return null;

        var extracts = await BuildAlwaysExtractsAsync(ownerId, active, turnText);
        return extracts is null ? index : index + "\n" + extracts;
    }

    // Индекс привязок: заголовок + инструкция + строка на привязку (лимит IndexLimit).
    // Привязка с недоступным способом (нет секции workspace, цель не найдена) опускается.
    // Internal — для юнит-тестов чистой логики сборки.
    internal string? BuildIndex(string ownerId, IReadOnlyList<PersonaBinding> active,
        IReadOnlyList<string> mountedSections)
    {
        var lines = new List<string>();
        foreach (var binding in active)
        {
            if (lines.Count >= IndexLimit) break;
            var way = DescribeWay(ownerId, binding, mountedSections);
            if (way is null) continue;
            var cond = binding.Condition?.Trim();
            lines.Add(string.IsNullOrEmpty(cond)
                ? $"- [{TypeLabel(binding.Type)}] всегда под рукой → {way}"
                : $"- [{TypeLabel(binding.Type)}] Когда: {cond} → {way}");
        }
        if (lines.Count == 0) return null;

        var sb = new StringBuilder();
        sb.AppendLine("### Привязанные знания и правила");
        sb.AppendLine("К тебе привязаны источники знаний и правила. Когда выполняется условие " +
                      "источника — сначала подгрузи его указанным способом, затем действуй.");
        foreach (var line in lines) sb.AppendLine(line);
        return sb.ToString().TrimEnd();
    }

    private static string TypeLabel(PersonaBindingType type) => type switch
    {
        PersonaBindingType.Project => "проект",
        PersonaBindingType.ProjectPath => "папка проекта",
        PersonaBindingType.Knowledge => "база знаний",
        PersonaBindingType.Notes => "заметки",
        PersonaBindingType.Tool => "инструмент",
        PersonaBindingType.Skill => "навык",
        _ => "источник",
    };

    // Способ подгрузки источника для строки индекса; null — тип опускается
    // (секция workspace не смонтирована или цель не найдена/удалена).
    private string? DescribeWay(string ownerId, PersonaBinding binding, IReadOnlyList<string> mountedSections)
    {
        switch (binding.Type)
        {
            case PersonaBindingType.Project:
            case PersonaBindingType.ProjectPath:
            {
                if (!mountedSections.Contains("files")) return null;
                var project = _projects.GetById(binding.Target);
                if (project is null || project.OwnerId != ownerId) return null;
                var path = string.IsNullOrWhiteSpace(binding.Path) ? "" : $", путь \"{binding.Path}\"";
                return $"mcp__wsp__files_tree/files_read (projectId \"{project.Id}\"{path}, проект «{project.Name}»)";
            }
            case PersonaBindingType.Knowledge:
            {
                var ds = KnownDatasets(ownerId).FirstOrDefault(d => d.Id == binding.Target);
                if (ds.Id is null) return null;
                // Датасет проекта ищется workspace-инструментом (нужна секция knowledge);
                // датасет заметок — семантическим поиском notes-сервера
                if (ds.ProjectId is not null)
                    return mountedSections.Contains("knowledge")
                        ? $"mcp__wsp__knowledge_search (projectId \"{ds.ProjectId}\", база «{ds.Label}»)"
                        : null;
                return "mcp__notes__notes_semantic_search (база знаний заметок)";
            }
            case PersonaBindingType.Notes:
            {
                var source = _notes.GetSources(ownerId).FirstOrDefault(s => s.Key == binding.Target);
                if (source is null) return null;
                var folder = string.IsNullOrWhiteSpace(binding.Path) ? "" : $", папка \"{binding.Path}\"";
                return $"mcp__notes__notes_search/notes_semantic_search (source \"{source.Key}\"{folder}, «{source.Label}»)";
            }
            case PersonaBindingType.Skill:
            {
                var skill = _skills.GetGlobalSkills()
                    .FirstOrDefault(s => string.Equals(s.Name, binding.Target, StringComparison.OrdinalIgnoreCase));
                if (skill is null) return null;
                return $"прочитай файл навыка {skill.FilePath} инструментом Read и следуй ему";
            }
            case PersonaBindingType.Tool:
            {
                var label = ToolCatalog.TryGetValue(binding.Target, out var t) ? t.Label : binding.Target;
                return $"применяй инструменты «{label}» ({binding.Target})";
            }
            default:
                return null;
        }
    }

    // Выжимки Always-привязок по тексту хода: параллельно, с общим failsafe-таймаутом
    // (паттерн BuildPersonaRecallProvider). Упавшие/не успевшие источники молча пропускаются.
    private async Task<string?> BuildAlwaysExtractsAsync(string ownerId,
        IReadOnlyList<PersonaBinding> active, string turnText)
    {
        var always = active.Where(b => b.Mode == PersonaBindingMode.Always).Take(AlwaysLimit).ToList();
        if (always.Count == 0) return null;

        var query = turnText.Trim();
        if (query.Length > 500) query = query[..500];
        var timeoutMs = int.TryParse(_config["Persona:BindingsTimeoutMs"], out var t) ? t : 3000;

        var tasks = always.Select(b => ExtractAsync(ownerId, b, query)).ToList();
        try { await Task.WhenAny(Task.WhenAll(tasks), Task.Delay(timeoutMs)); }
        catch { /* сбои отдельных источников разбираются ниже по задачам */ }

        var parts = tasks
            .Where(task => task.IsCompletedSuccessfully)
            .Select(task => task.Result)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .ToList();
        if (parts.Count == 0) return null;

        var sb = new StringBuilder();
        sb.AppendLine("Выжимки из источников режима «всегда» (авто-подбор по текущему сообщению, может быть мимо):");
        foreach (var part in parts) sb.AppendLine(part);
        return sb.ToString().TrimEnd();
    }

    // Выжимка одного Always-источника. Ошибки → warning в лог и null (ход идёт без неё).
    private async Task<string?> ExtractAsync(string ownerId, PersonaBinding binding, string query)
    {
        var maxChars = int.TryParse(_config["Persona:BindingsSnippetMaxChars"], out var m) ? m : 1500;
        try
        {
            var text = binding.Type switch
            {
                PersonaBindingType.Knowledge => await ExtractKnowledgeAsync(ownerId, binding, query),
                PersonaBindingType.Notes => await ExtractNotesAsync(ownerId, binding, query),
                PersonaBindingType.ProjectPath => ExtractProjectFile(ownerId, binding),
                PersonaBindingType.Skill => _skills.GetSkillContent(binding.Target),
                _ => null, // Project/Tool — только строка индекса, выжимки нет
            };
            if (string.IsNullOrWhiteSpace(text)) return null;
            text = text.Trim();
            if (text.Length > maxChars) text = text[..maxChars] + "…";
            return $"#### {ResolveTargetLabel(ownerId, binding)}\n{text}";
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Выжимка привязки {Type}:{Target}", binding.Type, binding.Target);
            return null;
        }
    }

    private async Task<string?> ExtractKnowledgeAsync(string ownerId, PersonaBinding binding, string query)
    {
        if (query.Length == 0 || !_knowledge.IsConfigured) return null;
        var topK = int.TryParse(_config["Persona:BindingsRecallTopK"], out var k) ? k : 4;
        var chunks = await _knowledge.RetrieveAsync(binding.Target, query, topK);
        if (chunks.Count == 0) return null;
        return string.Join("\n", chunks.Select(c =>
        {
            var content = c.Content.Replace('\n', ' ').Trim();
            return $"— [{c.DocumentName}] {content}";
        }));
    }

    private async Task<string?> ExtractNotesAsync(string ownerId, PersonaBinding binding, string query)
    {
        if (query.Length == 0) return null;
        var topK = int.TryParse(_config["Persona:BindingsRecallTopK"], out var k) ? k : 4;
        var hits = (await _notesKb.SearchAsync(ownerId, query, Math.Max(topK, 8)))
            .Where(h => h.Source == binding.Target);
        // Пост-фильтр по папке источника: пути берём из сводок заметок
        if (!string.IsNullOrWhiteSpace(binding.Path))
        {
            var prefix = binding.Path.TrimEnd('/') + "/";
            var paths = _notes.GetSummaries(ownerId, binding.Target, null)
                .ToDictionary(s => s.Id, s => s.Path);
            hits = hits.Where(h => paths.TryGetValue(h.Id, out var p)
                && p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        }
        var top = hits.Take(topK).ToList();
        if (top.Count == 0) return null;
        return string.Join("\n", top.Select(h => $"— [[{h.Title}]] (id: {h.Id}) {h.Snippet}"));
    }

    // Сырое содержимое файла проекта (ProjectPath, указывающий на файл) с байтовым капом.
    // Путь — только через SafeJoin (защита от traversal). Папка/бинарь/нет файла → null.
    private string? ExtractProjectFile(string ownerId, PersonaBinding binding)
    {
        if (string.IsNullOrWhiteSpace(binding.Path)) return null;
        var project = _projects.GetById(binding.Target);
        if (project is null || project.OwnerId != ownerId) return null;
        var capBytes = int.TryParse(_config["Persona:BindingsFileCapBytes"], out var c) ? c : 16384;

        var full = FileService.SafeJoinPublic(project.RootPath, binding.Path);
        if (!File.Exists(full)) return null;
        using var stream = File.OpenRead(full);
        var buffer = new byte[capBytes];
        var read = stream.Read(buffer, 0, capBytes);
        if (read == 0) return null;
        var text = Encoding.UTF8.GetString(buffer, 0, read);
        // Бинарный файл в промпт не тащим
        return text.Contains('\0') ? null : text;
    }

    // Человекочитаемая подпись цели привязки: projectId → имя проекта, datasetId → имя базы,
    // source заметок → его подпись, skill/tool → имя. Не найдена — сам Target.
    public string ResolveTargetLabel(string ownerId, PersonaBinding binding)
    {
        switch (binding.Type)
        {
            case PersonaBindingType.Project:
            case PersonaBindingType.ProjectPath:
            {
                var name = _projects.GetById(binding.Target)?.Name ?? binding.Target;
                return string.IsNullOrWhiteSpace(binding.Path) ? name : $"{name}/{binding.Path}";
            }
            case PersonaBindingType.Knowledge:
                return KnownDatasets(ownerId).FirstOrDefault(d => d.Id == binding.Target).Label
                    ?? binding.Target;
            case PersonaBindingType.Notes:
            {
                var label = _notes.GetSources(ownerId)
                    .FirstOrDefault(s => s.Key == binding.Target)?.Label ?? binding.Target;
                return string.IsNullOrWhiteSpace(binding.Path) ? label : $"{label}/{binding.Path}";
            }
            case PersonaBindingType.Tool:
                return ToolCatalog.TryGetValue(binding.Target, out var t) ? t.Label : binding.Target;
            default:
                return binding.Target;
        }
    }

    // --- Валидация ---

    // Нормализация Path привязки: forward slashes, без обрамляющих слэшей. null — пусто.
    internal static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var norm = path.Trim().Replace('\\', '/').Trim('/');
        return norm.Length == 0 ? null : norm;
    }

    // Проверка привязки перед сохранением: цель существует и принадлежит владельцу,
    // Path безопасен, дубликат Type+Target+Path запрещён. existing — текущие привязки
    // персоны (привязка с тем же Id при апдейте не считается дубликатом самой себя).
    // Возвращает текст ошибки или null (ок).
    public Task<string?> ValidateAsync(string ownerId, PersonaBinding binding,
        IReadOnlyList<PersonaBinding>? existing)
    {
        return Task.FromResult(Validate(ownerId, binding, existing));
    }

    private string? Validate(string ownerId, PersonaBinding binding, IReadOnlyList<PersonaBinding>? existing)
    {
        if (string.IsNullOrWhiteSpace(binding.Target))
            return "Не указана цель привязки (target)";

        // Path: нормализован и без выхода за корень
        if (!string.IsNullOrEmpty(binding.Path))
        {
            var norm = NormalizePath(binding.Path);
            if (norm is null || norm.Split('/').Any(seg => seg is ".." or ".")
                || System.IO.Path.IsPathRooted(binding.Path))
                return "Недопустимый путь привязки (path)";
            binding.Path = norm;
        }

        switch (binding.Type)
        {
            case PersonaBindingType.Project:
            case PersonaBindingType.ProjectPath:
            {
                var project = _projects.GetById(binding.Target);
                if (project is null || project.OwnerId != ownerId)
                    return "Проект не найден или недоступен";
                if (binding.Type == PersonaBindingType.ProjectPath && string.IsNullOrEmpty(binding.Path))
                    return "Для привязки к папке проекта нужен path";
                break;
            }
            case PersonaBindingType.Knowledge:
                if (KnownDatasets(ownerId).All(d => d.Id != binding.Target))
                    return "База знаний не найдена среди датасетов владельца";
                break;
            case PersonaBindingType.Notes:
                if (_notes.GetSources(ownerId).All(s => s.Key != binding.Target))
                    return "Источник заметок не найден";
                break;
            case PersonaBindingType.Skill:
                if (_skills.GetGlobalSkills()
                        .All(s => !string.Equals(s.Name, binding.Target, StringComparison.OrdinalIgnoreCase)))
                    return "Скилл не найден среди глобальных";
                break;
            case PersonaBindingType.Tool:
                if (!ToolCatalog.ContainsKey(binding.Target))
                    return $"Неизвестный ключ инструмента: {binding.Target} " +
                           $"(допустимы: {string.Join(", ", ToolCatalog.Keys)})";
                break;
            default:
                return "Неизвестный тип привязки";
        }

        var duplicate = existing?.FirstOrDefault(b => b.Id != binding.Id
            && b.Type == binding.Type
            && string.Equals(b.Target, binding.Target, StringComparison.OrdinalIgnoreCase)
            && string.Equals(NormalizePath(b.Path), binding.Path, StringComparison.OrdinalIgnoreCase));
        if (duplicate is not null)
            return "Такая привязка уже есть (дубликат type+target+path)";

        return null;
    }
}
