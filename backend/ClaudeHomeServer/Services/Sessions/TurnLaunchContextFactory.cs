using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.ChildMode;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Turn;

namespace ClaudeHomeServer.Services.Sessions;

// Сборка контекста запуска (S3-5, docs/research/session-manager-split-2026-11.md §1 «процесс», §2):
// единое место, где собирается LlmSessionContext для процесса CLI. До S3-5 конструкция
// дублировалась в трёх местах SessionManager (StartNewSessionAsync и обе ветки
// EnsureProcessCoreAsync); здесь она одна — метод Compose, к которому ведут два тонких вызова
// (первый старт и переподключение). Чем именно отличаются прежние ветки и как это сохранено
// значениями, а не составом конструкции — комментарий у Compose.
//
// Зависимостей ровно десять (потолок плана §2; сторож SessionCoreShapeGuardTests правило 2):
// четыре билдера контекста (builtin, personaLayer, external, moduleContexts), фабрика лаунчеров,
// источник каталогов плагинов, config (DataDir и SystemPromptViaFile — их читает только контекст
// запуска, поле _dataDir из конструктора фасада переехало сюда целиком), стор (PersistSessions и
// FrameSource — единственное законное пользование стора в фабрике) и два ленивых шва:
// ITurnMessageSink (сообщения процесса в разбор ленты SessionManager — шов введён здесь, S3-5)
// и ITurnStarter (OrchestrationDone — разбор очереди после хода оркестратора).
//
// Отклонение от таблицы плана §2 (отдельным решением не подтверждено — на проверке у Александра):
// рождается в момент вызова — передаёт вызывающий
// (TurnProcessHost, у него есть маршрут и проекты): rootPath/mainRootPath (EffectiveRoot /
// ResolveChatRoot — территория маршрута), cliConfigRoot (CliRoute.ConfigRootFor — метод экземпляра),
// transcriptOnServer (CliRoute.TranscriptOnServer — единственная запись правила ADR-016),
// project (резолв вызывающего, вместе с отказом «Проект не найден»), permissionRules (замыкание на
// ProjectManager), колбэк EnqueueBypass (очередь — дело хоста). Фабрика не берёт ProjectManager и
// CliRoute в конструктор — ровно это и значит «зависимостей десять».
// Класс internal (как в плане §2): его сигнатура держит internal-шов ITurnMessageSink с
// internal TurnAccumulator, поэтому публичной она быть не может (CS0051). Полем и параметром
// публичного конструктора фасада фабрика тоже не становится — она нужна только хосту процесса
// и собирается там: в DI — в лямбде AddSessionCore, в тестах — в запасной сборке фасада.
internal sealed class TurnLaunchContextFactory
{
    private readonly BuiltinMcpContexts _builtin;
    private readonly PersonaLayerBuilder _persona;
    private readonly ExternalMcpContexts _external;
    private readonly ModulesMcpContexts _moduleContexts;
    private readonly Execution.ILauncherFactory _launchers;
    private readonly Plugins.IPluginDirSource? _pluginDirSource;
    private readonly IConfiguration _config;
    private readonly SessionStore _store;
    private readonly Lazy<ITurnMessageSink> _messageSink;
    private readonly Lazy<ITurnStarter> _turnStarter;

    public TurnLaunchContextFactory(BuiltinMcpContexts builtin, PersonaLayerBuilder personaLayer,
        ExternalMcpContexts external, ModulesMcpContexts moduleContexts,
        Execution.ILauncherFactory launchers, Plugins.IPluginDirSource? pluginDirSource,
        IConfiguration config, SessionStore store,
        Lazy<ITurnMessageSink> messageSink, Lazy<ITurnStarter> turnStarter)
    {
        _builtin = builtin;
        _persona = personaLayer;
        _external = external;
        _moduleContexts = moduleContexts;
        _launchers = launchers;
        _pluginDirSource = pluginDirSource;
        _config = config;
        _store = store;
        _messageSink = messageSink;
        _turnStarter = turnStarter;
    }

    // Директория данных для CLI (выражение переехало из конструктора SessionManager: значение
    // нужно только контексту запуска). Относительный DataPath отдал бы в deny-правило CLI
    // относительный корень, а «//…» в синтаксисе правил — абсолютный путь (ревью S1c, Н5).
    private string DataDir => Path.GetDirectoryName(
        Path.GetFullPath(_config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json")))
        ?? Path.Combine(AppContext.BaseDirectory, "data");

    // Единственное место чтения Llm:SystemPromptViaFile (нет ключа = файл включён)
    private bool SystemPromptViaFile =>
        !bool.TryParse(_config["Llm:SystemPromptViaFile"], out var viaFile) || viaFile;

    // Персона-слой сессии: контекст памяти (для долгой памяти персоны/команды) + сама персона
    // для гейтов возможностей. Промпт характера + групповая надстройка + онбординг-оверлей —
    // у IPromptSectionContributor (PersonaLayerContributor). Резолв персоны — через
    // PersonaLayerBuilder.ResolvePersona: PersonaManager остаётся у роли персоны.
    private (MemoryMcpContext? Memory, Persona? Persona)
        BuildPersonaLayer(Session session, string? ownerId)
    {
        if (session.PersonaId is null || ownerId is null) return (null, null);
        var persona = _persona.ResolvePersona(session.PersonaId, ownerId);
        if (persona is null) return (null, null);
        // Долгая память — только если включена у персоны
        if (!persona.MemoryEnabled) return (null, persona);
        // team_memory_* (③-3.4, диета памяти команды ч.3) — по проекту ТЕКУЩЕГО чата, не по
        // scope персоны: состав MCP-инструментов один и тот же у проектных и глобальных персон
        // (инвариант «tools/list не зависит от хода» — тем более не от того, какая персона),
        // а пишет ли персона в команду — решает бэкенд (ProjectsController.TeamMemoryWriteAllowed:
        // Persona.Scope==Project && Persona.ProjectId==id проекта памяти). Глобальная персона в
        // проектном чате получает team_memory_list/search (read-only), персона другого проекта —
        // так же; вне проектного чата (session.ProjectId пуст) команды памяти нет вообще.
        return (_builtin.BuildMemoryContext(ownerId, persona.Id, session.ProjectId), persona);
    }

    // Билдеры колбэков для LlmSessionContext: замыкают sessionId сессии (адаптер передаёт тот же
    // Info.Id, но захват надёжнее — сессия уже известна при создании контекста).
    private Action<string> BuildOrchestrationDone(string sessionId)
        => __ => { _ = Task.Run(async () =>
            {
                try { await _turnStarter.Value.DrainNextPendingAsync(sessionId); }
                catch (Exception ex) { Console.Error.WriteLine($"[SessionManager] Разбор очереди после оркестрации ({sessionId}): {ex.Message}"); }
            }); };

    // Первый старт сессии (бывш. StartNewSessionAsync) и переподключение (бывш.
    // EnsureProcessCoreAsync, обе его ветки) — два тонких вызова к одной сборке Compose:
    // единственному месту, где рождается LlmSessionContext (сторож c4: grep по Services/ —
    // одно совпадение). Различия прежних веток выражены значениями, а не составом конструкции:
    // — ownerId: у проектной ветки — владелец найденного проекта, у внепроектной — owner чата
    //   (для проектного чата ResolveOwnerId и есть владелец проекта);
    // — mainRootPath/rawSystemPrompt/permissionRules: то, что ветка брала из проекта;
    //   у внепроектной — null, как раньше;
    // — контексты, которых внепроектная ветка не строила вовсе (codeGraph, architecture,
    //   localMedia, tests, teamMemory, chatContext): их билдеры возвращают null по пустому
    //   projectId (BuiltinMcpContexts: BuildCodeGraphContext/BuildArchitectureContext/
    //   BuildLocalMediaContext/BuildTestsContext/BuildTeamMemoryContext), поэтому общий вызов
    //   даёт ровно те же null; ServerContentFor(null) = true и HandsFor(null) = false — те же
    //   значения, что давали дефолты record в прежней внепроектной конструкции.
    // Формы параметров — то, что родилось в момент вызова и известно вызывающему (rootPath,
    // резолв проекта, ConfigRootFor); колбэк msg строится здесь, через шов ITurnMessageSink:
    // разбор сообщения остаётся у SessionManager до S3-6.
    internal LlmSessionContext BuildForNewSession(Session session, string? ownerId, Project? project,
        string rootPath, string mainRootPath, string? rawSystemPrompt,
        Func<IReadOnlyList<PermissionRule>>? permissionRules, TurnAccumulator accumulator, long runId,
        Func<string, string, IReadOnlyList<string>?, int, bool, Task> enqueueBypass,
        ITurnEventBus? events, string cliConfigRoot, bool transcriptOnServer)
        => Compose(session, ownerId, project, rootPath, mainRootPath, rawSystemPrompt, permissionRules,
            accumulator, runId, enqueueBypass, events, cliConfigRoot, transcriptOnServer);

    // Переподключение: project — найден и проверен вызывающим (отказ «Проект не найден»
    // остаётся у хоста); rootPath — EffectiveRoot/ResolveChatRoot, посчитанные хостом
    // (у фабрики нет маршрута).
    internal LlmSessionContext BuildForReconnect(SessionEntry entry, Project? project,
        string rootPath, TurnAccumulator accumulator, long runId,
        // nullable, как в исходных местах фасада: внепроектная ветка передаёт null
        // (правил разрешения проекта нет)
        Func<IReadOnlyList<PermissionRule>>? permissionRules,
        Func<string, string, IReadOnlyList<string>?, int, bool, Task> enqueueBypass,
        ITurnEventBus? events, string cliConfigRoot, bool transcriptOnServer)
        => Compose(entry.Info, project?.OwnerId ?? entry.Info.OwnerId, project, rootPath,
            project?.RootPath, project?.SystemPrompt, permissionRules,
            accumulator, runId, enqueueBypass, events, cliConfigRoot, transcriptOnServer);

    private LlmSessionContext Compose(Session session, string? ownerId, Project? project,
        string rootPath, string? mainRootPath, string? rawSystemPrompt,
        Func<IReadOnlyList<PermissionRule>>? permissionRules, TurnAccumulator accumulator, long runId,
        Func<string, string, IReadOnlyList<string>?, int, bool, Task> enqueueBypass,
        ITurnEventBus? events, string cliConfigRoot, bool transcriptOnServer)
    {
        // И1-бис (ADR-041): последний рубеж детского режима. Поля чата ребёнка сверяются с профилем
        // ДО сборки любого контекста; расхождение — ход не стартует, тихой подстановки нет.
        if (_builtin.ChildGate.LaunchMismatch(ownerId, session) is { } mismatch)
            throw new InvalidOperationException("Детский режим: ход не запущен — " + mismatch);
        // И3 (ADR-041, KM-B5d): вне окна, сверх дневного лимита пространства и сверх бюджета новый
        // ход ребёнка не стартует. Место — здесь, а не на входах: через Compose идут и SendMessage
        // хаба, и автопродолжение после рестарта (BuildForReconnect), и ретраи, и цикл «до готово»
        // (фолбэк у ребёнка пуст, KM-B4). Начатый ход доживает: доживающий процесс не пересобирается
        // — EnsureProcessCoreAsync выходит при entry.Process is not null (TurnProcessHost.cs:179),
        // поэтому отказ бьёт только по ещё не начатому ходу. Состояние считает ChildLimitsCalculator
        // (одна точка, KM-B5c); здесь — только штатная ошибка хода с детским текстом, без технических
        // слов. Решение на каждом запуске заново, без кэша: правка профиля действует со следующего хода.
        // Текст ошибки — сам детский текст состояния, без технического префикса (И3): его читает
        // ребёнок в ленте чата.
        if (_builtin.ChildGate.LaunchLimitRefusal(ownerId, session, DateTimeOffset.UtcNow) is { } refusal)
            throw new InvalidOperationException(refusal);
        // И1 п.10: детскому владельцу объявляются только серверы с парами в детском allow-list.
        // Решение зависит от владельца, не от хода — состав tools/list не мерцает.
        var childGate = _builtin.ChildGate;
        var persona = BuildPersonaLayer(session, ownerId);
        // Сужение набора MCP слепком этапа (И5): свойство сессии, зафиксированное при её создании
        var stageAllow = session.StageMcpAllow;
        T? Gate<T>(string serverKey, T? context) where T : class =>
            BuiltinMcpContexts.StageAllows(stageAllow, serverKey) && childGate.AllowsServer(ownerId, serverKey)
                ? context : null;
        var workspace = Gate("wsp", _external.BuildWorkspaceContext(ownerId, session.ProjectId, session.Id, persona.Persona));

        var widgetsMcp = Gate("widgets", _builtin.BuildWidgetsContext(ownerId, persona.Persona));
        var watchMcp = Gate("watch", _builtin.BuildWatchContext(ownerId));
        // Репетитор детского режима: объявляется ТОЛЬКО ребёнку и только пока у сервера есть пара в
        // allow-list. НЕ через Gate(...): у взрослого AllowsServer = true для любого сервера, и tutor
        // объявился бы всем (разрез «Знания репетитора» §3). Решение — свойство владельца, не хода.
        var tutorMcp = ownerId is not null && childGate.IsChild(ownerId) && ChildModeAllowList.AllowsServer("tutor")
            ? _builtin.BuildTutorContext(ownerId, session.Id) : null;
        var webSearchMcp = Gate("websearch", _builtin.BuildWebSearchContext(ownerId, persona.Persona));
        var higgsfieldMcp = Gate("higgsfield", _builtin.BuildHiggsfieldContext(ownerId, persona.Persona));
        var imageEditorMcp = Gate("image-editor", _builtin.BuildImageEditorContext(ownerId, session));
        var audioEditorMcp = Gate("audio-editor", _builtin.BuildAudioEditorContext(ownerId, session));
        var videoEditorMcp = Gate("video-editor", _builtin.BuildVideoEditorContext(ownerId, session));
        var pluginsMcp = Gate("plugins", _builtin.BuildPluginsContext(ownerId, session));
        var turnContextMcp = Gate("turn-context", _builtin.BuildTurnContextContext(ownerId, session));
        var localMediaMcp = Gate("local-media", _builtin.BuildLocalMediaContext(ownerId, session.ProjectId, persona.Persona));
        var testsMcp = Gate("tests", _builtin.BuildTestsContext(ownerId, session.ProjectId, persona.Persona));
        var memoryMcp = Gate("memory", persona.Memory ?? _builtin.BuildTeamMemoryContext(ownerId, session.ProjectId));
        var tasksMcp = _builtin.TasksMcpEnabled(ownerId, session, persona.Persona)
            && childGate.AllowsServer(ownerId, "tasks")
            ? _builtin.BuildTasksContext(ownerId, session.ProjectId, persona.Persona) : null;
        var notesMcp = _builtin.NotesMcpEnabled(ownerId, persona.Persona)
            && childGate.AllowsServer(ownerId, "notes")
            ? Gate("notes", _builtin.BuildNotesContext(ownerId, session.ProjectId, persona.Persona)) : null;
        var personasMcp = Gate("personas", _persona.BuildPersonasContext(ownerId, session.ProjectId, session, persona.Persona));
        var notificationsMcp = Gate("notifications", _external.BuildNotificationsContext(ownerId, session.PersonaId, persona.Persona));
        var codeGraphMcp = Gate("codegraph", _builtin.BuildCodeGraphContext(ownerId, session.ProjectId, session.Id, rootPath, persona.Persona));
        var architectureMcp = Gate("architecture", _builtin.BuildArchitectureContext(ownerId, session.ProjectId, persona.Persona));
        var difyMcp = Gate("dify", _builtin.BuildDifyContext(ownerId));
        var hands = _builtin.HandsFor(session.ProjectId);
        // Материалы контекста и проверка локальности — по проекту, который нашёл вызывающий:
        // тот же GetById по session.ProjectId, что делал фасад (null — когда id нет или проекта нет).
        var launcher = project is { } launchProject
            ? _launchers.ForProject(launchProject)
            : _launchers.ForOwner(ownerId);
        return new LlmSessionContext(rootPath,
            msg => _messageSink.Value.OnMessageQueuedAsync(session.Id, accumulator, msg, runId),
            rawSystemPrompt, ProjectManager.BuiltInSystemPrompt, permissionRules,

            ContentRootPath: AppContext.BaseDirectory,
            DataDir: DataDir,
            TasksMcp: tasksMcp,
            OwnerId: ownerId,
            NotesMcp: notesMcp,
            PersonaProvider: _persona.BuildPersonaProvider(session, ownerId),
            MemoryMcp: memoryMcp,
            ExtraDisallowedTools: _persona.BuildExtraDisallowed(ownerId, persona.Persona, session),
            PersonasMcp: personasMcp,
            NotificationsMcp: notificationsMcp,
            WorkspaceMcp: workspace,
            PersonaAgentsProvider: _persona.BuildPersonaAgentsProvider(ownerId, session, persona.Persona),
            // Проектный чат — среда проекта (ADR-016: локальный проект исполняется на устройстве)
            Launcher: launcher,
            ModulesMcp: childGate.IsChild(ownerId) ? null : _moduleContexts.BuildModulesContext(ownerId),
            WidgetsMcp: widgetsMcp,
            CodeGraphMcp: codeGraphMcp,
            DifyMcp: difyMcp,
            BrowserEnabled: !childGate.IsChild(ownerId) && _builtin.BrowserEnabled(ownerId, persona.Persona),
            SystemPromptViaFile: SystemPromptViaFile,
            CliConfigRoot: cliConfigRoot,
            // Внешние серверы владельца ребёнку не объявляются вовсе (И1 п.10)
            ExternalMcpProvider: childGate.IsChild(ownerId) ? null : _external.BuildExternalMcpProvider(ownerId, session.ProjectId, persona.Persona, session.Id, stageAllow),
            PersistSessions: _store.SaveSessions,
            EnqueueBypass: enqueueBypass,
            OrchestrationDone: BuildOrchestrationDone(session.Id),
            HttpMcpActive: BuiltinMcpContexts.HttpMcpActive(widgetsMcp, memoryMcp, tasksMcp, notesMcp, personasMcp,
                workspace, notificationsMcp, codeGraphMcp, difyMcp, watchMcp, webSearchMcp, higgsfieldMcp,
                imageEditorMcp, localMediaMcp, architectureMcp, audioEditorMcp, videoEditorMcp, turnContextMcp, testsMcp,
                pluginsMcp, tutorMcp),
            HttpMcpEnabledProvider: _builtin.HttpMcpEnabled,
            ArchitectureMcp: architectureMcp,
            // Материалы контекста — только у проектных чатов (адреса file/task живут внутри
            // проекта); во внепроектной ветке восстановления провайдер не передаётся вовсе
            ChatContextProvider: session.ProjectId is not null ? _external.BuildChatContextProvider(session.Id) : null,
            Events: events,
            WatchMcp: watchMcp,
            TutorMcp: tutorMcp,
            WebSearchMcp: webSearchMcp,
            HiggsfieldMcp: higgsfieldMcp,
            ImageEditorMcp: imageEditorMcp,
            AudioEditorMcp: audioEditorMcp,
            VideoEditorMcp: videoEditorMcp,
            PluginsMcp: pluginsMcp,
            TurnContextMcp: turnContextMcp,
            LocalMediaMcp: localMediaMcp,
            TestsMcp: testsMcp,
            // Корень ГЛАВНОЙ ветки проекта — fallback для slice графа кода, пока свой граф
            // worktree-ветки не построен (ADR-003). У не-worktree чата совпадает с rootPath,
            // fallback сводится к no-op в CodeGraphPromptProvider.GetSliceAsync.
            MainRootPath: mainRootPath,
            ServerContent: _builtin.ServerContentFor(session.ProjectId),
            // Лежит ли транскрипт на диске сервера (ADR-016): значение считает вызывающий
            // (_route.TranscriptOnServer) и передаёт параметром — единственная запись правила
            // остаётся в CliRoute (замечание 07.11)
            TranscriptOnServer: transcriptOnServer,
            HandsEnabled: hands,
            LlmGatewayApiUrl: _builtin.LlmGatewayApiUrlFor(session, ownerId),
            // ADR-031, шаг 2.1: каталоги плагинов для --plugin-dir (опционально) и проект
            // для проверки локальности (у локального проекта в MVP плагинов нет).
            // Детский ход (И1, п.13): источник плагинов не отдаётся вовсе — пустой набор по построению.
            PluginDirSource: childGate.IsChild(ownerId) ? null : _pluginDirSource,
            // Рамка хода для подборки (S2a-4): вердикт читает сам ClaudeSession через этот шов
            Project: project,
            FrameSource: _store,
            // И2: признак «ход ребёнка» живёт в контексте запуска и читается на каждом ходу
            // Политика берётся из гейта билдера (он обязателен), а не отдельной опциональной зависимостью
            ChildPolicyProvider: !string.IsNullOrEmpty(ownerId)
                ? () => childGate.PolicyFor(ownerId)
                : null);
    }
}
