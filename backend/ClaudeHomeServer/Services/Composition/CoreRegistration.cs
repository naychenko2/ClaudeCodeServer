using ClaudeHomeServer.Services.Auth;
using ClaudeHomeServer.Services.Composition.Llm;
using ClaudeHomeServer.Services.Git;

namespace ClaudeHomeServer.Services.Composition;

// Ядро композиции Main (S6-2): то, что является самой спиной — вход и пользователи,
// среда исполнения процессов пользователя, реестр проектов, сферы и журнал событий
// проекта, домашние папки владельца, а также сервисы панелей расхода и медиа-журнала
// (это не вертикали: их читают сразу несколько сторон). Ничего из этого не принадлежит
// одной вертикали и не является чужим швом: перенос в `Register` подсистемы дал бы
// обратную ссылку вертикаль → Main (ADR-014), поэтому ядро живёт здесь.
//
// Узкие швы, чей корень стоит в этом же ряду (IUserStore, IProjectManager,
// IProjectRootLookup, IHomePathResolver, ISphereDirectory, IProjectEventLogService,
// ITierModelResolver), зарегистрированы рядом со своими корнями — как и было в
// Program.cs: форвардер без своего корня читается потерянно. Швы ЧИСТЫХ корней
// (корень в другой вертикали или в другом ряду) — в `CoreSeamsGlue.cs`.
//
// Ядро сессий и чатов отсюда заберёт S3 в `Services/Sessions/SessionCoreRegistration.cs`
// (тот же приём, что с `AddChatContext` в Core). Порядок и состав регистраций — ровно
// те, что были в Program.cs: relative порядок хостед-сервисов сохранён.
public static class CoreRegistration
{
    public static IServiceCollection AddCore(this IServiceCollection services, IConfiguration config)
    {
        // Файл NT-хэшей для NTLM WebDAV (gss-ntlmssp, вне data/); пишет его только UserStore
        services.AddSingleton<ClaudeHomeServer.WebDav.NtlmUserFile>();
        services.AddSingleton<UserStore>();
        services.AddSingleton<IForgejoAccountStore>(sp => sp.GetRequiredService<UserStore>());
        // Этап 5, волна E: узкие Core-швы для выноса Notes (NotesKnowledgeService → UserStore
        // ради User.Username для имени Dify-датасета). Полный UserStore в Main, Notes видит
        // только IUserStore (см. Core/Services/IUserStore.cs).
        // Ф5: явный класс-адаптер UserStoreAdapter (вместо фабрики-синонима) —
        // точка для подмены в тестах и формализация, что это узкая проекция, а не весь UserStore.
        services.AddSingleton<IUserStore, UserStoreAdapter>();
        // Драйверы среды исполнения процессов пользователей (local / docker-песочница)
        services.AddSingleton<ClaudeHomeServer.Services.Execution.SandboxManager>();
        // Канал устройств и шлюз хода (ADR-016) — ленивым резолвом: прямая зависимость замыкала граф синглтонов
        services.AddSingleton<ClaudeHomeServer.Services.Execution.ILauncherFactory>(sp =>
            new ClaudeHomeServer.Services.Execution.LauncherFactory(
                sp.GetRequiredService<IUserStore>(),
                sp.GetRequiredService<ClaudeHomeServer.Services.Execution.SandboxManager>(),
                () => sp.GetService<ClaudeHomeServer.Services.Execution.IDeviceExecChannel>(),
                () => sp.GetService<ClaudeHomeServer.Services.Execution.IDeviceTurnGateway>(),
                () => sp.GetService<ClaudeHomeServer.Services.Execution.IHumanTurnStop>()));
        // «Стоп» из трея рук = прерывание хода человеком, тем же путём, что веб-«Стоп»
        services.AddSingleton<ClaudeHomeServer.Services.Execution.IHumanTurnStop,
            ClaudeHomeServer.Services.Composition.HumanTurnStop>();
        // Узкий шов пула preview-портов песочницы для вертикали ProjectServices
        // (Этап 5, волна C, шаг 2): DevServerService в отдельной сборке
        // получает только диапазон, всё остальное в SandboxManager остаётся
        // инкапсулировано в Execution/Main.
        services.AddSingleton<ClaudeHomeServer.Services.Execution.ISandboxPortRange,
            ClaudeHomeServer.Services.Execution.SandboxPortRangeAdapter>();
        // Шов записи фона/цвета проекта для вертикали Backgrounds (Этап 5, волна C,
        // шаг 2): ProjectBackgroundService в отдельной сборке пишет Background/Color
        // через IProjectBackgroundWriter, форвардер сидит рядом с ProjectManager.
        services.AddSingleton<ClaudeHomeServer.Services.IProjectBackgroundWriter,
            ClaudeHomeServer.Services.ProjectBackgroundWriterAdapter>();
        // Шов миграции значка для вертикали ProjectIcons (Этап 5, волна C, шаг 2).
        services.AddSingleton<ClaudeHomeServer.Services.IProjectIconMigrator,
            ClaudeHomeServer.Services.ProjectIconMigratorAdapter>();
        // Шов «снимок data перед необратимой операцией» для вертикали ProjectIcons
        // (Этап 5, волна C, шаг 2); формализует бывшую полумеру (комментарий
        // `ProjectIconMigration.cs:78-84`) — теперь обязательный шов.
        services.AddSingleton<ClaudeHomeServer.Services.IDataBackupService,
            ClaudeHomeServer.Services.Backup.DataBackupServiceAdapter>();
        services.AddSingleton<JwtService>();
        services.AddSingleton<FeatureFlagService>();
        services.AddSingleton<AppSettingsService>();
        // Кэш ответа /api/usage (перф-3 B6): лёгкий scope=limits отдаётся из кэша, пока не изменились
        // входы (версии снимков/пула/опроса + mtime тарифа) — см. UsageResponseCache.
        services.AddSingleton<UsageResponseCache>();
        // История медиа-генераций (ADR-030, шаг 3): индекс 7 дней (читает журнал, получает события
        // от ComfyRunHistory через IMediaHistoryEventSink) и кэш миниатюр. Флаг/роль проверяет контроллер
        services.AddSingleton<ClaudeHomeServer.Services.MediaHistory.MediaHistoryIndex>();
        services.AddSingleton<ClaudeHomeServer.Services.MediaHistory.MediaHistoryThumbService>();
        // ComfyRunHistory (Images) резолвит наблюдателя лениво: тот же экземпляр индекса
        services.AddSingleton<ClaudeHomeServer.Services.Media.IMediaHistoryEventSink>(sp => sp.GetRequiredService<ClaudeHomeServer.Services.MediaHistory.MediaHistoryIndex>());
        // Рубильник «медиа-панель для обычных пользователей» (ADR-030, 2026-10-18): секция
        // LocalModelStats, по умолчанию true; false — не-админ получает 404 на медиа/историю.
        // Связывание — единственная точка чтения (не инлайн config.GetValue в контроллерах).
        services.Configure<ClaudeHomeServer.Models.LocalModelStatsMediaOptions>(config.GetSection(ClaudeHomeServer.Models.LocalModelStatsMediaOptions.Section));
        // AppSettingsService реализует ITierModelResolver (Core-шов для слота модели).
        // DI сама по себе не резолвит concrete→interface — пробрасываем вручную, тот
        // же singleton-экземпляр. Отдельного адаптера не нужно: класс уже реализует шов.
        // IModelCatalog — Core-шов для каталога моделей (волна 1 выноса Llm). Реализация
        // ModelCatalogAdapter мапит nested ModelInfo (Main) в ModelCatalogEntry (Core).
        services.AddSingleton<ITierModelResolver>(sp => sp.GetRequiredService<AppSettingsService>());
        services.AddSingleton<IModelCatalog, ModelCatalogAdapter>();
        // ISubscriptionAlertNotifier — спинный адаптер поверх NotificationService (волна 6 выноса
        // Llm). Регистрация живёт здесь, а не в LlmSubsystem: реализация тянет спинной
        // NotificationService, и держать резолв в DI вертикали было бы запрещённой границей.
        // Прецедент уборки Git (CLAUDE.md, «возврат регистрации спинных реакторов»).
        services.AddSingleton<ISubscriptionAlertNotifier, SubscriptionAlertNotifier>(sp =>
            new SubscriptionAlertNotifier(
                sp.GetRequiredService<NotificationService>(),
                sp.GetRequiredService<IUserStore>()));
        // UserModelTierResolver (слоты моделей) — DI в подсистеме `LlmSubsystem`
        // (шаг 0 волны 4, см. LlmSubsystem.cs).
        services.AddSingleton<UserHomeResolver>();
        // Форвардер, а не вторая регистрация: иначе второй экземпляр `UserHomeResolver` со своим
        // кешем домашних папок (см. DuplicateSingletonRegistrationTests).
        services.AddSingleton<IHomePathResolver>(sp => sp.GetRequiredService<UserHomeResolver>());
        services.AddSingleton<ProjectManager>();
        // Этап 5, волна E: узкий Core-шов IProjectManager для выноса Notes (см.
        // Core/Services/IProjectManager.cs). Полный ProjectManager в Main, Notes видит
        // только GetById/GetByOwner/GetAll — этого хватает для NoteTaskSync/NoteExpiry/NotesService.
        // Ф5: явный класс-адаптер ProjectManagerAdapter (вместо фабрики-синонима) —
        // точка для подмены в тестах и формализация, что это узкая проекция, а не весь ProjectManager.
        services.AddSingleton<IProjectManager, ProjectManagerAdapter>();
        // Шов для вертикалей (Этап 3, волна 1): вместо прямой зависимости от ProjectManager
        // вертикали берут узкий контракт IProjectRootLookup. Реализация — тонкая обёртка
        // над ProjectManager в `Services/ProjectRootLookup`, живёт здесь же в Main.
        services.AddSingleton<IProjectRootLookup, ProjectRootLookup>();
        // Швы для Skills (волна 4C, шаг 1 — финальный): вместо прямой зависимости
        // SkillSuggestService от PersonaManager/ProjectManager — узкие контракты
        // на резолв персоны+Skill-биндингов и проекта по id. Реализации — тонкие
        // обёртки в Main, контракты живут в Core.
        services.AddSingleton<IPersonaSkillBindingLookup, PersonaSkillBindingLookup>();
        services.AddSingleton<IProjectSummaryLookup, ProjectSummaryLookup>();
        // Шов для Tts (Этап 3, вынос Tts): вместо прямой зависимости VoiceResolver от
        // PersonaManager — узкий контракт на голос персоны. Реализация — тонкая обёртка
        // в Main (`Services/Composition/PersonaVoiceLookup`), контракт живёт в Core.
        services.AddSingleton<IPersonaVoiceLookup, PersonaVoiceLookup>();
        // Шов для Llm (Этап 5, волна 3): вместо прямой зависимости ClaudeSession/
        // LlmSessionAdapterFactory от WorkspaceKnowledgeStore — узкий контракт на чтение
        // знаний рабочего дерева (dataset id + теги документов), нужное ходу. Реализация —
        // тонкая обёртка в Main (`Services/Composition/WorkspaceDatasetLookup`), контракт в Core.
        services.AddSingleton<IWorkspaceDatasetLookup, WorkspaceDatasetLookup>();
        // Швы для Docs/Changelog (Этап 5, ярус 1, волна A): вместо прямой зависимости
        // DocsIndexService/ChangelogService от FileService — узкие контракты на файловые
        // операции и чтение git-лога. Реализации — тонкие обёртки в `Services/Composition`,
        // контракты живут в Core. Адаптер файлов идёт через FileService (а не пишет сам),
        // чтобы синк базы знаний продолжал видеть правки документов.
        services.AddSingleton<IProjectFileGateway, ProjectFileGateway>();
        // Швы для Modules и ProjectServices (Этап 5, волна C, шаг 1): вместо прямой
        // зависимости от JwtService — узкие контракты на проверку пользовательского
        // и preview-токенов. Auth уже без связи: у AdminByStore.cs JwtService упомянут
        // только в комментарии. Адаптер в `Services/JwtValidatorGateway` реализует оба
        // интерфейса и идёт через `JwtService` — разделение на стороне потребителя.
        services.AddSingleton<JwtValidatorGateway>();
        // Форвардеры, а не вторая регистрация: иначе второй экземпляр `JwtValidatorGateway` со
        // своим кешем/состоянием (см. DuplicateSingletonRegistrationTests).
        services.AddSingleton<IUserTokenValidator>(sp => sp.GetRequiredService<JwtValidatorGateway>());
        services.AddSingleton<IPreviewTokenValidator>(sp => sp.GetRequiredService<JwtValidatorGateway>());
        // Шов шлюза MCP (ADR-016): адрес бэкенда и сервисный JWT владельца — теми же функциями,
        // что у конфига серверного хода.
        services.AddSingleton<IMcpBackendAccess, ClaudeHomeServer.Services.Composition.McpBackendAccess>();
        // Шов для Modules (Этап 5, волна C, шаг 1б): вместо прямой зависимости от
        // FeatureFlagService — узкий контракт на проверку одного флага. Адаптер в
        // `Services/FeatureFlagGateway` идёт через `FeatureFlagService` — Modules
        // получает только `IsEnabled`, без каталога определений и записи.
        services.AddSingleton<IModuleFeatureFlagReader, FeatureFlagGateway>();
        services.AddSingleton<ICommitLogReader, CommitLogReader>();
        // Узкий шов инспекции коммитов (Этап 5, волна 2, разрез Dossiers↔Git): потребитель —
        // Dossiers (DossierCaptureService/DossierRecallService), 5 методов инспекции коммитов.
        // Реализация — `GitCommitInspector` (Main, тонкий форвардер на `GitService` из Core.
        // Git — вынесенная вертикаль с собственным синглтоном; форвардер через тот же
        // `sp.GetRequiredService<GitService>()`, что и `IGitRefSnapshotStore` ниже в GitSubsystem.
        services.AddSingleton<ClaudeHomeServer.Services.Git.IGitCommitInspector,
            ClaudeHomeServer.Services.Git.GitCommitInspector>();
        // CodeGraph: граф зависимостей кода — DI в подсистеме `CodeGraphSubsystem`
        // (волна 2, первая с пост-билд фазой: регистрирует языковые провайдеры в ConfigureApp).
        // Узкий шов инспекции графа (Этап 5, волна 2, разрез Dossiers↔CodeGraph): потребитель —
        // Dossiers, два метода (GetSnapshotAsync + StartRebuildIfIdle). Форвардер через тот же
        // синглтон `CodeGraphService`, что и подсистем.
        services.AddSingleton<ClaudeHomeServer.Services.CodeGraph.ICodeGraphInspector,
            ClaudeHomeServer.Services.CodeGraph.CodeGraphInspector>();
        // Шов снимка графа для раздела «Архитектура» (Viaduct 10.1, разрез Architecture↔CodeGraph):
        // регистрация форвардера под гейтом CodeGraph — в Composition/ArchitectureCodeGraphGlue.cs (S6-1).
        services.AddArchitectureCodeSource(config);
        services.AddSingleton<SphereManager>();
        // Форвардер: единый стор сфер; PersonaManager берёт зону персон через ISphereDirectory
        services.AddSingleton<ClaudeHomeServer.Services.Spheres.ISphereDirectory>(sp => sp.GetRequiredService<SphereManager>());
        services.AddSingleton<ProjectEventLogService>();
        // Этап 5, волна E: узкий Core-шов IProjectEventLogService для выноса Notes (NotesService
        // пишет ProjectEventTypes.NoteChanged при мутациях заметок). Полный сервис в Main,
        // Notes видит только Append.
        services.AddSingleton<IProjectEventLogService>(sp => sp.GetRequiredService<ProjectEventLogService>());
        return services;
    }
}
