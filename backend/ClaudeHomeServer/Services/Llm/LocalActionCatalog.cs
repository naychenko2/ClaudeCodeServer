namespace ClaudeHomeServer.Services.Llm;

// Профиль вызова локальной модели: задаёт размер контекстного окна, лимит вывода и
// таймаут. Разные фоновые задачи грузят модель по-разному — от мелкой классификации
// (short) до суммаризации большого транскрипта (large). num_ctx особенно важен:
// Ollama по умолчанию режет вход до ~4k токенов и МОЛЧА теряет хвост промпта.
public enum CheapProfile { Small, Text, Large }

// Базовые параметры профиля (переопределяются секцией Ollama:Profiles в конфиге).
public sealed record CheapProfileSpec(int NumCtx, int NumPredict, int TimeoutMs);

// Одно фоновое one-shot действие, которое МОЖЕТ выполняться локальной моделью.
// Key — стабильный идентификатор (ключ в конфиге Ollama:Actions и в UI использования).
// DefaultLocal — рекомендация по умолчанию (политика A): при настроенном Ollama действие
// уходит на локаль, если в конфиге явно не сказано иначе.
public sealed record LocalAction(
    string Key, string Title, string Group, CheapProfile Profile, bool DefaultLocal);

// Каталог всех фоновых one-shot действий — единый источник правды для роутинга и UI.
// Сюда НЕ входят технически неприменимые: задача-исполнитель (агентная сессия с
// инструментами, не one-shot) и генерация картинок fal.ai.
public static class LocalActionCatalog
{
    // Ключи действий — ссылаются потребители (типобезопасно вместо строк-литералов).
    public const string ActionRank = "action-rank";
    public const string NotesTags = "notes-tags";
    public const string NotesLinks = "notes-links";
    public const string NotesDailySummary = "notes-daily-summary";
    public const string NoteTitle = "note-title";
    public const string ChatTitle = "chat-title";
    public const string ChatExtractTasks = "chat-extract-tasks";
    public const string MemoryWriteResolve = "memory-write-resolve";
    public const string PersonaMemoryAutolearn = "persona-memory-autolearn";
    public const string TeamMemoryAutolearn = "team-memory-autolearn";
    public const string PersonaMemoryConsolidate = "persona-memory-consolidate";
    public const string TeamMemoryConsolidate = "team-memory-consolidate";
    public const string AutomationGate = "automation-gate";
    public const string DocSummary = "doc-summary";
    public const string DocExtract = "doc-extract";
    public const string DocTags = "doc-tags";
    public const string DocFormat = "doc-format";
    public const string KbDescribe = "kb-describe";
    public const string PersonaMatch = "persona-match";
    public const string TaskAi = "task-ai";
    public const string TaskClassify = "task-classify";
    public const string TaskNormalizeTitle = "task-normalize-title";
    public const string TaskDedup = "task-dedup";
    public const string SkillSuggest = "skill-suggest";
    public const string SessionSummary = "session-summary";
    public const string GitCommitMsg = "git-commit-msg";
    public const string GitStashName = "git-stash-name";
    public const string SkillTranslate = "skill-translate";
    public const string SkillGenerate = "skill-generate";
    public const string DailyBriefing = "daily-briefing";

    // Дефолты профилей. Переопределяются Ollama:Profiles:{small|text|large}:{NumCtx|NumPredict|TimeoutMs}.
    public static readonly IReadOnlyDictionary<CheapProfile, CheapProfileSpec> ProfileDefaults =
        new Dictionary<CheapProfile, CheapProfileSpec>
        {
            [CheapProfile.Small] = new(NumCtx: 4096, NumPredict: 256, TimeoutMs: 20_000),
            [CheapProfile.Text] = new(NumCtx: 8192, NumPredict: 768, TimeoutMs: 45_000),
            [CheapProfile.Large] = new(NumCtx: 16384, NumPredict: 1024, TimeoutMs: 90_000),
        };

    public static readonly IReadOnlyList<LocalAction> All =
    [
        new(ActionRank, "Ранжир действий AI-хаба", "AI-хаб", CheapProfile.Small, DefaultLocal: true),
        new(NotesTags, "Теги заметок", "Заметки", CheapProfile.Small, DefaultLocal: true),
        new(NotesLinks, "Связи заметок", "Заметки", CheapProfile.Text, DefaultLocal: true),
        new(NotesDailySummary, "Конспект дня", "Заметки", CheapProfile.Large, DefaultLocal: true),
        new(NoteTitle, "Заголовок заметки", "Заметки", CheapProfile.Small, DefaultLocal: true),
        new(ChatTitle, "Заголовок чата", "Чаты", CheapProfile.Small, DefaultLocal: true),
        new(ChatExtractTasks, "Извлечение задач из чата", "Задачи", CheapProfile.Large, DefaultLocal: true),
        new(MemoryWriteResolve, "Резолвер записи памяти", "Память", CheapProfile.Small, DefaultLocal: true),
        new(PersonaMemoryAutolearn, "Автолёрн памяти персон", "Память", CheapProfile.Large, DefaultLocal: true),
        new(TeamMemoryAutolearn, "Автолёрн памяти команды", "Память", CheapProfile.Large, DefaultLocal: true),
        new(PersonaMemoryConsolidate, "Консолидация памяти персон", "Память", CheapProfile.Text, DefaultLocal: true),
        new(TeamMemoryConsolidate, "Консолидация памяти команды", "Память", CheapProfile.Text, DefaultLocal: true),
        new(AutomationGate, "Гейт проактивности персон", "Персоны", CheapProfile.Small, DefaultLocal: true),
        new(DocSummary, "Краткое содержание документа", "Документы", CheapProfile.Large, DefaultLocal: true),
        new(DocExtract, "Выжимка из документа", "Документы", CheapProfile.Large, DefaultLocal: true),
        new(DocTags, "Теги документа", "Документы", CheapProfile.Text, DefaultLocal: true),
        new(DocFormat, "Разметка Markdown при трансформации", "Документы", CheapProfile.Large, DefaultLocal: true),
        new(KbDescribe, "Описание базы знаний", "Знания", CheapProfile.Small, DefaultLocal: true),
        new(PersonaMatch, "Подбор персоны под задачу", "Персоны", CheapProfile.Small, DefaultLocal: true),
        new(TaskAi, "Описание и подзадачи задач", "Задачи", CheapProfile.Text, DefaultLocal: true),
        new(TaskClassify, "Приоритет и метки задач", "Задачи", CheapProfile.Small, DefaultLocal: true),
        new(TaskNormalizeTitle, "Нормализация заголовка задачи", "Задачи", CheapProfile.Small, DefaultLocal: true),
        new(TaskDedup, "Поиск дублей задач", "Задачи", CheapProfile.Small, DefaultLocal: true),
        new(SkillSuggest, "Подбор навыка", "Навыки", CheapProfile.Small, DefaultLocal: true),
        new(SessionSummary, "Сводка сессии", "Сессии", CheapProfile.Large, DefaultLocal: true),
        new(GitCommitMsg, "Commit-сообщения", "Git", CheapProfile.Text, DefaultLocal: true),
        new(GitStashName, "Названия стэшей", "Git", CheapProfile.Small, DefaultLocal: true),
        // Ниже — по умолчанию остаются на claude (лицо продукта / генерация артефактов),
        // но конфиг поддерживает и их перевод на локаль при желании.
        new(SkillTranslate, "Перевод описаний навыков", "Навыки", CheapProfile.Small, DefaultLocal: false),
        new(SkillGenerate, "Генерация тела навыка", "Навыки", CheapProfile.Text, DefaultLocal: false),
        new(DailyBriefing, "Утренний бриф", "Продукт", CheapProfile.Large, DefaultLocal: false),
    ];

    private static readonly Dictionary<string, LocalAction> ByKey =
        All.ToDictionary(a => a.Key, StringComparer.OrdinalIgnoreCase);

    public static LocalAction? Find(string key) =>
        ByKey.TryGetValue(key, out var a) ? a : null;

    public static bool IsKnown(string key) => ByKey.ContainsKey(key);
}
