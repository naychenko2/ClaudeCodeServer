using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using ClaudeHomeServer.Hubs;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Auth;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Composition.Notifications;
using ClaudeHomeServer.Services.Knowledge;
using ClaudeHomeServer.Services.Desktop;
using ClaudeHomeServer.Services.Execution;
using ClaudeHomeServer.Services.Git;
using ClaudeHomeServer.Services.Team;
using ClaudeHomeServer.Services.Http;
using ClaudeHomeServer.Services.Mcp;
using ClaudeHomeServer.Services.ProjectServices;
using ClaudeHomeServer.Services.Terminal;
using ClaudeHomeServer.Services.TriggerSources;
using ClaudeHomeServer.Services.Modules;
using ClaudeHomeServer.Services.Turn;
using ClaudeHomeServer.Services.Video;
using ClaudeHomeServer.Telemetry;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.StaticFiles;
using Yarp.ReverseProxy.Forwarder;

JwtSecurityTokenHandler.DefaultMapInboundClaims = false;

// Кириллица в stdout: без явной кодировки .NET на Windows пишет в OEM (866), и потребители
// вывода (раннер-трей, docker logs) получают кашу. Единый UTF-8 — при любом способе запуска.
try { Console.OutputEncoding = System.Text.Encoding.UTF8; }
catch { /* нет консоли/права — не критично, останется дефолт */ }

// Режимы обслуживания (--backup / --restore / --inspect) отрабатывают и завершаются,
// не поднимая веб-приложение. Обязательно ДО ProcessRegistry.Initialize ниже: тот бьёт
// «сирот» по pid-файлу, а он общий с работающим сервером.
if (ClaudeHomeServer.Services.Backup.BackupCli.TryHandle(args)) return;

// ADR-015 §8 этап 2: dryRun-прогон синка профилей. Работает без поднятого веб-приложения:
// обходит подпапки _profilesDir, делает усыновление (если манифеста нет) и считает
// "удалил бы" по mirror-зонам. Удалений не производит — только печатает сводку.
// Рубильник Claude:ProfileSync:Mirror (off | dryRun | on) управляет поведением;
// в dryRun запускать через env-переменную Claude__ProfileSync__Mirror=dryRun.
if (ClaudeHomeServer.Services.Llm.ProfileSyncCli.TryHandle(args)) return;

var builder = WebApplication.CreateBuilder(args);

// Локальные машинно-специфичные переопределения (пути, URL, секреты).
// Файл вне git (.gitignore), у каждого свой. Грузится последним — переопределяет
// appsettings.json и appsettings.{Environment}.json. Необязателен: нет файла — берутся
// дефолты из git (важно, чтобы у брата ничего не отъехало).
// В Testing (TestWebApplicationFactory) файл НЕ подключаем: иначе боевые токены
// подписок, Dify:ApiKey и ключи LlmProviders разработчика протекают в тестовые хосты,
// а прогрев подписок запускает настоящие claude.exe с боевым OAuth-токеном.
if (!builder.Environment.IsEnvironment("Testing"))
    builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);

// Инспекционная копия: её параметры обязаны победить Local.json, который только что лёг
// поверх командной строки. Иначе копия открыла бы боевой DataPath и боевой Dify.
var inspectionOverrides = ClaudeHomeServer.Services.Backup.BackupCli.InspectionOverrides(args);
if (inspectionOverrides is not null) builder.Configuration.AddInMemoryCollection(inspectionOverrides);
var inspectionMode = builder.Configuration.GetValue<bool>("InspectionMode");

// Таймстемп в консольном логе (I11): обёртка Console.Out/Error добавляет UTC-время в начало
// каждой строки — и для ILogger, и для голых Console.WriteLine. Здесь, чтобы Local.json тоже
// мог переопределить формат. Ключ Diagnostics:ConsoleTimestampFormat — НЕ под Logging:Console:
// то имя (Logging:Console:TimestampFormat) биндится во фреймворчный ConsoleLoggerOptions.
// TimestampFormat, и форматтер ILogger печатал второй таймстемп (с липовым Z — локальное время).
// Пусто — без обёртки.
TimestampedConsoleWriter.Enable(builder.Configuration["Diagnostics:ConsoleTimestampFormat"]);

// Файловый лог инстанса (data/logs/server-YYYYMMDD.log, stderr-зеркало, дневная ротация):
// вне Development/Testing включён по умолчанию — боевой инстанс обязан оставлять след
// обрывов ходов и смертей процессов CLI независимо от способа запуска (трей/Runner/консоль).
// Выключается Logging:File:Enabled=false (см. Services/Diagnostics/FileLog.cs).
ClaudeHomeServer.Services.Diagnostics.FileLog.Attach(builder.Configuration, builder.Environment);

// Зачистка процессов-сирот от предыдущего запуска сервера (краш/форс-килл):
// на Windows дочерние node-процессы MCP-серверов не умирают при смерти родителя —
// без этого они копятся и съедают гигабайты памяти. Должно быть ДО первого Process.Start.
// В инспекционной копии пропускаем: pid-файл лежит рядом с exe (а не в DataPath), то есть
// принадлежит БОЕВОМУ серверу — чистка убила бы его MCP-серверы и идущие ходы.
if (!inspectionMode) ProcessRegistry.Initialize();

// Признак «сервер работает на этом каталоге data»: держится весь uptime и проверяется
// восстановлением. Живой сервер во время restore продолжил бы писать в перемещённый
// каталог и пересоздал бы data под собой.
var instanceLock = ClaudeHomeServer.Services.Backup.InstanceLock.TryAcquireInstance(
    Path.GetDirectoryName(Path.GetFullPath(builder.Configuration["DataPath"]
        ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json")))!);

// Токен подписки claude CLI (`claude setup-token`) можно держать в appsettings.Local.json —
// удобнее, чем переменная окружения: IDE наследует окружение от родителя (explorer/Toolbox),
// и свежий setx там не виден, пока не перезайдёшь в систему. Кладём его в env процесса, откуда
// его унаследуют все запуски claude.exe (ClaudeSession, OneShotClaudeRunner, ModelCatalogService).
// Явная переменная окружения имеет приоритет — конфиг её не перетирает (важно для docker).
const string OAuthTokenVar = "CLAUDE_CODE_OAUTH_TOKEN";
if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(OAuthTokenVar))
    && builder.Configuration["Claude:OAuthToken"] is { Length: > 0 } oauthToken
    && !string.IsNullOrWhiteSpace(oauthToken))
{
    Environment.SetEnvironmentVariable(OAuthTokenVar, oauthToken);
    // Значение — секрет, печатаем только факт (иначе токен утечёт в логи IDE/CI)
    Console.WriteLine($"[Claude] Токен подписки взят из конфига Claude:OAuthToken ({oauthToken.Length} симв.)");
}

// Последний рубеж пайплайна: исключение, не пойманное по дороге, логируется структурно
// (маршрут, тип, точка броска) и превращается в 500 ProblemDetails — см.
// Services/Http/UnhandledExceptionHandler.cs. AddProblemDetails() нужен самому middleware:
// без зарегистрированного IProblemDetailsService оно отказывается стартовать. На готовые
// ответы контроллеров это не влияет — тело problem+json пишут только StatusCodePages и
// обработчик исключений, а StatusCodePages здесь не подключён.
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ClaudeHomeServer.Services.Http.UnhandledExceptionHandler>();

builder.Services.AddControllers()
    .AddJsonOptions(o =>
        o.JsonSerializerOptions.Converters.Add(
            new JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase)));

// Hosted-сервисы: в Testing-среде (TestWebApplicationFactory) НЕ регистрируются без
// явного флага Testing:EnableHostedServices=true — 17 фоновых циклов на каждый из
// ~27 бутов тестовых хостов только жгли время прогона и порождали фоновую возню
// (диагностика 2026-07-30). Singleton-регистрации остаются — лениво достаются из DI.
// Хелперы живут в Services/Composition/SubsystemHostingExtensions.cs — те же правила
// гейта, чтобы подсистемы и main-Program регистрировали hosted одинаково.

builder.Services.AddSignalR(o =>
    {
        // Смягчаем разрывы у клиентов с дрожащим каналом (мобильные, засыпающие вкладки):
        // сервер закрывает соединение, если не слышал клиента дольше ClientTimeoutInterval.
        // Дефолт 30 с рвал соединение при коротком замолкании — поднимаем до 60 с
        // (должен быть ≥ 2× клиентского KeepAlive). KeepAlive 15 с — пинги сервера клиенту.
        o.ClientTimeoutInterval = TimeSpan.FromSeconds(60);
        o.KeepAliveInterval = TimeSpan.FromSeconds(15);
        // Медленное рукопожатие на плохом канале не должно ронять подключение
        o.HandshakeTimeout = TimeSpan.FromSeconds(30);
    })
    .AddJsonProtocol(o =>
        o.PayloadSerializerOptions.Converters.Add(
            new JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase)));

// Observability: OTel SDK (traces + metrics) с two-mode конфигурацией.
// Конфиг через секцию Telemetry в appsettings*.json. См. docs/observability/overview.md.
builder.Services.AddObservability(builder.Configuration);

builder.Services.AddSingleton<UserStore>();
builder.Services.AddSingleton<IForgejoAccountStore>(sp => sp.GetRequiredService<UserStore>());
// Этап 5, волна E: узкие Core-швы для выноса Notes (NotesKnowledgeService → UserStore
// ради User.Username для имени Dify-датасета). Полный UserStore в Main, Notes видит
// только IUserStore (см. Core/Services/IUserStore.cs).
// Ф5: явный класс-адаптер UserStoreAdapter (вместо фабрики-синонима) —
// точка для подмены в тестах и формализация, что это узкая проекция, а не весь UserStore.
builder.Services.AddSingleton<IUserStore, UserStoreAdapter>();
// Драйверы среды исполнения процессов пользователей (local / docker-песочница)
builder.Services.AddSingleton<ClaudeHomeServer.Services.Execution.SandboxManager>();
builder.Services.AddSingleton<ClaudeHomeServer.Services.Execution.ILauncherFactory,
    ClaudeHomeServer.Services.Execution.LauncherFactory>();
// Узкий шов пула preview-портов песочницы для вертикали ProjectServices
// (Этап 5, волна C, шаг 2): DevServerService в отдельной сборке
// получает только диапазон, всё остальное в SandboxManager остаётся
// инкапсулировано в Execution/Main.
builder.Services.AddSingleton<ClaudeHomeServer.Services.Execution.ISandboxPortRange,
    ClaudeHomeServer.Services.Execution.SandboxPortRangeAdapter>();
// Шов записи фона/цвета проекта для вертикали Backgrounds (Этап 5, волна C,
// шаг 2): ProjectBackgroundService в отдельной сборке пишет Background/Color
// через IProjectBackgroundWriter, форвардер сидит рядом с ProjectManager.
builder.Services.AddSingleton<ClaudeHomeServer.Services.IProjectBackgroundWriter,
    ClaudeHomeServer.Services.ProjectBackgroundWriterAdapter>();
// Шов миграции значка для вертикали ProjectIcons (Этап 5, волна C, шаг 2).
builder.Services.AddSingleton<ClaudeHomeServer.Services.IProjectIconMigrator,
    ClaudeHomeServer.Services.ProjectIconMigratorAdapter>();
// Шов «снимок data перед необратимой операцией» для вертикали ProjectIcons
// (Этап 5, волна C, шаг 2); формализует бывшую полумеру (комментарий
// `ProjectIconMigration.cs:78-84`) — теперь обязательный шов.
builder.Services.AddSingleton<ClaudeHomeServer.Services.IDataBackupService,
    ClaudeHomeServer.Services.Backup.DataBackupServiceAdapter>();
builder.Services.AddSingleton<JwtService>();
builder.Services.AddSingleton<FeatureFlagService>();
builder.Services.AddSingleton<AppSettingsService>();
// UserModelTierResolver (слоты моделей) — DI в подсистеме `LlmSubsystem`
// (шаг 0 волны 4, см. LlmSubsystem.cs).
builder.Services.AddSingleton<UserHomeResolver>();
builder.Services.AddSingleton<ProjectManager>();
// Этап 5, волна E: узкий Core-шов IProjectManager для выноса Notes (см.
// Core/Services/IProjectManager.cs). Полный ProjectManager в Main, Notes видит
// только GetById/GetByOwner/GetAll — этого хватает для NoteTaskSync/NoteExpiry/NotesService.
// Ф5: явный класс-адаптер ProjectManagerAdapter (вместо фабрики-синонима) —
// точка для подмены в тестах и формализация, что это узкая проекция, а не весь ProjectManager.
builder.Services.AddSingleton<IProjectManager, ProjectManagerAdapter>();
// Шов для вертикалей (Этап 3, волна 1): вместо прямой зависимости от ProjectManager
// вертикали берут узкий контракт IProjectRootLookup. Реализация — тонкая обёртка
// над ProjectManager в `Services/ProjectRootLookup`, живёт здесь же в Main.
builder.Services.AddSingleton<IProjectRootLookup, ProjectRootLookup>();
// Швы для Skills (волна 4C, шаг 1 — финальный): вместо прямой зависимости
// SkillSuggestService от PersonaManager/ProjectManager — узкие контракты
// на резолв персоны+Skill-биндингов и проекта по id. Реализации — тонкие
// обёртки в Main, контракты живут в Core.
builder.Services.AddSingleton<IPersonaSkillBindingLookup, PersonaSkillBindingLookup>();
builder.Services.AddSingleton<IProjectSummaryLookup, ProjectSummaryLookup>();
// Шов для Tts (Этап 3, вынос Tts): вместо прямой зависимости VoiceResolver от
// PersonaManager — узкий контракт на голос персоны. Реализация — тонкая обёртка
// в Main (`Services/Composition/PersonaVoiceLookup`), контракт живёт в Core.
builder.Services.AddSingleton<IPersonaVoiceLookup, PersonaVoiceLookup>();
// Швы для Docs/Changelog (Этап 5, ярус 1, волна A): вместо прямой зависимости
// DocsIndexService/ChangelogService от FileService — узкие контракты на файловые
// операции и чтение git-лога. Реализации — тонкие обёртки в `Services/Composition`,
// контракты живут в Core. Адаптер файлов идёт через FileService (а не пишет сам),
// чтобы синк базы знаний продолжал видеть правки документов.
builder.Services.AddSingleton<IProjectFileGateway, ProjectFileGateway>();
// Швы для Modules и ProjectServices (Этап 5, волна C, шаг 1): вместо прямой
// зависимости от JwtService — узкие контракты на проверку пользовательского
// и preview-токенов. Auth уже без связи: у AdminByStore.cs JwtService упомянут
// только в комментарии. Адаптер в `Services/JwtValidatorGateway` реализует оба
// интерфейса и идёт через `JwtService` — разделение на стороне потребителя.
builder.Services.AddSingleton<IUserTokenValidator, JwtValidatorGateway>();
builder.Services.AddSingleton<IPreviewTokenValidator, JwtValidatorGateway>();
// Шов для Modules (Этап 5, волна C, шаг 1б): вместо прямой зависимости от
// FeatureFlagService — узкий контракт на проверку одного флага. Адаптер в
// `Services/FeatureFlagGateway` идёт через `FeatureFlagService` — Modules
// получает только `IsEnabled`, без каталога определений и записи.
builder.Services.AddSingleton<IModuleFeatureFlagReader, FeatureFlagGateway>();
builder.Services.AddSingleton<ICommitLogReader, CommitLogReader>();
// CodeGraph: граф зависимостей кода — DI в подсистеме `CodeGraphSubsystem`
// (волна 2, первая с пост-билд фазой: регистрирует языковые провайдеры в ConfigureApp).
builder.Services.AddSingleton<ProjectGroupManager>();
builder.Services.AddSingleton<ProjectEventLogService>();
// Этап 5, волна E: узкий Core-шов IProjectEventLogService для выноса Notes (NotesService
// пишет ProjectEventTypes.NoteChanged при мутациях заметок). Полный сервис в Main,
// Notes видит только Append.
builder.Services.AddSingleton<IProjectEventLogService>(sp => sp.GetRequiredService<ProjectEventLogService>());
builder.Services.AddSingleton<PersonaManager>();
builder.Services.AddSingleton<PersonaPromptBuilder>();
// Память персон и команды (волна 3, шаг 4) — DI в подсистеме `MemorySubsystem`:
// PersonaMemoryService и TeamMemoryService регистрируются там же.
// Паспорта изменений (ADR-004) — DI в подсистеме `DossiersSubsystem`
// (волна 3, шаг 2): InstanceSecretsProvider/DossierStore/DossierCaptureState/
// DossierRecallService/DossierDiscussion{Store,Service}/DossierCaptureService (hosted)/
// DossierAutoExporter (singleton + hosted через AddGatedHostedFrom — подписки в
// StartAsync на тот же инстанс)/DossierAutoImporter (hosted). Форвардер
// IKnowledgeSyncParticipant → DossierStore в блоке Knowledge ниже — участник
// реконсайлера Dify, остаётся в Program.cs до выделения Knowledge (следующий шаг).
builder.Services.AddSingleton<PersonaBindingsService>();
// Этап 5, шаг 6: форвардер IPersonaServerToolGate → PersonaBindingsService — узкая часть
// контракта ServerToolEnabled (deny-only по Tool-привязке), нужная контрибьюторам
// секций промпта из чужих вертикалей (CodeGraph → codegraph). Без шва вертикаль
// CodeGraph тянула бы root Services напрямую — запрет архитектуры.
builder.Services.AddSingleton<IPersonaServerToolGate>(sp => sp.GetRequiredService<PersonaBindingsService>());
// Черновик персоны по промпту (one-shot LLM → JSON): переиспользуется ai/quick-create
// и страховкой онбординга «Применить итоги разговора». Stateless — singleton.
builder.Services.AddSingleton<ClaudeHomeServer.Services.Personas.PersonaDraftService>();
// Провижн авто-ассистента (фича default-personas-onboarding): заготовка «Ассистент»
// как дефолт при первом включении флага. Singleton — статический реестр семафоров.
builder.Services.AddSingleton<DefaultAssistantProvisioner>();
// Применение шаблонов прав специальности (тонкая прослойка над SpecialtySettingsStore,
// который переехал в Services.Llm — DI там же, см. LlmSubsystem.cs)
builder.Services.AddSingleton<SpecialtyTemplatesService>();
// Планирование режима «Командная реализация» (Э2): подбор координатора/планировщика,
// карточки кандидатов и структурный план
builder.Services.AddSingleton<TeamPlanningService>();
// Файловые сабагенты-персоны: генерация + синк .md-агентов
// Пул подписок с восстановлением пометок исчерпания из снапшотов usage после рестарта,
// активность аккаунта, сторож setup-токена, прогрев/идл-пинг и OAuth-опрос утилизации —
// переехали в Services.Llm (волна 4B, шаг 2). DI в подсистеме `LlmSubsystem`.
builder.Services.AddSingleton<PersonaAgentFileGenerator>();
builder.Services.AddSingleton<PersonaAgentFileSync>();
// Генерация картинок (иконка проекта, аватар персоны, фон проекта) — подключается
// подсистемой ImagesSubsystem вместе с прочими внутренними разделами ниже
// (`AddSubsystems(...)`). FalImageService регистрируется внутри как драйвер —
// отдельный AddSingleton дал бы второй экземпляр того же типа.
// Память персон и команды (волна 3, шаг 4) — DI в подсистеме `MemorySubsystem`:
// PersonaMemoryService/PersonaMemoryConsolidationService/PersonaMemoryAutolearnService/
// TeamMemoryService/TeamMemoryConsolidationService/TeamMemoryAutolearnService.
// Форвардеры `IKnowledgeSyncParticipant → {PersonaMemoryService, TeamMemoryService}`
// остаются ниже (Program.cs:~688-691) сознательно — кросс-вертикальный клей
// реконсайлера error-документов Dify, не собственность Memory.
// Разовый backfill дефолтных привязок существующим проектным персонам (файлы/заметки/знания)
builder.Services.AddGatedHostedService<PersonaProjectBindingsMigration>(builder.Configuration);
// Разовая переадресация закреплённых моделей GLM на действующий каталог (алиасы z.ai)
// — DI в подсистеме `LlmSubsystem` (шаг 0 волны 4, см. LlmSubsystem.cs).
// TaskManager/TaskAiService/BoardService/DailyBriefingService/TaskSchedulerService
// — DI в подсистеме `TasksSubsystem` (волна 4C, шаг 1).
builder.Services.AddSingleton<FileService>();
// Резолв контекста чата (фича chat-context): признак «не найден» считает одна точка
// для REST фронта и MCP-тула context_list
builder.Services.AddSingleton<SessionContextResolver>();
// Документация проекта (README + docs/) для панели «Доки»: индекс, связи, поиск.
// Кеш живёт внутри сервиса и ключуется корнем папки, поэтому singleton.
builder.Services.AddSingleton<ClaudeHomeServer.Services.Docs.DocsIndexService>();
// Применение пресета каркаса знакомства v2: только добавляет поверх живой папки,
// отчёт по каждому шагу; зависимости-синглтоны, сам тоже stateless-синглтон
builder.Services.AddSingleton<ProjectPresetService>();
// Документы AI: конвертация в Markdown (markitdown) + ИИ-помощь (суммари/выжимка/теги) на локальной модели
builder.Services.AddSingleton<ClaudeHomeServer.Services.Docs.MarkitdownService>();
builder.Services.AddSingleton<ClaudeHomeServer.Services.Docs.DocumentAiService>();
// Подсистема Git: GitService/GitServerService/GitAiService регистрируются в
// `ClaudeHomeServer.Git/Services/Git/GitSubsystem.cs` — вертикаль вынесена в отдельную
// сборку (Этап 3, 2026-09-08), сюда её приносит ProjectReference Main→Git. Реакторы
// GitAutoCommitService/CommitAttributionService — общая композиция спины, см. блок
// рядом с FileWatcherService (Этап 3, уборка Git).
builder.Services.AddSingleton<UnifiedSearchService>();
// Аналитика расхода токенов (Spend Analytics v2) — DI в подсистеме `SpendSubsystem`.
// Модельный слой (OneShotClaudeRunner / OllamaClient / LlamaServerClient / CloudCheapClient /
// LocalActionRouter / ModelAssignmentResolver / CheapTextRunner / LocalActionOverridesStore /
// LocalActionPresetService / FallbackSettingsStore / OllamaActionRankService и тихие HTTP-клиенты
// Ollama/LlamaServer) — DI в подсистеме `LlmSubsystem` (шаг 0 волны 4, см. LlmSubsystem.cs).
// Фон рабочего пространства проекта (ADR-008) и значок проекта (ADR-009) переехали
// в подсистемы `BackgroundsSubsystem` / `ProjectIconsSubsystem` (волна 2 внутренних
// подсистем): регистрации подключаются через `AddSubsystems(...)` ниже. Здесь остаются
// только общие клиенты и конфигурация, нужные вне подсистем (HTTP-клиенты fal/glif —
// общие с биллинг-сервисами, см. шапку `ImagesSubsystem`).
// Общий LLM-резолвер записи памяти (Mem0 ADD/UPDATE/DELETE/NOOP) — авто-путь обоих слоёв памяти.
// Живёт в `Services.Memory`, не в `Services.Llm` — отдельная вертикаль Memory,
// регистрация оставлена здесь сознательно.
builder.Services.AddSingleton<ClaudeHomeServer.Services.Memory.MemoryWriteResolver>();
// One-shot ответы персон от их лица (persona_ask из MCP персон)
builder.Services.AddSingleton<PersonaAskService>();
builder.Services.AddSingleton<SyncService>();
builder.Services.AddSingleton<FileWatcherService>();
// Реактор авто-commit (Project.GitAutoCommit после каждого хода Claude). Живёт в
// корне Services, не в Git-вертикали — это «реакция на ход» по прецеденту
// PersonaMemoryAutolearnService. Здесь, а не в GitSubsystem, потому что регистрации
// реакторов — общая композиция спины, а не домена Git.
builder.Services.AddGatedHostedService<GitAutoCommitService>(builder.Configuration);
// Детект коммита по сдвигу HEAD: помечает чатам зафиксированные пути
// (Session.CommittedFilePaths), чтобы атрибуция файлов чатам не врала после коммита.
// Аналогично GitAutoCommitService — реактор на горячем пути статуса, не домен Git.
builder.Services.AddSingleton<CommitAttributionService>();
builder.Services.AddSingleton<ConnectionDiagnostics>();
builder.Services.AddSingleton<ChatHistoryService>();
builder.Services.AddSingleton<PromptSnapshotStore>();
builder.Services.AddSingleton<PromptAuditService>();
// WorkspaceKnowledgeStore — DI в подсистеме `KnowledgeSubsystem`
// (волна 3, шаг 3): миграция из старых Project-записей — отдельный блок
// PostConfigure ниже (см. ограничения в шапке KnowledgeSubsystem.cs).
builder.Services.AddSingleton<FalCostService>();
builder.Services.AddSingleton<FalAccountService>();
builder.Services.AddSingleton<GlifAccountService>();
// UsageService — DI в подсистеме `LlmSubsystem` (волна 4B, шаг 2: переезд подписок и
// настроек специальностей к модели).
// Модельный слой (LlmProviderRegistry / ProviderBalanceService / ProviderHealthRegistry /
// ContextCapacityRegistry / IProviderBalanceService / FileChangeAttributor /
// ILlmSessionAdapterFactory / SubagentRunLog / TurnRunLog / EgressProbe) —
// DI в подсистеме `LlmSubsystem` (шаг 0 волны 4, см. LlmSubsystem.cs).
// Наблюдаемость вызовов продуктовых MCP-серверов (счётчики + последние сбои, только в памяти)
builder.Services.AddSingleton<ClaudeHomeServer.Services.Mcp.McpCallLog>();
// Личный реестр MCP-серверов владельца: записи без секретов (data/mcp-servers.json)
// и значения ключей/токенов отдельным стором (data/mcp-secrets.json — не едет в облачный архив)
builder.Services.AddSingleton<ClaudeHomeServer.Services.Mcp.McpSecretStore>();
builder.Services.AddSingleton<ClaudeHomeServer.Services.Mcp.McpRegistry>();
// Последний известный статус серверов (data/mcp-status.json — в архив не едет) и разовая
// проба по кнопке: фонового поллинга нет, наблюдение приходит из system/init каждого хода
builder.Services.AddSingleton<ClaudeHomeServer.Services.Mcp.McpStatusStore>();
// Вход в чужой сервер по OAuth: discovery, регистрация клиента, обмен кода и обновление
// токена перед ходом (pending-записи входа живут только в памяти — отсюда singleton)
builder.Services.AddSingleton<ClaudeHomeServer.Services.Mcp.McpOAuthService>();
builder.Services.AddSingleton<ClaudeHomeServer.Services.Mcp.McpProbeService>();
// Встроенная интеграция Higgsfield (волна 1): запись реестра + OAuth-вход + инжект в ход
builder.Services.AddSingleton<ClaudeHomeServer.Services.Mcp.HiggsfieldIntegration>();
// Продуктовые MCP-серверы поверх HTTP (ADR-012): тулсет отдаёт схемы, общий контроллер
// McpTransportController — транспорт. Новый сервер добавляется одной регистрацией здесь.
builder.Services.AddSingleton<ClaudeHomeServer.Services.Mcp.Http.IMcpToolset,
    ClaudeHomeServer.Services.Mcp.Http.WidgetsToolset>();
// Память персоны/команды (фаза 2, волна 1): один тулсет на все ключи memory и pmem_<handle> —
// персона и проект едут хвостом маршрута /mcp/memory/{personaId}/{projectId}
builder.Services.AddSingleton<ClaudeHomeServer.Services.Mcp.Http.IMcpToolset,
    ClaudeHomeServer.Services.Mcp.Http.MemoryToolset>();
// Задачи и заметки (фаза 2, волна 2): сессия-вызыватель едет хвостом /mcp/{tasks,notes}/{sessionId},
// по ней тулсет резолвит проект чата, персону и её привязки на каждый вызов
builder.Services.AddSingleton<ClaudeHomeServer.Services.Mcp.Http.IMcpToolset,
    ClaudeHomeServer.Services.Mcp.Http.TasksToolset>();
builder.Services.AddSingleton<ClaudeHomeServer.Services.Mcp.Http.IMcpToolset,
    ClaudeHomeServer.Services.Mcp.Http.NotesToolset>();
// Персоны (фаза 2, волна 2): тяжёлая оркестрация CRUD — в PersonasCrudService (общий с REST),
// тулсет — тонкий JSON-фасад над ним и сервисами; хвост маршрута — та же сессия-вызыватель
builder.Services.AddSingleton<ClaudeHomeServer.Services.PersonasCrudService>();
// Чаты (фаза 2, волна 3): оркестрация chats_send/chats_report_up — общая для REST и wsp-тулсета
builder.Services.AddSingleton<ClaudeHomeServer.Services.SessionMessagingService>();
// Знания (фаза 2, волна 3): каталог баз Dify под пользователя — общий для REST и wsp-тулсета.
// Регистрация `KnowledgeBaseCatalogService` переехала в `KnowledgeSubsystem` (волна 3,
// шаг 3 — выделение подсистемы Knowledge).
builder.Services.AddSingleton<ClaudeHomeServer.Services.Mcp.Http.IMcpToolset,
    ClaudeHomeServer.Services.Mcp.Http.PersonasToolset>();
// Волна 3 (ADR-012): рабочее пространство, граф кода и уведомления
builder.Services.AddSingleton<ClaudeHomeServer.Services.Mcp.Http.IMcpToolset,
    ClaudeHomeServer.Services.Mcp.Http.WorkspaceToolset>();
builder.Services.AddSingleton<ClaudeHomeServer.Services.Mcp.Http.IMcpToolset,
    ClaudeHomeServer.Services.Mcp.Http.CodeGraphToolset>();
builder.Services.AddSingleton<ClaudeHomeServer.Services.Mcp.Http.IMcpToolset,
    ClaudeHomeServer.Services.Mcp.Http.NotificationsToolset>();
// Базы знаний Dify (фаза 2, волна 4 — последний сервер фазы): тулсет ходит во внешний
// Dify напрямую через KnowledgeService, ключ не покидает бэкенд; хвост — сессия-вызыватель
builder.Services.AddSingleton<ClaudeHomeServer.Services.Mcp.Http.IMcpToolset,
    ClaudeHomeServer.Services.Mcp.Http.DifyToolset>();
// Сторожа чатов: хвост /mcp/watch/{sessionId} — сессия-вызыватель; право на чат
// проверяется на каждый tools/list и вызов (WatchToolset.TryResolve)
builder.Services.AddSingleton<ClaudeHomeServer.Services.Mcp.Http.IMcpToolset,
    ClaudeHomeServer.Services.Mcp.Http.WatchToolset>();
// Веб-поиск (websearch: web_search + web_read): поиск идёт во внешний Perplexity Sonar
// (ключ не покидает бэкенд), чтение страницы — существующим ридером панели «Чтение»
// вместе с его SsrfGuard и квотой владельца. Пустой Perplexity:ApiKey = сервер ходу
// не объявляется вовсе (SessionManager не строит контекст).
builder.Services.AddSingleton<ClaudeHomeServer.Services.Mcp.Http.IMcpToolset,
    ClaudeHomeServer.Services.Mcp.Http.WebSearchToolset>();
builder.Services.AddSingleton<ClaudeHomeServer.Services.Mcp.Http.McpToolsetRegistry>();
// Белый список инструментов профиля провайдера (KeepMcpTools): читает McpTransportController
// на tools/list и tools/call, сами тулсеты о нём не знают
builder.Services.AddSingleton<ClaudeHomeServer.Services.Mcp.Http.McpToolWhitelist>();
// BoardService — DI в подсистеме `TasksSubsystem` (волна 4C, шаг 1).
// Шина событий хода (ADR-013): один экземпляр на инстанс, изоляция владельцев — через
// TurnContext события. На этапе 0 подписчиков нет; этап 2 — реестр секций промпта
// (6 провайдеров + DossierTrailerHint) подключается к шине через SessionManager.
builder.Services.AddSingleton<ClaudeHomeServer.Services.Turn.ITurnEventBus,
    ClaudeHomeServer.Services.Turn.TurnEventBus>();
// Этап 2: контрибьюторы секций системного промпта (этап 2 плана «Шина событий хода»).
// Каждый контрибьютор несёт Order/Key/Group и два метода: IsEnabled (гейт по
// per-session условиям — без него регрессия golden-фикстуры 4) и BuildAsync (исключения
// гасятся внутри). Подключаются к шине фильтром prompt/assembling через
// PromptSectionContributorsRegistration.RegisterAll в SessionManager.
// Новый контрибьютор — одна строка в PromptSectionContributorsDi.AddPromptSectionContributors().
builder.Services.AddPromptSectionContributors();
builder.Services.AddSingleton<SessionManager>();
// Серверные сторожа чатов: стор + цикл опроса. Запуск poll-команд — через
// ILauncherFactory (среда владельца); цикл — hosted, в Testing-среде не поднимается
// (см. AddHosted), юниты гоняют TickAsync напрямую
builder.Services.AddSingleton<ClaudeHomeServer.Services.Watchdog.WatchdogStore>();
builder.Services.AddSingleton<ClaudeHomeServer.Services.Watchdog.IWatchdogEnvironment,
    ClaudeHomeServer.Services.Watchdog.WatchdogEnvironment>();
builder.Services.AddSingleton<ClaudeHomeServer.Services.Watchdog.IWatchdogCommandRunner,
    ClaudeHomeServer.Services.Watchdog.WatchdogCommandRunner>();
builder.Services.AddSingleton<ClaudeHomeServer.Services.Watchdog.IWatchdogAlarm,
    ClaudeHomeServer.Services.Watchdog.WatchdogAlarm>();
builder.Services.AddGatedHostedService<ClaudeHomeServer.Services.Watchdog.WatchdogService>(builder.Configuration);
// Снапшот сторожей + событие watchdogs_changed (визуализация для фронта): подписка на
// Changed стора живёт в конструкторе — слушатель обязан существовать к первым постановкам;
// прогрев ниже делает подписку независимой от hosted-цикла
builder.Services.AddSingleton<ClaudeHomeServer.Services.Watchdog.WatchdogNotifier>();
// Обратный индекс «файл → какие ещё чаты его меняли» (панель «Изменения») — см. GetForProjectAsync
builder.Services.AddSingleton<ProjectFileSessionsIndex>();
builder.Services.AddSingleton<ModelCatalogService>();
builder.Services.AddSingleton<NotificationStore>();
builder.Services.AddSingleton<NotificationService>();
// Шов ITaskNotificationDispatcher (Core) → TaskNotificationDispatcherAdapter → NotificationService.
// Обоснование отдельного шва (а не расширения IKnowledgeNotificationDispatcher) —
// в ITaskNotificationDispatcher.cs: Tasks работает с готовым NotificationMessage и
// всегда с web push; Knowledge шлёт плоские параметры и без push.
builder.Services.AddSingleton<TaskNotificationDispatcherAdapter>();
builder.Services.AddSingleton<ITaskNotificationDispatcher>(sp => sp.GetRequiredService<TaskNotificationDispatcherAdapter>());
builder.Services.AddSingleton<PushSubscriptionStore>();
builder.Services.AddSingleton<PushService>();
builder.Services.AddSingleton<TaskExecutionService>();
// Шов ITaskExecutor (Core) → TaskExecutorAdapter → TaskExecutionService.
// Адаптер резолвит TaskExecutionService через конструктор, DI форвардер ниже.
builder.Services.AddSingleton<TaskExecutorAdapter>();
builder.Services.AddSingleton<ITaskExecutor>(sp => sp.GetRequiredService<TaskExecutorAdapter>());
// Раздача под-задач и волны режима «Командная реализация» (Э3): создание задач по плану
// и пакетный запуск исполнителей. Конструктор вешает хук в SessionManager — сервис нужно
// прогреть на старте (ниже), иначе «Запустить» в карточке плана осталось бы без раздачи.
builder.Services.AddSingleton<TeamWaveService>();
// Сторож зависших волн (Э4): без него молчаливо умерший исполнитель оставлял бы штаб
// в стадии «волна N» навсегда
builder.Services.AddGatedHostedService<TeamWaveWatchdog>(builder.Configuration);
builder.Services.AddSingleton<SessionSummaryService>();
// Сводка карточки архива (место chat-digest, шаг 5 плана «Архив чатов») и
// карта плана (место plan-map, «Визуальный разворот плана» часть B) —
// DI в подсистеме `LlmSubsystem` (шаг 0 волны 4, см. LlmSubsystem.cs).
builder.Services.AddSingleton<ChatTaskExtractionService>();
// DailyBriefingService — DI в Main (Этап 5, вынос Tasks): вертикаль Tasks живёт в
// отдельной сборке и больше не может регистрировать Main-типы. DailyBriefingService
// остался кросс-вертикальным фасадом (Tasks+Notes+Llm) в корне Services. Шов
// IDailyBriefingRunner (Core) → DailyBriefingRunnerAdapter → DailyBriefingService.
// Адаптер обязан идти ПОСЛЕ DailyBriefingService — он резолвит его через конструктор.
builder.Services.AddSingleton<DailyBriefingService>();
builder.Services.AddSingleton<DailyBriefingRunnerAdapter>();
builder.Services.AddSingleton<IDailyBriefingRunner>(sp => sp.GetRequiredService<DailyBriefingRunnerAdapter>());
// Проактивность персон (событийно-управляемый rules-движок): state store, источники и сервис-collaborator
builder.Services.AddSingleton<AutomationStateStore>();
builder.Services.AddSingleton<AutomationRootResolver>();
builder.Services.AddSingleton<MentionTriggerSource>();
builder.Services.AddSingleton<ITriggerSource, TimerTriggerSource>();
builder.Services.AddSingleton<ITriggerSource, FileTriggerSource>();
builder.Services.AddSingleton<ITriggerSource, NoteTriggerSource>();
builder.Services.AddSingleton<ITriggerSource, GitCommitTriggerSource>();
builder.Services.AddSingleton<ITriggerSource, TaskStatusTriggerSource>();
builder.Services.AddSingleton<PersonaAutomationService>();
// Шов IPersonaAutomationRunner (Core) → PersonaAutomationRunnerAdapter → PersonaAutomationService.
// Регистрация адаптера вынесена в Main (Этап 5, вынос Tasks): вертикаль Tasks больше не
// может регистрировать Main-типы. IPersonaAutomationRunner фабрика тоже здесь —
// TasksSubsystem её больше не ставит.
builder.Services.AddSingleton<PersonaAutomationRunnerAdapter>();
builder.Services.AddSingleton<IPersonaAutomationRunner>(sp => sp.GetRequiredService<PersonaAutomationRunnerAdapter>());
// Бэкапы: singleton + hosted-обёртка — снапшот дёргают и таймер, и админский API
builder.Services.AddSingleton<ClaudeHomeServer.Services.Backup.BackupService>();
builder.Services.AddGatedHostedFrom(builder.Configuration, sp =>
    sp.GetRequiredService<ClaudeHomeServer.Services.Backup.BackupService>());

// === Десктопный агент (ADR-008): руки песочницы на машине пользователя ===
// Реестр устройств и хеши их токенов — единственный стор грани; сеансы рук и живые
// соединения канала живут только в памяти (рестарт бэкенда гасит сеанс по построению).
builder.Services.AddSingleton<ClaudeHomeServer.Services.Desktop.DeviceRegistry>();
builder.Services.AddSingleton<ClaudeHomeServer.Services.Desktop.DevicePairingService>();
// Отправитель команд на устройство: push в конкретное соединение хаба (групп нет)
builder.Services.AddSingleton<ClaudeHomeServer.Services.Desktop.IDeviceCommandSender,
    ClaudeHomeServer.Services.Desktop.DeviceHubCommandSender>();
builder.Services.AddSingleton<ClaudeHomeServer.Services.Desktop.DesktopCallRouter>();
builder.Services.AddSingleton<ClaudeHomeServer.Services.Desktop.IDesktopChatDirectory,
    ClaudeHomeServer.Services.Desktop.DesktopChatDirectory>();
builder.Services.AddSingleton<ClaudeHomeServer.Services.Desktop.IDesktopDeviceDirectory,
    ClaudeHomeServer.Services.Desktop.DesktopDeviceDirectory>();
builder.Services.AddSingleton<ClaudeHomeServer.Services.Desktop.IDesktopHandsNotifier,
    ClaudeHomeServer.Services.Desktop.DesktopHandsNotifier>();
builder.Services.AddSingleton<ClaudeHomeServer.Services.Desktop.IDesktopCallCanceller,
    ClaudeHomeServer.Services.Desktop.DesktopRouterCallCanceller>();
builder.Services.AddSingleton<ClaudeHomeServer.Services.Desktop.DesktopHandsSessionService>();
// Разрыв соединения — один из поводов погасить сеанс: маршрутизатор канала знает о нём
// первым, поэтому сеансы подписаны на него наблюдателем, а не наоборот (форвард на тот же
// синглтон, не второй экземпляр).
builder.Services.AddSingleton<ClaudeHomeServer.Services.Desktop.IDeviceConnectionObserver>(
    sp => sp.GetRequiredService<ClaudeHomeServer.Services.Desktop.DesktopHandsSessionService>());
builder.Services.AddSingleton<ClaudeHomeServer.Services.Desktop.DesktopAccessGate>();
// Сторож сеансов: 15 минут простоя, потолок 2 часа, исчезнувший чат, снятый тумблер грани
builder.Services.AddGatedHostedService<ClaudeHomeServer.Services.Desktop.DesktopSessionReaper>(builder.Configuration);
// TaskSchedulerService — DI в подсистеме `TasksSubsystem` (волна 4C, шаг 1).
builder.Services.AddGatedHostedService<ChatExpiryService>(builder.Configuration);
// Автоправило архивации чатов (флаг chat-auto-archive) — singleton + hosted: кнопка
// «Применить сейчас» (POST /api/chats/archive-run) дёргает RunNowAsync того же инстанса
builder.Services.AddSingleton<ChatArchiveService>();
builder.Services.AddGatedHostedFrom(builder.Configuration, sp => sp.GetRequiredService<ChatArchiveService>());
builder.Services.AddGatedHostedService<ChatTurnLoggerService>(builder.Configuration);
// NoteExpiryService — DI в подсистеме `NotesSubsystem` (волна 4C, шаг 2).
// Фоновый прогрев сводок «Что нового» — в `Services.Changelog.ChangelogSubsystem`.
// Терминал (PTY) — единственная регистрация в корне Services, под гейтом workspace-destructive.
// Подсистема не заведена сознательно: единственная регистрация и два резолва при
// shutdownTerminals (см. блок var app = builder.Build() ниже) — прецедент вертикали
// без IAppSubsystem, как у Services.Watchdog.
// Шов `ITerminalHubNotifier` (Этап 5, волна C, шаг 2): реализация лежит
// рядом с TerminalHub (Hubs/), вертикаль Terminal зависит только от Core.
builder.Services.AddSingleton<ClaudeHomeServer.Services.Composition.ITerminalHubNotifier,
    ClaudeHomeServer.Hubs.TerminalHubNotifier>();
builder.Services.AddSingleton<TerminalService>();
// Раздел «Сервисы проекта» (Preview/DevServer/ExternalPreview/...) — пилотная подсистема
// волны 4A. Сам `ProjectServicesSubsystem.Register` подключает ВСЕ регистрации этой
// группы: подсистема самодостаточна, точка регистрации HTTP-клиента `proxy` тоже там.
// Внешние CDN медиа (/api/proxy): прямой канал к ним душится DPI —
// эти запросы ОБЯЗАНЫ идти через системный egress-прокси.
builder.Services.AddHttpClient("media-proxy");
// Загрузка произвольных пользовательских URL (save-from-url): без авто-редиректов,
// чтобы редирект на приватный хост не обошёл SSRF-проверку (см. SsrfGuard).
builder.Services.AddHttpClient("safe-download")
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
// Раздел «Видео» — пилот подсистемы (см. `ClaudeHomeServer.Video/VideoSubsystem.cs`).
// Сам `VideoSubsystem.Register` подключает и платформенный `IMemoryCache` для своих
// провайдеров: подсистема самодостаточна, точку регистрации кеша в `Program.cs`
// больше не держим.
builder.Services.AddSubsystems(builder.Configuration,
    // `git` идёт ПЕРВЫМ: это нижний слой вертикалей — на него смотрят будущие
    // Dossiers (захват коммитов), Knowledge (синк файлов), Deploy (publish/rollback),
    // плюс hosted-сервисы SessionManager/ProjectManager. Раньше — сломает их DI-порядок.
    new ClaudeHomeServer.Services.Git.GitSubsystem(),
    // CodeGraph — после Git, потому что это первая подсистема с пост-билд фазой
    // (`IAppPhaseSubsystem.ConfigureApp` регистрирует языковые провайдеры `.cs`/`.ts`/`.tsx`).
    new ClaudeHomeServer.Services.CodeGraph.CodeGraphSubsystem(),
    // Dossiers — после CodeGraph: вертикаль захвата паспортов зависит от швов
    // `Services.Git` (GitService — захват коммитов) и `Services.CodeGraph`
    // (CodeGraphService — обогащение паспортов графом кода). Нижние слои должны
    // регистрироваться раньше: Microsoft DI порядок регистраций при резолве не
    // учитывает; порядок здесь — очередь старта IHostedService и читаемость
    // (нижние слои раньше).
    new ClaudeHomeServer.Services.Dossiers.DossiersSubsystem(),
    // Llm — после Dossiers: нижний слой, от которого зависят Memory (ICheapTextRunner
    // для консолидации/autolearn), Spend (LlmProviderRegistry для прайса),
    // Backgrounds/ProjectIcons (ICheapTextRunner для подбора фона/значка).
    // Шаг 0 волны 4: адрес переездов Workflow / Subscription / Specialty на
    // следующих шагах — здесь живут LlmProviderRegistry/ModelAssignmentResolver/
    // UserModelTierResolver/OneShotClaudeRunner/CheapTextRunner и т.д.
    new ClaudeHomeServer.Services.Llm.LlmSubsystem(),
    // Memory — после Llm: фасады памяти персон/команды используют ICheapTextRunner
    // (консолидация LLM-merge и autolearn). Общий слой `Services.Memory`
    // (MemoryWriteResolver/MemoryDify) при этом независим.
    new ClaudeHomeServer.Services.Memory.MemorySubsystem(),
    // Knowledge — после Dossiers: вертикаль Dify RAG (Knowledge.md + ADR-013).
    // Форвардеры `IKnowledgeSyncParticipant → {DossierStore, ...}` остаются в
    // Program.cs (кросс-вертикальный клей), поэтому KnowledgeSubsystem не зависит
    // от Dossiers/Knowledge напрямую; миграция WorkspaceKnowledgeStore из Project —
    // отдельный пост-билд блок ниже, чтобы не словить construct до PostRestoreHook.
    new ClaudeHomeServer.Services.Knowledge.KnowledgeSubsystem(),
    new ClaudeHomeServer.Services.Spend.SpendSubsystem(),
    new VideoSubsystem(),
    new ClaudeHomeServer.Services.Yandex.YandexSubsystem(),
    new ClaudeHomeServer.Services.Reader.ReaderSubsystem(),
    new ClaudeHomeServer.Services.Images.ImagesSubsystem(),
    new ClaudeHomeServer.Services.Tts.TtsSubsystem(),
    new ClaudeHomeServer.Services.Deploy.DeploySubsystem(),
    new ClaudeHomeServer.Services.Backgrounds.BackgroundsSubsystem(),
    new ClaudeHomeServer.Services.ProjectIcons.ProjectIconsSubsystem(),
    // ProjectServices — раздел «Сервисы проекта» (Preview/DevServer). Регистрация ниже
    // всех: вертикаль листовая, ни от кого не зависит; наоборот, на неё ссылаются
    // PreviewController и SessionHub (через Program.cs).
    new ClaudeHomeServer.Services.ProjectServices.ProjectServicesSubsystem(),
    // Changelog — «Что нового»: листовая подсистема, ни от кого не зависит, читает
    // git-вывод и `data/changelog/product.json` через `FileService`.
    new ClaudeHomeServer.Services.Changelog.ChangelogSubsystem(),
    // Tasks — вертикаль задач с доской агентов (BoardService). Утренний брифинг
    // (DailyBriefingService) остался в корне Services как кросс-вертикальный фасад
    // (Tasks+Notes+Llm) и регистрируется выше по тексту Program.cs.
    // Регистрируется после Changelog потому что зависит от `Services.Hubs`
    // (IHubContext<SessionHub>) и `Services.Llm` (ICheapTextRunner через TaskAiService).
    // TaskSchedulerService — gated hosted, его тип `AddGatedHostedService` активируется
    // флагом (см. appsettings). Шов Tasks → Models.Session (три статических
    // резолвера) описан в `Services/Tasks/TasksSubsystem.cs` (см. комментарий 6).
    new ClaudeHomeServer.Services.Tasks.TasksSubsystem(),
    // Notes — вертикаль заметок (Obsidian-совместимый vault, AI-сводки, синк с Dify,
    // мост чекбоксов заметок ↔ задач). Регистрируется после Tasks: шов `Notes → Tasks`
    // через `NoteTaskSyncService` (TaskManager, CreateTaskRequest, UpdateTaskRequest) —
    // нижний слой регистрируется раньше, как и везде.
    new ClaudeHomeServer.Services.Notes.NotesSubsystem(),
    // Skills — вертикаль навыков (реестр skills.sh, LLM-подбор/генерация, обёртка
    // CLI «npx skills»). Регистрируется после Notes: порядок с Task/Notes не связан
    // (Skills — листовая, единственный шов `SessionManager → SkillsService` остаётся
    // до этапа 4 — расщепление SessionManager).
    new ClaudeHomeServer.Services.Skills.SkillsSubsystem());
// Dify и fal — опциональные зависимости: локальный Dify поднят не всегда, fal живёт за DPI,
// и оба вызывающих ловят отказ сами (KnowledgeService деградирует, FalImageService возвращает
// пустой список). Тихий клиент вместо дефолтного — иначе каждый запрос печатает Error
// со стектрейсом; см. Services/Http/QuietHttpLogger.
// HTTP-клиент `dify` зарегистрирован внутри KnowledgeSubsystem (волна 3, шаг 3): Dify —
// локальный сервис, и `WithoutEgressProxy` обязателен (инвариант, как у Forgejo).
// Forgejo — клиент локального Git-сервера: зарегистрирован внутри GitSubsystem
// (Forgejo — локальный сервис, `WithoutEgressProxy` обязателен).
builder.Services.AddQuietHttpClient("fal", new QuietHttpClientProfile(
    Category: "ClaudeHomeServer.Media.Fal",
    Subject: "сервисом fal.ai",
    Consequence: "Генерация изображений и учёт расхода недоступны."));
// Озвучка голосового режима чата переехала в TtsSubsystem (волна 1.4).
// Контракт фолбэка на голос браузера живёт в TtsController: 503 not_configured / 502 upstream.
builder.Services.AddQuietHttpClient(
    ClaudeHomeServer.Controllers.FilesController.OnlyOfficeCommandClient,
    new QuietHttpClientProfile(
        Category: "ClaudeHomeServer.Files.OnlyOffice",
        Subject: "Command API OnlyOffice",
        Consequence: "Принудительное сохранение документа ждёт таймаут."))
    .WithoutEgressProxy();
builder.Services.AddHttpClient("glif");
// Проба MCP-сервера из личного реестра: чужой сервер лежит штатно (не поднят, сменил адрес),
// и человек видит причину в ответе — консоли не нужны стектрейсы на каждый клик
builder.Services.AddQuietHttpClient(
    ClaudeHomeServer.Services.Mcp.McpProbeService.HttpClientName,
    new QuietHttpClientProfile(
        Category: "ClaudeHomeServer.Mcp.Probe",
        Subject: "внешним MCP-сервером",
        Consequence: "Проверка сервера показала отказ — сам ход это не ломает."));
// Authorization server чужого MCP-сервера: недоступен ровно так же штатно (нет DCR, лежит
// well-known, отозван клиент) — человек видит причину в ответе, консоли стектрейсы не нужны
builder.Services.AddQuietHttpClient(
    ClaudeHomeServer.Services.Mcp.McpOAuthService.HttpClientName,
    new QuietHttpClientProfile(
        Category: "ClaudeHomeServer.Mcp.OAuth",
        Subject: "сервером авторизации MCP",
        Consequence: "Вход в сервер не выполнен — инструменты этого сервера в ход не поедут."));
// Официальный реестр MCP (каталог MCP-серверов, волна 1): сервис зарубежный — клиент ходит
// ЧЕРЕЗ egress-прокси (WithoutEgressProxy тут НЕ звать). Опциональная зависимость: пустой
// Mcp:Catalog:BaseUrl выключает каталог целиком, раздел продолжает работать вручную.
builder.Services.AddSingleton(ClaudeHomeServer.Services.Mcp.Catalog.McpCatalogOptions.FromConfig(
    builder.Configuration));
builder.Services.AddQuietHttpClient(
    ClaudeHomeServer.Services.Mcp.Catalog.McpCatalogClient.HttpClientName,
    new QuietHttpClientProfile(
        Category: "ClaudeHomeServer.Mcp.Catalog",
        Subject: "реестром MCP registry.modelcontextprotocol.io",
        Consequence: "Поиск по каталогу MCP-серверов недоступен — добавить сервер можно вручную."))
    .ConfigureHttpClient(c =>
    {
        // Реестр публичный, но из РФ ходим через egress-прокси — общий таймаут включает
        // DNS+коннект+тело, и 5 с не хватает в дневной нагрузке. 15 с — запас с одной
        // попытки; повтор — отдельный шаг (rate-limit на поиск + кэш страницы).
        c.Timeout = TimeSpan.FromSeconds(15);
        // Реестр публичный и маленький, но доверять его размеру не обязаны: бьём oversized-ответ
        c.MaxResponseContentBufferSize = 2 * 1024 * 1024;
    });
builder.Services.AddSingleton<ClaudeHomeServer.Services.Mcp.Catalog.McpCatalogClient>();
// Веб-поиск Perplexity Sonar (MCP-сервер websearch): сервис зарубежный — клиент ходит
// ЧЕРЕЗ egress-прокси (WithoutEgressProxy тут НЕ звать, в отличие от локальных Dify/Forgejo).
// Опциональная зависимость: пустой Perplexity:ApiKey выключает сервер целиком, а лежащий
// сервис не должен сыпать в консоль стектрейсами на каждый запрос
builder.Services.AddSingleton(ClaudeHomeServer.Services.WebSearch.PerplexityOptions.FromConfig(
    builder.Configuration));
builder.Services.AddQuietHttpClient(
    ClaudeHomeServer.Services.WebSearch.PerplexitySearchService.HttpClientName,
    new QuietHttpClientProfile(
        Category: "ClaudeHomeServer.WebSearch.Perplexity",
        Subject: "сервисом веб-поиска Perplexity",
        Consequence: "Инструмент web_search вернёт модели отказ — сам ход это не ломает."));
builder.Services.AddSingleton<ClaudeHomeServer.Services.WebSearch.PerplexitySearchService>();
// Сторонний провайдер — опциональная зависимость: баланс уходит в протухший кэш, каталог
// моделей — в дефолтный список, фоновое действие — к другой модели. Мёртвый провайдер
// не должен засыпать консоль стектрейсами (см. QuietHttpLogger)
builder.Services.AddQuietHttpClient("llm-provider", new QuietHttpClientProfile(
    Category: "ClaudeHomeServer.Llm.Provider",
    Subject: "API стороннего провайдера моделей",
    Consequence: "Баланс и каталог моделей не обновятся, фоновое действие уйдёт другой модели."));
// Шлюз квоты Alibaba Coding Plan: авторизация по cookie консоли, а не по ApiKey (публичного
// API квоты у Token Plan нет). Неавторизованную (протухшую) сессию шлюз редиректит 302 на
// err.taobao.com — редиректы отключаем, иначе получим HTML вместо JSON. Логирование протухания
// (одна строка Warning, троттлинг) — в ProviderBalanceService.LogAlibabaExpiry, без QuietHttpLogger,
// чтобы не дублировать жалобы: протухание видно и на уровне тела (code != SUCCESS / NotAuthorised)
builder.Services.AddHttpClient("alibaba-console")
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddHttpClient("anthropic-oauth");
builder.Services.AddHttpForwarder();
// Раздел «Телеметрия»: опции проброса SigNoz UI (Telemetry:Ui) + короткий HTTP-клиент
// для health-пинга статуса. Опции регистрируем ВСЕГДА (выключенные тоже) — их читают
// и контроллер статуса, и middleware проброса /telemetry-proxy/** ниже.
builder.Services.AddSingleton(
    ClaudeHomeServer.Telemetry.TelemetryUiOptions.FromConfig(builder.Configuration));
builder.Services.AddHttpClient("telemetry-ui", c => c.Timeout = TimeSpan.FromSeconds(3))
    .WithoutEgressProxy();
builder.Services.AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"))
    .ConfigureHttpClient((_, handler) =>
    {
        // Таймаут установки TCP-соединения с модулем. Без него при мёртвом модуле
        // connect() висит до HttpClient.Timeout (100 с). 2 с — достаточно для любого
        // живого бэкенда и не блокирует шлюз надолго при недоступности.
        handler.ConnectTimeout = TimeSpan.FromSeconds(2);
    });
// Платформа внешних модулей (docs/modules/integration-contract.md): реестр манифестов,
// RS256-токены с JWKS и ДОБАВОЧНЫЙ провайдер YARP-конфига из реестра (LoadFromConfig выше
// не заменяется — YARP объединяет несколько IProxyConfigProvider, существующие маршруты
// OnlyOffice/drawio/forgejo работают как раньше).
builder.Services.AddSingleton<ClaudeHomeServer.Services.Modules.ModuleRegistry>();
builder.Services.AddSingleton<ClaudeHomeServer.Services.Modules.ModuleTokenService>();
builder.Services.AddSingleton<Yarp.ReverseProxy.Configuration.IProxyConfigProvider,
    ClaudeHomeServer.Services.Modules.ModuleProxyConfigProvider>();
// LLM-канал модулей (контракт §10): лимит конкурентности per-модуль + учёт вызовов R13
builder.Services.AddSingleton<ClaudeHomeServer.Services.Modules.HostLlmConcurrencyLimiter>();
builder.Services.AddSingleton<ClaudeHomeServer.Services.Modules.ModuleLlmUsageStore>();
// Секция DifyOptions + KnowledgeService + ProjectKnowledgeSyncService (singleton + hosted
// мост ProjectKnowledgeTurnSync) + UserKnowledgeCascade + IKnowledgeAlertNotifier +
// KnowledgeIndexReconciler (singleton + hosted) — DI в подсистеме `KnowledgeSubsystem`
// (волна 3, шаг 3). Миграция `WorkspaceKnowledgeStore.MigrateFromProjects` тоже
// остаётся отдельным пост-билд блоком (строка ~ниже) — см. ограничения в шапке
// KnowledgeSubsystem.cs.
// Участники реконсайлера error-документов Dify: пять владельцев локальных сторов
// «запись → {DocId, Hash}» (форвард на существующие singleton'ы, не новые экземпляры).
// Остаются в композиционном корне сознательно — кросс-вертикальный клей, не собственность
// Knowledge: перенос в KnowledgeSubsystem дал бы ей прямые ссылки на DossierStore и
// прочие чужие вертикали.
// Первые два (Persona/TeamMemoryService) — фасады подсистемы Memory (волна 3, шаг 4);
// `AddSingleton` ниже — форвардеры IKnowledgeSyncParticipant для них, а сами
// регистрации этих сервисов переехали в `MemorySubsystem.Register`.
builder.Services.AddSingleton<ClaudeHomeServer.Services.Knowledge.IKnowledgeSyncParticipant>(
    sp => sp.GetRequiredService<ClaudeHomeServer.Services.Memory.PersonaMemoryService>());
builder.Services.AddSingleton<ClaudeHomeServer.Services.Knowledge.IKnowledgeSyncParticipant>(
    sp => sp.GetRequiredService<ClaudeHomeServer.Services.Memory.TeamMemoryService>());
builder.Services.AddSingleton<ClaudeHomeServer.Services.Knowledge.IKnowledgeSyncParticipant>(
    sp => sp.GetRequiredService<ClaudeHomeServer.Services.Dossiers.DossierStore>());
builder.Services.AddSingleton<ClaudeHomeServer.Services.Knowledge.IKnowledgeSyncParticipant>(
    sp => sp.GetRequiredService<ClaudeHomeServer.Services.Notes.NotesKnowledgeService>());
builder.Services.AddSingleton<ClaudeHomeServer.Services.Knowledge.IKnowledgeSyncParticipant>(
    sp => sp.GetRequiredService<ProjectKnowledgeSyncService>());

// Адаптеры Core-швов Knowledge (Этап 5, волна 5): живут в Main, регистрируются
// здесь — Knowledge не должна знать про конкретные классы, только про швы.
builder.Services.AddSingleton<ClaudeHomeServer.Services.IKnowledgeHubNotifier,
    ClaudeHomeServer.Services.Composition.KnowledgeHubNotifier>();
builder.Services.AddSingleton<ClaudeHomeServer.Services.Composition.ISessionMessageObserver,
    ClaudeHomeServer.Services.Composition.SessionMessageObserver>();
builder.Services.AddSingleton<ClaudeHomeServer.Services.Composition.IPersonaDirectory,
    ClaudeHomeServer.Services.Composition.PersonaDirectoryAdapter>();
builder.Services.AddSingleton<ClaudeHomeServer.Services.Composition.IKnowledgeNotificationDispatcher,
    ClaudeHomeServer.Services.Composition.Notifications.KnowledgeNotificationDispatcher>();
builder.Services.AddSingleton<ClaudeHomeServer.Core.Telemetry.IDifyMetrics,
    ClaudeHomeServer.Services.Composition.DifyMetricsAdapter>();

// Этап 5, шаг 1: адаптеры узких швов для выноса Spend в отдельный .csproj.
// Spend ходит в корень Services только через эти интерфейсы — иначе сторож
// границ поймал бы прямую ссылку на SessionManager/PersonaManager/etc.
builder.Services.AddSingleton<ClaudeHomeServer.Services.ISessionDirectory,
    ClaudeHomeServer.Services.Composition.SessionDirectoryAdapter>();
builder.Services.AddSingleton<ClaudeHomeServer.Services.IPersonaLookup,
    ClaudeHomeServer.Services.Composition.PersonaLookupAdapter>();
builder.Services.AddSingleton<ClaudeHomeServer.Services.IChatHistoryLoader,
    ClaudeHomeServer.Services.Composition.ChatHistoryLoaderAdapter>();
builder.Services.AddSingleton<ClaudeHomeServer.Services.ITaskLookup,
    ClaudeHomeServer.Services.Composition.TaskLookupAdapter>();

// Этап 5, узкие швы Turn: адаптеры, которыми контрибьюторы промпта заменили прямые
// ссылки на вертикали. Регистрация в композиционном корне, а не в вертикали: адаптер
// знает обе стороны шва, и это единственное место, которому это позволено.
builder.Services.AddSingleton<ClaudeHomeServer.Services.Memory.IPersonaRecallSource,
    ClaudeHomeServer.Services.Composition.PersonaRecallSourceAdapter>();
builder.Services.AddSingleton<ClaudeHomeServer.Services.Turn.IAgentPromptSource,
    ClaudeHomeServer.Services.Composition.AgentPromptSourceAdapter>();
builder.Services.AddSingleton<ClaudeHomeServer.Services.Turn.ITeamMechanicsBlockSource,
    ClaudeHomeServer.Services.Composition.TeamMechanicsBlockAdapter>();

// Этап 5, волна E: forwarder-регистрации двух Core-интерфейсов выноса Notes.
// Реализации (`TaskBridge` поверх TaskManager, `NotesHubNotifier` поверх IHubContext<SessionHub>)
// живут в Main как тонкие обёртки; Notes (в отдельной сборке) получает только
// Core-контракты.
builder.Services.AddSingleton<ClaudeHomeServer.Services.Tasks.TaskBridge>();
builder.Services.AddSingleton<ClaudeHomeServer.Services.Composition.NotesHubNotifier>();
builder.Services.AddSingleton<ClaudeHomeServer.Services.Notes.INoteTaskBridge>(
    sp => sp.GetRequiredService<ClaudeHomeServer.Services.Tasks.TaskBridge>());
builder.Services.AddSingleton<ClaudeHomeServer.Services.Notes.INotesHubNotifier>(
    sp => sp.GetRequiredService<ClaudeHomeServer.Services.Composition.NotesHubNotifier>());

// Этап 5, Ф4: шов ISessionBroadcaster (Core) → SessionHubBroadcaster (Main, поверх
// IHubContext<SessionHub>). Префиксы групп "user_"/"project_" собираются только здесь —
// потребители больше не видят SignalR напрямую.
builder.Services.AddSingleton<ClaudeHomeServer.Services.Composition.SessionHubBroadcaster>();
builder.Services.AddSingleton<ClaudeHomeServer.Services.Composition.ISessionBroadcaster>(
    sp => sp.GetRequiredService<ClaudeHomeServer.Services.Composition.SessionHubBroadcaster>());

// JWT для REST/SignalR; Negotiate (NTLM/Kerberos) для WebDAV (Microsoft Office).
// Плюс ДВЕ именованные схемы грани десктопа (ADR-008, «Авторизация канала»): дефолтная
// JwtBearer к /api/devices/* не допускается вовсе — сервисный JWT владельца лежит в env
// каждого его хода, и на нём «ось выдачи» превратилась бы в барьер состава, а не
// авторизации. Схемы именованные: контроллеры грани называют их явным
// [Authorize(AuthenticationSchemes = ...)], и ни один эндпоинт не открывается «заодно».
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer()
    .AddNegotiate()
    // capability-токен чата: audience desktop, claims ownerId + sessionId + deviceId, TTL минуты
    .AddDesktopCapabilityAuth()
    // токен устройства: 256 бит, на сервере только хеш в data/devices.json
    .AddDesktopDeviceAuth();
builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<JwtService>((opts, jwt) =>
    {
        opts.TokenValidationParameters = jwt.ValidationParameters;
        opts.Events = new JwtBearerEvents
        {
            OnMessageReceived = ctx =>
            {
                var token = ctx.Request.Query["access_token"].ToString();
                if (!string.IsNullOrWhiteSpace(token)) ctx.Token = token;
                return Task.CompletedTask;
            },
            // Отзыв токенов сменой пароля: подпись и срок ещё ничего не гарантируют.
            // Здесь проверка накрывает весь [Authorize]-периметр — REST и handshake SignalR
            OnTokenValidated = ctx =>
            {
                var svc = ctx.HttpContext.RequestServices.GetRequiredService<JwtService>();
                if (!svc.IsSessionCurrent(ctx.Principal)) ctx.Fail("Токен отозван сменой пароля");
                return Task.CompletedTask;
            }
        };
    });
builder.Services.AddAuthorization(options =>
    // Политика для ручек, которые обязаны быть доступны из ХОДА админа (MCP-инструменты
    // deploy_*): роль в сервисном токене всегда "user", а проверять надо владельца по стору —
    // см. AdminByStoreRequirement
    options.AddPolicy(AdminByStoreRequirement.PolicyName, policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.Requirements.Add(new AdminByStoreRequirement());
    }));
builder.Services.AddSingleton<IAuthorizationHandler, AdminByStoreHandler>();

// За reverse-proxy (Caddy/туннель) берём реальный IP клиента из X-Forwarded-For,
// иначе rate-limit считал бы все запросы с адреса прокси как один
builder.Services.Configure<ForwardedHeadersOptions>(o =>
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto);

// Защита /api/auth/login от перебора паролей — фиксированное окно на IP.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    // §10.2 v1.5: превышение лимита → 429 + Retry-After. FixedWindowRateLimiter кладёт
    // остаток окна в метаданные лизы при отказе, но заголовок сам не проставляет — читаем
    // и выставляем явно, для всех политик разом (тело ответа не трогаем).
    options.OnRejected = (ctx, _) =>
    {
        if (ctx.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
            ctx.HttpContext.Response.Headers.RetryAfter =
                ((int)retryAfter.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return ValueTask.CompletedTask;
    };
    options.AddPolicy("auth-login", ctx =>
    {
        var limit = ctx.RequestServices.GetRequiredService<IConfiguration>()
            .GetValue("Auth:LoginRateLimit", 10);
        return RateLimitPartition.GetFixedWindowLimiter(
            ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = limit,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            });
    });
    // Выпуск host-токенов модулей (§10.2 v1.5): RSA-подпись на каждый выпуск — защищаем от
    // молотьбы. Партиция — {moduleId}:{sub} из aud/sub токена (без проверки подписи, она и
    // не нужна: подделка aud/sub лишь уводит запрос в другую партицию, сам запрос всё равно
    // упрётся в Validate следом). Иначе все модули идут с внутренних адресов (127.0.0.1,
    // host.docker.internal) и делят один общий счётчик — зациклившийся модуль бьёт по
    // всем соседям. Нераспознанный токен — фолбэк на IP, чтобы не образовать общую корзину.
    options.AddPolicy("host-token", ctx =>
    {
        var limit = ctx.RequestServices.GetRequiredService<IConfiguration>()
            .GetValue("Modules:HostTokenRateLimit", 30);
        var registry = ctx.RequestServices.GetRequiredService<ModuleRegistry>();
        var token = HostChannelMiddleware.ExtractBearerToken(ctx.Request);
        var partitionKey = HostChannelMiddleware.TryReadPartitionKey(token, registry)
            ?? ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return RateLimitPartition.GetFixedWindowLimiter(
            partitionKey,
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = limit,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            });
    });
    // Поиск по каталогу MCP-серверов: потолок запросов обязан стоять на бэке —
    // дебаунс фронта не защита. Партиция — владелец: реестр внешний, и молотить его
    // от одного аккаунта нельзя; без sub (запрос до авторизации) — фолбэк на IP
    options.AddPolicy("mcp-catalog", ctx =>
    {
        var limit = ctx.RequestServices.GetRequiredService<IConfiguration>()
            .GetValue("Mcp:Catalog:RateLimit", 30);
        var partitionKey = ctx.User.FindFirst("sub")?.Value
            ?? ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return RateLimitPartition.GetFixedWindowLimiter(
            partitionKey,
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = limit,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            });
    });
});

// CORS: только белый список origin'ов из конфига (Cors:AllowedOrigins).
// Фронт раздаётся same-origin из wwwroot, поэтому пустой список ничего не ломает.
var corsOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.WithOrigins(corsOrigins)
     .AllowAnyHeader()
     .AllowAnyMethod()
     .AllowCredentials()));

// Инспекционная копия не запускает НИ ОДИН свой фоновый сервис: планировщик задач взялся
// бы выполнять просроченные задачи Claude-исполнителем (реальные деньги и правки файлов),
// синхронизация знаний — «поправлять» боевые датасеты Dify под отставшее состояние,
// автоочистки — удалять чаты и заметки. Снимаем регистрации разом, а не по списку:
// перечень пришлось бы дописывать при каждом новом сервисе, и однажды его забудут.
// Хостинговые сервисы самого ASP.NET Core (Kestrel и прочие) не трогаем — только свои.
if (inspectionMode)
{
    var appAssembly = typeof(ClaudeHomeServer.Services.Backup.BackupService).Assembly;
    var background = builder.Services
        .Where(d => d.ServiceType == typeof(IHostedService))
        .Where(d => d.ImplementationType?.Assembly == appAssembly
                    || d.ImplementationFactory?.Method.DeclaringType?.Assembly == appAssembly)
        .ToList();
    foreach (var descriptor in background) builder.Services.Remove(descriptor);
    Console.WriteLine($"[Inspection] фоновые сервисы отключены ({background.Count})");
}

var app = builder.Build();

// Пост-билд шаг подсистем: для каждой подсистемы, реализующей IAppPhaseSubsystem,
// зовём ConfigureApp(app) в порядке регистрации. Вызываем ДО любых других
// пост-билд инициализаций и до middleware-конвейера, чтобы зависящие от него
// шаги (например, MigrateFromProjects в Knowledge) уже видели результат.
app.UseSubsystems();

// Логгер статического парсера workflow-транскриптов (DI туда не дотягивается)
ClaudeHomeServer.Services.Llm.WorkflowAgentParser.Log = app.Services.GetRequiredService<ILoggerFactory>()
    .CreateLogger(nameof(ClaudeHomeServer.Services.Llm.WorkflowAgentParser));
// Логгер резолвера meta-блоков workflow (обогащение input вызова по имени)
ClaudeHomeServer.Services.Llm.WorkflowMetaResolver.Log = app.Services.GetRequiredService<ILoggerFactory>()
    .CreateLogger(nameof(ClaudeHomeServer.Services.Llm.WorkflowMetaResolver));
// Дополнительные разрешённые корни транскриптов — пути проектов сторонних CLI-провайдеров
// (GLM/DeepSeek используют изолированные профили, транскрипты пишутся не в ~/.claude)
try
{
    var registry = app.Services.GetRequiredService<ClaudeHomeServer.Services.Llm.LlmProviderRegistry>();
    foreach (var dir in registry.GetProviderProjectsDirs())
    {
        ClaudeHomeServer.Services.TranscriptRoots.AddAllowedRoot(dir);
        Console.WriteLine($"[TranscriptRoots] разрешён корень провайдера: {dir}");
    }
    // Профили подписок (sub-*) и созданные после старта: разрешаем весь корень
    // claude-profiles по шаблону {key}/projects — иначе WorkflowWatcher у таких
    // сессий молча выключается («Детали недоступны» в блоке Workflow)
    ClaudeHomeServer.Services.TranscriptRoots.ProfilesRoot = registry.ProfilesDir;
    Console.WriteLine($"[TranscriptRoots] разрешён корень профилей: {registry.ProfilesDir}");
}
catch (Exception ex)
{
    Console.Error.WriteLine($"[TranscriptRoots] не удалось зарегистрировать корни провайдеров: {ex.Message}");
}

// Доводка после восстановления из бэкапа: сбросить карты документов баз знаний, чтобы
// Dify-слой пересобрался с натуры. Строго ДО первого обращения к WorkspaceKnowledgeStore
// (тот читает файл в конструкторе) — то есть до MigrateFromProjects ниже.
if (!inspectionMode)
{
    ClaudeHomeServer.Services.Backup.PostRestoreHook.RunIfNeeded(
        Path.GetDirectoryName(Path.GetFullPath(app.Configuration["DataPath"]
            ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json")))!,
        app.Services.GetService<ILogger<Program>>());
}

// Прогрев сервисов на старте — UserStore печатает предупреждение если создал admin/admin
app.Services.GetRequiredService<UserStore>();
// Реестр модулей — строго ДО первого обращения к слою Llm: его конструктор регистрирует
// LLM-действия модулей (§10.1) в динамическом слое LocalActionCatalog, а
// LocalActionOverridesStore при загрузке отбрасывает оверрайды неизвестных ключей —
// поздняя регистрация теряла бы сохранённые маршруты модульных действий
app.Services.GetRequiredService<ModuleRegistry>();
if (!inspectionMode)
{
    // Фоновый прогрев каталога моделей (опрос claude CLI ~5 с — не задерживаем старт).
    // В копии пропускаем: запуск claude зарегистрировал бы процесс в pid-файле БОЕВОГО
    // сервера (реестр живёт рядом с exe, а не в DataPath).
    _ = Task.Run(() => app.Services.GetRequiredService<ModelCatalogService>().GetModelsAsync());
    // Фоновый прогрев активной локальной LLM (грузим веса в память заранее; best-effort).
    // Резолвится через интерфейс — выбор движка уже сделан по LocalLlm:Provider.
    _ = Task.Run(() => app.Services.GetRequiredService<ClaudeHomeServer.Services.Llm.ILocalLlmClient>().WarmUpAsync());
}
app.Services.GetRequiredService<JwtService>();
// Раздача волн «Командной реализации»: конструктор вешает хук в SessionManager
app.Services.GetRequiredService<TeamWaveService>();
// Слушатель событий стора сторожей (watchdogs_changed): подписка встаёт в конструкторе
app.Services.GetRequiredService<ClaudeHomeServer.Services.Watchdog.WatchdogNotifier>();
// Синк файловых сабагентов-персон: подписки на события PersonaManager должны встать
// до первых запросов (иначе ранние правки персон не долетят до .md-файлов).
// В копии НЕ поднимаем: синк пишет .claude/agents/*.md в реальные папки проектов
// из восстановленного projects.json — то есть в рабочие каталоги на диске.
if (!inspectionMode) app.Services.GetRequiredService<PersonaAgentFileSync>();

// Однократная миграция @handle персон под контекстное правило: схлопывает лишние суффиксы
// (masha-2 → masha там, где контексты не пересекаются) и чистит старые .md-файлы сабагентов
// по прежнему handle. Маркер-файл — чтобы не гонять на каждом старте. Best-effort: сбой
// миграции не мешает старту.
// В инспекционной копии не запускаем: маркер в архиве может быть старым, и миграция
// переименовала бы персон, удаляя и перегенерируя .md в РЕАЛЬНЫХ папках проектов.
if (!inspectionMode)
    try
    {
        var dataDir = Path.GetDirectoryName(
            app.Configuration["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json"))
            ?? Path.Combine(AppContext.BaseDirectory, "data");
        var marker = Path.Combine(dataDir, "handle-migration-v1.done");
        if (!File.Exists(marker))
        {
            var personaManager = app.Services.GetRequiredService<PersonaManager>();
            var agentSync = app.Services.GetRequiredService<PersonaAgentFileSync>();
            var renamed = personaManager.MigrateContextualHandles();
            // Сначала удалить старые .md по прежнему handle (клон с oldHandle даёт старые пути)
            foreach (var (persona, oldHandle) in renamed)
                try { agentSync.RemovePersona(PersonaManager.WithHandle(persona, oldHandle)); } catch { /* не критично */ }
            // Затем перегенерировать файлы затронутых владельцев под новые handle
            foreach (var owner in renamed.Select(r => r.Persona.OwnerId).Distinct())
                try { agentSync.SyncOwner(owner, force: true); } catch { /* не критично */ }
            Directory.CreateDirectory(dataDir);
            File.WriteAllText(marker, $"{DateTime.UtcNow:O} renamed={renamed.Count}");
            if (renamed.Count > 0)
                Console.WriteLine($"[HandleMigration] контекстные @handle: переименовано персон — {renamed.Count}");
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[HandleMigration] миграция пропущена: {ex.Message}");
    }

// Стартовый проход провижна авто-ассистента — ЕДИНСТВЕННАЯ точка провижна существующим
// пользователям: для каждого гарантируем действующую дефолт-персону.
// Идемпотентен (EnsureAsync не плодит дубль), поэтому гонять на каждом старте безопасно.
// Best-effort: сбой одного пользователя не мешает старту. Синхронно (по образцу миграции
// handle выше) — к моменту готовности сервера принимать запросы ассистент уже создан,
// и /api/auth/me отдаёт его без провижна на чтение
// (инвариант: провижн НИКОГДА не вызывается на GET). В копии не запускаем: пишет в
// users.json/personas.json, а инспекционная копия чужие данные не трогает.
if (!inspectionMode)
    try
    {
        var users = app.Services.GetRequiredService<UserStore>();
        var provisioner = app.Services.GetRequiredService<DefaultAssistantProvisioner>();
        foreach (var u in users.GetAll())
        {
            try { _ = provisioner.EnsureAsync(u.Id).GetAwaiter().GetResult(); }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[DefaultAssistant] провижн пользователя {u.Id} пропущен: {ex.Message}");
            }
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[DefaultAssistant] стартовый проход пропущен: {ex.Message}");
    }

// Чистка осиротевших temp-конфигов MCP: содержат сервисный токен и могли
// остаться после крэша (штатно удаляются в finally каждого хода).
// Temp общий с боевым сервером — копия чужие конфиги не трогает.
if (!inspectionMode)
    _ = Task.Run(() =>
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(Path.GetTempPath(), "claude-mcp-*.json"))
                try { if (File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddHours(-6)) File.Delete(f); } catch { }
        }
        catch { /* нет доступа к temp — не критично */ }
    });

// Однократная миграция: переносим DifyDatasetId/DocumentTags из старых Project-записей в WorkspaceKnowledge
app.Services.GetRequiredService<WorkspaceKnowledgeStore>()
    .MigrateFromProjects(app.Services.GetRequiredService<ProjectManager>().GetAll());

app.UseForwardedHeaders();

// Ставится сразу за ForwardedHeaders — раньше любого перехватчика, чтобы падение в них
// тоже попало в структурный лог, а не в безымянное сообщение Kestrel.
app.UseExceptionHandler();


// Принудительный HTTPS только для публичного домена naychenko.me;
// доступ из локальной сети по IP остаётся по HTTP (сертификат на IP не выдан)
if (!app.Environment.IsDevelopment())
    app.Use(async (ctx, next) =>
    {
        if (!ctx.Request.IsHttps &&
            ctx.Request.Host.Host.EndsWith("naychenko.me", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Response.Redirect(
                $"https://{ctx.Request.Host.Host}{ctx.Request.PathBase}{ctx.Request.Path}{ctx.Request.QueryString}",
                permanent: false);
            return;
        }
        await next();
    });
// Внешний доступ к дев-серверу проекта по отдельному поддомену (ExternalPreviewOptions).
//
// Почему сайт нельзя показать под путём /preview/{id}/ и понадобился свой хост: дев-сервер
// отдаёт абсолютные ссылки от корня («/assets/app.js», «/@vite/client», чанки Module
// Federation), и под префиксом они уходят в корень ПРОДУКТА, а не к сайту. Здесь префикса нет.
//
// Стоит ДО UseRouting не из-за раздачи фронта (она сильно ниже), а из-за перехватчиков между:
// WebDAV на /projects/*, ModuleGateway и прокси OnlyOffice, который ловит ЛЮБОЙ путь, у
// которого второй символ — цифра. Ассеты проксируемого сайта попадали бы к ним.
//
// Правило перехвата ИНВЕРСНОЕ: хост из конфигурации наш всегда, и всё, что мы не обслужили
// сами, отбивается здесь же. Иначе выключенная фича отдавала бы со второго имени весь
// продукт целиком — и /api, и фронт.
{
    var extInvoker = new HttpMessageInvoker(new SocketsHttpHandler
    {
        UseProxy = false,
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
        UseCookies = false,
    });

    app.Use(async (ctx, next) =>
    {
        var router = ctx.RequestServices.GetRequiredService<ExternalPreviewRouter>();
        if (!router.IsOwnHost(ctx.Request.Host.Host)) { await next(); return; }

        // Продление сертификата по HTTP-01 обязано проходить мимо перехвата. Сейчас выпуск
        // идёт через DNS-01, но смену механизма наш 404 сломал бы молча и через месяцы —
        // причину искали бы где угодно, только не здесь.
        if (ctx.Request.Path.StartsWithSegments("/.well-known/acme-challenge"))
        {
            await next();
            return;
        }

        // Обмен токена на куку. Токен обязан исчезнуть из адресной строки: в URL он осел бы
        // в истории браузера, в закладках и в логах любого промежуточного узла.
        if (ctx.Request.Path.Equals(ExternalPreviewRouter.AuthPath, StringComparison.OrdinalIgnoreCase))
        {
            var handoff = ctx.Request.Query["t"].ToString();
            var (authTarget, authDenial) = await router.ResolveAsync(handoff);
            if (authTarget is null)
            {
                await ExternalPreviewResponses.WriteDenialAsync(ctx, authDenial);
                return;
            }

            ctx.Response.Cookies.Append(ExternalPreviewRouter.CookieName, handoff, new CookieOptions
            {
                HttpOnly = true,
                Secure = true,
                SameSite = SameSiteMode.Lax,
                Path = "/",
                // Срок куки = остатку срока ссылки. Сессионная умерла бы вместе с вкладкой
                // телефона, и это выглядело бы как «ссылка сломалась» на ровном месте.
                MaxAge = authTarget.Link.ExpiresAt - DateTimeOffset.UtcNow,
            });
            ctx.Response.Redirect("/");
            return;
        }

        var (target, denial) = await router.ResolveAsync(ctx.Request.Cookies[ExternalPreviewRouter.CookieName]);
        if (target is null)
        {
            await ExternalPreviewResponses.WriteDenialAsync(ctx, denial);
            return;
        }

        // Префикс не срезаем и не добавляем: сайт живёт в корне — ровно ради этого здесь
        // отдельный хост, а не путь.
        var extForwarder = ctx.RequestServices.GetRequiredService<IHttpForwarder>();
        var extError = await extForwarder.SendAsync(ctx, target.BaseUrl, extInvoker, ForwarderRequestConfig.Empty,
            new ExternalPreviewTransformer(target.Port, ctx.Request.Host, ctx.Request.IsHttps));
        // До назначения не достучались — забываем и порт, и выбранную семью адресов:
        // сервис мог смениться. Отмены клиентом о живости назначения не говорят ничего.
        if (extError is ForwarderError.Request or ForwarderError.RequestTimedOut)
            router.ForgetPort(target.Link.Jti, target.Port);
    });
}

app.UseRouting();
app.UseCors();
// UseRateLimiter — после UseRouting, иначе эндпоинт-политика [EnableRateLimiting] не видна
app.UseRateLimiter();
// Инспекционная копия — только чтение. Гейт один на весь пайплайн, а не перечень
// контроллеров: перечень устаревает с каждым новым эндпоинтом. Стоит ДО аутентификации
// (иначе запись отбивал бы 401 раньше нас, и гейт работал бы только для залогиненных),
// ДО WebDAV и YARP-прокси (их PUT/MKCOL/MOVE/DELETE пишут в реальные папки проектов).
//
// Следствие: POST /hubs/*/negotiate тоже отбивается, поэтому SignalR в копии не
// поднимается и UI показывает ошибку подключения. Это защита, а не дефект: живой хаб
// означал бы ходы чатов и терминалы в боевых рабочих папках.
if (inspectionMode)
{
    app.Use(async (ctx, next) =>
    {
        var method = ctx.Request.Method;
        var readOnly = HttpMethods.IsGet(method) || HttpMethods.IsHead(method)
            || HttpMethods.IsOptions(method);
        var isAuth = ctx.Request.Path.StartsWithSegments("/api/auth");

        if (!readOnly && !isAuth)
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            await ctx.Response.WriteAsJsonAsync(new
            {
                error = "inspection_read_only",
                message = "Инспекционная копия работает только на чтение",
            });
            return;
        }
        await next();
    });
}

app.UseAuthentication();
app.UseAuthorization();

// Наблюдаемость продуктовых MCP-серверов: их вызовы к бэкенду видно по заголовку
// X-Caller-Session-Id. Ставим ПОСЛЕ авторизации: иначе 401 попадал бы в статистику как
// отказ инструмента, хотя до контроллера запрос и не дошёл
app.UseMcpCallLog();

// Gateway внешних модулей (контракт §5.2): срезка клиентских identity-заголовков,
// валидация cc_token и инъекция модульного токена — ДО YARP-прокси
app.UseModuleGateway();

// Host-канал модулей (контракт §10.2): аутентификация /api/host/** модульным токеном RS256.
// Поверхность вне HMAC-схемы ядра — контроллеры под префиксом БЕЗ [Authorize], вход
// охраняется только здесь. Rate-limit обмена токена отрабатывает раньше (UseRateLimiter выше).
app.UseHostChannel();

// WebDAV — middleware перехватывает /projects/* до роутинга.
// Собственный Basic Auth внутри хендлера, вне JWT pipeline.
// Также отвечает на OPTIONS / (Windows WebClient зондирует корень перед монтированием).
app.Use(async (ctx, next) =>
{
    var path = ctx.Request.Path.Value ?? "";
    if (ctx.Request.Method.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase) && path == "/")
    {
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentLength = 0;
        ctx.Response.Headers["DAV"] = "1, 2";
        ctx.Response.Headers["MS-Author-Via"] = "DAV";
        ctx.Response.Headers["Allow"] = "OPTIONS, GET, HEAD, PUT, DELETE, PROPFIND, PROPPATCH, MKCOL, COPY, MOVE, LOCK, UNLOCK";
        return;
    }
    if (path == "/projects" || path.StartsWith("/projects/", StringComparison.OrdinalIgnoreCase))
    {
        await ClaudeHomeServer.WebDav.WebDavHandler.HandleAsync(ctx);
        return;
    }
    await next(ctx);
});

// OnlyOffice DS добавляет версионный префикс к URL ресурсов И Socket.IO WebSocket:
// /9.4.0-hash/web-apps/... и /9.4.0-hash/doc/.../c/?transport=websocket
// IHttpForwarder поддерживает WebSocket upgrade нативно — в отличие от HttpClient.
{
    var dsBase = builder.Configuration
        .GetSection("ReverseProxy:Clusters:onlyoffice:Destinations:default")
        .GetValue<string>("Address") ?? "http://localhost:8090";
    var ooInvoker = new HttpMessageInvoker(new SocketsHttpHandler
    {
        UseProxy = false,
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
        UseCookies = false,
    });

    // no-op SW: заменяем проблемный OO Service Worker нейтральным,
    // который очищает все кеши и не перехватывает запросы.
    // Иначе после первого визита SW кешируется в браузере и начинает
    // перехватывать Analytics.js с ошибкой net::ERR_FAILED.
    const string noOpSw =
        "self.addEventListener('install',e=>e.waitUntil(self.skipWaiting()));" +
        "self.addEventListener('activate',e=>e.waitUntil(" +
        "caches.keys().then(ks=>Promise.all(ks.map(k=>caches.delete(k)))).then(()=>self.clients.claim())" +
        "));";

    app.Use(async (ctx, next) =>
    {
        var path = ctx.Request.Path.Value ?? "";
        if (path.Length > 1 && char.IsDigit(path[1]))
        {
            // Service Worker OO заменяем no-op-ом — избегаем cacheFirst-ошибок в браузере
            if (path.EndsWith("/document_editor_service_worker.js", StringComparison.OrdinalIgnoreCase))
            {
                ctx.Response.ContentType = "application/javascript; charset=utf-8";
                ctx.Response.Headers["Service-Worker-Allowed"] = "/";
                ctx.Response.Headers["Cache-Control"] = "no-store";
                await ctx.Response.WriteAsync(noOpSw);
                return;
            }

            var forwarder = ctx.RequestServices.GetRequiredService<IHttpForwarder>();
            await forwarder.SendAsync(ctx, dsBase, ooInvoker, ForwarderRequestConfig.Empty, HttpTransformer.Default);
            return;
        }
        await next();
    });
}

// Dev-server preview proxy: /preview/{projectId}/{**path} → http://127.0.0.1:{port}
{
    var previewInvoker = new HttpMessageInvoker(new SocketsHttpHandler
    {
        UseProxy = false,
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
        UseCookies = false,
    });

    app.Use(async (ctx, next) =>
    {
        var path = ctx.Request.Path.Value ?? "";
        var match = System.Text.RegularExpressions.Regex.Match(path, @"^/preview/([^/]+)(/.*)?$");
        if (match.Success)
        {
            var projectId = match.Groups[1].Value;
            var restPath = match.Groups[2].Value ?? "/";

            // Аутентификация: middleware выполняется ДО endpoint routing, поэтому [Authorize]
            // тут не действует и ctx.User для iframe-запроса пуст. Токен берём из cookie
            // cc_preview (её ставит фронт перед загрузкой iframe — уходит и с сабресурсами),
            // либо из access_token / Bearer (прямое открытие в новой вкладке). Затем сверяем
            // владельца проекта — иначе любой мог бы проксироваться на чужой dev-сервер.
            var jwtSvc = ctx.RequestServices.GetRequiredService<JwtService>();
            // Куку принимаем только со СВОЕГО адреса. SameSite=Strict тут не защита: поддомен
            // внешнего доступа — тот же site, и браузер приложил бы куку к запросу со страницы
            // проксируемого дев-сайта (см. SecFetchSiteGuard). Отвергнутая кука роняет запрос
            // на access_token/Bearer ниже — их автоматически никто не подставляет.
            var previewToken = SecFetchSiteGuard.CookieAuthAllowed(ctx.Request)
                ? ctx.Request.Cookies["cc_preview"]
                : null;
            if (string.IsNullOrEmpty(previewToken))
            {
                var q = ctx.Request.Query["access_token"].ToString();
                if (!string.IsNullOrEmpty(q)) previewToken = q;
                else
                {
                    var auth = ctx.Request.Headers.Authorization.ToString();
                    if (auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                        previewToken = auth["Bearer ".Length..].Trim();
                }
            }
            var previewUserId = jwtSvc.ValidateUserToken(previewToken);
            if (previewUserId is null)
            {
                ctx.Response.StatusCode = 401;
                await ctx.Response.WriteAsync("{\"error\":\"Требуется авторизация\"}");
                return;
            }
            var previewProject = ctx.RequestServices.GetRequiredService<ProjectManager>().GetById(projectId);
            if (previewProject is null || previewProject.OwnerId != previewUserId)
            {
                ctx.Response.StatusCode = 403;
                await ctx.Response.WriteAsync("{\"error\":\"Доступ запрещён\"}");
                return;
            }

            var devServer = ctx.RequestServices.GetRequiredService<DevServerService>();
            // Порт активного для превью сервиса проекта; если ни один не запущен — 503.
            var port = devServer.GetActivePreviewPort(projectId);
            if (port is null)
            {
                ctx.Response.StatusCode = 503;
                await ctx.Response.WriteAsync("{\"error\":\"Dev-сервер не запущен\"}");
                return;
            }

            // HttpTransformer.Default сам дописывает к префиксу Path и QueryString запроса,
            // поэтому в префиксе пути быть не должно (иначе /preview/{id} уедет на дев-сервер
            // дважды и тот ответит 404). Срезаем свой префикс прямо в запросе.
            ctx.Request.Path = restPath.Length == 0 ? "/" : restPath;
            // Семью loopback-адресов выбирает LoopbackResolver, а не литерал: dev-сервер
            // на Node 17+ слушает ТОЛЬКО ::1, и прежний 127.0.0.1 до него не доставал —
            // живой сервис отдавал «соединение отвергнуто» при работающем порте.
            var previewBase = await LoopbackResolver.ResolveBaseAsync(port.Value);
            if (previewBase is null)
            {
                ctx.Response.StatusCode = 503;
                await ctx.Response.WriteAsync("{\"error\":\"Dev-сервер не отвечает\"}");
                return;
            }

            var forwarder = ctx.RequestServices.GetRequiredService<IHttpForwarder>();
            var previewError = await forwarder.SendAsync(ctx, previewBase, previewInvoker,
                ForwarderRequestConfig.Empty, HttpTransformer.Default);
            // До назначения не достучались — процесс мог смениться на слушающий по другой
            // семье, поэтому выбор семьи забываем, а не держим до истечения TTL. Отмены
            // клиентом сюда не попадают: они ничего не говорят о живости назначения.
            if (previewError is ForwarderError.Request or ForwarderError.RequestTimedOut)
                LoopbackResolver.Invalidate(port.Value);
            return;
        }
        await next();
    });
}

// Telemetry proxy: /telemetry-proxy/** → SigNoz UI (раздел «Телеметрия», admin-only).
// Same-origin проброс для iframe: браузер грузит /telemetry-proxy/ с нашего origin, мы
// форвардим на локальный SigNoz. Ключевое отличие от preview выше — префикс НЕ срезаем:
// SigNoz с env SIGNOZ_GLOBAL_EXTERNAL__URL=.../telemetry-proxy сам живёт под этим base-path
// (вставляет <base href> в SPA, ассеты и API резолвятся под префиксом — подтверждено спайком),
// поэтому HttpTransformer.Default отдаёт путь как есть. Стоит ДО раздачи фронта/SPA-fallback,
// чтобы /telemetry-proxy/* не ушёл в index.html.
{
    var telemetryUi = app.Services.GetRequiredService<ClaudeHomeServer.Telemetry.TelemetryUiOptions>();
    var telemetryInvoker = new HttpMessageInvoker(new SocketsHttpHandler
    {
        UseProxy = false,
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
        UseCookies = false,
    });

    app.Use(async (ctx, next) =>
    {
        var path = ctx.Request.Path.Value ?? "";
        if (path.Equals("/telemetry-proxy", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/telemetry-proxy/", StringComparison.OrdinalIgnoreCase))
        {
            // Выключено в конфиге — 503, фронт покажет заглушку «настрой, администратор».
            if (!telemetryUi.Enabled)
            {
                ctx.Response.StatusCode = 503;
                ctx.Response.ContentType = "application/json; charset=utf-8";
                await ctx.Response.WriteAsync("{\"error\":\"Телеметрия не настроена\"}");
                return;
            }

            // Аутентификация как у preview: iframe не носит Bearer, поэтому токен берём из
            // cookie cc_telemetry (её ставит фронт перед загрузкой iframe — уходит и с
            // сабресурсами SigNoz), либо из access_token / Bearer (прямое открытие в новой вкладке).
            var jwtSvc = ctx.RequestServices.GetRequiredService<JwtService>();
            // Тот же гейт, что у preview: кука действует только со своего адреса
            var token = SecFetchSiteGuard.CookieAuthAllowed(ctx.Request)
                ? ctx.Request.Cookies["cc_telemetry"]
                : null;
            if (string.IsNullOrEmpty(token))
            {
                var q = ctx.Request.Query["access_token"].ToString();
                if (!string.IsNullOrEmpty(q)) token = q;
                else
                {
                    var auth = ctx.Request.Headers.Authorization.ToString();
                    if (auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                        token = auth["Bearer ".Length..].Trim();
                }
            }
            var userId = jwtSvc.ValidateUserToken(token);
            if (userId is null)
            {
                ctx.Response.StatusCode = 401;
                await ctx.Response.WriteAsync("{\"error\":\"Требуется авторизация\"}");
                return;
            }
            // Телеметрия — админская. Роль берём из стора (source-of-truth: ловит отзыв роли,
            // в отличие от роли из уже выданного JWT). Не админ — 403, чтобы валидный
            // cc_telemetry не-админа не пускал к SigNoz.
            var user = ctx.RequestServices.GetRequiredService<UserStore>().GetById(userId);
            if (user is null || user.Role != "admin")
            {
                ctx.Response.StatusCode = 403;
                await ctx.Response.WriteAsync("{\"error\":\"Доступ запрещён\"}");
                return;
            }

            var forwarder = ctx.RequestServices.GetRequiredService<IHttpForwarder>();
            await forwarder.SendAsync(ctx, telemetryUi.InternalUrl, telemetryInvoker,
                ForwarderRequestConfig.Empty, HttpTransformer.Default);
            return;
        }
        await next();
    });
}

// Раздача фронтенда: wwwroot/ рядом с exe (prod) или ../../frontend/dist (dev)
var wwwrootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot");
var devDistPath = Path.GetFullPath(Path.Combine(
    Directory.GetCurrentDirectory(), "..", "..", "frontend", "dist"));
var distPath = Directory.Exists(wwwrootPath) ? wwwrootPath : devDistPath;
if (Directory.Exists(distPath))
{
    // Откуда реально раздаётся фронт — главный вопрос при «на стенде старый дизайн»;
    // wwwroot в репо не живет (артефакт сборки), деву нужен свежий frontend/dist
    app.Logger.LogInformation("Фронтенд раздаётся из {Path}", distPath);
    var fp = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(distPath);

    // index.html и SW-файлы — no-store: браузер всегда берёт свежую версию с сервера.
    // /assets/** — immutable: хэши в именах гарантируют уникальность, кэшируем «вечно».
    Action<StaticFileResponseContext> setCacheHeaders = ctx =>
    {
        var name = ctx.File.Name;
        var headers = ctx.Context.Response.Headers;
        if (name.Equals("index.html", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("sw.js", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("registerSW.js", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".webmanifest", StringComparison.OrdinalIgnoreCase))
        {
            headers.CacheControl = "no-store, no-cache, must-revalidate";
            headers.Pragma = "no-cache";
            headers.Expires = "0";
        }
        else if (ctx.Context.Request.Path.StartsWithSegments("/assets"))
        {
            headers.CacheControl = "public, max-age=31536000, immutable";
        }
    };

    // .onnx (модель Silero барж-ина, wwwroot/vad) в стандартной карте MIME отсутствует —
    // без явной записи StaticFiles отвечает 404, SPA-fallback отдаёт вместо модели
    // index.html, и VAD падает на инициализации («No graph was found in the protobuf»).
    // В dev не воспроизводится: статику там раздаёт Vite, он отдаёт octet-stream сам
    var contentTypes = new Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider();
    contentTypes.Mappings[".onnx"] = "application/octet-stream";

    app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = fp });
    app.UseStaticFiles(new StaticFileOptions { FileProvider = fp, OnPrepareResponse = setCacheHeaders, ContentTypeProvider = contentTypes });
    // /_api/* — Office/SharePoint-запросы; возвращаем 404 вместо SPA, иначе Word показывает «Нет доступа»
    app.Map("/_api", api => api.Run(ctx => { ctx.Response.StatusCode = 404; return Task.CompletedTask; }));
    app.MapFallbackToFile("index.html", new StaticFileOptions { FileProvider = fp, OnPrepareResponse = setCacheHeaders });
}
else
{
    app.Logger.LogWarning(
        "Фронтенд не найден: нет ни {Wwwroot} (рядом с exe), ни {Dist} (дев). " +
        "Сервер поднимется без статики — собери фронт (cd frontend; npm run build) или выложи wwwroot",
        wwwrootPath, devDistPath);
}

// JWKS модульных токенов (контракт §5.3) — публичный well-known, модули валидируют
// подписи RS256 по нему; аутентификации не требует по определению
app.MapGet("/.well-known/aihome-modules/jwks.json",
    (ModuleTokenService tokens) => Results.Json(tokens.BuildJwks()));

// Кастомный proxy-pipeline = дефолтный YARP + оформление ошибок модулей (§3.2):
// gateway обязан отдавать зарезервированные формы module_unavailable/module_timeout.
// Маршруты из конфига (OnlyOffice/drawio/forgejo) в ветку ошибок модулей не попадают.
app.MapReverseProxy(proxyPipeline =>
{
    proxyPipeline.Use(async (ctx, next) =>
    {
        await next();

        var routeId = ctx.GetReverseProxyFeature().Route.Config.RouteId;
        if (!routeId.StartsWith("module-", StringComparison.Ordinal) || ctx.Response.HasStarted)
            return;
        var moduleId = routeId["module-".Length..];

        var error = ctx.Features.Get<IForwarderErrorFeature>()?.Error;
        if (error == ForwarderError.RequestTimedOut)
        {
            // §3.1/§3.2: activity timeout 300 с бездействия → форма module_timeout
            ctx.Response.Clear();
            ctx.Response.StatusCode = StatusCodes.Status504GatewayTimeout;
            await ctx.Response.WriteAsJsonAsync(new { error = "module_timeout", moduleId });
        }
        else if (error is not null and not ForwarderError.RequestCanceled
                 || ctx.Response.StatusCode is StatusCodes.Status502BadGateway or StatusCodes.Status503ServiceUnavailable)
        {
            // Модуль погашен/unhealthy (нет доступных destinations или ошибка соединения)
            ctx.Response.Clear();
            ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            ctx.Response.Headers.RetryAfter = "15";
            await ctx.Response.WriteAsJsonAsync(new { error = "module_unavailable", moduleId, retryAfterSeconds = 15 });
        }
    });
    proxyPipeline.UseSessionAffinity();
    proxyPipeline.UseLoadBalancing();
    proxyPipeline.UsePassiveHealthChecks();
});
app.MapControllers();
app.MapHub<SessionHub>("/hubs/session");
app.MapHub<TerminalHub>("/hubs/terminal");
// Канал десктопного агента (ADR-008): исходящее соединение клиента с машины пользователя,
// push команды в конкретное соединение. Схема авторизации — токен устройства, а не общий JWT
app.MapHub<ClaudeHomeServer.Hubs.DeviceHub>("/hubs/devices");

// Graceful shutdown: гасим все живые процессы claude, терминалы и dev-серверы.
//
// Ссылки берём СЕЙЧАС, а не в колбэке: если хост не смог подняться (занятый порт —
// обычное дело, когда рядом уже крутится второй инстанс), провайдер успевает
// освободиться раньше, чем сработает ApplicationStopping, и резолв падал с
// ObjectDisposedException — гасить процессы было уже нечем. Все три сервиса —
// синглтоны, так что заранее взятая ссылка та же самая.
var shutdownSessions = app.Services.GetRequiredService<SessionManager>();
var shutdownTerminals = app.Services.GetRequiredService<TerminalService>();
var shutdownDevServers = app.Services.GetRequiredService<DevServerService>();

app.Lifetime.ApplicationStopping.Register(() =>
{
    // Каждый шаг отдельно: упавшая уборка одного не должна оставить процессы других
    static void Safe(Action step, string what)
    {
        try { step(); }
        catch (Exception ex) { Console.Error.WriteLine($"Shutdown: {what} — {ex.Message}"); }
    }

    Safe(shutdownSessions.KillAllProcesses, "процессы claude");
    Safe(shutdownTerminals.Dispose, "терминалы");
    Safe(shutdownDevServers.Dispose, "dev-серверы");
    // Тот же pid-файл принадлежит боевому серверу — копия его не трогает
    if (!inspectionMode) Safe(ProcessRegistry.KillAll, "реестр процессов");
});

app.Run();

// Отпустить признак «сервер работает» — после этого восстановление разрешено
if (instanceLock is not null)
{
    try { instanceLock.ReleaseMutex(); } catch { /* мьютекс мог быть заброшен */ }
    instanceLock.Dispose();
}

public partial class Program { }
