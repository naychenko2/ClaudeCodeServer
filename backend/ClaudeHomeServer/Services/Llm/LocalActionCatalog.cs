namespace ClaudeHomeServer.Services.Llm;

// Профиль вызова: задаёт размер контекстного окна, лимит вывода и таймауты. Разные
// фоновые задачи грузят модель по-разному — от мелкой классификации (short) до
// суммаризации большого транскрипта (large). num_ctx особенно важен:
// Ollama по умолчанию режет вход до ~4k токенов и МОЛЧА теряет хвост промпта.
public enum CheapProfile { Small, Text, Large }

// Базовые параметры профиля (переопределяются секцией Ollama:Profiles в конфиге).
// Параметров по ДВА на каждый ограничитель — по числу маршрутов, и по одной и той же
// причине: локальные значения калибровались под Ollama на своём железе, облачным моделям
// они малы.
//  • TimeoutMs / CloudTimeoutMs — потолок времени. Облачная сильная модель на сложной
//    задаче отвечает заметно дольше локали, и локальный потолок её обрывал (прод
//    2026-08-04: планировщик «Командной реализации» на opus не уложился в 90 с).
//  • NumPredict / CloudNumPredict — потолок ВЫВОДА. Локальный бережёт память Ollama
//    (num_predict), облачный уходит в max_tokens запроса. Тот же перекос и та же цена:
//    у профиля Large на облаке 1024 токена вывода обрывали JSON плана на полуслове,
//    ParsePlan возвращал null, и человек видел «план не построился» — симптом,
//    неотличимый от таймаута (прод 2026-08-05). Облачные значения — с запасом на
//    крупный структурный ответ, но не выше 8k: это потолок вывода, который держат
//    практически все модели агрегатора, а больший провайдер может отбить 400-й.
public sealed record CheapProfileSpec(
    int NumCtx, int NumPredict, int TimeoutMs, int CloudTimeoutMs, int CloudNumPredict);

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
// CloudTimeoutMs — пер-местный потолок ожидания ОБЛАЧНОГО шага вместо потолка профиля:
// для мест, чей исполнитель отвечает дольше (или зависает дольше), чем допускает профиль,
// не трогая общий потолок для всех остальных мест того же профиля. null = профиль.
public sealed record LocalAction(
    string Key, string Title, string Group, CheapProfile Profile, bool DefaultLocal,
    bool Agentic = false, ModelTier? Tier = null, int? CloudTimeoutMs = null);

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
    // Голосовой режим чата (Session.VoiceMode): разговор без инструментов. Единственное
    // чатовое место с НЕагентной семантикой — локаль для него пригодна.
    public const string ChatVoice = "chat-voice";
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
    public const string TeamImplementPlan = "team-implement-plan";
    public const string MemoryWriteResolve = "memory-write-resolve";
    public const string PersonaMemoryAutolearn = "persona-memory-autolearn";
    public const string TeamMemoryAutolearn = "team-memory-autolearn";
    public const string TeamMemoryCompress = "team-memory-compress";
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
    public const string PromptAudit = "prompt-audit";
    // Паспорта изменений (ADR-004, этап 1): выжимка «зачем/решения/отказы/грабли» на коммит
    public const string DossierSummary = "dossier-summary";
    // Фон проекта (ADR-008): JSON со списком фигур дудла и ключом цвета палитры
    public const string ProjectBackground = "project-background";
    // Значок проекта (ADR-009): имя иконки из белого списка lucide либо нарисованные
    // моделью path'ы в viewBox 24; разметку собирает сервер (GlyphSvg.Build)
    public const string ProjectIcon = "project-icon";

    // Дефолты профилей. Переопределяются
    // Ollama:Profiles:{small|text|large}:{NumCtx|NumPredict|TimeoutMs|CloudTimeoutMs|CloudNumPredict}.
    // CloudTimeoutMs и CloudNumPredict растут с профилем: мелкой задаче на облаке хватает
    // общего дефолта раннера (120 с) и короткого ответа, тяжёлой нужно заметно больше —
    // планировщик на сильной модели с большим промптом отвечает до нескольких минут и
    // выдаёт многокилобайтный JSON.
    public static readonly IReadOnlyDictionary<CheapProfile, CheapProfileSpec> ProfileDefaults =
        new Dictionary<CheapProfile, CheapProfileSpec>
        {
            [CheapProfile.Small] = new(NumCtx: 4096, NumPredict: 256,
                TimeoutMs: 20_000, CloudTimeoutMs: 120_000, CloudNumPredict: 1024),
            [CheapProfile.Text] = new(NumCtx: 8192, NumPredict: 768,
                TimeoutMs: 45_000, CloudTimeoutMs: 180_000, CloudNumPredict: 4096),
            [CheapProfile.Large] = new(NumCtx: 16384, NumPredict: 1024,
                TimeoutMs: 90_000, CloudTimeoutMs: 300_000, CloudNumPredict: 8192),
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
        // Голосовой режим: ходы разговора (Session.VoiceMode) без инструментов — прямой вызов
        // локальной модели мимо claude CLI (ответ за секунды, а не старт подпроцесса). Место
        // рулит ТОЛЬКО веткой «Локальная»: любой другой выбор (слот/модель) — обычный CLI-путь.
        // На создание чата место не влияет (не участвует в ResolveDefaultModel).
        new(ChatVoice, "Голосовой чат (разговор)", "Чаты и персоны", CheapProfile.Text,
            DefaultLocal: false),

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
        // Разбор промпта хода («что тут лишнее»): вызывается человеком по кнопке, не фоном.
        // Локаль по умолчанию выключена — разбор идёт по метаданным секций и требует
        // рассуждения, слабая модель выдаёт общие слова вместо конкретных сокращений.
        new(PromptAudit, "Разбор промпта хода", "Чаты", CheapProfile.Large,
            DefaultLocal: false, Tier: ModelTier.Medium),
        // Планировщик режима «Командная реализация»: декомпозиция вводной и подбор
        // исполнителей по компетенциям. Локаль намеренно выключена — слабая модель
        // раздаёт работу случайно, а весь смысл места в осмысленном выборе персоны.
        new(TeamImplementPlan, "Планировщик командной реализации", "Задачи", CheapProfile.Large,
            DefaultLocal: false, Tier: ModelTier.Strong),
        new(MemoryWriteResolve, "Резолвер записи памяти", "Память", CheapProfile.Small, DefaultLocal: true),
        new(PersonaMemoryAutolearn, "Автолёрн памяти персон", "Память", CheapProfile.Large, DefaultLocal: true),
        new(TeamMemoryAutolearn, "Автолёрн памяти команды", "Память", CheapProfile.Large, DefaultLocal: true),
        // Сжатие авто-записи памяти команды длиннее ~700 символов до сути (~500) — только
        // бесплатная цепочка (RunFreeAsync): при недоступности локали/адаптера — жёсткая
        // обрезка на стороне TeamMemoryService, платить claude за это не нужно.
        new(TeamMemoryCompress, "Сжатие авто-записи памяти команды", "Память", CheapProfile.Small, DefaultLocal: true),
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
        // Паспорта изменений: сырьё как у ChatExtractTasks (реплики хода целиком) — Large
        // обязателен, Text/Small молча обрежут хвост промпта (num_ctx Ollama) и дадут
        // выдуманную выжимку, ради борьбы с которой заведён отдельный флаг recall.
        new(DossierSummary, "Выжимка паспорта изменения", "Паспорта изменений", CheapProfile.Large, DefaultLocal: true),
        // Фон проекта: 8–14 фигур дудла — это 2–4 КБ JSON, на Small/Text потолок вывода
        // обрежет ответ на полуслове и даст неотличимый от таймаута «bad-json». Локаль
        // выключена намеренно (решение владельца): рисование SVG не текстовая задача, а
        // DefaultLocal: false заодно убирает локаль из страховочного шага цепочки —
        // «сойдёт за успех» тут дороже честного отказа.
        new(ProjectBackground, "Фон проекта", "Проекты", CheapProfile.Large,
            DefaultLocal: false, Tier: ModelTier.Strong),
        // Значок проекта: Large — в промпт уходит весь белый список имён lucide, а на
        // Small/Text потолок контекста обрежет его и модель начнёт выдумывать
        // несуществующие имена. Локаль выключена по той же причине, что у фона:
        // рисование SVG — не текстовая задача. Тир Strong — решение владельца
        // (2026-08-17), поднят с Medium (ADR-009 §9).
        // Собственный лимит облака 180 с (прод 17.08): сильная модель отвечает 52–126 с,
        // а потолок профиля Large в 300 с оставлял зависший вызов висеть пять минут —
        // миграция значков на таком вызове стояла в полдня (Узбекистан/Общие вопросы,
        // no-model). 180 с покрывает наблюдаемые ответы с запасом и не трогает общий
        // потолок профиля для остальных Large-мест.
        new(ProjectIcon, "Значок проекта", "Проекты", CheapProfile.Large,
            DefaultLocal: false, Tier: ModelTier.Strong, CloudTimeoutMs: 180_000),
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

    /// <summary>
    /// Дефолтный уровень места по ключу каталога. Нужен вызывающим PersonaModel: уровень
    /// места разворачивает матрицы персоны, когда ни у неё, ни у специальности своего
    /// уровня нет (иначе ячейка персоны молча не срабатывала). null — неизвестный ключ.
    /// </summary>
    public static ModelTier? DefaultTierOf(string key) =>
        Find(key) is { } action ? EffectiveDefaultTier(action) : null;
}
