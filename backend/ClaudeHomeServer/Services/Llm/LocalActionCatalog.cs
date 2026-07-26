namespace ClaudeHomeServer.Services.Llm;

// Профиль вызова локальной модели: задаёт размер контекстного окна, лимит вывода и
// таймаут. Разные фоновые задачи грузят модель по-разному — от мелкой классификации
// (short) до суммаризации большого транскрипта (large). num_ctx особенно важен:
// Ollama по умолчанию режет вход до ~4k токенов и МОЛЧА теряет хвост промпта.
public enum CheapProfile { Small, Text, Large }

// Базовые параметры профиля (переопределяются секцией Ollama:Profiles в конфиге).
public sealed record CheapProfileSpec(int NumCtx, int NumPredict, int TimeoutMs);

// Одно место применения модели. Исторически — фоновое one-shot действие, с v2 каталог
// накрывает и агентные места (группа «Чаты и персоны»): им тоже назначается исполнитель.
// Key — стабильный идентификатор (ключ в конфиге Ollama:Actions, сторе назначений и UI).
// DefaultLocal — рекомендация по умолчанию (политика A): при настроенном Ollama действие
// уходит на локаль, если в конфиге явно не сказано иначе.
// Agentic — место запускает агентную сессию claude CLI (не one-shot): локаль и
// direct:-модели ему недоступны, маршрут резолвится ModelAssignmentResolver'ом,
// а не CheapTextRunner'ом.
// Tier — слот по умолчанию, когда админ ничего не выбирал; null = вычислить из Profile
// (Small/Text → слабая, Large → средняя): см. EffectiveDefaultTier.
public sealed record LocalAction(
    string Key, string Title, string Group, CheapProfile Profile, bool DefaultLocal,
    bool Agentic = false, ModelTier? Tier = null);

// Каталог всех фоновых one-shot действий — единый источник правды для роутинга и UI.
// Сюда НЕ входят технически неприменимые: задача-исполнитель (агентная сессия с
// инструментами, не one-shot) и генерация картинок fal.ai.
// Два слоя: статический массив встроенных действий + динамический слой LLM-действий
// внешних модулей (контракт §10.1, ключи module:{id}:{key}) — его наполняет ModuleRegistry
// на старте, по паттерну динамических флагов module-{id} в FeatureFlagService.
public static class LocalActionCatalog
{
    // Ключи действий — ссылаются потребители (типобезопасно вместо строк-литералов).
    // Группа «Чаты и персоны» (Agentic): места, где раньше модель выбиралась неявно.
    public const string ChatNew = "chat-new";
    public const string ChatPersona = "chat-persona";
    public const string TasksExecutor = "tasks-executor";
    public const string SubagentConsultant = "subagent-consultant";
    public const string ModulesLlm = "modules-llm";

    public const string ActionRank = "action-rank";
    public const string NotesTags = "notes-tags";
    public const string NotesLinks = "notes-links";
    public const string NotesDailySummary = "notes-daily-summary";
    public const string NoteTitle = "note-title";
    public const string NoteToc = "note-toc";
    public const string NoteTranslate = "note-translate";
    public const string ChatTitle = "chat-title";
    public const string ChatRetitle = "chat-retitle";
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
    public const string NotificationSummary = "notification-summary";
    public const string GitCommitMsg = "git-commit-msg";
    public const string GitStashName = "git-stash-name";
    public const string SkillTranslate = "skill-translate";
    public const string SkillGenerate = "skill-generate";
    public const string DailyBriefing = "daily-briefing";
    public const string PersonaAiCondition = "persona-ai-condition";
    public const string PersonaAiCharacter = "persona-ai-character";
    public const string PersonaAutomationSuggest = "persona-automation-suggest";
    public const string PersonaBindingsSuggest = "persona-bindings-suggest";
    public const string PersonaQuickCreate = "persona-quick-create";
    public const string PersonaAiTeam = "persona-ai-team";
    public const string Changelog = "changelog";

    // Дефолты профилей. Переопределяются Ollama:Profiles:{small|text|large}:{NumCtx|NumPredict|TimeoutMs}.
    public static readonly IReadOnlyDictionary<CheapProfile, CheapProfileSpec> ProfileDefaults =
        new Dictionary<CheapProfile, CheapProfileSpec>
        {
            [CheapProfile.Small] = new(NumCtx: 4096, NumPredict: 256, TimeoutMs: 20_000),
            [CheapProfile.Text] = new(NumCtx: 8192, NumPredict: 768, TimeoutMs: 45_000),
            [CheapProfile.Large] = new(NumCtx: 16384, NumPredict: 1024, TimeoutMs: 90_000),
        };

    private static readonly IReadOnlyList<LocalAction> Builtin =
    [
        // Агентные места (группа первая — в UI это самые важные назначения). Профиль у них
        // номинальный (для этих мест он не используется — цепочка локали не применяется).
        new(ChatNew, "Новый чат", "Чаты и персоны", CheapProfile.Large, DefaultLocal: false,
            Agentic: true, Tier: ModelTier.Strong),
        new(ChatPersona, "Чат с персоной (без своей модели)", "Чаты и персоны", CheapProfile.Large,
            DefaultLocal: false, Agentic: true, Tier: ModelTier.Strong),
        new(TasksExecutor, "Исполнитель задач", "Чаты и персоны", CheapProfile.Large,
            DefaultLocal: false, Agentic: true, Tier: ModelTier.Strong),
        new(SubagentConsultant, "Сабагенты-консультанты", "Чаты и персоны", CheapProfile.Large,
            DefaultLocal: false, Agentic: true, Tier: ModelTier.Medium),
        new(ModulesLlm, "LLM-канал внешних модулей", "Чаты и персоны", CheapProfile.Large,
            DefaultLocal: false, Agentic: true, Tier: ModelTier.Medium),

        new(ActionRank, "Ранжир действий AI-хаба", "AI-хаб", CheapProfile.Small, DefaultLocal: true),
        new(NotesTags, "Теги заметок", "Заметки", CheapProfile.Small, DefaultLocal: true),
        new(NotesLinks, "Связи заметок", "Заметки", CheapProfile.Text, DefaultLocal: true),
        new(NotesDailySummary, "Конспект дня", "Заметки", CheapProfile.Large, DefaultLocal: true),
        new(NoteTitle, "Заголовок заметки", "Заметки", CheapProfile.Small, DefaultLocal: true),
        new(NoteToc, "Оглавление заметки", "Заметки", CheapProfile.Text, DefaultLocal: true),
        new(NoteTranslate, "Перевод заметки", "Заметки", CheapProfile.Large, DefaultLocal: true),
        new(ChatTitle, "Заголовок чата", "Чаты", CheapProfile.Small, DefaultLocal: true),
        new(ChatRetitle, "Обновление названия чата", "Чаты", CheapProfile.Text, DefaultLocal: true),
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
        new(NotificationSummary, "Суть уведомления", "Уведомления", CheapProfile.Small, DefaultLocal: true),
        new(GitCommitMsg, "Commit-сообщения", "Git", CheapProfile.Text, DefaultLocal: true),
        new(GitStashName, "Названия стэшей", "Git", CheapProfile.Small, DefaultLocal: true),
        new(PersonaAiCondition, "Условие применения привязки", "Персоны", CheapProfile.Small, DefaultLocal: true),
        new(PersonaAiCharacter, "Характер персоны", "Персоны", CheapProfile.Text, DefaultLocal: true),
        new(PersonaAutomationSuggest, "Подсказка правил проактивности", "Персоны", CheapProfile.Text, DefaultLocal: true),
        new(PersonaBindingsSuggest, "Подбор привязок знаний", "Персоны", CheapProfile.Text, DefaultLocal: true),
        // Ниже — по умолчанию остаются на claude (лицо продукта / генерация артефактов),
        // но конфиг и админский тумблер поддерживают и их перевод на локаль при желании.
        new(SkillTranslate, "Перевод описаний навыков", "Навыки", CheapProfile.Small, DefaultLocal: false),
        new(SkillGenerate, "Генерация тела навыка", "Навыки", CheapProfile.Text, DefaultLocal: false),
        new(DailyBriefing, "Утренний бриф", "Продукт", CheapProfile.Large, DefaultLocal: false),
        new(PersonaQuickCreate, "Черновик персоны по промпту", "Персоны", CheapProfile.Text, DefaultLocal: false),
        new(PersonaAiTeam, "Состав команды персон", "Персоны", CheapProfile.Large, DefaultLocal: false),
        // Сводка «Что нового»: тяжелее прочих (много коммитов, до 12 пунктов JSON, свой большой
        // таймаут Changelog:TimeoutMs) — потребитель передаёт timeout/maxTokens поверх профиля.
        // Дефолт claude: на бесплатной модели показ стоимости отпадает (она 0), что корректно.
        new(Changelog, "Сводка «Что нового»", "Продукт", CheapProfile.Large, DefaultLocal: false),
    ];

    private static readonly Dictionary<string, LocalAction> ByKey =
        Builtin.ToDictionary(a => a.Key, StringComparer.OrdinalIgnoreCase);

    // Динамический слой: действия внешних модулей. Снимок заменяется целиком при регистрации
    // (мержем поверх прежнего — читатели никогда не видят полумутированного состояния).
    private static volatile Dictionary<string, LocalAction> _dynamic =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly Lock DynamicSync = new();

    /// <summary>
    /// Регистрация LLM-действий модулей (§10.1). Ключи ОБЯЗАНЫ нести неймспейс
    /// module:{id}:{key} — коллизии со встроенными и попытки без неймспейса отбрасываются
    /// (встроенный каталог модулю не перехватить). Вызывается ModuleRegistry на старте.
    /// </summary>
    public static void RegisterDynamic(IEnumerable<LocalAction> actions)
    {
        lock (DynamicSync)
        {
            var next = new Dictionary<string, LocalAction>(_dynamic, StringComparer.OrdinalIgnoreCase);
            foreach (var a in actions)
            {
                if (!a.Key.StartsWith("module:", StringComparison.Ordinal) || ByKey.ContainsKey(a.Key))
                    continue;
                next[a.Key] = a;
            }
            _dynamic = next;
        }
    }

    // Встроенные + модульные. Свойство, а не поле: динамический слой наполняется после
    // инициализации типа; порядок стабильный — встроенные первыми, модульные следом.
    public static IReadOnlyList<LocalAction> All
    {
        get
        {
            var dynamic = _dynamic;
            return dynamic.Count == 0 ? Builtin : [.. Builtin, .. dynamic.Values];
        }
    }

    public static LocalAction? Find(string key) =>
        ByKey.TryGetValue(key, out var a) ? a : (_dynamic.TryGetValue(key, out var d) ? d : null);

    public static bool IsKnown(string key) => Find(key) is not null;

    // Слот по умолчанию для места: явный Tier записи, иначе из профиля сложности —
    // мелочь и середина на слабой, тяжёлое на средней. Сильная по умолчанию только
    // у агентных мест (задаётся явно в записи).
    public static ModelTier EffectiveDefaultTier(LocalAction action) =>
        action.Tier ?? (action.Profile == CheapProfile.Large ? ModelTier.Medium : ModelTier.Weak);
}
