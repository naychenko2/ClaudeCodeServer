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
    // До multi-assembly-фикса typeof(VideoSubsystem).Assembly форсировал загрузку Main —
    // сторож получал ровно её типы и не задумывался о lazy-load. После фикса сторож
    // перебирает AppDomain.CurrentDomain.GetAssemblies(): если ни один тест в этом
    // testhost до сих пор не тронул Main, она не загружена, и сторож проходит
    // вакуумно — выборка по любой вертикали в Main будет пустой. Статический
    // конструктор гарантирует загрузку Main при первом обращении к типу этого класса,
    // и сторож видит обе сборки (Core + Main).
    static SubsystemBoundaryTests()
    {
        // До multi-assembly-фикса typeof(...).Assembly форсировал загрузку Main
        // и сторож получал её типы по умолчанию. После фикса сторож перебирает
        // AppDomain.CurrentDomain.GetAssemblies() с фильтром по имени —
        // без явного форс-референса Main не загружается, если её никто не тронул
        // до этого теста (сборка-точка-точка ленивая), и сторож проходит
        // вакуумно по всем вертикалям в Main. Этот статический конструктор
        // гарантирует загрузку Main при первом обращении к типу этого класса.
        _ = typeof(ClaudeHomeServer.Services.Video.VideoSubsystem).Assembly;
    }

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
        // 2) `KnowledgeService` (DossierStore.cs:40, опциональный параметр ctor) — клиент
        //    Dify, шов «вертикаль → спинка» по аналогии с `Spend` (KnowledgeService — общий
        //    для Dify-синка). После переноса в `Services.Knowledge` имя в FullName сохраняется,
        //    поэтому и тут обновляем префикс.
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
                    "ClaudeHomeServer.Services.FeatureFlagService",
                    "ClaudeHomeServer.Services.Knowledge.KnowledgeService",
                    // DossierStore — участник реконсайлера error-документов Dify
                    // (KnowledgeIndexReconciler, ADR-004 §4); public-метод ListTargets
                    // возвращает `IReadOnlyList<Knowledge.KnowledgeSyncTarget>` —
                    // рефлексия видит `KnowledgeSyncTarget` как возвращаемый тип
                    // и в generic-аргументе. Шов через IKnowledgeSyncParticipant
                    // (DossierStore имплементирует интерфейс) рефлексия не видит,
                    // см. «Известное ограничение» в шапке. Форвардер регистрации
                    // `IKnowledgeSyncParticipant → DossierStore` остаётся в блоке
                    // Knowledge (Program.cs:~688) — кросс-вертикальный клей.
                    "ClaudeHomeServer.Services.Knowledge.KnowledgeSyncTarget",
                    "ClaudeHomeServer.Protocol.StoredMessage",
                }),
        },
        // Knowledge — вертикаль Dify RAG (Knowledge.md + ADR-013 §4). Сторож проверяет
        // типы из `Services.Knowledge` (KnowledgeAlertNotifier, KnowledgeIndexReconciler,
        // IKnowledgeSyncParticipant/KnowledgeSyncTarget, KnowledgeService, WorkspaceKnowledgeStore,
        // KnowledgeBaseCatalogService, ProjectKnowledgeSyncService, UserKnowledgeCascade,
        // а также вложенные типы KnowledgeService — DifyDocumentItem/DifyDocumentsPage).
        // Префикс-шов: `ClaudeHomeServer.Hubs` — `IHubContext<SessionHub>` для событий
        // `knowledge_changed` (KnowledgeController, DifyToolset) и `ProjectKnowledgeTurnSync`
        // (hosted-мост событий хода Claude на FileService.OnMutated).
        // Допуски к корню Services — точечные (по образцу Dossiers/Spend):
        // 1) `NotificationService`/`NotificationStore` (Services/ корень) — `KnowledgeAlertNotifier`
        //    шлёт алерт владельцу через общий нотификатор (KnowledgeAlertNotifier.cs:23-24),
        //    «вертикаль → спинка» (общая инфраструктура).
        // 2) `UserStore` (Services/ корень) — `KnowledgeBaseCatalogService` (для доступа
        //    к списку пользователей, по префиксу имени определяется «своя»/«чужая» БЗ).
        // 3) `ProjectManager` (Services/ корень) — `ProjectKnowledgeSyncService`/
        //    `ProjectKnowledgeTurnSync`/`UserKnowledgeCascade` для разрешения пути
        //    проекта и удаления каскада (UserKnowledgeCascade удаляет все БЗ проекта).
        // 4) `FileService` (Services/ корень) — `ProjectKnowledgeSyncService` подписан
        //    на `FileService.OnMutated` через мост хода, видит поле типа `IHubContext`.
        // 5) `SessionManager` (Services/ корень) — `ProjectKnowledgeTurnSync` пишет
        //    `session_known` снапшоты через `SessionManager` (поле `_sessions`).
        // 6) `PersonaManager`/`PersonaMemoryService` (Services/ корень) — `UserKnowledgeCascade`
        //    удаляет персональные БЗ и Dify-датасеты персон при удалении пользователя.
        // 7) `TeamMemoryService` (Services/ корень) — `UserKnowledgeCascade` чистит
        //    team-memory-датасеты per-проект.
        // 8) `Dossiers.DossierStore` (Services/Dossiers) — `UserKnowledgeCascade` чистит
        //    dossiers-датасеты per-проект. Сознательная зависимость: каскадная уборка
        //    знаний идёт по ВСЕМ владельцам стора «запись → Dify-документ», как и форвардер
        //    `IKnowledgeSyncParticipant → DossierStore` в Program.cs. Выделение явного
        //    интерфейса «владелец Dify-датасета» — отдельная задача.
        // 9) `NotesKnowledgeService` (Services/ корень) — `UserKnowledgeCascade` чистит
        //    notes-датасет; на него уже есть форвардер `IKnowledgeSyncParticipant →
        //    NotesKnowledgeService` в Program.cs, каскад идёт той же логикой.
        // Допуски к `ClaudeHomeServer.Controllers` (DTO):
        // 10) `KnowledgeBaseSummary`/`KnowledgeBaseDetail`/`KnowledgeDocumentDto`
        //    (Controllers) — `KnowledgeBaseCatalogService` отдаёт их же и REST, и MCP-тулсету
        //    (общая оркестрация; ADR-014 §Knowledge). DTO живут в Controllers как
        //    ASP.NET-контракт ответа — выделение отдельной сборки под общие DTO не делали.
        // Форвардеры `IKnowledgeSyncParticipant → {DossierStore, NotesKnowledgeService,
        // ProjectKnowledgeSyncService, ...}` остаются в Program.cs (кросс-вертикальный клей)
        // и поэтому НЕ входят в allow-list Knowledge.
        new object[]
        {
            new VerticalBoundary(
                "Knowledge",
                "ClaudeHomeServer.Services.Knowledge",
                SharedAllowedPrefixes
                    .Concat(new[]
                    {
                        "ClaudeHomeServer.Services.Knowledge",
                        "ClaudeHomeServer.Hubs",
                    })
                    .ToArray(),
                new[]
                {
                    "ClaudeHomeServer.Services.NotificationService",
                    "ClaudeHomeServer.Services.NotificationStore",
                    "ClaudeHomeServer.Services.UserStore",
                    "ClaudeHomeServer.Services.ProjectManager",
                    "ClaudeHomeServer.Services.FileService",
                    "ClaudeHomeServer.Services.SessionManager",
                    "ClaudeHomeServer.Services.PersonaManager",
                    "ClaudeHomeServer.Services.PersonaMemoryService",
                    "ClaudeHomeServer.Services.TeamMemoryService",
                    "ClaudeHomeServer.Services.Dossiers.DossierStore",
                    "ClaudeHomeServer.Services.NotesKnowledgeService",
                    "ClaudeHomeServer.Controllers.KnowledgeBaseSummary",
                    "ClaudeHomeServer.Controllers.KnowledgeBaseDetail",
                    "ClaudeHomeServer.Controllers.KnowledgeDocumentDto",
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
        // Memory — долгая память персон и общая память команды проекта. Подсистема
        // регистрирует только фасады (PersonaMemoryService/TeamMemoryService + их
        // консолидация/autolearn), а общий слой ядра (MemoryWriteResolver/MemoryDify/
        // MemoryConsolidationCore/AutolearnGate/...) живёт в этом namespace уже давно.
        // Допуски к корню Services — точные (по образцу Dossiers/Spend):
        // 1) `SessionManager`/`ProjectManager`/`PersonaManager` (Services/ корень) —
        //    общая инфраструктура для фасадов: SessionManager (подписка autolearn на
        //    OnSessionMessage), ProjectManager (резолв проекта для team-memory),
        //    PersonaManager (список персон для консолидации).
        // 2) `ProjectEventLogService` (Services/ корень) — `PersonaMemoryAutolearnService`
        //    логирует событие памяти в ленту проекта.
        // Префиксы-швы (как у Dossiers/Spend):
        // 3) `ClaudeHomeServer.Services.Knowledge` — общий клиент Dify и `IKnowledgeSyncParticipant`;
        //    `MemoryDify` использует `KnowledgeService`/`KnowledgeSyncTarget` для записи
        //    в Dify-датасеты (per-persona/per-project).
        // 4) `ClaudeHomeServer.Services.Llm` — `ICheapTextRunner` для консолидации (LLM-merge)
        //    и autolearn (извлечение фактов из транскрипта). Префикс-шов, как у
        //    `Git`/`Backgrounds`/`Deploy`/`Spend`/`Dossiers`.
        // 5) `ClaudeHomeServer.Hubs` — `IHubContext<SessionHub>` для нотификации о новых
        //    записях памяти команды (`TeamMemoryAutolearnService` шлёт `memory_changed`
        //    в ленту сессии).
        // Точечный допуск к `ClaudeHomeServer.Protocol`:
        // 6) `StoredMessage`/`StoredUserMessage`/`StoredTextMessage` — `AutolearnGate.CheckContent`
        //    и `LastTurnLength` принимают `IReadOnlyList<StoredMessage>` (видна в public-методе),
        //    `switch` по `StoredUserMessage`/`StoredTextMessage` в `LastTurnLength` — это
        //    pattern matching, рефлексия поля типа не видит, но аргументы публичного метода —
        //    видит. Префикс `ClaudeHomeServer.Protocol` снят (волна 3), чтобы сторож ловил
        //    новые зависимости от любых из ~105 публичных типов протокола.
        // ⚠ Два форвардера `IKnowledgeSyncParticipant → {PersonaMemoryService,
        // TeamMemoryService}` остаются в Program.cs (кросс-вертикальный клей реконсайлера
        // error-документов Dify) и потому НЕ входят в allow-list Memory.
        // Сами шесть типов фасадов (`PersonaMemoryService`/`TeamMemoryService`/`*Consolidation*`/
        // `*Autolearn*`) пока живут в `ClaudeHomeServer.Services` (root) — задача явно
        // ограничилась переносом регистраций, без рефакторинга имён/неймспейсов; полный
        // переезд в `Services.Memory` — отдельная задача. Поэтому в allow-list они идут
        // ТОЧНЫМИ именами, а не префиксом корня `ClaudeHomeServer.Services` (префикс был бы
        // разрушением default-deny).
        new object[]
        {
            new VerticalBoundary(
                "Memory",
                "ClaudeHomeServer.Services.Memory",
                SharedAllowedPrefixes
                    .Concat(new[]
                    {
                        "ClaudeHomeServer.Services.Memory",
                        "ClaudeHomeServer.Services.Knowledge",
                        "ClaudeHomeServer.Services.Llm",
                        "ClaudeHomeServer.Hubs",
                    })
                    .ToArray(),
                new[]
                {
                    "ClaudeHomeServer.Services.SessionManager",
                    "ClaudeHomeServer.Services.ProjectManager",
                    "ClaudeHomeServer.Services.PersonaManager",
                    "ClaudeHomeServer.Services.ProjectEventLogService",
                    // Шесть фасадов памяти — собственность подсистемы Memory, но пока
                    // остаются в `ClaudeHomeServer.Services` (root). Точные имена, чтобы
                    // не открывать корневой префикс.
                    "ClaudeHomeServer.Services.PersonaMemoryService",
                    "ClaudeHomeServer.Services.PersonaMemoryConsolidationService",
                    "ClaudeHomeServer.Services.PersonaMemoryAutolearnService",
                    "ClaudeHomeServer.Services.TeamMemoryService",
                    "ClaudeHomeServer.Services.TeamMemoryConsolidationService",
                    "ClaudeHomeServer.Services.TeamMemoryAutolearnService",
                    // AutolearnGate.CheckContent / LastTurnLength — public-метод
                    // с параметром IReadOnlyList<StoredMessage> и switch по
                    // StoredUserMessage/StoredTextMessage.
                    "ClaudeHomeServer.Protocol.StoredMessage",
                    "ClaudeHomeServer.Protocol.StoredUserMessage",
                    "ClaudeHomeServer.Protocol.StoredTextMessage",
                }),
        },
        // === Шаг 0 волны 4: пять целевых + семь найденных Coverage-тестом неймспейсов,
        // каждый со своим allow-list строго по фактическим ссылкам (прогон probe — см.
        // отчёт шага 0). Префиксы-швы используются ТОЛЬКО там, где Llm/Turn реально
        // держит связь с целым поддеревом (Execution — запуск процессов, Turn — шина
        // событий хода); точечные типы — везде, где связь идёт через 1–2 типа из
        // чужой вертикали. Не раздувать список без ссылки: ровно то, что нашёл probe.
        //
        // Llm — пилот волны 4 (305 типов, основной объём). Шовные префиксы:
        //   * Execution — IProcessLauncher/ILauncherFactory для OneShotClaudeRunner
        //     и ClaudeSession (запуск cli-процесса);
        //   * Turn — ITurnEventBus/TurnContext/PromptSection/PromptAssembling/
        //     PromptSessionContext для ClaudeSession (consume событий и секций
        //     промпта из шины Turn).
        // Точечные допуски: протокольные WS-типы (поля async-state-машин
        // FallbackLlmSessionAdapter и ClaudeSession), точечные root-типы из
        // `Services` (ModelTier/AppSettingsService/ChatHistoryService/...),
        // WorkspaceKnowledgeStore (ClaudeSession материализует в async-state)
        // и ISpendCollector (метрики трат).
        new object[]
        {
            new VerticalBoundary(
                "Llm",
                "ClaudeHomeServer.Services.Llm",
                SharedAllowedPrefixes
                    .Concat(new[]
                    {
                        "ClaudeHomeServer.Services.Llm",
                        "ClaudeHomeServer.Services.Execution",
                        "ClaudeHomeServer.Services.Turn",
                    })
                    .ToArray(),
                new[]
                {
                    // Protocol — поля async-state-машин (FallbackLlmSessionAdapter,
                    // ClaudeSession, LlamaServerClient.ReadChatStreamAsync,
                    // OllamaClient.ReadChatStreamAsync, LlmProviderRegistry,
                    // LlmSessionContext, TurnFileWatcher, ChatDigestService,
                    // ClaudeRateLimitParser, TurnPromptAssembler, SubagentStreamWatcher).
                    "ClaudeHomeServer.Protocol.StoredMessage",
                    "ClaudeHomeServer.Protocol.UsageInfo",
                    "ClaudeHomeServer.Protocol.ServerMessage",
                    "ClaudeHomeServer.Protocol.RateLimitMessage",
                    "ClaudeHomeServer.Protocol.ErrorMessage",
                    "ClaudeHomeServer.Protocol.ResultMessage",
                    "ClaudeHomeServer.Protocol.McpServerInfo",
                    "ClaudeHomeServer.Protocol.TurnWorktreeInfo",
                    "ClaudeHomeServer.Protocol.PromptSectionDto",
                    "ClaudeHomeServer.Protocol.CliSkillDto",
                    "ClaudeHomeServer.Protocol.RecallItemDto",
                    // Root Services — точные (общая инфраструктура: реестры, сторы,
                    // рантайм-состояния, доменные модели). Префикс `ClaudeHomeServer.Services`
                    // для них НЕ открываем (default-deny), только FullName.
                    "ClaudeHomeServer.Services.ModelTier",
                    "ClaudeHomeServer.Services.ModelTier[]",
                    "ClaudeHomeServer.Services.ModelRoutePreset",
                    "ClaudeHomeServer.Services.PresetScope",
                    "ClaudeHomeServer.Services.SkillInfo",
                    "ClaudeHomeServer.Services.SystemPromptPart",
                    "ClaudeHomeServer.Services.AppSettingsService",
                    "ClaudeHomeServer.Services.ChatHistoryService",
                    "ClaudeHomeServer.Services.ClaudeSubscriptionPool",
                    "ClaudeHomeServer.Services.NotesService",
                    "ClaudeHomeServer.Services.ProjectManager",
                    "ClaudeHomeServer.Services.SessionManager",
                    "ClaudeHomeServer.Services.SkillsService",
                    "ClaudeHomeServer.Services.SpecialtySettingsStore",
                    "ClaudeHomeServer.Services.SubscriptionActivityTracker",
                    "ClaudeHomeServer.Services.UserStore",
                    "ClaudeHomeServer.Services.WorkflowWatcher",
                    "ClaudeHomeServer.Services.ModelCatalogService",
                    "ClaudeHomeServer.Services.ModelCatalogService+ModelInfo",
                    // Knowledge — WorkspaceKnowledgeStore (CLI-сессия и фабрика
                    // адаптеров материализуют тип в async-state). Префикс не открываем:
                    // Knowledge — отдельная вертикаль, точечный допуск ровно на нужный тип.
                    "ClaudeHomeServer.Services.Knowledge.WorkspaceKnowledgeStore",
                    // Spend — ISpendCollector пишут все четыре ход-раннера (cloud-cheap,
                    // Ollama/LlamaServer и OneShot-Claude). Префикс не открываем.
                    "ClaudeHomeServer.Services.Spend.ISpendCollector",
                }),
        },
        // Docs — индекс документации (ADR). Единственная внешняя зависимость —
        // FileService (Services/ корень, точечный допуск).
        new object[]
        {
            new VerticalBoundary(
                "Docs",
                "ClaudeHomeServer.Services.Docs",
                SharedAllowedPrefixes
                    .Concat(new[] { "ClaudeHomeServer.Services.Docs" })
                    .ToArray(),
                new[]
                {
                    "ClaudeHomeServer.Services.FileService",
                }),
        },
        // Turn — шина событий хода + контрибьюторы секций системного промпта.
        // Префикс-шов: Services.CodeGraph (CodeGraphContributor читает
        // CodeGraphPromptProvider). Точечные: root Services, Dossiers
        // (PersonaRecallContributor тянет DossierRecallService/Request),
        // Services.Llm (RecallItem в полях контрибьюторов, TurnRunPassport
        // в TurnCompleted, SubagentRunPassport в SubagentRunCompleted),
        // Protocol (StoredMessage/McpServerInfo/PromptSnapshotDraft в полях).
        new object[]
        {
            new VerticalBoundary(
                "Turn",
                "ClaudeHomeServer.Services.Turn",
                SharedAllowedPrefixes
                    .Concat(new[]
                    {
                        "ClaudeHomeServer.Services.Turn",
                        "ClaudeHomeServer.Services.CodeGraph",
                    })
                    .ToArray(),
                new[]
                {
                    "ClaudeHomeServer.Protocol.StoredMessage",
                    "ClaudeHomeServer.Protocol.McpServerInfo",
                    "ClaudeHomeServer.Protocol.PromptSnapshotDraft",
                    "ClaudeHomeServer.Services.PersonaBindingsService",
                    "ClaudeHomeServer.Services.NotesKnowledgeService",
                    "ClaudeHomeServer.Services.NoteSemanticHit",
                    "ClaudeHomeServer.Services.PersonaManager",
                    "ClaudeHomeServer.Services.PersonaPromptBuilder",
                    "ClaudeHomeServer.Services.ProjectManager",
                    "ClaudeHomeServer.Services.SkillsService",
                    "ClaudeHomeServer.Services.UserStore",
                    "ClaudeHomeServer.Services.SkillInfo",
                    "ClaudeHomeServer.Services.ChatHistoryService",
                    "ClaudeHomeServer.Services.FeatureFlagService",
                    "ClaudeHomeServer.Services.PersonaMemoryService",
                    "ClaudeHomeServer.Services.PersonaMemoryHit",
                    "ClaudeHomeServer.Services.PersonaMemoryService+PersonaRecallResult",
                    "ClaudeHomeServer.Services.SpecialtySettingsStore",
                    "ClaudeHomeServer.Services.SpecialtySettingsStore+EffectivePromptSection",
                    "ClaudeHomeServer.Services.Dossiers.DossierRecallService",
                    "ClaudeHomeServer.Services.Dossiers.DossierRecallRequest",
                    "ClaudeHomeServer.Services.Llm.RecallItem",
                    "ClaudeHomeServer.Services.Llm.TurnRunPassport",
                    "ClaudeHomeServer.Services.Llm.Claude.SubagentRunPassport",
                }),
        },
        // Prompts — статические каталоги секций промпта (OmO/онбординг/голос/команды).
        // Точечные: ModelTier (PantheonTemplate), Llm.Claude.SubagentRunPassport
        // (SubagentPrompts формирует заголовок сабагента из паспорта).
        new object[]
        {
            new VerticalBoundary(
                "Prompts",
                "ClaudeHomeServer.Services.Prompts",
                SharedAllowedPrefixes
                    .Concat(new[] { "ClaudeHomeServer.Services.Prompts" })
                    .ToArray(),
                new[]
                {
                    "ClaudeHomeServer.Services.ModelTier",
                    "ClaudeHomeServer.Services.Llm.Claude.SubagentRunPassport",
                }),
        },
        // Execution — запуск процессов и песочница (LauncherFactory/SandboxManager/
        // IProcessLauncher/IPathMapper/ILauncherFactory). Точечный: UserStore
        // (LauncherFactory знает владельца процесса).
        new object[]
        {
            new VerticalBoundary(
                "Execution",
                "ClaudeHomeServer.Services.Execution",
                SharedAllowedPrefixes
                    .Concat(new[] { "ClaudeHomeServer.Services.Execution" })
                    .ToArray(),
                new[]
                {
                    "ClaudeHomeServer.Services.UserStore",
                }),
        },
        // Auth — узкая вертикаль авторизации (AdminByStoreRequirement +
        // AdminByStoreHandler, 1 файл). Зависимость только от UserStore.
        new object[]
        {
            new VerticalBoundary(
                "Auth",
                "ClaudeHomeServer.Services.Auth",
                SharedAllowedPrefixes
                    .Concat(new[] { "ClaudeHomeServer.Services.Auth" })
                    .ToArray(),
                new[]
                {
                    "ClaudeHomeServer.Services.UserStore",
                }),
        },
        // Backup — инфраструктура снимков data/. Точечный допуск к ProjectManager
        // (BackupService знает владельца каталога). По прецеденту Deploy→Backup:
        // внутри вертикали есть статика `Backup.InstanceLock.TryAcquireDeploy`,
        // которую внешние потребители (DeployHost) зовут через статику — но
        // внешние сервисы на наш Backup НЕ ссылаются, поэтому allow-list остаётся
        // узким.
        new object[]
        {
            new VerticalBoundary(
                "Backup",
                "ClaudeHomeServer.Services.Backup",
                SharedAllowedPrefixes
                    .Concat(new[] { "ClaudeHomeServer.Services.Backup" })
                    .ToArray(),
                new[]
                {
                    "ClaudeHomeServer.Services.ProjectManager",
                }),
        },
        // Desktop — ручной агент песочницы (ADR-008). Префикс-шов Hubs (DeviceHub),
        // точечные — Protocol (DesktopCall*/DeviceHello*/DesktopCancel/DesktopGo),
        // плюс root Services (JwtService/FeatureFlagService/PersonaManager/
        // ProjectManager/SessionManager/UserStore) для capability-токенов и каталога
        // чатов/устройств/сессий.
        new object[]
        {
            new VerticalBoundary(
                "Desktop",
                "ClaudeHomeServer.Services.Desktop",
                SharedAllowedPrefixes
                    .Concat(new[]
                    {
                        "ClaudeHomeServer.Services.Desktop",
                        "ClaudeHomeServer.Hubs",
                    })
                    .ToArray(),
                new[]
                {
                    "ClaudeHomeServer.Protocol.DesktopCallResult",
                    "ClaudeHomeServer.Protocol.DesktopCallCommand",
                    "ClaudeHomeServer.Protocol.DeviceHello",
                    "ClaudeHomeServer.Protocol.DeviceHelloAck",
                    "ClaudeHomeServer.Protocol.DesktopCancelCommand",
                    "ClaudeHomeServer.Protocol.DesktopGoCommand",
                    "ClaudeHomeServer.Services.JwtService",
                    "ClaudeHomeServer.Services.FeatureFlagService",
                    "ClaudeHomeServer.Services.PersonaManager",
                    "ClaudeHomeServer.Services.ProjectManager",
                    "ClaudeHomeServer.Services.SessionManager",
                    "ClaudeHomeServer.Services.UserStore",
                }),
        },
        // Diagnostics — файловый лог инстанса (FileLog, 1 файл). Полностью изолирован.
        new object[]
        {
            new VerticalBoundary(
                "Diagnostics",
                "ClaudeHomeServer.Services.Diagnostics",
                SharedAllowedPrefixes
                    .Concat(new[] { "ClaudeHomeServer.Services.Diagnostics" })
                    .ToArray(),
                Array.Empty<string>()),
        },
        // Modules — YARP-реверс-прокси для внешних модулей. Префикс-шов
        // Yarp.ReverseProxy.Configuration (third-party, по прецеденту AngleSharp
        // у Reader — открываем префиксом). Точечные: FeatureFlagService/JwtService
        // (ModuleGatewayMiddleware знает владельца), Services.Llm.LocalAction
        // (ModuleRegistry регистрирует LLM-действия модулей, тип едет в поле).
        new object[]
        {
            new VerticalBoundary(
                "Modules",
                "ClaudeHomeServer.Services.Modules",
                SharedAllowedPrefixes
                    .Concat(new[]
                    {
                        "ClaudeHomeServer.Services.Modules",
                        "Yarp.ReverseProxy.Configuration",
                    })
                    .ToArray(),
                new[]
                {
                    "ClaudeHomeServer.Services.FeatureFlagService",
                    "ClaudeHomeServer.Services.JwtService",
                    "ClaudeHomeServer.Services.Llm.LocalAction",
                }),
        },
        // Personas — черновик персоны по промпту (PersonaDraftService, 1 файл).
        // Полностью изолирован: Stateless-сервис по тексту промпта.
        new object[]
        {
            new VerticalBoundary(
                "Personas",
                "ClaudeHomeServer.Services.Personas",
                SharedAllowedPrefixes
                    .Concat(new[] { "ClaudeHomeServer.Services.Personas" })
                    .ToArray(),
                Array.Empty<string>()),
        },
        // TriggerSources — источники событий проактивности персон (timer/file/note/
        // git/task). Точечные root-сервисы: AppSettingsService/ProjectManager/
        // UserHomeResolver (AutomationRootResolver знает владельца), FileService
        // (GitCommitTriggerSource слушает файлы), PersonaManager (MentionTriggerSource),
        // NotesService (NoteTriggerSource), TaskManager (TaskStatusTriggerSource),
        // RuleRuntimeState (TriggerContext несёт состояние правила).
        new object[]
        {
            new VerticalBoundary(
                "TriggerSources",
                "ClaudeHomeServer.Services.TriggerSources",
                SharedAllowedPrefixes
                    .Concat(new[] { "ClaudeHomeServer.Services.TriggerSources" })
                    .ToArray(),
                new[]
                {
                    "ClaudeHomeServer.Services.AppSettingsService",
                    "ClaudeHomeServer.Services.ProjectManager",
                    "ClaudeHomeServer.Services.UserHomeResolver",
                    "ClaudeHomeServer.Services.FileService",
                    "ClaudeHomeServer.Services.PersonaManager",
                    "ClaudeHomeServer.Services.NotesService",
                    "ClaudeHomeServer.Services.TaskManager",
                    "ClaudeHomeServer.Services.RuleRuntimeState",
                }),
        },
    };

    [Theory]
    [MemberData(nameof(Boundaries))]
    public void Vertical_НеСсылаетсяНаДругиеВертикали(VerticalBoundary boundary)
    {
        // Сторож сканирует типы не из одной сборки (раньше typeof(VideoSubsystem).Assembly
        // возвращал единственную ClaudeHomeServer.dll, где жили все вертикали), а по всем
        // загруженным сборкам ClaudeHomeServer.* — сейчас это Core + Main, в будущем
        // добавляются отдельные сборки вынесенных вертикалей. Фильтр берёт:
        //  - `ClaudeHomeServer` — главная сборка (этап 0/1 не выносил вертикали, она
        //    ещё содержит все `Services.*`, и её имя НЕ имеет точки в имени сборки);
        //  - `ClaudeHomeServer.<X>` — Core и будущие вертикальные сборки.
        // Исключение `ClaudeHomeServer.Tests` гарантирует, что тестовая сборка с её
        // стабами не путается с продовыми типами. Статический конструктор форсирует
        // загрузку Main до этого момента (см. комментарий там).
        var assemblies = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a =>
            {
                var name = a.GetName().Name;
                return name is not null
                    && (name == "ClaudeHomeServer" || name.StartsWith("ClaudeHomeServer.", StringComparison.Ordinal))
                    && name != "ClaudeHomeServer.Tests"
                    && !name.StartsWith("ClaudeHomeServer.Tests.", StringComparison.Ordinal);
            })
            .ToList();

        var types = CollectTypesInNamespaceTree(assemblies, boundary.NamespaceRoot).ToList();

        // Защита от вакуумного прохода: если фильтр сборок или порядок загрузки сломается,
        // набор станет пустым и сторож пройдёт зелёным, ничего не проверив (доказано мутацией
        // в ревью 23d353d7: без `name == "ClaudeHomeServer"` — 17/17 зелёных при нуле типов).
        assemblies.Should().HaveCountGreaterThanOrEqualTo(2,
            "сторож должен видеть минимум ClaudeHomeServer и ClaudeHomeServer.Core");
        types.Should().NotBeEmpty(
            $"вертикаль {boundary.VerticalName} ({boundary.NamespaceRoot}) обязана иметь хотя бы " +
            "один тип — иначе она исчезла/переименована, а проверка границ ничего не проверяет");

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

    private static IEnumerable<Type> CollectTypesInNamespaceTree(IEnumerable<Assembly> assemblies, string namespaceRoot)
    {
        // Сам неймспейс + все вложенные поднеймспейсы, по всем переданным сборкам.
        foreach (var assembly in assemblies)
        {
            foreach (var type in assembly.GetTypes())
            {
                if (type.Namespace == namespaceRoot
                    || (type.Namespace?.StartsWith(namespaceRoot + ".", StringComparison.Ordinal) ?? false))
                {
                    yield return type;
                }
            }
        }
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