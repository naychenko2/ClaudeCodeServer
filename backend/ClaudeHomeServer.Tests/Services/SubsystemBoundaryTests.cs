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
/// Сторож читает: поля, параметры конструкторов, публичные свойства, сигнатуры
/// публичных методов И тела методов (IL-скан через <see cref="BoundaryIlScanner"/>).
/// Поэтому видны: статические вызовы, резолвы <c>sp.GetRequiredService&lt;T&gt;()</c>,
/// вызовы из async-state-машинок и замыканий. Обход вложенных типов обязателен:
/// без него 3 из 7 известных швов остаются невидимыми.
///
/// Что НЕ проверяется осознанно: интерфейсы и базовые классы (за пределами четырёх мест
/// ниже). Если потребуется — расширим в следующем шаге.
/// </summary>
public class SubsystemBoundaryTests
{
    // Форс-загрузка всех вертикальных сборок (Main плюс вынесенные .Reader/.Yandex/.Video):
    // сторож перебирает AppDomain.CurrentDomain.GetAssemblies(), и без явного
    // референса сборка-точка-точка ленивая — если ни один тест в этом testhost до
    // теста сторожа её не тронул, она не загружена, и сторож проходит вакуумно
    // по целым вертикалям. Раньше типы всех вертикалей жили в Main, и одного
    // typeof(VideoSubsystem).Assembly хватало на всё; теперь у каждой вертикали
    // своя сборка, и форс-референс нужен на каждую.
    static SubsystemBoundaryTests()
    {
        _ = typeof(ClaudeHomeServer.Services.Video.VideoSubsystem).Assembly;
        _ = typeof(ClaudeHomeServer.Services.Yandex.YandexSubsystem).Assembly;
        _ = typeof(ClaudeHomeServer.Services.Reader.ReaderService).Assembly;
        _ = typeof(ClaudeHomeServer.Services.CodeGraph.CodeGraphSubsystem).Assembly;
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
        // Сборка `ClaudeHomeServer.Core` — спинка целиком: к ней допускаются все
        // вертикали (SsrfGuard/JsonFileStore/PermissionModeGuard/TeamProtocolMarkers
        // и Composition/Http/Mcp-узлы). Контроль на уровне сборки, не namespace:
        // см. `CoreAssemblyName` ниже и `IsCoreAssembly`.
        // `ClaudeHomeServer.Protocol` — WS-контракт с фронтом. Объявлен спиной по
        // решению архитектора (задача `8beee75e`, ADR-014 §«Решение по Protocol»):
        // record-DTO дискриминируются по `type` в едином потоке `ServerMessage`,
        // разрезание по папкам не убирает ни одной рантайм-зависимости, а точечный
        // allow-list разрастается до ~70 записей на каждое новое WS-событие.
        // Честная цена: дублирующие сообщения остаются зоной ревью.
        "ClaudeHomeServer.Protocol",
    };

    /// <summary>Имя сборки «спинки» из общего кода: всё, что едет в
    /// `ClaudeHomeServer.Core.dll`, — инфраструктурные примитивы, а не сервисная логика.
    /// Допуск по сборке, а не по namespace: 4 типа (JsonFileStore/SsrfGuard/
    /// PermissionModeGuard/TeamProtocolMarkers) физически живут в `Core.dll`, но
    /// объявлены в namespace `ClaudeHomeServer.Services` (root) — namespace-фильтр
    /// их не видит, а assembly-фильтр видит. Не путать с проверкой
    /// <c>type.Namespace.StartsWith("ClaudeHomeServer.Core")</c> — namespace у этих
    /// типов `ClaudeHomeServer.Services`.</summary>
    private const string CoreAssemblyName = "ClaudeHomeServer.Core";

    /// <summary>Является ли тип из сборки-спинки Core (assembly-based backbone check).</summary>
    private static bool IsCoreAssembly(Type type)
    {
        var name = type.Assembly.GetName().Name;
        return name == CoreAssemblyName;
    }

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
        // возвращаемый из `SsrfGuard.*`) в полях async-state-машины. IL-скан видит
        // как сам класс (declaring-тип `call`-опкодов), так и nested enum в полях;
        // оба покрыты сборкой Core.dll (`IsCoreAssembly`), точная запись — для явности.
        // `AngleSharp.*` — third-party HTML-парсер (ReaderService парсит DOM).
        // `SmartReader.*` — third-party извлечение статьи (SmartReader.Reader/
        // SmartReader.Article в `ReaderService.ReadImageCoreAsync`/WalkToFinalResponseAsync,
        // статический вызов из тел async-методов — IL-скан ловит точно).
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
                        "SmartReader",
                    })
                    .ToArray(),
                new[]
                {
                    "ClaudeHomeServer.Services.SsrfGuard+AddressCheck",
                }),
        },
        // WebSearch — вертикаль веб-поиска (клиент Perplexity Sonar под MCP-сервером
        // websearch). Вертикаль без IAppSubsystem: регистрация — две строки в Program.cs
        // рядом с прочими клиентами внешних сервисов, отдельный .csproj по критерию
        // ADR-014 не оправдан (нужны Models и связка со Spend/Reader на стороне тулсета).
        // Зависимостей за пределами спинки нет вовсе: клиент берёт IHttpClientFactory,
        // свои опции и логгер. Чтение страниц лежит НЕ здесь — его делает вертикаль Reader
        // (переиспользование ридера вместе с его SsrfGuard и квотой), а склейку «поиск +
        // чтение + траты» держит тулсет в спине `Services.Mcp.Http`.
        new object[]
        {
            new VerticalBoundary(
                "WebSearch",
                "ClaudeHomeServer.Services.WebSearch",
                SharedAllowedPrefixes
                    .Concat(new[] { "ClaudeHomeServer.Services.WebSearch" })
                    .ToArray(),
                Array.Empty<string>()),
        },
        // Images — вертикаль генерации картинок. Допуск к корню Services точечный,
        // через AllowedExactNamespaces: `PersonaManager` (Services/ корень) — догоняющая
        // генерация аватара триггерится из карточки персоны; это сознательная зависимость
        // от «спинки» (доменная модель пользователей и персон). См. ctor
        // `ImageBackfillService.cs:34-37`.
        // Прочие соседи по корню (`FalImageService`, `ImageAssetHelper`) — отдельные
        // единицы из корня, но ImagesSubsystem ссылается на них через интерфейс
        // `IImageGenerator` (своя вертикаль) и через static-вызовы. Прежде рефлексия их
        // не видела; после волны 1 IL-скан видит — потому `ImageAssetHelper` и стоит
        // в допуске ниже, а не держится на слепоте сторожа.
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
                    // ImageAssetHelper (Services/ImageAssetHelper.cs) — IL-видимость
                    // (задача `8beee75e`): `ImageBackfillService.cs:269` зовёт
                    // `ImageAssetHelper.ExtFor(...)` (static-метод) из тела метода.
                    // Точечный допуск: «вертикаль → спинка» (root-инфраструктура).
                    "ClaudeHomeServer.Services.ImageAssetHelper",
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
        // плюс hosted-сервисы SessionManager/ProjectManager). После Этапа 3
        // (задача `4d044b22`) реакторы `GitAutoCommitService`/`CommitAttributionService`
        // вынесены в корень `Services.*` — это «реакция на ход/статус» (прецедент
        // `PersonaMemoryAutolearnService`), они ЗОВУТ `GitService`, а не принадлежат
        // Git. Поэтому `SessionManager`/`ProjectManager`/`ProjectFileSessionsIndex`/
        // `SessionChangedPaths` больше НЕ нужны Git-вертикали — допуски убраны.
        // Допуск к корню Services точечный:
        // 1) `UserStore` (Services/ корень) — `GitServerService:18` (Forgejo-клиент
        //    резолвит креденшалы пользователя).
        // 2) `ClaudeHomeServer.Services.Execution` — `ILauncherFactory`, через который
        //    GitService запускает процессы git (источник истины, как в задаче).
        // 3) `ClaudeHomeServer.Services.Llm` — `ICheapTextRunner` для генерации сообщения
        //    коммита и имени стэша в `GitAiService`. Это сознательная связь «вертикаль →
        //    спинка»: LLM-инфраструктура общего назначения (дешёвые one-shot ходы через
        //    локальную модель или haiku), используемая и другими разделами (теги заметок,
        //    сводки, память...). Вынос Llm в отдельный allow-list вместо расширения
        //    SharedAllowedPrefixes — чтобы не открывать любой подсистеме весь
        //    `ClaudeHomeServer.Services.Llm`.
        // 4) `ClaudeHomeServer.Protocol` — префикс СНЯТ (волна 3) как полностью
        //    избыточный: `GitTurnCommitMessage`/`GitStatusChangedMessage` теперь создаются
        //    в `Services.GitAutoCommitService` (root Services, не в Git-вертикали),
        //    а метод OnSessionMessageAsync с ServerMessage в сигнатуре — private,
        //    сторож читает только public-методы. Поэтому убираем префикс, а
        //    `AllowedExactNamespaces` для `ClaudeHomeServer.Protocol.*` оставляем
        //    пустым — это сознательный нулевой allow-list: если вертикаль получит
        //    поле/параметр типа из Protocol, сторож поймает это сразу.
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
                    })
                    .ToArray(),
                new[]
                {
                    "ClaudeHomeServer.Services.UserStore",
                    // Точечные зависимости из тел методов (IL-видимость, задача `8beee75e`):
                    // `GitService.cs:73` зовёт `FileService.SafeJoinPublic(...)` static-метод,
                    // `GitServerService.cs:213` зовёт `PersonaManager.Slugify(name)` — имя репозитория,
                    //    а не автор коммита (обоснование выправлено по факту, ревью 894e3ec9).
                    "ClaudeHomeServer.Services.FileService",
                    "ClaudeHomeServer.Services.PersonaManager",
                    // === Этап 3, задача `4d044b22` — реакторы `GitAutoCommitService`/
                    // `CommitAttributionService` переехали в корень `Services.*` (прецедент
                    // `PersonaMemoryAutolearnService`). `GitSubsystem.Register` всё ещё
                    // регистрирует их — это сознательная связь «вертикаль → root»,
                    // подсистема знает, что регистрирует, и без точечного допуска сторож
                    // ловит generic-аргументы `<GitAutoCommitService>`/`<CommitAttributionService>`.
                    // Префикс `Services` целиком не открываем.
                    "ClaudeHomeServer.Services.GitAutoCommitService",
                    "ClaudeHomeServer.Services.CommitAttributionService",
                }),
        },
        // CodeGraph — вертикаль графа зависимостей кода (узлы — типы, рёбра — Calls/Implements/References).
        // Пост-билд фаза `ConfigureApp` регистрирует языковые провайдеры (`.cs`/`.ts`/`.tsx`),
        // MCP-тулсет `CodeGraphToolset` живёт в `Services/Mcp/Http` и регистрируется в Program.cs.
        // Допуски к корню Services — точечные (см. ниже): `ExecutableResolver`. Прежний допуск
        // `ProjectManager` снят: CodeGraph получает проект через шов `IProjectRootLookup` из Core,
        // прямой зависимости от `Services.ProjectManager` в CodeGraph больше нет.
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
                    // ExecutableResolver (шаг 5) — корневой примитив поиска по PATH+PATHEXT.
                    // `TypeScriptGraphProvider.cs:154` зовёт `ResolveExecutable("node")`
                    // из тела метода. До выноса шов шёл на `LocalProcessRunner` —
                    // теперь на корневой `Services.ExecutableResolver`. По образцу
                    // `TranscriptRoots` (волна 4C): корневой спин не входит в
                    // `SharedAllowedPrefixes`, нужен точечный допуск.
                    "ClaudeHomeServer.Services.ExecutableResolver",
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
        // 3) `ClaudeHomeServer.Services.Git` — `GitService.RepoSnapshotAsync` в `DeployHost`
        //    (проба репозитория: HEAD + грязное дерево). Это СОЗНАТЕЛЬНАЯ связь
        //    «вертикаль → вертикаль». `IGitGuard` как общий шов для Deploy+Dossiers
        //    ОТКЛОНЁН (разведка Dossiers↔Git, 2026-09-07): Deploy нужен один метод,
        //    Dossiers — 7+ разных с 5 намерениями — единый контракт был бы вторым
        //    `LlmSessionContext`. Deploy остаётся на конкретном `GitService`, допуск
        //    в allow-list не снимается;
        //
        // === Шов `Deploy → Backup.InstanceLock.TryAcquireDeploy` (шаг 5, задача `57b5e9bc`).
        // `DeployHost.TryLockAgent` берёт мьютекс `Global\ccs-deploy` через статику
        // `Backup.InstanceLock.TryAcquireDeploy()`. Это инфраструктурный примитив
        // (мьютекс с трей-раннером), а не логика Backup. Префиксный допуск `Backup`
        // сужен до точечного `Backup.InstanceLock`: внутри Deploy-вертикали нет
        // других Backup-типов, расширять префикс незачем.
        // ВЫНОС НЕ СДЕЛАН, причины:
        //   * `InstanceLock` живёт в Main (ClaudeHomeServer.dll), Core не имеет
        //     ссылки на Main → обёртка в Core невозможна (направление `Main → Core`).
        //   * Если делать примитив `DeployAgentLock.TryAcquire()` в root Services,
        //     он тянет «одно и то же имя `Global\ccs-deploy`, один и тот же хелпер
        //     `TryAcquire(name)` с обработкой AbandonedMutexException/
        //     UnauthorizedAccessException» к трём разным классам — риск
        //     рассинхронизации имени и поведения.
        // Полумера (сужение префикса до точечного типа) лучше протащенной зависимости.
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
                    })
                    .ToArray(),
                new[]
                {
                    "ClaudeHomeServer.Services.SessionManager",
                    "ClaudeHomeServer.Services.NotificationService",
                    // Точечный допуск `Deploy → Backup.InstanceLock` (шаг 5):
                    // сузили прежний префикс `Backup`. Другого использования Backup
                    // внутри Deploy-вертикали нет (`grep -rn 'Backup\.' Deploy/`).
                    "ClaudeHomeServer.Services.Backup.InstanceLock",
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
        //    флаг `Icon.Glyph` в доменной модели проекта).
        //
        // === Шов к Backup.{BackupCore, BackupContext, BackupResult} (IL-видимость).
        // `ProjectIconMigration.cs:73` зовёт `BackupCore.Snapshot(BackupContext.FromConfiguration(config), log)`
        // и получает `BackupResult`. Попытка вынести примитив «снимок data перед
        // необратимой операцией» в Core блокируется направлением ссылок (`Main → Core`,
        // Core не видит Main/Backup), а обёртка в root Services нарушает root-сторож.
        // Допуск ОСТАВЛЕН с явной фиксацией причины — полумера лучше протащенной
        // зависимости (отчёт шага 5, задача `57b5e9bc`).
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
                    "ClaudeHomeServer.Services.Backup.BackupResult",
                    "ClaudeHomeServer.Services.Backup.BackupContext",
                    "ClaudeHomeServer.Services.Backup.BackupCore",
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
                    "ClaudeHomeServer.Services.Tasks.TaskManager",
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
        // «вертикали-нижнего-слоя» (Git/CodeGraph) и общий слой Dify-синка
        // (Services.Memory). Допуски к корню Services точечные:
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
        // 4) `ClaudeHomeServer.Services.Git` — большая часть уже за узким швом
        //    `IGitRefSnapshotStore` (волна 2 линии Dossiers↔Git, снапшот-реф ветки
        //    паспортов, владение константами `DossierBranch` — в Dossiers). Остаток —
        //    конкретный `GitService` для операций вне scope снапшот-рефа (`git show`
        //    и т.п. в DossierCaptureService/DossierRecallService). Общий `IGitGuard`
        //    на Deploy+Dossiers ОТКЛОНЁН (разведка 2026-09-07): 7+ разных методов с
        //    5 намерениями — грабмешок, не шов. Допуск остаётся точечным.
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
        //    сторож их сигнатуры не читает). `ServerMessage` ТУТ НЕ нужен: фигурирует
        //    в private-методе `DossierCaptureService.OnSessionMessageAsync`, но метод
        //    возвращает void, поэтому `ServerMessage` не материализуется в IL-операндах.
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
                    "ClaudeHomeServer.Services.Tasks.TaskManager",
                    "ClaudeHomeServer.Services.FileService",
                    "ClaudeHomeServer.Services.UserStore",
                    "ClaudeHomeServer.Services.FeatureFlagService",
                    "ClaudeHomeServer.Services.Knowledge.KnowledgeService",
                    // DossierStore — участник реконсайлера error-документов Dify
                    // (KnowledgeIndexReconciler, ADR-004 §4); public-метод ListTargets
                    // возвращает `IReadOnlyList<Knowledge.KnowledgeSyncTarget>` —
                    // рефлексия видит `KnowledgeSyncTarget` как возвращаемый тип
                    // и в generic-аргументе. Шов через IKnowledgeSyncParticipant
                    // (DossierStore имплементирует интерфейс) виден IL-скану как
                    // implementing-тип; явный allow-list не нужен, т.к. интерфейс
                    // живёт в namespace Knowledge (префикс вертикали Knowledge).
                    // Форвардер регистрации остаётся в Knowledge — кросс-клей.
                    "ClaudeHomeServer.Services.Knowledge.KnowledgeSyncTarget",
                    "ClaudeHomeServer.Protocol.StoredMessage",
                    // === Точечные допуски IL-видимости (задача `8beee75e`, волна 1).
                    // `DossierCaptureService` материализует `SessionSummaryService`
                    // (статический вызов `SessionSummaryService.BuildTranscript` из тела
                    // метода — по аналогии с `Memory → SessionSummaryService`, который
                    // уже был зафиксирован; теперь Dossiers — второй потребитель).
                    "ClaudeHomeServer.Services.SessionSummaryService",
                    // `DossierRecallService` материализует `SessionChangedPaths`
                    // (поле async-state-машины). Точечный допуск по образцу Git.
                    "ClaudeHomeServer.Services.SessionChangedPaths",
                    // `InstanceSecretsProvider` ссылается на реестр имён секретов
                    // `Services.InstanceSecretFiles.Names` (шаг 5). Примитив вынесен
                    // из `Backup.BackupPaths` в спину — по образцу `TranscriptRoots`.
                    // Допуск на `Backup.BackupPaths` снят (см. `p5-Dossiers`).
                    "ClaudeHomeServer.Services.InstanceSecretFiles",
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
                    "ClaudeHomeServer.Services.Memory.PersonaMemoryService",
                    "ClaudeHomeServer.Services.Memory.TeamMemoryService",
                    "ClaudeHomeServer.Services.Dossiers.DossierStore",
                    "ClaudeHomeServer.Services.Notes.NotesKnowledgeService",
                    "ClaudeHomeServer.Controllers.KnowledgeBaseSummary",
                    "ClaudeHomeServer.Controllers.KnowledgeBaseDetail",
                    "ClaudeHomeServer.Controllers.KnowledgeDocumentDto",
                    // FileMutationKind (Services/FileService.cs) — IL-видимость
                    // (задача `8beee75e`): `ProjectKnowledgeSyncService` материализует
                    // enum в поле async-state-машины. Точечный допуск по образцу Git.
                    "ClaudeHomeServer.Services.FileMutationKind",
                    // Telemetry (бывший префикс, заменён точечным допуском):
                    // `ProjectKnowledgeSyncService` логирует Dify-ошибки через
                    // ServerMetrics.RecordDifySyncError + DifyErrorCategorizer.
                    "ClaudeHomeServer.Telemetry.ServerMetrics",
                    "ClaudeHomeServer.Telemetry.DifyErrorCategorizer",
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
        //    вертикаль. Префикс `ClaudeHomeServer.Protocol` возвращён в
        //    `SharedAllowedPrefixes` (волна 1 IL-сторожа, 2026-09-06, задача `8beee75e`;
        //    решение зафиксировано в ADR-014 §«Решение по Protocol`), точечный допуск
        //    оставлен как документация явного шва для будущих ревью.
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
                    // SendOutcome nested-типы (задача `8beee75e`): `WatchdogAlarm`
                    // материализует `Completed`/`Queued`/`Running` в async-state-машине
                    // (поле `<DeliverAsync>d__N`). Раньше сторож видел только `SendOutcome`
                    // (базовый record), nested-варианты — нет, теперь видит.
                    // Шов уже зафиксирован через `+SendOutcome`, дописываем nested-типы.
                    "ClaudeHomeServer.Services.SessionMessagingService+SendOutcome+Completed",
                    "ClaudeHomeServer.Services.SessionMessagingService+SendOutcome+Queued",
                    "ClaudeHomeServer.Services.SessionMessagingService+SendOutcome+Running",
                }),
        },
        // Memory — долгая память персон и общая память команды проекта. Подсистема
        // регистрирует фасады `PersonaMemoryService`/`TeamMemoryService` + их
        // консолидацию/autolearn; в волне 4B шаг 1 эти шесть файлов переехали
        // из корня `Services/` в `Services/Memory/` — теперь они охвачены префиксом
        // `ClaudeHomeServer.Services.Memory` и точные имена в allow-list не нужны.
        // Общий слой ядра (MemoryWriteResolver/MemoryDify/MemoryConsolidationCore/
        // AutolearnGate/...) живёт в этом namespace уже давно.
        // Префиксы-швы (как у Dossiers/Spend):
        // 1) `ClaudeHomeServer.Services.Knowledge` — общий клиент Dify;
        //    `MemoryDify` держит `KnowledgeService` полем и материализует
        //    `DifyDocumentInfo` в async-state `DiffSyncAsync`.
        // 2) `ClaudeHomeServer.Services.Llm` — `ICheapTextRunner` в поле
        //    `MemoryWriteResolver` (консолидация LLM-merge и autolearn). Префикс-шов,
        //    как у `Git`/`Backgrounds`/`Deploy`/`Spend`/`Dossiers`.
        // Точечные допуски к корню Services — «вертикаль → спинка», аналогично
        // `Dossiers`/`Knowledge`/`Git`. После переезда фасадов в `Services.Memory`
        // эти связи видны IL-скану (вызовы из тел методов фасадов, которые теперь
        // внутри вертикали):
        // 3) `SessionManager` (PersonaMemoryService.cs:61, PersonaMemoryAutolearnService.cs:32,
        //    TeamMemoryAutolearnService.cs:40, TeamMemoryConsolidationService.cs:?;
        //    TeamMemoryService.cs:?) — нужен фасадам памяти для подписки на ходы
        //    (`OnSessionMessage`) и записи recall-результатов.
        // 4) `ProjectManager` (TeamMemoryAutolearnService.cs:40, TeamMemoryService.cs:?) —
        //    фасады team-памяти резолвят проект для recall и для авто-памяти.
        // 5) `UserStore` (PersonaMemoryService.cs:61) — общий учёт пользователей,
        //    нужен для проверки владельца персоны/проекта в lookup-методах фасадов.
        // 6) `PersonaManager` (PersonaMemoryService.cs:61, PersonaMemoryAutolearnService.cs:32,
        //    PersonaMemoryConsolidationService.cs:29) — lookup персоны для recall
        //    итераций и для авто-памяти по итогам хода. Тип живёт в корне `Services/`
        //    без IAppSubsystem (Persona в инвентаре — группа, а не подсистема), и
        //    формально это «вертикаль → root-синглтон», а не «вертикаль → вертикаль».
        //    Но исторически тип принадлежит группе Persona, поэтому фиксируем
        //    явно в отчёте шага 4B.1: `Memory` → `PersonaManager` — фактическая
        //    зависимость от «доменной модели персон», допустимая как вертикаль →
        //    спинка, по аналогии с тем, как `Images` зависит от того же типа
        //    (см. Boundaries.Images ниже).
        // 7) `PersonaMemoryScorer`/`TeamMemoryScorer` — статические вызовы из тел
        //    методов `BuildEvictIds` (PersonaMemoryConsolidationService, TeamMemory
        //    ConsolidationService). Оба типа живут в namespace `Services.Memory` —
        //    собственный префикс вертикали покрывает их, отдельная запись не нужна.
        // 8) `SessionSummaryService` (Services/ корень) — статические вызовы
        //    `SessionSummaryService.BuildTranscript(...)` из тел методов
        //    `PersonaMemoryAutolearnService.cs` и `TeamMemoryAutolearnService.cs`.
        //    IL-скан видит declaring-тип; допуск явный в allow-list (тот же шов
        //    «вертикаль → спинка», что и прочие точечные допуски ниже).
        // Точечный допуск к `ClaudeHomeServer.Protocol`:
        // 8) `StoredMessage` — `AutolearnGate.CheckContent`/`LastTurnLength` принимают
        //    `IReadOnlyList<StoredMessage>` (видна в сигнатуре public-метода). Префикс
        //    `ClaudeHomeServer.Protocol` снят (волна 3), чтобы сторож ловил новые
        //    зависимости от любых из ~105 публичных типов протокола.
        // ⚠ Два форвардера `IKnowledgeSyncParticipant → {PersonaMemoryService,
        // TeamMemoryService}` остаются в Program.cs (кросс-вертикальный клей реконсайлера
        // error-документов Dify) и потому НЕ входят в allow-list Memory.
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
                    // Вертикаль → спинка (см. пункты 3-6 комментария выше).
                    "ClaudeHomeServer.Services.SessionManager",
                    "ClaudeHomeServer.Services.ProjectManager",
                    "ClaudeHomeServer.Services.UserStore",
                    "ClaudeHomeServer.Services.PersonaManager",
                    "ClaudeHomeServer.Services.ProjectEventLogService",
                    "ClaudeHomeServer.Services.Notes.NotesService",
                    "ClaudeHomeServer.Services.Dossiers.DossierRecallService",
                    "ClaudeHomeServer.Services.Dossiers.DossierRecallRequest",
                    "ClaudeHomeServer.Services.Dossiers.DossierRecallResult",
                    // AutolearnGate.CheckContent / LastTurnLength — public-метод
                    // с параметром IReadOnlyList<StoredMessage>.
                    "ClaudeHomeServer.Protocol.StoredMessage",
                    // Шов Memory → Services.SessionSummaryService (см. пункт 8
                    // комментария выше). Статический вызов из тел методов
                    // PersonaMemoryAutolearnService / TeamMemoryAutolearnService;
                    // IL-скан видит declaring-тип.
                    "ClaudeHomeServer.Services.SessionSummaryService",
                    // Telemetry (бывший префикс, заменён точечным допуском):
                    // `MemoryDify` логирует Dify-ошибки через
                    // ServerMetrics.RecordDifySyncError + DifyErrorCategorizer.
                    "ClaudeHomeServer.Telemetry.ServerMetrics",
                    "ClaudeHomeServer.Telemetry.DifyErrorCategorizer",
                }),
        },
        // === Шаг 2б плана выноса штаба (этап 4): интерфейс-шов `ITeamNotifier`
        // (Services/Team/) — единственный тип вертикали. Реализация пока внутри
        // SessionManager обёрткой над прежними приватными методами; переезд тела (2г)
        // добавит реализацию в новый класс и расширит allow-list по факту. Сейчас
        // внешних зависимостей нет: сигнатуры только из примитивов (`string`, `bool`,
        // `Task`). Запись заведена, чтобы сторож границ видел вертикаль штаба — иначе
        // namespace Services.Team остаётся в `verticalOnlyNamespaces` Coverage-теста,
        // который маскирует удаление строки из `Boundaries` (см. комментарий там).
        new object[]
        {
            new VerticalBoundary(
                "Team",
                "ClaudeHomeServer.Services.Team",
                SharedAllowedPrefixes
                    .Concat(new[]
                    {
                        "ClaudeHomeServer.Services.Team",
                        // Префиксы-швы по факту ссылок спутников (шаг 2г-1 этапа 4):
                        // - Llm: ICheapTextRunner/LlmTimeoutException (TeamPlanningService);
                        // - Tasks: TaskManager (TeamWaveService);
                        // - Prompts: TeamImplementPrompts (TeamWaveService);
                        // - Hubs: SessionHub (IHubContext<SessionHub> в TeamWaveService);
                        // - Protocol: TeamWavePulseMessage (async-state-машина SendWavePulsesAsync).
                        "ClaudeHomeServer.Services.Llm",
                        "ClaudeHomeServer.Services.Tasks",
                        "ClaudeHomeServer.Services.Prompts",
                        "ClaudeHomeServer.Hubs",
                        "ClaudeHomeServer.Protocol",
                        // Префикс-шов по факту ссылок спутников (волна Ж):
                        // - Turn: TurnCompleted (подписчик шины turn/completed в TeamTurnCompletionService).
                        "ClaudeHomeServer.Services.Turn",
                    })
                    .ToArray(),
                new[]
                {
                    // Точечные допуски к корню `Services.*` — «вертикаль → спинка» (по образцу Skills):
                    // PersonaManager/ProjectManager/TaskExecutionService/NotificationService —
                    // параметры конструкторов TeamWaveService; SessionManager — там же,
                    // для получения session id и публикации событий штаба; nested ReportUpResult
                    // (enum, объявленный внутри SessionManager) — сигнал пробуждения штаба,
                    // возвращается из SessionManager.ReportUpAsync, который TeamTurnCompletionService
                    // зовёт при пробуждении через ReportBlockerAsync.
                    "ClaudeHomeServer.Services.PersonaManager",
                    "ClaudeHomeServer.Services.ProjectManager",
                    "ClaudeHomeServer.Services.TaskExecutionService",
                    "ClaudeHomeServer.Services.NotificationService",
                    "ClaudeHomeServer.Services.SessionManager",
                    "ClaudeHomeServer.Services.SessionManager+ReportUpResult",
                    // === IL-видимость (задача `8beee75e`, волна 1).
                    // `TeamPlanFileRenderer.cs:112` зовёт `FileService.SafeJoin(...)`
                    // static-метод из тела метода — точечный допуск по образцу
                    // `Tasks → FileService` (вертикаль → спинка).
                    "ClaudeHomeServer.Services.FileService",
                    // `TeamPlanningService.cs:146` материализует `SpecialtyCatalog`
                    // (статический класс из корня) в async-state. По прецеденту Llm
                    // (тоже `SpecialtyCatalog`) — фиксируем шов явно, иначе IL-скан
                    // поймает как нарушение.
                    "ClaudeHomeServer.Services.SpecialtyCatalog",
                    // `TeamEnableService.cs:69`/`TeamStateService.cs:196` зовут
                    // `SessionModeConflictException` static-метод из тел методов
                    // (permission-гард, шаг 2г-3 этапа 4). Точечный допуск.
                    "ClaudeHomeServer.Services.SessionModeConflictException",
                    // ⚠ ИНВЕРСИЯ СЛОЁВ `Team → Controllers` через extension-метод
                    // `TaskHubExtensions` (TeamWaveService.cs:369,647,163 —
                    // `DropSubtaskAsync`, `LaunchReissueAsync`, `StartWaveCoreAsync`).
                    // Брат-двойник `Tasks → TaskHubExtensions` (уже в Tasks allow-list
                    // с пометкой «⚠ ИНВЕРСИЯ СЛОЁВ, разбор — этап 4»).
                    "ClaudeHomeServer.Controllers.TaskHubExtensions",
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
        //
        // Волна 4B, шаг 2 — в `Services.Llm` переехали `SpecialtySettingsStore` +
        // `SpecialtySettingsLayer`, `UsageService`, `ClaudeSubscriptionPool` +
        // `SubscriptionActivityTracker` + `SubscriptionWindowMismatchGuard` +
        // `SubscriptionUsageWarmupService` + `SubscriptionOAuthUsageService`,
        // `WorkflowAgentParser` + `WorkflowWatcher` + `WorkflowMetaResolver`.
        // Удалены из `AllowedExactNamespaces` как осиротевшие (теперь ссылки
        // внутренние: `ClaudeHomeServer.Services.Llm.*`): `ModelRoutePreset` и
        // `PresetScope` переехали в `SpecialtySettingsStore.cs:78,88`.
        // `WorkflowWatcher` остался как `Services.Llm.WorkflowWatcher` — поле
        // async-state-машины `ClaudeSession+<...>d__N`, материал-аргумент
        // публичного метода `ClaudeSession.OnSubagentStreamAsync`.
        //
        // До переезда SpecialtySettingsStore в `Services.Llm` статические
        // вызовы `SpecialtyCatalog.*` и `SpecialtyPromptPresets.*` шли из тел
        // методов корневого `Services/SpecialtySettingsStore.cs` — рефлексия
        // их не видела, и эти типы в allow-list Llm НЕ нужны были (как
        // сейчас не нужны `Deploy → Services.Backup.InstanceLock.*`). После
        // переезда `SpecialtySettingsStore` остался ЕДИНСТВЕННЫМ потребителем
        // обоих типов внутри `Services.Llm`, и без явного объявления шов
        // «теряется» при расширении сторожа до IL — будет выглядеть как
        // нарушение, а не как ожидаемая зависимость от корневого каталога.
        // Поэтому `SpecialtyCatalog` и `SpecialtyPromptPresets` (статические
        // классы из `ClaudeHomeServer.Services`) видны IL-скану (declaring-тип
        // `call`-опкодов из `SpecialtySettingsStore.cs`), поэтому добавлены в
        // `AllowedExactNamespaces`. Аналогично `Memory → SessionSummaryService`
        // и `Execution → TranscriptRoots`: шов «вертикаль → корневая спинка».
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
                    // Волна 4B, шаг 2 — WorkflowAgentParser/Watcher материализуют
                    // типы протокола в полях public-методов и в generic-аргументах
                    // лямбд (<>c__DisplayClass).
                    "ClaudeHomeServer.Protocol.WorkflowAgentDto",
                    "ClaudeHomeServer.Protocol.WorkflowAgentBlockDto",
                    "ClaudeHomeServer.Protocol.WorkflowToolDto",
                    // Root Services — точные (общая инфраструктура: реестры, сторы,
                    // рантайм-состояния, доменные модели). Префикс `ClaudeHomeServer.Services`
                    // для них НЕ открываем (default-deny), только FullName.
                    "ClaudeHomeServer.Services.ModelTier",
                    "ClaudeHomeServer.Services.ModelTier[]",
                    "ClaudeHomeServer.Services.Skills.SkillInfo",
                    "ClaudeHomeServer.Services.SystemPromptPart",
                    "ClaudeHomeServer.Services.AppSettingsService",
                    "ClaudeHomeServer.Services.ChatHistoryService",
                    "ClaudeHomeServer.Services.FileService",
                    "ClaudeHomeServer.Services.SessionSummaryService",
                    "ClaudeHomeServer.Services.Notes.NotesService",
                    "ClaudeHomeServer.Services.ProjectManager",
                    "ClaudeHomeServer.Services.SessionManager",
                    "ClaudeHomeServer.Services.Skills.SkillsService",
                    "ClaudeHomeServer.Services.UserStore",
                    "ClaudeHomeServer.Services.ModelCatalogService",
                    "ClaudeHomeServer.Services.ModelCatalogService+ModelInfo",
                    // Волна 4B, шаг 2 — переехавшие типы тянут точечные зависимости
                    // от корня Services:
                    // 1) `SpecialtyTemplate` (Services/SpecialtyCatalog.cs:12) — каталожный
                    //    record прав специальности; `SpecialtySettingsStore.EffectiveTemplate`
                    //    материализует его в return-типе (public-метод виден рефлексии).
                    //    SpecialtyCatalog целиком в Llm НЕ переезжает — нужен только этот
                    //    record (см. вердикт архитектора по вопросу 4: SpecialtyCatalog
                    //    остаётся в корне, в Llm едет только слой настроек).
                    // 2) `SpecialtyPromptPresets+SectionMeta` — generic-аргумент
                    //    `Dictionary<string, SectionMeta>` в поле async-state-машины
                    //    `SpecialtySettingsStore.EffectivePromptSectionStates`.
                    // 3) `NotificationService` (Services/NotificationService.cs) —
                    //    `SubscriptionAlertNotifier` шлёт алерт админам через общий
                    //    нотификатор (тот же шов «вертикаль → спинка», что у
                    //    `Knowledge`/`Deploy`).
                    "ClaudeHomeServer.Services.SpecialtyTemplate",
                    "ClaudeHomeServer.Services.SpecialtyPromptPresets+SectionMeta",
                    "ClaudeHomeServer.Services.SpecialtyCatalog+Entry",
                    // Шов на статические классы каталога (см. комментарий выше).
                    // `SpecialtySettingsStore.cs` зовёт их из тел методов; IL-скан
                    // видит declaring-типы. Шов зафиксирован явно.
                    "ClaudeHomeServer.Services.SpecialtyCatalog",
                    "ClaudeHomeServer.Services.SpecialtyPromptPresets",
                    "ClaudeHomeServer.Services.NotificationService",
                    // Knowledge — WorkspaceKnowledgeStore (CLI-сессия и фабрика
                    // адаптеров материализуют тип в async-state). Префикс не открываем:
                    // Knowledge — отдельная вертикаль, точечный допуск ровно на нужный тип.
                    "ClaudeHomeServer.Services.Knowledge.WorkspaceKnowledgeStore",
                    // Spend — ISpendCollector пишут все четыре ход-раннера (cloud-cheap,
                    // Ollama/LlamaServer и OneShot-Claude). Префикс не открываем.
                    "ClaudeHomeServer.Services.Spend.ISpendCollector",
                    // Telemetry (бывший префикс, заменён точечным допуском):
                    // `ClaudeSession` зовёт `TurnTelemetry.StartTurnSpan`/`RecordTurnResult`
                    // и прочие методы из тел async-методов.
                    "ClaudeHomeServer.Telemetry.TurnTelemetry",
                    // PromptSnapshotStore (Services/PromptSnapshotStore.cs) — ClaudeSession
                    // зовёт `PromptSnapshotStore.NewPublicId()` (static) из тела
                    // `RunTurnAsync` (ClaudeSession.cs:3912); IL-скан видит тип.
                    "ClaudeHomeServer.Services.PromptSnapshotStore",
                    // TranscriptRoots — реестр корней транскриптов (волна 4C, шаг 4).
                    // Вынесен из WorkflowAgentParser в спину `Services.TranscriptRoots`,
                    // чтобы устранить цикл Llm ⇄ Execution. Llm держит шов
                    // `WorkflowWatcher`/`SubagentStreamWatcher`/`TranscriptProbe`/`ClaudeSession`
                    // через вызовы `TranscriptRoots.IsPathAllowed/DefaultRoot/ProfilesRoot/
                    // AllowedRoots` (поля и параметры public-методов — рефлексия видит).
                    "ClaudeHomeServer.Services.TranscriptRoots",
                    // === Циклы, объявленные по IL-видимости (задача `8beee75e`, волна 1).
                    // Раньше сторож не видел статические вызовы из тел методов, и эти
                    // циклы существовали «вслепую». После включения IL-скана они пойманы
                    // и объявлены явно с TODO на шов.
                    //
                    // `Llm ⇄ Prompts`: ClaudeSession ссылается на статические
                    // классы `CoordinatorWriteGuard` (DecidePermissionAsync),
                    // `ChatContextPrompts`/`VoicePrompts` (RunTurnAsync). Все три —
                    // статические вызовы из тел методов, теперь видимые. Префикс-шов
                    // через `Services.Prompts` НЕ открываем: Prompts — каталожная
                    // вертикаль, узкие зависимости объявляем точечно.
                    // (TODO на шов: завести интерфейс `IPromptGuard`/`IPromptCatalog`
                    // в `Services.Llm` и проксировать stat-вызовы, тогда Prompts-типы
                    // уйдут из allow-list Llm и цикл Llm ⇄ Prompts исчезнет.)
                    "ClaudeHomeServer.Services.Prompts.CoordinatorWriteGuard",
                    "ClaudeHomeServer.Services.Prompts.ChatContextPrompts",
                    "ClaudeHomeServer.Services.Prompts.VoicePrompts",
                    // `Llm ⇄ Team`: ClaudeSession.HandleControlRequestAsync ссылается
                    // на `TeamImplementPrompts.MaxInterviewRounds`/`InterviewRoundsExhausted`
                    // через static-вызов (видимо IL-сканером в `<HandleControlRequestAsync>d__144`).
                    // Шов уже частично объявлен у Turn (TeamImplementPrompts/TeamMechanicsPromptCatalog),
                    // здесь — точный для Llm.
                    // (TODO на шов: вынести константы `MaxInterviewRounds`/`InterviewRoundsExhausted`
                    // в общий `Services.Composition`-тип (или `IClaudeHomeConstants`) и убрать
                    // зависимость Llm → Team; тогда цикл Llm ⇄ Team разрезается.)
                    "ClaudeHomeServer.Services.Team.TeamImplementPrompts",
                    // `Git ⇄ Llm`: ClaudeSession.cs:2457 зовёт
                    // `AttachmentsGitExclude.Ensure(_rootPath)` (static) из тела
                    // `RunTurnAsync`; IL-скан видит declaring-тип. Обратная сторона
                    // (`Git → Llm` через `ICheapTextRunner` в `GitAiService`) уже
                    // объявлена префиксом `ClaudeHomeServer.Services.Llm` у Git.
                    // Цикл наполовину разрезан примитивом `AttachmentsGitExclude`
                    // (задача `4d044b22`): `EnsureAttachmentsExcluded` вынесен
                    // из `GitService` в `Services.AttachmentsGitExclude`, и Llm
                    // больше не зависит от `Services.Git`. Префикс `Services.Git`
                    // у Llm снят — `Services.Git` в целом не нужен. Обратное
                    // ребро `Git → Llm` остаётся как сознательная зависимость
                    // (префикс-шов `Services.Llm` у Git).
                    "ClaudeHomeServer.Services.AttachmentsGitExclude",
                }),
        },
        // Docs — индекс документации (ADR) + ИИ-помощь по документам (волна 4A, шаг 2).
        // Внешние зависимости:
        // 1) Префикс-шов `Services.Llm` — `ICheapTextRunner` для ИИ-помощи (summary/extract/tags/
        //    enhance) и `LocalActionCatalog.DocFormat/Summary/Extract/Tags`. Тот же шов,
        //    что у `Backgrounds`/`Git`/`Deploy`/`Changelog`.
        // 2) Точечный допуск к корню Services — `FileService`: DocsIndexService читает каталог
        //    `docs/` и кеширует индекс, DocumentAiService читает файлы по хосту через FilesController.
        new object[]
        {
            new VerticalBoundary(
                "Docs",
                "ClaudeHomeServer.Services.Docs",
                SharedAllowedPrefixes
                    .Concat(new[]
                    {
                        "ClaudeHomeServer.Services.Docs",
                        "ClaudeHomeServer.Services.Llm",
                    })
                    .ToArray(),
                new[]
                {
                    "ClaudeHomeServer.Services.FileService",
                }),
        },
        // Turn — шина событий хода + контрибьюторы секций системного промпта.
        // Все внешние допуски точечные (префиксов-швов нет): CodeGraphPromptProvider
        // (единственный тип, который читает CodeGraphContributor — открывать всю
        // вертикаль CodeGraph ради него нельзя), root Services, Dossiers
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
                    })
                    .ToArray(),
                new[]
                {
                    "ClaudeHomeServer.Protocol.StoredMessage",
                    "ClaudeHomeServer.Protocol.McpServerInfo",
                    "ClaudeHomeServer.Protocol.PromptSnapshotDraft",
                    "ClaudeHomeServer.Services.CodeGraph.CodeGraphPromptProvider",
                    "ClaudeHomeServer.Services.PersonaBindingsService",
                    "ClaudeHomeServer.Services.Notes.NotesKnowledgeService",
                    "ClaudeHomeServer.Services.Notes.NoteSemanticHit",
                    "ClaudeHomeServer.Services.PersonaManager",
                    "ClaudeHomeServer.Services.PersonaPromptBuilder",
                    "ClaudeHomeServer.Services.ProjectManager",
                    "ClaudeHomeServer.Services.Skills.SkillsService",
                    "ClaudeHomeServer.Services.UserStore",
                    "ClaudeHomeServer.Services.Skills.SkillInfo",
                    "ClaudeHomeServer.Services.ChatHistoryService",
                    "ClaudeHomeServer.Services.FeatureFlagService",
                    "ClaudeHomeServer.Services.Memory.PersonaMemoryService",
                    "ClaudeHomeServer.Services.Memory.PersonaMemoryHit",
                    "ClaudeHomeServer.Services.Memory.PersonaMemoryService+PersonaRecallResult",
                    // PromptSectionsContributor ссылается на Llm.SpecialtySettingsStore
                    // и его nested EffectivePromptSection (поле async-state-машины). В
                    // корне Services этого типа больше нет — SpecialtySettingsStore
                    // переехал в Llm (волна 4B, шаг 2), и Turn держит ссылку на новый
                    // адрес (см. `ClaudeHomeServer.Services.Llm.SpecialtySettingsStore`
                    // в allow-list Llm).
                    "ClaudeHomeServer.Services.Llm.SpecialtySettingsStore",
                    "ClaudeHomeServer.Services.Llm.SpecialtySettingsStore+EffectivePromptSection",
                    "ClaudeHomeServer.Services.Dossiers.DossierRecallService",
                    "ClaudeHomeServer.Services.Dossiers.DossierRecallRequest",
                    "ClaudeHomeServer.Services.Llm.RecallItem",
                    "ClaudeHomeServer.Services.Llm.TurnRunPassport",
                    "ClaudeHomeServer.Services.Llm.Claude.SubagentRunPassport",
                    // Этап 4, шаг 2г-2 — переезд промптов штаба: ClaudeSession
                    // (DecidePermissionAsync) ссылается на `TeamImplementPrompts.MaxInterviewRounds`
                    // и `InterviewRoundsExhausted` для гейта AskUserQuestion. До переезда
                    // шёл через префикс `Services.Prompts` в SharedAllowedPrefixes, но
                    // после переезда тип в `Services.Team` — точный допуск.
                    "ClaudeHomeServer.Services.Team.TeamImplementPrompts",
                    // Этап 4, шаг 2г-2 — PersonaLayerContributor (в Turn-boundary) вызывает
                    // TeamMechanicsPromptCatalog.BuildPromptBlock через return-тип; до переезда
                    // шло через префикс `Services.Prompts` (SharedAllowedPrefixes), теперь —
                    // точный допуск.
                    "ClaudeHomeServer.Services.Team.TeamMechanicsPromptCatalog",
                    // === IL-видимость (задача `8beee75e`, волна 1).
                    // `Turn → Knowledge`: NotesRecallContributor (`<BuildAsync>d__13`)
                    // и PersonaRecallContributor (`<BuildAsync>d__19`) материализуют
                    // `KnowledgeService` в async-state. НЕ цикл (Knowledge на Turn
                    // не смотрит), но новое межвертикальное ребро — фиксируем.
                    "ClaudeHomeServer.Services.Knowledge.KnowledgeService",
                    // `PersonaLayerContributor` ссылается на `OnboardingPrompts`
                    // (статический каталог в `Services.Prompts`). Префикс Prompts
                    // НЕ открываем: точечный допуск ровно на нужный тип.
                    "ClaudeHomeServer.Services.Prompts.OnboardingPrompts",
                    // `PersonaRecallContributor` материализует `SessionChangedPaths`
                    // в async-state `<LastTurnChangedFilesAsync>d__21.MoveNext`.
                    "ClaudeHomeServer.Services.SessionChangedPaths",
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
                    // `OmcPersonaRouting.cs:114` зовёт `PersonaConsultantToolset`
                    // static-метод из тела метода (IL-видимость, задача `8beee75e`).
                    // Точечный допуск по образцу `Llm → SpecialtyCatalog`/`SpecialtyPromptPresets`.
                    "ClaudeHomeServer.Services.PersonaConsultantToolset",
                }),
        },
        // Execution — запуск процессов и песочница (LauncherFactory/SandboxManager/
        // IProcessLauncher/IPathMapper/ILauncherFactory). Точечный: UserStore
        // (LauncherFactory знает владельца процесса).
        //
        // Волна 4C, шаг 4 — цикл `Llm ⇄ Execution` разрезан:
        //   * `ClaudeCliLocator` (поиск исполняемого файла claude CLI) уехал
        //     в `Services.Execution` — задача слоя Execution, не Llm;
        //   * реестр корней транскриптов уехал в спину `Services.TranscriptRoots`
        //     (три источника из разных слоёв: константа `~/.claude/projects`,
        //     `LlmProviderRegistry.ProfilesDir` через Program.cs,
        //     `DockerProcessRunner.EnsureProfile` через `TranscriptRoots.AddAllowedRoot`).
        // Префикс `Services.Llm` больше не открываем — прямых рёбер нет.
        // Точечный допуск к `TranscriptRoots`: статический вызов
        // `TranscriptRoots.AddAllowedRoot(...)` в теле `DockerProcessRunner.EnsureProfile`.
        // IL-скан видит declaring-тип; допуск явный (зависимость от «спинки»
        // рядом с `SafeJoin`/`SsrfGuard`).
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
                    // Шов Execution → TranscriptRoots: статический вызов из тела,
                    // IL-скан видит declaring-тип.
                    "ClaudeHomeServer.Services.TranscriptRoots",
                    // Шов Execution → ExecutableResolver (шаг 5): `LocalProcessRunner.BuildStartInfo`
                    // зовёт `ExecutableResolver.ResolveExecutable(spec.FileName)` из тела метода;
                    // сам `LocalProcessRunner.ResolveExecutable` теперь — тонкая обёртка
                    // над корневым примитивом. Допуск по образцу TranscriptRoots выше.
                    "ClaudeHomeServer.Services.ExecutableResolver",
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
                    // Шов Backup → InstanceSecretFiles (шаг 5): `BackupPaths.SecretFileNames`
                    // — тонкий алиас на `InstanceSecretFiles.Names`. IL-скан видит
                    // declaring-тип как ссылку на спинку. Допуск по образцу
                    // Execution → TranscriptRoots / ExecutableResolver.
                    "ClaudeHomeServer.Services.InstanceSecretFiles",
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
        // Yarp.ReverseProxy целиком (third-party, по прецеденту AngleSharp у Reader —
        // открываем префиксом; ModuleProxyConfigProvider ссылается на Configuration/
        // Forwarder/Transforms, ModuleGatewayMiddleware — на Forwarder). Точечные:
        // FeatureFlagService/JwtService (ModuleGatewayMiddleware знает владельца),
        // Services.Llm.LocalAction (ModuleRegistry регистрирует LLM-действия
        // модулей, тип едет в поле).
        new object[]
        {
            new VerticalBoundary(
                "Modules",
                "ClaudeHomeServer.Services.Modules",
                SharedAllowedPrefixes
                    .Concat(new[]
                    {
                        "ClaudeHomeServer.Services.Modules",
                        "Yarp.ReverseProxy",
                    })
                    .ToArray(),
                new[]
                {
                    "ClaudeHomeServer.Services.FeatureFlagService",
                    "ClaudeHomeServer.Services.JwtService",
                    "ClaudeHomeServer.Services.Llm.LocalAction",
                    // === IL-видимость (задача `8beee75e`, волна 1).
                    // `ModuleGatewayMiddleware.cs:84` (`<Invoke>d__2`) резолвит
                    // `UserStore` через `sp.GetRequiredService<UserStore>()` —
                    // generic-аргумент виден IL-сканом как `Modules → UserStore`.
                    // Точечный допуск по образцу `Llm → SessionSummaryService`.
                    "ClaudeHomeServer.Services.UserStore",
                    // `ModuleRegistry` (`<>c__DisplayClass7_0`) материализует
                    // `ModelTier` в generic-аргументе. Точечный допуск.
                    "ClaudeHomeServer.Services.ModelTier",
                    // `ModuleRegistry` ссылается на `LocalActionCatalog` и
                    // `CheapProfile` статические классы из `Services.Llm` —
                    // аналогично `Llm.LocalAction` (уже в списке), но шире.
                    // Префикс `Services.Llm` НЕ открываем: точечные допуски.
                    "ClaudeHomeServer.Services.Llm.LocalActionCatalog",
                    "ClaudeHomeServer.Services.Llm.CheapProfile",
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
                    "ClaudeHomeServer.Services.Notes.NotesService",
                    "ClaudeHomeServer.Services.Tasks.TaskManager",
                    "ClaudeHomeServer.Services.RuleRuntimeState",
                    // `MentionTriggerSource.cs:18` ссылается на `GroupChatRouter`
                    // (IL-видимость, задача `8beee75e`). Точечный допуск.
                    "ClaudeHomeServer.Services.GroupChatRouter",
                }),
        },
        // ProjectServices — вертикаль раздела «Сервисы проекта» (волна 4A). Допуски:
        // 1) Префикс-шов `ClaudeHomeServer.Execution` — `IProcessLauncher`/`ILauncherFactory`/
        //    `SandboxManager` для запуска процессов дев-серверов/терминалов (тот же шов,
        //    что у `Git`/`Deploy`/`Backgrounds`/`Spend`/`Dossiers`).
        // 2) Префикс-шов `ClaudeHomeServer.Hubs` — `IHubContext<SessionHub>` для рассылки
        //    вывода и статусов дев-серверов подписчикам группы (DevServerService:121).
        // 3) Префикс-шов `Yarp.ReverseProxy` — `ExternalPreviewProxy` ссылается на
        //    `Yarp.ReverseProxy.Forwarder.HttpTransformer` (статический вызов из тела
        //    метода, IL-скан ловит).
        // 4) Допуски к корню Services точечные:
        //    - `ProjectManager` — общая инфраструктура (`DevServerService`, `ExternalPreviewRouter`).
        //    - `JwtService` — формирование токена внешней ссылки (`ExternalPreviewRouter:38`).
        //    - `OutputRingBuffer` — общий примитив реплея вывода, общий с Terminal (шапка
        //      `OutputRingBuffer.cs:5-10` явно фиксирует общее использование).
        //    Префикс на корень Services не открываем (default-deny).
        new object[]
        {
            new VerticalBoundary(
                "ProjectServices",
                "ClaudeHomeServer.Services.ProjectServices",
                SharedAllowedPrefixes
                    .Concat(new[]
                    {
                        "ClaudeHomeServer.Services.ProjectServices",
                        "ClaudeHomeServer.Services.Execution",
                        "ClaudeHomeServer.Hubs",
                        "Yarp.ReverseProxy",
                    })
                    .ToArray(),
                new[]
                {
                    "ClaudeHomeServer.Services.ProjectManager",
                    "ClaudeHomeServer.Services.JwtService",
                    "ClaudeHomeServer.Services.OutputRingBuffer",
                    // FileService (IL-видимость, задача `8beee75e`): `DevServerService`/
                    // `LaunchConfigService`/`ProjectServiceDiscovery`/`DevServerService.<StartAsync>d__17`
                    // зовут `FileService.SafeJoin(...)` static-метод из тел методов.
                    // Точечный допуск по образцу `Tasks → FileService` (вертикаль → спинка).
                    "ClaudeHomeServer.Services.FileService",
                }),
        },
        // Changelog — «Что нового» (волна 4A, шаг 2): продуктовая история по коммитам всех
        // проектов + фоновый прогрев кеша. Внешние зависимости:
        // 1) Префикс-шов `Services.Llm` — `ICheapTextRunner` для дневной сводки (тот же шов,
        //    что у `Backgrounds`/`Git`/`Deploy`/`Docs`).
        // 2) Точечный допуск к корню Services — `FileService`: чтение git-вывода и
        //    `data/changelog/product.json` (ChangelogService).
        new object[]
        {
            new VerticalBoundary(
                "Changelog",
                "ClaudeHomeServer.Services.Changelog",
                SharedAllowedPrefixes
                    .Concat(new[]
                    {
                        "ClaudeHomeServer.Services.Changelog",
                        "ClaudeHomeServer.Services.Llm",
                    })
                    .ToArray(),
                new[]
                {
                    "ClaudeHomeServer.Services.FileService",
                }),
        },
        // Terminal — вертикаль PTY-терминала (волна 4A, листовая: регистрация одна,
        // подсистема не заведена). Допуски:
        // 1) Префикс-шов `ClaudeHomeServer.Execution` — `IProcessLauncher`/`ILauncherFactory`
        //    для запуска процессов терминалов (тот же шов, что у `ProjectServices`).
        // 2) Префикс-шов `ClaudeHomeServer.Hubs` — `IHubContext<TerminalHub>` для рассылки
        //    вывода/статусов терминала (TerminalService:91).
        // 3) Допуски к корню Services точечные:
        //    - `ProjectManager` — общая инфраструктура (`TerminalService:92`).
        //    - `OutputRingBuffer` — общий примитив реплея вывода (шапка `OutputRingBuffer.cs`).
        // 4) Точечные допуски к `ClaudeHomeServer.Protocol`: `TerminalOutputMessage`/
        //    `TerminalStatusMessage`/`TerminalRenamedMessage` — типы WS-событий терминала,
        //    которые TerminalService шлёт в хаб (TerminalService.cs:272,297,396). Префикс
        //    `ClaudeHomeServer.Protocol` снят (волна 3), чтобы сторож ловил новые зависимости.
        //    ⚠ Эти три записи **инертны** с точки зрения сторожа: использование
        //    идёт из ТЕЛ методов TerminalService (SendAsync с новым message-объектом
        //    не переживает await — поля state-машины нет), а сторож читает только
        //    публичные сигнатуры. Удаление всех трёх оставляет тест зелёным —
        //    проверено ревью 4A. Оставлены как **обозначение шва**, чтобы будущая
        //    правка TerminalService, вытащившая один из типов в публичную сигнатуру,
        //    сразу упёрлась в сторож — по образцу шва `Deploy → Services.Backup`,
        //    задокументированного в шапке (невидимо для рефлексии — обозначение шва,
        //    не контролируемое сторожем).
        new object[]
        {
            new VerticalBoundary(
                "Terminal",
                "ClaudeHomeServer.Services.Terminal",
                SharedAllowedPrefixes
                    .Concat(new[]
                    {
                        "ClaudeHomeServer.Services.Terminal",
                        "ClaudeHomeServer.Services.Execution",
                        "ClaudeHomeServer.Hubs",
                    })
                    .ToArray(),
                new[]
                {
                    "ClaudeHomeServer.Services.ProjectManager",
                    "ClaudeHomeServer.Services.OutputRingBuffer",
                    "ClaudeHomeServer.Protocol.TerminalOutputMessage",
                    "ClaudeHomeServer.Protocol.TerminalStatusMessage",
                    "ClaudeHomeServer.Protocol.TerminalRenamedMessage",
                }),
        },
        // Tasks — вертикаль задач с доской агентов (BoardService) и утренним брифингом
        // (DailyBriefingService, волна 4C, шаг 1).
        // Префикс-швы:
        // 1) `ClaudeHomeServer.Services.Llm` — `ICheapTextRunner` для `TaskAiService`
        //    (генерация описания/подзадач/классификация/нормализация/дедуп) и
        //    `DailyBriefingService` (утренний брифинг) — префикс-шов, как у
        //    `Git`/`Backgrounds`/`Deploy`/`Changelog`/`ProjectIcons`.
        // 2) `ClaudeHomeServer.Hubs` — `IHubContext<SessionHub>` для `TaskSchedulerService`
        //    (`BroadcastTaskChangedAsync`/напоминания) и `DailyBriefingService`
        //    (событие `task_reminder` в ленту).
        // Точечные допуски к корню `ClaudeHomeServer.Services.*` — «вертикаль → спинка»
        // (по аналогии с `Dossiers`/`Git`/`Spend`/`Knowledge`):
        // 1) `SessionManager` — `TaskSchedulerService` цикл «до готово» вызывает
        //    `TaskExecutionService` для автозапуска исполнителя; `BoardService` берёт
        //    живые сессии по id; `DailyBriefingService` использует для поиска чатов
        //    владельца. `SessionManager` остаётся backbone-типом корня.
        // 2) `ProjectManager` — `TaskAiService` берёт контекст проекта (CLAUDE.md);
        //    `DailyBriefingService` перебирает проекты владельца для git-активности;
        //    `TaskManager.LogTask` пишет события в проектный лог
        //    (TaskManager.cs:~LogTask, опциональный параметр ctor).
        // 3) `UserStore` — перебор пользователей в `TaskSchedulerService.TickAsync` и
        //    `DailyBriefingService.GenerateAsync`/`GitActivityAsync`.
        // 4) `NotificationService` — `SendNotificationMessageAsync` в TaskSchedulerService
        //    и DailyBriefingService.NotifyAsync (напоминания + «доброе утро»).
        // 5) `AppSettingsService` — гейт `DailyBriefingEnabled` (DailyBriefingService.MaybeRunScheduledAsync).
        // 7) `ProjectEventLogService` — запись событий задач в проектный лог
        //    (`TaskManager.LogTask`) + чтение событий за сутки (`DailyBriefingService`).
        // 8) `PersonaManager` — `BoardService` берёт персоны для подписи колонки;
        //    `DailyBriefingService` находит персона-секретаря; `TaskManager` —
        //    опциональная зависимость для лога.
        // 9) `PushService` — `DailyBriefingService.GenerateAsync` шлёт web-push по расписанию.
        // 10) `NotesService` — `DailyBriefingService.BuildAndWriteAsync` пишет в дневник
        //     (`GetOrCreateDaily`/`Update`). Пока остаётся в корне (следующий шаг
        //     волны 4C).
        // Точечный допуск к `ClaudeHomeServer.Protocol.*` — `NotificationMessage` материализуется
        // как параметр public-метода `TaskSchedulerService.SendNotificationAsync`
        // (см. `PushService`/`NotificationService`). Остальные Protocol-ссылки идут
        // через тела методов и async-state-машины; префикс `ClaudeHomeServer.Protocol`
        // снят (волна 3), чтобы сторож ловил новые зависимости.
        // Зафиксированные швы (IL-скан видит declaring-типы):
        // - `Tasks.TaskManager` ставит три резолвера на `Models.Session`
        //   (`Session.TaskSourceSessionResolver`/`TaskDelegationDepthResolver`/
        //   `TaskDoneResolver`) в конструкторе TaskManager.cs:32-36. Связь Tasks →
        //   Models видна через `ClaudeHomeServer.Models` (SharedAllowedPrefixes);
        //   мутация статической модели зафиксирована в `TasksSubsystem.Register`.
        // - `Services.TaskExecutionService` (корень, остаётся до этапа 4) зовёт
        //   `Services.Tasks.TaskSchedulerService.TaskUrl(...)` статически из 6 мест
        //   тел методов — Tasks-тип виден IL-скану из корневого типа.
        //   Когда `TaskExecutionService` переедет, шов сам разорвётся.
        // - `Services.PersonaAutomationService` (корень, остаётся в корне до этапа 4)
        //   зовёт `Services.Tasks.TaskDueCalculator.ResolveTimeZone(...)` в теле
        //   метода (`PersonaAutomationService.cs:531`). Аналогичный шов
        //   root → Tasks-тип через статику; разорвётся при переезде Persona.
        // - `Services.Tasks.TaskManager` зовёт `Services.ModelTiers.TryParse(...)` в
        //   теле метода (`TaskManager.cs:112,250`) — root-тип `ModelTiers`
        //   (AppSettingsService.cs:15). Шов Tasks → корень через статику.
        // - `Services.Tasks.TaskManager` зовёт `Services.ExecutorStopClassifier.IsTerminal(...)`
        //   в теле метода (`TaskManager.cs:429`) — root-тип `ExecutorStopClassifier`
        //   (ExecutorStopClassifier.cs:16). Шов Tasks → корень через статику.
        // - `Services.Tasks.TaskSchedulerService` зовёт
        //   `Controllers.TaskHubExtensions.BroadcastTaskChangedAsync(...)` в теле
        //   метода (`TaskSchedulerService.cs:128`) — это extension-метод,
        //   объявленный в `Controllers/TasksController.cs:486` (`public static class
        //   TaskHubExtensions`). ⚠ ИНВЕРСИЯ СЛОЁВ: сервис Tasks вызывает код,
        //   объявленный в `ClaudeHomeServer.Controllers.*`. Оставить как шов;
        //   разбор вынести в отдельную задачу этапа 4.
        new object[]
        {
            new VerticalBoundary(
                "Tasks",
                "ClaudeHomeServer.Services.Tasks",
                SharedAllowedPrefixes
                    .Concat(new[]
                    {
                        "ClaudeHomeServer.Services.Tasks",
                        "ClaudeHomeServer.Services.Llm",
                        "ClaudeHomeServer.Hubs",
                    })
                    .ToArray(),
                new[]
                {
                    // Корневые «спинки» (см. комментарий выше 1–10).
                    "ClaudeHomeServer.Services.SessionManager",
                    "ClaudeHomeServer.Services.ProjectManager",
                    "ClaudeHomeServer.Services.UserStore",
                    "ClaudeHomeServer.Services.NotificationService",
                    "ClaudeHomeServer.Services.AppSettingsService",
                    "ClaudeHomeServer.Services.ProjectEventLogService",
                    "ClaudeHomeServer.Services.PersonaManager",
                    "ClaudeHomeServer.Services.PushService",
                    // Notes — вертикаль (волна 4C, шаг 2): `DailyBriefingService.BuildAndWriteAsync`
                    // пишет в дневниковую заметку (`GetOrCreateDaily`/`Update`).
                    "ClaudeHomeServer.Services.Notes.NotesService",
                    // ⚠ TaskSchedulerService (Tasks) держит в ctor
                    // `TaskExecutionService executor` (полный жизненный цикл хода:
                    // автозапуск Claude-исполнителя + страховка незакрытой задачи)
                    // и `PersonaAutomationService automation` (collaborator проактивности).
                    // Оба остаются в корне до этапа 4 (расщепление SessionManager).
                    // Когда `TaskExecutionService`/`PersonaAutomationService` уедут —
                    // эти точечные допуски уйдут вместе с ними.
                    "ClaudeHomeServer.Services.TaskExecutionService",
                    "ClaudeHomeServer.Services.PersonaAutomationService",
                    // ⚠ Шов `Tasks → Models.Session`: три статических резолвера в
                    // TaskManager.cs:32-36. Контракт: мутация проходит до первого
                    // использования (TaskManager — singleton, инстанс строится при
                    // первом резолве из контроллеров/hosted-сервисов; Backbone
                    // SessionManager — тоже singleton, формирует сессии после этого).
                    // Префикс `ClaudeHomeServer.Models` уже открыт через SharedAllowedPrefixes,
                    // но сам факт мутации статической модели чужой вертикали фиксируем
                    // отдельным комментарием, чтобы ревью и расширение сторожа до IL видели
                    // волюнтаристский шов. Future-proof: свести к интерфейсу
                    // `ITaskFacts { string? SourceSession(string taskId); bool Done(string taskId); ... }`
                    // и инжектить в Session — отдельная задача (этап 4).
                    // Точечный допуск к `ClaudeHomeServer.Protocol.*` — `NotificationMessage`
                    // в публичной сигнатуре `TaskSchedulerService.SendNotificationAsync`.
                    "ClaudeHomeServer.Protocol.NotificationMessage",
                    // ⚠ Шов `Tasks → Models`-слой через `ModelTiers`/`ExecutorStopClassifier`
                    // (root-статика, см. комментарий выше). Пока объявляем как точечные
                    // имена; возможный путь — отдельный мини-шейп вроде `ITaskModelTierPolicy`.
                    "ClaudeHomeServer.Services.ModelTiers",
                    // ⚠ IL-видимость (задача `8beee75e`): `ModelTier` enum
                    // материализуется в поле async-state-машины `TaskManager`
                    // через `ModelTiers.TryParse`. Точечный допуск.
                    "ClaudeHomeServer.Services.ModelTier",
                    "ClaudeHomeServer.Services.ExecutorStopClassifier",
                    // ⚠ ИНВЕРСИЯ СЛОЁВ `Tasks → Controllers` через extension-метод
                    // `TaskHubExtensions.BroadcastTaskChangedAsync` (см. комментарий выше;
                    // TaskSchedulerService.cs:128). Прямой шов Tasks-вертикали на
                    // Controllers-пространство имён. Разбор и разворот — этап 4.
                    "ClaudeHomeServer.Controllers.TaskHubExtensions",
                }),
        },
        // Notes — вертикаль заметок (волна 4C, шаг 2). Obsidian-совместимый vault
        // (`[[wikilinks]]`/backlinks/граф/комментарии), AI-сводки, синк с Dify,
        // мост чекбоксов заметок ↔ задач (`NoteTaskSyncService`), авто-истечение
        // заметок (`NoteExpiryService`). `UnifiedSearchService` остаётся в корне —
        // он фасад поперёк Notes+Task, отдельная задача на разрез.
        // Префиксы-швы (по образцу `Tasks`/`Spend`/`Memory`/`Dossiers`):
        // 1) `ClaudeHomeServer.Services.Tasks` — префикс-шов через `NoteTaskSyncService`
        //    (`TaskManager` + `CreateTaskRequest` + `UpdateTaskRequest`): мост чекбоксов
        //    заметок и карточек задач. Шов снимается порядком — Tasks уже вертикаль,
        //    направление одно: Notes → Tasks.
        // 2) `ClaudeHomeServer.Services.Knowledge` — префикс-шов через `NotesKnowledgeService`
        //    (`IKnowledgeSyncParticipant` + `KnowledgeService` + `KnowledgeSyncTarget`):
        //    синхронизация заметок с Dify-датасетом per-owner; тот же шов, что у
        //    `Knowledge` → `Memory`/`Dossiers` и у `Memory`/`Spend` → `Knowledge`.
        // 3) `ClaudeHomeServer.Services.Llm` — префикс-шов через `NotesAiService`
        //    (`ICheapTextRunner`) для тегов/сводок заметок. Префикс-шов по прецеденту
        //    `Git`/`Backgrounds`/`Deploy`/`Changelog`/`ProjectIcons`/`Tasks`/`Docs`.
        // 4) `ClaudeHomeServer.Hubs` — префикс-шов через `NoteTaskSyncService` и
        //    `NoteExpiryService` (`IHubContext<SessionHub>`): рассылка `notes_changed`
        //    и напоминания об истечении заметок. По прецеденту `Tasks`/`Git`/`Images`/
        //    `ProjectServices`/`Terminal`/`Watchdog`.
        // Точечные допуски к корню `ClaudeHomeServer.Services.*` — «вертикаль → спинка»:
        // 5) `ProjectManager` — `NoteTaskSyncService` (projectId для промоута чекбокса),
        //    `NoteExpiryService` (проекты для авто-истечения), `NotesService` (пути
        //    к папкам заметок).
        // 6) `UserStore` — `NotesKnowledgeService` (имя владельца → имя Dify-датасета
        //    `{username}:notes`).
        // 7) Точечный допуск к `ClaudeHomeServer.Protocol` — `NotesChangedMessage` в
        //    `NoteTaskSyncService.BroadcastNoteChangedAsync` (материал-аргумент `SendAsync`,
        //    поле state-машины). Префикс `ClaudeHomeServer.Protocol` возвращён в
        //    `SharedAllowedPrefixes` (волна 1 IL-сторожа, 2026-09-06, задача `8beee75e`;
        //    решение зафиксировано в ADR-014 §«Решение по Protocol»), оставлен точный
        //    тип как документация явного шва по образцу `Spend`/`Memory`/`Dossiers`/`Watchdog`/`Terminal`.
        // 8) `FileService` (Services/ корень) — `NotesService` зовёт
        //    `FileService.SafeJoinPublic(...)` из 12 мест тел методов
        //    (NotesService.cs:128,133,498,653,660,724,767,780,814,815,874,889,903,920,1005
        //    и NotesService.Annotations.cs:41,50,447). Статический вызов через
        //    path-traversal-санитайзер — допускаем как инфраструктурный шов.
        // 9) ⚠ ИНВЕРСИЯ СЛОЁВ `Notes → Controllers` через extension-метод
        //    `TaskHubExtensions.BroadcastTaskChangedAsync` (NoteTaskSyncService.cs:61,106,157,162).
        //    Прямой шов Notes-вертикали на Controllers-пространство имён. Объявляем
        //    явно, разбор — этап 4 (как у `Tasks`).
        new object[]
        {
            new VerticalBoundary(
                "Notes",
                "ClaudeHomeServer.Services.Notes",
                SharedAllowedPrefixes
                    .Concat(new[]
                    {
                        "ClaudeHomeServer.Services.Notes",
                        "ClaudeHomeServer.Services.Tasks",
                        "ClaudeHomeServer.Services.Knowledge",
                        "ClaudeHomeServer.Services.Llm",
                        "ClaudeHomeServer.Hubs",
                    })
                    .ToArray(),
                new[]
                {
                    "ClaudeHomeServer.Services.ProjectManager",
                    "ClaudeHomeServer.Services.UserStore",
                    "ClaudeHomeServer.Services.ProjectEventLogService",
                    "ClaudeHomeServer.Services.FileService",
                    "ClaudeHomeServer.Protocol.NotesChangedMessage",
                    // ⚠ ИНВЕРСИЯ СЛОЁВ `Notes → Controllers` через extension-метод
                    // TaskHubExtensions (см. комментарий выше, пункт 9). Разбор — этап 4.
                    "ClaudeHomeServer.Controllers.TaskHubExtensions",
                }),
        },
        // Skills — вертикаль навыков (волна 4C, шаг 3): чтение скиллов и агентов из
        // глобального (~/.claude/skills, ~/.claude/workflows, ~/.claude/plugins) и
        // проектного (.claude/skills, .claude/agents) каталога; обёртка CLI «npx skills»
        // для поиска/установки из реестра skills.sh; LLM-подбор под персону/проект/запрос;
        // LLM-генерация тела нового навыка; перевод описаний RU→EN; фоновый перевод
        // плагиновых описаний с персистентным кешем.
        // Префиксы-швы (по образцу `Tasks`/`Notes`/`Dossiers`):
        // 1) `ClaudeHomeServer.Services.Llm` — `ICheapTextRunner` для `SkillSuggestService`,
        //    `SkillTranslationService`, `SkillGenerationService` (действия
        //    `LocalActionCatalog.SkillSuggest/SkillTranslate/SkillGenerate`). Префикс-шов,
        //    как у `Git`/`Backgrounds`/`Deploy`/`Changelog`/`ProjectIcons`/`Tasks`/`Docs`.
        // 2) `ClaudeHomeServer.Services.Execution` — `ILauncherFactory` для запуска
        //    CLI «npx skills» в `SkillsCliService.RunAsync` (запуск процесса — общий
        //    слой, по аналогии с `ProjectServices`/`Terminal`/`Git`/`Deploy`).
        // Точечные допуски к корню `ClaudeHomeServer.Services.*` — «вертикаль → спинка»:
        // 3) `PersonaManager` — `SkillSuggestService.SuggestForPersonaAsync` резолвит
        //    персону владельца и читает существующие Skill-привязки
        //    (`PersonaBinding.Target`) для исключения уже привязанных скиллов.
        // 4) `ProjectManager` — `SkillSuggestService.SuggestForProjectAsync` берёт
        //    контекст проекта (имя + системный промпт); `SkillsService.GetProjectSkills/
        //    Agents` работают с `projectRootPath`; `SkillsController` использует
        //    `GetById(projectId)` для получения пути.
        new object[]
        {
            new VerticalBoundary(
                "Skills",
                "ClaudeHomeServer.Services.Skills",
                SharedAllowedPrefixes
                    .Concat(new[]
                    {
                        "ClaudeHomeServer.Services.Skills",
                        "ClaudeHomeServer.Services.Llm",
                        "ClaudeHomeServer.Services.Execution",
                    })
                    .ToArray(),
                new[]
                {
                    "ClaudeHomeServer.Services.PersonaManager",
                    "ClaudeHomeServer.Services.ProjectManager",
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
        // Исключение `*.Tests` гарантирует, что любая тестовая сборка (сейчас их
        // четыре: `ClaudeHomeServer.Tests`, `ClaudeHomeServer.Video.Tests`,
        // `ClaudeHomeServer.Yandex.Tests`, `ClaudeHomeServer.Reader.Tests`) с её
        // стабами не путается с продовыми типами. Статический конструктор форсирует
        // загрузку всех вертикальных сборок до этого момента (см. комментарий там).
        var assemblies = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a =>
            {
                var name = a.GetName().Name;
                return name is not null
                    && (name == "ClaudeHomeServer" || name.StartsWith("ClaudeHomeServer.", StringComparison.Ordinal))
                    && !name.EndsWith(".Tests", StringComparison.Ordinal);
            })
            .ToList();

        var types = CollectTypesInNamespaceTree(assemblies, boundary.NamespaceRoot).ToList();

        // Защита от вакуумного прохода: если фильтр сборок или порядок загрузки сломается,
        // набор станет пустым и сторож пройдёт зелёным, ничего не проверив (доказано мутацией
        // в ревью 23d353d7: без `name == "ClaudeHomeServer"` — 17/17 зелёных при нуле типов).
        // Главная гарантия — `types.Should().NotBeEmpty(...)` ниже: пустой набор типов
        // ловится им. Порог count — вспомогательный, ловит «ни одной сборки не загружено».
        // 6 = Main + Core + 4 вынесенные на Этапе 3 (Video/Yandex/Reader/CodeGraph);
        // при добавлении новых `.csproj` подсистем обновить.
        assemblies.Should().HaveCountGreaterThanOrEqualTo(6,
            "после Этапа 3 сторож должен видеть 6 прод-сборок: ClaudeHomeServer, " +
            "ClaudeHomeServer.Core, ClaudeHomeServer.Video, ClaudeHomeServer.Yandex, " +
            "ClaudeHomeServer.Reader, ClaudeHomeServer.CodeGraph");
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

            // IL-скан тел методов (см. BoundaryIlScanner): ловит статические вызовы,
            // DI-резолвы `sp.GetRequiredService<T>()`, generic-аргументы инстанцированных
            // методов. Обход nested-типов (async-state-машины `<...>d__NN`,
            // `<>c__DisplayClass`) обязателен — без него сторож видит 4 из 7
            // известных швов (docs/research/il-boundary-scan-2026-09.md, раздел про слепые пятна).
            // Сбор идёт через общий BoundaryIlScanner.CollectAllReferencedTypes — ту же
            // точку, что и регрессия IlBoundaryRegressionTests; сломай обход там —
            // краснеют оба гейта (закрывает дыру «сторож зовёт другой код»).
            foreach (var referenced in BoundaryIlScanner.CollectAllReferencedTypes(type))
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
            "(Services.Http/Composition/Mcp, Protocol; Telemetry — только тремя точечными "
            + "допусками, префикса у неё нет) либо на самих себя. " +
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

    /// <summary>
    /// Единственный путь сбора типов: <see cref="BoundaryIlScanner.CollectAllReferencedTypes"/>.
    /// Прежде у сторожа было два независимых пути — IL-скан и собственная рефлексия полей,
    /// и подмена вызова сканера в коде сторожа оставляла все тесты зелёными. Теперь оба
    /// идут через ту же функцию: подмена реализации сканера роняет весь гейт единым
    /// движением, в том числе регрессию <see cref="IlBoundaryRegressionTests"/>.
    /// </summary>
    private static IEnumerable<Type> CollectReferencedTypes(Type type) =>
        BoundaryIlScanner.CollectAllReferencedTypes(type);


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

        // Сборка `ClaudeHomeServer.Core` — спинка по построению (Composition/Http/
        // Mcp-узлы плюс JsonFileStore/SsrfGuard/PermissionModeGuard/TeamProtocolMarkers).
        // assembly-проверка важнее namespace: 4 файла с namespace `ClaudeHomeServer.Services`
        // (root) физически живут в Core.dll и не должны флагаться как cross-vertical.
        if (IsCoreAssembly(type)) return true;

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

    /// <summary>
    /// Core.dll — спинка: не имеет project-ссылок (см. .csproj). Сторож
    /// гарантирует, что ни один тип Core не тянет в IL-операндах типы
    /// из проектных ассемблей (`ClaudeHomeServer*`). Это защищает от
    /// случайного добавления ProjectReference и сохраняя Core «чистым
    /// листом» по определению.
    /// </summary>
    [Fact]
    public void CoreDll_НеСодержитСсылокНаПроектныеАссембли()
    {
        var asm = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == CoreAssemblyName);
        Assert.NotNull(asm);

        var violations = new List<string>();

        foreach (var type in asm.GetTypes())
        {
            foreach (var referenced in BoundaryIlScanner.CollectAllReferencedTypes(type))
            {
                var refAsm = referenced.Assembly.GetName().Name;
                if (refAsm is not null && refAsm.StartsWith("ClaudeHomeServer", StringComparison.Ordinal)
                    && refAsm != CoreAssemblyName)
                {
                    violations.Add(
                        $"{type.FullName} → {referenced.FullName} (asm: {refAsm})");
                }
            }
        }

        Assert.True(violations.Count == 0,
            $"Core.dll должен ссылаться только на BCL/ASP.NET. " +
            $"Проектные ссылки (нарушение): {string.Join("; ", violations.Take(10))}");
    }

    /// <summary>
    /// Разрешённые неймспейсы спинки. Список закрытый: Core — общая инфраструктура
    /// (контракт подсистем, файловый стор, http-обвязка, secret-стор, guard'ы) плюс
    /// два перенесённых примитива в <c>Models</c>. Вертикалям тут места нет.
    /// </summary>
    /// <remarks>
    /// Корневого <c>ClaudeHomeServer.Services</c> здесь НЕТ намеренно: под ним в Main
    /// живут вертикали, и одного этого неймспейса хватило бы, чтобы спрятать вертикаль
    /// в спинке уровнем выше (ревью 894e3ec9 доказало мутацией — сторож молчал).
    /// Четыре корневых примитива Core пришпилены поимённо в <see cref="CoreAllowedRootTypes"/>.
    /// </remarks>
    private static readonly string[] CoreAllowedNamespaces =
    [
        "ClaudeHomeServer.Models",
        "ClaudeHomeServer.Services.Composition",
        "ClaudeHomeServer.Services.Http",
        "ClaudeHomeServer.Services.Mcp",
    ];

    /// <summary>
    /// Единственные типы, которым позволено лежать в Core прямо в корневом
    /// <c>ClaudeHomeServer.Services</c>. Список закрытый и поимённый: пятый примитив
    /// здесь — осознанное решение, а не побочный эффект переноса файла.
    /// </summary>
    private static readonly string[] CoreAllowedRootTypes =
    [
        "ClaudeHomeServer.Services.JsonFileStore",
        "ClaudeHomeServer.Services.PermissionModeGuard",
        "ClaudeHomeServer.Services.SsrfGuard",
        "ClaudeHomeServer.Services.TeamProtocolMarkers",
        // Этап 3, волна 1: чистые статики подняты в Core ради CodeGraph.
        // PathNormalizer — нормализация корня, ExecutableResolver — поиск по PATH+PATHEXT.
        "ClaudeHomeServer.Services.PathNormalizer",
        "ClaudeHomeServer.Services.ExecutableResolver",
    ];

    /// <summary>
    /// Второй сторож состава Core, и он про ДРУГУЮ дверь, чем
    /// <see cref="CoreDll_НеСодержитСсылокНаПроектныеАссембли"/>.
    ///
    /// Тот проверяет, что Core ни на кого не ссылается, — но покраснеть он может
    /// только если кто-то допишет <c>ProjectReference</c> в <c>Core.csproj</c>:
    /// без ссылки типы Main компилятору попросту не видны. Открытой оставалась
    /// обратная дверь: тип ВЕРТИКАЛИ, положенный в Core «чтобы собиралось».
    /// Внешних ссылок у него не будет (их неоткуда взять), первый сторож смолчит,
    /// а <c>IsCoreAssembly</c> проверяется РАНЬШЕ exact/prefix — и тип станет
    /// невидим обоим сторожам границ разом.
    ///
    /// Поэтому здесь список неймспейсов закрытый: новый неймспейс в Core — это
    /// осознанное решение и правка этого списка, а не побочный эффект переноса файла.
    /// </summary>
    [Fact]
    public void CoreDll_СодержитТолькоРазрешённыеНеймспейсы()
    {
        var asm = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == CoreAssemblyName);
        Assert.NotNull(asm);

        var types = asm!.GetTypes()
            .Where(t => !t.IsNested && t.Namespace is not null)
            .ToArray();
        Assert.NotEmpty(types); // защита от вакуумного прохода: пустой набор зеленит что угодно

        var strays = types
            .Where(t => !CoreAllowedNamespaces.Contains(t.Namespace!, StringComparer.Ordinal)
                     && !CoreAllowedRootTypes.Contains(t.FullName ?? "", StringComparer.Ordinal))
            .Select(t => $"{t.FullName} (namespace {t.Namespace})")
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.True(strays.Length == 0,
            "В Core.dll появились типы вне разрешённых неймспейсов — такой тип невидим "
            + "обоим сторожам границ (IsCoreAssembly срабатывает раньше allow-list). "
            + "Либо ему не место в спинке, либо неймспейс добавляется в CoreAllowedNamespaces "
            + "осознанно: " + string.Join("; ", strays.Take(10)));
    }
}