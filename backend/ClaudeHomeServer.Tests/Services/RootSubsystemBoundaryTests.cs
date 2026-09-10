using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// Сторож границ для синглтонов из КОРНЯ <c>ClaudeHomeServer.Services</c>:
/// рефлексия по сборке покрывает только top-level типы (без nested) с
/// <c>Namespace == "ClaudeHomeServer.Services"</c> строго (без поддеревьев
/// <c>Services.X</c>), и запрещает им ссылаться на ТИПЫ ИЗ ПОДСИСТЕМНЫХ
/// ВЕРТИКАЛЕЙ (например, <c>Services.Knowledge</c>, <c>Services.Llm</c>,
/// <c>Services.Dossiers</c>, и т.д.).
///
/// Допустимые зависимости:
/// - «Спинка»: <c>System.*</c>, <c>Microsoft.*</c>, <c>ClaudeHomeServer.Models</c>,
///   <c>ClaudeHomeServer.Services.Http/Composition/Mcp</c> — единая «спинка»
///   приложения (источник правды — <c>SharedAllowedPrefixes</c> в
///   <see cref="SubsystemBoundaryTests"/>).
/// - Инфраструктурные под-вертикали (<see cref="RootAllowedSubVerticalPrefixes"/>):
///   модельный слой (<c>Services.Llm</c>), запуск процессов и песочница
///   (<c>Services.Execution</c>), память/Dify (<c>Services.Memory</c>),
///   знания (<c>Services.Knowledge</c>), генерация картинок
///   (<c>Services.Images</c>), граф кода (<c>Services.CodeGraph</c>),
///   паспорта (<c>Services.Dossiers</c>), метрики задач (<c>Services.Spend</c>),
///   триггеры автоматизации (<c>Services.TriggerSources</c>), модули
///   (<c>Services.Modules</c>), документация (<c>Services.Docs</c>),
///   desktop-капабилити (<c>Services.Desktop</c>). Root-типы — общая
///   инфраструктура приложения, и зависимости от этих под-вертикалей
///   устоявшиеся: аналогично тому, как <see cref="SubsystemBoundaryTests"/>
///   даёт каждой под-вертикали явный allow-list по «спинке», здесь
///   root-слой получает явный allow-list по инфраструктурным под-вертикалям.
///   Расширять — только при появлении новой общей инфраструктуры.
/// - Peer-root: ссылки на другие top-level типы в <c>ClaudeHomeServer.Services</c>
///   (например, <c>BoardService</c> → <c>TaskManager</c>). Все root-классы живут
///   в общем «корневом слое» и могут свободно ссылаться друг на друга — это
///   общая инфраструктура, а не вертикаль. Сторож должен ловить только
///   выход root-классов в НЕинфраструктурные подсистемные вертикали
///   (например, <c>Services.Video</c>, <c>Services.Dossiers</c> если не в
///   allow-list, и т.п.), а не «ссылки на однофамильцев в корне».
/// - Backbone root-типы (<see cref="ExcludedRootTypes"/>): шовный код, который
///   по построению держит ссылки на все вертикали и сам перечислен в
///   <c>SharedAllowedPrefixes</c> или в точечных allow-list каждой вертикали
///   (<c>PersonaManager</c>, <c>ProjectManager</c>, <c>SessionManager</c>,
///   <c>UserStore</c>, <c>ChatHistoryService</c>,
///   <c>FileService</c>, <c>NotificationService</c>, <c>NotificationStore</c>).
///   <c>TaskManager</c> уехал в вертикаль <c>Services.Tasks</c> (волна 4C, шаг 1) —
///   его держит собственный подсистемный сторож <c>SubsystemBoundaryTests</c>,
///   а из root-исключений он снят, чтобы пустая запись не глушила проверку
///   по <c>FullName</c> (комментарий у <c>ExcludedRootTypes</c>).
///   Исключены из проверки и сами, и как цели ссылок (defense-in-depth: даже
///   если правило для peer-root сломается, эти типы не флагаются).
/// - Self-ссылки внутри класса: тип ссылается сам на себя, на свои
///   nested-типы (<c>Foo+Bar</c>) или на типы, объявленные внутри самого
///   класса (<c>referenced.IsNested &amp;&amp; referenced.DeclaringType == type</c>).
///
/// Зачем отдельный сторож: основной <see cref="SubsystemBoundaryTests"/> сканирует
/// типы из подсистемного <c>NamespaceRoot</c> (например, <c>Services.Knowledge</c>),
/// и НЕ покрывает корневые синглтоны — мутации в них проходят молча. Находка
/// ревью 50f3b849: добавление <c>PersonaManager</c> в конструктор
/// <c>KnowledgeService</c> давало зелёный boundary-тест, потому что
/// <c>KnowledgeService</c> жил в корне Services и его не видел ни один страж.
///
/// Сторож читает: поля, параметры конструкторов, публичные свойства, сигнатуры
/// публичных методов И тела методов (IL-скан через <see cref="BoundaryIlScanner"/>).
/// </summary>
public class RootSubsystemBoundaryTests
{
    // Форс-загрузка всех вертикальных сборок (Main плюс вынесенные .Reader/.Yandex/.Video).
    // Подробности см. в комментарии к статическому конструктору SubsystemBoundaryTests.
    static RootSubsystemBoundaryTests()
    {
        _ = typeof(ClaudeHomeServer.Services.Video.VideoSubsystem).Assembly;
        _ = typeof(ClaudeHomeServer.Services.Yandex.YandexSubsystem).Assembly;
        _ = typeof(ClaudeHomeServer.Services.Reader.ReaderService).Assembly;
        _ = typeof(ClaudeHomeServer.Services.CodeGraph.CodeGraphSubsystem).Assembly;
        _ = typeof(ClaudeHomeServer.Services.Skills.SkillsSubsystem).Assembly;
        _ = typeof(ClaudeHomeServer.Services.Git.GitSubsystem).Assembly;
        _ = typeof(ClaudeHomeServer.Services.Notes.NotesSubsystem).Assembly;
        _ = typeof(ClaudeHomeServer.Services.Tts.TtsSubsystem).Assembly;
        _ = typeof(ClaudeHomeServer.Services.Personas.PersonaDraftService).Assembly;
        _ = typeof(ClaudeHomeServer.Services.Diagnostics.FileLog).Assembly;
        _ = typeof(ClaudeHomeServer.Services.WebSearch.PerplexitySearchService).Assembly;
        _ = typeof(ClaudeHomeServer.Services.Changelog.ChangelogSubsystem).Assembly;
        _ = typeof(ClaudeHomeServer.Services.Docs.DocsIndexService).Assembly;
        _ = typeof(ClaudeHomeServer.Services.Knowledge.KnowledgeSubsystem).Assembly;
        _ = typeof(ClaudeHomeServer.Services.Modules.ModuleRegistry).Assembly;
        _ = typeof(ClaudeHomeServer.Services.ProjectServices.ProjectServicesSubsystem).Assembly;
        _ = typeof(ClaudeHomeServer.Services.Spend.SpendSubsystem).Assembly;
        _ = typeof(ClaudeHomeServer.Services.Tasks.TasksSubsystem).Assembly;
        // Turn — отдельная сборка (Этап 5, вынос Turn): форс-загрузка нужна, чтобы
        // сторож видел сборку Turn.dll и её root-типы.
        _ = typeof(ClaudeHomeServer.Services.Turn.IPromptSectionContributor).Assembly;
        // Dossiers/Memory — отдельные сборки (Этап 5, волна 3, финал): форс-загрузка нужна,
        // чтобы сторож видел сборки Dossiers.dll / Memory.dll и их root-типы.
        _ = typeof(ClaudeHomeServer.Services.Dossiers.DossiersSubsystem).Assembly;
        _ = typeof(ClaudeHomeServer.Services.Memory.MemorySubsystem).Assembly;
        // Desktop — отдельная сборка (Этап 5, вынос Desktop): форс-загрузка нужна,
        // чтобы сторож видел типы грани (маршрутизатор канала, хаб устройств, схемы
        // авторизации) и проверял их границы по Desktop.dll.
        _ = typeof(ClaudeHomeServer.Services.Desktop.DesktopCallRouter).Assembly;
        _ = typeof(ClaudeHomeServer.Services.Backgrounds.BackgroundsSubsystem).Assembly;
        _ = typeof(ClaudeHomeServer.Services.ProjectIcons.ProjectIconsSubsystem).Assembly;
        _ = typeof(ClaudeHomeServer.Services.Terminal.TerminalService).Assembly;
        // Llm — отдельная сборка (Этап 5, финал линии): форс-загрузка нужна, чтобы
        // сторож видел сборку Llm.dll и её root-типы.
        _ = typeof(ClaudeHomeServer.Services.Llm.LlmSubsystem).Assembly;
    }

    /// <summary>Неймспейсы, на которые ЛЮБОЙ root-тип имеет право ссылаться
    /// (BCL/платформа + общие служебные слои). Источник правды — <c>SharedAllowedPrefixes</c>
    /// из <see cref="SubsystemBoundaryTests"/>.</summary>
    private static readonly string[] SharedAllowedPrefixes =
    {
        "System",
        "Microsoft",
        "ClaudeHomeServer.Models",
        "ClaudeHomeServer.Services.Http",
        "ClaudeHomeServer.Services.Composition",
        "ClaudeHomeServer.Services.Mcp",
        // Сборка `ClaudeHomeServer.Core` — спинка (assembly-based check в
        // `IsSharedAllowed`); Protocol — WS-контракт.
        // См. комментарии в SubsystemBoundaryTests.
        "ClaudeHomeServer.Protocol",
    };

    /// <summary>Сборка-спинка из общего кода. Любой тип из `ClaudeHomeServer.Core.dll`
    /// — инфраструктурный примитив, не сервисная логика (см. подробности в
    /// <see cref="SubsystemBoundaryTests.CoreAssemblyName"/>).</summary>
    private const string CoreAssemblyName = "ClaudeHomeServer.Core";

    /// <summary>Под-вертикали, на которые root-типы имеют право ссылаться
    /// как на общую инфраструктуру. По аналогии с per-vertical
    /// <c>AllowedNamespacePrefixes</c> в <see cref="SubsystemBoundaryTests"/>:
    /// там каждая под-вертикаль сама объявляет, на какие под-вертикали ей
    /// можно полагаться, а здесь root-слой объявляет «свою спинку»
    /// инфраструктурных под-вертикалей. Сознательно у́же, чем все
    /// существующие <c>Services.*</c>: новая под-вертикаль, на которую
    /// root начнёт ссылаться, БЕЗ записи здесь поймается сторожем —
    /// и повод обсудить, действительно ли это инфраструктура или нет.
    ///
    /// Сюда попадают только те под-вертикали, на которые root ссылается
    /// МНОЖЕСТВОМ типов (3+). Точечные зависимости от единичных типов
    /// (например, <c>FileWatcherService → CodeGraphService</c>) переехали в
    /// <see cref="RootAllowedExactTypes"/> — префикс открывал бы всю вертикаль,
    /// а реально нужен один тип.</summary>
    private static readonly string[] RootAllowedSubVerticalPrefixes =
    {
        // Модельный слой: дешёвые ходы (ICheapTextRunner), резолверы моделей
        // и слотов (LlmProviderRegistry, ModelAssignmentResolver, UserModelTierResolver),
        // one-shot раннеры (OneShotClaudeRunner, IOneShotRunner), каталог пресетов
        // (TierMatrix) и логи ходов (SubagentRunLog). 33 пары root→Llm — это
        // префикс-шов по прецеденту Git/Backgrounds/Deploy/Spend/Dossiers/Memory.
        "ClaudeHomeServer.Services.Llm",
        // Запуск процессов и песочница: ILauncherFactory, IProcessLauncher,
        // SandboxManager. 9 пар root→Execution (DevServer*/Terminal*/SkillsCli/
        // TaskExecution/PersonaAgentFileSync/UserHomeResolver).
        "ClaudeHomeServer.Services.Execution",
        // Память/Dify: MemoryWriteResolver, MemoryScoringOptions,
        // MemoryFusionOptions, MemoryDifyDebouncer. Общий слой дебаунса
        // и записи в Dify-датасеты — знаниевая инфраструктура. 10 пар.
        "ClaudeHomeServer.Services.Memory",
        // Знания: Dify RAG клиент (KnowledgeService), пути и нормализация
        // (WorkspaceKnowledgeStore), участник синка (ProjectKnowledgeSyncService),
        // участник каскада (KnowledgeSyncTarget). 9 пар.
        "ClaudeHomeServer.Services.Knowledge",
        // Генерация картинок и backfill: ImageGenerationService,
        // ImageBackfillService, ImageModelInfo, GeneratedImage. 4 пары.
        "ClaudeHomeServer.Services.Images",
        // Триггеры автоматизации: MentionTriggerSource, AutomationRootResolver,
        // ITriggerSource. PersonaAutomationService опирается на них напрямую.
        // 3 пары — кандидат на сужение, оставлен префиксом ради новых источников.
        "ClaudeHomeServer.Services.TriggerSources",
    };

    /// <summary>Точечные типы, на которые root-типы имеют право ссылаться,
    /// когда префикс открывать не нужно — реально нужен один тип из под-вертикали.
    /// По образцу <c>AllowedExactNamespaces</c> из <see cref="SubsystemBoundaryTests"/>:
    /// сравнение по <c>FullName</c>, nested-типы (содержащие '+') тоже ловятся.
    /// Раньше эти зависимости открывались целым префиксом в <see cref="RootAllowedSubVerticalPrefixes"/> —
    /// и под-вертикаль могла незаметно наращивать зависимости, потому что сторож
    /// пропускал всё, что под префиксом. Точечный допуск закрывает эту щель:
    /// если завтра root начнёт ссылаться на ещё один тип из <c>Services.CodeGraph</c>
    /// (не <c>CodeGraphService</c>), сторож покраснеет, и повод обсудить, нужна
    /// ли зависимость или пора выделить шов.</summary>
    private static readonly HashSet<string> RootAllowedExactTypes = new(StringComparer.Ordinal)
    {
        // Граф кода: FileWatcherService инвалидирует граф при изменении файлов
        // (см. FileWatcherService — подписка на CodeGraphService).
        "ClaudeHomeServer.Services.CodeGraph.CodeGraphService",
        // Desktop-капабилити: JwtService создаёт capability-токен для канала
        // desktop MCP через DesktopCaller (IssueDesktopToken — формирование
        // капабилити-токена). После выноса Desktop в отдельную сборку (Этап 5) это
        // ЕДИНСТВЕННАЯ ссылка root → Desktop: проверка токена отдаёт ClaimsPrincipal,
        // разбор в вызывателя ушёл на сторону вертикали. Направление Main → вертикаль
        // разрешено, но допуск точечный — новый тип грани в корне обязан покраснеть.
        // Живость проверена мутацией: снятие записи роняет сторож.
        "ClaudeHomeServer.Services.Desktop.DesktopCaller",
        // Документация: DocsIndexService читается ProjectPresetService для
        // построения списка доступных пресетов в композере онбординга v2.
        "ClaudeHomeServer.Services.Docs.DocsIndexService",
        // Внешние модули (YARP): ModuleRegistry знает, активен ли модуль —
        // FeatureFlagService дёргает ModuleRegistry на каждый запрос.
        "ClaudeHomeServer.Services.Modules.ModuleRegistry",
        // Метрики задач: TaskExecutionService пишет метрики в spend-стор через
        // TaskPromptMetricsStore. Первый root→Spend переход.
        "ClaudeHomeServer.Services.Spend.TaskPromptMetricsStore",
        // SpendMapping — чистые функции сборки SpendRecord из потока сообщений приёма хода
        // (этап 4, волна 1, 2026-09-07). Использует `ISpendCollector` как зависимость в
        // параметрах; префикс `Services.Spend` не открываем (Spend — продуктовая под-вертикаль).
        // Если завтра root начнёт ссылаться ещё на `SpendStore`/`SpendRecord`/etc — повод
        // пересмотреть, не выделить ли сборочные хелперы внутрь Spend (или не открыть
        // общий Spend-интерфейс).
        "ClaudeHomeServer.Services.Spend.ISpendCollector",
        // ⚠ Волна 4C, шаг 1 — выделена вертикаль Tasks, но шесть root-типов держат
        // `TaskManager` в конструкторе как «продуктовую зависимость», а не как
        // инфраструктуру. Префикс `Services.Tasks` не открываем (Tasks — продуктовая
        // вертикаль, а не «общий слой»): точечный допуск ровно на `TaskManager`,
        // чтобы `NoteTaskSyncService`/`SessionContextResolver`/`SessionMessagingService`/
        // `TaskExecutionService`/`TeamWaveService`/`UnifiedSearchService` могли держать
        // его в сигнатуре. Если завтра root начнёт ссылаться ещё на `TaskAiService`/
        // `BoardService`/`DailyBriefingService` и т.п., сторож покраснеет — повод
        // пересмотреть, не вынести ли конкретный root-тип внутрь Tasks (или не выделить
        // общий Tasks-интерфейс).
        "ClaudeHomeServer.Services.Tasks.TaskManager",
        // ⚠ Волна 4C, шаг 2 — выделена вертикаль Notes; шесть root-типов держат
        // `NotesService`/`NotesKnowledgeService` в конструкторе как «продуктовую
        // зависимость» (Notes записывает события в журнал, генерирует теги,
        // делает семантический поиск для recall-запросов). Префикс `Services.Notes`
        // не открываем (Notes — продуктовая вертикаль, не «общий слой»):
        // точечный допуск ровно на два типа, чтобы `PersonaBindingsService`/
        // `PersonasCrudService`/`SessionSummaryService`/`TaskExecutionService`/
        // `UnifiedSearchService`/`ChatDigestService` могли держать их в сигнатуре.
        // (`ChatDigestService` — волна 3 выноса Llm: приехал из вертикали в спину,
        // берёт `NotesService.GetDetail` для текста карточки архива.) Если завтра root
        // начнёт ссылаться ещё на `NotesAiService`/`NoteTaskSyncService`/etc —
        // сторож покраснеет, и повод пересмотреть, не выделить ли конкретный
        // root-тип внутрь Notes.
        "ClaudeHomeServer.Services.Notes.NotesService",
        "ClaudeHomeServer.Services.Notes.NotesKnowledgeService",
        // ⚠ Шов `root → Skills`: `PersonaBindingsService`/`PersonasCrudService` дёргают
        // `SkillsService` в ctor для валидации Skill-привязок персон и UI карточки персоны
        // (список глобальных скиллов). Префикс `Services.Skills` не открываем (Skills — листовая
        // продуктовая вертикаль): точечный допуск ровно на `SkillsService` (того же типа,
        // что у `Llm`/`Turn` в их allow-list).
        "ClaudeHomeServer.Services.Skills.SkillsService",
        // === IL-видимость (задача `8beee75e`, волна 1).
        // `PersonaAgentFileGenerator` зовёт `OmcPersonaRouting.AgentTypesFor(...)`
        // (static-метод, PersonaAgentFileGenerator.cs:148-149). IL-скан видит
        // declaring-тип. Точечный допуск на конкретный тип.
        "ClaudeHomeServer.Services.Prompts.OmcPersonaRouting",
        // `PersonaAutomationService` зовёт `Tasks.TaskDueCalculator.ResolveTimeZone(...)`
        // static-метод из тела метода. Точечный допуск.
        "ClaudeHomeServer.Services.Tasks.TaskDueCalculator",
        // `PersonaBindingsService`/`PersonasCrudService`/`TaskExecutionService`/
        // `UnifiedSearchService` материализуют `Notes.NoteSemanticHit` (record,
        // return-тип `NotesKnowledgeService.SearchAsync`). Точечный допуск.
        "ClaudeHomeServer.Services.Notes.NoteSemanticHit",
        // `PersonaBindingsService`/`PersonasCrudService` резолвят `Skills.SkillInfo`
        // через `sp.GetRequiredService<SkillInfo>()` (generic-аргумент виден
        // IL-скану). Точечный допуск.
        "ClaudeHomeServer.Services.Skills.SkillInfo",
        // `PersonasCrudService` ссылается на `Personas.PersonaDraftService`
        // (черновик персоны по промпту, тот же namespace — но это другой тип
        // внутри root-неймспейса `ClaudeHomeServer.Services.Personas`).
        // Префикс-шов не открываем: точечный допуск.
        "ClaudeHomeServer.Services.Personas.PersonaDraftService",
        // `ProjectPresetService` материализует nested-типы `DocsIndexService` в
        // async-state (статический вызов `DocsIndexService.Scan(...)` из тела метода).
        "ClaudeHomeServer.Services.Docs.DocsIndexService+ScopeFileResult",
        "ClaudeHomeServer.Services.Docs.DocsIndexService+ScopeFileWriteStatus",
        // `TaskExecutionService` зовёт `Tasks.TaskSchedulerService.TaskUrl(...)`
        // static-метод из тела метода — аналогично задокументированному шву
        // `Tasks → TaskHubExtensions` (см. комментарий у Tasks allow-list).
        "ClaudeHomeServer.Services.Tasks.TaskSchedulerService",
        // `TaskExecutionService` материализует nested `Spend.TaskPromptMetricsStore+Entry`
        // в async-state (поле state-машины). Точечный допуск на nested-тип.
        "ClaudeHomeServer.Services.Spend.TaskPromptMetricsStore+Entry",
        // Telemetry (бывший префикс, заменён точечным допуском):
        // `PersonasCrudService`/`OnboardingController`/`ProjectPresetsController`
        // зовут `ServerMetrics.Record*` из тел методов. Запись ДЕКОРАТИВНА и оставлена
        // как документация: финальное правило сторожа ниже флагает только
        // `ClaudeHomeServer.Services.*`, поэтому `ClaudeHomeServer.Telemetry` не гейтится
        // здесь вовсе — как `Hubs`/`Protocol`/`Controllers`. Гейт живёт в сторожа вертикалей.
        "ClaudeHomeServer.Telemetry.ServerMetrics",
        // === Этап 3, задача `4d044b22` — реакторы вынесены в root Services.
        // `GitAutoCommitService`/`CommitAttributionService` (root) держат `GitService`/
        // `GitCredentials` в ctor и через вызовы статики. Префикс `Services.Git` не
        // открываем: Git — продуктовая вертикаль, не «общий слой». Прецедент —
        // `Dossiers → Git.GitService` в `SubsystemBoundaryTests.Boundaries`.
        "ClaudeHomeServer.Services.Git.GitService",
        "ClaudeHomeServer.Services.Git.GitCredentials",
    };

    /// <summary>Корневые инфраструктурные слоны, исключённые из проверки (и как
    /// сами проверяемые типы, и как цели ссылок — defense-in-depth). Это шовный код,
    /// который по построению держит ссылки на все вертикали и сам перечислен в
    /// <c>SharedAllowedPrefixes</c> или в точечных allow-list каждой вертикали.</summary>
    private static readonly HashSet<string> ExcludedRootTypes = new(StringComparer.Ordinal)
    {
        // Корень Services.
        "ClaudeHomeServer.Services.PersonaManager",
        "ClaudeHomeServer.Services.ProjectManager",
        "ClaudeHomeServer.Services.SessionManager",
        "ClaudeHomeServer.Services.UserStore",
        // TaskManager уехал в Services.Tasks.TasksSubsystem (волна 4C, шаг 1) —
        // его границы ловит SubsystemBoundaryTests по строке `Tasks` в Boundaries,
        // здесь он больше не исключение.
        "ClaudeHomeServer.Services.ChatHistoryService",
        "ClaudeHomeServer.Services.FileService",
        "ClaudeHomeServer.Services.NotificationService",
        "ClaudeHomeServer.Services.NotificationStore",
        // Знаниевых типов в корне БОЛЬШЕ НЕТ: шаги 6 и 8 волны 3 перенесли
        // KnowledgeService/WorkspaceKnowledgeStore/KnowledgeBaseCatalogService/
        // ProjectKnowledgeSyncService/UserKnowledgeCascade/KnowledgeAccess в
        // `Services.Knowledge`, где их держит обычный per-vertical сторож.
        // Держать их тут «на всякий случай» нельзя: запись в ExcludedRootTypes
        // — это ИСКЛЮЧЕНИЕ из проверки, то есть ровно тот слепой пятак, ради
        // закрытия которого сторож и заводился (находка ревью 50f3b849).
    };

    [Fact]
    public void RootServices_НеСсылаетсяНаПодсистемныеВертикали()
    {
        // Сторож смотрит типы только в root-неймспейсе `ClaudeHomeServer.Services`
        // (вертикали типа `ClaudeHomeServer.Services.Video` — не его забота,
        // их проверяет SubsystemBoundaryTests). После выделения Core часть root-типов
        // (например, SsrfGuard) живёт в ClaudeHomeServer.Core.dll — без перебора
        // ВСЕХ ClaudeHomeServer.* сборок страж видит только Main и пропускает
        // нарушения в Core. Главная сборка `ClaudeHomeServer` (имя без суффикса —
        // этап 0/1 ещё не вынес вертикали) тоже входит в выборку. Тестовая сборка
        // `ClaudeHomeServer.Tests` исключена: её `namespace` имеет префикс
        // `ClaudeHomeServer.Tests.*`, не путается с `ClaudeHomeServer.Services.*`,
        // но ловить в ней root-типы тоже нечего.
        var assemblies = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a =>
            {
                var name = a.GetName().Name;
                return name is not null
                    && (name == "ClaudeHomeServer" || name.StartsWith("ClaudeHomeServer.", StringComparison.Ordinal))
                    && !name.EndsWith(".Tests", StringComparison.Ordinal);
            })
            .ToList();

        // Защита от вакуумного прохода: если фильтр сборок или порядок загрузки сломается,
        // набор станет пустым и сторож пройдёт зелёным, ничего не проверив (доказано мутацией
        // в ревью 23d353d7: без `name == "ClaudeHomeServer"` — 17/17 зелёных при нуле типов).
        assemblies.Should().HaveCountGreaterThanOrEqualTo(2,
            "сторож должен видеть минимум ClaudeHomeServer и ClaudeHomeServer.Core");

        // Только top-level root: Namespace строго "ClaudeHomeServer.Services", без nested
        // (`Foo+Bar` — часть родительского типа, проверяются через него) и без
        // compiler-generated типов (async state-машины `Foo+<Method>d__N`, лямбды
        // `<>c__DisplayClass*`, кэши для анонимных типов и т.п.). У таких типов
        // `Name` содержит `<` — это устойчивый маркер C#-компилятора.
        var rootTypes = assemblies.SelectMany(a => a.GetTypes())
            .Where(t => t.Namespace == "ClaudeHomeServer.Services")
            .Where(t => !(t.FullName?.Contains('+') ?? false))
            .Where(t => !(t.Name?.Contains('<') ?? false))
            .Where(t => !ExcludedRootTypes.Contains(t.FullName ?? string.Empty))
            .ToList();

        rootTypes.Should().NotBeEmpty(
            "в корне ClaudeHomeServer.Services должны найтись типы (ориентир замера — ~95 после волны 4) — " +
            "иначе проверка границ корня ничего не проверяет");

        var violations = new List<string>();

        foreach (var type in rootTypes)
        {
            var seen = new HashSet<(string, string)>();

            // Сначала рефлексия (поля/конструкторы/сигнатуры), затем IL-скан тел
            // методов. Обход nested-типов обязателен: без него сторож видит 4 из 7
            // известных швов (docs/research/il-boundary-scan-2026-09.md, раздел про слепые пятна).
            foreach (var referenced in CollectReferencedTypes(type)
                .Concat(IlScanReferencedTypes(type)))
            {
                if (referenced.Namespace is null) continue; // Безымянный namespace — не Services.*.

                // Self-ссылка: тип ссылается сам на себя.
                if (referenced == type) continue;

                // Self-ссылка на nested-тип, объявленный внутри самого класса
                // (DeclaringType идёт по цепочке вложенности, и для `Foo+Bar` он
                // равен `Foo`, для `Foo+Bar+Baz` — `Foo+Bar`; нам нужен только
                // прямой ребёнок проверяемого типа).
                if (referenced.IsNested && referenced.DeclaringType == type) continue;

                // Допустимая ссылка на «спинку» (System.*, Microsoft.*, Models,
                // Services.Http/Composition/Mcp) — единая для всех вертикалей
                // таблица SharedAllowedPrefixes из SubsystemBoundaryTests.
                if (IsSharedAllowed(referenced)) continue;

                // Допустимая ссылка на инфраструктурную под-вертикаль
                // (RootAllowedSubVerticalPrefixes). По аналогии с per-vertical
                // AllowedNamespacePrefixes — здесь «спинка» root-слоя.
                if (IsRootAllowedSubVertical(referenced)) continue;

                // Допустимая ссылка на конкретный тип из под-вертикали, открытый
                // точечно (RootAllowedExactTypes). Закрывает щель, которая раньше
                // требовала открывать префикс ради одного типа: теперь сторож
                // видит «root ссылается на ещё один тип из Services.CodeGraph» и
                // требует явного расширения allow-list (повод выделить шов).
                if (IsRootAllowedExactType(referenced)) continue;

                // Backbone root-типы: defense-in-depth. Peer-root ссылки
                // (`SessionManager`, `TaskManager` и т.п.) уже отсекаются правилом
                // ниже, но если правило однажды сломается — ExcludedRootTypes держит
                // гейт узким.
                if (ExcludedRootTypes.Contains(referenced.FullName ?? string.Empty)) continue;

                // Правило: ловим ТОЛЬКО выход в подсистемные вертикали. Ссылка на
                // peer-root тип (тот же `ClaudeHomeServer.Services`, без поддерева)
                // — общая инфраструктура, не нарушение. Ссылка на `Hubs.*` /
                // `Protocol.*` / `Controllers.*` / third-party — тоже не нарушение
                // (это другая «спинка», не вертикаль).
                bool isSubVertical = referenced.Namespace.StartsWith(
                    "ClaudeHomeServer.Services.", StringComparison.Ordinal);
                bool isPeerRoot = referenced.Namespace == type.Namespace;
                if (!isSubVertical || isPeerRoot) continue;

                seen.Add((type.FullName ?? type.Name, referenced.FullName ?? referenced.Name));
            }

            foreach (var (owner, forbidden) in seen)
            {
                violations.Add(
                    $"{owner} ссылается на {forbidden} " +
                    "из подсистемной вертикали (root-сторож: root-классы не должны зависеть от Services.*)");
            }
        }

        violations.Should().BeEmpty(
            "типы из корня ClaudeHomeServer.Services должны ссылаться только на спинку " +
            "(System.*, Microsoft.*, Models, Services.Http/Composition/Mcp, Protocol) или на другие " +
            "top-level root-типы. Любая ссылка на подсистемные вертикали " +
            "(Services.Knowledge, Services.Llm, Services.Dossiers и т.д.) — нарушение " +
            "архитектурного правила (см. CLAUDE.md/ADR-014). " +
            "Найденные нарушения:\n" +
            string.Join("\n", violations));
    }

    /// <summary>Типы, упомянутые в телах методов (включая nested-типы): статические
    /// вызовы, <c>sp.GetRequiredService&lt;T&gt;()</c>, generic-аргументы инстанцированных
    /// методов. Идёт через общий <see cref="BoundaryIlScanner.CollectAllReferencedTypes"/> —
    /// единую точку сбора для сторожей и регрессии, чтобы сломать обход nested-типов
    /// в одном месте и сразу покраснели ОБЕ проверки.</summary>
    private static IEnumerable<Type> IlScanReferencedTypes(Type type)
    {
        foreach (var t in BoundaryIlScanner.CollectAllReferencedTypes(type))
            yield return t;
    }

    private static bool IsSharedAllowed(Type type)
    {
        var ns = type.Namespace;
        if (ns is null) return true;

        // Сборка-спинка (см. CoreAssemblyName в SubsystemBoundaryTests): JsonFileStore/
        // SsrfGuard/PermissionModeGuard/TeamProtocolMarkers живут в `Core.dll`, но их
        // namespace = `ClaudeHomeServer.Services` (root). Без assembly-проверки они бы
        // считались «root-типами с внешними ссылками» и ловились как нарушения.
        if (type.Assembly.GetName().Name == CoreAssemblyName) return true;

        foreach (var prefix in SharedAllowedPrefixes)
        {
            if (ns == prefix
                || ns.StartsWith(prefix + ".", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsRootAllowedSubVertical(Type type)
    {
        var ns = type.Namespace;
        if (ns is null) return false;

        foreach (var prefix in RootAllowedSubVerticalPrefixes)
        {
            if (ns == prefix
                || ns.StartsWith(prefix + ".", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsRootAllowedExactType(Type type)
    {
        return RootAllowedExactTypes.Contains(type.FullName ?? string.Empty);
    }

    /// <summary>
    /// Единственный путь сбора типов: <see cref="BoundaryIlScanner.CollectAllReferencedTypes"/>.
    /// Прежде у сторожа было два независимых пути — IL-скан и собственная рефлексия полей,
    /// и подмена вызова сканера в коде сторожа оставляла все тесты зелёными. Теперь оба
    /// идут через ту же функцию: подмена реализации сканера роняет весь гейт единым
    /// движением, в том числе регрессию <see cref="IlBoundaryRegressionTests"/>.
    /// </summary>
    private static IEnumerable<Type> CollectReferencedTypes(Type type) =>
        BoundaryIlScanner.CollectAllReferencedTypes(type);

}