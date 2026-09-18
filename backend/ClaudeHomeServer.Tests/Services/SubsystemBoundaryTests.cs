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
        _ = typeof(ClaudeHomeServer.Services.Skills.SkillsSubsystem).Assembly;
        _ = typeof(ClaudeHomeServer.Services.Git.GitSubsystem).Assembly;
        _ = typeof(ClaudeHomeServer.Services.Tts.TtsSubsystem).Assembly;
        _ = typeof(ClaudeHomeServer.Services.Notes.NotesSubsystem).Assembly;
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
        // сторож видел типы Turn (IPromptSectionContributor и пр.) и проверял их
        // границы по сборке Turn.dll.
        _ = typeof(ClaudeHomeServer.Services.Turn.IPromptSectionContributor).Assembly;
        // Dossiers/Memory — отдельные сборки (Этап 5, волна 3, финал): форс-загрузка нужна,
        // чтобы сторож видел их типы и проверял границы по Dossiers.dll / Memory.dll.
        _ = typeof(ClaudeHomeServer.Services.Dossiers.DossiersSubsystem).Assembly;
        _ = typeof(ClaudeHomeServer.Services.Memory.MemorySubsystem).Assembly;
        // Desktop — отдельная сборка (Этап 5, вынос Desktop): форс-загрузка нужна,
        // чтобы сторож видел типы грани (маршрутизатор канала, хаб устройств, схемы
        // авторизации) и проверял их границы по Desktop.dll.
        _ = typeof(ClaudeHomeServer.Services.Desktop.DesktopCallRouter).Assembly;
        // === Этап 5, волна C, шаг 2: новые швы Core, использованные вынесенными
        // вертикалями. Форс-загрузка нужна, чтобы вертикальные сборки (Modules,
        // ProjectServices) видели соответствующие Core-интерфейсы по сборке Core.dll.
        _ = typeof(ClaudeHomeServer.Services.Execution.ISandboxPortRange).Assembly;
        _ = typeof(ClaudeHomeServer.Services.Backgrounds.BackgroundsSubsystem).Assembly;
        _ = typeof(ClaudeHomeServer.Services.IProjectBackgroundWriter).Assembly;
        _ = typeof(ClaudeHomeServer.Services.ProjectIcons.ProjectIconsSubsystem).Assembly;
        _ = typeof(ClaudeHomeServer.Services.IProjectIconMigrator).Assembly;
        _ = typeof(ClaudeHomeServer.Services.IDataBackupService).Assembly;
        _ = typeof(ClaudeHomeServer.Services.Terminal.TerminalService).Assembly;
        _ = typeof(ClaudeHomeServer.Services.Composition.ITerminalHubNotifier).Assembly;
        // Llm — отдельная сборка (Этап 5, финал линии): форс-загрузка нужна, чтобы
        // сторож видел сборку Llm.dll и её типы. Без неё изолированный прогон
        // проходит по вертикали вакуумно.
        _ = typeof(ClaudeHomeServer.Services.Llm.LlmSubsystem).Assembly;
        // Execution/Deploy — отдельные сборки (Этап 5, волна 2): форс-загрузка,
        // чтобы сторож видел DockerProcessRunner/SandboxManager и Deploy-типы.
        _ = typeof(ClaudeHomeServer.Services.Execution.DockerProcessRunner).Assembly;
        _ = typeof(ClaudeHomeServer.Services.Deploy.DeployService).Assembly;
        // Images — отдельная сборка (Этап 5, вынос Images): форс-загрузка нужна,
        // чтобы сторож видел типы Images (IImageGenerator, ImageGenerationService)
        // и проверял границы по Images.dll.
        _ = typeof(ClaudeHomeServer.Services.Images.ImagesSubsystem).Assembly;
        // Prompts — отдельная сборка (Этап 5, вынос Prompts): форс-загрузка нужна,
        // чтобы сторож видел типы Prompts (OmoPrompts, SubagentPrompts, OmcPersonaRouting)
        // и проверял границы по Prompts.dll.
        _ = typeof(ClaudeHomeServer.Services.Prompts.OmoPrompts).Assembly;
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
        // `ClaudeHomeServer.Protocol` — WS-контракт с фронтом. После Ф3б едет
        // в `Core.dll` (задача `b6f0e1f4`, ADR-014 §«Решение по Protocol»),
        // допуск идёт по имени сборки через `IsCoreAssembly` — namespace-запись
        // здесь больше не нужна, иначе она бы тихо пропускала случайное
        // появление типа `ClaudeHomeServer.Protocol.*` в Main.
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
        // Images — вертикаль генерации картинок. После выноса (Этап 5) все зависимости
        // на внешние типы уходят в Core-интерфейсы: `IPersonaLookup`/`IPersonaAvatarStore`
        // (аватар персоны), `ServerMessage` (ImageBackfilledMessage), `ImageAssetHelper`
        // (ExtFor) — все в ClaudeHomeServer.Core.dll, покрываются `IsCoreAssembly`.
        // Допусков за пределами спинки и своего namespace не осталось.
        new object[]
        {
            new VerticalBoundary(
                "Images",
                "ClaudeHomeServer.Services.Images",
                SharedAllowedPrefixes
                    .Concat(new[] { "ClaudeHomeServer.Services.Images" })
                    .ToArray(),
                Array.Empty<string>()),
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
        // 2) Execution-зависимость снята (задача 36e7a41d): `ILauncherFactory`/
        //    `IProcessLauncher`/`ProcessSpec` живут в Core (assembly-фильтр),
        //    ProjectReference на ClaudeHomeServer.Execution из Deploy.csproj убран.
        // 3) Git-зависимость снята (задача 36e7a41d): `DeployHost` теперь работает
        //    через Core-шов `IGitRepoChecker` (Services.Composition), а не на
        //    конкретном `GitService`. Прежняя связь «вертикаль → вертикаль»
        //    (`ClaudeHomeServer.Services.Git` в allow-list) больше не требуется.
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
        // Backgrounds — вертикаль фона рабочего пространства проекта (ADR-008,
        // Этап 5, волна C, шаг 2). Чтение проекта через Core-шов `IProjectManager`,
        // запись (TryBeginBackground / SetBackground* / Update / BackgroundsDir) —
        // через Core-шов `IProjectBackgroundWriter` (узкий форвардер к ProjectManager
        // из Main, см. `Services/ProjectBackgroundWriterAdapter.cs`). Раньше здесь
        // лежали точечные допуски на `ProjectManager` и `UserStore` — оба мёртвые
        // после выноса в .csproj: Backgrounds физически не может сослаться на
        // `ProjectManager` (нет ProjectReference на Main), запись идёт через
        // Core-интерфейс, `IUserStore` уже в Core. `ICheapTextRunner` (ход модели,
        // генерирующий JSON фигур) и `LocalActionCatalog.ProjectBackground` — в Core,
        // покрыты `IsCoreAssembly`, префикс `Services.Llm` в allow-list не нужен.
        new object[]
        {
            new VerticalBoundary(
                "Backgrounds",
                "ClaudeHomeServer.Services.Backgrounds",
                SharedAllowedPrefixes
                    .Concat(new[]
                    {
                        "ClaudeHomeServer.Services.Backgrounds",
                    })
                    .ToArray(),
                Array.Empty<string>()),
        },
        // ProjectIcons — вертикаль значка проекта (ADR-009, Этап 5, волна C, шаг 2).
        // Чтение проекта через Core-шов `IProjectManager`, запись `Icon.Glyph` —
        // через Core-шов `IProjectIconMigrator`, снимок data перед необратимой
        // операцией — через Core-шов `IDataBackupService` (формализация прежней
        // полумеры `ProjectIconMigration.cs:78-84`, Этап 5 курс Андрея 2026-09-08
        // делает шов обязательным). Точечные `ProjectManager`/`Backup.*` сняты:
        // вертикаль физически не может сослаться на Main-типы после выноса в
        // .csproj, запись идёт через Core-интерфейсы, чтение через `IProjectManager`
        // (Core). `ICheapTextRunner`/`LocalActionCatalog` (ходы подбора) — в Core,
        // покрыты `IsCoreAssembly`, префикс `Services.Llm` в allow-list не нужен.
        new object[]
        {
            new VerticalBoundary(
                "ProjectIcons",
                "ClaudeHomeServer.Services.ProjectIcons",
                SharedAllowedPrefixes
                    .Concat(new[]
                    {
                        "ClaudeHomeServer.Services.ProjectIcons",
                    })
                    .ToArray(),
                Array.Empty<string>()),
        },
        // Spend — аналитика расхода токенов (Spend Analytics v2, Этап 5).
        // Allow-list пустой поверх общей спинки: после выноса Spend в отдельный .csproj
        // ВСЕ его внешние зависимости идут через Core-швы (`ISessionDirectory`,
        // `IProjectManager`, `IUserStore`, `IPersonaLookup`, `ITaskLookup`,
        // `IChatHistoryLoader`, `IModelResolver`, `ISpendCollector`). Раньше здесь
        // были точечные допуски на `SessionManager`/`ProjectManager`/`TaskManager`/
        // `PersonaManager`/`UserStore`/`ChatHistoryService` (Main) и пять типов
        // `ClaudeHomeServer.Protocol.*` — после выноса все они стали мёртвыми:
        //  * Main-типы — Spend.dll не имеет ProjectReference на Main, IL-скан их
        //    в Spend просто не видит;
        //  * типы протокола — `StoredMessage`/`StoredResultMessage`/`StoredFalCostMessage`/
        //    `StoredGlifCostMessage`/`UsageInfo` переехали в Core-сборку ещё на
        //    выносе протокола (Этап 5, ADR-014 §«Решение по Protocol»), покрываются
        //    `IsCoreAssembly` сторожа.
        // Мутация (убрать все 11 допусков → прогон `SubsystemBoundary`) дала зелёный
        // результат — снятие законно. Если вернётся хоть одна прямая зависимость
        // на Main-тип — сторож укажет точное имя и файл через IL-скан.
        //
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
                    })
                    .ToArray(),
                Array.Empty<string>()),
        },
        // Dossiers — паспорта изменений (ADR-004). Вынесена в отдельный `.csproj`
        // (Этап 5, волна 3, финал), единственный `ProjectReference` — на Core.
        // Allow-list ПУСТ поверх общей спинки, как у Knowledge: все прежние допуски
        // проверены мутацией (сняты все разом → сторож остался зелёным) и оказались
        // мёртвыми. Причина не в сторожé, а в компиляторе: вертикаль в своей сборке
        // физически не видит типы Main, и остаток связей пришлось разрезать швами.
        // Куда уехало то, что раньше требовало допуска:
        // - `SessionManager`/`ProjectManager`/`UserStore`/`TaskManager`/`FileService`/
        //   `KnowledgeService` — Core-швы `ISessionDirectory`/`IProjectManager`/
        //   `IUserStore`/`ITaskLookup`/`IKnowledgeIndex` (волна 2);
        // - `Services.Git`/`Services.CodeGraph` — `IGitRefSnapshotStore`/
        //   `IGitCommitInspector`/`ICodeGraphInspector` (волна 2). Общий `IGitGuard`
        //   на Deploy+Dossiers по-прежнему ОТКЛОНЁН (разведка 2026-09-07): 7+ разных
        //   методов с 5 намерениями — грабмешок, не шов;
        // - `Services.Memory` (общий слой Dify-синка) — Core, `Core/Services/Memory/`;
        // - `Services.Llm.TranscriptMigrator.IsSafeSessionId` — Core-примитив
        //   `Services.SessionIdGuard.IsSafe` (волна 3). Сама `Services/Llm` НЕ тронута:
        //   её ведёт соседняя линия, поэтому одноимённая копия предиката там осталась
        //   — техдолг на один шаг, помечен в `SessionIdGuard`;
        // - `SessionSummaryService.BuildTranscript` — Core-примитив
        //   `Services.SessionTranscript.Build` (волна 3), в Main остался форвардер;
        // - `InstanceSecretFiles` — переехал в Core целиком (волна 3);
        // - `SessionChangedPaths` — уже жил в Core, допуск был мёртв;
        // - `Protocol.StoredMessage` — покрыт префиксом `ClaudeHomeServer.Protocol`
        //   из `SharedAllowedPrefixes`, точечный допуск был лишним.
        // `FeatureFlagService` (гейт флага `change-dossiers-recall`) — тоже мёртв:
        // вертикаль ходит за флагом через Core-шов, а не за конкретным типом Main.
        // Появится новая прямая зависимость — сборка не пройдёт раньше сторожа.
        new object[]
        {
            new VerticalBoundary(
                "Dossiers",
                "ClaudeHomeServer.Services.Dossiers",
                SharedAllowedPrefixes
                    .Concat(new[]
                    {
                        "ClaudeHomeServer.Services.Dossiers",
                    })
                    .ToArray(),
                Array.Empty<string>()),
        },
        // Knowledge — вертикаль Dify RAG (Knowledge.md + ADR-013 §4). Allow-list
        // пустой поверх общей спинки: всё, что раньше требовало точечных допусков
        // к `Services`/`Controllers`, после выноса закрыто швами в `Core`. Перечень
        // швов (источник правды — `Core/Services/Composition/`, `Core/Services/Knowledge/`,
        // `Core/Services/IKnowledgeNotificationDispatcher.cs`):
        // - `IProjectFileGateway` (был `FileService` + `FileService.OnMutated` в allow-list
        //   п.4 и комментарии про Hubs-шов) — `ProjectKnowledgeSyncService` подписан
        //   на `OnMutated` через адаптер `ProjectFileGateway` (Services/), адаптер
        //   идёт через `FileService` (инвариант, Core/Services/Composition/IProjectFileGateway.cs:13-16).
        // - `ISessionMessageObserver` (был `Hubs.SessionHub`/`SessionManager` в п.5 и
        //   комментарии про Hubs-шов) — `ProjectKnowledgeTurnSync` слушает события хода
        //   через Core-шов, без прямого доступа к `SessionManager`.
        // - `IKnowledgeHubNotifier` (был `Hubs.SessionHub`/`IHubContext<SessionHub>`
        //   в комментарии про Hubs-шов) — `ProjectKnowledgeSyncService` шлёт
        //   `knowledge_changed` через Core-шов; реализация живёт в Main и инъектится
        //   форвардером в Program.cs.
        // - `IKnowledgeNotificationDispatcher` (был `NotificationService`/`NotificationStore`
        //   в п.1) — `KnowledgeAlertNotifier` ходит через Core-шов (Core/Services/IKnowledgeNotificationDispatcher.cs).
        // - `IKnowledgeSyncParticipant` (был `DossierStore`/`NotesKnowledgeService` в п.8-9)
        //   — `UserKnowledgeCascade` чистит датасеты per-участник через Core-контракт,
        //   форвардеры `IKnowledgeSyncParticipant → {DossierStore, NotesKnowledgeService,
        //   ProjectKnowledgeSyncService, ...}` остаются в Program.cs (кросс-вертикальный клей)
        //   и поэтому не входят в allow-list Knowledge.
        // - `IKnowledgeIndex` (был `KnowledgeBaseSummary`/`KnowledgeBaseDetail`/
        //   `KnowledgeDocumentDto` в п.10) — узкий Core-контракт из шести методов для
        //   Notes/Memory/Dossiers; DTO (`DifyDocumentInfo`/`DifyRetrieveChunk`/
        //   `KnowledgeMetadataFilter`/`KnowledgeMetadataFieldInfo`) переехали в
        //   `Core/Services/Knowledge/KnowledgeDtos.cs` (Этап 5, волна 5), отдельной
        //   сборки под DTO в Controllers больше нет.
        // - `IProjectManager` (был п.3) — `ProjectKnowledgeSyncService`/`UserKnowledgeCascade`
        //   резолвят путь проекта через Core-шов (Core/Services/Composition/IProjectManager.cs);
        //   `ProjectManager` живёт в `Services/` root, но обращение из Knowledge идёт
        //   через интерфейс, который собирается в Core и потому покрыт `IsCoreAssembly`.
        // `UserStore` (был п.2) — больше не нужен: префиксный допуск имени пользователя
        // для определения «своей»/«чужой» БЗ заменён на `IUserLookup` (Core).
        // `PersonaManager`/`PersonaMemoryService` (был п.6) — `UserKnowledgeCascade`
        // ходит через Core-шов, конкретные типы персон не видит.
        // `TeamMemoryService` (был п.7) — то же: `UserKnowledgeCascade` идёт через
        // `IKnowledgeSyncParticipant`, и для Team подключён форвардер в Program.cs.
        // Итого: `AllowedExactNamespaces` пуст, точечных допусков нет. Появится новая
        // прямая зависимость — добавляется шов в Core, иначе сторож укажет на нарушение.
        new object[]
        {
            new VerticalBoundary(
                "Knowledge",
                "ClaudeHomeServer.Services.Knowledge",
                SharedAllowedPrefixes
                    .Concat(new[]
                    {
                        "ClaudeHomeServer.Services.Knowledge",
                    })
                    .ToArray(),
                Array.Empty<string>()),
        },
        // Watchdog — серверные сторожа чатов (ADR-013). Вынесена в отдельный `.csproj`
        // (Этап 5, Этап 5b), единственный `ProjectReference` — на Core.
        // Допуски:
        // 1) `ClaudeHomeServer.Services.Execution` — `ILauncherFactory`/`IProcessLauncher`/
        //    `ProcessSpec` для poll-команды (WatchdogRunner.cs). Все в Core-сборке,
        //    но namespace не входит в SharedAllowedPrefixes — точечный prefix-допуск.
        // 2) `ClaudeHomeServer.Protocol` — `WatchdogsChangedMessage` (WatchdogNotifier.cs).
        //    В Core-сборке, допуск идёт через `IsCoreAssembly`, но prefix-запись
        //    оставлена как документация явного шва (прецедент Memory/Dossiers).
        // Main-типы: `WatchdogEnvironment` (SessionManager, ProjectManager, UserStore,
        // UserHomeResolver) и `WatchdogAlarm` (SessionMessagingService) живут в Main
        // namespace `ClaudeHomeServer.Services.Watchdog` как адаптеры — точечные допуски
        // на их Main-зависимости.
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
                    })
                    .ToArray(),
                new[]
                {
                    "ClaudeHomeServer.Protocol.WatchdogsChangedMessage",
                    // Адаптеры в Main (namespace Watchdog) ссылаются на Main-типы:
                    "ClaudeHomeServer.Services.SessionMessagingService",
                    "ClaudeHomeServer.Services.SessionMessagingService+SendOutcome",
                    "ClaudeHomeServer.Services.SessionMessagingService+SendOutcome+Completed",
                    "ClaudeHomeServer.Services.SessionMessagingService+SendOutcome+Queued",
                    "ClaudeHomeServer.Services.SessionMessagingService+SendOutcome+Running",
                    "ClaudeHomeServer.Services.SessionManager",
                    "ClaudeHomeServer.Services.UserStore",
                    "ClaudeHomeServer.Services.UserHomeResolver",
                    "ClaudeHomeServer.Services.IProjectManager",
                    "ClaudeHomeServer.Services.ProjectManager",
                }),
        },
        // Memory — долгая память персон и общая память команды проекта. Вынесена
        // в отдельный `.csproj` (Этап 5, волна 3, финал), единственный
        // `ProjectReference` — на Core. Allow-list ПУСТ поверх общей спинки, как
        // у Knowledge/Dossiers: прежние допуски сняты все разом и сторож остался
        // зелёным — значит каждый был мёртв. Держит их мёртвыми компилятор: из своей
        // сборки вертикаль типы Main не видит вовсе.
        // Куда уехало то, что раньше требовало допуска:
        // - `SessionManager`/`ProjectManager`/`UserStore`/`PersonaManager`/
        //   `ProjectEventLogService` — Core-швы (`ISessionDirectory`/`IProjectManager`/
        //   `IUserStore`/`IPersonaResolver`+`IPersonaLookup`+`IPersonaDirectory`/
        //   `IProjectEventLogService`);
        // - `Services.Knowledge` (клиент Dify) и `Services.Notes` — `IKnowledgeIndex`/
        //   `IKnowledgeSyncParticipant`/`INoteAccessor` (волна 2);
        // - `Services.Dossiers.DossierRecallService` — Core-шов на ОДИН метод
        //   `IDossierRecallSource.BuildRecallBlockAsync` (волна 3). Это была последняя
        //   связь «вертикаль → вертикаль» между Memory и Dossiers; опциональность
        //   (`IDossierRecallSource?`) сохранена — на ней стоит публичный
        //   `PersonaMemoryService.DossierRecallAvailable`, и «канала нет» тут штатно;
        // - `SessionSummaryService.BuildTranscript` — Core-примитив
        //   `Services.SessionTranscript.Build` (волна 3), в Main остался форвардер;
        // - `Telemetry.ServerMetrics`/`Telemetry.DifyErrorCategorizer` — общий слой
        //   Dify-синка (`MemoryDify`) уехал в Core, а метрику вертикаль берёт
        //   Core-швом `IDifyMetrics` (волна 2);
        // - префикс `ClaudeHomeServer.Hubs` — мёртв: вещание идёт Core-швом
        //   `ISessionBroadcaster` (TeamMemoryAutolearnService.cs:29), а не через
        //   `IHubContext<SessionHub>` напрямую;
        // - `Protocol.StoredMessage` (`AutolearnGate.CheckContent`/`LastTurnLength`) —
        //   покрыт префиксом `ClaudeHomeServer.Protocol` из `SharedAllowedPrefixes`;
        // - `PersonaMemoryScorer`/`TeamMemoryScorer` — свои же типы вертикали,
        //   покрыты её собственным префиксом.
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
                    })
                    .ToArray(),
                Array.Empty<string>()),
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
                    // LlmSessionContext, TurnFileWatcher,
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
                    "ClaudeHomeServer.Services.AppSettingsService",
                    "ClaudeHomeServer.Services.ChatHistoryService",
                    "ClaudeHomeServer.Services.FileService",
                    "ClaudeHomeServer.Services.SessionSummaryService",
                    // `Notes.NotesService` снят (Этап 5, 2026-09-12): прямых ссылок
                    // из Llm на сервис вертикали Notes не осталось (замечание сторожа
                    // было осиротевшим — пережило переезд `ChatDigestService` в спину).
                    // ProjectManager снят: сборку частей системного промпта
                    // (GetSystemPromptParts + SystemPromptPart) забрала спина —
                    // Core: Services/Llm/SystemPromptComposer, а встроенная часть
                    // промпта приезжает в ClaudeSession текстом через LlmSessionContext.
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
                    // (Этап 5: после переезда ISpendCollector в Core сборка закрыта
                    // `IsCoreAssembly`, точечный допуск снят — мёртвый.)
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
                    // `Llm → Prompts`: ClaudeSession ссылается на `CoordinatorWriteGuard`,
                    // `ChatContextPrompts`, `VoicePrompts` (все три — Core, этап 5, шаг 2;
                    // проходят IsCoreAssembly). Цикл Llm ⇄ Prompts (вертикаль) разрезан
                    // выносом Prompts: Llm не ссылается на типы вертикали Prompts.dll.
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
                    })
                    .ToArray(),
                new[]
                {
                    "ClaudeHomeServer.Services.FileService",
                }),
        },
        // Turn — шина событий хода + контрибьюторы секций системного промпта.
        // Все внешние допуски точечные (префиксов-швов нет): root Services, Dossiers
        // (PersonaRecallContributor тянет DossierRecallService/Request),
        // Services.Llm (TurnRunPassport в TurnCompleted, SubagentRunPassport в
        // SubagentRunCompleted), Protocol (StoredMessage/McpServerInfo/PromptSnapshotDraft).
        //
        // Этап 5, шаг 6 (инверсия контрибьюторов): CodeGraphContributor и
        // NotesRecallContributor уехали в свои вертикали (CodeGraph/Notes), а
        // PersonaRecallContributor перешёл на Core-форвард KnowledgeQueryUtilities —
        // допуски на CodeGraphPromptProvider / NotesKnowledgeService / NoteSemanticHit /
        // KnowledgeService сняты как мёртвые.
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
                // Точечных допусков НЕТ. После выноса в отдельный .csproj все вне-Core
                // зависимости закрыты швами (`IFeatureFlagGate`, `IPersonaResolver`,
                // `IPersonaPromptAssembler`, `IPersonaBindingsSource`, `IChatHistoryLoader`),
                // а `OnboardingPrompts` и `SessionChangedPaths` ПЕРЕЕХАЛИ в Core —
                // вертикаль берёт их оттуда. Допуски на эти два типа были заведены
                // исполнителем как «живые», но мутация показала обратное: сторож
                // остаётся зелёным без них. Сняты 2026-09-10.
                Array.Empty<string>()),
        },
        // Prompts — статические каталоги секций промпта (OmO/онбординг/голос/команды).
        // Вынесена в отдельную сборку (Этап 5, финал линии). Зависимости — только Core
        // (ModelTier, PersonaConsultantToolset, SubagentRunPassport, SpecialtyCatalog —
        // все в Core, проходят IsCoreAssembly). Точечных допусков нет.
        new object[]
        {
            new VerticalBoundary(
                "Prompts",
                "ClaudeHomeServer.Services.Prompts",
                SharedAllowedPrefixes
                    .Concat(new[] { "ClaudeHomeServer.Services.Prompts" })
                    .ToArray(),
                Array.Empty<string>()),
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
        // Desktop — ручной агент песочницы (ADR-008), отдельная сборка
        // ClaudeHomeServer.Desktop (Этап 5, вынос Desktop). Допусков нет: связи с корнем
        // закрыты швами спины (ISessionDirectory/IFeatureFlagGate/IPersonaResolver/
        // IProjectManager/IUserStore/IDesktopCapabilityTokens), хаб устройств переехал
        // в саму вертикаль, а Protocol.* проходит по сборке Core.
        new object[]
        {
            new VerticalBoundary(
                "Desktop",
                "ClaudeHomeServer.Services.Desktop",
                SharedAllowedPrefixes
                    .Concat(new[] { "ClaudeHomeServer.Services.Desktop" })
                    .ToArray(),
                Array.Empty<string>()),
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
        // Modules — YARP-реверс-прокси для внешних модулей (Этап 5, волна C, шаг 2).
        // Префикс-шов Yarp.ReverseProxy целиком (third-party, по прецеденту AngleSharp
        // у Reader — открываем префиксом; ModuleProxyConfigProvider ссылается на
        // Configuration/Forwarder/Transforms, ModuleGatewayMiddleware — на Forwarder).
        // Все Main-зависимости сняты волной C швов (`b7eb7ca7`, `dd48831b`):
        // `FeatureFlagService`/`JwtService`/`UserStore` → Core-швы
        // `IModuleFeatureFlagReader`/`IUserTokenValidator`/`IUserStore`;
        // `Services.Llm.LocalAction`/`LocalActionCatalog`/`CheapProfile`/
        // `Services.ModelTier` — в Core (assembly-фильтр, ловятся раньше namespace-чека).
        // `AllowedExactNamespaces` пуст: ни одного «соседа по корню Services» в коде
        // не осталось, швы держат все общие зависимости.
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
                Array.Empty<string>()),
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
        // git/task). Вынесены в отдельный `.csproj` (Этап 5, Этап 5b), единственный
        // `ProjectReference` — на Core. Allow-list ПУСТ: все прежние допуски к Main-типам
        // (AppSettingsService/ProjectManager/UserHomeResolver/FileService/PersonaManager/
        // NotesService/TaskManager/RuleRuntimeState/GroupChatRouter) сняты швами:
        // IHomePathResolver, IProjectManager (Core), ICommitLogReader, IPersonaHandleResolver,
        // INoteSummaryReader, ITaskStatusReader — все в Core.
        new object[]
        {
            new VerticalBoundary(
                "TriggerSources",
                "ClaudeHomeServer.Services.TriggerSources",
                SharedAllowedPrefixes
                    .Concat(new[] { "ClaudeHomeServer.Services.TriggerSources" })
                    .ToArray(),
                Array.Empty<string>()),
        },
        // ProjectServices — вертикаль раздела «Сервисы проекта» (Этап 5, волна C, шаг 2).
        // Волна C швов + Этап 5 выноса сняли все Main-зависимости кроме Yarp:
        // - `ProjectManager` → Core-шов `IProjectManager` (чтение через GetById;
        //   записей нет — `ProjectServices` нигде не меняет проект).
        // - `JwtService` → Core-шов `IPreviewTokenValidator` (валидация превью-токена,
        //   шаг 1 волны C).
        // - `FileService.SafeJoin` → Core-примитив `SafePath.Join` (шаг 1б волны C).
        // - `FileService.TreeExcludes` → Core-примитив `TreeExcludes` (шаг 1б волны C).
        // - `OutputRingBuffer` — перенесён в Core (`IsCoreAssembly`), отдельный допуск не нужен.
        // - `Services.Execution.ILauncherFactory`/`IProcessLauncher`/`ISandboxPortRange`/
        //   `ProcessSpec` — Core (тот же assembly-фильтр).
        // - `IHubContext<SessionHub>` → Core-шов `ISessionBroadcaster` (Этап 5, Ф4).
        // - `SandboxManager.Options.PortRangeStart/Size` → Core-шов `ISandboxPortRange`
        //   (этот шаг).
        // Остался только Yarp.ReverseProxy (third-party) — префикс по прецеденту AngleSharp
        // у Reader.
        new object[]
        {
            new VerticalBoundary(
                "ProjectServices",
                "ClaudeHomeServer.Services.ProjectServices",
                SharedAllowedPrefixes
                    .Concat(new[]
                    {
                        "ClaudeHomeServer.Services.ProjectServices",
                        "Yarp.ReverseProxy",
                    })
                    .ToArray(),
                Array.Empty<string>()),
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
                    })
                    .ToArray(),
                new[]
                {
                    "ClaudeHomeServer.Services.FileService",
                }),
        },
        // Terminal — вертикаль PTY-терминала (Этап 5, волна C, шаг 2; вертикаль без
        // IAppSubsystem — регистрация одна, прецедент `Services.Watchdog`).
        // Все прежние допуски сняты как мёртвые после выноса в отдельный .csproj —
        // вертикаль физически не может сослаться на Main (нет ProjectReference):
        // - `IHubContext<TerminalHub>` (префикс `ClaudeHomeServer.Hubs`) → Core-шов
        //   `ITerminalHubNotifier` (три метода: SendToClient/SendToGroup/AddToGroup).
        //   Прежде это было «названное исключение» Ф4 наравне с `DesktopCallRouter`;
        //   курс Этапа 5 на вынос ВСЕХ вертикалей потребовал закрыть его швом.
        //   Реализация `TerminalHubNotifier` осталась в `Hubs/` рядом с `TerminalHub`
        //   (SignalR — транспорт, живёт в Main; тот же приём, что `ISessionBroadcaster`).
        // - `ProjectManager` → Core-шов `IProjectManager` (только GetById, записей нет).
        // - `OutputRingBuffer` — переехал в Core, ловится `IsCoreAssembly`.
        // - `Services.Execution.ILauncherFactory`/`IProcessLauncher`/`ProcessSpec` — Core,
        //   тот же assembly-фильтр; `ConPtyBridgeLocator` поднят в Core этим шагом
        //   (чистая статика «папка + билд ОС», прецедент `ExecutableResolver`).
        // - Три типа `Protocol.Terminal*Message` были ИНЕРТНЫ и в старой записи
        //   (использование только из тел методов, сторож читает сигнатуры;
        //   проверено ревью 4A) — сняты вместе с остальными: `Protocol` едет в
        //   Core.dll и покрыт `IsCoreAssembly`.
        new object[]
        {
            new VerticalBoundary(
                "Terminal",
                "ClaudeHomeServer.Services.Terminal",
                SharedAllowedPrefixes
                    .Concat(new[]
                    {
                        "ClaudeHomeServer.Services.Terminal",
                    })
                    .ToArray(),
                Array.Empty<string>()),
        },
        // Tasks — вертикаль задач с доской агентов (Этап 5, вынос вертикали в
        // отдельный csproj). После выноса физически не может ссылаться на Main
        // (нет ProjectReference), и весь прежний список допусков закрыт контрактами
        // из Core, которые проходят более ранний чек `IsCoreAssembly`:
        //  - `SessionManager` → `ISessionDirectory` (3 метода по факту вызовов);
        //  - `PersonaManager` → `IPersonaLookup` (1 метод);
        //  - `NotificationService` → `ITaskNotificationDispatcher` (2 метода);
        //  - `TaskExecutionService` → `ITaskExecutor` (2 метода);
        //  - `PersonaAutomationService` → `IPersonaAutomationRunner` (1 метод);
        //  - `IProjectEventLogService` — уже в Core с волны 1 Skills;
        //  - `IHubContext<SessionHub>` → `ISessionBroadcaster` (волна 4C, Ф4);
        //  - `IUserStore`/`IProjectManager`/`IConfiguration` — Core-примитивы;
        //  - `Protocol.NotificationMessage` — в Core, ловится IsCoreAssembly;
        //  - `ModelTiers`/`ModelTier`/`ExecutorStopClassifier` — переехали в Core
        //    в этой же волне (`Models`-слой и `Services`-слой примитивов спины).
        // Префиксы-швы (после выноса):
        // 1) `ClaudeHomeServer.Services.Llm` — `ICheapTextRunner` для `TaskAiService`
        //    (генерация описания/подзадач/классификация/нормализация/дедуп через
        //    `LocalActionCatalog.TaskAi/TaskClassify/...`). Префикс-шов, как у
        //    `Git`/`Backgrounds`/`Deploy`/`Changelog`/`ProjectIcons`/`Spend`.
        // 2) `ClaudeHomeServer.Hubs` — больше не нужен: фактических ссылок на
        //    `IHubContext<SessionHub>` в Tasks нет (комментарий прежний, наследие Ф4;
        //    чистый — `ISessionBroadcaster` (Core) дёргается из TasksScheduler через
        //    `IHubContext` теперь неявно через Core-шов).
        // Зафиксированные швы (IL-скан видит declaring-типы):
        // - `Tasks.TaskManager` ставит три статических резолвера на `Models.Session`
        //   (`Session.TaskSourceSessionResolver`/`TaskDelegationDepthResolver`/
        //   `TaskDoneResolver`) в конструкторе TaskManager.cs. Связь Tasks →
        //   Models видна через `ClaudeHomeServer.Models` (SharedAllowedPrefixes);
        //   мутация статической модели — фиксированное исключение.
        // Мёртвые допуски сняты ревью (2026-09-09): формально сторож оставался зелёным
        // и с ними, но был ШИРЕ реальной поверхности зависимостей Tasks после выноса —
        // иначе вертикаль могла бы прикинуться «спиной» в обход швов. Та же ловушка,
        // что поймана на Notes после её выноса.
        // Сюда же — снятый префикс `ClaudeHomeServer.Services.Llm`: он стоял ради
        // `ICheapTextRunner` и `LocalActionCatalog`, но оба типа давно переехали в
        // Core (`Core/Services/Llm/`), и Tasks берёт их ОТТУДА. Мутация подтверждает:
        // без допуска сторож зелёный. ТАКОЙ ЖЕ мёртвый допуск остался ещё у пяти
        // вертикалей (Backgrounds, ProjectIcons, Memory, Docs, Changelog) — снимается
        // отдельной уборкой, чтобы не смешивать её с выносом Tasks.
        new object[]
        {
            new VerticalBoundary(
                "Tasks",
                "ClaudeHomeServer.Services.Tasks",
                SharedAllowedPrefixes
                    .Concat(new[]
                    {
                        "ClaudeHomeServer.Services.Tasks",
                    })
                    .ToArray(),
                Array.Empty<string>()),
        },
        // Notes — вертикаль заметок (волна 4C, шаг 2). Obsidian-совместимый vault
        // (`[[wikilinks]]`/backlinks/граф/комментарии), AI-сводки, синк с Dify,
        // мост чекбоксов заметок ↔ задач (`NoteTaskSyncService`), авто-истечение
        // заметок (`NoteExpiryService`). `UnifiedSearchService` остаётся в корне —
        // он фасад поперёк Notes+Task, отдельная задача на разрез.
        // **Допусков нет вовсе — ни префиксов, ни точечных типов.** Пустой
        // `AllowedExactNamespaces` здесь не забытая строка, а результат Этапа 5: после
        // выноса в `ClaudeHomeServer.Notes.csproj` вертикаль физически не может сослаться
        // на Main (нет `ProjectReference`), и весь прежний список швов закрыт контрактами
        // из Core, которые проходят более ранний чек `IsCoreAssembly`:
        //  - `TaskManager` → `INoteTaskBridge`;
        //  - `KnowledgeService` → `IKnowledgeIndex` (6 методов по факту вызовов);
        //  - `ICheapTextRunner` — уже в Core с волны 1 Skills;
        //  - `IHubContext<SessionHub>` и `Protocol.NotesChangedMessage` →
        //    `INotesHubNotifier` (первый в проекте шов Hub-рассылки для вынесенной
        //    вертикали; по этому образцу пойдут Team и Desktop);
        //  - `ProjectManager` → `IProjectManager` (3 метода), `UserStore` → `IUserStore`,
        //    `ProjectEventLogService` → `IProjectEventLogService`;
        //  - `FileService.SafeJoinPublic` → Core-примитив `SafePath.Join`;
        //  - инверсия `Notes → Controllers.TaskHubExtensions` снята переносом статики
        //    в `Hubs/` (волна C), допуск больше не нужен.
        // Мёртвые допуски снял ревьюер (2026-09-08): формально сторож оставался зелёным
        // и с ними, но был ШИРЕ реальной поверхности зависимостей — то есть пропустил бы
        // повторную прямую связь Notes с `TaskManager`/`FileService`/`Hubs` в обход
        // интерфейсов. Ровно та декоративность, ради проверки которой волна и делалась.
        new object[]
        {
            new VerticalBoundary(
                "Notes",
                "ClaudeHomeServer.Services.Notes",
                SharedAllowedPrefixes
                    .Concat(new[]
                    {
                        "ClaudeHomeServer.Services.Notes",
                    })
                    .ToArray(),
                Array.Empty<string>()),
        },
        // Skills — вертикаль навыков (волна 4C, шаг 3): чтение скиллов и агентов из
        // глобального (~/.claude/skills, ~/.claude/workflows, ~/.claude/plugins) и
        // проектного (.claude/skills, .claude/agents) каталога; обёртка CLI «npx skills»
        // для поиска/установки из реестра skills.sh; LLM-подбор под персону/проект/запрос;
        // LLM-генерация тела нового навыка; перевод описаний RU→EN; фоновый перевод
        // плагиновых описаний с персистентным кешем.
        // Префиксов-швов к чужому `ClaudeHomeServer.Services.*` после Этапа 3 (волна 2) нет:
        //  - `LocalActionCatalog` (SkillSuggest/SkillTranslate/SkillGenerate) переехал
        //    в Core вместе с `ICheapTextRunner` — оба ловятся assembly-фильтром
        //    `IsCoreAssembly`, отдельного допуска в allow-list не требуется.
        //  - `IPersonaSkillBindingLookup`/`IProjectSummaryLookup` (Core) — узкие швы
        //    вместо прямых ссылок на `PersonaManager`/`ProjectManager`.
        //  - `Execution` (`ILauncherFactory`/`IProcessLauncher`/`ProcessSpec`) — интерфейсы
        //    в Core (assembly-фильтр), реализации (`LauncherFactory`/`LocalProcessRunner`/
        //    `DockerProcessRunner`) Skills не нужны: `SkillsCliService` зовёт только
        //    интерфейсы.
        // Точечных допусков к корню `Services.*` нет:
        //  - `PersonaManager` снят: `SkillSuggestService` берёт персону через
        //    `IPersonaSkillBindingLookup` (Core, assembly-фильтр).
        //  - `ProjectManager` снят: `SkillSuggestService` берёт проект через
        //    `IProjectSummaryLookup` (Core, assembly-фильтр).
        //  - `Execution` (`ILauncherFactory`/`IProcessLauncher`/`ProcessSpec`) —
        //    интерфейсы переехали в Core (assembly-фильтр), а реализации (`LauncherFactory`/
        //    `LocalProcessRunner`/`DockerProcessRunner`) Skills не нужны.
        //    `SkillsCliService` зовёт только интерфейсы.
        // `SkillsController` лежит в `ClaudeHomeServer.Controllers`, не в этой вертикали;
        // под сторож не попадает.
        new object[]
        {
            new VerticalBoundary(
                "Skills",
                "ClaudeHomeServer.Services.Skills",
                SharedAllowedPrefixes
                    .Concat(new[]
                    {
                        "ClaudeHomeServer.Services.Skills",
                    })
                    .ToArray(),
                Array.Empty<string>()),
        },
        // Git — вертикаль локальных git-операций над проектом (запуск через
        // ILauncherFactory) + Forgejo HTTP-клиент для remote + LLM-помощь для
        // сообщений коммита и имён stash (GitAiService через LocalActionCatalog +
        // ICheapTextRunner, оба в Core после Этапа 3, ловятся assembly-фильтром).
        // Допусков к корню Services нет: вся зависимость от спинки закрыта Core:
        //  - Models.GitStatus (Core, assembly-фильтр);
        //  - Services.Execution.ILauncherFactory/ProcessSpec (Core, assembly-фильтр);
        //  - Services.Composition.IAppSubsystem (Core, assembly-фильтр);
        //  - Services.Http.WithoutEgressProxy (Core, assembly-фильтр).
        // Реакторы GitAutoCommitService/CommitAttributionService лежат в корне
        // Services и регистрируются в Program.cs — это сознательная связь
        // «вертикаль → спинка», Main->Git направление, сторож по Git не
        // запрещает Main ссылаться на Git.
        //
        // `AllowedExactNamespaces` пуст СОЗНАТЕЛЬНО, это не забытая строка (перенесено
        // из прежней записи, снятой при выносе в отдельный .csproj): префикс
        // `ClaudeHomeServer.Protocol` был убран ещё волной 3 как избыточный —
        // `GitTurnCommitMessage`/`GitStatusChangedMessage` создаются в
        // `Services.GitAutoCommitService` (корень Services, не в вертикали), а
        // `OnSessionMessageAsync` с `ServerMessage` в сигнатуре — private, сторож
        // читает только public-члены. Нулевой allow-list здесь работает как ловушка:
        // получит вертикаль поле или параметр типа из Protocol — сторож поймает сразу.
        new object[]
        {
            new VerticalBoundary(
                "Git",
                "ClaudeHomeServer.Services.Git",
                SharedAllowedPrefixes
                    .Concat(new[]
                    {
                        "ClaudeHomeServer.Services.Git",
                    })
                    .ToArray(),
                Array.Empty<string>()),
        },
        // Power — питание машины из веб-морды (выключить/перезагрузить/усылать). Вертикаль
        // без IAppSubsystem (как Watchdog): регистрации в Program.cs хоста, сервисы живут
        // в Main (`backend/ClaudeHomeServer/Services/Power/`). Независимых рёбер нет:
        // типы вертикали используют только `ClaudeHomeServer.Models` (опции в Core) и BCL,
        // обе записи уже в SharedAllowedPrefixes — allow-list дефолтный (спинка + сама
        // вертикаль), точные допуски пустые.
        new object[]
        {
            new VerticalBoundary(
                "Power",
                "ClaudeHomeServer.Services.Power",
                SharedAllowedPrefixes
                    .Concat(new[] { "ClaudeHomeServer.Services.Power" })
                    .ToArray(),
                Array.Empty<string>()),
        },
        // RemoteCommands — пульт удалённых команд (запуск/остановка объявленных в конфиге
        // действий на машине сервера). Микро-вертикаль без IAppSubsystem, как Power:
        // регистрации в Program.cs хоста, сервисы живут в Main
        // (`backend/ClaudeHomeServer/Services/RemoteCommands/`). Независимых рёбер нет:
        // опции — `ClaudeHomeServer.Models` (Core), буфер вывода — `OutputRingBuffer` и
        // формула шелла — `ShellCommandLine`, оба в Core.dll (допуск по сборке). Готовый
        // `WatchdogCommandRunner` сознательно НЕ переиспользован: он исполняет команду в
        // среде владельца (вплоть до контейнера), пульту нужен всегда хост, а ссылка
        // «вертикаль → вертикаль» тут же покраснела бы у этого сторожа.
        new object[]
        {
            new VerticalBoundary(
                "RemoteCommands",
                "ClaudeHomeServer.Services.RemoteCommands",
                SharedAllowedPrefixes
                    .Concat(new[] { "ClaudeHomeServer.Services.RemoteCommands" })
                    .ToArray(),
                Array.Empty<string>()),
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
        // 20 = Main + Core + 18 вынесенных (Video/Yandex/Reader/CodeGraph/Skills/Git/Tts/Notes
        // — Этап 3 и вынос Notes; Personas/Diagnostics/WebSearch/Changelog/Docs/Modules
        // — Этап 5, волны A и C; ProjectServices/Backgrounds/ProjectIcons/Terminal —
        // Этап 5, волна C, шаг 2); при добавлении новых `.csproj` подсистем обновить.
        assemblies.Should().HaveCountGreaterThanOrEqualTo(20,
            "сторож должен видеть 20 прод-сборок: ClaudeHomeServer, " +
            "ClaudeHomeServer.Core, ClaudeHomeServer.Video, ClaudeHomeServer.Yandex, " +
            "ClaudeHomeServer.Reader, ClaudeHomeServer.CodeGraph, ClaudeHomeServer.Skills, " +
            "ClaudeHomeServer.Git, ClaudeHomeServer.Tts, ClaudeHomeServer.Notes, " +
            "ClaudeHomeServer.Personas, ClaudeHomeServer.Diagnostics, ClaudeHomeServer.WebSearch, " +
            "ClaudeHomeServer.Changelog, ClaudeHomeServer.Docs, ClaudeHomeServer.Modules, " +
            "ClaudeHomeServer.ProjectServices, ClaudeHomeServer.Backgrounds, " +
            "ClaudeHomeServer.ProjectIcons, ClaudeHomeServer.Terminal");
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

            foreach (var referenced in BoundaryIlScanner.CollectAllReferencedTypes(type))
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
    /// Корневого <c>ClaudeHomeServer.Services</c> здесь НЕТ: под ним в Main живут
    /// вертикали, и широкой записи namespace (48+ типов) было достаточно, чтобы
    /// спрятать вертикаль в спинке (ревью 894e3ec9 доказало мутацией). Все типы
    /// корневого namespace Core перечислены поимённо в <see cref="CoreAllowedRootTypes"/>.
    /// </remarks>
    private static readonly string[] CoreAllowedNamespaces =
    [
        "ClaudeHomeServer.Models",
        // Этап 5, ярус 0 (Ф3б): WS-контракт с фронтом едет в Core как спина
        // (задача `b6f0e1f4`, ADR-014 §«Решение по Protocol») — record-DTO
        // дискриминируются по `type` в едином потоке `ServerMessage`, отдельная
        // сборка не нужна.
        "ClaudeHomeServer.Protocol",
        "ClaudeHomeServer.Services.Composition",
        "ClaudeHomeServer.Services.Http",
        "ClaudeHomeServer.Services.Mcp",
        // Этап 5, волна 5б (Llm): LoopbackProxyBypass — env-утилита спины, переехала из
        // Services/Mcp/Http вертикали. Никаких чужих using (только BCL), один call-site
        // (ClaudeSession), и лежит по теме, а не по слою. namespace сохранён ради
        // call-site и обоих тестов.
        // Волна NotesToolset: под тем же namespace живут узкие швы контекста вызова
        // `McpCallContextSeams` (IMcpSessionAccessor/IMcpPersonaBindings) — они
        // форвардят в god-объекты Main (SessionManager/PersonaBindingsService), и
        // реализации-адапторы лежат рядом в PromptSeamAdapters.
        "ClaudeHomeServer.Services.Mcp.Http",
        // Этап 3, волна 1 (Skills): ICheapTextRunner/OneShotResult/OneShotUsage/
        // LlmTimeoutException переехали в Core, чтобы вертикали (Skills, Git, Notes, Tasks)
        // могли зависеть от шва без ProjectReference на Main.
        "ClaudeHomeServer.Services.Llm",
        // Этап 5, финал линии Llm: TranscriptProbe — stateless-примитив спины (117 строк,
        // только BCL + `TranscriptRoots`), нужный обеим сторонам границы: вертикали
        // (ClaudeSession/MainTranscriptTailer/WorkflowAgentParser) и спине (SessionManager).
        // Переехал в Core целиком, а не через шов: статической функции без состояния
        // интерфейс не нужен (тот же довод, что у SafePath/CmdlineEstimate). namespace
        // сохранён ради неизменных call-site'ов с обеих сторон — прецедент
        // `Services.Mcp.Http` (LoopbackProxyBypass, волна 5б).
        "ClaudeHomeServer.Services.Llm.Claude",
        // Этап 3, волна 1 (Skills): ILauncherFactory/IProcessLauncher/ProcessSpec/IPathMapper
        // переехали в Core — общие контракты запуска процессов для всех вертикалей.
        "ClaudeHomeServer.Services.Execution",
        // Этап 5, волна A (Notes): IKnowledgeSyncParticipant/KnowledgeSyncTarget в Core —
        // контракт синка с Dify, который реализуют ЧЕТЫРЕ вертикали (Notes, Memory,
        // Dossiers, ProjectKnowledgeSync). Он же снял прямые ссылки из
        // UserKnowledgeCascade на конкретные типы этих вертикалей: каскад теперь берёт
        // IEnumerable<IKnowledgeSyncParticipant>. Сам KnowledgeService в Core НЕ едет —
        // он тянет WorkspaceKnowledgeStore, законную внутреннюю композицию своей
        // вертикали; потребителям вместо него узкий контракт (волна B).
        "ClaudeHomeServer.Services.Knowledge",
        // Этап 5, шаг Ф1 (Models→Core): контракт записи долгой памяти нужен обоим
        // стекам (Persona и Team), оба реализатора лежат в Models/ — без переноса
        // интерфейса в Core `Models/` целиком не уезжает.
        "ClaudeHomeServer.Services.Memory",
        // Этап 5 (Turn): OnboardingPrompts переехал в Core, потому что Turn
        // (PersonaLayerContributor) ссылается на него в слое персоны. Сам класс
        // — stateless-промпт-материал (как Slugifier/PathNormalizer), но целиком
        // под `Prompts/OnboardingPrompts.cs` — не один файл-примитив. Чтобы
        // не раздувать CoreAllowedRootTypes, разрешаем namespace.
        "ClaudeHomeServer.Services.Prompts",
        // Этап 5, узкие швы Turn: DossierRecallRequest/DossierRecallResult — контрактные
        // DTO пассивного recall паспортов. Запрос собирает Turn (контрибьютор промпта),
        // исполняет Memory (PersonaMemoryService.BuildRecallAsync), владеет Dossiers.
        // Держать форму данных внутри вертикали значило бы ссылку на Dossiers у двух
        // посторонних слоёв ради типа, а не ради поведения. Поведение (DossierRecallService)
        // осталось в вертикали и в Core НЕ едет.
        "ClaudeHomeServer.Services.Dossiers",
        // Этап 5, шаг 6 (инверсия контрибьюторов промпта): IPromptSectionContributor +
        // PromptSection + PromptSectionContribution + PromptSessionContext +
        // extension для DI — контракт шины TurnEventBus, реализации едут в чужих
        // вертикалях (CodeGraph, Notes). Прецедент IKnowledgeSyncParticipant: тот же
        // приём — контракт в Core, реализации по вертикалям, реестр через IEnumerable<>.
        "ClaudeHomeServer.Services.Turn",
        // Этап 5, волна D (Notes): INoteTaskBridge + узкие Core-типы (NoteTaskRef/
        // NoteTaskCreateRequest/NoteTaskUpdateRequest/NoteTaskStatus/NoteTaskKind/
        // NoteTaskRecurrence) — мост Notes → Tasks. Нужны Core, чтобы Notes
        // ссылалась на шов без ProjectReference на Main.
        "ClaudeHomeServer.Services.Notes",
        // Этап 5, шаг 1 (цикл Llm ⇄ Spend): ISpendCollector переехал в Core, чтобы
        // вертикаль Llm могла зависеть от Core-интерфейса без прямой ссылки на
        // вертикаль Spend. Реализация `SpendStore : ISpendCollector` остаётся
        // в Main (Services/Spend) — пока сам Spend не вынесен в свой csproj.
        "ClaudeHomeServer.Services.Spend",
        // Этап 5, узкие швы Skills (Llm → Skills): ICommandExpansion (разворот
        // /skill в тексте хода) и ISkillSnapshotSource (каталог CliSkillDto для
        // снимка промпта) переехали в Core — те же прецеденты, что IAgentPromptSource
        // выше и ISpendCollector: Llm берёт шов без ProjectReference на вертикаль Skills.
        // Реализации — CommandExpansionAdapter/SkillSnapshotSourceAdapter в Main,
        // форвардят в SkillsService. Узкие: 1 метод + 1 метод.
        "ClaudeHomeServer.Services.Skills",
        // Этап 5, волна 2 (Memory↔Dossiers↔Git): IGitRefSnapshotStore + 5 record-типов
        // (GitRefIdentity/GitRefTip/GitRefSnapshotResult/GitCredentials/GitSnapshotFile)
        // переехали из вертикали Git в Core — узкий контракт generic plumbing ветки-паспорта,
        // по которому несколько потребителей могут иметь свою ветку (Dossiers и в будущем —
        // другие «ветки-паспорта»). Без переноса контракт жил бы ВНУТРИ вертикали Git и любая
        // вертикаль-потребитель получала бы запрещённую сторожем границ связь. Дополнительно —
        // IGitCommitInspector (5 методов инспекции коммитов, один потребитель Dossiers) и
        // GitRepo.IsRepo (статика, примитив спины по образцу SafePath.Join). Реализация
        // контракта — GitService в вынесенной вертикали Git; форвардеры (GitCommitInspector)
        // живут в Main как тонкие прокладки.
        "ClaudeHomeServer.Services.Git",
        // Этап 5, волна 2 (Dossiers↔CodeGraph): ICodeGraphInspector + 2 record-типа
        // (CodeGraphSnapshot/CodeGraphNode) — узкий шов инспекции графа кода для Dossiers
        // (якоря FQN + сигнатура кеша статусов). CodeGraph — вынесенная вертикаль; без
        // переноса контракт в Core Dossiers получал бы запрещённую сторожем границ связь.
        "ClaudeHomeServer.Services.CodeGraph",
        // Этап 5, волна 5 (Knowledge): узкий Core-шов IDifyMetrics (ProjectKnowledgeSyncService
        // больше не ссылается на ServerMetrics/Main напрямую) + DifyErrorCategorizer
        // (43 строки чистой функции, нужны и Knowledge, и Memory, обе вертикали).
        "ClaudeHomeServer.Core.Telemetry",
    ];

    /// <summary>
    /// Единственные типы, которым позволено лежать в Core прямо в корневом
    /// <c>ClaudeHomeServer.Services</c>. Список закрытый и поимённый: тип, добавленный
    /// сюда без осознанного решения — дефект. Широкая запись namespace (ранее
    /// <c>"ClaudeHomeServer.Services"</c> в <see cref="CoreAllowedNamespaces"/>) удалена:
    /// она открывала ВСЕ типы корневого namespace разом (48+), и тип вертикали,
    /// положенный в Core «чтобы собиралось», проходил сторожем молча (ревью 2026-09-10).
    /// </summary>
    private static readonly string[] CoreAllowedRootTypes =
    [
        // ── Stateful / utility classes (spine primitives and infrastructure) ──
        "ClaudeHomeServer.Services.OutputRingBuffer",
        "ClaudeHomeServer.Services.JsonFileStore",
        "ClaudeHomeServer.Services.SsrfGuard",
        "ClaudeHomeServer.Services.RuleRuntimeState",
        "ClaudeHomeServer.Services.ModelTiers",
        "ClaudeHomeServer.Services.SpecialtyDefaultBinding",
        // ── Static spine primitives (stateless helpers, no vertical ownership) ──
        "ClaudeHomeServer.Services.SafePath",
        // Формула запуска строки в системном шелле (cmd /s /c сырой строкой / bash -lc):
        // общая у сторожей чатов и пульта удалённых команд, дубль тут однажды стоил прода
        "ClaudeHomeServer.Services.ShellCommandLine",
        "ClaudeHomeServer.Services.TreeExcludes",
        "ClaudeHomeServer.Services.Slugifier",
        "ClaudeHomeServer.Services.PathNormalizer",
        "ClaudeHomeServer.Services.ExecutableResolver",
        "ClaudeHomeServer.Services.PermissionModeGuard",
        "ClaudeHomeServer.Services.TeamProtocolMarkers",
        "ClaudeHomeServer.Services.ModelTier",
        "ClaudeHomeServer.Services.TranscriptRoots",
        "ClaudeHomeServer.Services.TextPathMentions",
        "ClaudeHomeServer.Services.AttachmentsGitExclude",
        "ClaudeHomeServer.Services.SnapshotIdGenerator",
        "ClaudeHomeServer.Services.McpEndpoints",
        "ClaudeHomeServer.Services.TaskUrl",
        "ClaudeHomeServer.Services.ImageAssetHelper",
        "ClaudeHomeServer.Services.InstanceSecretFiles",
        "ClaudeHomeServer.Services.SessionChangedPaths",
        "ClaudeHomeServer.Services.SessionIdGuard",
        "ClaudeHomeServer.Services.SessionTranscript",
        "ClaudeHomeServer.Services.ExecutorStopClassifier",
        "ClaudeHomeServer.Services.PersonaLabel",
        "ClaudeHomeServer.Services.PersonaConsultantToolset",
        "ClaudeHomeServer.Services.SpecialtyCatalog",
        "ClaudeHomeServer.Services.SpecialtyPromptPresets",
        "ClaudeHomeServer.Services.SpecialtyTemplate",
        // ── Shared contracts (interfaces crossing vertical boundaries) ──
        "ClaudeHomeServer.Services.IChatHistoryLoader",
        "ClaudeHomeServer.Services.IDailyBriefingRunner",
        "ClaudeHomeServer.Services.IDataBackupService",
        "ClaudeHomeServer.Services.IKnowledgeHubNotifier",
        "ClaudeHomeServer.Services.IModelCatalog",
        "ClaudeHomeServer.Services.IPersonaAutomationRunner",
        "ClaudeHomeServer.Services.IPersonaAvatarStore",
        "ClaudeHomeServer.Services.IPersonaLookup",
        "ClaudeHomeServer.Services.IPersonaResolver",
        "ClaudeHomeServer.Services.IProjectBackgroundWriter",
        "ClaudeHomeServer.Services.IProjectEventLogService",
        "ClaudeHomeServer.Services.IProjectIconMigrator",
        "ClaudeHomeServer.Services.IProjectManager",
        "ClaudeHomeServer.Services.ISessionDirectory",
        "ClaudeHomeServer.Services.ITaskExecutor",
        "ClaudeHomeServer.Services.ITaskLookup",
        "ClaudeHomeServer.Services.ITaskNotificationDispatcher",
        "ClaudeHomeServer.Services.ITierModelResolver",
        "ClaudeHomeServer.Services.IUserStore",
        "ClaudeHomeServer.Services.IWorkspaceDatasetLookup",
        // ── DTOs (records shared across verticals) ──
        "ClaudeHomeServer.Services.DataBackupResult",
        "ClaudeHomeServer.Services.ModelCatalogEntry",
        "ClaudeHomeServer.Services.WorkspaceDatasetInfo",
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