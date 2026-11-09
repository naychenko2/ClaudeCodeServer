using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Llm;

namespace ClaudeHomeServer.Services.Sessions;

// Встроенные продуктовые MCP-серверы хода (S3-4, docs/research/session-manager-split-2026-11.md):
// контексты tasks/notes/widgets/watch/websearch/architecture/higgsfield/image-editor/audio-editor/
// video-editor/plugins/turn-context/local-media/tests/dify/codegraph/memory/team-memory и их
// гейты (возможность персоны, наличие тулсета в реестре, подсистема, форма адреса).
// Тела перенесены из SessionManager дословно; фасад держит делегирующие обёртки прежних
// сигнатур — на них сидят точки сборки контекста (StartNewSessionAsync, EnsureProcessCoreAsync)
// и внешние тулсеты.
//
// Состав решений не зависит от хода (инвариант ADR-012, сторож McpToolsetStabilityTests):
// каждый гейт читает свойства владельца/сессии/процессы/персоны.
//
// Зависимостей восемь: endpoint, flags, bindings, IServiceProvider (ленивый резолв
// McpToolsetRegistry и IDeviceExecChannel — прямой замкнул бы граф синглтонов; allow-list
// правила 5 сторожа формы ядра), SubsystemStateStore, projects, HiggsfieldOAuthService, config.
// В таблице S3-0 §2 у этой роли десять: launchers и users из неё не нужны — override адреса
// среды владельца читает McpEndpoint (строка 757 исходника), а users нужен плану workspace
// и модулям (ExternalMcpContexts / ModulesMcpContexts).
// Класс public ТОЛЬКО потому, что он параметр публичного конструктора SessionManager (CS0051) и DI нужен
// публичный конструктор; обход фасада закрывает сторож формы ядра (правило 4: на типы Services.Sessions
// ссылаются лишь SessionManager, сами Services.Sessions и тесты).
public sealed class BuiltinMcpContexts
{
    private readonly McpEndpoint _endpoint;
    private readonly FeatureFlagService _flags;
    private readonly PersonaBindingsService _bindings;
    private readonly IServiceProvider? _services;
    private readonly SubsystemStateStore? _subsystemStates;
    private readonly ProjectManager _projects;
    private readonly Mcp.HiggsfieldOAuthService? _higgsfieldOAuth;
    private readonly IConfiguration _config;

    private Services.Mcp.Http.McpToolsetRegistry? _mcpToolsets;

    // Секция Dify (ApiUrl/ApiKey/неймспейс) — для BuildDifyContext (волна 4): единственное
    // потребление тут — проверка настроенности и строки stdio-ветки отката; вся работа с
    // Dify — в KnowledgeService со своей копией IOptions
    private readonly ClaudeHomeServer.Models.DifyOptions _dify = new();

    public BuiltinMcpContexts(McpEndpoint endpoint, FeatureFlagService flags,
        PersonaBindingsService bindings, IServiceProvider? services,
        SubsystemStateStore? subsystemStates, ProjectManager projects,
        Mcp.HiggsfieldOAuthService? higgsfieldOAuth, IConfiguration config)
    {
        _endpoint = endpoint;
        _flags = flags;
        _bindings = bindings;
        _services = services;
        _subsystemStates = subsystemStates;
        _projects = projects;
        _higgsfieldOAuth = higgsfieldOAuth;
        _config = config;
        config.GetSection(ClaudeHomeServer.Models.DifyOptions.Section).Bind(_dify);
    }

    // --- MCP tasks-server ---

    // tasks-MCP доступен, когда разрешён персоной (Persona.Tools/привязки), ЛИБО сессия
    // является исполнителем задачи — тогда tasks-MCP форсируется: исполнитель обязан
    // управлять задачей через mcp__tasks__* (иначе ограниченная персона не сможет её
    // ни прочитать, ни завершить и свалится в нерабочий встроенный Task-тул).
    // internal — ту же формулу резолвит TasksToolset по живой сессии-вызывателю (http,
    // ADR-012 волна 2): право на сервер проверяется на каждый tools/list и tools/call
    internal bool TasksMcpEnabled(string? ownerId, Session session, Persona? persona) =>
        session.TaskExecution || _bindings.EffectiveToolEnabled(ownerId, persona, "tasks");

    // Контекст MCP-сервера задач для сессии; null — только для чата без владельца.
    // persona — для кросс-проектных ProjectTasks-привязок (доступ к задачам ДРУГИХ проектов).
    // Токен — фабрикой (ADR-012 волна 2, как widgets/memory): захваченный строкой JWT у
    // долгоживущего чата истекал, и задачи пропадали у модели молча.
    internal TasksMcpContext? BuildTasksContext(string? ownerId, string? projectId, Persona? persona = null)
    {
        if (ownerId is null) return null;
        var extraScopes = _bindings.BuildExternalTaskScopes(ownerId, persona);
        var extraIds = extraScopes.Select(s => s.ProjectId).Distinct().ToList();
        var extraReadOnly = extraScopes.Where(s => s.ReadOnly).Select(s => s.ProjectId).Distinct().ToList();
        var apiUrl = _endpoint.ResolveTasksApiUrl(ownerId);
        return new TasksMcpContext(apiUrl, () => _endpoint.GetServiceToken(ownerId), projectId,
            extraIds.Count > 0 ? extraIds : null, extraReadOnly.Count > 0 ? extraReadOnly : null,
            UseHttp: _endpoint.HttpEndpointUsable(apiUrl));
    }

    // Решение «даём ли персоне сервер заметок» — близнец TasksMcpEnabled, только без
    // форсирования по роли сессии: заметок у исполнителя задачи нет по замыслу. Формула
    // живёт здесь, а не в сборщике контекста запуска: у TurnLaunchContextFactory нет права
    // на PersonaBindingsService напрямую (S3-5, план §2 «зависимости ровно 10»).
    internal bool NotesMcpEnabled(string? ownerId, Persona? persona) =>
        _bindings.EffectiveToolEnabled(ownerId, persona, "notes");

    // Контекст MCP-сервера заметок; null — только для чата без владельца.
    // Модуль комментариев к документам и редких операций — за ключом notes-annotations
    // (дефолт выключен, решение ПО ПЕРСОНЕ: PersonaBindingsService.SectionEnabled).
    internal NotesMcpContext? BuildNotesContext(string? ownerId, string? projectId, Persona? persona)
    {
        if (ownerId is null) return null;
        var apiUrl = _endpoint.ResolveTasksApiUrl(ownerId);
        return new NotesMcpContext(apiUrl, () => _endpoint.GetServiceToken(ownerId), projectId,
            AnnotationsEnabled: _bindings.SectionEnabled(ownerId, persona, "notes-annotations"),
            UseHttp: _endpoint.HttpEndpointUsable(apiUrl));
    }

    // Контекст MCP-сервера виджетов чата: адрес API и сервисный токен владельца — сервер
    // переехал в Kestrel (ADR-012), ход зовёт его по http, а не поднимает процесс node.
    // Адрес даёт та же ResolveTasksApiUrl, что и остальным серверам: второго правила про
    // песочницу здесь нет — оно уже внутри резолвера.
    // Фича штатная (без фич-флага), как personas/notifications; персона может выключить
    // сервер Off-привязкой tool:widgets (PersonaBindingsService.ServerToolEnabled).
    internal WidgetsMcpContext? BuildWidgetsContext(string? ownerId, Persona? persona)
    {
        if (ownerId is null || !_bindings.ServerToolEnabled(ownerId, persona, "widgets")) return null;
        var apiUrl = _endpoint.ResolveTasksApiUrl(ownerId);
        // Токен — фабрикой, как у памяти: контекст живёт столько же, сколько адаптер, а
        // захваченный строкой JWT у долгоживущего чата истекал (ADR-012, урок фазы 1)
        return new WidgetsMcpContext(apiUrl, () => _endpoint.GetServiceToken(ownerId), _endpoint.HttpEndpointUsable(apiUrl));
    }

    // Контекст MCP-сервера сторожей чатов: null — только для чата без владельца.
    // Сессия-вызыватель едет хвостом URL — как у tasks/notes волны 2.
    internal WatchMcpContext? BuildWatchContext(string? ownerId)
    {
        if (ownerId is null) return null;
        var apiUrl = _endpoint.ResolveTasksApiUrl(ownerId);
        return new WatchMcpContext(apiUrl, () => _endpoint.GetServiceToken(ownerId!), _endpoint.HttpEndpointUsable(apiUrl));
    }

    // Контекст MCP-сервера веб-поиска: null — чат без владельца ИЛИ пустой Perplexity:ApiKey
    // (единственный рубильник фичи, как у Dify). Ключ читается ЖИВЬЁМ, но остаётся свойством
    // ИНСТАНСА, а не хода: инвариант стабильности состава (ADR-012) не задет — правка ключа
    // штатно меняет сигнатуру запуска и перезапускает CLI, как любое изменение shapes.
    // Сам ключ в контекст не кладётся: наружу он не уезжает, тулсет берёт его из конфига.
    //
    // Третье условие — возможность персоны «web» (EffectiveToolEnabled, та же, что гасит
    // встроенные WebSearch/WebFetch у CLI): персона с выключенным веб-поиском не должна
    // получать обходной путь в интернет через MCP. Гейт стоит на объявлении сервера, а не
    // только в запретах: так у такой персоны схемы ещё и не занимают окно. Персона — свойство
    // сессии, инвариант стабильности состава не задет (как у widgets).
    internal WebSearchMcpContext? BuildWebSearchContext(string? ownerId, Persona? persona)
    {
        if (ownerId is null) return null;
        if (string.IsNullOrWhiteSpace(_config["Perplexity:ApiKey"])) return null;
        if (!_bindings.EffectiveToolEnabled(ownerId, persona, "web")) return null;
        var apiUrl = _endpoint.ResolveTasksApiUrl(ownerId);
        return new WebSearchMcpContext(apiUrl, () => _endpoint.GetServiceToken(ownerId!), _endpoint.HttpEndpointUsable(apiUrl));
    }

    // Контекст MCP-сервера архитектуры (arch_*: C4-модель проекта). Все оси — свойства
    // владельца/сессии/процесса, не хода (инвариант стабильности состава ADR-012): чат
    // проекта, модуль Architecture реально загружен (динамическая сборка, Viaduct 10.2: без
    // dll тулсета нет в реестре, и ход объявил бы CLI мёртвый сервер — урок форвардера
    // Knowledge у Notes) и не выключен вторым замком Subsystems:architecture:Enabled, плюс
    // Off-привязка персоны tool:architecture. Фич-флага у раздела нет (решение 2026-09-26):
    // включение — только конфигом модуля. Тулсет перепроверяет то же на каждом вызове.
    internal ArchitectureMcpContext? BuildArchitectureContext(string? ownerId, string? projectId, Persona? persona)
    {
        if (ownerId is null || string.IsNullOrEmpty(projectId)) return null;
        // Локальный проект (ADR-016): модели и графа кода на сервере нет — тулсету не с чем работать
        if (_projects.GetById(projectId) is not { } project || !ProjectCapabilities.FilesOnServer(project)) return null;
        if (_subsystemStates?.ActiveKeys().Contains(McpEndpoints.ArchitectureName, StringComparer.OrdinalIgnoreCase) != true) return null;
        if (!Composition.SubsystemGate.IsEnabled(_config, McpEndpoints.ArchitectureName)) return null;
        if (!_bindings.ServerToolEnabled(ownerId, persona, "architecture")) return null;
        var apiUrl = _endpoint.ResolveTasksApiUrl(ownerId);
        return new ArchitectureMcpContext(apiUrl, () => _endpoint.GetServiceToken(ownerId), _endpoint.HttpEndpointUsable(apiUrl));
    }

    // MCP-сервер Higgsfield: инстансное OAuth-подключение (единый вход админа, шарится
    // всеми владельцами). Узел присутствует в конфиге хода только когда:
    //   1) инстанс подключён (EnsureFresh() ≠ null) — иначе прокси некому ретранслировать
    //   2) персона НЕ ReadOnly — RO-гейт (аналог «инстанс не подключён → узла нет»)
    // URL — тот же Kestrel, что и websearch: /mcp/higgsfield/{sessionId}.
    // Токен — сервисный JWT владельца (Bearer), как у websearch/dify/wsp.
    internal HiggsfieldMcpContext? BuildHiggsfieldContext(string? ownerId, Persona? persona)
    {
        if (ownerId is null) return null;
        if (_higgsfieldOAuth is null) return null;
        // RO-гейт: ReadOnly-персона не получает higgsfield (аналог «инстанс не подключён»)
        if (persona is { Access: PersonaAccess.ReadOnly }) return null;
        // Инстанс подключён: EnsureFresh() ≠ null
        if (_higgsfieldOAuth.EnsureFresh() is null) return null;
        var apiUrl = _endpoint.ResolveTasksApiUrl(ownerId);
        return new HiggsfieldMcpContext(apiUrl, () => _endpoint.GetServiceToken(ownerId!), _endpoint.HttpEndpointUsable(apiUrl));
    }

    // MCP-сервер редактора картинок (ADR-019 §4): в любом чате владельца — проектном и личном,
    // при загруженном модуле. Контекст собирается во ВСЕХ трёх точках
    // (StartNewSessionAsync и обе ветки EnsureProcessCoreAsync) — иначе состав прыгает между
    // первым ходом и перезапуском процесса (сторож в McpToolsetStabilityTests). Последнее — наличие тулсета в реестре: модуль выключен, а
    // контекст собран — CLI получил бы сервер, отвечающий 404, и «fetch failed» у всех
    // инструментов хода. Все условия — свойства сессии, владельца и процесса; от хода, фокуса и
    // нитей картинок состав не зависит (McpToolsetStabilityTests).
    internal ImageEditorMcpContext? BuildImageEditorContext(string? ownerId, Session session)
    {
        if (ownerId is null) return null;
        _mcpToolsets ??= _services?.GetService<Services.Mcp.Http.McpToolsetRegistry>();
        if (_mcpToolsets?.Find(McpEndpoints.ImageEditorName) is null) return null;
        var apiUrl = _endpoint.ResolveTasksApiUrl(ownerId);
        return new ImageEditorMcpContext(apiUrl, () => _endpoint.GetServiceToken(ownerId), _endpoint.HttpEndpointUsable(apiUrl),
            ImageEditor.ImageEditorAgentTools.AutoAllowTools);
    }

    // MCP-сервер модуля «Звук» (ADR-021 §5) — по тем же правилам, что image-editor: любой чат владельца,
    // тулсет в реестре (модуль загружен), все три точки сборки контекста. От хода,
    // фокуса, режима и нитей звука состав не зависит (McpToolsetStabilityTests).
    internal AudioEditorMcpContext? BuildAudioEditorContext(string? ownerId, Session session)
    {
        if (ownerId is null) return null;
        _mcpToolsets ??= _services?.GetService<Services.Mcp.Http.McpToolsetRegistry>();
        if (_mcpToolsets?.Find(McpEndpoints.AudioEditorName) is null) return null;
        var apiUrl = _endpoint.ResolveTasksApiUrl(ownerId);
        return new AudioEditorMcpContext(apiUrl, () => _endpoint.GetServiceToken(ownerId), _endpoint.HttpEndpointUsable(apiUrl),
            AudioEditor.AudioEditorAgentTools.AutoAllowTools);
    }

    // MCP-сервер модуля «Видео» (ADR-022 §5) — те же правила, что у «Звука»: любой чат владельца,
    // тулсет в реестре (модуль загружен), все три точки сборки контекста. От хода, фокуса и
    // поставщика состав не зависит (McpToolsetStabilityTests).
    internal VideoEditorMcpContext? BuildVideoEditorContext(string? ownerId, Session session)
    {
        if (ownerId is null) return null;
        _mcpToolsets ??= _services?.GetService<Services.Mcp.Http.McpToolsetRegistry>();
        if (_mcpToolsets?.Find(McpEndpoints.VideoEditorName) is null) return null;
        var apiUrl = _endpoint.ResolveTasksApiUrl(ownerId);
        return new VideoEditorMcpContext(apiUrl, () => _endpoint.GetServiceToken(ownerId), _endpoint.HttpEndpointUsable(apiUrl),
            VideoEditor.VideoEditorAgentTools.AutoAllowTools);
    }

    // MCP-сервер plugins (ADR-031, шаг 4.1): любой чат владельца, тулсет в реестре (подсистема
    // plugins загружена). Ограничения (флаг plugins-build-yourself, локальный проект) — на
    // вызове, не в составе. От хода, фокуса и черновиков состав не зависит (McpToolsetStabilityTests).
    internal PluginsMcpContext? BuildPluginsContext(string? ownerId, Session session)
    {
        if (ownerId is null) return null;
        _mcpToolsets ??= _services?.GetService<Services.Mcp.Http.McpToolsetRegistry>();
        if (_mcpToolsets?.Find(McpEndpoints.PluginsName) is null) return null;
        var apiUrl = _endpoint.ResolveTasksApiUrl(ownerId);
        return new PluginsMcpContext(apiUrl, () => _endpoint.GetServiceToken(ownerId), _endpoint.HttpEndpointUsable(apiUrl));
    }

    // MCP-сервер «Контекст чата» (ADR-023 §3.2) — любой чат владельца, тулсет в реестре, все три точки
    // сборки контекста. От хода и
    // содержимого контекста состав не зависит (McpToolsetStabilityTests).
    internal TurnContextMcpContext? BuildTurnContextContext(string? ownerId, Session session)
    {
        if (ownerId is null) return null;
        _mcpToolsets ??= _services?.GetService<Services.Mcp.Http.McpToolsetRegistry>();
        if (_mcpToolsets?.Find(McpEndpoints.TurnContextName) is null) return null;
        var apiUrl = _endpoint.ResolveTasksApiUrl(ownerId);
        return new TurnContextMcpContext(apiUrl, () => _endpoint.GetServiceToken(ownerId), _endpoint.HttpEndpointUsable(apiUrl),
            Services.ChatContext.TurnContextAgentTools.AutoAllowTools);
    }

    // MCP-сервер локальной генерации (ComfyUI на своей GPU). Узел есть в конфиге хода, только когда:
    //   1) включён машинный тумблер LocalMedia:Enabled и подсистема images (там живёт движок);
    //   2) чат проекта, чьи файлы на сервере: результат пишется в папку проекта
    //      (локальный проект ADR-016 — отказ через ProjectCapabilities);
    //   3) персона НЕ ReadOnly — сервер пишет файлы (тот же RO-гейт, что у higgsfield).
    // Всё это — свойства инстанса, сессии и персоны; тумблер читается живьём, но его поворот
    // штатно меняет сигнатуру запуска, как правка ключа у websearch. Гейты тулсета на вызове
    // повторяют проверки (defense-in-depth).
    internal LocalMediaMcpContext? BuildLocalMediaContext(string? ownerId, string? projectId, Persona? persona)
    {
        if (ownerId is null || projectId is null) return null;
        if (!Images.LocalMedia.LocalMediaOptions.IsEnabled(_config)) return null;
        if (!Composition.SubsystemGate.IsEnabled(_config, "images")) return null;
        if (persona is { Access: PersonaAccess.ReadOnly }) return null;
        if (_projects.GetById(projectId) is not { } project || !ProjectCapabilities.FilesOnServer(project)) return null;
        var apiUrl = _endpoint.ResolveTasksApiUrl(ownerId);
        return new LocalMediaMcpContext(apiUrl, () => _endpoint.GetServiceToken(ownerId), _endpoint.HttpEndpointUsable(apiUrl));
    }

    // MCP-сервер прогона тестов (run_tests). Узел есть в конфиге хода, только когда:
    //   1) включена подсистема test-runs (там живёт движок прогона);
    //   2) чат проекта, чьи файлы на сервере (локальный проект ADR-016 в v1 не поддерживаем:
    //      TRX и лог лежали бы на устройстве — тесты там агент гоняет Bash'ем);
    //   3) персоне не запрещён Bash (ReadOnly или Custom без Bash): прогон исполняет код
    //      проекта и пишет bin/obj и .cc-attachments — это тот же запуск кода, что и Bash.
    // Всё это — свойства сессии, персоны и процесса, не хода. Тулсет на вызове повторяет проверки.
    internal TestsMcpContext? BuildTestsContext(string? ownerId, string? projectId, Persona? persona)
    {
        if (ownerId is null || projectId is null) return null;
        if (!Composition.SubsystemGate.IsEnabled(_config, TestRuns.TestRunsSubsystem.SubsystemKey)) return null;
        if (!PersonaAccessPolicy.AllowsBash(persona)) return null;
        if (_projects.GetById(projectId) is not { } project || !ProjectCapabilities.FilesOnServer(project)) return null;
        var apiUrl = _endpoint.ResolveTasksApiUrl(ownerId);
        return new TestsMcpContext(apiUrl, () => _endpoint.GetServiceToken(ownerId), _endpoint.HttpEndpointUsable(apiUrl));
    }

    // Живое значение рубильника Mcp:HttpTransport для провайдера контекста: IConfiguration
    // перечитывается (appsettings.Local.json подключён с reloadOnChange), поэтому поворот
    // ключа доезжает до уже поднятых чатов следующим ходом — без рестарта бэкенда.
    internal bool HttpMcpEnabled() =>
        _config.GetValue(Services.Mcp.Http.McpHttpTransport.EnabledKey, true);

    // Сводный признак «у сессии есть продуктовые MCP-серверы, чей адрес допускает http»
    // (ADR-012): от него (вместе с живым рубильником) зависит NO_PROXY хода — обход прокси
    // нужен ЛЮБОМУ http-серверу, а не только виджетам. Решение стоит на едином гейте
    // HttpEndpointUsable (каждый Build*Context уже прогнал через него свой UseHttp) —
    // отдельного условия «виджеты на http» больше нет; во волне 2 к widgets/memory
    // добавились tasks/notes/personas, в волне 3 — wsp/notifications/codegraph, в волне 4 —
    // dify. pmem-консультанты приезжают списком на каждый ход и уточняют признак на стороне
    // ClaudeSession.
    internal static bool HttpMcpActive(WidgetsMcpContext? widgets, MemoryMcpContext? memory,
        TasksMcpContext? tasks = null, NotesMcpContext? notes = null, PersonasMcpContext? personas = null,
        WorkspaceMcpContext? workspace = null, NotificationsMcpContext? notifications = null,
        CodeGraphMcpContext? codeGraph = null, DifyMcpContext? dify = null,
        WatchMcpContext? watch = null, WebSearchMcpContext? webSearch = null,
        HiggsfieldMcpContext? higgsfield = null, ImageEditorMcpContext? imageEditor = null,
        LocalMediaMcpContext? localMedia = null, ArchitectureMcpContext? architecture = null,
        AudioEditorMcpContext? audioEditor = null, VideoEditorMcpContext? videoEditor = null,
        TurnContextMcpContext? turnContext = null, TestsMcpContext? tests = null,
        PluginsMcpContext? plugins = null) =>
        architecture is { UseHttp: true } || tests is { UseHttp: true }
        || widgets is { UseHttp: true } || memory is { UseHttp: true }
        || tasks is { UseHttp: true } || notes is { UseHttp: true } || personas is { UseHttp: true }
        || workspace is { UseHttp: true } || notifications is { UseHttp: true }
        || codeGraph is { UseHttp: true } || dify is { UseHttp: true }
        || watch is { UseHttp: true } || webSearch is { UseHttp: true }
        || higgsfield is { UseHttp: true } || imageEditor is { UseHttp: true }
        || localMedia is { UseHttp: true } || audioEditor is { UseHttp: true }
        || videoEditor is { UseHttp: true } || plugins is { UseHttp: true }
        || turnContext is { UseHttp: true };

    // Браузер (плагин playwright): нужен по роли тестировщику, остальным персонам — нет.
    // Ключ-надстройка «browser» с дефолтом по пресету (SectionEnabled → SpecialtySections),
    // как git/kb; чат без персоны получает браузер как раньше — ручную проверку страницы
    // человек делает из своего чата. Решение зависит только от персоны, поэтому постоянно
    // в рамках сессии (оно входит в сигнатуру прогона — см. ClaudeRuntimeSettings).
    internal bool BrowserEnabled(string? ownerId, Persona? persona) =>
        persona is null || _bindings.SectionEnabled(ownerId, persona, "browser");

    // Контекст MCP-сервера баз знаний Dify (ADR-012, волна 4 — последний продуктовый
    // сервер фазы 2). Подключается при настроенной секции Dify (ApiUrl/ApiKey — источник
    // правды ключа с волны 4: внешний dify-узел базового конфига им перекрывается).
    // Проект/дефолтный датасет в контекст не входят: тулсет резолвит их живьём из
    // сессии-вызывателя (датасет появляется у проекта в середине жизни чата).
    // DifyUrl/DifyKey — только stdio-ветке отката (env узла mcp-dify/dist).
    internal DifyMcpContext? BuildDifyContext(string? ownerId)
    {
        if (ownerId is null) return null;
        if (string.IsNullOrEmpty(_dify.ApiUrl) || string.IsNullOrEmpty(_dify.ApiKey)) return null;
        var apiUrl = _endpoint.ResolveTasksApiUrl(ownerId);
        return new DifyMcpContext(apiUrl, _dify.ApiUrl.TrimEnd('/'), _dify.ApiKey,
            () => _endpoint.GetServiceToken(ownerId), UseHttp: _endpoint.HttpEndpointUsable(apiUrl));
    }

    // Руки локального проекта (ADR-016 §7) — свойство чата, а не хода: матрица
    // ProjectCapabilities.HandsRefusal (локальный проект, hands у устройства,
    // тумблер проекта). Провайдер хода не сужает ни руки, ни фолбэк (решение владельца
    // 2026-09-27): снимки окон отсекает зрение провайдера в ClaudeSession.
    // Канал устройства резолвится лениво: прямая зависимость замкнула бы граф синглтонов.
    internal bool HandsFor(string? projectId)
    {
        if (projectId is null || _projects.GetById(projectId) is not { OwnerId: { } ownerId } project) return false;
        var device = ProjectCapabilities.IsDeviceBound(project)
            ? _services?.GetService<Execution.IDeviceExecChannel>()?.GetStatus(ownerId, project.DeviceId!)
            : null;
        var refusal = ProjectCapabilities.HandsRefusal(project, device, project.HandsEnabled);
        return refusal is null;
    }

    // Работает ли у проекта чата группа «нужен контент на сервере» (ADR-016 §4). Чат вне
    // проекта — да: его папка серверная
    internal bool ServerContentFor(string? projectId) =>
        projectId is null || _projects.GetById(projectId) is not { } p || ProjectCapabilities.ServerContentEnabled(p);

    // Адрес шлюза LLM для серверного процесса CLI (ADR-016 §2, серверный ход провайдера с
    // NormalizeToolInputArrays) — тот же, что у MCP-серверов хода, у песочницы это мост хоста.
    // У чата проекта, привязанного к устройству, — null: CLI там живёт на устройстве, и шлюз
    // ходу ставит раннер устройства.
    internal string? LlmGatewayApiUrlFor(Session info, string? ownerId) =>
        info.ProjectId is not null && _projects.GetById(info.ProjectId) is { } p && ProjectCapabilities.IsDeviceBound(p)
            ? null
            : _endpoint.ResolveTasksApiUrl(ownerId);

    // Контекст MCP-сервера графа кода: инструменты codegraph_* доступны только в чате проекта —
    // граф ключуется проектом (в чате вне проекта искать нечего). Тот же сервисный токен
    // владельца, что у tasks/notes; владение проектом дополнительно проверяет CodeGraphController.
    // rootPath — рабочее дерево сессии (EffectiveRoot): у чата с отдельным worktree свой граф,
    // иначе инструменты смотрели бы в основное дерево, а правки шли в другое (ADR-003).
    // Персона может выключить граф Off-привязкой tool:codegraph — тогда нет ни сервера,
    // ни slice в промпте (CodeGraphContributor).
    internal CodeGraphMcpContext? BuildCodeGraphContext(string? ownerId, string? projectId, string sessionId,
        string? rootPath, Persona? persona)
    {
        if (ownerId is null || string.IsNullOrEmpty(projectId)) return null;
        // CodeGraph — группа «контент на сервере»: у локального проекта инструмента нет
        if (!ServerContentFor(projectId)) return null;
        if (!_bindings.ServerToolEnabled(ownerId, persona, "codegraph")) return null;
        var apiUrl = _endpoint.ResolveTasksApiUrl(ownerId);
        return new CodeGraphMcpContext(apiUrl, () => _endpoint.GetServiceToken(ownerId), projectId, sessionId, rootPath,
            UseHttp: _endpoint.HttpEndpointUsable(apiUrl));
    }

    // Контекст MCP-сервера памяти персоны (та же фабрика сервисного токена, что у tasks/notes).
    // projectId — проект ТЕКУЩЕГО чата (③-3.4: даёт доступ к team_memory_* команды), не scope
    // персоны — см. BuildPersonaLayer: любая персона в проектном чате получает эти инструменты,
    // пишет ли она в команду реально — решает бэкенд-гейт (TeamMemoryService.WriteDeniedFor).
    // DossierToolsEnabled — секция dossier_lookup/dossier_get (этап 2, ADR-004 §5): включена
    // всегда (флаг change-dossiers-recall снят). Входит в отпечаток состава сервера (shapes
    // memory) — от СВОЙСТВ ХОДА состав tools/list не зависит (инвариант McpToolsetStabilityTests).
    // UseHttp — сервер памяти переехал в Kestrel (ADR-012, фаза 2): ход зовёт его по http
    // (personaId/projectId едут хвостом URL), процесса node нет; false — откат на stdio.
    internal MemoryMcpContext BuildMemoryContext(string ownerId, string personaId, string? projectId)
    {
        var apiUrl = _endpoint.ResolveTasksApiUrl(ownerId);
        return new MemoryMcpContext(apiUrl, () => _endpoint.GetServiceToken(ownerId), personaId, projectId,
            DossierToolsEnabled: true,
            UseHttp: _endpoint.HttpEndpointUsable(apiUrl),
            SphereToolsEnabled: _flags.IsEnabled(ownerId, FeatureFlagKeys.Spheres));
    }

    // Контекст memory-server для проектной сессии БЕЗ персоны: только team_memory_* (③-3.4) —
    // память проекта доступна из ЛЮБОГО чата проекта, не только персонного (personaId пуст →
    // personal-инструменты memory_* не регистрируются, см. mcp/memory-server/index.js).
    internal MemoryMcpContext? BuildTeamMemoryContext(string? ownerId, string? projectId) =>
        ownerId is not null && !string.IsNullOrEmpty(projectId)
            ? BuildMemoryContext(ownerId, "", projectId)
            : null;
}
