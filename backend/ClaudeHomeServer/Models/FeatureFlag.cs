namespace ClaudeHomeServer.Models;

/// <summary>
/// Определение фич-флага — декларируется в коде (source of truth).
/// </summary>
/// <param name="Key">Стабильный машинный ключ (kebab-case), по нему хранится override юзера.</param>
/// <param name="Title">Человекочитаемое название для тумблера.</param>
/// <param name="Description">Что включает фича.</param>
/// <param name="Default">Значение по умолчанию, когда у юзера нет override.</param>
/// <param name="Stage">Зрелость: "dev" | "beta" | "stable" — только для метки в UI.</param>
public record FeatureFlagDefinition(
    string Key,
    string Title,
    string Description,
    bool Default,
    string Stage);

/// <summary>
/// Константы ключей флагов — использовать вместо строковых литералов,
/// чтобы опечатка не отключала фичу молча.
/// </summary>
public static class FeatureFlagKeys
{
    public const string SessionArtifacts = "session-artifacts";
    public const string Notes = "notes";
    public const string NotesSessionSummary = "notes-session-summary";
    public const string NotesAutoRecall = "notes-auto-recall";
    public const string NotesMemorySource = "notes-memory-source";
    public const string DailyBriefing = "daily-briefing";
    public const string NotesTaskSync = "notes-task-sync";
    public const string TaskExecContext = "task-exec-context";
    public const string ChatExtractTasks = "chat-extract-tasks";
    public const string UnifiedSearch = "unified-search";
}

/// <summary>
/// Единственное место, где объявляются фич-флаги. Чтобы добавить новый флаг —
/// допиши строку в <see cref="All"/> (и продублируй ключ в lib/featureFlags.ts на фронте).
/// </summary>
public static class FeatureFlagCatalog
{
    public static readonly IReadOnlyList<FeatureFlagDefinition> All =
    [
        // Панель «Артефакты сессии» — сводка по активной сессии справа от чата:
        // измененные файлы (с дельтами строк), план (из ExitPlanMode), задачи (TodoWrite) и ссылки.
        new FeatureFlagDefinition(
            Key: FeatureFlagKeys.SessionArtifacts,
            Title: "Артефакты сессии",
            Description: "Панель справа от чата с измененными файлами, планом, задачами, агентами и ссылками за текущую сессию.",
            Default: false,
            Stage: "beta"),

        // Раздел «Заметки» — Obsidian-совместимая база знаний: markdown-заметки с
        // [[wikilinks]], backlinks и графом; Claude ведёт их через MCP notes-server.
        new FeatureFlagDefinition(
            Key: FeatureFlagKeys.Notes,
            Title: "Заметки",
            Description: "База знаний: markdown-заметки со связями [[…]], обратными ссылками и графом. Claude ведёт заметки, единый граф поверх личного vault и проектов.",
            Default: false,
            Stage: "beta"),

        // «Итог сессии» — кнопка в шапке чата: конспект сессии one-shot вызовом LLM
        // сохраняется заметкой (проект → notes/Сессии, чат → личный vault).
        new FeatureFlagDefinition(
            Key: FeatureFlagKeys.NotesSessionSummary,
            Title: "Итог сессии в заметку",
            Description: "Кнопка в шапке чата: Claude составляет конспект сессии (цель, решения, результат) и сохраняет его заметкой.",
            Default: false,
            Stage: "beta"),

        // Auto-recall — перед каждым ходом семантический поиск по заметкам,
        // топ релевантных подмешивается в системный промпт (нужен Dify).
        new FeatureFlagDefinition(
            Key: FeatureFlagKeys.NotesAutoRecall,
            Title: "Заметки в контексте Claude",
            Description: "Перед каждым ходом Claude автоматически получает выдержки из семантически близких заметок — база знаний работает как память.",
            Default: false,
            Stage: "dev"),

        // Память Claude Code (~/.claude/projects/<slug>/memory) как read-only
        // источник заметок: видна в списке, графе и семантическом поиске.
        new FeatureFlagDefinition(
            Key: FeatureFlagKeys.NotesMemorySource,
            Title: "Память Claude в заметках",
            Description: "Файлы памяти Claude Code по проектам показываются как источник заметок (только чтение): видно, что Claude помнит.",
            Default: false,
            Stage: "dev"),

        // Утренний бриф — агент собирает просроченные/сегодняшние задачи, изменённые
        // заметки и git-активность за сутки, прогоняет через LLM и пишет план дня
        // в дневниковую заметку (## Утренний бриф) + push. On-demand и по расписанию утром.
        new FeatureFlagDefinition(
            Key: FeatureFlagKeys.DailyBriefing,
            Title: "Утренний бриф",
            Description: "Агент утром собирает задачи на день, свежие заметки и активность по проектам в короткий план дня — пишет его в дневник и присылает уведомление.",
            Default: false,
            Stage: "dev"),

        // Связь чекбоксов заметок и задач: `- [ ] … 📅 дата` можно превратить в настоящую
        // задачу (календарь, напоминания); завершение с любой стороны синхронизирует галочку.
        new FeatureFlagDefinition(
            Key: FeatureFlagKeys.NotesTaskSync,
            Title: "Задачи из заметок",
            Description: "Чекбоксы `- [ ]` в заметках можно превратить в задачи с датой и повтором. Отметка выполнения в заметке и в календаре синхронизируется.",
            Default: false,
            Stage: "dev"),

        // Claude-исполнитель задач получает в контекст семантически близкие заметки
        // (нужен Dify): выполняет задачу, опираясь на базу знаний, а не вслепую.
        new FeatureFlagDefinition(
            Key: FeatureFlagKeys.TaskExecContext,
            Title: "Заметки в контексте исполнителя",
            Description: "Когда Claude берёт задачу в работу, в постановку подмешиваются выдержки из семантически близких заметок — исполнение с опорой на базу знаний.",
            Default: false,
            Stage: "dev"),
    ];

    private static readonly HashSet<string> Keys = All.Select(f => f.Key).ToHashSet();

    /// <summary>Существует ли флаг с таким ключом в реестре.</summary>
    public static bool Exists(string key) => Keys.Contains(key);
}
