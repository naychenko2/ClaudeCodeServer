using System.Text.Json;
using System.Text.Json.Serialization;
using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.Tasks;

// Область шаблона: глобальный (правит админ) или проектный (правит владелец проекта, И8).
public enum WorkflowTemplateScope { Global, Project }

// Строка списка: шаблон вместе с областью (у проектного — id проекта).
public sealed record WorkflowTemplateEntry(WorkflowTemplateScope Scope, string? ProjectId, WorkflowTemplate Template);

public enum WorkflowTemplateErrorKind { Validation, Conflict, NotFound, Forbidden }

// Отказ стора: вызывающая сторона (REST B1c) раскладывает по Kind в 400/409/404/403.
public sealed class WorkflowTemplateException(
    WorkflowTemplateErrorKind kind, string message, IReadOnlyList<string>? errors = null) : Exception(message)
{
    public WorkflowTemplateErrorKind Kind { get; } = kind;
    // Для Validation — полный список ошибок WorkflowRules.Validate.
    public IReadOnlyList<string> Errors { get; } = errors ?? [];
}

// Стор шаблонов процесса фичи (разрез docs/research/ai-fabrika-workflow-cut-2026-11.md §1, §4):
// data/workflow-templates.json. Файл в data/ — в бэкап попадает по умолчанию (BackupPaths
// работает от обратного), tasks.json не трогается никогда (И4).
//
// - Каждое сохранение идёт через WorkflowRules.Validate, Version +1; старые версии не храним.
// - Встроенные пресеты живут в коде (BuiltInKey): удалить нельзя, «сбросить к коду» можно.
// - Все методы чтения отдают КОПИИ: живой объект наружу не уходит, правка копии стор не меняет.
// - Единственный экземпляр (singleton) и один лок на файл: запись = клон снимка → правка →
//   атомарный JsonFileStore.Save → подмена снимка. Сбой записи оставляет прежний снимок.
// - Битый файл JsonFileStore уносит в .corrupt-*.bak, стор поднимается на пресетах кода;
//   исключение из конструктора не выходит.
public sealed class WorkflowTemplateStore
{
    public const int FormatVersion = 1;
    public const string FullCycleKey = "full-cycle";
    public const string FastTrackKey = "fast-track";
    public const string ParallelCycleKey = "parallel-cycle";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly string _path;
    private readonly ILogger<WorkflowTemplateStore>? _log;
    private readonly object _lock = new();
    private WorkflowTemplateFile _file;
    // Файл записан более новым кодом: читаем на пресетах, не перезаписываем чужой формат.
    private readonly bool _futureFormat;

    public WorkflowTemplateStore(IConfiguration config, ILogger<WorkflowTemplateStore>? log = null)
        : this(ResolvePath(config), log, withBuiltIns: true) { }

    // Прямой конструктор для тестов: произвольный путь файла.
    public WorkflowTemplateStore(string path, ILogger<WorkflowTemplateStore>? log = null)
        : this(path, log, withBuiltIns: true) { }

    // Шов для тестов (минор ревью R-M1+M2): стор с ПУСТЫМ каталогом — пресеты кода сюда не
    // добавляются. В работе пустого каталога не бывает (EnsureBuiltIns всегда), состояние нужно
    // только чтобы проверить guard маркера M1: при пустом каталоге фичу не запираем.
    internal static WorkflowTemplateStore EmptyCatalogForTests(string path) => new(path, null, withBuiltIns: false);

    private WorkflowTemplateStore(string path, ILogger<WorkflowTemplateStore>? log, bool withBuiltIns)
    {
        _path = path;
        _log = log;
        _file = new WorkflowTemplateFile { Version = FormatVersion };
        try
        {
            var loaded = JsonFileStore.Load<WorkflowTemplateFile>(_path, JsonOpts, _log);
            if (loaded is { Version: > FormatVersion })
            {
                _futureFormat = true;
                _log?.LogWarning("{Path}: формат v{V} новее кода (v{Code}) — файл игнорируется, запись отключена",
                    _path, loaded.Version, FormatVersion);
            }
            else if (loaded is not null)
            {
                _file = Sanitize(loaded);
            }
        }
        catch (Exception ex)
        {
            _log?.LogError(ex, "{Path}: шаблоны не прочитаны, стартуем на пресетах кода", _path);
        }
        if (withBuiltIns) EnsureBuiltIns(_file);
    }

    private static string ResolvePath(IConfiguration config)
    {
        var dataPath = config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json");
        return Path.Combine(Path.GetDirectoryName(dataPath)!, "workflow-templates.json");
    }

    // Встроенные пресеты в коде. Id стабильный и производный от ключа: BasedOn проектных
    // шаблонов ссылается на него, он не должен меняться между перезапусками.
    public static WorkflowTemplate BuiltInPreset(string key) => key switch
    {
        FullCycleKey => Preset(key, "Полный цикл",
            "Все этапы конвейера: нарезка, дизайн и QA — по необходимости.",
            (TaskRoles.Cut, WorkflowStagePresence.On),
            (TaskRoles.Design, WorkflowStagePresence.Optional),
            (TaskRoles.Plan, WorkflowStagePresence.On),
            (TaskRoles.Step, WorkflowStagePresence.On),
            (TaskRoles.Review, WorkflowStagePresence.On),
            (TaskRoles.Qa, WorkflowStagePresence.Optional),
            (TaskRoles.Final, WorkflowStagePresence.On),
            (TaskRoles.Merge, WorkflowStagePresence.On)),
        FastTrackKey => Preset(key, "Быстрый трек",
            "Малая фича: без нарезки, дизайна и QA; план — по необходимости.",
            (TaskRoles.Cut, WorkflowStagePresence.Off),
            (TaskRoles.Design, WorkflowStagePresence.Off),
            (TaskRoles.Plan, WorkflowStagePresence.Optional),
            (TaskRoles.Step, WorkflowStagePresence.On),
            (TaskRoles.Review, WorkflowStagePresence.On),
            (TaskRoles.Qa, WorkflowStagePresence.Off),
            (TaskRoles.Final, WorkflowStagePresence.On),
            (TaskRoles.Merge, WorkflowStagePresence.On)),
        // Третий пресет — графовой формы (разрез 2 §1.10): две линии шага параллельно и
        // отдельная ветка QA плана. Presence — по образцу full-cycle (съёмные там же, где у
        // него); несъёмные (step/final/merge) — всегда «on», их требует WorkflowRules.
        // Имена qa-этапов (G7, a5d0c76e): qa-plan и qa делят роль (линии у qa нет) — по правилу
        // WorkflowRules у каждого обязателен Title, различный в группе; без них граф показал бы
        // две неотличимые «Проверки». Остальным этапам имени не нужно: их роли в шаблоне уникальны.
        ParallelCycleKey => GraphPreset(key, "Параллельный цикл",
            "Две линии работы: шаг по бэку и фронту параллельно, проверка плана — отдельной веткой.",
            new() { Role = TaskRoles.Cut, Presence = WorkflowStagePresence.On },
            new() { Role = TaskRoles.Design, Presence = WorkflowStagePresence.Optional },
            new() { Role = TaskRoles.Plan, Presence = WorkflowStagePresence.On },
            new()
            {
                Id = "qa-plan", Role = TaskRoles.Qa, Presence = WorkflowStagePresence.Optional,
                After = ["design"], Specialty = PersonaSpecialty.Tester, Title = "Проверка плана",
            },
            new() { Id = "step-back", Role = TaskRoles.Step, Lane = "back", After = ["plan"] },
            new() { Id = "step-front", Role = TaskRoles.Step, Lane = "front", After = ["plan"] },
            new() { Role = TaskRoles.Review, After = ["step-back", "step-front"] },
            new() { Id = "qa", Role = TaskRoles.Qa, Presence = WorkflowStagePresence.Optional, After = ["review", "qa-plan"], Title = "Проверка" },
            new() { Role = TaskRoles.Final },
            new() { Role = TaskRoles.Merge }),
        _ => throw new ArgumentException($"неизвестный встроенный пресет «{key}»", nameof(key)),
    };

    public static readonly IReadOnlyList<string> BuiltInKeys = [FullCycleKey, FastTrackKey, ParallelCycleKey];

    private static WorkflowTemplate Preset(string key, string name, string description,
        params (string Role, string Presence)[] stages) => new()
    {
        Id = $"builtin-{key}",
        Name = name,
        Description = description,
        Version = 1,
        BuiltInKey = key,
        Stages = stages.Select(s => new WorkflowStage { Role = s.Role, Presence = s.Presence }).ToList(),
    };

    // Графовой пресет: в записи этапа — только то, что отличается от значения по умолчанию
    // (id = роль — WorkflowGraph.Normalize; «после» = предыдущий в списке там же; Presence =
    // «on» — в самом WorkflowStage). Порядок списка — топологический: After ссылается только
    // на более ранние.
    private static WorkflowTemplate GraphPreset(string key, string name, string description,
        params WorkflowStage[] stages) => new()
    {
        Id = $"builtin-{key}",
        Name = name,
        Description = description,
        Version = 1,
        BuiltInKey = key,
        Stages = [.. stages],
    };

    // Глобальные шаблоны, затем (если задан projectId) проектные. Копии.
    public IReadOnlyList<WorkflowTemplateEntry> List(string? projectId = null)
    {
        lock (_lock)
        {
            var result = _file.Global
                .Select(t => new WorkflowTemplateEntry(WorkflowTemplateScope.Global, null, Clone(t)))
                .ToList();
            if (!string.IsNullOrEmpty(projectId) && _file.Projects.TryGetValue(projectId, out var own))
                result.AddRange(own.Select(t => new WorkflowTemplateEntry(WorkflowTemplateScope.Project, projectId, Clone(t))));
            return result;
        }
    }

    public WorkflowTemplate? Get(string id, WorkflowTemplateScope scope, string? projectId = null)
    {
        lock (_lock)
        {
            var found = ListFor(_file, scope, projectId, create: false)?.FirstOrDefault(t => t.Id == id);
            return found is null ? null : Clone(found);
        }
    }

    // Сохранить шаблон (создать или обновить по Id). Version у входа игнорируется: её ставит стор.
    // expectedVersion — оптимистичная проверка: версия, которую видел редактор; не совпала с
    // текущей — отказ Conflict («шаблон изменился»). null — без проверки (правка поверх
    // последнего состояния): так два параллельных сохранения дают N+1 и N+2, а не теряют правку.
    // Новый шаблон: Version = 1, встроенным стать не может. Существующий: BuiltInKey сохраняется.
    public WorkflowTemplate Save(WorkflowTemplate template, WorkflowTemplateScope scope,
        string? projectId = null, int? expectedVersion = null)
    {
        var incoming = template is null ? null : Clone(template);
        var errors = WorkflowRules.Validate(incoming);
        if (incoming is not null && string.IsNullOrWhiteSpace(incoming.Name))
            errors.Add("шаблон: имя не задано.");
        if (errors.Count > 0)
            throw new WorkflowTemplateException(WorkflowTemplateErrorKind.Validation,
                "шаблон не прошёл проверку: " + string.Join(" ", errors), errors);

        incoming = Normalize(incoming!);
        if (scope == WorkflowTemplateScope.Global) incoming.BasedOn = null;

        lock (_lock)
        {
            EnsureWritable();
            var next = CloneFile(_file);
            var list = ListFor(next, scope, projectId, create: true)!;
            var existing = list.FindIndex(t => t.Id == incoming.Id);
            if (existing < 0)
            {
                if (expectedVersion is > 0)
                    throw new WorkflowTemplateException(WorkflowTemplateErrorKind.Conflict, "шаблон изменился: его уже нет.");
                incoming.Version = 1;
                incoming.BuiltInKey = null;
                list.Add(incoming);
            }
            else
            {
                var current = list[existing];
                if (expectedVersion is { } expected && expected != current.Version)
                    throw new WorkflowTemplateException(WorkflowTemplateErrorKind.Conflict,
                        $"шаблон изменился: версия {current.Version}, ожидалась {expected}.");
                incoming.Version = current.Version + 1;
                incoming.BuiltInKey = current.BuiltInKey;
                list[existing] = incoming;
            }
            Commit(next);
            return Clone(incoming);
        }
    }

    // Встроенный удалить нельзя; отсутствующий — NotFound.
    public void Delete(string id, WorkflowTemplateScope scope, string? projectId = null)
    {
        lock (_lock)
        {
            EnsureWritable();
            var next = CloneFile(_file);
            var list = ListFor(next, scope, projectId, create: false);
            var existing = list?.FindIndex(t => t.Id == id) ?? -1;
            if (list is null || existing < 0)
                throw new WorkflowTemplateException(WorkflowTemplateErrorKind.NotFound, "шаблон не найден.");
            if (list[existing].BuiltInKey is not null)
                throw new WorkflowTemplateException(WorkflowTemplateErrorKind.Forbidden,
                    "встроенный шаблон удалить нельзя — можно сбросить к коду.");
            list.RemoveAt(existing);
            Commit(next);
        }
    }

    // Сбросить встроенный пресет к коду: содержимое из кода, Version = текущая + 1.
    public WorkflowTemplate ResetBuiltIn(string builtInKey)
    {
        if (!BuiltInKeys.Contains(builtInKey))
            throw new WorkflowTemplateException(WorkflowTemplateErrorKind.NotFound, $"встроенный пресет «{builtInKey}» не найден.");
        lock (_lock)
        {
            EnsureWritable();
            var next = CloneFile(_file);
            var fresh = BuiltInPreset(builtInKey);
            var idx = next.Global.FindIndex(t => t.BuiltInKey == builtInKey);
            if (idx < 0) next.Global.Insert(0, fresh);
            else
            {
                fresh.Version = next.Global[idx].Version + 1;
                next.Global[idx] = fresh;
            }
            Commit(next);
            return Clone(fresh);
        }
    }

    private void EnsureWritable()
    {
        if (_futureFormat)
            throw new WorkflowTemplateException(WorkflowTemplateErrorKind.Forbidden,
                "файл шаблонов записан более новой версией сервера — запись отключена.");
    }

    private void Commit(WorkflowTemplateFile next)
    {
        next.Version = FormatVersion;
        JsonFileStore.Save(_path, next, JsonOpts);
        _file = next;
    }

    private static List<WorkflowTemplate>? ListFor(WorkflowTemplateFile file, WorkflowTemplateScope scope,
        string? projectId, bool create)
    {
        if (scope == WorkflowTemplateScope.Global) return file.Global;
        if (string.IsNullOrWhiteSpace(projectId))
            throw new WorkflowTemplateException(WorkflowTemplateErrorKind.Validation, "для проектного шаблона нужен projectId.");
        if (file.Projects.TryGetValue(projectId, out var list)) return list;
        if (!create) return null;
        return file.Projects[projectId] = [];
    }

    // Пресеты кода, которых нет в файле, добавляются в начало (порядок: full-cycle, fast-track).
    private static void EnsureBuiltIns(WorkflowTemplateFile file)
    {
        var at = 0;
        foreach (var key in BuiltInKeys)
        {
            if (!file.Global.Any(t => t.BuiltInKey == key)) file.Global.Insert(at, BuiltInPreset(key));
            at++;
        }
    }

    // Файл из диска мог быть правлен руками: null-элементы и пустые списки не должны ронять стор.
    private static WorkflowTemplateFile Sanitize(WorkflowTemplateFile file)
    {
        file.Global = (file.Global ?? []).Where(t => t is not null).ToList();
        file.Projects = (file.Projects ?? new())
            .ToDictionary(p => p.Key, p => (p.Value ?? []).Where(t => t is not null).ToList(), StringComparer.Ordinal);
        foreach (var t in file.Global.Concat(file.Projects.Values.SelectMany(v => v)))
            t.Stages ??= [];
        return file;
    }

    // Триммим строки, которые Validate сравнивает после Trim, и клампим порог молчания (И3).
    private static WorkflowTemplate Normalize(WorkflowTemplate t)
    {
        t.Name = t.Name.Trim();
        foreach (var s in t.Stages)
        {
            s.Role = s.Role.Trim();
            s.Presence = s.Presence.Trim();
            // Имя этапа (G7): та же нормализация, что проверяет Validate, — «пустая → null».
            s.Title = string.IsNullOrWhiteSpace(s.Title) ? null : s.Title.Trim();
            s.StallPolicy = string.IsNullOrWhiteSpace(s.StallPolicy) ? null : s.StallPolicy.Trim();
            s.StallMinutes = WorkflowRules.NormalizeStall(s.StallMinutes);
        }
        return t;
    }

    private static WorkflowTemplate Clone(WorkflowTemplate t) =>
        JsonSerializer.Deserialize<WorkflowTemplate>(JsonSerializer.Serialize(t, JsonOpts), JsonOpts)!;

    private static WorkflowTemplateFile CloneFile(WorkflowTemplateFile f) =>
        JsonSerializer.Deserialize<WorkflowTemplateFile>(JsonSerializer.Serialize(f, JsonOpts), JsonOpts)!;
}
