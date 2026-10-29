using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Skills;
using ClaudeHomeServer.Services.Tasks;
using ClaudeHomeServer.Services.Memory;
using ClaudeHomeServer.Services.Knowledge;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Notes;
using ClaudeHomeServer.Services.Prompts;
using ClaudeHomeServer.Services.Team;
using ClaudeHomeServer.Services.TriggerSources;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ClaudeHomeServer.Tests.Services;

public abstract class SessionManagerTestBase : IDisposable
{
    protected const string TestUserId = "test-user-id";
    protected const string TestUsername = "test-user";

    protected readonly string _tempDir;
    protected readonly ProjectManager _projectManager;
    protected readonly TeamPlanningService _teamPlanning;
    protected readonly StubCheapRunner _plannerStub;
    // Ответ подставного планировщика для тестов карточки плана (Э2)
    protected string _plannerAnswer = "";
    // Исключение подставного планировщика (null — не бросать): так тесты имитируют
    // сбой и таймаут планировщика (карточка отказа и повтор по кнопке).
    protected Exception? _plannerException;
    protected readonly ChatHistoryService _historyService;
    protected readonly UserStore _userStore;
    protected readonly PersonaManager _personaManager;
    protected readonly AppSettingsService _appSettings;
    protected readonly ClaudeHomeServer.Services.Llm.LocalActionOverridesStore _actionOverrides;
    protected readonly UsageService _usage;
    protected readonly SubscriptionActivityTracker _activity;
    // Стор паспортов сабагентов (только память): без него сток паспортов SessionManager = null
    protected readonly ClaudeHomeServer.Services.Llm.Claude.SubagentRunLog _subagentRuns = new();
    protected readonly ClaudeSubscriptionPool _subPool;
    protected readonly SessionManager _sut;
    // Пустой реестр задач для гейта паузы в ReportBlockerAsync: гасится в Dispose фикстуры,
    // а не в конструкторе — владелец ссылки (_sut) живёт дольше фикстуры-конструктора.
    protected readonly ClaudeHomeServer.Services.Tasks.TaskManager _taskStore;
    // Подставной шов графа кода: считает запросы прогрева дерева (ADR-003)
    protected readonly RecordingGraphInspector _graphInspector = new();

    protected sealed class RecordingGraphInspector : ClaudeHomeServer.Services.CodeGraph.ICodeGraphInspector
    {
        public List<string> Warmed { get; } = [];
        public Task<ClaudeHomeServer.Services.CodeGraph.CodeGraphSnapshot?> GetSnapshotAsync(string rootPath, CancellationToken ct)
            => Task.FromResult<ClaudeHomeServer.Services.CodeGraph.CodeGraphSnapshot?>(null);
        public void StartRebuildIfIdle(string rootPath) { }
        public Task WarmIfNeeded(string rootPath)
        {
            lock (Warmed) Warmed.Add(rootPath);
            return Task.CompletedTask;
        }
    }
    protected readonly TrackingBroadcaster _broadcaster;
    protected readonly List<ServerMessage> _sentMessages = new();
    // Broadcast'ы из фоновых задач (OnMessageAsync запускает HandleTeamTurnEnd fire-and-forget)
    // пишут в _sentMessages параллельно с чтением из проверок теста — синхронизируем
    // запись и чтение, иначе Add попадает в момент перечисления ToList()
    // («Collection was modified», плавуче на слабом CI-раннере).
    protected readonly object _sentMessagesLock = new();

    // ЕДИНСТВЕННЫЙ способ читать отправленные сообщения в тестах: снимок под локом.
    // Голое `_sentMessages.OfType<…>()` в проверке — гонка с фоновым разбором конца хода.
    protected List<T> Sent<T>()
    {
        lock (_sentMessagesLock) return _sentMessages.OfType<T>().ToList();
    }

    protected void ClearSent()
    {
        lock (_sentMessagesLock) _sentMessages.Clear();
    }

    // Локальная обёртка ISessionBroadcaster поверх TestSessionBroadcaster: писали через мок
    // IHubContext, и старый setup ловил ТОЛЬКО session-группу (user_/project_-группы шли
    // в отдельный Mock<IClientProxy> без callback — без записи в _sentMessages). Здесь
    // то же правило: только ToSession пишет в _sentMessages, остальные каналы — в inner.
    // Не лежит в Helpers/ — единый тест, иначе размывает «broadcaster пишет в одно место»
    // в общий шаблон.
    protected sealed class TrackingBroadcaster(
        TestSessionBroadcaster inner, List<ServerMessage> sentMessages, object sentLock)
        : ClaudeHomeServer.Services.Composition.ISessionBroadcaster
    {
        // Гейт медленного клиента: пока он не завершён, рассылка в session-группу «висит»
        // (как SignalR Group.SendAsync при полном буфере клиента). Запись в sentMessages
        // идёт ПОСЛЕ гейта — в списке только реально доставленное.
        public Func<Protocol.ServerMessage, Task>? Gate { get; set; }

        public async Task ToSession(string sessionId, Protocol.ServerMessage message)
        {
            if (Gate is { } gate) await gate(message);
            lock (sentLock) sentMessages.Add(message);
            await inner.ToSession(sessionId, message);
        }

        // Except-канал — тот же единый поток чтения (Sent<T>): ручной ввод теперь
        // приходит через него, тесты «в ленте есть user_message» не должны различать адрес
        public Task ToSessionExcept(string sessionId, string exceptConnectionId, Protocol.ServerMessage message)
        {
            lock (sentLock) sentMessages.Add(message);
            return inner.ToSessionExcept(sessionId, exceptConnectionId, message);
        }

        public Task ToOwner(string ownerId, Protocol.ServerMessage message) =>
            inner.ToOwner(ownerId, message);

        public Task ToProject(string projectId, Protocol.ServerMessage message) =>
            inner.ToProject(projectId, message);

        public Task ToPreviewLog(string projectId, string serviceId, Protocol.ServerMessage message) =>
            inner.ToPreviewLog(projectId, serviceId, message);

        // Доступ к inner для проверки адресации каналов (Session vs SessionExcept):
        // Sent<T> не различает, кому именно ушла реплика
        public Helpers.TestSessionBroadcaster Inner => inner;
    }

    public SessionManagerTestBase()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "smgr_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_tempDir, "projects.json"),
                // Автосейв по таймеру выключен: внутри SaveSessions живёт sweep-terminus, и
                // фоновое срабатывание выполняло его между ассертами теста. Когда sweep ещё читал
                // статический Session.TaskSourceSessionResolver (перезаписывался каждым новым
                // TaskManager в параллельном классе), это давало плавающее падение
                // Sweep_ЖивойПотомокВГлубину на полном прогоне. Тесты зовут sweep явно.
                ["Session:AutoSaveSeconds"] = "0",
                // Домашние папки владельцев (чаты вне проекта живут в {home}/Chats) — в temp
                ["DefaultProjectsPath"] = Path.Combine(_tempDir, "homes"),
                // Профиль CLI внутри temp — иначе уборка транскриптов при удалении чата
                // (DeleteAsync) полезла бы в настоящий ~/.claude пользователя
                ["ClaudeUserProfileDir"] = Path.Combine(_tempDir, "claude-profile"),
                // Сторонний провайдер glm — для тестов пересчёта Provider по модели
                // (инцидент 14.08.2026: пара «модель × провайдер» из разных шагов цепочки)
                ["LlmProviders:glm:ApiKey"] = "sk-test",
                ["LlmProviders:glm:AnthropicBaseUrl"] = "https://glm.example.com",
                ["LlmProviders:glm:Models:0:Id"] = "glm-5.2",
                // Сериализация отложенной доставки на смерти (Ф1) — в тестах отключена: StubAdapter
                // держит HasLiveTurn=true вечно, без этого ключа drain-тесты ждали бы потолок.
                ["Delivery:AwaitProcessExitSeconds"] = "0"
            })
            .Build();

        var userStore = new UserStore(config, new ClaudeHomeServer.Tests.Helpers.FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        _userStore = userStore;
        var appSettings = new AppSettingsService(config);
        _appSettings = appSettings;
        _projectManager = new ProjectManager(config, userStore, appSettings);
        _historyService = new ChatHistoryService(config);

        // Локальная обёртка TestSessionBroadcaster → _sentMessages: писали через мок IHubContext
        // и теперь пишем через единый broadcaster — assertions Sent<T>/ClearSent сохранены как
        // единый API всех тестов файла.
        var testBroadcaster = new TestSessionBroadcaster();
        _broadcaster = new TrackingBroadcaster(testBroadcaster, _sentMessages, _sentMessagesLock);

        var llmProviders = new ClaudeHomeServer.Services.Llm.LlmProviderRegistry(config);
        var subPool = new ClaudeSubscriptionPool(config);
        _subPool = subPool;
        var adapters = new ClaudeHomeServer.Services.Llm.LlmSessionAdapterFactory(
            config, new AgentPromptSourceAdapter(new SkillsService()), new WorkspaceDatasetLookup(new WorkspaceKnowledgeStore(config)), llmProviders, subPool);
        var falCost = new FalCostService(new Mock<IHttpClientFactory>().Object, config);
        _usage = new UsageService(config);
        _activity = new SubscriptionActivityTracker();
        var jwt = new JwtService(config, userStore, NullLogger<JwtService>.Instance);
        var server = new Mock<Microsoft.AspNetCore.Hosting.Server.IServer>();
        server.Setup(s => s.Features).Returns(new Microsoft.AspNetCore.Http.Features.FeatureCollection());
        var wkStore = new WorkspaceKnowledgeStore(config);
        var knowledge = new KnowledgeService(new Mock<IHttpClientFactory>().Object,
            Microsoft.Extensions.Options.Options.Create(new DifyOptions()), wkStore);
        var flags = new FeatureFlagService(userStore);
        var notesSvc = new NotesService(_projectManager, config, NullLogger<NotesService>.Instance);
        var notesKb = new NotesKnowledgeService(knowledge, notesSvc, userStore, config,
            NullLogger<NotesKnowledgeService>.Instance);
        var personas = new PersonaManager(config);
        _personaManager = personas;
        var bindings = new PersonaBindingsService(personas, _projectManager, wkStore,
            knowledge, new SkillsService(), userStore, config, NullLogger<PersonaBindingsService>.Instance, notes: notesSvc, notesKb: notesKb);
        var sandbox = new ClaudeHomeServer.Services.Execution.SandboxManager(config,
            NullLogger<ClaudeHomeServer.Services.Execution.SandboxManager>.Instance);
        _actionOverrides = new ClaudeHomeServer.Services.Llm.LocalActionOverridesStore(config);
        // С резолвером личных слотов — как в DI: слот в модель разворачивается по владельцу
        var assignments = new ClaudeHomeServer.Services.Llm.ModelAssignmentResolver(appSettings, _actionOverrides,
            new ClaudeHomeServer.Services.Llm.UserModelTierResolver(userStore, appSettings));
        // Планирование «Командной реализации» (Э2): раннер планировщика подставной —
        // ответ задаётся тестом через _plannerAnswer, сбой — через _plannerException
        _plannerStub = new StubCheapRunner(() =>
        {
            if (_plannerException is not null) throw _plannerException;
            return _plannerAnswer;
        });
        _teamPlanning = new TeamPlanningService(personas, _plannerStub);
        // Git — настоящий CLI: нужен привязке чата к существующему дереву (AttachWorktreeAsync
        // сверяет путь с «git worktree list»); остальные тесты его не трогают
        // Реестр задач подключён пустым: гейт паузы в ReportBlockerAsync теперь обязательный
        // и без реестра падает сразу (R-HOLD-I). Пустой реестр — то же поведение: задач нет,
        // паузы нет, пробуждения идут как раньше.
        // Реестр пустой: TaskManager — IDisposable с таймером дебаунса, и без гашения на
        // каждый экземпляр фиксации он утекал. Гасится в Dispose фикстуры (хвост R-N2,
        // минор 4): using var гасил его в конструкторе, а _sut держит ссылку дольше —
        // пригодность TaskManager после Dispose не документирована.
        _taskStore = new ClaudeHomeServer.Services.Tasks.TaskManager(config);
        _sut = new SessionManager(_projectManager, _historyService, config, adapters, falCost, _usage, appSettings, userStore, jwt, server.Object, llmProviders, flags, personas, bindings, subPool, NullLogger<SessionManager>.Instance, TestLauncherFactory.Instance, sandbox, git: new ClaudeHomeServer.Services.Git.GitService(TestLauncherFactory.Instance), assignments: assignments, teamPlanning: _teamPlanning, activity: _activity, subagentRuns: _subagentRuns, broadcaster: _broadcaster, graphInspector: _graphInspector, tasks: _taskStore);
    }

    public void Dispose()
    {
        // Реестр гасится ДО удаления каталога: Dispose досбрасывает несохранённое (Flush),
        // и запись tasks.json идёт в _tempDir — после удаления она бы упала или оставила
        // файлы в уже удаляемом каталоге.
        _taskStore.Dispose();
        // Запись history.json идёт из fire-and-forget обработчиков SessionManager: тест
        // доходит до Dispose раньше дописывания — отсюда «Directory not empty» на Linux и
        // занятый {path}.{guid}.tmp на Windows. Ретрай живёт в общем хелпере.
        Helpers.TestFs.DeleteDirectoryResilient(_tempDir);
    }

    protected string MkProjectDir(string suffix) =>
        Directory.CreateDirectory(Path.Combine(_tempDir, "proj_" + suffix)).FullName;

    // Ожидание условия с дедлайном (таймеры гоняют колбэки на thread pool).
    protected static void WaitUntil(Func<bool> condition, string what, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"условие не выполнилось за {timeout?.TotalSeconds ?? 10}с: {what}");
            System.Threading.Thread.Sleep(5);
        }
    }

    protected static double TimeMs(Action action)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        action();
        sw.Stop();
        return sw.Elapsed.TotalMilliseconds;
    }

    // Менеджер с включённым автосейвом (Session:AutoSaveSeconds=1) — дебаунс MarkRead активен.
    // Копия BuildSecondManager с тем же конфигом, кроме автосейва (1с) и задержки дебаунса (0,2с).
    protected SessionManager BuildDebounceManager()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_tempDir, "projects.json"),
                ["Session:AutoSaveSeconds"] = "1",
                ["Session:MarkReadDebounceSeconds"] = "0.2",
                ["DefaultProjectsPath"] = Path.Combine(_tempDir, "homes"),
                ["ClaudeUserProfileDir"] = Path.Combine(_tempDir, "claude-profile"),
            })
            .Build();
        var userStore = new UserStore(config, new ClaudeHomeServer.Tests.Helpers.FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        var appSettings = new AppSettingsService(config);
        var projectManager = new ProjectManager(config, userStore, appSettings);
        var historyService = new ChatHistoryService(config);

        var clients = new Mock<IHubClients>();
        var clientProxy = new Mock<IClientProxy>();
        clientProxy
            .Setup(c => c.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        clients.Setup(c => c.Group(It.IsAny<string>())).Returns(clientProxy.Object);

        var llmProviders = new ClaudeHomeServer.Services.Llm.LlmProviderRegistry(config);
        var subPool = new ClaudeSubscriptionPool(config);
        var adapters = new ClaudeHomeServer.Services.Llm.LlmSessionAdapterFactory(
            config, new AgentPromptSourceAdapter(new SkillsService()), new WorkspaceDatasetLookup(new WorkspaceKnowledgeStore(config)), llmProviders, subPool);
        var falCost = new FalCostService(new Mock<IHttpClientFactory>().Object, config);
        var usage = new UsageService(config);
        var jwt = new JwtService(config, userStore, NullLogger<JwtService>.Instance);
        var server = new Mock<Microsoft.AspNetCore.Hosting.Server.IServer>();
        server.Setup(s => s.Features).Returns(new Microsoft.AspNetCore.Http.Features.FeatureCollection());
        var wkStore = new WorkspaceKnowledgeStore(config);
        var knowledge = new KnowledgeService(new Mock<IHttpClientFactory>().Object,
            Microsoft.Extensions.Options.Options.Create(new DifyOptions()), wkStore);
        var flags = new FeatureFlagService(userStore);
        var notesSvc = new NotesService(projectManager, config, NullLogger<NotesService>.Instance);
        var notesKb = new NotesKnowledgeService(knowledge, notesSvc, userStore, config,
            NullLogger<NotesKnowledgeService>.Instance);
        var personas = new PersonaManager(config);
        var bindings = new PersonaBindingsService(personas, projectManager, wkStore,
            knowledge, new SkillsService(), userStore, config, NullLogger<PersonaBindingsService>.Instance, notes: notesSvc, notesKb: notesKb);
        var sandbox = new ClaudeHomeServer.Services.Execution.SandboxManager(config,
            NullLogger<ClaudeHomeServer.Services.Execution.SandboxManager>.Instance);

        return new SessionManager(projectManager, historyService, config, adapters, falCost,
            usage, appSettings, userStore, jwt, server.Object, llmProviders, flags, personas,
            bindings, subPool, NullLogger<SessionManager>.Instance,
            TestLauncherFactory.Instance, sandbox, broadcaster: new TestSessionBroadcaster());
    }

    // --- Смена провайдера в настройках чата: правило «есть ли что переносить» ---

    // Корень профиля CLI родного Claude в тестах: пул подписок пуст, оверрайда провайдера
    // нет — ходы идут в пользовательском профиле (ключ ClaudeUserProfileDir)
    protected string ClaudeProfileRoot => Path.Combine(_tempDir, "claude-profile");
    protected string GlmProfileRoot => Path.Combine(_tempDir, "claude-profiles", "glm");

    // Разложить транскрипт CLI в профиле по соглашению об уплощении cwd
    protected static void PutTranscript(string configRoot, string cwd, string csid, string content)
    {
        var dir = Directory.CreateDirectory(
            Path.Combine(configRoot, "projects", TranscriptMigrator.FlattenCwd(cwd))).FullName;
        File.WriteAllText(Path.Combine(dir, csid + ".jsonl"), content);
    }

    // --- Слоты тиров в чатах персон ---

    protected Persona MkPersona(string name, string? model, string projectId) =>
        _personaManager.Create(TestUserId, name, role: null, description: null, systemPrompt: null,
            model: model, effort: null, scope: PersonaScope.Project, projectId: projectId,
            color: null, greeting: null, memoryEnabled: false);

    // Второй SessionManager на том же DataPath — чтение стора после «перезапуска сервера».
    // Конструктор идентичен основному _sut (без teamPlanning/activity — те не участвуют
    // в загрузке sessions.json), автосейв выключен тем же конфигом
    protected SessionManager BuildSecondManager()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_tempDir, "projects.json"),
                ["Session:AutoSaveSeconds"] = "0",
                ["DefaultProjectsPath"] = Path.Combine(_tempDir, "homes"),
                ["ClaudeUserProfileDir"] = Path.Combine(_tempDir, "claude-profile"),
            })
            .Build();
        var userStore = new UserStore(config, new ClaudeHomeServer.Tests.Helpers.FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        var appSettings = new AppSettingsService(config);
        var projectManager = new ProjectManager(config, userStore, appSettings);
        var historyService = new ChatHistoryService(config);

        var clients = new Mock<IHubClients>();
        var clientProxy = new Mock<IClientProxy>();
        clientProxy
            .Setup(c => c.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        clients.Setup(c => c.Group(It.IsAny<string>())).Returns(clientProxy.Object);

        var llmProviders = new ClaudeHomeServer.Services.Llm.LlmProviderRegistry(config);
        var subPool = new ClaudeSubscriptionPool(config);
        var adapters = new ClaudeHomeServer.Services.Llm.LlmSessionAdapterFactory(
            config, new AgentPromptSourceAdapter(new SkillsService()), new WorkspaceDatasetLookup(new WorkspaceKnowledgeStore(config)), llmProviders, subPool);
        var falCost = new FalCostService(new Mock<IHttpClientFactory>().Object, config);
        var usage = new UsageService(config);
        var jwt = new JwtService(config, userStore, NullLogger<JwtService>.Instance);
        var server = new Mock<Microsoft.AspNetCore.Hosting.Server.IServer>();
        server.Setup(s => s.Features).Returns(new Microsoft.AspNetCore.Http.Features.FeatureCollection());
        var wkStore = new WorkspaceKnowledgeStore(config);
        var knowledge = new KnowledgeService(new Mock<IHttpClientFactory>().Object,
            Microsoft.Extensions.Options.Options.Create(new DifyOptions()), wkStore);
        var flags = new FeatureFlagService(userStore);
        var notesSvc = new NotesService(projectManager, config, NullLogger<NotesService>.Instance);
        var notesKb = new NotesKnowledgeService(knowledge, notesSvc, userStore, config,
            NullLogger<NotesKnowledgeService>.Instance);
        var personas = new PersonaManager(config);
        var bindings = new PersonaBindingsService(personas, projectManager, wkStore,
            knowledge, new SkillsService(), userStore, config, NullLogger<PersonaBindingsService>.Instance, notes: notesSvc, notesKb: notesKb);
        var sandbox = new ClaudeHomeServer.Services.Execution.SandboxManager(config,
            NullLogger<ClaudeHomeServer.Services.Execution.SandboxManager>.Instance);

        return new SessionManager(projectManager, historyService, config, adapters, falCost,
            usage, appSettings, userStore, jwt, server.Object, llmProviders, flags, personas,
            bindings, subPool, NullLogger<SessionManager>.Instance,
            TestLauncherFactory.Instance, sandbox, broadcaster: new TestSessionBroadcaster());
    }

    // --- Очередь сообщений занятой сессии (chats_send в идущий ход) ---

    // Сессия «занята»: статус выставляем напрямую — поднимать реальный ход claude.exe
    // в юнит-тесте нечем, а проверка занятости смотрит именно на Info.Status
    protected async Task<Session> MkBusySessionAsync(string suffix, SessionStatus status = SessionStatus.Working)
    {
        var dir = MkProjectDir("q_" + suffix);
        var project = _projectManager.Create("Q-" + suffix, dir, TestUserId, TestUsername);
        var session = await _sut.CreateAsync(project.Id, ClaudeMode.Auto);
        session.Status = status;
        return session;
    }

    // --- Отметка «Ход остановлен пользователем» в истории ---

    protected async Task<(Session Session, Mock<ILlmSessionAdapter> Adapter)> MkRunningTurnAsync(
        string suffix, SessionStatus status = SessionStatus.Working)
    {
        var session = await MkBusySessionAsync(suffix, status);
        session.Name = "есть имя";
        var entry = GetEntry(session.Id);
        var adapter = StubAdapter(entry);
        SetProcess(entry, adapter.Object);
        GetAccumulator(entry).OnUserMessage("сделай отчёт", []);
        GetAccumulator(entry).OnTextDelta("начинаю");
        return (session, adapter);
    }

    // --- Гейт протухших ответов и реанимация зависшего чата ---

    protected static void SetPendingInteraction(object entry, ServerMessage? msg) =>
        entry.GetType().GetField("PendingInteraction")!.SetValue(entry, msg);

    protected static object? GetProcess(object entry) =>
        entry.GetType().GetField("Process")!.GetValue(entry);

    // --- «Разрешать всегда»: список живёт на сессии, а не в памяти адаптера ---

    // Инструменты «всегда разрешать» из стора для сессии session (PascalCase, как _jsonOpts)
    protected async Task<List<string?>> StoredAutoAllowAsync(string sessionId)
    {
        using var doc = JsonDocument.Parse(
            await File.ReadAllTextAsync(Path.Combine(_tempDir, "sessions.json")));
        return doc.RootElement.EnumerateArray()
            .Single(e => e.GetProperty("Id").GetString() == sessionId)
            .GetProperty("AutoAllowTools").EnumerateArray().Select(e => e.GetString()).ToList();
    }

    // --- Разовая переадресация закреплённых моделей GLM (миграция каталога провайдера) ---

    protected IConfiguration GlmMigrationConfig() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(_tempDir, "projects.json"),
        }).Build();

    protected GlmModelAliasMigration NewGlmMigration()
    {
        var config = GlmMigrationConfig();
        return new GlmModelAliasMigration(_sut,
            ClaudeHomeServer.Tests.Helpers.TestSpecialtyStore.Create(config),
            config, NullLogger<GlmModelAliasMigration>.Instance);
    }

    protected async Task<Session> MkSessionWithModelAsync(string suffix, string? model)
    {
        var dir = MkProjectDir("glm_" + suffix);
        var project = _projectManager.Create("GLM-" + suffix, dir, TestUserId, TestUsername);
        var session = await _sut.CreateAsync(project.Id, ClaudeMode.Auto);
        session.Model = model;
        return session;
    }

    // --- Модели родного Claude храним семействами (opus/fable/sonnet/haiku) ---

    protected ClaudeModelFamilyMigration NewFamilyMigration()
    {
        var config = GlmMigrationConfig();
        return new ClaudeModelFamilyMigration(
            new ClaudeHomeServer.Services.Llm.LlmProviderRegistry(TestConfig.Build(new Dictionary<string, string?>
            {
                ["LlmProviders:glm:ApiKey"] = "sk-test",
                ["LlmProviders:glm:AnthropicBaseUrl"] = "https://glm.example.com",
                ["LlmProviders:glm:Models:0:Id"] = "glm-5.2",
            })),
            _sut, config, NullLogger<ClaudeModelFamilyMigration>.Instance,
            personas: _personaManager, appSettings: _appSettings, users: _userStore,
            localActions: _actionOverrides);
    }

    // Свободный чат с заглушкой процесса: SendMessageAsync идёт в SendDirectAsync (Started),
    // а не в очередь — паттерн «MkBusySessionAsync без занятости»
    protected async Task<Session> MkFreeSessionWithStubAsync(string suffix)
    {
        var session = await MkBusySessionAsync(suffix, SessionStatus.Active);
        session.Name = "есть имя"; // иначе фоновый уточнятор заголовка полезет в локальную модель
        var entry = GetEntry(session.Id);
        SetProcess(entry, StubAdapter(entry).Object);
        return session;
    }

    // --- Превью карточки чата (Session.LastMessage) ---

    // Свободный чат с подставным адаптером: ход идёт прямо в процесс (SendDirectAsync),
    // минуя очередь. sentText ловит текст, который реально уехал в CLI.
    protected async Task<(Session Session, object Entry, Func<string?> SentText)> MkPreviewChatAsync(string suffix)
    {
        var session = await MkBusySessionAsync(suffix, SessionStatus.Active); // Active = чат свободен
        session.Name = "есть имя"; // иначе фоновый уточнятор заголовка полезет в локальную модель
        var entry = GetEntry(session.Id);
        var adapter = StubAdapter(entry);
        string? sent = null;
        adapter.Setup(a => a.SendMessageAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<int>(), It.IsAny<bool>()))
            .Callback<string, IReadOnlyList<string>?, int, bool, string?>((t, _, _, _, _) => sent = t)
            .Returns(Task.CompletedTask);
        SetProcess(entry, adapter.Object);
        return (session, entry, () => sent);
    }

    protected static ClaudeHomeServer.Services.Llm.SubagentRunPassport PreviewBgRun() =>
        new("a1", "mark", "Волна 1: агент деплоя", "sess-1", "toolu_1",
            DateTime.UtcNow.AddMinutes(-8), DateTime.UtcNow, 495, 34, 90, 1, 146_000, 4000,
            "tool_use", true, "Bash", "claude-opus-5", 1024, 0, false, "bg_done", DateTime.UtcNow);

    // White-box: пользовательская вводная напрямую в очередь — моделирует «сообщение встало,
    // пока ход стримился» без побочных эффектов реальной постановки (interrupt/dispatchNow).
    protected static void EnqueueUser(object entry, string text)
    {
        var pending = (System.Collections.IList)entry.GetType().GetField("Pending")!.GetValue(entry)!;
        pending.Add(new SessionManager.QueuedMessage(Guid.NewGuid().ToString("N"), text, null, null,
            0, DateTime.UtcNow, Kind: SessionManager.PendingKind.User));
    }

    // Агентское сообщение напрямую в очередь — для сценариев drain (доклады персон ждут
    // конца цикла, разбираются по его выключении / концу хода).
    protected static void EnqueueAgent(object entry, string text)
    {
        var pending = (System.Collections.IList)entry.GetType().GetField("Pending")!.GetValue(entry)!;
        pending.Add(new SessionManager.QueuedMessage(Guid.NewGuid().ToString("N"), text, null, null,
            0, DateTime.UtcNow, Kind: SessionManager.PendingKind.Agent));
    }

    // --- Цикл «до готово»: явная остановка в ленту (B3/B5/B6 — ContinueWorkLoopAsync
    // ни разу не был покрыт тестами: ни лимит итераций, ни LoopTurnFailed, ни verifying) ---

    protected static async Task WaitForConditionAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(10);
        }
    }

    internal static TurnAccumulator GetAccumulator(object entry) =>
        (TurnAccumulator)entry.GetType().GetField("Accumulator")!.GetValue(entry)!;

    protected static void SetLoopTurnInFlight(object entry, bool value) =>
        entry.GetType().GetField("LoopTurnInFlight")!.SetValue(entry, value);

    // ─── Обрыв фонового сабагента: сквозной путь «паспорт → добивание» ────────────────

    protected static ClaudeHomeServer.Services.Llm.SubagentRunPassport CutBgPassport(
        string sessionId, string agentId = "a-cut", string finishedBy = "bg_done", bool truncated = true) =>
        new(agentId, "mark", "Волна 1", sessionId, "toolu_1",
            DateTime.UtcNow.AddMinutes(-5), DateTime.UtcNow, 300, 20, 40, 1, 120_000, 3000,
            truncated ? "tool_use" : "end_turn", truncated, "Bash", "claude-opus-5", 1024, 0, false,
            finishedBy, DateTime.UtcNow);

    // Сток паспортов — ровно тот делегат, что SessionManager отдаёт ватчеру сабагентов
    protected Action<ClaudeHomeServer.Services.Llm.SubagentRunPassport> SubagentSink(string sessionId) =>
        (Action<ClaudeHomeServer.Services.Llm.SubagentRunPassport>)typeof(SessionManager)
            .GetMethod("SubagentRunSinkFor", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(_sut, [sessionId])!;

    // --- Фиксы ревью Глеба: цикл «до готово» (Major 3, Minor 5/6) ---

    protected static bool GetLoopTurnInFlight(object entry) =>
        (bool)entry.GetType().GetField("LoopTurnInFlight")!.GetValue(entry)!;

    // --- Цикл «до готово» + делегирование: фаза ожидания и PendingKind.Report ---

    // Хелпер: имитация живой делегированной задачи через делегат HasLiveDelegatedTasks.
    // Вызывающий ставит его перед сценарием и сбрасывает в null в finally, чтобы не
    // утекал в соседние тесты — глобальное свойство экземпляра SessionManager.
    protected bool _liveDelegated;
    protected void WithLiveDelegated()
    {
        _liveDelegated = true;
        _sut.HasLiveDelegatedTasks = _ => _liveDelegated;
    }

    // Помощник: подменяет _waitingTickInterval на тестовое значение, чтобы тик проходил
    // немедленно (без ожидания реальных 5 минут). Через рефлексию — поле приватное.
    protected void SetWaitingTickInterval(TimeSpan value)
    {
        var f = typeof(SessionManager).GetField("_waitingTickInterval",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        f.SetValue(_sut, value);
    }

    // Помощник: подменяет _maxWaitingTicks на тестовое значение (чтобы не гонять 20 тиков).
    protected void SetMaxWaitingTicks(int value)
    {
        var f = typeof(SessionManager).GetField("_maxWaitingTicks",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        f.SetValue(_sut, value);
    }

    // Помощник: ставит WaitingSince в прошлое, чтобы тик увидел «прошло достаточно».
    protected void BackdateWaitingSince(string sessionId, TimeSpan ago)
    {
        var loop = _sut.GetById(sessionId)!.WorkLoop!;
        loop.WaitingSince = DateTime.UtcNow - ago;
    }

    // Буфер текста хода штаба (маркеры координатора) — под своим локом, как в SessionManager
    protected static string GetTeamTurnText(object entry)
    {
        var buffer = (System.Text.StringBuilder)entry.GetType().GetField("TeamTurnText")!.GetValue(entry)!;
        lock (entry.GetType().GetField("TeamTurnLock")!.GetValue(entry)!) return buffer.ToString();
    }

    // Этап 4 / шаг 1в: план вызова HandleTeamTurnEndAsync на SessionEntry с ключом по TurnSeq.
    // Хранилище LastTeamTurnEnds — private словарь, методы публичны, доступ через рефлексию
    // (тесты лежат в отдельном проекте). Значение TeamTurnEndCall вложенное и недоступно без
    // рефлексии, generic-каст в Dictionary<int, TValue> невозможен без MakeGenericType —
    // для проверок размера и наличия ключа хватает IDictionary non-generic.
    protected static System.Collections.IDictionary GetLastTeamTurnEnds(object entry) =>
        (System.Collections.IDictionary)entry.GetType()
            .GetField("LastTeamTurnEnds", BindingFlags.Public | BindingFlags.Instance)!
            .GetValue(entry)!;

    protected static int GetLastTurnSeq(object entry) =>
        (int)entry.GetType()
            .GetField("LastTurnSeq", BindingFlags.Public | BindingFlags.Instance)!.GetValue(entry)!;

    protected static void SetLastTurnSeq(object entry, int value) =>
        entry.GetType()
            .GetField("LastTurnSeq", BindingFlags.Public | BindingFlags.Instance)!.SetValue(entry, value);

    protected static void InvokeRecordTeamTurnEnd(object entry, int seq, string? text, bool failed, bool asked)
    {
        entry.GetType()
            .GetMethod("RecordTeamTurnEnd", BindingFlags.Public | BindingFlags.Instance)!
            .Invoke(entry, [seq, text, failed, asked]);
    }

    // TryTakeTeamTurnEnd — три out-параметра через массив args, чтобы попасть в обе
    // (string?, TeamTurnEndCall?) одной рефлексией. На выходе текст отдельно, call упакован
    // в структуру TeamTurnEndCall { Text, Failed, Asked }.
    protected static bool InvokeTryTakeTeamTurnEnd(object entry, int seq,
        out string? text, out bool failed, out bool asked)
    {
        var args = new object?[] { seq, null, null };
        var ok = (bool)entry.GetType()
            .GetMethod("TryTakeTeamTurnEnd", BindingFlags.Public | BindingFlags.Instance)!
            .Invoke(entry, args)!;
        text = (string?)args[1];
        var call = args[2];
        if (call is null) { failed = false; asked = false; return false; }
        failed = (bool)call.GetType().GetProperty("Failed")!.GetValue(call)!;
        asked = (bool)call.GetType().GetProperty("Asked")!.GetValue(call)!;
        return ok;
    }

    // Опубликовать TurnCompleted через шину того же SessionManager. В тестах шина создаётся
    // лениво в SessionManager (TurnEvents), подписчик HandleTeamTurnCompletedShim уже
    // зарегистрирован в конструкторе — публикация сразу идёт в него.
    protected static async Task PublishTurnCompletedAsync(
        SessionManager sut, string sessionId, int turnSeq, string outcome, string? errorClass = null)
    {
        var bus = (ClaudeHomeServer.Services.Turn.ITurnEventBus)typeof(SessionManager)
            .GetField("_turnEvents", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(sut)!;
        var turn = new ClaudeHomeServer.Services.Turn.TurnContext(
            sessionId, TestUserId, turnSeq, AgentDepth: 0, ProjectId: null);
        await bus.PublishAsync(new ClaudeHomeServer.Services.Turn.TurnCompleted(
            turn, outcome, errorClass));
    }

    // --- Этап 4 / шаг 1в: тесты проводки turn/completed для штаба ---
    // Хелперы выше (InvokeOnMessageAsync + PublishTurnCompletedAsync) эмулируют тот же путь,
    // что ходит прод: OnMessageAsync кладёт план в LastTeamTurnEnds при терминале хода,
    // шина turn/completed публикуется ОДИН раз на ход из finally FallbackLlmSessionAdapter,
    // подписчик HandleTeamTurnCompletedShim забирает план и асинхронно зовёт
    // HandleTeamTurnEndAsync.
    //
    // Все семь исходов из каталога turn/completed покрыты отдельным тестом:
    // success / failed / egress_down / local_down → штаб разбирает ход и публикует карточку;
    // interrupted / cancelled / crashed → штаб молчит (та же защита, что раньше стояла
    // в OnMessageAsync на кейсе «Стоп» и прерывания ради очереди — теперь живёт в
    // подписчике).

    // Хелпер: довести ход штаба до терминала и затем опубликовать turn/completed через шину.
    // План кладёт OnMessageAsync (текст дельты + result/error), шина сама публикуется
    // подписчиком через PublishTurnCompletedAsync.
    protected async Task DriveOneTeamTurnAsync(
        SessionManager sut, string sessionId, int turnSeq, string outcome,
        string deltaText, bool failed,
        TeamEscalationKind? expectKind = null)
    {
        // 1) Делаем ход «живым» — статус Working + LastTurnSeq, чтобы осушительный блок
        //    OnMessageAsync клал план в LastTeamTurnEnds. Условно — потому что в тестах мы
        //    идём мимо SessionStartedMessage: подписчик работает на той же шине, и без
        //    LastTurnSeq (отличного от 0) план не положен. GetEntry идёт через _sessions
        //    рефлексией — он находит сессию даже если публичный GetById отдаёт null (что бывает
        //    после SendMessageAsync, когда статус мигрирует через стоп-кран).
        var entry = GetEntry(sessionId);
        _sut.GetById(sessionId)!.Status = SessionStatus.Working;
        SetLastTurnSeq(entry, turnSeq);

        // 2) Текст дельты копится в TeamTurnText (тот же буфер, что в живой трансляции).
        await InvokeOnMessageAsync(sessionId, new TurnAccumulator(new List<StoredMessage>()),
            new TextDeltaMessage(deltaText));
        // 3) Терминал: result успех / result error / ErrorMessage — для штабного пути любой из
        //    них кладёт план в LastTeamTurnEnds под тем же TurnSeq.
        await InvokeOnMessageAsync(sessionId, new TurnAccumulator(new List<StoredMessage>()),
            failed
                ? (ServerMessage)new ErrorMessage("сбой хода", ExpectResultFollows: false)
                : new ResultMessage("success", 10, 1, null, null));
        // 4) Шина публикует turn/completed — подписчик забирает план и зовёт штаб. Перед вызовом
        //    ловим счётчик карточек, чтобы WaitForEscalationAsync увидел изменение.
        await PublishTurnCompletedAsync(sut, sessionId, turnSeq, outcome);

        if (expectKind is not null)
        {
            // Ждём именно доставки карточки, а не мгновенного снимка: HandleTeamTurnEndAsync
            // асинхронный (Task.Run), и публикация TeamEscalationMessage едет с задержкой.
            // Snapshot по сообщениям внутри await даёт false-positive, поэтому см. ниже.
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                var cards = Sent<TeamEscalationMessage>()
                    .Where(m => m.SessionId == sessionId && !m.Resolved).ToList();
                if (cards.Any(c => c.Kind == expectKind.Value.ToString())) return;
                await Task.Delay(30);
            }
        }
    }

    // M7: ходы в тестах завершаются прямым вызовом HandleTeamTurnEndAsync, минуя запуск
    // (SendDirectAsync/SendMessageAndWaitAsync), — флаг «вводная от человека» проставляем явно,
    // как это сделал бы запуск хода по сообщению человека.
    protected static void SetTeamTurnFromHuman(object entry, bool value) =>
        entry.GetType().GetField("TeamTurnFromHuman")!.SetValue(entry, value);

    // Баг b63fd8ea: метка подавления гарда живёт на SessionEntry. Для теста «дольше порога»
    // сдвигаем её в прошлое через рефлексию — ShouldSuppressAsyncAgentStallGuard её
    // потом прочитает и решит, что подавление истекло.
    protected static void SetAsyncAgentStallSince(object entry, DateTime? value) =>
        entry.GetType().GetField("AsyncAgentStallSince")!.SetValue(entry, value);

    // --- Этап 4 / шаг 2в: восстановление отсечек сторожа волн через шину turn/completed ---
    // Хелпер: штаб в стадии Wave, волна открыта, отсечки сторожа погашены (WaveStartedAt=null).
    // Это именно то состояние, которое погасил OnAskQuestionStabAsync: координатор задал
    // вопрос ASK посреди волны, сторож отключён, и теперь ход оборвался — старая ветка в
    // OnMessageAsync (9441) возвращала отсечки синхронно по ExitedMessage. Перенесли на шину.
    protected async Task<(Session Session, Persona Backend, Persona Frontend)> MakeStabInWaveWithPausedWatchdogAsync(
        string suffix)
    {
        var (session, backend, frontend) = await MakeTeamStabAsync(suffix);
        ((ITeamRunState)_sut).WithTeamState(session.Id, t =>
        {
            t.Stage = TeamImplementStage.Wave;
            t.WaveNumber = 1;
            t.ClosedWave = 0; // волна открыта
            t.WaveStartedAt = null; // отсечки погашены вопросом ASK
            return true;
        });
        return (session, backend, frontend);
    }

    // Чтение WaveStartedAt после шины — асинхронное (шина — fire-and-forget), ждём момент.
    protected static async Task<DateTimeOffset?> GetWaveStartedAtAsync(SessionManager sut, string sessionId,
        int attempts = 20)
    {
        for (var i = 0; i < attempts; i++)
        {
            var ti = sut.GetById(sessionId)?.TeamImplement;
            if (ti is not null && ti.WaveStartedAt is not null)
                return ti.WaveStartedAt;
            await Task.Delay(25);
        }
        return sut.GetById(sessionId)?.TeamImplement?.WaveStartedAt;
    }

    // Хелпер: довести штабный чат до стадии Wave и опубликовать turn/completed без полного
    // прохода через HandleTeamTurnEndAsync (на interrupted/crashed/cancelled шим не зовёт
    // HandleTeamTurnEndAsync — план в LastTeamTurnEnds не изымается). Для interrupted/crashed
    // достаточно установить LastTurnSeq, чтобы подписчик смог найти entry.
    protected async Task PublishTurnCompletedForWaveAsync(
        SessionManager sut, string sessionId, int turnSeq, string outcome)
    {
        var entry = GetEntry(sessionId);
        _sut.GetById(sessionId)!.Status = SessionStatus.Working;
        SetLastTurnSeq(entry, turnSeq);
        await PublishTurnCompletedAsync(sut, sessionId, turnSeq, outcome);
    }

    // Ждём именно доставку (SendMessageAsync мока): drain — fire-and-forget Task.Run,
    // а Invocations целиком не годятся — там уже лежат Interrupt/Info этого же сценария
    protected static async Task WaitForSendAsync(Mock<ILlmSessionAdapter> adapter, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (adapter.Invocations.Any(i => i.Method.Name == nameof(ILlmSessionAdapter.SendMessageAsync)))
                return;
            await Task.Delay(10);
        }
    }

    // --- Режим «Командная реализация»: каркас состояния ---

    // Чат-штаб под тесты каркаса режима: собеседник-координатор и второй профиль в команде
    // проекта. Без них включение отклоняет гард на входе (B2 приёмки).
    protected async Task<Session> MakeStabForModeAsync(string suffix, ClaudeMode mode = ClaudeMode.Auto)
    {
        var dir = MkProjectDir(suffix);
        var project = _projectManager.Create(suffix, dir, TestUserId, TestUsername);
        Persona Mk(string name, string role) =>
            _personaManager.Create(TestUserId, name, role, null, null, null, null,
                PersonaScope.Project, project.Id, null, null, memoryEnabled: false);

        var coordinator = Mk("Алекс " + suffix, "Тимлид");
        Mk("Денис " + suffix, "Backend-разработчик");
        return await _sut.CreateAsync(project.Id, mode, personaId: coordinator.Id);
    }

    // --- Э2: карточка плана в ленте и ответ по ней ---

    // Штаб с координатором и командой из двух профилей; возвращает (сессия, бэкендер, фронтендер)
    protected async Task<(Session Session, Persona Backend, Persona Frontend)> MakeTeamStabAsync(string suffix)
    {
        var dir = MkProjectDir(suffix);
        var project = _projectManager.Create(suffix, dir, TestUserId, TestUsername);
        Persona Mk(string name, string role, PersonaSpecialty spec = PersonaSpecialty.None) =>
            _personaManager.Create(TestUserId, name, role, null, null, null, null,
                PersonaScope.Project, project.Id, null, null, memoryEnabled: false, specialty: spec);

        var coordinator = Mk("Алекс", "Тимлид", PersonaSpecialty.Coordinator);
        var backend = Mk("Денис", "Backend-разработчик");
        var frontend = Mk("Кира", "Frontend-разработчик");

        var session = await _sut.CreateAsync(project.Id, ClaudeMode.Auto, personaId: coordinator.Id);
        await _sut.SetTeamImplementAsync(session.Id, enabled: true, userId: TestUserId);
        return (session, backend, frontend);
    }

    protected void SetPlannerAnswer(Persona backend, Persona frontend) => _plannerAnswer = $$"""
        {"summary":"Экспорт задач в CSV","subtasks":[
          {"title":"Эндпоинт экспорта","goal":"GET /api/tasks/export",
           "executorPersonaId":"{{backend.Id}}","executorRationale":"Серверная часть — его зона",
           "files":["backend/Controllers/TasksController.cs"],"wave":1,"doneCriteria":"отдаёт CSV"},
          {"title":"Кнопка «Экспорт»","goal":"Кнопка в тулбаре",
           "executorPersonaId":"{{frontend.Id}}","executorRationale":"UI — её зона",
           "files":["frontend/src/components/Toolbar.tsx"],"wave":2,"doneCriteria":"файл скачивается"}]}
        """;

    // Штаб «после рестарта сервера»: карточка плана лежит в истории на диске, а накопителя
    // хода у чата нет (он оживляется лениво, с первым ходом). Ровно это состояние встречает
    // человек, который вернулся к чату после перезапуска и жмёт «Запустить».
    protected async Task<(Session Session, TeamImplementPlan Plan)> MakeRestartedStabWithPlanAsync(string suffix)
    {
        var dir = MkProjectDir(suffix);
        var project = _projectManager.Create(suffix, dir, TestUserId, TestUsername);
        Persona Mk(string name, string role, PersonaSpecialty spec = PersonaSpecialty.None) =>
            _personaManager.Create(TestUserId, name, role, null, null, null, null,
                PersonaScope.Project, project.Id, null, null, memoryEnabled: false, specialty: spec);

        var coordinator = Mk("Алекс", "Тимлид", PersonaSpecialty.Coordinator);
        var backend = Mk("Денис", "Backend-разработчик");
        var frontend = Mk("Кира", "Frontend-разработчик");

        // resumeSessionId задаёт ClaudeSessionId — по нему история и пишется на диск
        var session = await _sut.CreateAsync(project.Id, ClaudeMode.Auto,
            resumeSessionId: "csid-" + suffix, personaId: coordinator.Id);
        await _sut.SetTeamImplementAsync(session.Id, enabled: true, userId: TestUserId);
        SetPlannerAnswer(backend, frontend);
        var (plan, reason) = await _sut.CreateTeamPlanAsync(session.Id, "Экспорт", TestUserId);
        reason.Should().BeNull();

        ClearAccumulator(GetEntry(session.Id));
        return (session, plan!);
    }

    protected static void ClearAccumulator(object entry) =>
        entry.GetType().GetField("Accumulator")!.SetValue(entry, null);

    // Подставной раннер планировщика: отдаёт заготовленный тестом ответ
    protected sealed class StubCheapRunner(Func<string> answer) : ClaudeHomeServer.Services.Llm.ICheapTextRunner
    {
        // Сколько раз звали планировщика и какой промпт пришёл последним (повтор по кнопке)
        public int Calls { get; private set; }
        public string LastPrompt { get; private set; } = "";
        // Калитка «долгого планировщика»: RunAsync ждёт её, имитируя живой вызов (потолок
        // 300 с), пока тест проверяет поведение конца хода на фоне работающего планирования.
        public Func<Task>? Gate;

        public bool UsesLocal(string actionKey) => false;
        public bool HasFreeRoute(string actionKey) => false;
        public bool HasRoute(string actionKey) => true;
        public string DescribeRoute(string actionKey, string? fallbackModel) => "claude";

        public async Task<string> RunAsync(string actionKey, string prompt, string? fallbackModel = null,
            string? ownerId = null, object? jsonFormat = null, CancellationToken ct = default)
        {
            Calls++;
            LastPrompt = prompt;
            if (Gate is not null) await Gate();
            return answer();
        }

        public Task<string?> RunLocalOrChosenAsync(string actionKey, string prompt, string? ownerId = null,
            object? jsonFormat = null, CancellationToken ct = default) => Task.FromResult<string?>(answer());

        public Task<ClaudeHomeServer.Services.Llm.OneShotResult> RunDetailedAsync(string actionKey,
            string prompt, string? fallbackModel = null, string? ownerId = null, TimeSpan? timeout = null,
            int? maxTokens = null, object? jsonFormat = null, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    // Доступ к приватному SessionEntry реестра _sessions (white-box: без него публичный API
    // не воспроизводит окно TOCTOU — все точки входа гейтят по статусу ДО EnqueuePendingAsync).
    protected object GetEntry(string sessionId)
    {
        var field = typeof(SessionManager).GetField("_sessions",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        var sessions = (System.Collections.IDictionary)field.GetValue(_sut)!;
        return sessions[sessionId]!;
    }

    protected static Mock<ILlmSessionAdapter> StubAdapter(object entry)
    {
        var adapter = new Mock<ILlmSessionAdapter>();
        var info = (Session)entry.GetType().GetField("Info")!.GetValue(entry)!;
        adapter.SetupGet(a => a.Info).Returns(info);
        // Подставной адаптер изображает занятый чат с идущим ходом: без этого «Стоп»
        // принял бы его за зависший и пошёл бы в реанимацию (SessionManager.Interrupt)
        adapter.SetupGet(a => a.HasLiveTurn).Returns(true);
        // Фолбэк-оркестрации нет (обычный адаптер) — dispatchNow-гейт по OrchestrationActive
        // не должен глушить форсированный разбор очереди. DIM без Setup в Moq возвращает
        // default, но явная настройка делает тест детерминированным и независимым от версии Moq.
        adapter.SetupGet(a => a.OrchestrationActive).Returns(false);
        adapter.Setup(a => a.SendMessageAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(),
            It.IsAny<int>(), It.IsAny<bool>())).Returns(Task.CompletedTask);
        return adapter;
    }

    // Подставной адаптер занимает место процесса вместе с идентификатором прогона: очередь
    // по exited разбирается только для прогона, чей ход прерывали (SessionEntry.DrainOnExitedRun),
    // поэтому в такие сценарии тот же runId уходит и в InvokeOnMessageAsync
    protected const long TestRunId = 1;

    protected static void SetProcess(object entry, ILlmSessionAdapter adapter, long runId = TestRunId)
    {
        entry.GetType().GetField("Process")!.SetValue(entry, adapter);
        entry.GetType().GetField("RunId")!.SetValue(entry, runId);
    }

    protected static void SetQueueFrozen(object entry, bool value) =>
        entry.GetType().GetField("QueueFrozen")!.SetValue(entry, value);

    protected async Task<SendAndWaitResult> InvokeEnqueuePendingAsync(string sessionId, object entry, string text)
    {
        var method = typeof(SessionManager).GetMethod("EnqueuePendingAsync",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        var task = (Task)method.Invoke(_sut,
        [
            sessionId, entry, text,
            /*senderPersonaId*/ null, /*senderOrigin*/ null, /*agentDepth*/ 0,
            /*silent*/ false, /*suppressTasksExecute*/ false, /*senderChatName*/ null,
            SessionManager.PendingKind.Agent, /*attachedPaths*/ null, /*mode*/ null,
            /*staffNote*/ null
        ])!;
        await task;
        return (SendAndWaitResult)task.GetType().GetProperty("Result")!.GetValue(task)!;
    }

    protected static async Task WaitForQueueAsync(SessionManager sut, string sessionId, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (sut.GetPending(sessionId).Count == 0) return;
            await Task.Delay(20);
        }
    }

    // DrainNextPendingAsync — fire-and-forget Task.Run: сообщение покидает очередь
    // (RemoveAt) ДО доставки (Process.SendMessageAsync). Поллим сам факт вызова мока,
    // а не пустоту очереди — иначе Verify ловит гонку «очередь пуста, мок ещё не позвали».
    protected static async Task WaitForAdapterCallAsync(Mock<ILlmSessionAdapter> adapter, TimeSpan timeout)
    {
        // Ждём именно SendMessageAsync, а не «любой invocation»: dispatchNow-гейт по
        // OrchestrationActive (П3) обращается к свойству адаптера раньше запуска drain —
        // этот вызов появляется в Invocations первым и фожно удовлетворял бы старому условию
        // Count>0, при этом SendMessageAsync ещё в пути (Task.Run drain). Гонка рвала Verify.
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (adapter.Invocations.Any(i => i.Method.Name == nameof(ILlmSessionAdapter.SendMessageAsync)))
                return;
            await Task.Delay(10);
        }
    }

    // --- Э4: бюджет-квота вместо запрета, карточки остановок, маркер эскалации ---

    // Стадия «волна» — предпосылка гейта запуска с Э7-фикса (Major №2): квота проверяет
    // не только бюджет, но и что план опубликован и подтверждён (единственное согласование).
    protected void SetWaveStage(string sessionId) =>
        ((ITeamRunState)_sut).WithTeamState(sessionId, t => { t.Stage = TeamImplementStage.Wave; return true; });

    // Запрос MCP-сервера от лица чата: заголовок X-Caller-Session-Id + sub владельца в JWT.
    // Так фильтр [DenyOnDelegatedTurn] видит запрос в бою (общий api() каждого сервера).
    protected Microsoft.AspNetCore.Mvc.Filters.ActionExecutingContext MakeMcpCallContext(string sessionId)
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions
            .AddSingleton(services, _sut);
        var http = new Microsoft.AspNetCore.Http.DefaultHttpContext
        {
            RequestServices = Microsoft.Extensions.DependencyInjection
                .ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(services),
        };
        http.Request.Headers[ClaudeHomeServer.Filters.DenyOnDelegatedTurnAttribute.CallerHeader] = sessionId;
        http.User = new System.Security.Claims.ClaimsPrincipal(
            new System.Security.Claims.ClaimsIdentity([new System.Security.Claims.Claim("sub", TestUserId)]));
        var actionContext = new Microsoft.AspNetCore.Mvc.ActionContext(http,
            new Microsoft.AspNetCore.Routing.RouteData(),
            new Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor());
        return new Microsoft.AspNetCore.Mvc.Filters.ActionExecutingContext(actionContext, [],
            new Dictionary<string, object?>(), controller: new object());
    }

    // Фильтр запуска задачи — тот самый атрибут, что стоит на TasksController.Execute:
    // копия настроек в тесте разъехалась бы с контроллером незаметно.
    protected static ClaudeHomeServer.Filters.DenyOnDelegatedTurnAttribute ExecuteFilter() =>
        (ClaudeHomeServer.Filters.DenyOnDelegatedTurnAttribute)Attribute.GetCustomAttribute(
            typeof(ClaudeHomeServer.Controllers.TasksController).GetMethod(
                nameof(ClaudeHomeServer.Controllers.TasksController.Execute))!,
            typeof(ClaudeHomeServer.Filters.DenyOnDelegatedTurnAttribute))!;

    // --- Цикл «до готово»: лимит Iteration на возвраты из ожидания (work-loop, ЕДИНСТВЕННЫЙ лимит) ---

    // Чат с включённым циклом «до готово» (work-loop-аналог MakeTeamStabAsync)
    protected async Task<Session> MakeWorkLoopChatAsync(string suffix)
    {
        var dir = MkProjectDir("wl-" + suffix);
        var project = _projectManager.Create("WL-" + suffix, dir, TestUserId, TestUsername);
        var session = await _sut.CreateAsync(project.Id, ClaudeMode.Auto);
        await _sut.SetWorkLoopAsync(session.Id, enabled: true, TestUserId);
        return _sut.GetById(session.Id)!;
    }

    // Штаб с планом и хуком раздачи: возвращает план и «раздано ли» по клику
    protected async Task<(Session Session, TeamImplementPlan Plan, Func<TeamImplementPlan?> Handed)>
        MakeStabWithPlanAndStarterAsync(string suffix)
    {
        var (session, backend, frontend) = await MakeTeamStabAsync(suffix);
        SetPlannerAnswer(backend, frontend);
        var (plan, reason) = await _sut.CreateTeamPlanAsync(session.Id, "Экспорт", TestUserId);
        reason.Should().BeNull();
        TeamImplementPlan? handed = null;
        _sut.TeamHandlers.WaveStarter = (s, p, _) =>
        {
            handed = p;
            // Как настоящая раздача (TeamWaveService.StartWaveCore): реально стартовавшая
            // волна сама переводит стадию — ответ на карточку её больше не форсирует
            s.TeamImplement!.Stage = TeamImplementStage.Wave;
            return Task.CompletedTask;
        };
        return (session, plan!, () => handed);
    }

    // --- Э5: непрерывный контур — ожидание вводной, классификация, добавочная волна ---

    // Штаб с уже пройденной итерацией: план утверждён и запущен, режим ждёт следующую вводную
    protected async Task<(Session Session, Persona Backend, Persona Frontend)> MakeIdleStabAsync(string suffix)
    {
        var (session, backend, frontend) = await MakeTeamStabAsync(suffix);
        SetPlannerAnswer(backend, frontend);
        var (plan, _) = await _sut.CreateTeamPlanAsync(session.Id, "Экспорт", TestUserId);
        await _sut.RespondTeamPlanAsync(session.Id, plan!.Id, TeamPlanDecision.Run, userId: TestUserId);
        // Волны отработали, проверка подведена координатором — стадия ожидания вводной
        await _sut.HandleTeamTurnEndAsync(session.Id, "итог: всё готово", failed: false);
        _sut.GetById(session.Id)!.TeamImplement!.Stage = TeamImplementStage.Checking;
        await _sut.HandleTeamTurnEndAsync(session.Id, "Итерация завершена: 2 задачи, проверка пройдена", failed: false);
        ClearSent();
        return (session, backend, frontend);
    }

    // Ответ планировщика по добавочной вводной: одна под-задача в одну волну
    protected void SetAdditionalPlannerAnswer(Persona backend) => _plannerAnswer = $$"""
        {"summary":"Экспорт в XLSX","subtasks":[
          {"title":"XLSX-выгрузка","goal":"Добавить формат xlsx",
           "executorPersonaId":"{{backend.Id}}","executorRationale":"Серверная часть — его зона",
           "files":["backend/Controllers/TasksController.cs"],"wave":1,"doneCriteria":"файл открывается"}]}
        """;

    protected async Task<IReadOnlyList<TeamEscalationMessage>> WaitForEscalationCardsAsync(
        string sessionId, int minCount, TimeSpan? timeout = null)
    {
        List<TeamEscalationMessage> Snapshot() =>
            Sent<TeamEscalationMessage>()
                .Where(m => m.SessionId == sessionId && !m.Resolved).ToList();

        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (DateTime.UtcNow < deadline)
        {
            if (Snapshot().Count >= minCount)
            {
                await Task.Delay(100); // даём догнать возможному второму (дублирующему) разбору
                return Snapshot();
            }
            await Task.Delay(30);
        }
        return Snapshot();
    }

    // --- Маркер молчания <no-reply/> (B4 доклада о завершении задачи) ---

    // Обычный чат — БЕЗ режима штаба: маркером молчания отвечает персона постановщика в своём
    // личном чате, и стрижка обязана работать там же, а не только в «Командной реализации».
    protected async Task<Session> MkPlainChatAsync(string suffix)
    {
        var dir = MkProjectDir("noreply_" + suffix);
        var project = _projectManager.Create("NR-" + suffix, dir, TestUserId, TestUsername);
        return await _sut.CreateAsync(project.Id, ClaudeMode.Auto);
    }

    // --- Э8: интервью, план-режим и перепланирование ---

    // Штаб сразу после вводной человека: стадия интервью, чат в план-режиме.
    // Ход не запускаем (Status=Working) — вводная встаёт в очередь, но итерацию открывает уже
    // сейчас, как и в тестах Э5.
    protected async Task<(Session Session, Persona Backend, Persona Frontend)> MakeInterviewStabAsync(
        string suffix, string request = "сделай экспорт задач в CSV")
    {
        var (session, backend, frontend) = await MakeTeamStabAsync(suffix);
        session.Status = SessionStatus.Working;
        await _sut.SendMessageAsync(session.Id, request, []);
        return (session, backend, frontend);
    }

    // --- «Изменить план» как решение по карточке (серверное перепланирование, прод 2026-08-04) ---

    // Ответ планировщика на пересборку по правке: одна под-задача, блок «что изменилось»
    protected void SetReplannedPlannerAnswer(Persona backend, string changes) => _plannerAnswer = $$"""
        {"summary":"Экспорт в CSV без ревью","intent":"Убираем ревью по просьбе человека.",
         "assumptions":[],"changes":["{{changes}}"],
         "subtasks":[{"title":"Выгрузка CSV","goal":"писать csv",
          "executorPersonaId":"{{backend.Id}}","executorRationale":"серверная часть",
          "files":["backend/Export.cs"],"wave":1,"doneCriteria":"файл открывается"}]}
        """;

    // Любая персона проекта для ответа планировщика (исполнитель не важен в этих тестах)
    protected Persona GetAnyPersona(Session session) =>
        _personaManager.GetForContext(TestUserId, session.ProjectId!).First();

    // --- ReportUpAsync: отчёт в родительский чат ---

    // Пара «родитель → ребёнок» в одном проекте (связь ставится ручной группировкой)
    protected async Task<(Session Parent, Session Child)> MkParentChildAsync(string suffix)
    {
        var dir = MkProjectDir("rep_" + suffix);
        var project = _projectManager.Create("REP-" + suffix, dir, TestUserId, TestUsername);
        var parent = await _sut.CreateAsync(project.Id, ClaudeMode.Auto, name: "Родитель");
        var child = await _sut.CreateAsync(project.Id, ClaudeMode.Auto, name: "Задача: починить билд");
        _sut.SetParent(child.Id, parent.Id, TestUserId);
        return (parent, child);
    }

    // --- Групповые чаты ---

    // Пользователь + проект + N проектных персон. Ведущая — проектная, чтобы
    // CreateGroupChatAsync шёл маршрутом проекта (чат вне проекта требует
    // DefaultProjectsPath, которого в тестовом конфиге нет).
    protected (User User, Project Project, List<Persona> Personas) MkGroupFixture(int count, string suffix)
    {
        var user = _userStore.Add("group-user-" + suffix, "pw-123456", "user");
        var dir = MkProjectDir("grp_" + suffix);
        var project = _projectManager.Create("GRP-" + suffix, dir, user.Id, user.Username);
        var personas = Enumerable.Range(1, count)
            .Select(i => _personaManager.Create(user.Id, $"Персона{i}", $"Роль{i}", null, null,
                model: null, effort: null, PersonaScope.Project, project.Id,
                color: null, greeting: null, memoryEnabled: false))
            .ToList();
        return (user, project, personas);
    }

    // --- Очередь отложенных сообщений переживает рестарт (pending.json) ---

    protected static object GetEntryOf(SessionManager manager, string sessionId)
    {
        var field = typeof(SessionManager).GetField("_sessions", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return ((System.Collections.IDictionary)field.GetValue(manager)!)[sessionId]!;
    }

    protected string PendingFilePath(string key) =>
        Path.Combine(_tempDir, "sessions", key, "pending.json");

    protected static void SetCurrentTurnSnapshot(object entry, string text, IReadOnlyList<string> attachedPaths, string? mode)
    {
        var field = entry.GetType().GetField("CurrentTurnSnapshot", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;
        var snapshotType = Nullable.GetUnderlyingType(field.FieldType) ?? field.FieldType;
        var snapshot = Activator.CreateInstance(snapshotType, text, attachedPaths, mode);
        field.SetValue(entry, snapshot);
    }

    protected async Task<IReadOnlyList<ComposerRestoreMessage>> WaitForComposerRestoresAsync(int count, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (DateTime.UtcNow < deadline)
        {
            var current = Sent<ComposerRestoreMessage>().ToList();
            if (current.Count >= count)
            {
                await Task.Delay(50);
                var after = Sent<ComposerRestoreMessage>().ToList();
                if (after.Count == current.Count)
                    return after;
            }
            await Task.Delay(50);
        }
        return Sent<ComposerRestoreMessage>().ToList();
    }

    // --- Привязка чата к УЖЕ существующему дереву (чат-исполнитель задачи с worktree) ---
    // Отличие от SetWorktreeAsync: дерево не создаётся и транскрипт не мигрирует — только
    // поля свежей сессии до первого хода, cwd подставит EnsureProcessAsync.

    // Репозиторий с одним коммитом и linked worktree на ветке wt/тест
    protected async Task<(Project Project, string Worktree)> MkRepoWithWorktreeAsync(string name)
    {
        var dir = MkProjectDir(name);
        var project = _projectManager.Create(name, dir, TestUserId, TestUsername);
        var git = new ClaudeHomeServer.Services.Git.GitService(TestLauncherFactory.Instance);
        await git.InitAsync(null, dir);
        // Личность коммиттера — локально в репе: на CI глобального git-конфига может не быть
        await RawGitAsync(dir, "config", "user.email", "test@test");
        await RawGitAsync(dir, "config", "user.name", "Тест");
        await File.WriteAllTextAsync(Path.Combine(dir, "a.txt"), "один\n");
        await git.StageAllAsync(null, dir);
        await git.CommitAsync(null, dir, "начальный коммит");
        var worktree = Path.Combine(_tempDir, "wt_" + name);
        await git.WorktreeAddAsync(null, dir, worktree, "wt/тест");
        return (project, worktree);
    }

    // Git-репо с одним коммитом (без worktree) для владельца ownerId: для тестов тумблера
    // SetWorktreeAsync, где дерево создаёт сам тумблер, а home владельца резолвится из стора
    protected async Task<Project> MkGitProjectAsync(string name, string ownerId, string ownerName)
    {
        var dir = MkProjectDir(name);
        var project = _projectManager.Create(name, dir, ownerId, ownerName);
        var git = new ClaudeHomeServer.Services.Git.GitService(TestLauncherFactory.Instance);
        await git.InitAsync(null, dir);
        await RawGitAsync(dir, "config", "user.email", "test@test");
        await RawGitAsync(dir, "config", "user.name", "Тест");
        await File.WriteAllTextAsync(Path.Combine(dir, "a.txt"), "один\n");
        await git.StageAllAsync(null, dir);
        await git.CommitAsync(null, dir, "начальный коммит");
        return project;
    }

    // Прямой git для арранжей (как в GitServiceTests)
    protected static async Task RawGitAsync(string root, params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git")
        {
            WorkingDirectory = root,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = System.Diagnostics.Process.Start(psi)!;
        await p.WaitForExitAsync();
    }

    // --- Живой ход: rate_limit_event пишет usage с source=turn и трогает activity tracker ---

    internal async Task InvokeOnMessageAsync(string sessionId, TurnAccumulator acc, ServerMessage msg,
        long runId = 0)
    {
        var method = typeof(SessionManager).GetMethod("OnMessageAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var task = (Task)method.Invoke(_sut, [sessionId, acc, msg, runId])!;
        await task;
    }

    // --- Per-persona рубильники MCP-серверов (Off-привязка type: tool, target: <ключ>) ---

    protected object? InvokePrivate(string name, params object?[] args)
    {
        var method = typeof(SessionManager).GetMethod(name,
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        return method.Invoke(_sut, args);
    }

    // Персона проекта + Off-привязки на перечисленные ключи (пусто — дефолтная персона)
    protected Persona MkGatedPersona(string projectId, string suffix, params string[] offKeys)
    {
        var persona = _personaManager.Create(TestUserId, "Гейт-" + suffix, role: null, description: null,
            systemPrompt: null, model: null, effort: null, scope: PersonaScope.Project,
            projectId: projectId, color: null, greeting: null, memoryEnabled: false);
        if (offKeys.Length == 0) return persona;
        return _personaManager.UpdateBindings(persona.Id, TestUserId,
            offKeys.Select(k => new PersonaBinding
            {
                Type = PersonaBindingType.Tool, Target = k, Mode = PersonaBindingMode.Off,
            }).ToList());
    }

    protected sealed class ArchitectureStubSubsystem : IAppSubsystem
    {
        public string Key => McpEndpoints.ArchitectureName;
        public string Title => "Архитектура (заглушка)";
        public void Register(Microsoft.Extensions.DependencyInjection.IServiceCollection services, IConfiguration config) { }
    }

    // --- Имя чата у командных механик: тема из JSON/строкового вызова вместо сырой обвязки ---

    protected static string MakeChatTitle(string text)
    {
        var method = typeof(SessionManager).GetMethod("MakeChatTitle",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        return (string)method.Invoke(null, [text])!;
    }

    // --- Фоновые действия идут по маршруту места, а не только «работает только на локали» ---

    // Подставной раннер для действий-«украшений»: response вызывается лениво в RunAsync,
    // поэтому может как отдать ответ, так и бросить исключение (эмуляция отказа исполнителя).
    protected sealed class StubTitleCheapRunner(bool usesLocal, Func<string> response) : ICheapTextRunner
    {
        public bool UsesLocal(string actionKey) => usesLocal;
        public bool HasFreeRoute(string actionKey) => false;
        public bool HasRoute(string actionKey) => usesLocal;
        public string DescribeRoute(string actionKey, string? fallbackModel) => "claude";

        public Task<string> RunAsync(string actionKey, string prompt, string? fallbackModel = null,
            string? ownerId = null, object? jsonFormat = null, CancellationToken ct = default) =>
            Task.FromResult(response());

        public Task<string?> RunLocalOrChosenAsync(string actionKey, string prompt, string? ownerId = null,
            object? jsonFormat = null, CancellationToken ct = default) => Task.FromResult<string?>(null);

        public Task<OneShotResult> RunDetailedAsync(string actionKey, string prompt, string? fallbackModel = null,
            string? ownerId = null, TimeSpan? timeout = null, int? maxTokens = null, object? jsonFormat = null,
            CancellationToken ct = default) => throw new NotSupportedException();
    }

    // Собирает изолированный PersonaAutomationService поверх уже готового _sut (тот же
    // SessionManager, что и у остальных тестов файла) — не дублирует его тяжёлую сборку.
    protected (PersonaAutomationService Service, AutomationStateStore State) BuildAutomationService(
        ICheapTextRunner cheap, [System.Runtime.CompilerServices.CallerMemberName] string suffix = "")
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_tempDir, "automation-" + suffix, "projects.json"),
            })
            .Build();
        var notifStore = new NotificationStore(config, NullLogger<NotificationStore>.Instance);
        var pushStore = new PushSubscriptionStore(config);
        var jwt = new JwtService(config, _userStore, NullLogger<JwtService>.Instance);
        var push = new PushService(config, pushStore, jwt, NullLogger<PushService>.Instance);
        var broadcaster = new TestSessionBroadcaster();
        var notif = new NotificationService(notifStore, broadcaster, push, _personaManager, _projectManager,
            NullLogger<NotificationService>.Instance);
        var state = new AutomationStateStore(config);
        var mentions = new MentionTriggerSource(_personaManager);
        var roots = new AutomationRootResolver(_projectManager, UserHomeResolver.WithoutOverrides(_appSettings));

        var service = new PersonaAutomationService(_personaManager, _sut, push, notif,
            state, mentions, _projectManager, _userStore, roots, Array.Empty<ITriggerSource>(),
            config, cheap, NullLogger<PersonaAutomationService>.Instance);
        return (service, state);
    }

    // --- P12/P15: sweep-terminus Active→Finished при потере exited прогона ---

    // Помощник: метку времени «ход завершён, ждём exited» — в прошлое за grace-окно.
    protected static void SetLastTurnEndedAt(object entry, DateTimeOffset? value) =>
        entry.GetType().GetField("LastTurnEndedAt")!.SetValue(entry, value);

    protected static DateTimeOffset? GetLastTurnEndedAt(object entry) =>
        (DateTimeOffset?)entry.GetType().GetField("LastTurnEndedAt")!.GetValue(entry);

    // Негативная проверка sweep: метка НЕ обнулена ↔ sweep не сработал (детерминированно, без
    // таймингов fire-and-forget ApplyStatus: обнуление идёт синхронно под PendingLock до Task.Run).
    protected static void AssertSweepSkipped(SessionManager sut, string sessionId, object entry)
    {
        GetLastTurnEndedAt(entry).Should().NotBeNull("sweep не должен был сработать");
        sut.GetById(sessionId)!.Status.Should().Be(SessionStatus.Active);
    }

    // --- P28: живой дочерний исполнитель (волна team-implement) держит координатора ---

    // Кандидат-координатор: ход отдан (Active, LastTurnEndedAt свежий от result), собственного
    // прогона нет (мёртвый адаптер). grace намеренно НЕ сдвигаем здесь: CreateAsync ребёнка
    // внутри зовёт SaveSessions, и если координатор уже кандидат с истёкшим grace, sweep успеет
    // закрыть его ДО того, как тест настроит ребёнку stub-адаптер. Сдвиг grace — на ответственности
    // теста, ПОСЛЕ полной настройки ребёнка.
    protected async Task<(Session session, object entry)> MkStuckCoordinatorAsync(string suffix)
    {
        var dir = MkProjectDir("p28c-" + suffix);
        var project = _projectManager.Create("P28C-" + suffix, dir, TestUserId, TestUsername);
        var session = await _sut.CreateAsync(project.Id, ClaudeMode.Auto);
        var entry = GetEntry(session.Id);
        var adapter = StubAdapter(entry);
        adapter.SetupGet(a => a.HasLiveTurn).Returns(false);
        adapter.SetupGet(a => a.HasPendingBg).Returns(false);
        adapter.SetupGet(a => a.IsContinuationInFlight).Returns(false);
        SetProcess(entry, adapter.Object);

        await InvokeOnMessageAsync(session.Id, new TurnAccumulator(new List<StoredMessage>()),
            new ResultMessage("success", 10, 1, null, null), TestRunId);
        return (session, entry);
    }

    // Ребёнок-исполнитель: своя сессия с TaskId, заданный статус и живость процесса. alive=false
    // изображает зависшего исполнителя (мёртвый процесс в рабочем статусе — клинч-страховка P30),
    // true — работающего.
    protected async Task<(Session session, object entry)> MkChildAsync(
        string suffix, SessionStatus status, bool alive)
    {
        var dir = MkProjectDir("p28h-" + suffix);
        var project = _projectManager.Create("P28H-" + suffix, dir, TestUserId, TestUsername);
        var session = await _sut.CreateAsync(project.Id, ClaudeMode.Auto,
            taskExecution: true, taskId: "t-" + suffix);
        var entry = GetEntry(session.Id);
        var adapter = StubAdapter(entry);
        adapter.SetupGet(a => a.HasLiveTurn).Returns(alive);
        adapter.SetupGet(a => a.HasPendingBg).Returns(false);
        adapter.SetupGet(a => a.IsContinuationInFlight).Returns(false);
        SetProcess(entry, adapter.Object);
        session.Status = status;
        return (session, entry);
    }

    // --- КР-наблюдаемость, этап 3: перезапуск зависшего хода (RestartStuckTurnAsync) ---

    // Адаптер с переключаемым «живым ходом»: Interrupt в моке процесс не убивает, поэтому
    // умирание изображаем флагом — так тест управляет моментом смерти процесса после kill.
    protected sealed class SwitchableLiveAdapter
    {
        public bool Alive = true;
        public readonly Mock<ILlmSessionAdapter> Mock = new();

        public SwitchableLiveAdapter(Session info)
        {
            Mock.SetupGet(a => a.Info).Returns(info);
            Mock.SetupGet(a => a.HasLiveTurn).Returns(() => Alive);
            Mock.SetupGet(a => a.HasQueuedTurn).Returns(false);
            Mock.Setup(a => a.SendMessageAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<int>(), It.IsAny<bool>())).Returns(Task.CompletedTask);
        }

        public ILlmSessionAdapter Object => Mock.Object;
    }

    // Транскрипт сессии в регистрируемом корне проб: TranscriptProbe ищет файл по
    // {root}/{flat-cwd}/{csid}.jsonl, а при промахе сканирует подпапки корня
    protected string WriteTranscript(string csid, string content)
    {
        var projDir = Path.Combine(_tempDir, "transcripts-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(projDir);
        var path = Path.Combine(projDir, csid + ".jsonl");
        File.WriteAllText(path, content);
        TranscriptRoots.AddAllowedRoot(_tempDir);
        return path;
    }

    // Штаб-сессия в форме главного сценария: режим включён, стадия «волна» с расходом
    // бюджета и версией плана, чат «занят» (Working) без живого прогона — зависший ход
    protected async Task<Session> MkStuckTeamSessionAsync(string suffix, string? claudeSessionId = null)
    {
        var (session, _, _) = await MakeTeamStabAsync(suffix);
        ((ITeamRunState)_sut).WithTeamState(session.Id, t =>
        {
            t.Stage = TeamImplementStage.Wave;
            t.WaveNumber = 1;
            t.PlannedWaves = 2;
            t.Budget.TasksUsed = 2;
            t.Budget.RunsUsed = 2;
            t.Budget.WavesUsed = 1;
            t.PlanVersion = 2;
            t.ApprovedPlanVersion = 2;
            return true;
        });
        var info = _sut.GetById(session.Id)!;
        info.Status = SessionStatus.Working;
        info.ClaudeSessionId = claudeSessionId;
        return info;
    }

    // «В чат снова можно писать» (доставка отложенной очереди и приём новых сообщений
    // после рестарта) проверяет TeamWaveRestartApiTests на фейк-фабрике адаптеров —
    // юнит-фикстура здесь настоящая LlmSessionAdapterFactory, и реальная отправка в ней
    // не гоняется (см. MakeRunningStabAsync в TeamWaveServiceTests).

    // Стерильный ITaskLookup для юнитов: id → TaskItem (SourceSessionId/Status из аргументов).
    // Заменяет прежний приём «Session.TaskSourceSessionResolver = ...» (статический резолвер,
    // гонка с параллельными TaskManager'ами). TaskManager в ctor SessionManager не пробрасывают
    // (DI-цикл), поэтому lookup подменяют через SessionManager.SetTaskLookupForTests.
    protected sealed class StubTasks : ITaskLookup
    {
        private readonly Dictionary<string, TaskItem> _byId;
        public StubTasks(params (string Id, string? SourceSessionId, TaskItemStatus Status)[] items)
        {
            _byId = items.ToDictionary(
                t => t.Id,
                t => new TaskItem { Id = t.Id, SourceSessionId = t.SourceSessionId, Status = t.Status });
        }
        public TaskItem? GetById(string id) => _byId.GetValueOrDefault(id);
    }

    // Задать «task → чат-источник» (и статус) для sweep/SetParent-тестов.
    protected void SetSutTasks(params (string TaskId, string? SourceSessionId, TaskItemStatus Status)[] items)
        => _sut.SetTaskLookupForTests(new StubTasks(items));

    // === Очередь сообщений сессии (SessionMessagePipeline) ===

    protected Session AddPipelineSession(bool executor = false)
    {
        var session = new Session { ProjectId = null, OwnerId = TestUserId, TaskId = executor ? "task-1" : null };
        _sut.AddSessionForTest(session);
        return session;
    }

}
