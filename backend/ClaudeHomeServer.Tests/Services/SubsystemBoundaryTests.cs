using System.Reflection;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// Сторож границ вертикалей: типы из подсистемной вертикали не должны ссылаться на типы
/// из других сервисных вертикалей. Правило — default-deny: разрешено ссылаться только на
/// «спинку» (BCL, ASP.NET, модели) и явно перечисленные служебные вертикали, остальные
/// <c>ClaudeHomeServer.Services.*</c> считаются чужой территорией.
///
/// Под «спинкой» понимается минимальный шов, через который любая вертикаль пользуется
/// платформой и общими сервисами: <c>System.*</c>, <c>Microsoft.*</c>,
/// <c>ClaudeHomeServer.Models</c>, <c>ClaudeHomeServer.Services.Http</c> (общие HTTP-утилиты),
/// <c>ClaudeHomeServer.Services.Composition</c> (контракт <c>IAppSubsystem</c>),
/// <c>ClaudeHomeServer.Services.Mcp</c> (сознательная граница для <c>McpSecretStore</c>).
///
/// Всё прочее под <c>ClaudeHomeServer.Services.*</c> (Desktop, Backup, Llm, Images, Tts,
/// Deploy, Memory, Turn, Docs, Git и т.п.) — нарушение. Список не дописывается под каждую
/// новую вертикаль: появилась новая — тест автоматом ловит любую ссылку на неё, и повод
/// обсудить шов. Подход — как в <c>PiiRules</c> (default-deny с явным allow-list).
///
/// Контракт allow-list разводит «префикс поддерева» и «точное совпадение namespace» в
/// двух разных полях (<see cref="VerticalBoundary.AllowedNamespacePrefixes"/> /
/// <see cref="VerticalBoundary.AllowedExactNamespaces"/>). Раньше единственное
/// поле-префикс разрешало корневую запись <c>ClaudeHomeServer.Services</c> и тем самым
/// открывало подсистеме весь <c>Services.*</c> — это и был сломанный инвариант.
///
/// Таблица <see cref="Boundaries"/> — единственная точка расширения: каждая будущая
/// подсистема добавляет ОДНУ строку со своим корневым namespace и собственным allow-list
/// (по умолчанию — общая спинка плюс сам проверяемый namespace). Полноту таблицы
/// (сторож «каждая <c>IAppSubsystem</c> имеет строку в <see cref="Boundaries"/>»)
/// держит отдельный <c>SubsystemBoundaryCoverageTests</c>.
///
/// Тест работает через рефлексию типов, а не через чтение исходного файла: один из
/// существующих стражей (<c>McpToolsetStabilityTests</c> через <c>FindSource</c> и
/// <c>MethodBody</c>) сломался на прошлом этапе просто от переименования метода
/// <c>PromptToolsSinkFor</c> → <c>SafePromptSnapshotAttach</c>, и пришлось чинить
/// отдельной задачей. Источник правды тут — сборка, а не текст.
///
/// Известное ограничение (в объём этой задачи НЕ входит расширение до IL):
/// сторож читает поля, параметры конструкторов, публичные свойства и сигнатуры
/// публичных методов — тела методов и IL НЕ читаются. Поэтому НЕВИДИМЫ:
///  - статические вызовы (например, <c>DeployHost.cs:47</c> → <c>GitService.IsGitRepo</c>,
///    <c>DeployHost.cs:122</c> → <c>Backup.InstanceLock.TryAcquireDeploy()</c>,
///    <c>ReaderService.cs:220,305-307</c> → <c>SsrfGuard.*</c>);
///  - <c>sp.GetRequiredService&lt;T&gt;()</c> и прочие сервисные резолвы из тел методов.
/// Шов через статический вызов ловится отдельным явным <c>AllowedNamespacePrefixes</c>
/// (как <c>ClaudeHomeServer.Services.Backup</c> в Deploy). Расширять до IL — отдельная
/// задача.
///
/// Что НЕ проверяется осознанно: интерфейсы и базовые классы (за пределами четырёх мест
/// ниже). Если потребуется — расширим в следующем шаге.
/// </summary>
public class SubsystemBoundaryTests
{
    /// <summary>Запись границы одной вертикали: имя (для отчёта), корневой namespace
    /// проверяемой вертикали и явный список разрешённых namespace-префиксов (с учётом
    /// вложенных через префикс "X.") + точных namespace-имён (одноуровневые синглтоны
    /// из корня Services, например <c>PersonaManager</c>).</summary>
    public sealed record VerticalBoundary(
        string VerticalName,
        string NamespaceRoot,
        string[] AllowedNamespacePrefixes,
        string[] AllowedExactNamespaces);

    /// <summary>Неймспейсы, на которые ЛЮБАЯ вертикаль имеет право ссылаться
    /// (BCL/платформа + общие служебные слои + сама вертикаль).</summary>
    private static readonly string[] SharedAllowedPrefixes =
    {
        // BCL и платформа — спинка для всех.
        "System",
        "Microsoft",
        // Доменные модели — разделяемые POCO, не сервисная логика.
        "ClaudeHomeServer.Models",
        // Спинка из общего кода сервисов: HTTP-утилиты, контракт подсистем и
        // MCP-секреты (сознательная граница, см. ADR-014).
        "ClaudeHomeServer.Services.Http",
        "ClaudeHomeServer.Services.Composition",
        "ClaudeHomeServer.Services.Mcp",
    };

    /// <summary>Таблица границ. Каждая подсистема добавляет ОДНУ строку: имя +
    /// корневой namespace + allow-list (по умолчанию shared-спинка + сама вертикаль)
    /// + точные namespace-имена для узких зависимостей из корня Services.</summary>
    public static IEnumerable<object[]> Boundaries => new[]
    {
        new object[]
        {
            new VerticalBoundary(
                "Video",
                "ClaudeHomeServer.Services.Video",
                SharedAllowedPrefixes
                    .Concat(new[] { "ClaudeHomeServer.Services.Video" })
                    .ToArray(),
                Array.Empty<string>()),
        },
        new object[]
        {
            new VerticalBoundary(
                "Yandex",
                "ClaudeHomeServer.Services.Yandex",
                SharedAllowedPrefixes
                    .Concat(new[] { "ClaudeHomeServer.Services.Yandex" })
                    .ToArray(),
                Array.Empty<string>()),
        },
        // Reader — единственная подсистема с прямой зависимостью от корня Services:
        // `SsrfGuard` (Services/ корень, общая инфраструктура, ADR-005) не переезжает
        // в подсистему. Точечный allow-list вместо расширения SharedAllowedPrefixes —
        // чтобы не открывать любой подсистеме весь `ClaudeHomeServer.Services.*`.
        // Допуск точный по FullName: async-state-машины `ReaderService.ReadImageCoreAsync`
        // и `WalkToFinalResponseAsync` материализуют `SsrfGuard.AddressCheck` (enum,
        // возвращаемый из `SsrfGuard.*`) в своих полях — компилятор C# кладёт возвращаемый
        // тип локальной переменной в поле state-машины, и рефлексия видит эту ссылку.
        // Сам класс `SsrfGuard` и его static-методы в полях/конструкторах/сигнатурах не
        // появляются (см. «Известное ограничение»), но nested enum появляется. Поэтому
        // ровно один точный тип в allow-list — `SsrfGuard+AddressCheck`.
        // `AngleSharp.*` — third-party HTML-парсер (SmartReader + HtmlParser).
        new object[]
        {
            new VerticalBoundary(
                "Reader",
                "ClaudeHomeServer.Services.Reader",
                SharedAllowedPrefixes
                    .Concat(new[]
                    {
                        "ClaudeHomeServer.Services.Reader",
                        "AngleSharp",
                    })
                    .ToArray(),
                new[]
                {
                    "ClaudeHomeServer.Services.SsrfGuard+AddressCheck",
                }),
        },
        // Images — вертикаль генерации картинок. Допуск к корню Services точечный,
        // через AllowedExactNamespaces: `PersonaManager` (Services/ корень) — догоняющая
        // генерация аватара триггерится из карточки персоны; это сознательная зависимость
        // от «спинки» (доменная модель пользователей и персон). См. ctor
        // `ImageBackfillService.cs:34-37`.
        // Прочие соседи по корню (`FalImageService`, `ImageAssetHelper`) — отдельные
        // единицы из корня, но ImagesSubsystem ссылается на них через интерфейс
        // `IImageGenerator` (своя вертикаль) и через static-вызовы; рефлексия их не
        // видит как ссылки из Images-типов.
        // `ClaudeHomeServer.Hubs` — нужен `IHubContext<SessionHub>` (событие
        // `image_backfilled` едет в ленту персоны).
        // Точечный допуск к `ClaudeHomeServer.Protocol`: `ImageBackfilledMessage`
        // (ImageBackfillService отправляет событие в ленту персоны; см. также
        // `event image_backfilled` в `ServerMessage`). Префикс `ClaudeHomeServer.Protocol`
        // снят (волна 3), чтобы сторож ловил новые зависимости от любых из ~105
        // публичных типов протокола (включая десктопный `DesktopCallCommand`/`DeviceHello`).
        new object[]
        {
            new VerticalBoundary(
                "Images",
                "ClaudeHomeServer.Services.Images",
                SharedAllowedPrefixes
                    .Concat(new[]
                    {
                        "ClaudeHomeServer.Services.Images",
                        "ClaudeHomeServer.Hubs",
                    })
                    .ToArray(),
                new[]
                {
                    "ClaudeHomeServer.Services.PersonaManager",
                    // ссылку на базовый record ServerMessage материализует наследование
                    // ImageBackfilledMessage (объявлен в самой вертикали,
                    // ImageBackfillService.cs:21).
                    "ClaudeHomeServer.Protocol.ServerMessage",
                }),
        },
        // Tts — вертикаль озвучки голосового режима чата. Допуск к корню Services
        // точечный: `PersonaManager` (Services/ корень) — голос персоны как часть
        // цепочки склейки в `VoiceResolver` (VoiceResolver.cs:16); связь «вертикаль →
        // спинка» (доменная модель пользователей и персон), а не на другую вертикаль.
        // `TtsVoiceCatalog` (Services/Tts) — статический каталог белого списка голосов;
        // его используют СНАРУЖИ вертикали `PersonaManager` и `PersonasController`, но это
        // сознательная обратная стрелка «спина → каталог вертикали», а не зависимость
        // самой вертикали Tts от чужой вертикали.
        new object[]
        {
            new VerticalBoundary(
                "Tts",
                "ClaudeHomeServer.Services.Tts",
                SharedAllowedPrefixes
                    .Concat(new[]
                    {
                        "ClaudeHomeServer.Services.Tts",
                    })
                    .ToArray(),
                new[]
                {
                    "ClaudeHomeServer.Services.PersonaManager",
                }),
        },
        // Git — НИЖНИЙ слой вертикалей (на него смотрят будущие Dossiers/Knowledge/Deploy,
        // плюс hosted-сервисы SessionManager/ProjectManager). Допуск к корню Services
        // точечный:
        // 1) `SessionManager`, `ProjectManager`, `UserStore`, `ProjectFileSessionsIndex`
        //    (Services/ корень) — общая инфраструктура (GitAutoCommitService:14-20,
        //    CommitAttributionService:19-20, GitServerService:18).
        // 2) `ClaudeHomeServer.Services.Execution` — `ILauncherFactory`, через который
        //    GitService запускает процессы git (источник истины, как в задаче).
        // 3) `ClaudeHomeServer.Services.Llm` — `ICheapTextRunner` для генерации сообщения
        //    коммита и имени стэша в `GitAiService`. Это сознательная связь «вертикаль →
        //    спинка»: LLM-инфраструктура общего назначения (дешёвые one-shot ходы через
        //    локальную модель или haiku), используемая и другими разделами (теги заметок,
        //    сводки, память...). Вынос Llm в отдельный allow-list вместо расширения
        //    SharedAllowedPrefixes — чтобы не открывать любой подсистеме весь
        //    `ClaudeHomeServer.Services.Llm`.
        // 4) `ClaudeHomeServer.Hubs` — `IHubContext<SessionHub>` для нотификации об
        //    авто-коммите (GitAutoCommitService отправляет событие в ленту сессии).
        // 5) `ClaudeHomeServer.Protocol` — префикс СНЯТ (волна 3) как полностью
        //    избыточный: `GitTurnCommitMessage`/`GitStatusChangedMessage` создаются в
        //    аргументах SendAsync и не переживают await (нет поля state-машины),
        //    а метод OnSessionMessageAsync с ServerMessage в сигнатуре — private,
        //    сторож читает только public-методы. Поэтому убираем префикс, а
        //    `AllowedExactNamespaces` для `ClaudeHomeServer.Protocol.*` оставляем
        //    пустым — это сознательный нулевой allow-list (по аналогии со снятым
        //    `Services.Llm` у Watchdog в волне 1): если вертикаль получит поле/параметр
        //    типа из Protocol, сторож поймает это сразу.
        new object[]
        {
            new VerticalBoundary(
                "Git",
                "ClaudeHomeServer.Services.Git",
                SharedAllowedPrefixes
                    .Concat(new[]
                    {
                        "ClaudeHomeServer.Services.Git",
                        "ClaudeHomeServer.Services.Execution",
                        "ClaudeHomeServer.Services.Llm",
                        "ClaudeHomeServer.Hubs",
                    })
                    .ToArray(),
                new[]
                {
                    "ClaudeHomeServer.Services.SessionManager",
                    "ClaudeHomeServer.Services.ProjectManager",
                    "ClaudeHomeServer.Services.UserStore",
                    "ClaudeHomeServer.Services.ProjectFileSessionsIndex",
                }),
        },
        // CodeGraph — вертикаль графа зависимостей кода (узлы — типы, рёбра — Calls/Implements/References).
        // Пост-билд фаза `ConfigureApp` регистрирует языковые провайдеры (`.cs`/`.ts`/`.tsx`),
        // MCP-тулсет `CodeGraphToolset` живёт в `Services/Mcp/Http` и регистрируется в Program.cs.
        // Допуски к корню Services — точечные:
        // 1) `ProjectManager` (Services/ корень) — граф зависит от проектов
        //    (`CodeGraphService.cs:43` параметр ctor и поле `_projects:15`).
        // `WorkspaceKnowledgeStore.NormalizePath` — статический вызов из тел методов
        // `CodeGraphService`/`/QueryService`/`/PromptProvider` (невидим рефлексии,
        // см. «Известное ограничение» в шапке файла); шов через `WorkspaceKnowledgeStore`
        // оставляем как есть, отдельный allow-list под static-вызов не нужен.
        // `LocalProcessRunner.ResolveExecutable("node")` в `TypeScriptGraphProvider:155` —
        // аналогичный static-вызов из `Services.Execution`, рефлексия его не видит.
        new object[]
        {
            new VerticalBoundary(
                "CodeGraph",
                "ClaudeHomeServer.Services.CodeGraph",
                SharedAllowedPrefixes
                    .Concat(new[]
                    {
                        "ClaudeHomeServer.Services.CodeGraph",
                    })
                    .ToArray(),
                new[]
                {
                    "ClaudeHomeServer.Services.ProjectManager",
                }),
        },
        // Deploy — вертикаль выкатки прода (ADR-010 + трей-раннер из веб-морды).
        // Допуск к корню Services точечный:
        // 1) `SessionManager` и `NotificationService` (Services/ корень) для
        //    `DeployReportService:15-20` (доклад об итоге выкатки в чат-инициатор и
        //    push-уведомление) — «вертикаль → спинка» (общая инфраструктура),
        //    аналогично `Git`/`Tts`/`Images`/`Reader`.
        // 2) `ClaudeHomeServer.Services.Execution` — `ILauncherFactory` для `schtasks`
        //    (`DeployHost.WakeAgentAsync` будит задачу планировщика через `launchers.Local`);
        //    легально: Execution — нижний слой, общий для всех, кто запускает процессы.
        // 3) `ClaudeHomeServer.Services.Git` — `GitService` для git-guard в `DeployHost`
        //    (проба репозитория: `rev-parse HEAD` и `status --porcelain`). Это СОЗНАТЕЛЬНАЯ
        //    связь «вертикаль → вертикаль» (TODO на шов: завести `IGitGuard` в `Services.Git`
        //    и перевести `DeployHost` на него, тогда `Services.Git` уйдёт из allow-list);
        // 4) `ClaudeHomeServer.Services.Backup` — статический класс `Backup.InstanceLock` с
        //    методом `TryAcquireDeploy()`, через который `DeployHost.TryLockAgent` берёт
        //    мьютекс `Global\ccs-deploy`. Это инфраструктурный примитив общего назначения
        //    (мьютекс деплоя), а не зависимость от логики Backup, и СОЗНАТЕЛЬНО выходит
        //    за рамки обычной рефлексии: доступ к статическому члену через точку не
        //    попадает в поля/конструкторы/return-типы, и без явного allow-list сторож
        //    этот шов пропустит. TODO на шов: выделить мьютекс в отдельный примитив
        //    (например, `DeployAgentLock` в `Services.Composition`) и убрать из allow-list
        //    ссылку на `Services.Backup`.
        new object[]
        {
            new VerticalBoundary(
                "Deploy",
                "ClaudeHomeServer.Services.Deploy",
                SharedAllowedPrefixes
                    .Concat(new[]
                    {
                        "ClaudeHomeServer.Services.Deploy",
                        "ClaudeHomeServer.Services.Execution",
                        "ClaudeHomeServer.Services.Git",
                        "ClaudeHomeServer.Services.Backup",
                    })
                    .ToArray(),
                new[]
                {
                    "ClaudeHomeServer.Services.SessionManager",
                    "ClaudeHomeServer.Services.NotificationService",
                }),
        },
        // Backgrounds — вертикаль фона рабочего пространства проекта (ADR-008).
        // Допуски:
        // 1) `ClaudeHomeServer.Services.Llm` — `ICheapTextRunner` для хода модели,
        //    который генерирует JSON с фигурами (префикс-шов, как у `Git`/`Deploy`).
        // 2) Допуски к корню Services — точные: `ProjectManager` (запись фона и флаг
        //    `Background` в доменной модели проекта), `UserStore` (перечень владельцев
        //    для массового прогона `RunAllAsync` в `ProjectBackgroundBackfill`).
        new object[]
        {
            new VerticalBoundary(
                "Backgrounds",
                "ClaudeHomeServer.Services.Backgrounds",
                SharedAllowedPrefixes
                    .Concat(new[]
                    {
                        "ClaudeHomeServer.Services.Backgrounds",
                        "ClaudeHomeServer.Services.Llm",
                    })
                    .ToArray(),
                new[]
                {
                    "ClaudeHomeServer.Services.ProjectManager",
                    "ClaudeHomeServer.Services.UserStore",
                }),
        },
        // ProjectIcons — вертикаль значка проекта (ADR-009). Допуски:
        // 1) `ClaudeHomeServer.Services.Llm` — `ICheapTextRunner` для двухходового
        //    подбора имени иконки (префикс-шов, как у `Git`/`Backgrounds`/`Deploy`).
        // 2) Допуск к корню Services — точечный: `ProjectManager` (запись значка и
        //    флаг `Icon.Glyph` в доменной модели проекта). `BackupCore.Snapshot` /
        //    `BackupContext.FromConfiguration` в `ProjectIconMigration` — статические
        //    вызовы из тел методов; рефлексия стражей их НЕ видит (см. «Известное
        //    ограничение»), выделение мьютекса бэкапа в шов — отдельная задача.
        new object[]
        {
            new VerticalBoundary(
                "ProjectIcons",
                "ClaudeHomeServer.Services.ProjectIcons",
                SharedAllowedPrefixes
                    .Concat(new[]
                    {
                        "ClaudeHomeServer.Services.ProjectIcons",
                        "ClaudeHomeServer.Services.Llm",
                    })
                    .ToArray(),
                new[]
                {
                    "ClaudeHomeServer.Services.ProjectManager",
                    // `BackupResult` (тип возврата `BackupCore.Snapshot` в `ProjectIconMigration.RunAsync`)
                    // — поле async-state-машины `<RunAsync>d__9`. Сам статический вызов
                    // рефлексия НЕ видит (см. «Известное ограничение»), но возвращаемый
                    // тип через `var backup = BackupCore.Snapshot(...)` материализуется
                    // компилятором C# в поле state-машины. Точечный FullName, чтобы не
                    // открывать вертикаль Backup целиком: миграция значков пользуется
                    // инфраструктурным примитивом снятия снимка, а не логикой Backup.
                    "ClaudeHomeServer.Services.Backup.BackupResult",
                }),
        },
        // Spend — аналитика расхода токенов (Spend Analytics v2). Самая «толстая» по
        // количеству зависимостей вертикаль волны 2: дашборд резолвит имена/метаданные
        // по всем доменам, а фоновый maintenance — ещё историю чатов для backfill.
        // Допуски к корню Services — точечные:
        // 1) `SessionManager`, `ProjectManager`, `TaskManager`, `PersonaManager`,
        //    `UserStore` (`SpendAnalyticsService.cs:65-67`) — резолв имён и метаданных
        //    в дашборде (чаты, проекты, задачи, персоны, пользователи).
        // 2) `ChatHistoryService` (`SpendMaintenanceService.cs:15`) — backfill истории
        //    расхода из сохранённых транскриптов при первом запуске.
        // 3) `ClaudeHomeServer.Services.Llm` — `LlmProviderRegistry` для расчёта
        //    стоимости по прайсу провайдера (тот же шов «вертикаль → спинка LLM», что
        //    у `Git`/`Backgrounds`/`Deploy`): цены живут в одном месте на все
        //    потребительские разделы, вынос из SharedAllowedPrefixes держит гейт узким.
        // 4) Точечный допуск к `ClaudeHomeServer.Protocol` — типы WS-событий, которые
        //    `SpendMaintenanceService.BackfillAsync` разбирает из истории чатов при
        //    первичном наполнении стора: `StoredMessage`/`StoredResultMessage`
        //    (попадают в поля async-state-машины `<BackfillAsync>d__7` через `var`),
        //    плюс `StoredFalCostMessage`/`StoredGlifCostMessage`/`UsageInfo` для
        //    подсчёта стоимости провайдеров по прайсу. Префикс `ClaudeHomeServer.Protocol`
        //    снят (волна 3), чтобы сторож ловил новые зависимости от любых из ~105
        //    публичных типов протокола (включая десктопный `DesktopCallCommand`/`DeviceHello`).
        // `SpendStore` форвардит `ISpendCollector` через `sp => ...GetRequiredService<SpendStore>()` —
        // инвариант «интерфейс и конкретный тип указывают на ОДИН инстанс» (тест
        // `SpendSubsystemRegistrationTests.Register_SpendCollector_IsSameInstanceAsStore`).
        new object[]
        {
            new VerticalBoundary(
                "Spend",
                "ClaudeHomeServer.Services.Spend",
                SharedAllowedPrefixes
                    .Concat(new[]
                    {
                        "ClaudeHomeServer.Services.Spend",
                        "ClaudeHomeServer.Services.Llm",
                    })
                    .ToArray(),
                new[]
                {
                    "ClaudeHomeServer.Services.SessionManager",
                    "ClaudeHomeServer.Services.ProjectManager",
                    "ClaudeHomeServer.Services.TaskManager",
                    "ClaudeHomeServer.Services.PersonaManager",
                    "ClaudeHomeServer.Services.UserStore",
                    "ClaudeHomeServer.Services.ChatHistoryService",
                    // SpendMaintenanceService.cs: BackfillAsync — поля async-state-машины
                    // материализуют возвращаемые типы из истории чатов.
                    "ClaudeHomeServer.Protocol.StoredMessage",
                    "ClaudeHomeServer.Protocol.StoredResultMessage",
                    "ClaudeHomeServer.Protocol.StoredFalCostMessage",
                    "ClaudeHomeServer.Protocol.StoredGlifCostMessage",
                    "ClaudeHomeServer.Protocol.UsageInfo",
                }),
        },
        // Dossiers — паспорта изменений (ADR-004). Сознательно завязана на две
        // «вертикали-нижнего-слоя» (Git/CodeGraph) — TODO на швы по прецеденту Deploy→Git,
        // и общий слой Dify-синка (Services.Memory). Допуски к корню Services точечные:
        // 1) `SessionManager`/`ProjectManager`/`TaskManager`/`FileService`/`UserStore`
        //    — общая инфраструктура (DossierCaptureService.cs:39-42, DossierStore.cs:41-42,
        //    DossierDiscussionService.cs:27, DossierRecallService:tasks/file params).
        // 2) `KnowledgeService` (DossierStore.cs:40, опциональный параметр ctor) — тот же
        //    шов «вертикаль → спинка», что у `Spend` (KnowledgeService — общий для Dify-синка).
        // 3) `FeatureFlagService` (DossierAutoExporter.cs:44, DossierAutoImporter.cs:36) —
        //    гейт флага `change-dossiers-recall` владельца.
        // Префиксы-швы:
        // 4) `ClaudeHomeServer.Services.Git` — `GitService` для захвата коммитов
        //    (DossierCaptureService.cs:53), recall (DossierRecallService),
        //    автовыгрузки/импорта (DossierAutoExporter/Importer). TODO на шов:
        //    завести `IGitGuard` в `Services.Git` и перевести вертикаль на него
        //    (по прецеденту Deploy→Git).
        // 5) `ClaudeHomeServer.Services.CodeGraph` — `CodeGraphService` для обогащения
        //    паспортов графом кода (DossierCaptureService.cs:54, DossierRecallService).
        //    TODO на шов аналогично Git.
        // 6) `ClaudeHomeServer.Services.Llm` — `ICheapTextRunner` для выжимки паспортов
        //    и конспектов (DossierCaptureService, DossierDiscussionService). Префикс-шов,
        //    как у `Git`/`Backgrounds`/`Deploy`/`Spend`.
        // 7) `ClaudeHomeServer.Services.Memory` — общий слой Dify-синка: `MemoryDocRef`,
        //    `MemoryDifyDebouncer` и `MemorySyncItem` в полях `DossierStore`/`DossierAutoExporter`
        //    (DossierStore.cs:21,48; DossierAutoExporter.cs:48). Префикс-шов, как
        //    `Git`/`Deploy` на `Services.Backup`.
        // Точечный допуск к `ClaudeHomeServer.Protocol`:
        // 8) `StoredMessage` — поля async-state-машин `DossierCaptureService+<BuildTranscriptAsync>d__39`
        //    и `DossierDiscussionService+<EnsureOneAsync>d__10` (сами методы private/internal —
        //    сторож их сигнатуры не читает). `ServerMessage` ТУТ НЕ нужен: фигурирует только
        //    в private-методе `DossierCaptureService.OnSessionMessageAsync`, а сторож читает
        //    только public-методы (см. «Известное ограничение» в шапке файла).
        new object[]
        {
            new VerticalBoundary(
                "Dossiers",
                "ClaudeHomeServer.Services.Dossiers",
                SharedAllowedPrefixes
                    .Concat(new[]
                    {
                        "ClaudeHomeServer.Services.Dossiers",
                        "ClaudeHomeServer.Services.Git",
                        "ClaudeHomeServer.Services.CodeGraph",
                        "ClaudeHomeServer.Services.Llm",
                        "ClaudeHomeServer.Services.Memory",
                    })
                    .ToArray(),
                new[]
                {
                    "ClaudeHomeServer.Services.SessionManager",
                    "ClaudeHomeServer.Services.ProjectManager",
                    "ClaudeHomeServer.Services.TaskManager",
                    "ClaudeHomeServer.Services.FileService",
                    "ClaudeHomeServer.Services.UserStore",
                    "ClaudeHomeServer.Services.KnowledgeService",
                    "ClaudeHomeServer.Services.FeatureFlagService",
                    // DossierStore — участник реконсайлера error-документов Dify
                    // (KnowledgeIndexReconciler, ADR-004 §4); public-метод ListTargets
                    // возвращает `IReadOnlyList<Knowledge.KnowledgeSyncTarget>` —
                    // рефлексия видит `KnowledgeSyncTarget` как возвращаемый тип
                    // и в generic-аргументе. Шов через IKnowledgeSyncParticipant
                    // (DossierStore имплементирует интерфейс) рефлексия не видит,
                    // см. «Известное ограничение» в шапке. Форвардер регистрации
                    // `IKnowledgeSyncParticipant → DossierStore` остаётся в блоке
                    // Knowledge (Program.cs:~688) до выделения Knowledge — отдельный
                    // шаг волны 3.
                    "ClaudeHomeServer.Services.Knowledge.KnowledgeSyncTarget",
                    "ClaudeHomeServer.Protocol.StoredMessage",
                }),
        },
        // Watchdog — серверные сторожа чатов (ADR-013). Вертикаль без реализации
        // `IAppSubsystem` (подаётся в Program.cs как обычные `AddSingleton`/
        // `AddHostedService`), поэтому попадает в таблицу вручную — зато сторож
        // полноты `SubsystemBoundaryCoverageTests` не пропустит её удаление.
        // Допуски:
        // 1) `ClaudeHomeServer.Services.Execution` — `ILauncherFactory` для poll-команды
        //    (WatchdogRunner.cs:3);
        // 2) `ClaudeHomeServer.Hubs` — `IHubContext<SessionHub>` для события
        //    `watchdogs_changed` (WatchdogNotifier.cs:22-23);
        // 3) Точечный допуск к `ClaudeHomeServer.Protocol`: `WatchdogsChangedMessage`
        //    (WatchdogNotifier.cs:3) — единственный тип протокола, на который ссылается
        //    вертикаль. Префикс `ClaudeHomeServer.Protocol` снят (волна 3), чтобы
        //    сторож ловил новые зависимости от любых из ~105 публичных типов протокола
        //    (включая десктопный `DesktopCallCommand`/`DeviceHello`).
        // 4) Допуски к корню Services — точные: серверные сторожа должны знать про чаты,
        //    проекты, юзеров и домашние папки, чтобы гаситься при удалении/архивации
        //    и резолвить рабочий каталог опроса. Это «вертикаль → спинка» (общая
        //    инфраструктура), по аналогии с `Git`/`Deploy`. Шов через явные
        //    `SessionManager`/`ProjectManager`/`UserStore`/`UserHomeResolver` —
        //    тестируется через `WatchdogEnvironment` (см. WatchdogEnvironment.cs:26-30).
        // 5) `ClaudeHomeServer.Services.SessionMessagingService` (точное) — `SessionMessagingService`
        //    для будильника (WatchdogAlarm.cs:17); nested `SendOutcome` едет в async-state-машине
        //    `WatchdogAlarm.DeliverAsync`. Сам `SessionMessagingService` живёт в namespace
        //    `ClaudeHomeServer.Services` (корень, см. SessionMessagingService.cs:3), поэтому
        //    префикс `ClaudeHomeServer.Services.Llm` НЕ нужен — точного допуска хватает.
        //    (Раньше префикс был — декорация, не гейт; убран вместе с фиксом default-deny.)
        new object[]
        {
            new VerticalBoundary(
                "Watchdog",
                "ClaudeHomeServer.Services.Watchdog",
                SharedAllowedPrefixes
                    .Concat(new[]
                    {
                        "ClaudeHomeServer.Services.Watchdog",
                        "ClaudeHomeServer.Services.Execution",
                        "ClaudeHomeServer.Hubs",
                    })
                    .ToArray(),
                new[]
                {
                    "ClaudeHomeServer.Services.SessionManager",
                    "ClaudeHomeServer.Services.ProjectManager",
                    "ClaudeHomeServer.Services.UserStore",
                    "ClaudeHomeServer.Services.UserHomeResolver",
                    "ClaudeHomeServer.Services.SessionMessagingService",
                    "ClaudeHomeServer.Services.SessionMessagingService+SendOutcome",
                    // WatchdogNotifier.cs:3 — поле async-state-машины
                    // WatchdogNotifier+<BroadcastAsync>d__8 (локал msg переживает await).
                    "ClaudeHomeServer.Protocol.WatchdogsChangedMessage",
                }),
        },
    };

    [Theory]
    [MemberData(nameof(Boundaries))]
    public void Vertical_НеСсылаетсяНаДругиеВертикали(VerticalBoundary boundary)
    {
        // Привязка к типу из подсистемы как точке входа в нужную сборку: проект тестов
        // ссылается на ClaudeHomeServer, typeof(...).Assembly гарантированно даёт её.
        // Подсистема `video` — первая в таблице, её тип доступен как якорь сборки.
        var assembly = typeof(ClaudeHomeServer.Services.Video.VideoSubsystem).Assembly;

        var types = CollectTypesInNamespaceTree(assembly, boundary.NamespaceRoot);

        var violations = new List<string>();

        foreach (var type in types)
        {
            // Дедупликация: одно поле record-а порождает несколько упоминаний (поле,
            // конструктор, свойство, get/set, Equals/PrintMembers). В отчёте достаточно
            // одной строки на пару «тип-источник → тип-нарушитель».
            var seen = new HashSet<(string, string)>();

            foreach (var referenced in CollectReferencedTypes(type))
            {
                if (!IsAllowed(referenced, boundary.AllowedNamespacePrefixes, boundary.AllowedExactNamespaces))
                {
                    seen.Add((type.FullName ?? type.Name, referenced.FullName ?? referenced.Name));
                }
            }

            foreach (var (owner, forbidden) in seen)
            {
                violations.Add(
                    $"{boundary.VerticalName}: {owner} ссылается на {forbidden} " +
                    "из чужой вертикали (нет в AllowedNamespacePrefixes/AllowedExactNamespaces)");
            }
        }

        violations.Should().BeEmpty(
            $"типы из {boundary.NamespaceRoot} должны ссылаться только на спинку " +
            "(System.*, Microsoft.*, Models) и явно разрешённые служебные вертикали " +
            "(Services.Http/Composition/Mcp) либо на самих себя. " +
            "Любая ссылка на прочие Services.* — нарушение архитектурного правила " +
            "(см. CLAUDE.md/ADR-014). Найденные нарушения:\n" +
            string.Join("\n", violations));
    }

    private static IEnumerable<Type> CollectTypesInNamespaceTree(Assembly assembly, string namespaceRoot)
    {
        // Сам неймспейс + все вложенные поднеймспейсы.
        return assembly.GetTypes()
            .Where(t => t.Namespace == namespaceRoot
                        || (t.Namespace?.StartsWith(namespaceRoot + ".", StringComparison.Ordinal) ?? false));
    }

    private static IEnumerable<Type> CollectReferencedTypes(Type type)
    {
        // Поля: declared-only, чтобы не утонуть в чужом базовом классе; private тоже —
        // границу нарушает любой член, а не только публичный контракт.
        var memberBinding = BindingFlags.Public | BindingFlags.NonPublic
                          | BindingFlags.Instance | BindingFlags.Static
                          | BindingFlags.DeclaredOnly;

        foreach (var field in type.GetFields(memberBinding))
        {
            foreach (var t in EnumerateTypeAndArgs(field.FieldType))
                yield return t;
        }

        foreach (var ctor in type.GetConstructors(memberBinding))
        {
            foreach (var parameter in ctor.GetParameters())
            {
                foreach (var t in EnumerateTypeAndArgs(parameter.ParameterType))
                    yield return t;
            }
        }

        // Публичные методы и свойства — все, включая унаследованные. Унаследованный
        // метод с типом из запрещённой вертикали — это часть публичного контракта
        // проверяемого типа (через него ссылка «торчит наружу»), и сторож должен
        // её ловить. Обход свойств отдельным проходом: get/set не попадают в GetMethods
        // под теми именами, по которым мы ищем нарушение.
        var publicBinding = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;

        foreach (var property in type.GetProperties(publicBinding))
        {
            foreach (var t in EnumerateTypeAndArgs(property.PropertyType))
                yield return t;
        }

        foreach (var method in type.GetMethods(publicBinding))
        {
            foreach (var t in EnumerateTypeAndArgs(method.ReturnType))
                yield return t;

            foreach (var parameter in method.GetParameters())
            {
                foreach (var t in EnumerateTypeAndArgs(parameter.ParameterType))
                    yield return t;
            }
        }
    }

    private static IEnumerable<Type> EnumerateTypeAndArgs(Type? type)
    {
        if (type is null) yield break;

        // Byref (ref/out/in SomeType) — ParameterType вернёт SomeType&, у которого
        // Namespace = null и IsAllowed безусловно пропустит (не Services.*). Снимаем обёртку сразу.
        if (type.IsByRef)
        {
            foreach (var t in EnumerateTypeAndArgs(type.GetElementType()))
                yield return t;
            yield break;
        }

        // Nullable<T> → T (System.Nullable<...> не интересует, зато интересует аргумент).
        var underlying = Nullable.GetUnderlyingType(type);
        if (underlying is not null)
        {
            yield return underlying;
            yield break;
        }

        // Сам тип (например, List<DesktopFoo> сам по себе не из разрешённого неймспейса,
        // но мы его всё равно отдаём — IsAllowed отфильтрует).
        yield return type;

        if (type.IsGenericType)
        {
            // Рекурсия: вложенные generic-аргументы тоже надо раскрыть, иначе тип вида
            // Nested<Wrap<DesktopFoo>> пройдёт мимо стора (Namespace внешнего generic
            // может быть «нейтральным»).
            foreach (var arg in type.GetGenericArguments())
            {
                yield return arg;
                foreach (var t in EnumerateTypeAndArgs(arg))
                    yield return t;
            }
        }

        // Массивы, указатели — раскрываем рекурсивно (на глубину 1 достаточно:
        // массив массивов экзотика, на которую обопрёмся, если встретим).
        if (type.HasElementType)
        {
            foreach (var t in EnumerateTypeAndArgs(type.GetElementType()))
                yield return t;
        }
    }

    // Сначала проверяем точное совпадение FullName (одноуровневые синглтоны из
    // корня Services: PersonaManager, SessionManager и т.п., плюс nested-типы
    // вроде `SsrfGuard+AddressCheck` и `SessionMessagingService+SendOutcome`) —
    // они НЕ открывают поддерево. Затем — префикс, как раньше.
    //
    // Сравнение по FullName, а не по Namespace, обязательно для nested-типов:
    // `SsrfGuard+AddressCheck` живёт в namespace `ClaudeHomeServer.Services`
    // (родительский), и проверка `ns == exact` его не поймает — пришлось бы
    // открывать всю корневую Services, что и есть запрещённое default-deny-разрушение.
    //
    // Раньше единственное поле было префиксом, и корень `"ClaudeHomeServer.Services"`
    // в allow-list открывал подсистеме весь `Services.*`. Теперь точные имена
    // вынесены в AllowedExactNamespaces и срабатывают по `type.FullName == exact`,
    // а префикс разрешает только поддеревья.
    private static bool IsAllowed(Type type, string[] allowedPrefixes, string[] allowedExact)
    {
        var ns = type.Namespace;
        if (ns is null) return true; // Безымянный namespace — не Services.*, разрешаем.

        foreach (var exact in allowedExact)
        {
            // `type.FullName` для не-nested совпадает с `ns`, для nested содержит
            // `+ИмяВложенного` (например, `ClaudeHomeServer.Services.SsrfGuard+AddressCheck`).
            if (type.FullName == exact) return true;
        }

        foreach (var prefix in allowedPrefixes)
        {
            if (ns == prefix
                || ns.StartsWith(prefix + ".", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}