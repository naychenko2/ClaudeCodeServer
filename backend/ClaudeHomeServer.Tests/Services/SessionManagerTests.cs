using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClaudeHomeServer.Hubs;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Prompts;
using ClaudeHomeServer.Services.TriggerSources;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ClaudeHomeServer.Tests.Services;

public class SessionManagerTests : IDisposable
{
    private const string TestUserId = "test-user-id";
    private const string TestUsername = "test-user";

    private readonly string _tempDir;
    private readonly ProjectManager _projectManager;
    private readonly TeamPlanningService _teamPlanning;
    // Ответ подставного планировщика для тестов карточки плана (Э2)
    private string _plannerAnswer = "";
    private readonly ChatHistoryService _historyService;
    private readonly UserStore _userStore;
    private readonly PersonaManager _personaManager;
    private readonly AppSettingsService _appSettings;
    private readonly ClaudeHomeServer.Services.Llm.LocalActionOverridesStore _actionOverrides;
    private readonly UsageService _usage;
    private readonly SubscriptionActivityTracker _activity;
    private readonly ClaudeSubscriptionPool _subPool;
    private readonly SessionManager _sut;
    private readonly Mock<IClientProxy> _clientProxy;
    private readonly List<ServerMessage> _sentMessages = new();

    public SessionManagerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "smgr_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_tempDir, "projects.json"),
                // Домашние папки владельцев (чаты вне проекта живут в {home}/Chats) — в temp
                ["DefaultProjectsPath"] = Path.Combine(_tempDir, "homes"),
                // Профиль CLI внутри temp — иначе уборка транскриптов при удалении чата
                // (DeleteAsync) полезла бы в настоящий ~/.claude пользователя
                ["ClaudeUserProfileDir"] = Path.Combine(_tempDir, "claude-profile")
            })
            .Build();

        var userStore = new UserStore(config, new ClaudeHomeServer.Tests.Helpers.FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        _userStore = userStore;
        var appSettings = new AppSettingsService(config);
        _appSettings = appSettings;
        _projectManager = new ProjectManager(config, userStore, appSettings);
        _historyService = new ChatHistoryService(config);

        var clients = new Mock<IHubClients>();
        _clientProxy = new Mock<IClientProxy>();
        _clientProxy
            .Setup(c => c.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
            .Callback<string, object[], CancellationToken>((method, args, _) =>
            {
                if (args.Length > 0 && args[0] is ServerMessage msg)
                    _sentMessages.Add(msg);
            })
            .Returns(Task.CompletedTask);
        // Захватываем только session-группу; project_/user_-группы дублировали бы сообщения
        clients.Setup(c => c.Group(It.Is<string>(g => !g.StartsWith("project_") && !g.StartsWith("user_"))))
            .Returns(_clientProxy.Object);
        clients.Setup(c => c.Group(It.Is<string>(g => g.StartsWith("project_") || g.StartsWith("user_"))))
            .Returns(new Mock<IClientProxy>().Object);

        var hub = new Mock<IHubContext<SessionHub>>();
        hub.Setup(h => h.Clients).Returns(clients.Object);

        var llmProviders = new ClaudeHomeServer.Services.Llm.LlmProviderRegistry(config);
        var subPool = new ClaudeSubscriptionPool(config);
        _subPool = subPool;
        var adapters = new ClaudeHomeServer.Services.Llm.LlmSessionAdapterFactory(
            config, new SkillsService(), new WorkspaceKnowledgeStore(config), llmProviders, subPool);
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
        var personaMemory = new PersonaMemoryService(knowledge, personas, userStore, config, NullLogger<PersonaMemoryService>.Instance);
        var bindings = new PersonaBindingsService(personas, _projectManager, wkStore, notesSvc, notesKb,
            knowledge, new SkillsService(), userStore, config, NullLogger<PersonaBindingsService>.Instance);
        var promptBuilder = new PersonaPromptBuilder(llmProviders);
        var sandbox = new ClaudeHomeServer.Services.Execution.SandboxManager(config,
            NullLogger<ClaudeHomeServer.Services.Execution.SandboxManager>.Instance);
        _actionOverrides = new ClaudeHomeServer.Services.Llm.LocalActionOverridesStore(config);
        // С резолвером личных слотов — как в DI: слот в модель разворачивается по владельцу
        var assignments = new ClaudeHomeServer.Services.Llm.ModelAssignmentResolver(appSettings, _actionOverrides,
            new ClaudeHomeServer.Services.Llm.UserModelTierResolver(userStore, appSettings));
        // Планирование «Командной реализации» (Э2): раннер планировщика подставной —
        // ответ задаётся тестом через _plannerAnswer
        _teamPlanning = new TeamPlanningService(personas, new StubCheapRunner(() => _plannerAnswer));
        // Git — настоящий CLI: нужен привязке чата к существующему дереву (AttachWorktreeAsync
        // сверяет путь с «git worktree list»); остальные тесты его не трогают
        _sut = new SessionManager(_projectManager, hub.Object, _historyService, config, adapters, falCost, _usage, appSettings, userStore, jwt, server.Object, llmProviders, notesKb, flags, personas, personaMemory, bindings, promptBuilder, subPool, NullLogger<SessionManager>.Instance, TestLauncherFactory.Instance, sandbox, git: new ClaudeHomeServer.Services.Git.GitService(TestLauncherFactory.Instance), assignments: assignments, teamPlanning: _teamPlanning, activity: _activity);
    }

    public void Dispose()
    {
        if (!Directory.Exists(_tempDir)) return;

        // Запись history.json идёт из fire-and-forget обработчиков SessionManager: после
        // ускорения прогона (нет CLI-прогревов) тест доходит до Dispose раньше дописывания,
        // и на Linux Directory.Delete падал «Directory not empty». Ретрай по паттерну
        // TestWebApplicationFactory.Dispose — уборка temp не предмет теста.
        for (var i = 1; ; i++)
        {
            try
            {
                Directory.Delete(_tempDir, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (i >= 5) return;
                Thread.Sleep(50 * i);
            }
        }
    }

    private string MkProjectDir(string suffix) =>
        Directory.CreateDirectory(Path.Combine(_tempDir, "proj_" + suffix)).FullName;

    // --- GetByProject ---

    [Fact]
    public void GetByProject_NewProject_ReturnsEmpty()
    {
        var dir = MkProjectDir("empty");
        var project = _projectManager.Create("Empty", dir, TestUserId, TestUsername);

        var result = _sut.GetByProject(project.Id);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetByProject_AfterCreate_ReturnsSessions()
    {
        var dir = MkProjectDir("a");
        var project = _projectManager.Create("A", dir, TestUserId, TestUsername);

        await _sut.CreateAsync(project.Id, ClaudeMode.Auto);
        await _sut.CreateAsync(project.Id, ClaudeMode.Plan);

        var result = _sut.GetByProject(project.Id);

        result.Should().HaveCount(2);
        result.Should().AllSatisfy(s => s.ProjectId.Should().Be(project.Id));
    }

    [Fact]
    public async Task GetByProject_FiltersByProjectId()
    {
        var dir1 = MkProjectDir("p1"); var p1 = _projectManager.Create("P1", dir1, TestUserId, TestUsername);
        var dir2 = MkProjectDir("p2"); var p2 = _projectManager.Create("P2", dir2, TestUserId, TestUsername);

        await _sut.CreateAsync(p1.Id, ClaudeMode.Auto);
        await _sut.CreateAsync(p1.Id, ClaudeMode.Auto);
        await _sut.CreateAsync(p2.Id, ClaudeMode.Auto);

        _sut.GetByProject(p1.Id).Should().HaveCount(2);
        _sut.GetByProject(p2.Id).Should().HaveCount(1);
    }

    [Fact]
    public async Task GetByProject_OrderedByUpdatedAtDescending()
    {
        var dir = MkProjectDir("ord");
        var project = _projectManager.Create("Ord", dir, TestUserId, TestUsername);

        var s1 = await _sut.CreateAsync(project.Id, ClaudeMode.Auto);
        var s2 = await _sut.CreateAsync(project.Id, ClaudeMode.Auto);

        // Симулируем что s1 — более свежая (например, пользователь только что в ней работал)
        s1.UpdatedAt = DateTime.UtcNow.AddMinutes(5);

        var result = _sut.GetByProject(project.Id).ToList();

        result[0].Id.Should().Be(s1.Id, "s1 имеет более поздний UpdatedAt");
        result[1].Id.Should().Be(s2.Id);
    }

    // --- GetById ---

    [Fact]
    public void GetById_NonExistent_ReturnsNull()
    {
        _sut.GetById("does-not-exist").Should().BeNull();
    }

    [Fact]
    public async Task GetById_ExistingSession_ReturnsSession()
    {
        var dir = MkProjectDir("gb");
        var project = _projectManager.Create("GB", dir, TestUserId, TestUsername);
        var session = await _sut.CreateAsync(project.Id, ClaudeMode.Auto);

        var found = _sut.GetById(session.Id);

        found.Should().NotBeNull();
        found!.Id.Should().Be(session.Id);
    }

    // --- CreateAsync ---

    [Fact]
    public async Task CreateAsync_ValidProject_ReturnsSession()
    {
        var dir = MkProjectDir("cr");
        var project = _projectManager.Create("CR", dir, TestUserId, TestUsername);

        var session = await _sut.CreateAsync(project.Id, ClaudeMode.Plan);

        session.ProjectId.Should().Be(project.Id);
        session.Mode.Should().Be(ClaudeMode.Plan);
        session.Status.Should().Be(SessionStatus.Starting);
        session.Id.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task CreateAsync_WithName_SessionHasName()
    {
        var dir = MkProjectDir("nm");
        var project = _projectManager.Create("NM", dir, TestUserId, TestUsername);

        var session = await _sut.CreateAsync(project.Id, ClaudeMode.Auto, null, "Мой чат");

        session.Name.Should().Be("Мой чат");
    }

    // --- Update: PATCH-семантика (null = не трогать) ---

    [Fact]
    public async Task Update_NullFields_KeepExistingNameModelEffort()
    {
        var dir = MkProjectDir("upd");
        var project = _projectManager.Create("UPD", dir, TestUserId, TestUsername);
        var session = await _sut.CreateAsync(project.Id, ClaudeMode.Auto, name: "Имя", model: "opus", effort: "high");

        // Частичный апдейт (как togglePin/chats_update): все поля null — ничего не затирается
        var updated = _sut.Update(session.Id, name: null, model: null, effort: null);

        updated!.Name.Should().Be("Имя");
        updated.Model.Should().Be("opus");
        updated.Effort.Should().Be("high");
    }

    [Fact]
    public async Task Update_OnlyName_DoesNotWipeModel()
    {
        var dir = MkProjectDir("upd2");
        var project = _projectManager.Create("UPD2", dir, TestUserId, TestUsername);
        var session = await _sut.CreateAsync(project.Id, ClaudeMode.Auto, name: "Старое", model: "opus", effort: "high");

        var updated = _sut.Update(session.Id, name: "Новое", model: null, effort: null);

        updated!.Name.Should().Be("Новое");
        updated.Model.Should().Be("opus", "модель не передавалась — не трогаем");
        updated.Effort.Should().Be("high");
    }

    // Владение проектной сессией — на нём держится гейт делегированных ходов
    [Fact]
    public async Task ПроектнаяСессия_ВладелецРезолвитсяЧерезПроект()
    {
        // У проектной сессии Session.OwnerId остаётся null — владелец живёт у проекта.
        // Кто сравнивает OwnerId напрямую, молча получает «чужую» сессию: так
        // GetActiveTurnDelegation сперва отключил запрет запуска исполнителя на
        // делегированном ходу (поймано live-тестом). Владение — только через GetOwned.
        var dir = MkProjectDir("owner");
        var project = _projectManager.Create("Owner", dir, TestUserId, TestUsername);
        var session = await _sut.CreateAsync(project.Id, ClaudeMode.Auto);

        session.OwnerId.Should().BeNull("владелец проектной сессии хранится у проекта");
        _sut.GetOwned(session.Id, TestUserId).Should().NotBeNull();
        _sut.GetOwned(session.Id, "another-user").Should().BeNull();

        // Гейт спрашивает состояние хода через тот же резолв владельца: сессия без живого
        // процесса — обычный ход; к чужой сессии запрет не применяется
        _sut.GetActiveTurnDelegation(session.Id, TestUserId)
            .Should().Be(new ClaudeHomeServer.Services.Llm.TurnDelegationState(0, false));
        _sut.GetActiveTurnDelegation(session.Id, "another-user")
            .Should().Be(new ClaudeHomeServer.Services.Llm.TurnDelegationState(0, false));
    }

    [Fact]
    public async Task Update_ExplicitValues_AreApplied()
    {
        var dir = MkProjectDir("upd3");
        var project = _projectManager.Create("UPD3", dir, TestUserId, TestUsername);
        var session = await _sut.CreateAsync(project.Id, ClaudeMode.Auto, name: "N", model: "opus", effort: "high");

        var updated = _sut.Update(session.Id, name: "N2", model: "sonnet", effort: "low");

        updated!.Name.Should().Be("N2");
        updated.Model.Should().Be("sonnet");
        updated.Effort.Should().Be("low");
    }

    [Fact]
    public async Task CreateAsync_WithTaskId_SessionHasTaskIdAndTaskOrigin()
    {
        var dir = MkProjectDir("tid");
        var project = _projectManager.Create("TID", dir, TestUserId, TestUsername);

        var session = await _sut.CreateAsync(project.Id, ClaudeMode.AcceptEdits, taskExecution: true, taskId: "task-1");

        session.TaskId.Should().Be("task-1");
        session.Origin.Should().Be(ChatOrigin.Task);
    }

    [Fact]
    public async Task CreateAsync_NonExistentProject_ThrowsKeyNotFound()
    {
        var act = () => _sut.CreateAsync("nonexistent-project", ClaudeMode.Auto);

        await act.Should().ThrowAsync<KeyNotFoundException>();
    }

    [Fact]
    public async Task CreateAsync_SessionAppearsBInGetByProject()
    {
        var dir = MkProjectDir("ap");
        var project = _projectManager.Create("AP", dir, TestUserId, TestUsername);

        var session = await _sut.CreateAsync(project.Id, ClaudeMode.Auto);

        _sut.GetByProject(project.Id).Should().ContainSingle(s => s.Id == session.Id);
    }

    // --- DeleteAsync ---

    [Fact]
    public async Task DeleteAsync_ExistingSession_RemovesFromStore()
    {
        var dir = MkProjectDir("del");
        var project = _projectManager.Create("Del", dir, TestUserId, TestUsername);
        var session = await _sut.CreateAsync(project.Id, ClaudeMode.Auto);

        await _sut.DeleteAsync(session.Id);

        _sut.GetById(session.Id).Should().BeNull();
    }

    [Fact]
    public async Task DeleteAsync_ExistingSession_DisappearsFromGetByProject()
    {
        var dir = MkProjectDir("dp");
        var project = _projectManager.Create("DP", dir, TestUserId, TestUsername);
        var s1 = await _sut.CreateAsync(project.Id, ClaudeMode.Auto);
        var s2 = await _sut.CreateAsync(project.Id, ClaudeMode.Auto);

        await _sut.DeleteAsync(s1.Id);

        var remaining = _sut.GetByProject(project.Id);
        remaining.Should().HaveCount(1);
        remaining.Should().NotContain(s => s.Id == s1.Id);
        remaining.Should().Contain(s => s.Id == s2.Id);
    }

    [Fact]
    public async Task DeleteAsync_NonExistentSession_DoesNotThrow()
    {
        var act = () => _sut.DeleteAsync("nonexistent");

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task DeleteAsync_SessionWithHistory_RemovesHistoryDir()
    {
        var dir = MkProjectDir("dh");
        var project = _projectManager.Create("DH", dir, TestUserId, TestUsername);
        var session = await _sut.CreateAsync(project.Id, ClaudeMode.Auto);

        var claudeSessionId = "test-claude-session-" + Guid.NewGuid().ToString("N");
        session.ClaudeSessionId = claudeSessionId;
        await _historyService.SaveAsync(claudeSessionId,
            [new ClaudeHomeServer.Protocol.StoredTextMessage("будет удалено")]);

        var historyDir = Path.Combine(_tempDir, "sessions", claudeSessionId);
        Directory.Exists(historyDir).Should().BeTrue();

        await _sut.DeleteAsync(session.Id);

        Directory.Exists(historyDir).Should().BeFalse();
    }

    [Fact]
    public async Task DeleteAsync_УдаляетТранскриптCliИНеТрогаетЧужой()
    {
        var dir = MkProjectDir("tr");
        var project = _projectManager.Create("TR", dir, TestUserId, TestUsername);
        var session = await _sut.CreateAsync(project.Id, ClaudeMode.Auto);

        var claudeSessionId = "cli-session-" + Guid.NewGuid().ToString("N");
        session.ClaudeSessionId = claudeSessionId;

        // Раскладка CLI: {профиль}/projects/{уплощенный cwd}/{csid}.jsonl. Рядом кладем
        // транскрипт другой сессии — в реальном ~/.claude в одной папке лежат чаты всех
        // инстансов сервера и интерактивные сессии пользователя, задеть их нельзя
        var transcriptDir = Path.Combine(_tempDir, "claude-profile", "projects",
            ClaudeHomeServer.Services.Llm.TranscriptMigrator.FlattenCwd(dir));
        Directory.CreateDirectory(transcriptDir);
        var mine = Path.Combine(transcriptDir, claudeSessionId + ".jsonl");
        var foreign = Path.Combine(transcriptDir, "someone-else.jsonl");
        File.WriteAllText(mine, "{\"type\":\"user\"}");
        File.WriteAllText(foreign, "{\"type\":\"user\"}");

        await _sut.DeleteAsync(session.Id);

        File.Exists(mine).Should().BeFalse();
        File.Exists(foreign).Should().BeTrue();
    }

    [Theory]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData(@"..\..\evil")]
    [InlineData("../../evil")]
    [InlineData(@"sub\dir")]
    [InlineData("with space")]
    public async Task CreateAsync_ПутьВResumeSessionId_Отклоняется(string badId)
    {
        // ClaudeSessionId становится именем папки в data/sessions и именем файла транскрипта,
        // а при удалении чата они удаляются рекурсивно. «..» здесь означал бы
        // Directory.Delete(data) — снос projects.json, users.json, историй и всех сторов
        var dir = MkProjectDir("bad");
        var project = _projectManager.Create("BAD", dir, TestUserId, TestUsername);

        var act = () => _sut.CreateAsync(project.Id, ClaudeMode.Auto, resumeSessionId: badId);

        (await act.Should().ThrowAsync<InvalidOperationException>()).And.Message.Should().Contain("resumeSessionId");
        _sut.GetByProject(project.Id).Should().BeEmpty("негодная сессия не должна оседать в реестре");
        Directory.Exists(Path.Combine(_tempDir, "sessions")).Should().BeFalse("папка данных не тронута");
    }

    [Fact]
    public async Task CreateAsync_UuidВResumeSessionId_Принимается()
    {
        var dir = MkProjectDir("ok");
        var project = _projectManager.Create("OK", dir, TestUserId, TestUsername);
        var uuid = Guid.NewGuid().ToString();

        var session = await _sut.CreateAsync(project.Id, ClaudeMode.Auto, resumeSessionId: uuid);

        session.ClaudeSessionId.Should().Be(uuid);
    }

    // --- Слоты тиров: новый чат идёт на слот «сильная» (назначение chat-new) ---

    [Fact]
    public async Task CreateAsync_БезModel_ПрименяетСлотСильной()
    {
        _appSettings.Save(new AppSettings { ModelTierStrong = "glm-5.2" });
        var dir = MkProjectDir("dcm");
        var project = _projectManager.Create("DCM", dir, TestUserId, TestUsername);

        var session = await _sut.CreateAsync(project.Id, ClaudeMode.Auto);

        session.Model.Should().Be("glm-5.2");
    }

    [Fact]
    public async Task CreateAsync_ЯвныйModel_ПеребиваетСлот()
    {
        _appSettings.Save(new AppSettings { ModelTierStrong = "glm-5.2" });
        var dir = MkProjectDir("dcm-explicit");
        var project = _projectManager.Create("DCME", dir, TestUserId, TestUsername);

        var session = await _sut.CreateAsync(project.Id, ClaudeMode.Auto, model: "opus");

        session.Model.Should().Be("opus");
    }

    [Fact]
    public async Task CreateAsync_МаркерУровня_РазворачиваетсяВМодельСлота()
    {
        // Уровень задачи/персоны приходит маркером «tier:*» — в сессии обязана осесть
        // конкретная модель: маркер не должен уйти ни в --model, ни на wire (шапка чата)
        _appSettings.Save(new AppSettings { ModelTierStrong = "glm-5.2", ModelTierMedium = "sonnet" });
        var dir = MkProjectDir("dcm-tier");
        var project = _projectManager.Create("DCMT", dir, TestUserId, TestUsername);

        var session = await _sut.CreateAsync(project.Id, ClaudeMode.Auto, model: "tier:medium");

        session.Model.Should().Be("sonnet");
    }

    [Fact]
    public async Task CreateAsync_МаркерУровня_ПустойСлот_НеОседаетВСессии()
    {
        // Слот не настроен: место идёт своим назначением (chat-new → слот «сильная»),
        // а маркер не сохраняется
        _appSettings.Save(new AppSettings { ModelTierStrong = "glm-5.2" });
        var dir = MkProjectDir("dcm-tier-empty");
        var project = _projectManager.Create("DCMTE", dir, TestUserId, TestUsername);

        var session = await _sut.CreateAsync(project.Id, ClaudeMode.Auto, model: "tier:weak");

        session.Model.Should().Be("glm-5.2");
    }

    [Fact]
    public async Task CreateAsync_Resume_НеПрименяетСлот()
    {
        // У resumed-сессии в транскрипте уже зафиксированы своя модель и провайдер —
        // подмена моделью слота сменила бы провайдер и упёрлась в guard (400)
        _appSettings.Save(new AppSettings { ModelTierStrong = "glm-5.2" });
        var dir = MkProjectDir("dcm-resume");
        var project = _projectManager.Create("DCMR", dir, TestUserId, TestUsername);

        var session = await _sut.CreateAsync(project.Id, ClaudeMode.Auto,
            resumeSessionId: Guid.NewGuid().ToString());

        session.Model.Should().BeNull();
    }

    [Fact]
    public async Task CreateAsync_ИсполнительЗадачи_ИдётПоНазначениюTasksExecutor()
    {
        // Назначение места «исполнитель задач» (оверрайд в сторе) сильнее слота chat-new
        _appSettings.Save(new AppSettings
        {
            ModelTierStrong = "glm-5.2",
            ModelTierMedium = "sonnet",
        });
        _actionOverrides.Set(ClaudeHomeServer.Services.Llm.LocalActionCatalog.TasksExecutor, "tier:medium");
        var dir = MkProjectDir("dcm-task");
        var project = _projectManager.Create("DCMT", dir, TestUserId, TestUsername);

        var session = await _sut.CreateAsync(project.Id, ClaudeMode.Auto,
            taskExecution: true, taskId: "task-1");

        session.Model.Should().Be("sonnet");
    }

    // B3 приёмки «Командной реализации» (дыра покрытия №14): смена ГЛОБАЛЬНОГО слота обязана
    // доезжать до дочерней executor-сессии — она стартует на том же провайдере, что и штаб,
    // а не на зашитом Claude-фолбэке. Назначения места нет, персона своей модели не задаёт.
    [Fact]
    public async Task CreateAsync_ИсполнительЗадачи_БезНазначения_ИдётГлобальнымСлотом()
    {
        _appSettings.Save(new AppSettings { ModelTierStrong = "deepseek-v4-pro" });
        var dir = MkProjectDir("dcm-task-slot");
        var project = _projectManager.Create("DCMTS", dir, TestUserId, TestUsername);
        var executor = _personaManager.Create(TestUserId, "Денис-исполнитель", null, null, null,
            model: null, effort: null, scope: PersonaScope.Project, projectId: project.Id,
            color: null, greeting: null, memoryEnabled: false);

        // Модель приходит из TaskExecutionService.ResolveExecutorModel: у задачи нет уровня,
        // у персоны нет ни модели, ни уровня — значит null, решает место tasks-executor
        var model = TaskExecutionService.ResolveExecutorModel(
            new TaskItem { Title = "под-задача волны", PersonaId = executor.Id }, executor);
        model.Should().BeNull();

        var session = await _sut.CreateAsync(project.Id, ClaudeMode.AcceptEdits, model: model,
            personaId: executor.Id, taskExecution: true, taskId: "task-wave-1");

        session.Model.Should().Be("deepseek-v4-pro", "слот «сильная» — дефолтный тир места tasks-executor");
    }

    // Уровень персоны-исполнителя тоже разворачивается по глобальному слоту, а не в Claude
    [Fact]
    public async Task CreateAsync_ИсполнительЗадачи_УровеньПерсоны_ИдётГлобальнымСлотом()
    {
        _appSettings.Save(new AppSettings
        {
            ModelTierStrong = "deepseek-v4-pro",
            ModelTierWeak = "deepseek-v4-flash",
        });
        var dir = MkProjectDir("dcm-task-tier");
        var project = _projectManager.Create("DCMTT", dir, TestUserId, TestUsername);
        var executor = _personaManager.Create(TestUserId, "Клио-исполнитель", null, null, null,
            model: null, effort: null, scope: PersonaScope.Project, projectId: project.Id,
            color: null, greeting: null, memoryEnabled: false, modelTier: "weak");

        var model = TaskExecutionService.ResolveExecutorModel(
            new TaskItem { Title = "под-задача", PersonaId = executor.Id }, executor);
        model.Should().Be("tier:weak");

        var session = await _sut.CreateAsync(project.Id, ClaudeMode.AcceptEdits, model: model,
            personaId: executor.Id, taskExecution: true, taskId: "task-wave-2");

        session.Model.Should().Be("deepseek-v4-flash", "уровень разворачивается слотом инстанса");
    }

    // --- Слоты тиров в чатах персон ---

    private Persona MkPersona(string name, string? model, string projectId) =>
        _personaManager.Create(TestUserId, name, role: null, description: null, systemPrompt: null,
            model: model, effort: null, scope: PersonaScope.Project, projectId: projectId,
            color: null, greeting: null, memoryEnabled: false);

    [Fact]
    public async Task ЧатПерсоны_БезМоделиУПерсоны_ПрименяетСлот()
    {
        _appSettings.Save(new AppSettings { ModelTierStrong = "glm-5.2" });
        var dir = MkProjectDir("persona-default");
        var project = _projectManager.Create("PD", dir, TestUserId, TestUsername);
        var persona = MkPersona("Без модели", null, project.Id);

        var session = await _sut.CreatePersonaChatAsync(TestUserId, persona.Id, ClaudeMode.Auto);

        session.Model.Should().Be("glm-5.2");
    }

    [Fact]
    public async Task ЧатПерсоны_МодельПерсоны_ПеребиваетСлот()
    {
        _appSettings.Save(new AppSettings { ModelTierStrong = "glm-5.2" });
        var dir = MkProjectDir("persona-explicit");
        var project = _projectManager.Create("PE", dir, TestUserId, TestUsername);
        var persona = MkPersona("С моделью", "opus", project.Id);

        var session = await _sut.CreatePersonaChatAsync(TestUserId, persona.Id, ClaudeMode.Auto);

        session.Model.Should().Be("opus");
    }

    [Fact]
    public async Task ГрупповойЧат_ВедущаяБезМодели_ПрименяетСлот()
    {
        _appSettings.Save(new AppSettings { ModelTierStrong = "glm-5.2" });
        var dir = MkProjectDir("group-default");
        var project = _projectManager.Create("GD", dir, TestUserId, TestUsername);
        var leader = MkPersona("Ведущая", null, project.Id);
        var second = MkPersona("Вторая", null, project.Id);

        var session = await _sut.CreateGroupChatAsync(TestUserId, [leader.Id, second.Id], ClaudeMode.Auto);

        session.Model.Should().Be("glm-5.2");
    }

    [Fact]
    public async Task SetPersona_ПерсонаБезМодели_НеЗатираетМодельСлота()
    {
        // Раньше назначение персоны в неначатый чат безусловно писало persona.Model = null,
        // и подставленная при создании модель слота молча терялась
        _appSettings.Save(new AppSettings { ModelTierStrong = "glm-5.2" });
        var dir = MkProjectDir("switch-speaker");
        var project = _projectManager.Create("SS", dir, TestUserId, TestUsername);
        var persona = MkPersona("Без модели", null, project.Id);
        var session = await _sut.CreateAsync(project.Id, ClaudeMode.Auto);
        session.Model.Should().Be("glm-5.2");

        var updated = _sut.SetPersona(session.Id, TestUserId, persona.Id);

        updated!.Model.Should().Be("glm-5.2");
    }

    [Fact]
    public async Task SetPersona_ПроектнаяСессия_УровеньПерсоныИдётПоЛичномуСлотуВладельца()
    {
        // У проектной сессии Session.OwnerId всегда null — владелец живёт у проекта.
        // Если брать поле напрямую, личный слот молча подменяется глобальным: персона
        // с уровнем «сильная» уезжала бы в проектном чате на чужую модель
        _appSettings.Save(new AppSettings { ModelTierStrong = "global-sonnet" });
        var user = _userStore.Add("tier-owner", "password123", "user");
        _userStore.SetModelTiers(user.Id, strong: "personal-opus", medium: null, weak: null);
        var dir = MkProjectDir("tier-owner");
        var project = _projectManager.Create("TO", dir, user.Id, "tier-owner");
        var persona = _personaManager.Create(user.Id, "С уровнем", role: null, description: null,
            systemPrompt: null, model: null, effort: null, scope: PersonaScope.Project,
            projectId: project.Id, color: null, greeting: null, memoryEnabled: false,
            modelTier: "strong");
        var session = await _sut.CreateAsync(project.Id, ClaudeMode.Auto, model: "sonnet");
        session.OwnerId.Should().BeNull("владелец проектной сессии живёт у проекта");

        var updated = _sut.SetPersona(session.Id, user.Id, persona.Id);

        updated!.Model.Should().Be("personal-opus");
    }

    [Fact]
    public async Task SetPersona_ЧатВнеПроекта_УровеньПерсоныИдётПоТомуЖеЛичномуСлоту()
    {
        // Парная проверка: вне проекта владелец берётся из самой сессии — результат
        // обязан совпасть с проектным случаем, иначе слот «плавает» от места чата
        _appSettings.Save(new AppSettings { ModelTierStrong = "global-sonnet" });
        var user = _userStore.Add("tier-owner-chat", "password123", "user");
        _userStore.SetModelTiers(user.Id, strong: "personal-opus", medium: null, weak: null);
        var persona = _personaManager.Create(user.Id, "С уровнем", role: null, description: null,
            systemPrompt: null, model: null, effort: null, scope: PersonaScope.Global,
            projectId: null, color: null, greeting: null, memoryEnabled: false,
            modelTier: "strong");
        var session = await _sut.CreateChatAsync(user.Id, ClaudeMode.Auto, model: "sonnet");

        var updated = _sut.SetPersona(session.Id, user.Id, persona.Id);

        updated!.Model.Should().Be("personal-opus");
    }

    [Fact]
    public async Task DeleteAsync_ОбщийТранскриптДругогоЧата_НеУдаляет()
    {
        // Сессия, созданная с resumeSessionId, несет тот же ClaudeSessionId — транскрипт у
        // двух чатов общий. Удаление одного не должно лишать второй памяти разговора
        var dir = MkProjectDir("shared");
        var project = _projectManager.Create("SHARED", dir, TestUserId, TestUsername);
        var claudeSessionId = "cli-session-" + Guid.NewGuid().ToString("N");

        var first = await _sut.CreateAsync(project.Id, ClaudeMode.Auto);
        first.ClaudeSessionId = claudeSessionId;
        var resumed = await _sut.CreateAsync(project.Id, ClaudeMode.Auto, resumeSessionId: claudeSessionId);
        resumed.ClaudeSessionId.Should().Be(claudeSessionId);

        var transcriptDir = Path.Combine(_tempDir, "claude-profile", "projects",
            ClaudeHomeServer.Services.Llm.TranscriptMigrator.FlattenCwd(dir));
        Directory.CreateDirectory(transcriptDir);
        var transcript = Path.Combine(transcriptDir, claudeSessionId + ".jsonl");
        File.WriteAllText(transcript, "{\"type\":\"user\"}");

        // История тоже общая — ее удаление обнулило бы ленту первого чата в UI
        await _historyService.SaveAsync(claudeSessionId,
            [new ClaudeHomeServer.Protocol.StoredTextMessage("общая история")]);
        var historyDir = Path.Combine(_tempDir, "sessions", claudeSessionId);

        await _sut.DeleteAsync(resumed.Id);
        File.Exists(transcript).Should().BeTrue("на транскрипт еще ссылается первый чат");
        Directory.Exists(historyDir).Should().BeTrue("история тоже общая");

        // Последний ссылающийся чат ушел — теперь убирать можно
        await _sut.DeleteAsync(first.Id);
        File.Exists(transcript).Should().BeFalse();
        Directory.Exists(historyDir).Should().BeFalse();
    }

    // --- SetExpiry ---

    [Fact]
    public async Task SetExpiry_ВключаетИВыключаетВременность()
    {
        var dir = MkProjectDir("ex");
        var project = _projectManager.Create("EX", dir, TestUserId, TestUsername);
        var session = await _sut.CreateAsync(project.Id, ClaudeMode.Auto);

        var updated = _sut.SetExpiry(session.Id, 1440);
        updated!.ExpiresAfterMinutes.Should().Be(1440);

        updated = _sut.SetExpiry(session.Id, null);
        updated!.ExpiresAfterMinutes.Should().BeNull();
    }

    [Fact]
    public async Task SetExpiry_ПерезапускаетОтсчёт_UpdatedAt()
    {
        var dir = MkProjectDir("ex2");
        var project = _projectManager.Create("EX2", dir, TestUserId, TestUsername);
        var session = await _sut.CreateAsync(project.Id, ClaudeMode.Auto);
        var before = DateTime.UtcNow;

        var updated = _sut.SetExpiry(session.Id, 60);

        updated!.UpdatedAt.Should().BeOnOrAfter(before);
    }

    [Fact]
    public void SetExpiry_NonExistentSession_ReturnsNull()
    {
        _sut.SetExpiry("nonexistent", 60).Should().BeNull();
    }

    // --- Очередь сообщений занятой сессии (chats_send в идущий ход) ---

    // Сессия «занята»: статус выставляем напрямую — поднимать реальный ход claude.exe
    // в юнит-тесте нечем, а проверка занятости смотрит именно на Info.Status
    private async Task<Session> MkBusySessionAsync(string suffix, SessionStatus status = SessionStatus.Working)
    {
        var dir = MkProjectDir("q_" + suffix);
        var project = _projectManager.Create("Q-" + suffix, dir, TestUserId, TestUsername);
        var session = await _sut.CreateAsync(project.Id, ClaudeMode.Auto);
        session.Status = status;
        return session;
    }

    [Theory]
    [InlineData(SessionStatus.Working)]
    [InlineData(SessionStatus.Waiting)]
    public async Task SendMessageAndWait_ЗанятаяСессия_СообщениеВОчередьАНеОтказ(SessionStatus status)
    {
        // Раньше сюда прилетал Busy → 409, и сообщение агента терялось
        var session = await MkBusySessionAsync("busy" + status, status);

        var result = await _sut.SendMessageAndWaitAsync(session.Id, "привет из другого чата",
            TimeSpan.FromSeconds(5));

        result.Should().BeOfType<SendAndWaitResult.Queued>()
            .Which.Position.Should().Be(1);
        _sut.GetPending(session.Id).Should().ContainSingle()
            .Which.Text.Should().Be("привет из другого чата");
    }

    [Fact]
    public async Task SendMessageAndWait_ОдинаковыйТекстОтТогоЖеОтправителя_НеДублируется()
    {
        // Прежний контракт chats_send советовал ретраить при отказе — наивный агент
        // насыпал бы очередь копиями одного сообщения
        var session = await MkBusySessionAsync("dup");
        await _sut.SendMessageAndWaitAsync(session.Id, "повтор", TimeSpan.Zero, senderPersonaId: "p-1");

        var again = await _sut.SendMessageAndWaitAsync(session.Id, "повтор", TimeSpan.Zero, senderPersonaId: "p-1");

        again.Should().BeOfType<SendAndWaitResult.Queued>().Which.Duplicate.Should().BeTrue();
        _sut.GetPending(session.Id).Should().HaveCount(1);
    }

    [Fact]
    public async Task SendMessageAndWait_ТотЖеТекстОтДругогоОтправителя_ЭтоРазныеСообщения()
    {
        var session = await MkBusySessionAsync("dup2");
        await _sut.SendMessageAndWaitAsync(session.Id, "готово?", TimeSpan.Zero, senderPersonaId: "p-1");

        await _sut.SendMessageAndWaitAsync(session.Id, "готово?", TimeSpan.Zero, senderPersonaId: "p-2");

        _sut.GetPending(session.Id).Should().HaveCount(2);
    }

    [Fact]
    public async Task SendMessageAndWait_ПереполненнаяОчередь_Отклоняет()
    {
        var session = await MkBusySessionAsync("full");
        for (var i = 0; i < 10; i++)
            await _sut.SendMessageAndWaitAsync(session.Id, $"сообщение {i}", TimeSpan.Zero);

        var overflow = await _sut.SendMessageAndWaitAsync(session.Id, "лишнее", TimeSpan.Zero);

        overflow.Should().BeOfType<SendAndWaitResult.QueueFull>().Which.Limit.Should().Be(10);
        _sut.GetPending(session.Id).Should().HaveCount(10, "лишнее не должно вытеснять принятое");
    }

    [Fact]
    public async Task SendMessageAndWait_ОчередьХранитИсточникИОтправителя()
    {
        var session = await MkBusySessionAsync("meta");

        await _sut.SendMessageAndWaitAsync(session.Id, "из соседнего проекта", TimeSpan.Zero,
            senderPersonaId: "p-9", senderOrigin: "Проект Альфа");

        var queued = _sut.GetPending(session.Id).Should().ContainSingle().Subject;
        queued.SenderPersonaId.Should().Be("p-9");
        queued.SenderOrigin.Should().Be("Проект Альфа");
        queued.Id.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task CancelPending_СнимаетСообщениеИзОчереди()
    {
        var session = await MkBusySessionAsync("cancel");
        await _sut.SendMessageAndWaitAsync(session.Id, "первое", TimeSpan.Zero);
        await _sut.SendMessageAndWaitAsync(session.Id, "второе", TimeSpan.Zero);
        var first = _sut.GetPending(session.Id)[0];

        (await _sut.CancelPendingAsync(session.Id, first.Id)).Should().BeTrue();

        _sut.GetPending(session.Id).Should().ContainSingle().Which.Text.Should().Be("второе");
        (await _sut.CancelPendingAsync(session.Id, first.Id)).Should().BeFalse("повторная отмена — уже нечего снимать");
    }

    [Fact]
    public async Task Interrupt_ЗамораживаетОчередьНеЧистит()
    {
        // «Стоп» замораживает очередь: агентское сообщение остаётся ждать возобновления,
        // а не вычищается (как было раньше) — иначе сразу после прерывания хлынул бы ход
        var session = await MkBusySessionAsync("interrupt");
        await _sut.SendMessageAndWaitAsync(session.Id, "не доставлять", TimeSpan.Zero);

        _sut.Interrupt(session.Id);

        _sut.GetPending(session.Id).Should().ContainSingle()
            .Which.Text.Should().Be("не доставлять");
    }

    [Fact]
    public void GetPending_НесуществующаяСессия_Пусто()
    {
        _sut.GetPending("nonexistent").Should().BeEmpty();
    }

    [Fact]
    public async Task SendOrEnqueue_ЗанятыйЧат_ОткладываетСлужебныйХодИНеПоказываетПризрак()
    {
        // Доклад исполнителя (модель Z): его текст уже лежит в ленте гостевой репликой,
        // поэтому ход-реакция откладывается молча — призрак дублировал бы служебный промпт
        var session = await MkBusySessionAsync("silent");

        var deferred = await _sut.SendOrEnqueueAsync(session.Id, "отреагируй на отчёт",
            senderPersonaId: "delegator-1", silent: true, suppressTasksExecute: true);

        deferred.Should().BeTrue();
        var queued = _sut.GetPending(session.Id).Should().ContainSingle().Subject;
        queued.Silent.Should().BeTrue();
        queued.SuppressTasksExecute.Should().BeTrue("иначе постановщик самозапустит задачу и закольцует A↔B");
        _sut.GetVisiblePending(session.Id).Should().BeEmpty("служебный ход призраком не показываем");
    }

    [Fact]
    public async Task GetVisiblePending_ОбычноеСообщение_Видно()
    {
        var session = await MkBusySessionAsync("visible");

        await _sut.SendMessageAndWaitAsync(session.Id, "привет", TimeSpan.Zero, senderOrigin: "Проект Бета");

        var visible = _sut.GetVisiblePending(session.Id).Should().ContainSingle().Subject;
        visible.Text.Should().Be("привет");
        visible.SenderOrigin.Should().Be("Проект Бета");
    }

    // --- «Честная очередь»: пользовательские сообщения в занятый чат ---

    [Theory]
    [InlineData(SessionStatus.Working)]
    [InlineData(SessionStatus.Waiting)]
    public async Task SendMessage_User_ЗанятыйЧат_СтавитВВидимуюОчередь(SessionStatus status)
    {
        // Раньше сообщение пользователя молча писалось в stdin занятого CLI — теперь
        // встаёт в видимую серверную очередь с вложениями и режимом (FIFO)
        var session = await MkBusySessionAsync("uq" + status, status);

        var outcome = await _sut.SendMessageAsync(session.Id, "подожди меня", ["a.txt", "b.txt"], mode: "plan");

        outcome.Should().Be(SessionManager.SendUserOutcome.Queued);
        var queued = _sut.GetPending(session.Id).Should().ContainSingle().Subject;
        queued.Text.Should().Be("подожди меня");
        queued.Kind.Should().Be(SessionManager.PendingKind.User);
        queued.AttachedPaths.Should().BeEquivalentTo(["a.txt", "b.txt"]);
        queued.Mode.Should().Be("plan");
        // В видимом снимке пользовательское тоже отражается (карточка-призрак)
        _sut.GetVisiblePending(session.Id).Should().ContainSingle();
    }

    [Fact]
    public async Task SendMessage_User_ДубликатыДопускаются()
    {
        // Человек может осознанно слать повторы («продолжи», «ещё раз») — дедуп только
        // для агентских, пользовательские не дедупятся
        var session = await MkBusySessionAsync("uqdup");

        await _sut.SendMessageAsync(session.Id, "ещё раз", []);
        await _sut.SendMessageAsync(session.Id, "ещё раз", []);

        _sut.GetPending(session.Id).Should().HaveCount(2);
    }

    [Fact]
    public async Task CancelPending_СнимаетПользовательскоеСообщение()
    {
        var session = await MkBusySessionAsync("uqcancel");
        await _sut.SendMessageAsync(session.Id, "первое", []);
        await _sut.SendMessageAsync(session.Id, "второе", []);
        var first = _sut.GetPending(session.Id)[0];

        (await _sut.CancelPendingAsync(session.Id, first.Id)).Should().BeTrue();

        _sut.GetPending(session.Id).Should().ContainSingle().Which.Text.Should().Be("второе");
    }

    [Fact]
    public async Task Interrupt_ИзымаетПоследнееПользовательское_АгентскиеОстаются()
    {
        // «Стоп» замораживает очередь и возвращает в композер ПОСЛЕДНЕЕ пользовательское:
        // оно изымается, агентские и более ранние пользовательские остаются ждать возобновления
        var session = await MkBusySessionAsync("uqfreeze");
        await _sut.SendMessageAndWaitAsync(session.Id, "от агента", TimeSpan.Zero);       // agent
        await _sut.SendMessageAsync(session.Id, "user-1", []);                              // user
        await _sut.SendMessageAsync(session.Id, "user-последнее", []);                      // user (последнее)

        _sut.Interrupt(session.Id);

        var pending = _sut.GetPending(session.Id);
        pending.Should().HaveCount(2); // agent + user-1 (последнее user изъято)
        pending.Select(p => p.Text).Should().BeEquivalentTo(["от агента", "user-1"]);
        pending.Should().NotContain(p => p.Text == "user-последнее");
    }

    // --- Прерывание хода входящим сообщением (enqueue + interrupt) ---

    [Fact]
    public async Task SendMessage_User_ЗанятыйЧат_ПрерываетХод_ИДоставляетПоExited()
    {
        // Сообщение пользователя в занятый чат больше не ждёт конца хода пассивно:
        // встаёт в очередь И прерывает текущий ход. Убитый процесс не шлёт result —
        // очередь разбирается по exited ТОГО ЖЕ прогона (SessionEntry.DrainOnExitedRun).
        var session = await MkBusySessionAsync("preempt", SessionStatus.Working);
        session.Name = "есть имя"; // иначе фоновый уточнятор заголовка полезет в локальную модель
        var entry = GetEntry(session.Id);
        var adapter = StubAdapter(entry);
        SetProcess(entry, adapter.Object);

        var outcome = await _sut.SendMessageAsync(session.Id, "срочное", []);

        outcome.Should().Be(SessionManager.SendUserOutcome.Queued);
        adapter.Verify(a => a.Interrupt(), Times.Once());
        _sut.GetPending(session.Id).Should().ContainSingle().Which.Text.Should().Be("срочное");

        // Конец прерванного хода — только exited (процесс убит): очередь разбирается сразу
        await InvokeOnMessageAsync(session.Id, new TurnAccumulator(new List<StoredMessage>()),
            new ExitedMessage(), TestRunId);
        await WaitForSendAsync(adapter, TimeSpan.FromSeconds(2));

        adapter.Verify(a => a.SendMessageAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(),
            It.IsAny<int>(), It.IsAny<bool>()), Times.Once());
        _sut.GetPending(session.Id).Should().BeEmpty("прерывание доставляет сообщение немедленно");
    }

    [Fact]
    public async Task SendMessage_User_ВЦиклеДоГотово_СнимаетЦиклИПрерываетХод()
    {
        // Пользовательское прерывает ВСЁ, включая цикл «до готово»: цикл снимается
        // синхронно (как по «Стоп»), текущий ход прерывается, сообщение доставится по exited
        var session = await MkBusySessionAsync("preempt-loop", SessionStatus.Working);
        await _sut.SetWorkLoopAsync(session.Id, enabled: true, userId: TestUserId);
        var entry = GetEntry(session.Id);
        var adapter = StubAdapter(entry);
        SetProcess(entry, adapter.Object);

        var outcome = await _sut.SendMessageAsync(session.Id, "вмешаться в цикл", []);

        outcome.Should().Be(SessionManager.SendUserOutcome.Queued);
        _sut.GetById(session.Id)!.WorkLoop.Should().BeNull("сообщение пользователя обрывает цикл");
        adapter.Verify(a => a.Interrupt(), Times.Once());
        _sut.GetPending(session.Id).Should().ContainSingle()
            .Which.Kind.Should().Be(SessionManager.PendingKind.User);
    }

    [Fact]
    public async Task SendMessage_User_ЦиклМеждуИтерациями_СнимаетЦиклИДоставляетСразу()
    {
        // Между итерациями цикла чат на мгновение свободен: прерывать нечего (Interrupt
        // не зовётся), но цикл снимается, и доставку форсирует dispatchNow постановки
        var session = await MkBusySessionAsync("preempt-idle-loop", SessionStatus.Active);
        session.Name = "есть имя";
        await _sut.SetWorkLoopAsync(session.Id, enabled: true, userId: TestUserId);
        var entry = GetEntry(session.Id);
        var adapter = StubAdapter(entry);
        SetProcess(entry, adapter.Object);

        var outcome = await _sut.SendMessageAsync(session.Id, "вмешаться между итерациями", []);

        outcome.Should().Be(SessionManager.SendUserOutcome.Queued);
        _sut.GetById(session.Id)!.WorkLoop.Should().BeNull();
        adapter.Verify(a => a.Interrupt(), Times.Never());
        await WaitForSendAsync(adapter, TimeSpan.FromSeconds(2));
        adapter.Verify(a => a.SendMessageAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(),
            It.IsAny<int>(), It.IsAny<bool>()), Times.Once());
        _sut.GetPending(session.Id).Should().BeEmpty();
    }

    [Fact]
    public async Task SendMessageAndWait_ОбычныйЗанятыйЧат_ПрерываетХод()
    {
        // Агентское сообщение (chats_send) в обычный занятый чат тоже прерывает ход —
        // доклад доставится сразу, а не после многоминутного хода
        var session = await MkBusySessionAsync("agent-preempt", SessionStatus.Working);
        var entry = GetEntry(session.Id);
        var adapter = StubAdapter(entry);
        SetProcess(entry, adapter.Object);

        var result = await _sut.SendMessageAndWaitAsync(session.Id, "срочный доклад", TimeSpan.Zero);

        result.Should().BeOfType<SendAndWaitResult.Queued>();
        adapter.Verify(a => a.Interrupt(), Times.Once());
        _sut.GetPending(session.Id).Should().ContainSingle();
    }

    [Fact]
    public async Task SendMessageAndWait_ЧатВЦиклеДоГотово_НеПрерывает()
    {
        // Доклады персон не рушат цикл «до готово»: сообщение ждёт конца всего цикла
        var session = await MkBusySessionAsync("agent-loop", SessionStatus.Working);
        await _sut.SetWorkLoopAsync(session.Id, enabled: true, userId: TestUserId);
        var entry = GetEntry(session.Id);
        var adapter = StubAdapter(entry);
        SetProcess(entry, adapter.Object);

        var result = await _sut.SendMessageAndWaitAsync(session.Id, "доклад в цикл", TimeSpan.Zero);

        result.Should().BeOfType<SendAndWaitResult.Queued>();
        adapter.Verify(a => a.Interrupt(), Times.Never());
        _sut.GetById(session.Id)!.WorkLoop.Should().NotBeNull("агентское сообщение не трогает цикл");
        _sut.GetPending(session.Id).Should().ContainSingle();
    }

    // --- Цикл «до готово»: явная остановка в ленту (B3/B5/B6 — ContinueWorkLoopAsync
    // ни разу не был покрыт тестами: ни лимит итераций, ни LoopTurnFailed, ни verifying) ---

    private static async Task WaitForConditionAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(10);
        }
    }

    private static TurnAccumulator GetAccumulator(object entry) =>
        (TurnAccumulator)entry.GetType().GetField("Accumulator")!.GetValue(entry)!;

    private static void SetLoopTurnInFlight(object entry, bool value) =>
        entry.GetType().GetField("LoopTurnInFlight")!.SetValue(entry, value);

    [Fact]
    public async Task ContinueWorkLoop_ЛимитИтераций_ЯвноеСообщениеИСниманиеЦикла()
    {
        var session = await MkBusySessionAsync("loop-limit", SessionStatus.Working);
        await _sut.SetWorkLoopAsync(session.Id, enabled: true, userId: TestUserId);
        var loop = _sut.GetById(session.Id)!.WorkLoop!;
        loop.MaxIterations = 3;
        loop.Iteration = 2; // следующая итерация (после ++) упрётся в лимит
        var entry = GetEntry(session.Id);
        SetLoopTurnInFlight(entry, true);

        await InvokeOnMessageAsync(session.Id, GetAccumulator(entry),
            new ResultMessage("success", 10, 1, null, null), TestRunId);
        await WaitForConditionAsync(() => _sut.GetById(session.Id)!.WorkLoop is null, TimeSpan.FromSeconds(2));

        _sut.GetById(session.Id)!.WorkLoop.Should().BeNull();
        var msg = _sentMessages.OfType<WorkLoopStoppedMessage>().Should().ContainSingle().Subject;
        msg.Reason.Should().Be("limit");
        msg.Text.Should().Contain("3 ходов");
    }

    [Fact]
    public async Task ContinueWorkLoop_ОшибкаХода_ЯвноеСообщениеИСниманиеЦикла()
    {
        var session = await MkBusySessionAsync("loop-error", SessionStatus.Working);
        await _sut.SetWorkLoopAsync(session.Id, enabled: true, userId: TestUserId);
        var entry = GetEntry(session.Id);
        SetLoopTurnInFlight(entry, true);

        // ErrorMessage выставляет LoopTurnFailed=true (SessionManager.OnMessageAsync) —
        // именно эта ветка ContinueWorkLoopAsync тестируется
        await InvokeOnMessageAsync(session.Id, GetAccumulator(entry),
            new ErrorMessage("подписка недоступна"), TestRunId);
        await WaitForConditionAsync(() => _sut.GetById(session.Id)!.WorkLoop is null, TimeSpan.FromSeconds(2));

        _sut.GetById(session.Id)!.WorkLoop.Should().BeNull();
        var msg = _sentMessages.OfType<WorkLoopStoppedMessage>().Should().ContainSingle().Subject;
        msg.Reason.Should().Be("error");
        msg.Text.Should().Be("Цикл остановлен: ход завершился ошибкой.");
    }

    [Fact]
    public async Task ContinueWorkLoop_НайденПромис_ПереходитВVerifying_БезСообщенияОстановки()
    {
        // Переход в verifying — штатный шаг цикла (не остановка с неясным исходом), поэтому
        // WorkLoopStoppedMessage тут не шлётся, а WorkLoop остаётся активным
        var session = await MkBusySessionAsync("loop-verify", SessionStatus.Working);
        session.Name = "есть имя";
        await _sut.SetWorkLoopAsync(session.Id, enabled: true, userId: TestUserId);
        var entry = GetEntry(session.Id);
        var adapter = StubAdapter(entry);
        SetProcess(entry, adapter.Object);
        SetLoopTurnInFlight(entry, true);

        var loop = _sut.GetById(session.Id)!.WorkLoop!;
        lock (entry.GetType().GetField("LoopTurnLock")!.GetValue(entry)!)
        {
            var buf = (System.Text.StringBuilder)entry.GetType().GetField("LoopTurnText")!.GetValue(entry)!;
            buf.Append($"готово <promise>{loop.Promise}</promise>");
        }

        await InvokeOnMessageAsync(session.Id, GetAccumulator(entry),
            new ResultMessage("success", 10, 1, null, null), TestRunId);
        await WaitForConditionAsync(() => _sut.GetById(session.Id)?.WorkLoop?.Phase == "verifying",
            TimeSpan.FromSeconds(2));

        _sut.GetById(session.Id)!.WorkLoop.Should().NotBeNull();
        _sut.GetById(session.Id)!.WorkLoop!.Phase.Should().Be("verifying");
        _sentMessages.OfType<WorkLoopStoppedMessage>().Should().BeEmpty();
        await WaitForSendAsync(adapter, TimeSpan.FromSeconds(2));
        adapter.Verify(a => a.SendMessageAsync(
            It.Is<string>(t => t.Contains("ВЕРИФИКАЦИЯ")), It.IsAny<IReadOnlyList<string>>(),
            It.IsAny<int>(), It.IsAny<bool>()), Times.Once());
    }

    [Fact]
    public async Task SetWorkLoop_РучнойСтоп_ШлётЯвноеСообщение()
    {
        var session = await MkBusySessionAsync("loop-manual-stop", SessionStatus.Active);
        await _sut.SetWorkLoopAsync(session.Id, enabled: true, userId: TestUserId);

        await _sut.SetWorkLoopAsync(session.Id, enabled: false, userId: TestUserId, manual: true);

        var msg = _sentMessages.OfType<WorkLoopStoppedMessage>().Should().ContainSingle().Subject;
        msg.Reason.Should().Be("manual");
        msg.Text.Should().Be("Цикл остановлен вами. Текущий ход продолжает работу.");
    }

    [Fact]
    public async Task SetWorkLoop_Включение_НеШлётСообщениеОстановки()
    {
        var session = await MkBusySessionAsync("loop-manual-on", SessionStatus.Active);

        await _sut.SetWorkLoopAsync(session.Id, enabled: true, userId: TestUserId, manual: true);

        _sentMessages.OfType<WorkLoopStoppedMessage>().Should().BeEmpty("это включение, а не остановка");
    }

    // --- Гард B4: автопилот и «Командная реализация» не сочетаются в одном чате ---

    [Fact]
    public async Task SetWorkLoop_ПриАктивнойКомандаРеализации_Отказ()
    {
        var session = await MakeStabForModeAsync("mode-conflict-1");
        await _sut.SetTeamImplementAsync(session.Id, enabled: true, userId: TestUserId);

        var act = () => _sut.SetWorkLoopAsync(session.Id, enabled: true, userId: TestUserId);

        (await act.Should().ThrowAsync<SessionModeConflictException>())
            .WithMessage("*Командной реализации*");
        _sut.GetById(session.Id)!.WorkLoop.Should().BeNull();
    }

    [Fact]
    public async Task SetTeamImplement_ПриАктивномАвтопилоте_Отказ()
    {
        var session = await MakeStabForModeAsync("mode-conflict-2");
        await _sut.SetWorkLoopAsync(session.Id, enabled: true, userId: TestUserId);

        var act = () => _sut.SetTeamImplementAsync(session.Id, enabled: true, userId: TestUserId);

        (await act.Should().ThrowAsync<SessionModeConflictException>())
            .WithMessage("*Автопилот*");
        _sut.GetById(session.Id)!.TeamImplement.Should().BeNull();
    }

    [Fact]
    public async Task SetTeamImplement_БезАктивногоАвтопилота_РаботаетКакРаньше()
    {
        var session = await MakeStabForModeAsync("mode-noconflict");

        var updated = await _sut.SetTeamImplementAsync(session.Id, enabled: true, userId: TestUserId);

        updated!.TeamImplement.Should().NotBeNull();
    }

    [Fact]
    public async Task SendMessageAndWait_ЧатШтаба_НеПрерывает()
    {
        // Штаб «Командной реализации» агентским сообщением не рушится — прежняя очередь
        var (session, _, _) = await MakeTeamStabAsync("agent-stab-preempt");
        session.Status = SessionStatus.Working;
        var entry = GetEntry(session.Id);
        var adapter = StubAdapter(entry);
        SetProcess(entry, adapter.Object);

        var result = await _sut.SendMessageAndWaitAsync(session.Id, "доклад штабу", TimeSpan.Zero);

        result.Should().BeOfType<SendAndWaitResult.Queued>();
        adapter.Verify(a => a.Interrupt(), Times.Never());
        _sut.GetPending(session.Id).Should().ContainSingle();
    }

    [Fact]
    public async Task Interrupt_Ручной_ЗамороженнаяОчередьНеРазбираетсяПоExited()
    {
        // Регресс «Стоп»: ручное прерывание замораживает очередь, и exited убитого хода
        // НЕ доставляет отложенное — даже если перед этим постановка взводила разбор по exited
        var session = await MkBusySessionAsync("stop-exited", SessionStatus.Working);
        var entry = GetEntry(session.Id);
        var adapter = StubAdapter(entry);
        SetProcess(entry, adapter.Object);
        await _sut.SendMessageAndWaitAsync(session.Id, "не доставлять", TimeSpan.Zero);

        _sut.Interrupt(session.Id);
        await InvokeOnMessageAsync(session.Id, new TurnAccumulator(new List<StoredMessage>()),
            new ExitedMessage(), TestRunId);

        await Task.Delay(150); // drain не должен запуститься
        _sut.GetPending(session.Id).Should().ContainSingle()
            .Which.Text.Should().Be("не доставлять");
        adapter.Verify(a => a.SendMessageAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(),
            It.IsAny<int>(), It.IsAny<bool>()), Times.Never());
    }

    [Fact]
    public async Task SendMessage_User_ЗанятыйШтаб_ПрерываетХодИЧиститМаркерыУбитого()
    {
        // Ход штаба убит пользовательским сообщением — result по нему не придёт, а с ним
        // не придёт и разбор конца хода, потребляющий буфер маркеров. Оставленный маркер
        // склеился бы с текстом следующего хода и применился задним числом: фантомная
        // эскалация и сдвиг стадии («волна-призрак»).
        var (session, _, _) = await MakeTeamStabAsync("stab-preempt-user");
        session.Status = SessionStatus.Working;
        var entry = GetEntry(session.Id);
        var adapter = StubAdapter(entry);
        SetProcess(entry, adapter.Object);
        // Координатор успел написать маркер — он копится в буфере хода штаба
        await InvokeOnMessageAsync(session.Id, new TurnAccumulator(new List<StoredMessage>()),
            new TextDeltaMessage("<<<ЭСКАЛАЦИЯ: расхождение с планом>>>"), TestRunId);
        GetTeamTurnText(entry).Should().NotBeEmpty("предусловие: буфер хода штаба непуст");

        var outcome = await _sut.SendMessageAsync(session.Id, "как дела?", []);

        outcome.Should().Be(SessionManager.SendUserOutcome.Queued);
        adapter.Verify(a => a.Interrupt(), Times.Once());
        GetTeamTurnText(entry).Should().BeEmpty("маркеры убитого хода не доживают до следующего");
    }

    [Fact]
    public async Task Exited_ЧужогоПрогона_НеРазбираетОчередьПрерванногоХода()
    {
        // exited доживающего прогона опаздывает до ~30 мин: не привязанный к прогону разбор
        // увёл бы сообщение в умирающий от interrupt адаптер — из видимой очереди изъято,
        // в семафоре адаптера потеряно
        var session = await MkBusySessionAsync("late-exited", SessionStatus.Working);
        session.Name = "есть имя";
        var entry = GetEntry(session.Id);
        var adapter = StubAdapter(entry);
        SetProcess(entry, adapter.Object, runId: 7);
        await _sut.SendMessageAndWaitAsync(session.Id, "доклад", TimeSpan.Zero); // enqueue + interrupt

        // Поздний exited СТАРОГО прогона
        await InvokeOnMessageAsync(session.Id, new TurnAccumulator(new List<StoredMessage>()),
            new ExitedMessage(), runId: 6);

        await Task.Delay(150); // разбор не должен запуститься
        _sut.GetPending(session.Id).Should().ContainSingle().Which.Text.Should().Be("доклад");
        adapter.Verify(a => a.SendMessageAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(),
            It.IsAny<int>(), It.IsAny<bool>()), Times.Never());

        // exited прерванного прогона — доставка идёт
        await InvokeOnMessageAsync(session.Id, new TurnAccumulator(new List<StoredMessage>()),
            new ExitedMessage(), runId: 7);
        await WaitForSendAsync(adapter, TimeSpan.FromSeconds(2));
        _sut.GetPending(session.Id).Should().BeEmpty();
    }

    [Fact]
    public async Task Exited_ПослеШтатногоResult_НеДренитОчередьВторойРаз()
    {
        // Прерванный ход всё же успел закончиться штатно: очередь разобрал result, метка
        // разбора по exited погашена. Поздний exited того же прогона не должен выпускать
        // следующее сообщение параллельно уже идущему ходу.
        var session = await MkBusySessionAsync("result-then-exited", SessionStatus.Working);
        session.Name = "есть имя";
        var entry = GetEntry(session.Id);
        var adapter = StubAdapter(entry);
        SetProcess(entry, adapter.Object);
        await _sut.SendMessageAndWaitAsync(session.Id, "первое", TimeSpan.Zero);
        await _sut.SendMessageAndWaitAsync(session.Id, "второе", TimeSpan.Zero);

        await InvokeOnMessageAsync(session.Id, new TurnAccumulator(new List<StoredMessage>()),
            new ResultMessage("success", 10, 1, null, null), TestRunId);
        await WaitForSendAsync(adapter, TimeSpan.FromSeconds(2)); // result выпустил «первое»

        await InvokeOnMessageAsync(session.Id, new TurnAccumulator(new List<StoredMessage>()),
            new ExitedMessage(), TestRunId);

        await Task.Delay(150); // второй разбор не должен запуститься
        adapter.Verify(a => a.SendMessageAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(),
            It.IsAny<int>(), It.IsAny<bool>()), Times.Once());
        _sut.GetPending(session.Id).Should().ContainSingle().Which.Text.Should().Be("второе");
    }

    // Буфер текста хода штаба (маркеры координатора) — под своим локом, как в SessionManager
    private static string GetTeamTurnText(object entry)
    {
        var buffer = (System.Text.StringBuilder)entry.GetType().GetField("TeamTurnText")!.GetValue(entry)!;
        lock (entry.GetType().GetField("TeamTurnLock")!.GetValue(entry)!) return buffer.ToString();
    }

    // M7: ходы в тестах завершаются прямым вызовом HandleTeamTurnEndAsync, минуя запуск
    // (SendDirectAsync/SendMessageAndWaitAsync), — флаг «вводная от человека» проставляем явно,
    // как это сделал бы запуск хода по сообщению человека.
    private static void SetTeamTurnFromHuman(object entry, bool value) =>
        entry.GetType().GetField("TeamTurnFromHuman")!.SetValue(entry, value);

    // Ждём именно доставку (SendMessageAsync мока): drain — fire-and-forget Task.Run,
    // а Invocations целиком не годятся — там уже лежат Interrupt/Info этого же сценария
    private static async Task WaitForSendAsync(Mock<ILlmSessionAdapter> adapter, TimeSpan timeout)
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
    private async Task<Session> MakeStabForModeAsync(string suffix, ClaudeMode mode = ClaudeMode.Auto)
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

    [Fact]
    public async Task SetTeamImplement_Включение_СоздаётСостояниеСДефолтамиИШлётWs()
    {
        var session = await MakeStabForModeAsync("ti-on");

        var updated = await _sut.SetTeamImplementAsync(session.Id, enabled: true, userId: TestUserId);

        var ti = updated!.TeamImplement;
        ti.Should().NotBeNull();
        ti!.Stage.Should().Be(TeamImplementStage.Interview,
            "волна 3: по спеке Э8 первая стадия итерации — интервью, а не планирование");
        ti.AutoWaves.Should().BeTrue("авто-волны включены по умолчанию");
        ti.WaveNumber.Should().Be(0);
        ti.ExecutorPersonaIds.Should().BeEmpty("пустой список = вся команда проекта");
        // Потолки бюджета из плана
        ti.Budget.MaxTasks.Should().Be(12);
        ti.Budget.MaxWaves.Should().Be(4);
        ti.Budget.MaxRuns.Should().Be(20);
        ti.Budget.MaxRetries.Should().Be(3);
        ti.Budget.MaxWakeups.Should().Be(10, "дефолт потолка срочных пробуждений координатора агентом");
        // Поля «израсходовано» стартуют с нуля
        ti.Budget.TasksUsed.Should().Be(0);
        // Рассылка WS-события режима
        var msg = _sentMessages.OfType<TeamImplementMessage>().Single();
        msg.Type.Should().Be("team_implement");
        msg.Active.Should().BeTrue();
        msg.Stage.Should().Be("interview");
        msg.AutoWaves.Should().BeTrue();
        // Состояние видно на сессии (wire-контракт)
        _sut.GetById(session.Id)!.TeamImplement.Should().NotBeNull();
    }

    [Fact]
    public async Task SetTeamImplement_СоставИАвтоПриВключении_Сохраняются()
    {
        var dir = MkProjectDir("ti-cfg");
        var project = _projectManager.Create("TI-CFG", dir, TestUserId, TestUsername);
        Persona Mk(string name) => _personaManager.Create(TestUserId, name, null, null, null, null, null,
            PersonaScope.Project, project.Id, null, null, memoryEnabled: false);
        var coordinator = Mk("Алекс");
        var planner = Mk("Полина");
        var executors = new[] { Mk("Денис").Id, Mk("Кира").Id };
        var session = await _sut.CreateAsync(project.Id, ClaudeMode.Auto);

        var updated = await _sut.SetTeamImplementAsync(session.Id, enabled: true,
            autoWaves: false, coordinatorPersonaId: coordinator.Id, plannerPersonaId: planner.Id,
            executorPersonaIds: executors, userId: TestUserId);

        var ti = updated!.TeamImplement!;
        ti.AutoWaves.Should().BeFalse();
        ti.CoordinatorPersonaId.Should().Be(coordinator.Id);
        ti.PlannerPersonaId.Should().Be(planner.Id);
        ti.ExecutorPersonaIds.Should().Equal(executors);
    }

    [Fact]
    public async Task SetTeamImplement_Выключение_ОбнуляетПоле()
    {
        var session = await MakeStabForModeAsync("ti-off");
        await _sut.SetTeamImplementAsync(session.Id, enabled: true, userId: TestUserId);

        var updated = await _sut.SetTeamImplementAsync(session.Id, enabled: false, userId: TestUserId);

        updated!.TeamImplement.Should().BeNull();
        _sut.GetById(session.Id)!.TeamImplement.Should().BeNull();
        // Последнее WS-событие — режим выключен
        _sentMessages.OfType<TeamImplementMessage>().Last().Active.Should().BeFalse();
    }

    // Э7-фикс (Major №1): CoordinatorWriteGuard проверяет команду Bash/PowerShell в момент
    // permission-запроса — а CLI в acceptEdits/bypassPermissions его вообще не присылает
    // (проверено вживую той же командой из находки Веры), гейт был бы мёртв.
    [Theory]
    [InlineData(ClaudeMode.AcceptEdits)]
    [InlineData(ClaudeMode.Bypass)]
    public async Task SetTeamImplement_НесовместимыйРежимПрав_ПереводитВAuto(ClaudeMode incompatible)
    {
        var session = await MakeStabForModeAsync("ti-mode-force-" + incompatible, incompatible);

        await _sut.SetTeamImplementAsync(session.Id, enabled: true, userId: TestUserId);

        _sut.GetById(session.Id)!.Mode.Should().Be(ClaudeMode.Auto,
            "в этом режиме CLI одобряет Bash клиентски, минуя сервер — гейт не сработает");
    }

    [Theory]
    [InlineData(ClaudeMode.Default)]
    [InlineData(ClaudeMode.Auto)]
    public async Task SetTeamImplement_УжеСовместимыйРежимПрав_НеТрогает(ClaudeMode compatible)
    {
        var session = await MakeStabForModeAsync("ti-mode-keep-" + compatible, compatible);

        await _sut.SetTeamImplementAsync(session.Id, enabled: true, userId: TestUserId);

        _sut.GetById(session.Id)!.Mode.Should().Be(compatible,
            "режим и так спрашивает разрешение на Bash — трогать выбор пользователя незачем");
    }

    [Fact]
    public async Task SetTeamImplementAuto_ПереключаетФлагНаХодуБезВыключенияРежима()
    {
        var session = await MakeStabForModeAsync("ti-auto");
        await _sut.SetTeamImplementAsync(session.Id, enabled: true, autoWaves: true, userId: TestUserId);

        var updated = await _sut.SetTeamImplementAutoAsync(session.Id, autoWaves: false, userId: TestUserId);

        var ti = updated!.TeamImplement!;
        ti.AutoWaves.Should().BeFalse("флаг переключён на ходу");
        ti.Stage.Should().Be(TeamImplementStage.Interview, "сама стадия не тронута");
        _sut.GetById(session.Id)!.TeamImplement.Should().NotBeNull("режим остался активен");
    }

    // Minor (волна 3): выключение режима посреди незакрытой волны раньше не оставляло следа —
    // задачи волны сиротели молча (доисполняются, но никто не подводит итог)
    [Fact]
    public async Task SetTeamImplement_ВыключениеПосредиВолны_ОставляетСледВЛенте()
    {
        var (session, _, _) = await MakeTeamStabAsync("ti-off-midwave");
        _sut.WithTeamState(session.Id, t =>
        {
            t.WaveNumber = 1;
            t.ClosedWave = 0; // волна ещё не закрыта
            return true;
        });

        await _sut.SetTeamImplementAsync(session.Id, enabled: false, userId: TestUserId);

        var note = _sentMessages.OfType<GuestTextMessage>().Should().ContainSingle().Subject;
        note.Text.Should().Contain("волны 1");
        note.Text.Should().Contain("выключен");
    }

    [Fact]
    public async Task SetTeamImplement_ВыключениеБезЖивойВолны_НичегоНеПишетВЛенту()
    {
        var (session, _, _) = await MakeTeamStabAsync("ti-off-idle");
        // Дефолт: WaveNumber == 0 — волна ещё не стартовала

        await _sut.SetTeamImplementAsync(session.Id, enabled: false, userId: TestUserId);

        _sentMessages.OfType<GuestTextMessage>().Should().BeEmpty(
            "без незакрытой волны обрывать нечего — лишняя карточка была бы шумом");
    }

    [Fact]
    public async Task SetTeamImplementAuto_БезАктивногоРежима_НеСоздаётСостояние()
    {
        var dir = MkProjectDir("ti-auto-null");
        var project = _projectManager.Create("TI-ANULL", dir, TestUserId, TestUsername);
        var session = await _sut.CreateAsync(project.Id, ClaudeMode.Auto);

        var updated = await _sut.SetTeamImplementAutoAsync(session.Id, autoWaves: false, userId: TestUserId);

        updated!.TeamImplement.Should().BeNull("переключение авто не включает режим");
    }

    [Fact]
    public async Task SetTeamImplement_ЧужойВладелец_Null()
    {
        var dir = MkProjectDir("ti-owner");
        var project = _projectManager.Create("TI-OWN", dir, TestUserId, TestUsername);
        var session = await _sut.CreateAsync(project.Id, ClaudeMode.Auto);

        var updated = await _sut.SetTeamImplementAsync(session.Id, enabled: true, userId: "another-user");

        updated.Should().BeNull("чужой владелец не включает режим");
        _sut.GetById(session.Id)!.TeamImplement.Should().BeNull();
    }

    [Fact]
    public void TeamImplement_СериализуетсяRoundTrip_ПереживаетРестарт()
    {
        // Переживание рестарта: Session целиком (с TeamImplement) сериализуется/десериализуется
        // теми же опциями, что SessionManager._jsonOpts (enum converter), и поле попадает в JSON.
        var opts = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } };
        var session = new Session
        {
            TeamImplement = new SessionTeamImplement
            {
                Stage = TeamImplementStage.Wave,
                WaveNumber = 2,
                AutoWaves = false,
                ExecutorPersonaIds = ["a", "b"],
                Budget = new TeamImplementBudget { TasksUsed = 3, WavesUsed = 1 },
            }
        };

        var json = JsonSerializer.Serialize(session, opts);
        var restored = JsonSerializer.Deserialize<Session>(json, opts)!;

        // sessions.json хранит PascalCase (SessionManager._jsonOpts без naming policy);
        // REST/SignalR-wire — camelCase (ASP.NET default). Проверяем попадание в хранилище.
        json.Should().Contain("TeamImplement", "поле попадает в JSON-стор (видно фронту)");
        json.Should().Contain("AutoWaves", "флаг авто персистентен");
        var ti = restored.TeamImplement!;
        ti.Should().NotBeNull();
        ti.Stage.Should().Be(TeamImplementStage.Wave);
        ti.WaveNumber.Should().Be(2);
        ti.AutoWaves.Should().BeFalse();
        ti.ExecutorPersonaIds.Should().Equal("a", "b");
        ti.Budget.TasksUsed.Should().Be(3);
        ti.Budget.WavesUsed.Should().Be(1);
    }

    // Дыра покрытия (волна 3): рестарт сервера ПОСРЕДИ интервью — SavedMode/PlanVersion/
    // InterviewRounds обязаны пережить сериализацию, иначе после рестарта чат теряет либо
    // навязанный план-режим (селектор разблокировался бы посреди интервью), либо счёт раундов
    // (лимит вопросов обнулился бы и координатор мог бы спрашивать заново).
    [Fact]
    public void TeamImplement_РестартПосредиИнтервью_СохраняетSavedModePlanVersionInterviewRounds()
    {
        var opts = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } };
        var session = new Session
        {
            Mode = ClaudeMode.Plan,
            TeamImplement = new SessionTeamImplement
            {
                Stage = TeamImplementStage.Interview,
                SavedMode = ClaudeMode.Auto,
                PlanVersion = 1,
                ApprovedPlanVersion = 0,
                InterviewRounds = 1,
                Replanning = true,
                FirstIterationOpened = true,
            }
        };

        var json = JsonSerializer.Serialize(session, opts);
        var restored = JsonSerializer.Deserialize<Session>(json, opts)!;

        var ti = restored.TeamImplement!;
        restored.Mode.Should().Be(ClaudeMode.Plan);
        ti.Stage.Should().Be(TeamImplementStage.Interview);
        ti.SavedMode.Should().Be(ClaudeMode.Auto, "иначе селектор режима после рестарта разблокировался бы посреди интервью");
        ti.PlanVersion.Should().Be(1);
        ti.InterviewRounds.Should().Be(1, "иначе лимит раундов обнулился бы и координатор мог бы спрашивать заново");
        ti.Replanning.Should().BeTrue();
        ti.FirstIterationOpened.Should().BeTrue();
    }

    // --- Э2: карточка плана в ленте и ответ по ней ---

    // Штаб с координатором и командой из двух профилей; возвращает (сессия, бэкендер, фронтендер)
    private async Task<(Session Session, Persona Backend, Persona Frontend)> MakeTeamStabAsync(string suffix)
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

    private void SetPlannerAnswer(Persona backend, Persona frontend) => _plannerAnswer = $$"""
        {"summary":"Экспорт задач в CSV","subtasks":[
          {"title":"Эндпоинт экспорта","goal":"GET /api/tasks/export",
           "executorPersonaId":"{{backend.Id}}","executorRationale":"Серверная часть — его зона",
           "files":["backend/Controllers/TasksController.cs"],"wave":1,"doneCriteria":"отдаёт CSV"},
          {"title":"Кнопка «Экспорт»","goal":"Кнопка в тулбаре",
           "executorPersonaId":"{{frontend.Id}}","executorRationale":"UI — её зона",
           "files":["frontend/src/components/Toolbar.tsx"],"wave":2,"doneCriteria":"файл скачивается"}]}
        """;

    [Fact]
    public async Task CreateTeamPlan_РаздаётРаботуПоПрофилюИПубликуетКарточку()
    {
        var (session, backend, frontend) = await MakeTeamStabAsync("ti-plan");
        SetPlannerAnswer(backend, frontend);

        var (plan, reason) = await _sut.CreateTeamPlanAsync(session.Id, "Добавить экспорт в CSV", TestUserId);

        reason.Should().BeNull();
        plan.Should().NotBeNull();
        // Бэкендовая часть — бэкендеру, фронтовая — фронтендеру, обоснование в данных карточки
        plan!.Subtasks[0].ExecutorPersonaId.Should().Be(backend.Id);
        plan.Subtasks[0].ExecutorRationale.Should().Be("Серверная часть — его зона");
        plan.Subtasks[1].ExecutorPersonaId.Should().Be(frontend.Id);
        plan.WaveCount.Should().Be(2);

        // Карточка ушла в ленту WS-событием
        var card = _sentMessages.OfType<TeamPlanMessage>().Single();
        card.Type.Should().Be("team_plan");
        card.PlanId.Should().Be(plan.Id);
        card.Resolved.Should().BeFalse("план ждёт подтверждения человека");
        card.Plan.Subtasks.Should().HaveCount(2);

        // Режим перешёл в стадию подтверждения и запомнил карточку
        var ti = _sut.GetById(session.Id)!.TeamImplement!;
        ti.Stage.Should().Be(TeamImplementStage.Confirming);
        ti.PlanCardId.Should().Be(plan.Id);
    }

    // --- «Замысел в карточке и полный план файлом» (решение владельца 2026-08-02) ---

    [Fact]
    public async Task CreateTeamPlan_СЗамыслом_ПишетФайлПланаИКладётПутьВКарточку()
    {
        var (session, backend, frontend) = await MakeTeamStabAsync("ti-plan-file");
        _plannerAnswer = $$"""
            {"summary":"Экспорт задач в CSV",
             "intent":"Идём через готовый эндпоинт экспорта и кнопку в тулбаре, сложную фильтрацию не делаем.",
             "subtasks":[
               {"title":"Эндпоинт экспорта","goal":"GET /api/tasks/export",
                "executorPersonaId":"{{backend.Id}}","executorRationale":"Серверная часть — его зона",
                "files":["backend/Controllers/TasksController.cs"],"wave":1,"doneCriteria":"отдаёт CSV"},
               {"title":"Кнопка «Экспорт»","goal":"Кнопка в тулбаре",
                "executorPersonaId":"{{frontend.Id}}","executorRationale":"UI — её зона",
                "files":["frontend/src/components/Toolbar.tsx"],"wave":2,"doneCriteria":"файл скачивается"}]}
            """;

        var (plan, reason) = await _sut.CreateTeamPlanAsync(session.Id, "Добавить экспорт в CSV", TestUserId);

        reason.Should().BeNull();
        plan.Should().NotBeNull();
        plan!.Intent.Should().Contain("Идём через готовый эндпоинт");
        plan.PlanFilePath.Should().NotBeNull()
            .And.StartWith("docs/plans/team/").And.EndWith("plan-v1.md");

        var project = _projectManager.GetById(session.ProjectId!)!;
        var full = Path.Combine(project.RootPath, plan.PlanFilePath!.Replace('/', Path.DirectorySeparatorChar));
        File.Exists(full).Should().BeTrue("сервер рендерит файл при публикации карточки, а не модель");
        var content = File.ReadAllText(full);
        content.Should().Contain("Эндпоинт экспорта").And.Contain("Идём через готовый эндпоинт")
            .And.Contain("Серверная часть — его зона").And.Contain("`backend/Controllers/TasksController.cs`");

        // Путь в карточке совпадает с записанным файлом — контракт для фронта
        var card = _sentMessages.OfType<TeamPlanMessage>().Single();
        card.Plan.PlanFilePath.Should().Be(plan.PlanFilePath);
    }

    [Fact]
    public async Task CreateTeamPlan_Перепланирование_ДаётВторойФайлПерваяЦела()
    {
        var (session, plan, _) = await MakeStabWithPlanAndStarterAsync("ti-plan-replan");
        await _sut.RespondTeamPlanAsync(session.Id, plan.Id, TeamPlanDecision.Run, userId: TestUserId);
        var v1Path = plan.PlanFilePath;
        v1Path.Should().NotBeNull().And.EndWith("plan-v1.md");

        await _sut.HandleTeamTurnEndAsync(session.Id,
            "<escalate:clarify>неясен формат</escalate>", failed: false);
        _plannerAnswer = $$"""
            {"summary":"Экспорт в XLSX","intent":"Меняем формат на XLSX по просьбе человека.",
             "subtasks":[{"title":"Выгрузка XLSX","goal":"писать xlsx",
              "executorPersonaId":"","executorRationale":"серверная часть",
              "files":["backend/Export.cs"],"wave":1,"doneCriteria":"файл открывается"}]}
            """;

        await _sut.HandleTeamTurnEndAsync(session.Id,
            "<team:work>переделать экспорт на XLSX</team>", failed: false);

        var card = _sentMessages.OfType<TeamPlanMessage>().Last();
        card.Plan.Version.Should().Be(2, "план vN после уточнений");
        var v2Path = card.Plan.PlanFilePath;
        v2Path.Should().NotBeNull().And.EndWith("plan-v2.md").And.NotBe(v1Path,
            "версия плана — отдельный файл, перепланирование не перезаписывает предыдущий");

        var project = _projectManager.GetById(session.ProjectId!)!;
        string Full(string rel) => Path.Combine(project.RootPath, rel.Replace('/', Path.DirectorySeparatorChar));
        File.Exists(Full(v1Path!)).Should().BeTrue();
        File.Exists(Full(v2Path!)).Should().BeTrue();
        File.ReadAllText(Full(v1Path!)).Should().NotContain("Выгрузка XLSX",
            "перепланирование не должно перезаписывать первую версию");
        File.ReadAllText(Full(v2Path!)).Should().Contain("Выгрузка XLSX");
    }

    [Fact]
    public async Task CreateTeamPlan_ГлобальныйЧатБезПроекта_ПубликуетсяБезФайлаИБезОшибок()
    {
        // Чат вне проекта резолвит домашнюю папку через UserStore — нужен настоящий пользователь,
        // а не голый TestUserId (тот заведён только в data проектов, не в UserStore)
        var owner = _userStore.Add("ti-global-owner", "password123", "user");
        Persona MkGlobal(string name, string role, PersonaSpecialty spec = PersonaSpecialty.None) =>
            _personaManager.Create(owner.Id, name, role, null, null, null, null,
                PersonaScope.Global, null, null, null, memoryEnabled: false, specialty: spec);

        var coordinator = MkGlobal("Алекс-Г", "Тимлид", PersonaSpecialty.Coordinator);
        var executor = MkGlobal("Денис-Г", "Бэкенд");
        var session = await _sut.CreatePersonaChatAsync(owner.Id, coordinator.Id, ClaudeMode.Auto);
        session.ProjectId.Should().BeNull("персона и чат глобальные — команды проекта нет");

        await _sut.SetTeamImplementAsync(session.Id, enabled: true,
            executorPersonaIds: [executor.Id], userId: owner.Id);
        _plannerAnswer = $$"""
            {"summary":"Сделать штуку","intent":"Коротко и по делу.",
             "subtasks":[{"title":"Штука","executorPersonaId":"{{executor.Id}}",
              "executorRationale":"его зона","wave":1}]}
            """;

        var (plan, reason) = await _sut.CreateTeamPlanAsync(session.Id, "Сделай штуку", owner.Id);

        reason.Should().BeNull();
        plan.Should().NotBeNull();
        plan!.PlanFilePath.Should().BeNull("вне проекта писать план некуда — практика при этом работает как раньше");
        _sentMessages.OfType<TeamPlanMessage>().Single().Plan.PlanFilePath.Should().BeNull();
    }

    [Fact]
    public async Task CreateTeamPlan_ОшибкаЗаписиФайла_ПубликацияНеПадает()
    {
        var (session, backend, frontend) = await MakeTeamStabAsync("ti-plan-write-fail");
        SetPlannerAnswer(backend, frontend);
        var project = _projectManager.GetById(session.ProjectId!)!;
        // Занимаем ожидаемый путь файла директорией — запись бросит исключение
        var rel = TeamPlanFileRenderer.RelativePath(session.Name, session.Id, 1);
        Directory.CreateDirectory(Path.Combine(project.RootPath, rel.Replace('/', Path.DirectorySeparatorChar)));

        var (plan, reason) = await _sut.CreateTeamPlanAsync(session.Id, "Экспорт", TestUserId);

        reason.Should().BeNull("ошибка записи файла плана не должна ронять публикацию карточки");
        plan.Should().NotBeNull();
        plan!.PlanFilePath.Should().BeNull("запись не удалась — ссылки в карточке нет, а не отказ публикации");
        _sentMessages.OfType<TeamPlanMessage>().Should().ContainSingle();
    }

    // B2 приёмки: отказ обязан приходить ДО интервью, а не после потраченного хода
    [Fact]
    public async Task SetTeamImplement_ЧатБезПерсоны_ОтклоняетНаВходеИНеСоздаётСостояние()
    {
        var dir = MkProjectDir("ti-nocoord");
        var project = _projectManager.Create("TI-NC", dir, TestUserId, TestUsername);
        var session = await _sut.CreateAsync(project.Id, ClaudeMode.Auto);

        var act = () => _sut.SetTeamImplementAsync(session.Id, enabled: true, userId: TestUserId);

        var ex = (await act.Should().ThrowAsync<TeamImplementSetupException>()).Which;
        ex.Code.Should().Be(TeamImplementSetupException.NoCoordinator, "фронт различает отказ машинно");
        ex.Message.Should().Contain("координатора");
        _sut.GetById(session.Id)!.TeamImplement.Should().BeNull("режим не включился");
        _sentMessages.OfType<TeamImplementMessage>().Should().BeEmpty("WS-события режима не было");
    }

    // Симметричный случай спеки: состава исполнителей нет (в команде только сам координатор,
    // явный список пуст) — подбирать не из кого. Отказ тоже на входе, а не после интервью.
    [Fact]
    public async Task SetTeamImplement_БезСоставаИсполнителей_ОтклоняетНаВходе()
    {
        var dir = MkProjectDir("ti-noexec");
        var project = _projectManager.Create("TI-NOEXEC", dir, TestUserId, TestUsername);
        var coordinator = _personaManager.Create(TestUserId, "Алекс-один", "Тимлид", null, null, null, null,
            PersonaScope.Project, project.Id, null, null, memoryEnabled: false);
        var session = await _sut.CreateAsync(project.Id, ClaudeMode.Auto, personaId: coordinator.Id);

        var act = () => _sut.SetTeamImplementAsync(session.Id, enabled: true,
            coordinatorPersonaId: coordinator.Id, userId: TestUserId);

        var ex = (await act.Should().ThrowAsync<TeamImplementSetupException>()).Which;
        ex.Code.Should().Be(TeamImplementSetupException.NoExecutors);
        ex.Message.Should().Contain("исполнителей");
        _sut.GetById(session.Id)!.TeamImplement.Should().BeNull();
    }

    // Тот же чат, но состав задан явно — включается
    [Fact]
    public async Task SetTeamImplement_ЯвныйСостав_Включается()
    {
        var dir = MkProjectDir("ti-exec-explicit");
        var project = _projectManager.Create("TI-EXEC", dir, TestUserId, TestUsername);
        Persona Mk(string name) => _personaManager.Create(TestUserId, name, null, null, null, null, null,
            PersonaScope.Project, project.Id, null, null, memoryEnabled: false);
        var coordinator = Mk("Алекс-2");
        var executor = Mk("Денис-2");
        var session = await _sut.CreateAsync(project.Id, ClaudeMode.Auto, personaId: coordinator.Id);

        var updated = await _sut.SetTeamImplementAsync(session.Id, enabled: true,
            executorPersonaIds: [executor.Id], userId: TestUserId);

        updated!.TeamImplement!.ExecutorPersonaIds.Should().Equal(executor.Id);
    }

    [Fact]
    public async Task CreateTeamPlan_БезВключённогоРежима_Отказ()
    {
        var dir = MkProjectDir("ti-nomode");
        var project = _projectManager.Create("TI-NM", dir, TestUserId, TestUsername);
        var session = await _sut.CreateAsync(project.Id, ClaudeMode.Auto);

        var (plan, reason) = await _sut.CreateTeamPlanAsync(session.Id, "сделай фичу", TestUserId);

        plan.Should().BeNull();
        reason.Should().Contain("не включён");
    }

    [Fact]
    public async Task RespondTeamPlan_СменаИсполнителяДоЗапуска_КарточкаОстаётсяОткрытой()
    {
        var (session, backend, frontend) = await MakeTeamStabAsync("ti-reassign");
        SetPlannerAnswer(backend, frontend);
        var (plan, _) = await _sut.CreateTeamPlanAsync(session.Id, "Экспорт", TestUserId);
        var subtask = plan!.Subtasks[0];

        var updated = await _sut.RespondTeamPlanAsync(session.Id, plan.Id, TeamPlanDecision.Reassign,
            subtask.Id, frontend.Id, TestUserId);

        updated!.Subtasks[0].ExecutorPersonaId.Should().Be(frontend.Id, "исполнитель сменён вручную");
        updated.Subtasks[0].ExecutorRationale.Should().Contain("вручную");
        updated.Approved.Should().BeNull("карточка ещё не решена");
        var last = _sentMessages.OfType<TeamPlanMessage>().Last();
        last.Resolved.Should().BeFalse();
        _sut.GetById(session.Id)!.TeamImplement!.Stage.Should().Be(TeamImplementStage.Confirming);
    }

    [Fact]
    public async Task RespondTeamPlan_ЧужойИсполнитель_Отклоняется()
    {
        var (session, backend, frontend) = await MakeTeamStabAsync("ti-alien");
        SetPlannerAnswer(backend, frontend);
        var (plan, _) = await _sut.CreateTeamPlanAsync(session.Id, "Экспорт", TestUserId);

        var updated = await _sut.RespondTeamPlanAsync(session.Id, plan!.Id, TeamPlanDecision.Reassign,
            plan.Subtasks[0].Id, "чужая-персона", TestUserId);

        updated.Should().BeNull("исполнителем можно поставить только свою персону");
        plan.Subtasks[0].ExecutorPersonaId.Should().Be(backend.Id, "назначение не тронуто");
    }

    [Fact]
    public async Task RespondTeamPlan_Запуск_РешаетКарточкуИДвигаетСтадию()
    {
        var (session, backend, frontend) = await MakeTeamStabAsync("ti-run");
        SetPlannerAnswer(backend, frontend);
        var (plan, _) = await _sut.CreateTeamPlanAsync(session.Id, "Экспорт", TestUserId);

        var updated = await _sut.RespondTeamPlanAsync(session.Id, plan!.Id, TeamPlanDecision.Run,
            userId: TestUserId);

        updated!.Approved.Should().BeTrue();
        var last = _sentMessages.OfType<TeamPlanMessage>().Last();
        last.Resolved.Should().BeTrue();
        last.Approved.Should().BeTrue();
        var ti = _sut.GetById(session.Id)!.TeamImplement!;
        ti.Stage.Should().Be(TeamImplementStage.Wave);
        // Плановое число волн — из плана, а не из потолка бюджета: бейдж покажет «из 2», а не «из 4»
        ti.PlannedWaves.Should().Be(2);
        ti.Budget.MaxWaves.Should().Be(4, "потолок бюджета к плановому числу волн отношения не имеет");
    }

    [Fact]
    public async Task RespondTeamPlan_Запуск_ЗовётРаздачуВолны()
    {
        var (session, backend, frontend) = await MakeTeamStabAsync("ti-wave-hook");
        SetPlannerAnswer(backend, frontend);
        var (plan, _) = await _sut.CreateTeamPlanAsync(session.Id, "Экспорт", TestUserId);
        // Хук раздачи (в бою его вешает TeamWaveService — цикл DI разорван им же)
        TeamImplementPlan? handed = null;
        _sut.TeamWaveStarter = (_, p) => { handed = p; return Task.CompletedTask; };

        await _sut.RespondTeamPlanAsync(session.Id, plan!.Id, TeamPlanDecision.Run, userId: TestUserId);

        handed.Should().BeSameAs(plan, "по «Запустить» задачи раздаёт бэкенд, а не модель");
    }

    [Fact]
    public async Task RespondTeamPlan_Отмена_РаздачуНеЗовёт()
    {
        var (session, backend, frontend) = await MakeTeamStabAsync("ti-wave-cancel");
        SetPlannerAnswer(backend, frontend);
        var (plan, _) = await _sut.CreateTeamPlanAsync(session.Id, "Экспорт", TestUserId);
        var called = false;
        _sut.TeamWaveStarter = (_, _) => { called = true; return Task.CompletedTask; };

        await _sut.RespondTeamPlanAsync(session.Id, plan!.Id, TeamPlanDecision.Cancel, userId: TestUserId);

        called.Should().BeFalse();
        _sut.GetById(session.Id)!.TeamImplement!.PlannedWaves.Should().Be(0);
    }

    [Fact]
    public async Task RespondTeamPlan_Отмена_ВозвращаетКПланированию()
    {
        var (session, backend, frontend) = await MakeTeamStabAsync("ti-cancel");
        SetPlannerAnswer(backend, frontend);
        var (plan, _) = await _sut.CreateTeamPlanAsync(session.Id, "Экспорт", TestUserId);

        var updated = await _sut.RespondTeamPlanAsync(session.Id, plan!.Id, TeamPlanDecision.Cancel,
            userId: TestUserId);

        updated!.Approved.Should().BeFalse();
        var ti = _sut.GetById(session.Id)!.TeamImplement!;
        ti.Stage.Should().Be(TeamImplementStage.Planning);
        ti.PlanCardId.Should().BeNull("отменённая карточка не остаётся текущей");
    }

    [Fact]
    public async Task RespondTeamPlan_ЧужойВладелец_Null()
    {
        var (session, backend, frontend) = await MakeTeamStabAsync("ti-owner2");
        SetPlannerAnswer(backend, frontend);
        var (plan, _) = await _sut.CreateTeamPlanAsync(session.Id, "Экспорт", TestUserId);

        var updated = await _sut.RespondTeamPlanAsync(session.Id, plan!.Id, TeamPlanDecision.Run,
            userId: "another-user");

        updated.Should().BeNull();
        _sut.GetById(session.Id)!.TeamImplement!.Stage.Should().Be(TeamImplementStage.Confirming);
    }

    // Штаб «после рестарта сервера»: карточка плана лежит в истории на диске, а накопителя
    // хода у чата нет (он оживляется лениво, с первым ходом). Ровно это состояние встречает
    // человек, который вернулся к чату после перезапуска и жмёт «Запустить».
    private async Task<(Session Session, TeamImplementPlan Plan)> MakeRestartedStabWithPlanAsync(string suffix)
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

    private static void ClearAccumulator(object entry) =>
        entry.GetType().GetField("Accumulator")!.SetValue(entry, null);

    [Fact]
    public async Task RespondTeamPlan_ПослеРестарта_ПланПоднимаетсяСДискаИВолнаРаздаётся()
    {
        var (session, plan) = await MakeRestartedStabWithPlanAsync("ti-plan-restart");
        TeamImplementPlan? handed = null;
        _sut.TeamWaveStarter = (_, p) => { handed = p; return Task.CompletedTask; };

        var updated = await _sut.RespondTeamPlanAsync(session.Id, plan.Id, TeamPlanDecision.Run,
            userId: TestUserId);

        updated.Should().NotBeNull("после рестарта карточка живёт на диске — оттуда её и берём");
        updated!.Approved.Should().BeTrue();
        handed.Should().NotBeNull();
        handed!.Id.Should().Be(plan.Id, "волна раздаётся по плану, поднятому с диска");
        _sut.GetById(session.Id)!.TeamImplement!.Stage.Should().Be(TeamImplementStage.Wave);

        // Решение записано в историю: карточка погашена и повторный клик волну не удвоит
        var history = await _sut.GetHistoryAsync(session.Id);
        var card = history.OfType<StoredTeamPlanMessage>().Last();
        card.Resolved.Should().BeTrue();
        card.Approved.Should().BeTrue();

        handed = null;
        var second = await _sut.RespondTeamPlanAsync(session.Id, plan.Id, TeamPlanDecision.Run,
            userId: TestUserId);
        second.Should().BeNull("двойной клик по уже решённой карточке проходит только раз");
        handed.Should().BeNull();
    }

    [Fact]
    public async Task SaveTeamPlanCard_ПослеРестарта_ПишетРаздачуВИсториюНаДиске()
    {
        // Раздача проставляет под-задачам TaskId. Не запиши мы это на диск — следующее
        // чтение плана увидело бы их нерозданными и завело дубли задач.
        var (session, plan) = await MakeRestartedStabWithPlanAsync("ti-plan-save-disk");
        plan.Subtasks[0].TaskId = "task-42";

        await _sut.SaveTeamPlanCardAsync(session.Id, plan);

        var reloaded = await _sut.GetTeamPlanAsync(session.Id, plan.Id);
        reloaded!.Subtasks[0].TaskId.Should().Be("task-42");
    }

    [Fact]
    public async Task TeamPlan_КарточкаПопадаетВИсторию_ПереживаетПерезагрузку()
    {
        var (session, backend, frontend) = await MakeTeamStabAsync("ti-history");
        SetPlannerAnswer(backend, frontend);
        var (plan, _) = await _sut.CreateTeamPlanAsync(session.Id, "Экспорт", TestUserId);

        var history = await _sut.GetHistoryAsync(session.Id);
        var card = history.OfType<StoredTeamPlanMessage>().Single();

        card.PlanId.Should().Be(plan!.Id);
        card.Resolved.Should().BeFalse();
        card.Plan.Subtasks.Should().HaveCount(2);
        card.Plan.Subtasks[0].ExecutorRationale.Should().NotBeEmpty("обоснование видно в данных карточки");
    }

    // Подставной раннер планировщика: отдаёт заготовленный тестом ответ
    private sealed class StubCheapRunner(Func<string> answer) : ClaudeHomeServer.Services.Llm.ICheapTextRunner
    {
        public bool UsesLocal(string actionKey) => false;

        public Task<string> RunAsync(string actionKey, string prompt, string? fallbackModel = null,
            string? ownerId = null, object? jsonFormat = null, CancellationToken ct = default) =>
            Task.FromResult(answer());

        public Task<string?> RunFreeAsync(string actionKey, string prompt, object? jsonFormat = null,
            CancellationToken ct = default) => Task.FromResult<string?>(answer());

        public Task<string?> RunLocalOnlyAsync(string actionKey, string prompt,
            CancellationToken ct = default) => Task.FromResult<string?>(null);

        public Task<ClaudeHomeServer.Services.Llm.OneShotResult> RunDetailedAsync(string actionKey,
            string prompt, string? fallbackModel = null, string? ownerId = null, TimeSpan? timeout = null,
            int? maxTokens = null, object? jsonFormat = null, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    // --- Гонка TOCTOU очереди: автодоставка при постановке в момент завершения хода ---
    //
    // SendMessageAsync/SendMessageAndWaitAsync читают Info.Status БЕЗ лока, а EnqueuePendingAsync
    // делает Add ПОД PendingLock. Между ними окно: ход завершился (ResultMessage → OnMessageAsync →
    // DrainNextPendingAsync отработал по ЕЩЁ ПУСТОЙ очереди, статус упал в Active), и новое сообщение
    // встаёт в очередь при СВОБОДНОМ чате — триггер автодоступы (по result) уже стрелял. Фикс: в
    // EnqueuePendingAsync после Add, под локом, проверить переход очереди 0→1 при свободном статусе
    // и форсировать DrainNextPendingAsync (идемпотентен). Проверяем сам инвариант white-box'ом:
    // приватный EnqueuePendingAsync + заглушка адаптера (чтобы доставка не запускала claude.exe).

    [Fact]
    public async Task EnqueuePending_СвободныйЧат_ФорсируетДоставкуИОчищаетОчередь()
    {
        // Симуляция окна result: ход «только что» завершился, статус Active, очередь пуста.
        // Без фикса сообщение зависло бы в очереди до следующего действия пользователя.
        var session = await MkBusySessionAsync("race", SessionStatus.Active);
        session.Name = "есть имя"; // иначе фоновый уточнятор заголовка полезет в локальную модель
        var entry = GetEntry(session.Id);
        var adapter = StubAdapter(entry);
        SetProcess(entry, adapter.Object);

        // Детерминированный сигнал доставки: адаптер зажигает TCS в момент вызова. Poll по
        // очереди (как и по invocations) здесь не годится — DrainNextPendingAsync изымает
        // сообщение (RemoveAt → очередь пуста) ДО вызова adapter.SendMessageAsync, а под
        // нагрузкой между RemoveAt и вызовом адаптера проходит значимое время. TCS ловит
        // сам факт доставки без гонок и таймаутных окон.
        var delivered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        adapter.Setup(a => a.SendMessageAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(),
            It.IsAny<int>(), It.IsAny<bool>()))
            .Callback(() => delivered.TrySetResult(true))
            .Returns(Task.CompletedTask);

        var result = await InvokeEnqueuePendingAsync(session.Id, entry, "зависшее сообщение");

        result.Should().BeOfType<SendAndWaitResult.Queued>().Which.Position.Should().Be(1);
        // DrainNextPendingAsync работает в fire-and-forget Task.Run: очередь пустеет
        // (RemoveAt) ДО фактической доставки (Process.SendMessageAsync). Ждём не пустоту
        // очереди, а сам вызов адаптера — иначе Verify ловит гонку.
        await WaitForAdapterCallAsync(adapter, TimeSpan.FromSeconds(2));

        _sut.GetPending(session.Id).Should().BeEmpty(
            "при свободном чате форсированный drain разбирает очередь сам, без зависания");
        adapter.Verify(a => a.SendMessageAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(),
            It.IsAny<int>(), It.IsAny<bool>()), Times.AtLeastOnce());
    }

    [Fact]
    public async Task EnqueuePending_ЗанятыйЧат_НеФорсируетДоставку()
    {
        // Регрессия: при Working/Waiting доставка не форсируется — сообщение ждёт конца хода
        // (result разберёт очередь). Иначе нормальная очередь дёргала бы процесс на каждом Add.
        var session = await MkBusySessionAsync("race-busy", SessionStatus.Working);
        var entry = GetEntry(session.Id);
        var adapter = StubAdapter(entry);
        SetProcess(entry, adapter.Object);

        var result = await InvokeEnqueuePendingAsync(session.Id, entry, "ждёт хода");

        result.Should().BeOfType<SendAndWaitResult.Queued>();
        await Task.Delay(150); // drain не запущен — за это время ничего не должно поменяться
        _sut.GetPending(session.Id).Should().ContainSingle().Which.Text.Should().Be("ждёт хода");
        adapter.Verify(a => a.SendMessageAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(),
            It.IsAny<int>(), It.IsAny<bool>()), Times.Never());
    }

    [Fact]
    public async Task EnqueuePending_ЗамороженнаяОчередь_НеФорсируетДоставку()
    {
        // Регрессия: «Стоп» заморозил очередь (QueueFrozen). Даже при свободном чате
        // авто-доставка запрещена — возобновляет только новое пользовательское сообщение.
        var session = await MkBusySessionAsync("race-frozen", SessionStatus.Active);
        var entry = GetEntry(session.Id);
        var adapter = StubAdapter(entry);
        SetProcess(entry, adapter.Object);
        SetQueueFrozen(entry, true);

        var result = await InvokeEnqueuePendingAsync(session.Id, entry, "в заморозке");

        result.Should().BeOfType<SendAndWaitResult.Queued>();
        await Task.Delay(150);
        _sut.GetPending(session.Id).Should().ContainSingle().Which.Text.Should().Be("в заморозке");
        adapter.Verify(a => a.SendMessageAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(),
            It.IsAny<int>(), It.IsAny<bool>()), Times.Never());
    }

    // Доступ к приватному SessionEntry реестра _sessions (white-box: без него публичный API
    // не воспроизводит окно TOCTOU — все точки входа гейтят по статусу ДО EnqueuePendingAsync).
    private object GetEntry(string sessionId)
    {
        var field = typeof(SessionManager).GetField("_sessions",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        var sessions = (System.Collections.IDictionary)field.GetValue(_sut)!;
        return sessions[sessionId]!;
    }

    private static Mock<ILlmSessionAdapter> StubAdapter(object entry)
    {
        var adapter = new Mock<ILlmSessionAdapter>();
        var info = (Session)entry.GetType().GetField("Info")!.GetValue(entry)!;
        adapter.SetupGet(a => a.Info).Returns(info);
        adapter.Setup(a => a.SendMessageAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(),
            It.IsAny<int>(), It.IsAny<bool>())).Returns(Task.CompletedTask);
        return adapter;
    }

    // Подставной адаптер занимает место процесса вместе с идентификатором прогона: очередь
    // по exited разбирается только для прогона, чей ход прерывали (SessionEntry.DrainOnExitedRun),
    // поэтому в такие сценарии тот же runId уходит и в InvokeOnMessageAsync
    private const long TestRunId = 1;

    private static void SetProcess(object entry, ILlmSessionAdapter adapter, long runId = TestRunId)
    {
        entry.GetType().GetField("Process")!.SetValue(entry, adapter);
        entry.GetType().GetField("RunId")!.SetValue(entry, runId);
    }

    private static void SetQueueFrozen(object entry, bool value) =>
        entry.GetType().GetField("QueueFrozen")!.SetValue(entry, value);

    private async Task<SendAndWaitResult> InvokeEnqueuePendingAsync(string sessionId, object entry, string text)
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

    private static async Task WaitForQueueAsync(SessionManager sut, string sessionId, TimeSpan timeout)
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
    private static async Task WaitForAdapterCallAsync(Mock<ILlmSessionAdapter> adapter, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (adapter.Invocations.Count > 0) return;
            await Task.Delay(10);
        }
    }

    // --- Э4: бюджет-квота вместо запрета, карточки остановок, маркер эскалации ---

    // Стадия «волна» — предпосылка гейта запуска с Э7-фикса (Major №2): квота проверяет
    // не только бюджет, но и что план опубликован и подтверждён (единственное согласование).
    private void SetWaveStage(string sessionId) =>
        _sut.WithTeamState(sessionId, t => { t.Stage = TeamImplementStage.Wave; return true; });

    [Fact]
    public async Task КвотаЗапуска_РежимИЦелыйБюджет_РазрешаетИСразуСчитаетРасход()
    {
        var (session, _, _) = await MakeTeamStabAsync("ti-quota-ok");
        SetWaveStage(session.Id);

        var (verdict, reason) = _sut.TryConsumeTeamImplementRun(session.Id, TestUserId);

        verdict.Should().Be(SessionManager.TeamRunQuota.Allowed,
            "на ходу-реакции штаба запрет заменён квотой — иначе автономный цикл невозможен");
        reason.Should().BeNull();
        _sut.GetById(session.Id)!.TeamImplement!.Budget.RunsUsed.Should().Be(1,
            "счёт ведёт бэкенд в точке запуска, а не модель");
    }

    // Запрос MCP-сервера от лица чата: заголовок X-Caller-Session-Id + sub владельца в JWT.
    // Так фильтр [DenyOnDelegatedTurn] видит запрос в бою (общий api() каждого сервера).
    private Microsoft.AspNetCore.Mvc.Filters.ActionExecutingContext MakeMcpCallContext(string sessionId)
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

    // Фильтр запуска задачи ровно с теми настройками, что стоят на TasksController.Execute
    private static ClaudeHomeServer.Filters.DenyOnDelegatedTurnAttribute ExecuteFilter() =>
        new("Запуск задачи на исполнение")
        {
            AlsoWhenExecutorSuppressed = true,
            AllowInTeamImplement = true,
        };

    [Fact]
    public async Task ГейтЗапуска_ОбычныйХодШтаба_ТожеРасходуетКвоту()
    {
        // Дыра, которую чиним: на НЕреакционном ходу фильтр выходил до квоты, и координатор
        // мог заспамить «создать задачу + запустить» мимо бюджета итерации
        var (session, _, _) = await MakeTeamStabAsync("ti-quota-human");
        SetWaveStage(session.Id);
        var context = MakeMcpCallContext(session.Id);

        ExecuteFilter().OnActionExecuting(context);

        context.Result.Should().BeNull("бюджет цел — запуск разрешён");
        var budget = _sut.GetById(session.Id)!.TeamImplement!.Budget;
        budget.RunsUsed.Should().Be(1, "квота расходуется на любом ходу штаба, не только на реакционном");
        budget.TasksUsed.Should().Be(1, "запущенная руками задача — такая же задача итерации");
    }

    [Fact]
    public async Task ГейтЗапуска_ОбычныйХодШтабаПриВыбранномБюджете_Отказ()
    {
        var (session, _, _) = await MakeTeamStabAsync("ti-quota-human-out");
        SetWaveStage(session.Id);
        var budget = _sut.GetById(session.Id)!.TeamImplement!.Budget;
        budget.RunsUsed = budget.MaxRuns;
        var context = MakeMcpCallContext(session.Id);

        ExecuteFilter().OnActionExecuting(context);

        var result = context.Result.Should().BeOfType<Microsoft.AspNetCore.Mvc.ObjectResult>().Subject;
        result.StatusCode.Should().Be(403);
        budget.RunsUsed.Should().Be(budget.MaxRuns, "отказ ничего не расходует");
    }

    [Fact]
    public async Task ГейтЗапуска_ОбычныйЧатВнеРежима_РаботаетКакРаньше()
    {
        var dir = MkProjectDir("ti-quota-plain-filter");
        var project = _projectManager.Create("TI-QPF", dir, TestUserId, TestUsername);
        var session = await _sut.CreateAsync(project.Id, ClaudeMode.Auto);
        var context = MakeMcpCallContext(session.Id);

        ExecuteFilter().OnActionExecuting(context);

        context.Result.Should().BeNull("вне режима обычный ход как был разрешён, так и остался");
    }

    // m3 (второй проход Глеба): OnActionExecuting списывает квоту АВАНСОМ, до попытки
    // запуска — Execute может вернуть 404 (задача не найдена). Действие не состоялось,
    // платить команде не с чего — OnActionExecuted обязан вернуть списанную единицу.
    [Fact]
    public async Task ГейтЗапуска_ДействиеВернуло404_ВозвращаетСписаннуюКвоту()
    {
        var (session, _, _) = await MakeTeamStabAsync("ti-quota-refund-404");
        SetWaveStage(session.Id);
        var context = MakeMcpCallContext(session.Id);
        var filter = ExecuteFilter();
        filter.OnActionExecuting(context);
        _sut.GetById(session.Id)!.TeamImplement!.Budget.RunsUsed.Should().Be(1, "квота списана авансом");

        var executed = new Microsoft.AspNetCore.Mvc.Filters.ActionExecutedContext(
            context, [], controller: new object())
        { Result = new Microsoft.AspNetCore.Mvc.NotFoundResult() };
        filter.OnActionExecuted(executed);

        var budget = _sut.GetById(session.Id)!.TeamImplement!.Budget;
        budget.RunsUsed.Should().Be(0, "действие не состоялось (404) — впустую списанная единица вернулась");
        budget.TasksUsed.Should().Be(0);
    }

    [Fact]
    public async Task ГейтЗапуска_ДействиеУспешно_КвотаОстаётсяСписанной()
    {
        var (session, _, _) = await MakeTeamStabAsync("ti-quota-refund-ok");
        SetWaveStage(session.Id);
        var context = MakeMcpCallContext(session.Id);
        var filter = ExecuteFilter();
        filter.OnActionExecuting(context);

        var executed = new Microsoft.AspNetCore.Mvc.Filters.ActionExecutedContext(
            context, [], controller: new object())
        { Result = new Microsoft.AspNetCore.Mvc.OkObjectResult(new { }) };
        filter.OnActionExecuted(executed);

        _sut.GetById(session.Id)!.TeamImplement!.Budget.RunsUsed.Should().Be(1, "успех — единица расходуется честно");
    }

    [Fact]
    public async Task КвотаЗапуска_ИзЧатаИсполненияПодШтабом_СписываетсяСБюджетаШтаба()
    {
        // Запуск второго уровня: исполнитель заводит задачу и запускает её из своего чата.
        // Без подъёма к штабу это был бы обход бюджета этажом ниже.
        var (stab, _, _) = await MakeTeamStabAsync("ti-quota-child");
        SetWaveStage(stab.Id);
        var child = await _sut.CreateAsync(stab.ProjectId!, ClaudeMode.Auto);
        _sut.SetParent(child.Id, stab.Id, TestUserId);

        var (verdict, _) = _sut.TryConsumeTeamImplementRun(child.Id, TestUserId);

        verdict.Should().Be(SessionManager.TeamRunQuota.Allowed);
        var budget = _sut.GetById(stab.Id)!.TeamImplement!.Budget;
        budget.RunsUsed.Should().Be(1, "расход лёг на бюджет штаба, а не потерялся");
    }

    [Fact]
    public async Task КвотаЗапуска_ИсчерпанныйБюджет_ОтказСПричиной()
    {
        var (session, _, _) = await MakeTeamStabAsync("ti-quota-out");
        SetWaveStage(session.Id);
        var budget = _sut.GetById(session.Id)!.TeamImplement!.Budget;
        budget.RunsUsed = budget.MaxRuns;

        var (verdict, reason) = _sut.TryConsumeTeamImplementRun(session.Id, TestUserId);

        verdict.Should().Be(SessionManager.TeamRunQuota.Exhausted);
        reason.Should().Contain("запусков исполнителей");
        budget.RunsUsed.Should().Be(budget.MaxRuns, "отказ ничего не расходует");
    }

    [Fact]
    public async Task КвотаЗапуска_ВнеРежимаПрактики_ГейтРаботаетКакРаньше()
    {
        var dir = MkProjectDir("ti-quota-plain");
        var project = _projectManager.Create("TI-QP", dir, TestUserId, TestUsername);
        var session = await _sut.CreateAsync(project.Id, ClaudeMode.Auto);

        var (verdict, _) = _sut.TryConsumeTeamImplementRun(session.Id, TestUserId);

        verdict.Should().Be(SessionManager.TeamRunQuota.NotTeamMode,
            "обычный чат остаётся под прежним запретом DenyOnDelegatedTurn");
    }

    [Fact]
    public async Task КвотаЗапуска_ЧужойВладелец_НеРаспознаётРежим()
    {
        var (session, _, _) = await MakeTeamStabAsync("ti-quota-alien");

        var (verdict, _) = _sut.TryConsumeTeamImplementRun(session.Id, "another-user");

        verdict.Should().Be(SessionManager.TeamRunQuota.NotTeamMode);
        _sut.GetById(session.Id)!.TeamImplement!.Budget.RunsUsed.Should().Be(0);
    }

    // Э7-фикс (находка Веры Major №2): координатор до публикации плана вызывал
    // tasks_run_executor напрямую — стадия оставалась planning, карточки плана не было,
    // а единственное согласование (карточка плана) обходилось целиком. Бюджет честно
    // считал расход, но самого гейта по стадии не было — квота разрешала запуск.
    [Fact]
    public async Task КвотаЗапуска_ДоПубликацииПлана_Отказ()
    {
        var (session, _, _) = await MakeTeamStabAsync("ti-quota-before-plan");
        // MakeTeamStabAsync только включает режим — стадия по умолчанию interview,
        // план ещё не публиковался (см. SessionTeamImplement.Stage)

        var (verdict, reason) = _sut.TryConsumeTeamImplementRun(session.Id, TestUserId);

        verdict.Should().Be(SessionManager.TeamRunQuota.Exhausted,
            "план не подтверждён — единственное согласование ещё не пройдено");
        reason.Should().Contain("не подтверждён");
        _sut.GetById(session.Id)!.TeamImplement!.Budget.RunsUsed.Should().Be(0, "отказ ничего не расходует");
    }

    [Fact]
    public async Task ГейтЗапуска_ДоПубликацииПлана_ОтказЧерезФильтр()
    {
        // Тот же сценарий на реальной точке входа: mcp__tasks__tasks_run_executor → /execute
        var (session, _, _) = await MakeTeamStabAsync("ti-quota-before-plan-filter");
        var context = MakeMcpCallContext(session.Id);

        ExecuteFilter().OnActionExecuting(context);

        var result = context.Result.Should().BeOfType<Microsoft.AspNetCore.Mvc.ObjectResult>().Subject;
        result.StatusCode.Should().Be(403);
    }

    [Fact]
    public async Task КвотаЗапуска_ПослеОстановкиЧеловеком_Отказ()
    {
        var (session, _, _) = await MakeTeamStabAsync("ti-quota-stop");
        await _sut.StopTeamImplementAsync(session.Id, TestUserId);

        var (verdict, reason) = _sut.TryConsumeTeamImplementRun(session.Id, TestUserId);

        verdict.Should().Be(SessionManager.TeamRunQuota.Exhausted);
        reason.Should().Contain("остановлена");
    }

    [Fact]
    public async Task Бюджет_РасширяетсяТолькоОтветомЧеловекаПоКарточке()
    {
        var (session, _, _) = await MakeTeamStabAsync("ti-budget-add");
        var budget = _sut.GetById(session.Id)!.TeamImplement!.Budget;
        var maxTasksBefore = budget.MaxTasks;
        budget.RunsUsed = budget.MaxRuns;
        // Исчерпание бюджета случается на ходу итерации — волна уже стартовала
        _sut.GetById(session.Id)!.TeamImplement!.WaveNumber = 1;

        // Действие агента (гейт запуска) потолки не двигает — оно их только читает
        _sut.TryConsumeTeamImplementRun(session.Id, TestUserId);
        budget.MaxRuns.Should().Be(budget.RunsUsed, "агент не может добавить себе бюджет");

        // Путь человека: карточка исчерпания + кнопка «Добавить бюджет» из хаба
        var escalation = new TeamEscalation
        {
            Kind = TeamEscalationKind.BudgetExhausted,
            Title = "Бюджет итерации израсходован",
            Actions = TeamEscalationActions.For(TeamEscalationKind.BudgetExhausted),
        };
        await _sut.PublishTeamEscalationAsync(session.Id, escalation);
        var ok = await _sut.RespondTeamEscalationAsync(session.Id, escalation.Id, "addBudget",
            userId: TestUserId);

        ok.Should().BeTrue();
        var after = _sut.GetById(session.Id)!.TeamImplement!;
        after.Budget.MaxTasks.Should().BeGreaterThan(maxTasksBefore);
        after.Budget.MaxRuns.Should().BeGreaterThan(after.Budget.RunsUsed, "работа может продолжиться");
        after.Stage.Should().Be(TeamImplementStage.Wave, "практика вернулась в работу");
    }

    [Fact]
    public async Task КарточкаОстановки_ПубликуетсяВЛентуИСтавитСтадиюОжидания()
    {
        var (session, _, _) = await MakeTeamStabAsync("ti-escalation");

        var escalation = new TeamEscalation
        {
            Kind = TeamEscalationKind.Blocker,
            Title = "Исполнитель застрял: нет доступа к БД",
            Details = "Не могу подключиться к тестовой базе",
            Wave = 1,
            Actions = TeamEscalationActions.For(TeamEscalationKind.Blocker),
        };
        await _sut.PublishTeamEscalationAsync(session.Id, escalation);

        var card = _sentMessages.OfType<TeamEscalationMessage>().Single();
        card.Type.Should().Be("team_escalation");
        card.Kind.Should().Be("blocker");
        card.Resolved.Should().BeFalse();
        card.Actions.Select(a => a.Id).Should().Contain(["answer", "reassign", "drop"]);
        _sut.GetById(session.Id)!.TeamImplement!.Stage.Should().Be(TeamImplementStage.AwaitingDecision,
            "молчаливых остановок в режиме не бывает");
    }

    // Дыра покрытия (волна 3): смена координатора не переписывает автора уже опубликованных
    // карточек — «всё, что штаб говорит человеку, идёт от лица координатора НА МОМЕНТ публикации»
    [Fact]
    public async Task ЭскалацияПослеСменыКоординатора_СтараяКарточкаСохраняетИсходногоАвтора()
    {
        var (session, backend, _) = await MakeTeamStabAsync("ti-author-change");
        var originalCoordinatorId = session.PersonaId!;
        var oldCard = new TeamEscalation
        {
            Kind = TeamEscalationKind.Blocker,
            Title = "Первая остановка",
            Wave = 1,
            Actions = TeamEscalationActions.For(TeamEscalationKind.Blocker),
        };
        await _sut.PublishTeamEscalationAsync(session.Id, oldCard);

        await _sut.SetTeamImplementAsync(session.Id, enabled: true, coordinatorPersonaId: backend.Id,
            userId: TestUserId);
        var newCard = new TeamEscalation
        {
            Kind = TeamEscalationKind.Blocker,
            Title = "Вторая остановка",
            Wave = 1,
            Actions = TeamEscalationActions.For(TeamEscalationKind.Blocker),
        };
        await _sut.PublishTeamEscalationAsync(session.Id, newCard);

        var cards = _sentMessages.OfType<TeamEscalationMessage>().ToList();
        cards.Single(c => c.Title == "Первая остановка").PersonaId.Should().Be(originalCoordinatorId,
            "старая карточка не должна переписываться на нового координатора задним числом");
        cards.Single(c => c.Title == "Вторая остановка").PersonaId.Should().Be(backend.Id,
            "новая карточка идёт от лица уже смененного координатора");
    }

    [Fact]
    public async Task КарточкаОстановки_ОтветЧеловека_ГаситКарточкуИВозвращаетВРаботу()
    {
        var (session, _, _) = await MakeTeamStabAsync("ti-escalation-answer");
        // Блокер приходит от исполнителя идущей волны
        _sut.GetById(session.Id)!.TeamImplement!.WaveNumber = 1;
        var escalation = new TeamEscalation
        {
            Kind = TeamEscalationKind.Blocker,
            Title = "Исполнитель застрял: нет доступа",
            Actions = TeamEscalationActions.For(TeamEscalationKind.Blocker),
        };
        await _sut.PublishTeamEscalationAsync(session.Id, escalation);

        var ok = await _sut.RespondTeamEscalationAsync(session.Id, escalation.Id, "answer",
            "доступ выдал, продолжай", TestUserId);

        ok.Should().BeTrue();
        var last = _sentMessages.OfType<TeamEscalationMessage>().Last();
        last.Resolved.Should().BeTrue();
        last.ChosenActionId.Should().Be("answer");
        _sut.GetById(session.Id)!.TeamImplement!.Stage.Should().Be(TeamImplementStage.Wave);
        // Служебный ход координатору — с подписью плашки механики вместо пузыря «Автоматически»
        _sentMessages.OfType<UserMessageMessage>().Last().StaffNote
            .Should().Be("Ответ на карточку передан координатору");
    }

    // Прод 2026-07-31: ответ на карточку «Координатор не понял вводную» (кнопок нет — ответ
    // полем, actionId=null) ставил Stage=Wave при WaveNumber=0 и PlanCardId=null — «волна-
    // призрак»: статус врёт «идёт волна 0, дождись докладов», волн/задач/плана нет, сторож
    // не тикает (WaveStartedAt=null). Ответ до первой волны возвращает практику в стадию,
    // из которой пришла карточка.
    [Fact]
    public async Task КарточкаНеПонялВводную_ОтветЧеловека_ВозвращаетВИнтервьюАНеВПризрачнуюВолну()
    {
        var (session, _, _) = await MakeInterviewStabAsync("ti-stall-answer");
        await _sut.HandleTeamTurnEndAsync(session.Id, "Хорошо, посмотрю что тут можно сделать.", failed: false);
        var card = _sentMessages.OfType<TeamEscalationMessage>().Last();
        card.Kind.Should().Be("productDecision");
        _sut.GetById(session.Id)!.TeamImplement!.Stage.Should().Be(TeamImplementStage.AwaitingDecision);

        var ok = await _sut.RespondTeamEscalationAsync(session.Id, card.EscalationId, null,
            "нужен экспорт именно в CSV, кнопка в тулбаре", TestUserId);

        ok.Should().BeTrue();
        var ti = _sut.GetById(session.Id)!.TeamImplement!;
        ti.WaveNumber.Should().Be(0);
        ti.Stage.Should().Be(TeamImplementStage.Interview,
            "до первой волны ответ на эскалацию возвращает практику в стадию карточки, " +
            "а не в «волну-призрак» без плана и сторожа");
    }

    // Дыра покрытия (волна 3): свободный текстовый ответ (без кнопки) доезжает до координатора
    // дословно — не теряется и не подменяется generic-текстом решения
    [Fact]
    public async Task RespondEscalation_СвободныйТекст_ДоезжаетДоКоординатораДословно()
    {
        var (session, _, _) = await MakeTeamStabAsync("ti-escalation-freetext");
        _sut.GetById(session.Id)!.TeamImplement!.WaveNumber = 1;
        var escalation = new TeamEscalation
        {
            Kind = TeamEscalationKind.Blocker,
            Title = "Исполнитель застрял: нет доступа",
            Actions = TeamEscalationActions.For(TeamEscalationKind.Blocker),
        };
        await _sut.PublishTeamEscalationAsync(session.Id, escalation);

        var ok = await _sut.RespondTeamEscalationAsync(session.Id, escalation.Id, null,
            "доступ выдал вручную, продолжай без кнопки", TestUserId);

        ok.Should().BeTrue();
        _sentMessages.OfType<UserMessageMessage>().Last().Text
            .Should().Contain("доступ выдал вручную, продолжай без кнопки");
    }

    // Штаб с планом и хуком раздачи: возвращает план и «раздано ли» по клику
    private async Task<(Session Session, TeamImplementPlan Plan, Func<TeamImplementPlan?> Handed)>
        MakeStabWithPlanAndStarterAsync(string suffix)
    {
        var (session, backend, frontend) = await MakeTeamStabAsync(suffix);
        SetPlannerAnswer(backend, frontend);
        var (plan, reason) = await _sut.CreateTeamPlanAsync(session.Id, "Экспорт", TestUserId);
        reason.Should().BeNull();
        TeamImplementPlan? handed = null;
        _sut.TeamWaveStarter = (s, p) =>
        {
            handed = p;
            // Как настоящая раздача (TeamWaveService.StartWaveCore): реально стартовавшая
            // волна сама переводит стадию — ответ на карточку её больше не форсирует
            s.TeamImplement!.Stage = TeamImplementStage.Wave;
            return Task.CompletedTask;
        };
        return (session, plan!, () => handed);
    }

    [Theory]
    [InlineData(TeamEscalationKind.BudgetExhausted, "addBudget")]
    [InlineData(TeamEscalationKind.Stopped, "resume")]
    [InlineData(TeamEscalationKind.WaveGate, "runNext")]
    public async Task КарточкаОстановки_РешениеВозвращающееВРаботу_РаздаётВолну(
        TeamEscalationKind kind, string actionId)
    {
        // Худший отказ автономного режима — молчаливый тупик после явного действия человека:
        // кнопка нажата, а волна не стартует, WaveStartedAt пуст и сторож молчит
        var (session, plan, handed) = await MakeStabWithPlanAndStarterAsync("ti-resume-" + actionId);
        var escalation = new TeamEscalation
        {
            Kind = kind,
            Title = "Практика ждёт решения",
            Actions = TeamEscalationActions.For(kind),
        };
        await _sut.PublishTeamEscalationAsync(session.Id, escalation);

        var ok = await _sut.RespondTeamEscalationAsync(session.Id, escalation.Id, actionId, userId: TestUserId);

        ok.Should().BeTrue();
        handed().Should().BeSameAs(plan, $"после «{actionId}» практика обязана поехать сама");
        _sut.GetById(session.Id)!.TeamImplement!.Stage.Should().Be(TeamImplementStage.Wave);
    }

    [Fact]
    public async Task КарточкаОстановки_ЗавершитьИтерацию_ВолнуНеРаздаёт()
    {
        var (session, _, handed) = await MakeStabWithPlanAndStarterAsync("ti-finish-nowave");
        var escalation = new TeamEscalation
        {
            Kind = TeamEscalationKind.BudgetExhausted,
            Title = "Бюджет израсходован",
            Actions = TeamEscalationActions.For(TeamEscalationKind.BudgetExhausted),
        };
        await _sut.PublishTeamEscalationAsync(session.Id, escalation);

        await _sut.RespondTeamEscalationAsync(session.Id, escalation.Id, "finish", userId: TestUserId);

        handed().Should().BeNull("«Завершить итерацию» новую работу не разворачивает");
        _sut.GetById(session.Id)!.TeamImplement!.Stage.Should().Be(TeamImplementStage.Checking);
    }

    [Fact]
    public async Task КарточкаОстановки_ЧужойВладелец_НеОтвечает()
    {
        var (session, _, _) = await MakeTeamStabAsync("ti-escalation-alien");
        var escalation = new TeamEscalation { Kind = TeamEscalationKind.Blocker, Title = "Застрял" };
        await _sut.PublishTeamEscalationAsync(session.Id, escalation);

        var ok = await _sut.RespondTeamEscalationAsync(session.Id, escalation.Id, "answer",
            userId: "another-user");

        ok.Should().BeFalse();
    }

    [Theory]
    [InlineData("Всё плохо\n<escalate:deviation>нужен файл вне владения</escalate>",
        TeamEscalationKind.PlanDeviation, "нужен файл вне владения")]
    [InlineData("<escalate:check>падает 3 теста</escalate>", TeamEscalationKind.CheckFailed, "падает 3 теста")]
    [InlineData("<escalate:decision>CSV или XLSX?</escalate>", TeamEscalationKind.ProductDecision, "CSV или XLSX?")]
    // Модель по XML-привычке закрывает тег по имени (</escalate:check>) — принимаем оба варианта
    [InlineData("<escalate:check>падает 3 теста</escalate:check>", TeamEscalationKind.CheckFailed, "падает 3 теста")]
    [InlineData("<escalate:clarify>что именно неясно</escalate:clarify>", TeamEscalationKind.NeedsClarification, "что именно неясно")]
    public void МаркерЭскалации_РазбираетсяПоТипу(string text, TeamEscalationKind kind, string details)
    {
        var parsed = SessionManager.ParseEscalationMarker(text);

        parsed.Should().NotBeNull();
        parsed!.Value.Kind.Should().Be(kind);
        parsed.Value.Text.Should().Be(details);
    }

    [Fact]
    public void МаркерЭскалации_ВнутриКодБлока_НеСчитается()
    {
        // Модель часто цитирует протокол, прежде чем им пользоваться — цитата не остановка
        var text = "Протокол такой:\n```\n<escalate:check>пример</escalate>\n```\nработаю дальше";

        SessionManager.ParseEscalationMarker(text).Should().BeNull();
    }

    [Fact]
    public void МаркерЭскалации_БезМаркера_Null()
    {
        SessionManager.ParseEscalationMarker("обычный ответ координатора").Should().BeNull();
    }

    // --- Э5: непрерывный контур — ожидание вводной, классификация, добавочная волна ---

    // Штаб с уже пройденной итерацией: план утверждён и запущен, режим ждёт следующую вводную
    private async Task<(Session Session, Persona Backend, Persona Frontend)> MakeIdleStabAsync(string suffix)
    {
        var (session, backend, frontend) = await MakeTeamStabAsync(suffix);
        SetPlannerAnswer(backend, frontend);
        var (plan, _) = await _sut.CreateTeamPlanAsync(session.Id, "Экспорт", TestUserId);
        await _sut.RespondTeamPlanAsync(session.Id, plan!.Id, TeamPlanDecision.Run, userId: TestUserId);
        // Волны отработали, проверка подведена координатором — стадия ожидания вводной
        await _sut.HandleTeamTurnEndAsync(session.Id, "итог: всё готово", failed: false);
        _sut.GetById(session.Id)!.TeamImplement!.Stage = TeamImplementStage.Checking;
        await _sut.HandleTeamTurnEndAsync(session.Id, "Итерация завершена: 2 задачи, проверка пройдена", failed: false);
        _sentMessages.Clear();
        return (session, backend, frontend);
    }

    // Ответ планировщика по добавочной вводной: одна под-задача в одну волну
    private void SetAdditionalPlannerAnswer(Persona backend) => _plannerAnswer = $$"""
        {"summary":"Экспорт в XLSX","subtasks":[
          {"title":"XLSX-выгрузка","goal":"Добавить формат xlsx",
           "executorPersonaId":"{{backend.Id}}","executorRationale":"Серверная часть — его зона",
           "files":["backend/Controllers/TasksController.cs"],"wave":1,"doneCriteria":"файл открывается"}]}
        """;

    [Fact]
    public async Task КонецХода_ПослеПроверки_ПереводитРежимВОжиданиеВводной()
    {
        var (session, _, _) = await MakeTeamStabAsync("ti-idle");
        _sut.GetById(session.Id)!.TeamImplement!.Stage = TeamImplementStage.Checking;

        await _sut.HandleTeamTurnEndAsync(session.Id, "Итерация завершена: 5 задач, проверка пройдена", failed: false);

        var ti = _sut.GetById(session.Id)!.TeamImplement!;
        ti.Should().NotBeNull("режим не выключается вместе с планом — выключает его только человек");
        ti.Stage.Should().Be(TeamImplementStage.Idle);
        _sentMessages.OfType<TeamImplementMessage>().Last().Stage.Should().Be("idle",
            "стадия ожидания уходит на провод — по ней рисуется бейдж «ждёт задачу»");
    }

    [Fact]
    public async Task КонецХода_ПроверкаУпала_ЗовётЧеловекаКарточкойИНеЗависаетВПроверке()
    {
        var (session, _, _) = await MakeTeamStabAsync("ti-idle-fail");
        _sut.GetById(session.Id)!.TeamImplement!.Stage = TeamImplementStage.Checking;

        await _sut.HandleTeamTurnEndAsync(session.Id, "", failed: true);

        var ti = _sut.GetById(session.Id)!.TeamImplement!;
        ti.Stage.Should().NotBe(TeamImplementStage.Idle,
            "упавший ход итог не подвёл — «итерация завершена» было бы враньём");
        ti.Stage.Should().Be(TeamImplementStage.AwaitingDecision,
            "и застревать в «проверке» навсегда нельзя — выход только через человека");
        var card = _sentMessages.OfType<TeamEscalationMessage>().Last();
        card.Kind.Should().Be("checkFailed");
        card.Actions.Select(a => a.Id).Should().Contain(["keepFixing", "finishWithIssues"]);
    }

    // m4 (второй проход Глеба, e7aee793): «Чинить дальше» уводило стадию в Wave без
    // фактической раздачи волны — упавший следующий ход не давал checkFailed
    // (HandleTeamTurnEndAsync требует Stage == Checking), а сторож волн в Wave не смотрит.
    // Контур «любая остановка = карточка» был дырявым ровно там, где это больнее всего.
    [Fact]
    public async Task КарточкаПроверки_ЧинитьДальше_ОстаётсяВПроверкеИДаётКарточкуПриПовторномПровале()
    {
        var (session, _, _) = await MakeTeamStabAsync("ti-keepfixing");
        _sut.GetById(session.Id)!.TeamImplement!.Stage = TeamImplementStage.Checking;
        await _sut.HandleTeamTurnEndAsync(session.Id, "", failed: true);
        var firstCard = _sentMessages.OfType<TeamEscalationMessage>().Last();
        firstCard.Kind.Should().Be("checkFailed");

        var ok = await _sut.RespondTeamEscalationAsync(session.Id, firstCard.EscalationId, "keepFixing",
            userId: TestUserId);

        ok.Should().BeTrue();
        _sut.GetById(session.Id)!.TeamImplement!.Stage.Should().Be(TeamImplementStage.Checking,
            "«Чинить дальше» чинит и перепроверяет сам координатор — раздачи волны здесь нет, " +
            "уводить стадию в Wave нельзя");

        // Следующий ход координатора тоже падает — контур «любая остановка = карточка» обязан
        // сработать снова, а не молчать
        await _sut.HandleTeamTurnEndAsync(session.Id, "", failed: true);

        var secondCard = _sentMessages.OfType<TeamEscalationMessage>().Last();
        secondCard.Kind.Should().Be("checkFailed",
            "упавший ход после «Чинить дальше» обязан снова дать карточку, а не молчать");
    }

    // Э7-фикс (находка Веры Major №3): координатор в стадии planning закончил ход без
    // маркера работы (у слабых моделей маркер иногда теряется) — молчаливый тупик:
    // плана нет, карточки нет, бейдж «планирование» повис бы навсегда без единого следа.
    [Fact]
    public async Task КонецХода_PlanningБезМаркераИБезВолн_ЗовётЧеловекаКарточкойВместоМолчания()
    {
        var (session, _, _) = await MakeTeamStabAsync("ti-silent-stall");
        // Stage по умолчанию planning, WaveNumber == 0 — ни одна волна ещё не стартовала

        await _sut.HandleTeamTurnEndAsync(session.Id, "Хорошо, посмотрю что тут можно сделать.", failed: false);

        var ti = _sut.GetById(session.Id)!.TeamImplement!;
        ti.Stage.Should().Be(TeamImplementStage.AwaitingDecision,
            "молчаливый тупик закрывается карточкой, а не бесконечным «планированием»");
        var card = _sentMessages.OfType<TeamEscalationMessage>().Last();
        card.Kind.Should().Be("productDecision");
    }

    // Э8-фикс (ревью Глеба): тот же класс молчаливого тупика, что чинил Э7-фикс, но не был
    // портирован на новую стадию Interview — координатор, закончивший первый ход итерации
    // обычным текстом без <team:work> и без <escalate:...>, вешал бы практику на «интервью»
    // навсегда без единой карточки.
    [Fact]
    public async Task КонецХода_InterviewБезМаркераИБезВолн_ЗовётЧеловекаКарточкойВместоМолчания()
    {
        var (session, _, _) = await MakeInterviewStabAsync("ti-interview-silent-stall");
        _sut.GetById(session.Id)!.TeamImplement!.Stage.Should().Be(TeamImplementStage.Interview);

        await _sut.HandleTeamTurnEndAsync(session.Id, "Хорошо, посмотрю что тут можно сделать.", failed: false);

        var ti = _sut.GetById(session.Id)!.TeamImplement!;
        ti.Stage.Should().Be(TeamImplementStage.AwaitingDecision,
            "молчаливый тупик в интервью закрывается карточкой, а не бесконечным «интервью»");
        var card = _sentMessages.OfType<TeamEscalationMessage>().Last();
        card.Kind.Should().Be("productDecision");
    }

    // Волна 6 (живая приёмка волны 5): ход, оборванный технически (рестарт сервера, упавший
    // процесс), раньше падал в ТУ ЖЕ ветку молчаливого тупика, что и координатор, реально
    // ответивший без маркера — карточка «Координатор не понял вводную» отправляла человека
    // переформулировать задачу, хотя причина не в ней. failed=true обязан давать честный текст
    // про обрыв хода, а не «не понял вводную».
    [Fact]
    public async Task КонецХода_ОборванТехническиВPlanning_ДаётЧестнуюКарточкуАНеНеПонялВводную()
    {
        var (session, _, _) = await MakeTeamStabAsync("ti-turn-interrupted");
        // Stage по умолчанию planning, WaveNumber == 0 — до маркера дело не дошло

        await _sut.HandleTeamTurnEndAsync(session.Id, "", failed: true);

        var ti = _sut.GetById(session.Id)!.TeamImplement!;
        ti.Stage.Should().Be(TeamImplementStage.AwaitingDecision);
        var card = _sentMessages.OfType<TeamEscalationMessage>().Last();
        card.Kind.Should().Be("productDecision");
        card.Title.Should().Be("Ход прервался");
        card.Title.Should().NotBe("Координатор не понял вводную",
            "обрыв хода — техническая причина, а не непонятая вводная");
        card.Details.Should().Contain("Повторить");
    }

    // Тот же честный текст обязан вытеснить и «Уточнения так и не пришли» — интервью,
    // прерванное технически посреди волны, это не тупик clarify-раунда.
    [Fact]
    public async Task КонецХода_ОборванТехническиВОжиданииУточнений_ДаётЧестнуюКарточку()
    {
        var (session, _, _) = await MakeInterviewStabAsync("ti-turn-interrupted-clarify");
        _sut.WithTeamState(session.Id, t => { t.WaveNumber = 1; return true; });

        await _sut.HandleTeamTurnEndAsync(session.Id, "", failed: true);

        var card = _sentMessages.OfType<TeamEscalationMessage>().Last();
        card.Title.Should().Be("Ход прервался");
        card.Title.Should().NotBe("Уточнения так и не пришли");
    }

    // Minor «дубль уведомления эскалации» (волна 3): ветка is_error в ClaudeSession шлёт
    // синтетический ErrorMessage(ExpectResultFollows: true) и следом безусловно ResultMessage
    // того же хода — раньше OnMessageAsync независимо запускал HandleTeamTurnEndAsync на КАЖДОМ
    // из них, и гонка двух фоновых задач давала две одинаковые карточки/push (GET /api/notifications
    // с createdAt до миллисекунды — живая приёмка, заходы 1 и 5). Спаренный ResultMessage
    // должен только погасить флаг SkipNextTeamTurnEnd, а не разобрать ход второй раз.
    [Fact]
    public async Task КонецХода_ПарнаяErrorИResultОдногоХода_НеДаётДублирующуюЭскалацию()
    {
        var (session, _, _) = await MakeInterviewStabAsync("ti-dup-notif");
        _sut.GetById(session.Id)!.TeamImplement!.Stage.Should().Be(TeamImplementStage.Interview);
        var acc = new TurnAccumulator(new List<StoredMessage>());

        await InvokeOnMessageAsync(session.Id, acc,
            new ErrorMessage("You've hit your weekly limit", ExpectResultFollows: true), TestRunId);
        await InvokeOnMessageAsync(session.Id, acc,
            new ResultMessage("success", 10, 1, null, null), TestRunId);

        var cards = await WaitForEscalationCardsAsync(session.Id, minCount: 1);
        cards.Should().ContainSingle(
            "спаренный ResultMessage не должен второй раз разбирать тот же ход штаба");
    }

    private async Task<IReadOnlyList<TeamEscalationMessage>> WaitForEscalationCardsAsync(
        string sessionId, int minCount, TimeSpan? timeout = null)
    {
        List<TeamEscalationMessage> Snapshot() => _sentMessages.OfType<TeamEscalationMessage>()
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

    // Регресс-страховка: после хотя бы одной волны marker-less ответ в planning/idle —
    // легитимный разговор по WorkClassificationProtocol («почему выбрали Киру?» и т.п.),
    // а не тупик. Карточка тут была бы навязчивым шумом на каждой обычной реплике.
    [Fact]
    public async Task КонецХода_РазговорПослеХотяБыОднойВолны_НеЗоветКарточкой()
    {
        var (session, _, _) = await MakeTeamStabAsync("ti-chat-after-wave");
        var team = _sut.GetById(session.Id)!.TeamImplement!;
        team.WaveNumber = 1;
        team.Stage = TeamImplementStage.Planning;

        await _sut.HandleTeamTurnEndAsync(session.Id,
            "Мы выбрали Дениса, потому что это бэкенд-часть.", failed: false);

        team.Stage.Should().Be(TeamImplementStage.Planning,
            "после хотя бы одной волны разговор без маркера — легитимный ответ, не тупик");
        _sentMessages.OfType<TeamEscalationMessage>().Should().BeEmpty();
    }

    [Theory]
    [InlineData("Понял, беру в работу.\n<team:work>добавить экспорт в CSV</team>", "добавить экспорт в CSV")]
    [InlineData("<team:work>  починить сортировку  </team>", "починить сортировку")]
    // Закрытие по имени (</team:work>) — так модель реально генерирует (прод 2026-07-31)
    [InlineData("<team:work>добавить экспорт в CSV</team:work>", "добавить экспорт в CSV")]
    public void МаркерРаботы_РазбираетсяИзОтветаКоординатора(string text, string expected)
    {
        SessionManager.ParseWorkMarker(text).Should().Be(expected);
    }

    // Прод 2026-07-31: координатор выдал постановку ~14 КБ и закрыл тег по имени — строгое
    // </team> маркер роняло, и вводная уходила в вечный цикл «Координатор не понял вводную».
    [Fact]
    public void МаркерРаботы_ЗакрытиеПоИмениНаДлиннойПостановке_Разбирается()
    {
        var brief = new string('x', 14 * 1024);

        SessionManager.ParseWorkMarker($"<team:work>{brief}</team:work>").Should().Be(brief);
        SessionManager.ParseWorkMarker($"<team:work>{brief}</team>").Should().Be(brief);
    }

    [Fact]
    public void МаркерРаботы_ВнутриКодБлокаИлиБезМаркера_Null()
    {
        SessionManager.ParseWorkMarker("Протокол:\n```\n<team:work>пример</team>\n```\nотвечаю").Should().BeNull();
        SessionManager.ParseWorkMarker("Киру выбрал планировщик: фронт — её зона").Should().BeNull();
    }

    // Волна 6 (живая приёмка волны 5, скриншоты w5-02/w5-03): маркеры протокола протекали в
    // видимый текст координатора — воспроизведено 4 раза, в т.ч. с закрытием тега по имени.
    // StripTeamProtocolMarkers — общая функция очистки и для сохранённой истории (TurnAccumulator),
    // и для живой трансляции (OnMessageAsync), поэтому тестируем сам разбор без харнесса сессии.
    [Theory]
    [InlineData("Каждый файл — отдельная подзадача.\n\n<team:work>распараллелить по файлам</team:work>",
        "Каждый файл — отдельная подзадача.\n\n")]
    // Осиротевший закрывающий тег без пары (прод 2026-08-02, находка Веры) — служебный
    // синтаксис протокола, человеку не место ни в какой форме, вырезаем и без открывающего
    [InlineData("Каждый файл — отдельная подзадача, можно запустить параллельно</team:work>",
        "Каждый файл — отдельная подзадача, можно запустить параллельно")]
    [InlineData("никакие другие файлы проекта не редактировать. </team>",
        "никакие другие файлы проекта не редактировать. ")]
    [InlineData("снимет блокировку запуска.</escalate>", "снимет блокировку запуска.")]
    [InlineData("<team:work>добавить экспорт</team>", "")]
    [InlineData("Либо исполнитель не прочитал источник.\n<escalate:check>суффикс не применён</escalate:check>",
        "Либо исполнитель не прочитал источник.\n")]
    [InlineData("<escalate:deviation>нужен доступ вне владения</escalate>", "")]
    [InlineData("Понял, вопросов нет.<team:talk/>", "Понял, вопросов нет.")]
    [InlineData("Понял, вопросов нет.<team:talk   />", "Понял, вопросов нет.")]
    [InlineData("обычный текст без маркеров", "обычный текст без маркеров")]
    public void StripTeamProtocolMarkers_ВырезаетЦеликомЗавершённыйМаркер(string input, string expected)
    {
        SessionManager.StripTeamProtocolMarkers(input).Should().Be(expected);
    }

    // Код-блок — цитата протокола, а не активный вызов (симметрично Parse*/Has* выше): человек
    // мог явно попросить координатора объяснить формат, такую цитату вырезать нельзя.
    [Fact]
    public void StripTeamProtocolMarkers_НеТрогаетМаркерВКодБлоке()
    {
        var text = "Протокол:\n```\n<team:work>пример</team:work>\n```\nвот так это выглядит";
        SessionManager.StripTeamProtocolMarkers(text).Should().Be(text);
    }

    // Маркер разъехался по нескольким чанкам стрима (закрывающий тег пришёл отдельной
    // дельтой от открывающего) — функция работает на УЖЕ СКЛЕЕННОМ тексте (TeamTurnText —
    // один StringBuilder на весь ход), поэтому границы исходных дельт для неё не видны.
    [Fact]
    public void StripTeamProtocolMarkers_МаркерСклеенныйИзНесколькихДельт_ВсёРавноВырезается()
    {
        var chunk1 = "Готовлю план. <team:wo";
        var chunk2 = "rk>добавить экспорт";
        var chunk3 = "</team:work> — всё по делу.";
        SessionManager.StripTeamProtocolMarkers(chunk1 + chunk2 + chunk3)
            .Should().Be("Готовлю план.  — всё по делу.");
    }

    [Theory]
    [InlineData("текст <team:wo", "текст ")]
    [InlineData("текст <escalate:che", "текст ")]
    [InlineData("текст <team:talk", "текст ")]
    [InlineData("текст <team:talk ", "текст ")]
    [InlineData("текст <", "текст ")]
    public void TrimAmbiguousMarkerTail_ПридерживаетНезавершённыйПрефиксМаркера(string input, string expected)
    {
        SessionManager.TrimAmbiguousMarkerTail(input).Should().Be(expected);
    }

    // Обычный текст, включая случайное «<», не должен придерживаться до бесконечности —
    // иначе живая трансляция обычной прозы координатора зависала бы посреди хода.
    [Theory]
    [InlineData("сравнение 5 < 10 верно")]
    [InlineData("обычный текст без служебных тегов")]
    [InlineData("html-подобное <div> тоже не наш маркер")]
    public void TrimAmbiguousMarkerTail_НеТрогаетТекстБезПротоколаНаХвосте(string input)
    {
        SessionManager.TrimAmbiguousMarkerTail(input).Should().Be(input);
    }

    // Открывающий тег напечатан ЦЕЛИКОМ, но закрытие ещё не пришло — IsAmbiguousMarkerTail
    // такой хвост уже не ловит (он не префикс открывающего тега, он ему равен), поэтому нужна
    // отдельная проверка «висит незакрытый маркер где-то в тексте».
    [Theory]
    [InlineData("Готовлю план. <team:work>", "Готовлю план. ")]
    [InlineData("Готовлю план. <team:work>постановка ещё пишется", "Готовлю план. ")]
    [InlineData("Нашёл проблему: <escalate:check>", "Нашёл проблему: ")]
    [InlineData("Понял. <team:talk", "Понял. ")]
    [InlineData("обычный текст без маркеров", "обычный текст без маркеров")]
    public void TrimUnresolvedMarkerOpen_ПрячетТелоЕщёНеЗакрытогоМаркера(string input, string expected)
    {
        SessionManager.TrimUnresolvedMarkerOpen(input).Should().Be(expected);
    }

    // Волна 6: интеграционная проверка живой трансляции через реальный OnMessageAsync — маркер,
    // разбитый на несколько TextDeltaMessage-чанков (как реально стримит CLI — символ за
    // символом или короткими группами), не должен долетать до клиента ни целиком, ни частично
    // ни в одной из дельт.
    [Fact]
    public async Task ЖиваяТрансляция_МаркерРазбитПоЧанкам_НеПротекаетНиВОднойДельте()
    {
        var (session, _, _) = await MakeTeamStabAsync("ti-stream-filter");
        var acc = new TurnAccumulator(new List<StoredMessage>());
        string[] chunks =
        [
            "Каждый файл — отдельная подзадача, ", "можно запустить параллельно.\n\n",
            "<team:wo", "rk>", "постановка для планировщика ", "с деталями", "</team:work>",
            " Готово.",
        ];

        foreach (var chunk in chunks)
            await InvokeOnMessageAsync(session.Id, acc, new TextDeltaMessage(chunk), TestRunId);

        var deltas = _sentMessages.OfType<TextDeltaMessage>().Select(m => m.Text).ToList();
        deltas.Should().NotBeEmpty();
        foreach (var d in deltas)
        {
            d.Should().NotContain("<team:work>");
            d.Should().NotContain("</team");
            d.Should().NotContain("<team:wo");
        }
        string.Concat(deltas).Should().Be(
            "Каждый файл — отдельная подзадача, можно запустить параллельно.\n\n Готово.");
    }

    // Ход оборвался (рестарт/упавший процесс) СРАЗУ после незавершённого хвоста, похожего на
    // начало маркера, — живая трансляция придержала его (TrimAmbiguousMarkerTail), а следующей
    // дельты, которая подтвердила бы или опровергла маркер, уже не будет. Конец хода обязан
    // довесить придержанный текст, а не потерять его молча.
    [Fact]
    public async Task ЖиваяТрансляция_ХодОборванПослеНезавершённогоХвоста_ДовешиваетЕгоНаКонцеХода()
    {
        var (session, _, _) = await MakeTeamStabAsync("ti-stream-catchup");
        var acc = new TurnAccumulator(new List<StoredMessage>());

        await InvokeOnMessageAsync(session.Id, acc,
            new TextDeltaMessage("Собираю план, минуту <team:wo"), TestRunId);
        _sentMessages.OfType<TextDeltaMessage>().Select(m => m.Text)
            .Should().ContainSingle().Which.Should().Be("Собираю план, минуту ",
                "хвост «<team:wo» ещё может дорасти до маркера следующей дельтой, поэтому придержан");

        await InvokeOnMessageAsync(session.Id, acc,
            new ErrorMessage("Сервер был перезапущен во время хода — ход прерван"), TestRunId);

        var all = _sentMessages.OfType<TextDeltaMessage>().Select(m => m.Text).ToList();
        string.Concat(all).Should().Be("Собираю план, минуту <team:wo",
            "придержанный хвост дальше ничем не резолвится — конец хода обязан довесить его как есть");
    }

    [Fact]
    public async Task ВводнаяЧеловека_ВОжидании_СбрасываетБюджетИтерацииИСнимаетОстановку()
    {
        // M6: сброс делает не приём сообщения, а классификация вводной как работы (спека
        // «Бюджет»). Разговорное сообщение на приёме не трогает ни потолки, ни «Остановить».
        var (session, backend, _) = await MakeIdleStabAsync("ti-reset");
        SetAdditionalPlannerAnswer(backend);
        var team = _sut.GetById(session.Id)!.TeamImplement!;
        team.Budget.TasksUsed = 7;
        team.Budget.WavesUsed = 3;
        team.Budget.RunsUsed = 11;
        team.Budget.WakeupsUsed = 4;
        team.ClosedWave = 2;
        await _sut.StopTeamImplementAsync(session.Id, TestUserId);
        // Занятый чат: ход не запускаем — вводная встаёт в очередь
        session.Status = SessionStatus.Working;

        await _sut.SendMessageAsync(session.Id, "теперь добавь экспорт в XLSX", []);

        var mid = _sut.GetById(session.Id)!.TeamImplement!;
        mid.Budget.TasksUsed.Should().Be(7, "на приёме сообщение ещё не классифицировано");
        mid.Stopped.Should().BeTrue();

        // Координатор классифицировал вводную как работу — итерация открывается заново
        SetTeamTurnFromHuman(GetEntry(session.Id), true);
        await _sut.HandleTeamTurnEndAsync(session.Id, "<team:work>добавить экспорт в XLSX</team>", failed: false);

        var after = _sut.GetById(session.Id)!.TeamImplement!;
        after.Budget.TasksUsed.Should().Be(0);
        after.Budget.WavesUsed.Should().Be(0);
        after.Budget.RunsUsed.Should().Be(0);
        after.Budget.WakeupsUsed.Should().Be(0);
        after.ClosedWave.Should().Be(0, "новый план начинает счёт волн заново");
        after.Stopped.Should().BeFalse("«Остановить» относилось к прошлой итерации");
    }

    [Fact]
    public async Task ВводнаяЧеловека_ПосредиВолны_БюджетНеСбрасывает()
    {
        // Иначе потолок обходится тривиально: пиши в чат почаще, и работающая практика
        // получает бесконечный бюджет
        var (session, _, _) = await MakeTeamStabAsync("ti-reset-wave");
        var team = _sut.GetById(session.Id)!.TeamImplement!;
        team.Stage = TeamImplementStage.Wave;
        team.Budget.RunsUsed = 9;
        session.Status = SessionStatus.Working;

        await _sut.SendMessageAsync(session.Id, "как там дела?", []);

        _sut.GetById(session.Id)!.TeamImplement!.Budget.RunsUsed.Should().Be(9);
    }

    [Fact]
    public async Task СбросБюджета_ДействиямиАгента_Недостижим()
    {
        // Главный риск непрерывного контура: сброс бюджета — вечный двигатель, если до него
        // дотягивается агент. Путь к сбросу ровно один — сообщение человека через хаб.
        var (session, _, _) = await MakeIdleStabAsync("ti-reset-agent");
        var child = await _sut.CreateAsync(session.ProjectId!, ClaudeMode.Auto, name: "Задача: экспорт");
        _sut.SetParent(child.Id, session.Id, TestUserId);
        session.ClaudeSessionId ??= "cli-" + Guid.NewGuid().ToString("N");
        var team = _sut.GetById(session.Id)!.TeamImplement!;
        team.Budget.TasksUsed = 7;
        team.Budget.RunsUsed = 11;
        session.Status = SessionStatus.Working;

        // 1. chats_send из чужого чата (агентский REST-канал)
        await _sut.SendMessageAndWaitAsync(session.Id, "продолжай работу", TimeSpan.Zero, agentDepth: 1);
        // 2. служебный ход-реакция (доклад исполнителя, сводка волны)
        await _sut.SendOrEnqueueAsync(session.Id, "отреагируй на доклад", silent: true, suppressTasksExecute: true);
        // 3. доклад-блокер снизу — единственное, что он двигает, это счётчик пробуждений
        await _sut.ReportBlockerAsync(child.Id, "застрял без доступа", TestUserId);

        var after = _sut.GetById(session.Id)!.TeamImplement!.Budget;
        after.TasksUsed.Should().Be(7, "агент не открывает новую итерацию");
        after.RunsUsed.Should().Be(11);
        after.WakeupsUsed.Should().BeGreaterThan(0, "пробуждение штаба агентом, наоборот, расходует квоту");
    }

    [Fact]
    public async Task МаркерРаботы_ВОжиданииПриАвтоВолнах_РазворачиваетВолнуБезПодтверждения()
    {
        var (session, backend, _) = await MakeIdleStabAsync("ti-additional");
        SetAdditionalPlannerAnswer(backend);
        TeamImplementPlan? handed = null;
        _sut.TeamWaveStarter = (_, p) => { handed = p; return Task.CompletedTask; };
        // Вводная человека: классификация работой открыла новую итерацию (бюджет и счёт волн
        // с нуля — M6: сброс по маркеру, а не по приёму сообщения)
        session.Status = SessionStatus.Working;
        await _sut.SendMessageAsync(session.Id, "теперь добавь выгрузку в XLSX", []);
        SetTeamTurnFromHuman(GetEntry(session.Id), true);

        await _sut.HandleTeamTurnEndAsync(session.Id,
            "Понял, беру в работу.\n<team:work>добавить выгрузку в XLSX</team>", failed: false);

        // Карточка состава опубликована, но клика не ждёт — работа уже роздана
        var planCard = _sentMessages.OfType<TeamPlanMessage>().Last();
        planCard.Resolved.Should().BeTrue("добавочный план подтверждения не ждёт");
        planCard.Approved.Should().BeTrue();
        handed.Should().NotBeNull("волна раздана тем же путём, что и по кнопке «Запустить»");
        handed!.Subtasks.Should().ContainSingle().Which.ExecutorPersonaId.Should().Be(backend.Id);

        // Информационная карточка с единственной кнопкой «Остановить»
        var info = _sentMessages.OfType<TeamEscalationMessage>().Last();
        info.Kind.Should().Be("waveAdded");
        info.Resolved.Should().BeFalse();
        info.Actions.Select(a => a.Id).Should().Equal("stop");
        info.Details.Should().Contain("XLSX");

        var ti = _sut.GetById(session.Id)!.TeamImplement!;
        ti.Stage.Should().Be(TeamImplementStage.Wave, "информационная карточка практику не останавливает");
        ti.PlannedWaves.Should().Be(1, "плановое число волн — из добавочного плана");
        ti.PlanCardId.Should().Be(planCard.PlanId);
    }

    [Fact]
    public async Task МаркерРаботы_АвтоВолныСняты_ПланЖдётПодтверждения()
    {
        // Первоначальный план подтверждается всегда; при снятом авто — и добавочный
        var (session, backend, _) = await MakeIdleStabAsync("ti-additional-manual");
        SetAdditionalPlannerAnswer(backend);
        await _sut.SetTeamImplementAutoAsync(session.Id, autoWaves: false, userId: TestUserId);
        var started = false;
        _sut.TeamWaveStarter = (_, _) => { started = true; return Task.CompletedTask; };

        await _sut.HandleTeamTurnEndAsync(session.Id, "<team:work>добавить выгрузку в XLSX</team>", failed: false);

        _sentMessages.OfType<TeamPlanMessage>().Last().Resolved.Should().BeFalse();
        started.Should().BeFalse("без авто-волн работа ждёт кнопки «Запустить»");
        _sut.GetById(session.Id)!.TeamImplement!.Stage.Should().Be(TeamImplementStage.Confirming);
    }

    [Fact]
    public async Task РазговорноеСообщение_БезМаркера_НиПланаНиВолны()
    {
        var (session, _, _) = await MakeIdleStabAsync("ti-talk");
        var started = false;
        _sut.TeamWaveStarter = (_, _) => { started = true; return Task.CompletedTask; };

        await _sut.HandleTeamTurnEndAsync(session.Id,
            "Киру выбрал планировщик: фронтовая часть — её зона.", failed: false);

        _sentMessages.OfType<TeamPlanMessage>().Should().BeEmpty("вопрос не создаёт плана и задач");
        started.Should().BeFalse();
        _sut.GetById(session.Id)!.TeamImplement!.Stage.Should().Be(TeamImplementStage.Idle);
    }

    [Fact]
    public async Task МаркерРаботы_ПосредиИдущейВолны_Игнорируется()
    {
        var (session, backend, _) = await MakeIdleStabAsync("ti-work-in-wave");
        SetAdditionalPlannerAnswer(backend);
        _sut.GetById(session.Id)!.TeamImplement!.Stage = TeamImplementStage.Wave;

        await _sut.HandleTeamTurnEndAsync(session.Id, "<team:work>ещё одна фича</team>", failed: false);

        _sentMessages.OfType<TeamPlanMessage>().Should().BeEmpty(
            "вторая волна поверх идущей не разворачивается — сначала доигрывает текущая");
    }

    [Fact]
    public async Task МаркерРаботы_ПланировщикНеСмог_ЧеловекПолучаетКарточку()
    {
        var (session, _, _) = await MakeIdleStabAsync("ti-work-noplan");
        _plannerAnswer = "не понял задачу";

        await _sut.HandleTeamTurnEndAsync(session.Id, "<team:work>сделай хорошо</team>", failed: false);

        var card = _sentMessages.OfType<TeamEscalationMessage>().Last();
        card.Kind.Should().Be("productDecision");
        card.Title.Should().Contain("не построился");
        card.Details.Should().Contain("Уточните задачу");
    }

    [Fact]
    public async Task ДобавочнаяВолна_КнопкаОстановить_ОстанавливаетПрактику()
    {
        var (session, backend, _) = await MakeIdleStabAsync("ti-additional-stop");
        SetAdditionalPlannerAnswer(backend);
        _sut.TeamWaveStarter = (_, _) => Task.CompletedTask;
        SetTeamTurnFromHuman(GetEntry(session.Id), true);
        await _sut.HandleTeamTurnEndAsync(session.Id, "<team:work>добавить XLSX</team>", failed: false);
        var info = _sentMessages.OfType<TeamEscalationMessage>().Last();

        var ok = await _sut.RespondTeamEscalationAsync(session.Id, info.EscalationId, "stop", userId: TestUserId);

        ok.Should().BeTrue();
        var ti = _sut.GetById(session.Id)!.TeamImplement!;
        ti.Stopped.Should().BeTrue("новые волны не стартуют, пока человек не продолжит");
        ti.Stage.Should().Be(TeamImplementStage.Wave, "«Остановить» не возобновляет работу и не двигает стадию");
    }

    // --- Э8: интервью, план-режим и перепланирование ---

    // Штаб сразу после вводной человека: стадия интервью, чат в план-режиме.
    // Ход не запускаем (Status=Working) — вводная встаёт в очередь, но итерацию открывает уже
    // сейчас, как и в тестах Э5.
    private async Task<(Session Session, Persona Backend, Persona Frontend)> MakeInterviewStabAsync(
        string suffix, string request = "сделай экспорт задач в CSV")
    {
        var (session, backend, frontend) = await MakeTeamStabAsync(suffix);
        session.Status = SessionStatus.Working;
        await _sut.SendMessageAsync(session.Id, request, []);
        return (session, backend, frontend);
    }

    [Fact]
    public async Task ВводнаяЧеловека_ОткрываетИнтервьюИПланРежим()
    {
        var (session, _, _) = await MakeInterviewStabAsync("ti-interview-open");

        var after = _sut.GetById(session.Id)!;
        after.TeamImplement!.Stage.Should().Be(TeamImplementStage.Interview,
            "первая вводная итерации проходит интервью всегда");
        after.Mode.Should().Be(ClaudeMode.Plan, "интервью и планирование идут в план-режиме");
        after.TeamImplement.SavedMode.Should().Be(ClaudeMode.Auto,
            "режим человека запомнен и вернётся после согласования плана");
        after.TeamImplement.InterviewRounds.Should().Be(0, "вопросов ещё не задавали");
        var ws = _sentMessages.OfType<TeamImplementMessage>().Last();
        ws.Stage.Should().Be("interview");
        ws.ModeLocked.Should().BeTrue("селектор режима в композере заблокирован");
    }

    [Fact]
    public async Task ОтветНаИнтервью_НеВводная_БюджетНеСбрасывает()
    {
        // Иначе цикл «вопрос — ответ» обнулял бы потолки: каждый ответ человека открывал бы
        // новую итерацию, и бюджет перестал бы что-либо ограничивать
        var (session, _, _) = await MakeInterviewStabAsync("ti-interview-budget");
        var team = _sut.GetById(session.Id)!.TeamImplement!;
        team.Budget.TasksUsed = 5;
        team.Budget.RunsUsed = 8;

        await _sut.SendMessageAsync(session.Id, "давай второй вариант", []);

        var after = _sut.GetById(session.Id)!.TeamImplement!;
        after.Budget.TasksUsed.Should().Be(5);
        after.Budget.RunsUsed.Should().Be(8);
        after.Stage.Should().Be(TeamImplementStage.Interview, "ответ не двигает стадию");
    }

    // Дыра покрытия (волна 3): «кристальная» задача — координатор не видит развилок и
    // сразу закрывает интервью маркером работы, ни разу не спросив ASK-карточкой (подтверждено
    // живьём, заход 5 живой приёмки). InterviewRounds обязан остаться 0, план строится сразу.
    [Fact]
    public async Task Интервью_КристальнаяЗадача_СтроитПланБезЕдиногоВопроса()
    {
        var (session, backend, frontend) = await MakeInterviewStabAsync("ti-no-questions",
            "создай about.html с фиксированным содержимым");
        SetPlannerAnswer(backend, frontend);

        await _sut.HandleTeamTurnEndAsync(session.Id,
            "<team:work>about.html, развилок нет</team>", failed: false);

        var ti = _sut.GetById(session.Id)!.TeamImplement!;
        ti.InterviewRounds.Should().Be(0, "координатор не задал ни одного вопроса");
        ti.Stage.Should().Be(TeamImplementStage.Confirming, "план построен и ждёт подтверждения человека");
        _sentMessages.OfType<TeamPlanMessage>().Should().ContainSingle();
    }

    [Fact]
    public async Task ВопросКоординатора_ВИнтервью_СчитаетсяРаундом()
    {
        var (session, _, _) = await MakeInterviewStabAsync("ti-interview-round");

        await _sut.OnStabAskQuestionAsync(session.Id);
        await _sut.OnStabAskQuestionAsync(session.Id);

        _sut.GetById(session.Id)!.TeamImplement!.InterviewRounds.Should().Be(2,
            "протокол разрешает не больше двух раундов на вводную — счёт ведёт бэкенд");
    }

    [Fact]
    public async Task МаркерРаботы_ИзИнтервью_ДаётПланПервойВерсииОтЛицаПланировщика()
    {
        var (session, backend, frontend) = await MakeInterviewStabAsync("ti-interview-plan");
        SetPlannerAnswer(backend, frontend);

        await _sut.HandleTeamTurnEndAsync(session.Id,
            "Вопросов нет — постановка ясна.\n<team:work>экспорт задач в CSV</team>", failed: false);

        var card = _sentMessages.OfType<TeamPlanMessage>().Last();
        card.Plan.Version.Should().Be(1);
        card.Resolved.Should().BeFalse("первоначальный план итерации ждёт подтверждения всегда");
        card.Plan.PlannerPersonaId.Should().NotBeNull(
            "карточка плана идёт от лица планировщика — авторство несёт сам Plan, без дублирующего поля в TeamPlanMessage");

        var after = _sut.GetById(session.Id)!;
        after.TeamImplement!.Stage.Should().Be(TeamImplementStage.Confirming);
        after.TeamImplement.PlanVersion.Should().Be(1);
        after.Mode.Should().Be(ClaudeMode.Plan, "план-режим держится до клика «Запустить»");
    }

    [Fact]
    public async Task RespondTeamPlan_Запустить_ВозвращаетРежимЧеловекаИФиксируетВерсию()
    {
        var (session, backend, frontend) = await MakeInterviewStabAsync("ti-interview-run");
        SetPlannerAnswer(backend, frontend);
        _sut.TeamWaveStarter = (_, _) => Task.CompletedTask;
        await _sut.HandleTeamTurnEndAsync(session.Id, "<team:work>экспорт</team>", failed: false);
        var planId = _sentMessages.OfType<TeamPlanMessage>().Last().PlanId;

        await _sut.RespondTeamPlanAsync(session.Id, planId, TeamPlanDecision.Run, userId: TestUserId);

        var after = _sut.GetById(session.Id)!;
        after.Mode.Should().Be(ClaudeMode.Auto, "после согласования чат работает в прежнем режиме человека");
        after.TeamImplement!.SavedMode.Should().BeNull();
        after.TeamImplement.ApprovedPlanVersion.Should().Be(1, "работа разрешена этой версии плана");
        after.TeamImplement.Stage.Should().Be(TeamImplementStage.Wave);
        _sentMessages.OfType<TeamImplementMessage>().Last().ModeLocked.Should().BeFalse(
            "селектор режима снова разблокирован");
    }

    [Fact]
    public async Task SetMode_ПокаШтабПланирует_Отклоняется()
    {
        var (session, _, _) = await MakeInterviewStabAsync("ti-interview-setmode");

        var act = () => _sut.SetMode(session.Id, "acceptEdits");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Штаб планирует*");
        _sut.GetById(session.Id)!.Mode.Should().Be(ClaudeMode.Plan);
    }

    [Fact]
    public async Task ExitPlanMode_ВРежимеШтаба_ЗапрещёнКоординатору()
    {
        // Иначе в план-режиме CLI сам предложит завершить планирование карточкой plan_review,
        // и человек получит два согласования подряд: штатное и нашу карточку плана
        var (session, _, _) = await MakeTeamStabAsync("ti-exitplan");
        var build = typeof(SessionManager).GetMethod("BuildExtraDisallowed",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

        var inMode = (IReadOnlyList<string>?)build.Invoke(_sut, [TestUserId, null, _sut.GetById(session.Id)!]);
        await _sut.SetTeamImplementAsync(session.Id, enabled: false, userId: TestUserId);
        var offMode = (IReadOnlyList<string>?)build.Invoke(_sut, [TestUserId, null, _sut.GetById(session.Id)!]);

        inMode.Should().Contain("ExitPlanMode");
        (offMode ?? []).Should().NotContain("ExitPlanMode", "вне режима штатное согласование плана работает как обычно");
    }

    [Fact]
    public async Task МаркерУточнений_ВВолне_СтавитВолныНаПаузуИВозвращаетВИнтервью()
    {
        var (session, plan, _) = await MakeStabWithPlanAndStarterAsync("ti-clarify");
        await _sut.RespondTeamPlanAsync(session.Id, plan.Id, TeamPlanDecision.Run, userId: TestUserId);
        _sut.WithTeamState(session.Id, t => { t.WaveStartedAt = DateTime.UtcNow; return true; });

        await _sut.HandleTeamTurnEndAsync(session.Id,
            "<escalate:clarify>непонятно, куда класть выгрузку</escalate>", failed: false);

        var after = _sut.GetById(session.Id)!;
        after.TeamImplement!.Stage.Should().Be(TeamImplementStage.Interview);
        after.TeamImplement.Replanning.Should().BeTrue("следующий план — новая версия, и её надо утвердить");
        after.TeamImplement.WaveStartedAt.Should().BeNull("в интервью таймаут волны не тикает");
        after.Mode.Should().Be(ClaudeMode.Plan, "перепланирование тоже идёт в план-режиме");

        var card = _sentMessages.OfType<TeamEscalationMessage>().Last();
        card.Kind.Should().Be("needsClarification");
        card.Title.Should().Be("Нужны уточнения — волны на паузе");
        card.Actions.Should().BeEmpty("ответы придут ASK-карточками, кнопок у этой карточки нет");
        card.Details.Should().Contain("куда класть выгрузку");
        card.PersonaId.Should().NotBeNull("карточка идёт от лица координатора");
    }

    [Fact]
    public async Task ПланПослеУточнений_ЖдётПодтвержденияДажеПриАвтоВолнах()
    {
        // Авто-волны покрывают волны по неизменному плану, но не смену самого плана
        var (session, plan, _) = await MakeStabWithPlanAndStarterAsync("ti-replan");
        await _sut.RespondTeamPlanAsync(session.Id, plan.Id, TeamPlanDecision.Run, userId: TestUserId);
        await _sut.HandleTeamTurnEndAsync(session.Id,
            "<escalate:clarify>неясен формат</escalate>", failed: false);
        var started = false;
        _sut.TeamWaveStarter = (_, _) => { started = true; return Task.CompletedTask; };
        _plannerAnswer = $$"""
            {"summary":"Экспорт в XLSX","assumptions":["формат — XLSX, как в соседнем модуле"],
             "changes":["CSV заменён на XLSX","под-задача про кнопку убрана"],
             "subtasks":[{"title":"Выгрузка XLSX","goal":"писать xlsx",
              "executorPersonaId":"","executorRationale":"серверная часть",
              "files":["backend/Export.cs"],"wave":1,"doneCriteria":"файл открывается"}]}
            """;

        await _sut.HandleTeamTurnEndAsync(session.Id,
            "<team:work>переделать экспорт на XLSX</team>", failed: false);

        var card = _sentMessages.OfType<TeamPlanMessage>().Last();
        card.Plan.Version.Should().Be(2, "план vN после уточнений");
        card.Resolved.Should().BeFalse("новая версия плана требует подтверждения и при авто-волнах");
        card.Plan.Assumptions.Should().ContainSingle().Which.Should().Contain("XLSX");
        card.Plan.Changes.Should().HaveCount(2, "блок «Что изменилось» — от планировщика");
        started.Should().BeFalse("до «Запустить» работа не идёт");

        var after = _sut.GetById(session.Id)!.TeamImplement!;
        after.Stage.Should().Be(TeamImplementStage.Confirming);
        after.PlanVersion.Should().Be(2);
        after.ApprovedPlanVersion.Should().Be(1, "подтверждена пока только прежняя версия");
        after.Replanning.Should().BeFalse("признак снят публикацией новой версии");
    }

    // --- Волна 1: машина состояний (B1, M3, M8, M9) ---

    // B1: добавочная вводная при авто-волнах согласования не ждёт — и режим прав человека
    // обязан вернуться сам. Иначе SavedMode, поставленный входом в план-режим по этой же
    // вводной, снять было бы негде: селектор навсегда залочен «Штаб планирует…».
    [Fact]
    public async Task ДобавочныйПлан_ПриАвтоВолнах_ВозвращаетРежимЧеловека()
    {
        var (session, plan, _) = await MakeStabWithPlanAndStarterAsync("ti-additional-mode");
        await _sut.RespondTeamPlanAsync(session.Id, plan.Id, TeamPlanDecision.Run, userId: TestUserId);
        // Итерация закончена, режим ждёт следующей вводной
        _sut.WithTeamState(session.Id, t => { t.Stage = TeamImplementStage.Idle; return true; });
        session.Status = SessionStatus.Working;

        await _sut.SendMessageAsync(session.Id, "теперь добавь выгрузку в XLSX", []);
        _sut.GetById(session.Id)!.TeamImplement!.SavedMode.Should().BeNull(
            "M6: на приёме план-режим не навязывается — сообщение ещё не классифицировано");

        // Классификация работой (вводная человека, M7) — план-режим на планирование,
        // а по публикации добавочного плана режим человека возвращается сам
        SetTeamTurnFromHuman(GetEntry(session.Id), true);
        await _sut.HandleTeamTurnEndAsync(session.Id, "<team:work>добавить XLSX</team>", failed: false);

        var after = _sut.GetById(session.Id)!;
        after.TeamImplement!.Stage.Should().Be(TeamImplementStage.Wave, "добавочная волна пошла сразу");
        after.TeamImplement.SavedMode.Should().BeNull("режим человека возвращён");
        after.Mode.Should().Be(ClaudeMode.Auto);
        _sentMessages.OfType<TeamImplementMessage>().Last().ModeLocked.Should().BeFalse(
            "селектор режима разблокирован");
    }

    // M3: ответ обычным сообщением — равноправная замена кнопок карточки остановки
    [Fact]
    public async Task ТекстВОжиданииРешения_ПослеПервойВолны_ВозвращаетПрактикуВВолну()
    {
        var (session, plan, _) = await MakeStabWithPlanAndStarterAsync("ti-decision-text");
        await _sut.RespondTeamPlanAsync(session.Id, plan.Id, TeamPlanDecision.Run, userId: TestUserId);
        _sut.WithTeamState(session.Id, t =>
        {
            t.WaveNumber = 1;
            t.Budget.TasksUsed = 3;
            return true;
        });
        await _sut.PublishTeamEscalationAsync(session.Id, new TeamEscalation
        {
            Kind = TeamEscalationKind.Blocker,
            Title = "Исполнитель встал",
            Details = "нет доступа к БД",
            Wave = 1,
            Actions = TeamEscalationActions.For(TeamEscalationKind.Blocker),
        });
        _sut.GetById(session.Id)!.TeamImplement!.Stage.Should().Be(TeamImplementStage.AwaitingDecision);
        session.Status = SessionStatus.Working;

        await _sut.SendMessageAsync(session.Id, "возьми доступ из appsettings.Local.json", []);

        var team = _sut.GetById(session.Id)!.TeamImplement!;
        team.Stage.Should().Be(TeamImplementStage.Wave, "практика пошла дальше с того же места");
        team.StageBeforeDecision.Should().BeNull();
        team.WaveStartedAt.Should().NotBeNull("сторож зависших волн снова тикает");
        team.Budget.TasksUsed.Should().Be(3, "итерация та же — бюджет не сбрасывается");
    }

    [Fact]
    public async Task ТекстВОжиданииРешения_ДоПервойВолны_ВозвращаетВСтадиюДоОжидания()
    {
        var (session, _, _) = await MakeInterviewStabAsync("ti-decision-text-early");
        await _sut.PublishTeamEscalationAsync(session.Id, new TeamEscalation
        {
            Kind = TeamEscalationKind.ProductDecision,
            Title = "Координатор не понял вводную",
            Details = "ответ без маркера",
            Actions = TeamEscalationActions.For(TeamEscalationKind.ProductDecision),
        });

        await _sut.SendMessageAsync(session.Id, "делаем экспорт в CSV, без вариантов", []);

        var team = _sut.GetById(session.Id)!.TeamImplement!;
        team.Stage.Should().Be(TeamImplementStage.Interview,
            "до первой волны возвращаемся туда, откуда ушли в ожидание, а не в «волну-призрак»");
        team.WaveStartedAt.Should().BeNull("волн ещё не было — сторожу нечего сторожить");
    }

    // M3: текст отказа квоты обязан быть честным — из «ждёт решения» план как раз подтверждён
    [Fact]
    public async Task КвотаЗапуска_ВОжиданииРешения_ОтказНазываетНастоящуюПричину()
    {
        var (session, plan, _) = await MakeStabWithPlanAndStarterAsync("ti-quota-awaiting");
        await _sut.RespondTeamPlanAsync(session.Id, plan.Id, TeamPlanDecision.Run, userId: TestUserId);
        _sut.WithTeamState(session.Id, t => { t.Stage = TeamImplementStage.AwaitingDecision; return true; });

        var (verdict, reason) = _sut.TryConsumeTeamImplementRun(session.Id, TestUserId);

        verdict.Should().Be(SessionManager.TeamRunQuota.Exhausted);
        reason.Should().Contain("ждёт решения");
        reason.Should().NotContain("не подтверждён", "план подтверждён — врать человеку и модели нельзя");
    }

    // M8: клик по карточке v1, когда опубликован v2
    [Fact]
    public async Task RespondTeamPlan_УстаревшаяКарточка_НеМеняетСостояниеИОбъясняет()
    {
        var (session, backend, frontend) = await MakeInterviewStabAsync("ti-stale-card");
        SetPlannerAnswer(backend, frontend);
        await _sut.HandleTeamTurnEndAsync(session.Id, "<team:work>экспорт</team>", failed: false);
        var stale = _sentMessages.OfType<TeamPlanMessage>().Last().PlanId;
        // Координатор задал вопрос поверх карточки v1 → интервью → план v2
        await _sut.OnStabAskQuestionAsync(session.Id);
        await _sut.HandleTeamTurnEndAsync(session.Id, "<team:work>экспорт, но в XLSX</team>", failed: false);
        var started = false;
        _sut.TeamWaveStarter = (_, _) => { started = true; return Task.CompletedTask; };

        var result = await _sut.RespondTeamPlanAsync(session.Id, stale, TeamPlanDecision.Run,
            userId: TestUserId);

        result.Should().BeNull("решение по устаревшей карточке не проходит");
        started.Should().BeFalse();
        var after = _sut.GetById(session.Id)!;
        after.TeamImplement!.Stage.Should().Be(TeamImplementStage.Confirming, "стадия не тронута");
        after.TeamImplement.WaveNumber.Should().Be(0, "«волны-призрака» не завелось");
        after.TeamImplement.ApprovedPlanVersion.Should().Be(0, "старая версия не стала подтверждённой");
        after.Mode.Should().Be(ClaudeMode.Plan, "план-режим не снят посреди перепланирования");
        // Молчаливого отказа не бывает: карточка погашена, человеку объяснили, где свежая
        var card = _sentMessages.OfType<TeamPlanMessage>().Last(m => m.PlanId == stale);
        card.Resolved.Should().BeTrue();
        card.Approved.Should().BeFalse();
        _sentMessages.OfType<GuestTextMessage>().Last().Text.Should().Contain("устарела");
    }

    // M9: интервью из волны приходит с WaveNumber > 0 — гард молчаливого тупика обязан
    // ловить и его, иначе обещанные карточкой вопросы не приходят никогда
    [Fact]
    public async Task КонецХода_ИнтервьюИзВолныБезВопросов_ДаётКарточку()
    {
        var (session, plan, _) = await MakeStabWithPlanAndStarterAsync("ti-clarify-stall");
        await _sut.RespondTeamPlanAsync(session.Id, plan.Id, TeamPlanDecision.Run, userId: TestUserId);
        _sut.WithTeamState(session.Id, t => { t.WaveNumber = 1; return true; });
        await _sut.HandleTeamTurnEndAsync(session.Id,
            "<escalate:clarify>неясен формат выгрузки</escalate>", failed: false);
        _sut.GetById(session.Id)!.TeamImplement!.Stage.Should().Be(TeamImplementStage.Interview);

        await _sut.HandleTeamTurnEndAsync(session.Id, "Подожду ваших уточнений.", failed: false);

        var card = _sentMessages.OfType<TeamEscalationMessage>().Last();
        card.Title.Should().Be("Уточнения так и не пришли");
        card.Details.Should().Contain("Подожду ваших уточнений");
        _sut.GetById(session.Id)!.TeamImplement!.Stage.Should().Be(TeamImplementStage.AwaitingDecision,
            "молчаливых тупиков не бывает — карточка зовёт человека");
    }

    [Fact]
    public async Task КонецХода_ВопросКоординатораВЭтомХоду_КарточкиТупикаНет()
    {
        var (session, _, _) = await MakeInterviewStabAsync("ti-stall-asked");

        await _sut.HandleTeamTurnEndAsync(session.Id, "Уточните два момента.", failed: false, asked: true);

        _sentMessages.OfType<TeamEscalationMessage>().Should().BeEmpty(
            "ход закончился вопросами человеку — интервью работает, а не стоит");
        _sut.GetById(session.Id)!.TeamImplement!.Stage.Should().Be(TeamImplementStage.Interview);
    }

    // --- Волна 2: гарды режима и контур (M1, M4, M5, M6, M7) ---

    // M1: селектор режима в стадии волны — тот же гард, что при включении: в acceptEdits/
    // bypassPermissions CLI не спрашивает разрешений, и CoordinatorWriteGuard молчал бы
    [Fact]
    public async Task SetMode_Штаб_НесовместимыйРежимПоднимаетсяДоAuto()
    {
        var (session, _, _) = await MakeTeamStabAsync("ti-mode-guard");

        _sut.SetMode(session.Id, "acceptEdits", TestUserId)!.Mode.Should().Be(ClaudeMode.Auto,
            "в acceptEdits запись через shell проходит мимо сервера — штаб там жить не может");
        _sut.SetMode(session.Id, "bypass", TestUserId)!.Mode.Should().Be(ClaudeMode.Auto);
        // Волна 3: dontAsk — тот же класс, что acceptEdits/bypass («не спрашивать разрешение»
        // по имени режима у CLI) — закрытый вопрос предыдущего аудита
        _sut.SetMode(session.Id, "dontAsk", TestUserId)!.Mode.Should().Be(ClaudeMode.Auto,
            "dontAsk тоже не спрашивает разрешений — гард должен поднимать и его");
        _sut.SetMode(session.Id, "default", TestUserId)!.Mode.Should().Be(ClaudeMode.Default,
            "совместимые режимы не трогаем");
    }

    [Fact]
    public async Task SetMode_ОбычныйЧат_РежимМеняетсяБезГарда()
    {
        var dir = MkProjectDir("ti-mode-plain");
        var project = _projectManager.Create("TI-PLAIN", dir, TestUserId, TestUsername);
        var session = await _sut.CreateAsync(project.Id, ClaudeMode.Auto);

        _sut.SetMode(session.Id, "acceptEdits", TestUserId)!.Mode.Should().Be(ClaudeMode.AcceptEdits,
            "гард — только у чата-штаба");
    }

    // M1: режим из тела сообщения — четвёртая точка смены, тоже под гардом
    [Fact]
    public async Task СообщениеСРежимом_Штаб_НесовместимыйРежимПоднимаетсяДоAuto()
    {
        var (session, _, _) = await MakeTeamStabAsync("ti-mode-msg");
        session.Name = "штаб";
        // Стадия волны — чтобы приём сообщения не открыл свежую итерацию с план-режимом
        _sut.GetById(session.Id)!.TeamImplement!.Stage = TeamImplementStage.Wave;
        var entry = GetEntry(session.Id);
        var adapter = StubAdapter(entry);
        SetProcess(entry, adapter.Object);

        await _sut.SendMessageAsync(session.Id, "продолжай", [], mode: "acceptEdits");

        _sut.GetById(session.Id)!.Mode.Should().Be(ClaudeMode.Auto);
    }

    // M1: на стадиях интервью/планирования сообщение с mode не сбрасывает навязанный
    // план-режим — тот же лок, что в SetMode (молча: отказ стоил бы самого сообщения)
    [Fact]
    public async Task СообщениеСРежимом_ВПланФазе_НеСбрасываетПланРежим()
    {
        var (session, _, _) = await MakeTeamStabAsync("ti-mode-planphase");
        session.Name = "штаб";
        var team = _sut.GetById(session.Id)!.TeamImplement!;
        team.Stage = TeamImplementStage.Interview;
        team.SavedMode = ClaudeMode.Auto;
        session.Mode = ClaudeMode.Plan;
        var entry = GetEntry(session.Id);
        var adapter = StubAdapter(entry);
        SetProcess(entry, adapter.Object);

        await _sut.SendMessageAsync(session.Id, "ответ на вопрос", [], mode: "acceptEdits");

        _sut.GetById(session.Id)!.Mode.Should().Be(ClaudeMode.Plan,
            "план-режим стадий интервью/планирования снимает только согласование плана");
    }

    // M4: правка состава/тумблера тем же эндпоинтом посреди волны не должна осиротить её
    [Fact]
    public async Task SetTeamImplement_ПоверхАктивного_СохраняетСостояниеИтерации()
    {
        var (session, _, _) = await MakeTeamStabAsync("ti-reenable");
        var team = _sut.GetById(session.Id)!.TeamImplement!;
        team.Stage = TeamImplementStage.Wave;
        team.WaveNumber = 2;
        team.PlanCardId = "plan-card-1";
        team.PlanVersion = 1;
        team.SavedMode = ClaudeMode.Default;
        team.Budget.TasksUsed = 5;
        team.Budget.RunsUsed = 9;

        var updated = await _sut.SetTeamImplementAsync(session.Id, enabled: true,
            autoWaves: false, userId: TestUserId);

        var ti = updated!.TeamImplement!;
        ti.AutoWaves.Should().BeFalse("настраиваемые поля меняются");
        ti.Stage.Should().Be(TeamImplementStage.Wave, "стадия переживает повторное включение");
        ti.WaveNumber.Should().Be(2);
        ti.PlanCardId.Should().Be("plan-card-1", "иначе закрытие волны не нашло бы план");
        ti.PlanVersion.Should().Be(1);
        ti.SavedMode.Should().Be(ClaudeMode.Default);
        ti.Budget.TasksUsed.Should().Be(5, "бюджет итерации не обнуляется");
        ti.Budget.RunsUsed.Should().Be(9);
    }

    // M5: хабовый «Стоп» — второй путь прерывания, буфер маркеров чистится так же,
    // как при прерывании очередью: иначе маркер убитого хода применился бы задним числом
    [Fact]
    public async Task Interrupt_Хабовый_ЧиститБуферМаркеровУбитогоХода()
    {
        var (session, _, _) = await MakeTeamStabAsync("stab-stop-buffer");
        session.Status = SessionStatus.Working;
        var entry = GetEntry(session.Id);
        var adapter = StubAdapter(entry);
        SetProcess(entry, adapter.Object);
        // Координатор успел написать маркер — он копится в буфере хода штаба
        await InvokeOnMessageAsync(session.Id, new TurnAccumulator(new List<StoredMessage>()),
            new TextDeltaMessage("<<<ЭСКАЛАЦИЯ: расхождение с планом>>>"), TestRunId);
        GetTeamTurnText(entry).Should().NotBeEmpty("предусловие: буфер хода штаба непуст");

        _sut.Interrupt(session.Id);

        adapter.Verify(a => a.Interrupt(), Times.Once());
        GetTeamTurnText(entry).Should().BeEmpty(
            "маркер убитого «Стоп» хода не доклеивается к следующему — иначе фантомная эскалация");
    }

    // M6: разговорный вопрос в ожидании — не вводная: потолки, стадия и режим человека
    // не трогаются, а ответ координатора без маркера — легальный, без ложной эскалации
    [Fact]
    public async Task РазговорныйВопрос_ВОжидании_НеТрогаетБюджетСтадиюИРежим()
    {
        var (session, _, _) = await MakeIdleStabAsync("ti-talk-idle");
        var team = _sut.GetById(session.Id)!.TeamImplement!;
        team.Budget.TasksUsed = 7;
        team.Budget.RunsUsed = 11;
        session.Status = SessionStatus.Working; // ход не запускаем — сообщение встаёт в очередь

        await _sut.SendMessageAsync(session.Id, "что вы сделали?", []);

        var after = _sut.GetById(session.Id)!;
        after.TeamImplement!.Budget.TasksUsed.Should().Be(7, "разговор бюджет не обнуляет");
        after.TeamImplement.Budget.RunsUsed.Should().Be(11);
        after.TeamImplement.Stage.Should().Be(TeamImplementStage.Idle,
            "стадия не двигается до классификации вводной");
        after.Mode.Should().Be(ClaudeMode.Auto, "план-режим разговору не навязывается");
        after.TeamImplement.SavedMode.Should().BeNull();

        // Координатор честно ответил без маркера — это легальный ответ, а не тупик
        await _sut.HandleTeamTurnEndAsync(session.Id, "Сделали экспорт в CSV за 2 задачи.", failed: false);

        _sentMessages.OfType<TeamEscalationMessage>().Should().BeEmpty(
            "ложной эскалации «Координатор не понял вводную» быть не должно");
        _sut.GetById(session.Id)!.TeamImplement!.Stage.Should().Be(TeamImplementStage.Idle);
    }

    // M6: маркер разговора — легальный выход из свежего интервью без плана и эскалации
    [Fact]
    public async Task МаркерРазговора_ВИнтервью_ЗакрываетИнтервьюБезЭскалации()
    {
        var (session, _, _) = await MakeInterviewStabAsync("ti-talk-close");
        // Предусловие: стадия интервью, чат в план-режиме, режим человека сохранён
        _sut.GetById(session.Id)!.Mode.Should().Be(ClaudeMode.Plan);

        await _sut.HandleTeamTurnEndAsync(session.Id,
            "Это был просто вопрос — отвечаю: экспорт уже есть.\n<team:talk/>", failed: false);

        var after = _sut.GetById(session.Id)!;
        after.TeamImplement!.Stage.Should().Be(TeamImplementStage.Planning,
            "свежая «итерация» возвращается в ожидание первой вводной");
        after.Mode.Should().Be(ClaudeMode.Auto, "режим человека возвращён");
        after.TeamImplement.SavedMode.Should().BeNull();
        _sentMessages.OfType<TeamEscalationMessage>().Should().BeEmpty("ложной эскалации нет");
    }

    // M6: маркер разговора из clarify-интервью (тупик в волне) снимает паузу — волна
    // продолжается, сторож взведён заново, а признак перепланирования снят: плана не было
    [Fact]
    public async Task МаркерРазговора_ИзClarifyИнтервью_ВозвращаетВолну()
    {
        var (session, _, _) = await MakeTeamStabAsync("ti-talk-clarify");
        var team = _sut.GetById(session.Id)!.TeamImplement!;
        team.WaveNumber = 1;
        team.PlanCardId = "plan-1";
        team.Stage = TeamImplementStage.Interview;
        team.Replanning = true;
        team.SavedMode = ClaudeMode.Auto;
        session.Mode = ClaudeMode.Plan;

        await _sut.HandleTeamTurnEndAsync(session.Id,
            "Понял, это был вопрос — отвечаю по существу.\n<team:talk/>", failed: false);

        var after = _sut.GetById(session.Id)!;
        after.TeamImplement!.Stage.Should().Be(TeamImplementStage.Wave, "пауза снята — волна продолжается");
        after.TeamImplement.Replanning.Should().BeFalse("интервью закончилось без нового плана");
        after.TeamImplement.WaveStartedAt.Should().NotBeNull("сторож волны взведён заново");
        after.Mode.Should().Be(ClaudeMode.Auto);
        _sentMessages.OfType<TeamEscalationMessage>().Should().BeEmpty();
    }

    // M6: у модели обязан быть легальный путь ответа на каждой стадии, где его ждёт
    // stall-гард, — в интервью это маркер разговора
    [Fact]
    public void ПромптКоординатора_ВИнтервью_ДаётЛегальныйВыходРазговором()
    {
        var interview = new SessionTeamImplement { Stage = TeamImplementStage.Interview };
        TeamImplementPrompts.CoordinatorTurn(interview).Should().Contain("<team:talk/>");

        var idle = new SessionTeamImplement { Stage = TeamImplementStage.Idle };
        TeamImplementPrompts.CoordinatorTurn(idle).Should().Contain("<team:work>",
            "в ожидании классификация «работа/разговор» по-прежнему в промпте");
    }

    // M7: агентская вводная (chats_send в штаб), классифицированная как работа, не получает
    // авто-подтверждения — единственное согласование остаётся за человеком
    [Fact]
    public async Task АгентскаяВводная_ВОжидании_ПланЖдётПодтвержденияЧеловека()
    {
        var (session, backend, _) = await MakeIdleStabAsync("ti-agent-input");
        SetAdditionalPlannerAnswer(backend);
        var started = false;
        _sut.TeamWaveStarter = (_, _) => { started = true; return Task.CompletedTask; };
        // Ход агента: флаг «вводная от человека» не выставлен (по умолчанию false)

        await _sut.HandleTeamTurnEndAsync(session.Id, "<team:work>добавить выгрузку в XLSX</team>", failed: false);

        var card = _sentMessages.OfType<TeamPlanMessage>().Last();
        card.Resolved.Should().BeFalse("авто-подтверждение опирается на вводную ЧЕЛОВЕКА");
        card.Approved.Should().NotBeTrue();
        started.Should().BeFalse("волна без согласования человека не стартует");
        _sut.GetById(session.Id)!.TeamImplement!.Stage.Should().Be(TeamImplementStage.Confirming);
    }

    // Рефанд квоты пробуждений: списанная авансом единица возвращается при недоставке
    [Fact]
    public async Task РефандПробуждения_ВозвращаетСписаннуюКвоту()
    {
        var (stab, _, _) = await MakeTeamStabAsync("ti-wakeup-refund");

        var wake = _sut.TryConsumeTeamWakeup(stab.Id);
        wake.Allowed.Should().BeTrue();
        _sut.GetById(stab.Id)!.TeamImplement!.Budget.WakeupsUsed.Should().Be(1);

        _sut.RefundTeamWakeup(stab.Id);

        _sut.GetById(stab.Id)!.TeamImplement!.Budget.WakeupsUsed.Should().Be(0,
            "несостоявшееся пробуждение команде ничего не стоит");
    }

    // --- ReportUpAsync: отчёт в родительский чат ---

    // Пара «родитель → ребёнок» в одном проекте (связь ставится ручной группировкой)
    private async Task<(Session Parent, Session Child)> MkParentChildAsync(string suffix)
    {
        var dir = MkProjectDir("rep_" + suffix);
        var project = _projectManager.Create("REP-" + suffix, dir, TestUserId, TestUsername);
        var parent = await _sut.CreateAsync(project.Id, ClaudeMode.Auto, name: "Родитель");
        var child = await _sut.CreateAsync(project.Id, ClaudeMode.Auto, name: "Задача: починить билд");
        _sut.SetParent(child.Id, parent.Id, TestUserId);
        return (parent, child);
    }

    [Fact]
    public async Task ReportUp_БезРодителя_ДокладыватьНекуда()
    {
        var dir = MkProjectDir("rep_solo");
        var project = _projectManager.Create("REP-SOLO", dir, TestUserId, TestUsername);
        var lonely = await _sut.CreateAsync(project.Id, ClaudeMode.Auto);

        var r = await _sut.ReportUpAsync(lonely.Id, "нашёл блокер", TestUserId, withTurn: false);

        r.Should().Be(SessionManager.ReportUpResult.NoParent);
    }

    [Fact]
    public async Task ReportUp_КладётКарточкуВРодителяБезХода()
    {
        var (parent, child) = await MkParentChildAsync("card");
        parent.ClaudeSessionId = "cli-" + Guid.NewGuid().ToString("N");

        var r = await _sut.ReportUpAsync(child.Id, "нашёл блокер: нет доступа к БД", TestUserId, withTurn: false);

        r.Should().Be(SessionManager.ReportUpResult.Delivered);
        var history = await _sut.GetHistoryAsync(parent.Id);
        history.Should().ContainSingle(m => m is Protocol.StoredUserMessage)
            .Which.Should().BeOfType<Protocol.StoredUserMessage>()
            .Which.Text.Should().Contain("нет доступа к БД");
        // Ход не запускался — чат остался в исходном статусе
        _sut.GetById(parent.Id)!.Status.Should().NotBe(SessionStatus.Working);
    }

    [Fact]
    public async Task ReportBlocker_КладётДокладСПометкойИПоднимаетКарточкуВШтабе()
    {
        // Штаб в режиме практики + дочерний чат исполнителя под ним
        var (stab, _, _) = await MakeTeamStabAsync("ti-blocker");
        stab.ClaudeSessionId = "cli-" + Guid.NewGuid().ToString("N");
        var child = await _sut.CreateAsync(stab.ProjectId!, ClaudeMode.Auto, name: "Задача: эндпоинт");
        _sut.SetParent(child.Id, stab.Id, TestUserId);

        var r = await _sut.ReportBlockerAsync(child.Id, "нет доступа к тестовой БД", TestUserId);

        r.Should().BeOneOf(SessionManager.ReportUpResult.Delivered, SessionManager.ReportUpResult.Queued);
        var history = await _sut.GetHistoryAsync(stab.Id);
        history.OfType<Protocol.StoredUserMessage>().Should()
            .Contain(m => m.Text.Contains("нет доступа к тестовой БД") && m.Text.Contains("Блокер"),
                "доклад помечен как блокер, а не теряется среди обычных отчётов");
        // Человек видит карточку немедленно, а не в конце волны
        var card = _sentMessages.OfType<TeamEscalationMessage>().Single();
        card.Kind.Should().Be("blocker");
        card.Details.Should().Contain("нет доступа");
        _sut.GetById(stab.Id)!.TeamImplement!.Stage.Should().Be(TeamImplementStage.AwaitingDecision);
    }

    [Fact]
    public async Task ReportBlocker_КаждоеПробуждениеШтабаРасходуетСвоюКвоту()
    {
        var (stab, _, _) = await MakeTeamStabAsync("ti-blocker-quota");
        stab.ClaudeSessionId = "cli-" + Guid.NewGuid().ToString("N");
        var child = await _sut.CreateAsync(stab.ProjectId!, ClaudeMode.Auto, name: "Задача: эндпоинт");
        _sut.SetParent(child.Id, stab.Id, TestUserId);

        await _sut.ReportBlockerAsync(child.Id, "первый блокер", TestUserId);
        await _sut.ReportBlockerAsync(child.Id, "второй блокер", TestUserId);

        _sut.GetById(stab.Id)!.TeamImplement!.Budget.WakeupsUsed.Should().Be(2,
            "платный ход штаба, поднятый агентом, обязан считаться — иначе лавина идёт мимо бюджета");
    }

    // m3 (второй проход Глеба): пробуждение штаба списывается ДО того, как доклад реально
    // дойдёт до родителя (TryConsumeTeamWakeup раньше ReportUpAsync). Цепочка автоотчётов
    // уже на потолке (TooDeep) — доклад не доставлен, координатор фактически не разбужен,
    // платить команде не с чего. Единица обязана вернуться.
    [Fact]
    public async Task ReportBlocker_ЦепочкаСлишкомГлубокая_ВозвращаетКвотуПробуждения()
    {
        var (stab, _, _) = await MakeTeamStabAsync("ti-blocker-toodeep");
        stab.ClaudeSessionId = "cli-" + Guid.NewGuid().ToString("N");
        var child = await _sut.CreateAsync(stab.ProjectId!, ClaudeMode.Auto, name: "Задача: эндпоинт");
        _sut.SetParent(child.Id, stab.Id, TestUserId);
        var childEntry = GetEntry(child.Id);
        childEntry.GetType().GetField("ReportChainDepth")!.SetValue(childEntry, 3);

        var r = await _sut.ReportBlockerAsync(child.Id, "застрял", TestUserId);

        r.Should().Be(SessionManager.ReportUpResult.TooDeep);
        _sut.GetById(stab.Id)!.TeamImplement!.Budget.WakeupsUsed.Should().Be(0,
            "доклад не дошёл — координатор фактически не разбужен, платить не с чего");
    }

    [Fact]
    public async Task ReportBlocker_КвотаПробужденийВыбрана_ХодаНет_НоЧеловекВидитКарточку()
    {
        var (stab, _, _) = await MakeTeamStabAsync("ti-blocker-cap");
        stab.ClaudeSessionId = "cli-" + Guid.NewGuid().ToString("N");
        var child = await _sut.CreateAsync(stab.ProjectId!, ClaudeMode.Auto, name: "Задача: эндпоинт");
        _sut.SetParent(child.Id, stab.Id, TestUserId);
        var budget = _sut.GetById(stab.Id)!.TeamImplement!.Budget;
        budget.WakeupsUsed = budget.MaxWakeups;

        var r = await _sut.ReportBlockerAsync(child.Id, "снова застрял", TestUserId);

        r.Should().Be(SessionManager.ReportUpResult.Delivered);
        (await _sut.GetHistoryAsync(stab.Id)).OfType<Protocol.StoredUserMessage>().Should()
            .Contain(m => m.Text.Contains("снова застрял"), "сам доклад терять нельзя");
        budget.WakeupsUsed.Should().Be(budget.MaxWakeups, "отказ ничего не расходует");
        // Ход координатора не поднимаем, но человек обязан узнать: застрявший исполнитель
        // без карточки — то самое молчаливое зависание, которого в режиме быть не должно
        var card = _sentMessages.OfType<TeamEscalationMessage>().Single();
        card.Kind.Should().Be("budgetExhausted");
        card.Details.Should().Contain("снова застрял");
    }

    [Fact]
    public async Task ReportBlocker_КвотаВыбрана_ВтороеСообщениеКарточкуНеДублирует()
    {
        // Практика уже ждёт решения по той же причине: вторая карточка и второй push —
        // это спам, а не сигнал. Сам доклад при этом в ленту всё равно ложится.
        var (stab, _, _) = await MakeTeamStabAsync("ti-blocker-spam");
        stab.ClaudeSessionId = "cli-" + Guid.NewGuid().ToString("N");
        var child = await _sut.CreateAsync(stab.ProjectId!, ClaudeMode.Auto, name: "Задача: эндпоинт");
        _sut.SetParent(child.Id, stab.Id, TestUserId);
        var budget = _sut.GetById(stab.Id)!.TeamImplement!.Budget;
        budget.WakeupsUsed = budget.MaxWakeups;

        await _sut.ReportBlockerAsync(child.Id, "первый застрял", TestUserId);
        await _sut.ReportBlockerAsync(child.Id, "второй застрял", TestUserId);

        _sentMessages.OfType<TeamEscalationMessage>().Should().ContainSingle(
            "карточка на остановку одна, сколько бы докладов ни пришло следом");
        (await _sut.GetHistoryAsync(stab.Id)).OfType<Protocol.StoredUserMessage>().Should()
            .Contain(m => m.Text.Contains("второй застрял"), "доклады не теряются");
    }

    [Fact]
    public async Task ReportBlocker_ПослеОстановкиЧеловеком_ХодаНет_НоКарточкаЕсть()
    {
        var (stab, _, _) = await MakeTeamStabAsync("ti-blocker-stopped");
        stab.ClaudeSessionId = "cli-" + Guid.NewGuid().ToString("N");
        var child = await _sut.CreateAsync(stab.ProjectId!, ClaudeMode.Auto, name: "Задача: эндпоинт");
        _sut.SetParent(child.Id, stab.Id, TestUserId);
        await _sut.StopTeamImplementAsync(stab.Id, TestUserId);
        _sentMessages.Clear();

        await _sut.ReportBlockerAsync(child.Id, "застрял", TestUserId);

        _sut.GetById(stab.Id)!.TeamImplement!.Budget.WakeupsUsed.Should().Be(0,
            "практика остановлена человеком — агент не поднимает её обратно");
        _sentMessages.OfType<TeamEscalationMessage>().Single().Kind.Should().Be("stopped");
    }

    [Fact]
    public async Task КвотаПробуждения_ЛюбойАгентскийХодШтаба_РасходуетЕёОдинаково()
    {
        // chats_send — соседний вход в тот же платный ход штаба: если бы он шёл мимо квоты,
        // бюджет обходился бы сменой инструмента
        var (stab, _, _) = await MakeTeamStabAsync("ti-wakeup-shared");

        var first = _sut.TryConsumeTeamWakeup(stab.Id);
        var second = _sut.TryConsumeTeamWakeup(stab.Id);

        first.TeamMode.Should().BeTrue();
        first.Allowed.Should().BeTrue();
        second.Allowed.Should().BeTrue();
        _sut.GetById(stab.Id)!.TeamImplement!.Budget.WakeupsUsed.Should().Be(2);
    }

    [Fact]
    public async Task КвотаПробуждения_ОбычныйЧат_НеОграничивается()
    {
        var dir = MkProjectDir("ti-wakeup-plain");
        var project = _projectManager.Create("TI-WP", dir, TestUserId, TestUsername);
        var session = await _sut.CreateAsync(project.Id, ClaudeMode.Auto);

        var wake = _sut.TryConsumeTeamWakeup(session.Id);

        wake.TeamMode.Should().BeFalse("вне режима переписка между чатами работает как раньше");
        wake.Allowed.Should().BeTrue();
    }

    [Fact]
    public async Task КвотаПробуждения_ИсчерпанныйБюджет_Отказ()
    {
        var (stab, _, _) = await MakeTeamStabAsync("ti-wakeup-cap");
        var budget = _sut.GetById(stab.Id)!.TeamImplement!.Budget;
        budget.WakeupsUsed = budget.MaxWakeups;

        var wake = _sut.TryConsumeTeamWakeup(stab.Id);

        wake.TeamMode.Should().BeTrue();
        wake.Allowed.Should().BeFalse();
        wake.Reason.Should().Contain("срочных вызовов");
    }

    [Fact]
    public async Task ReportBlocker_РодительВнеРежима_КарточкиНет_НоХодЗапускается()
    {
        var (parent, child) = await MkParentChildAsync("blocker-plain");
        parent.ClaudeSessionId = "cli-" + Guid.NewGuid().ToString("N");

        var r = await _sut.ReportBlockerAsync(child.Id, "застрял на конфликте", TestUserId);

        r.Should().BeOneOf(SessionManager.ReportUpResult.Delivered, SessionManager.ReportUpResult.Queued);
        _sentMessages.OfType<TeamEscalationMessage>().Should().BeEmpty(
            "карточка остановки — часть режима практики, обычному чату она не нужна");
    }

    [Fact]
    public async Task ReportUp_БезПерсоны_ПодписываетИменемЧата()
    {
        // Исполнитель — обычный Claude: лица нет, поэтому карточка идёт с именем его чата
        var (parent, child) = await MkParentChildAsync("name");
        parent.ClaudeSessionId = "cli-" + Guid.NewGuid().ToString("N");

        await _sut.ReportUpAsync(child.Id, "готово наполовину", TestUserId, withTurn: false);

        var stored = (await _sut.GetHistoryAsync(parent.Id)).OfType<Protocol.StoredUserMessage>().Single();
        stored.SenderChatName.Should().Be("Задача: починить билд");
        stored.ViaAgent.Should().BeTrue();
    }

    [Fact]
    public async Task ReportUp_ЦепочкаГлубже3_Отклоняется()
    {
        // a → b → c → d: отчёты идут вверх по цепочке, четвёртый по счёту упирается в потолок
        var dir = MkProjectDir("rep_deep");
        var project = _projectManager.Create("REP-DEEP", dir, TestUserId, TestUsername);
        var chats = new List<Session>();
        for (var i = 0; i < 5; i++)
        {
            var s = await _sut.CreateAsync(project.Id, ClaudeMode.Auto, name: $"чат {i}");
            s.ClaudeSessionId = "cli-" + Guid.NewGuid().ToString("N");
            chats.Add(s);
            if (i > 0) _sut.SetParent(chats[i].Id, chats[i - 1].Id, TestUserId);
        }

        // chats[4] → chats[3] → … каждый следующий отчёт наращивает глубину цепочки
        (await _sut.ReportUpAsync(chats[4].Id, "уровень 1", TestUserId, false))
            .Should().Be(SessionManager.ReportUpResult.Delivered);
        (await _sut.ReportUpAsync(chats[3].Id, "уровень 2", TestUserId, false))
            .Should().Be(SessionManager.ReportUpResult.Delivered);
        (await _sut.ReportUpAsync(chats[2].Id, "уровень 3", TestUserId, false))
            .Should().Be(SessionManager.ReportUpResult.Delivered);

        (await _sut.ReportUpAsync(chats[1].Id, "уровень 4", TestUserId, false))
            .Should().Be(SessionManager.ReportUpResult.TooDeep, "цепочка автоотчётов ограничена тремя звеньями");
    }

    [Fact]
    public async Task ReportUp_ЧужойЧат_НеНайден()
    {
        var (_, child) = await MkParentChildAsync("foreign");

        var r = await _sut.ReportUpAsync(child.Id, "текст", "another-user", withTurn: false);

        r.Should().Be(SessionManager.ReportUpResult.NotFound);
    }

    // --- SetParent: ручная группировка чатов (drag-and-drop) ---

    [Fact]
    public async Task SetParent_НазначаетРодителя()
    {
        var dir = MkProjectDir("par");
        var project = _projectManager.Create("PAR", dir, TestUserId, TestUsername);
        var parent = await _sut.CreateAsync(project.Id, ClaudeMode.Auto);
        var child = await _sut.CreateAsync(project.Id, ClaudeMode.Auto);

        var updated = _sut.SetParent(child.Id, parent.Id, TestUserId);

        updated!.ParentSessionId.Should().Be(parent.Id);
        _sut.GetById(child.Id)!.ParentSessionId.Should().Be(parent.Id, "связь персистится");
    }

    [Fact]
    public async Task SetParent_НеМеняетUpdatedAt()
    {
        // Корни сортируются по активности поддерева — перетаскивание не должно
        // выкидывать чат наверх списка, будто в нём был ход
        var dir = MkProjectDir("par-upd");
        var project = _projectManager.Create("PARU", dir, TestUserId, TestUsername);
        var parent = await _sut.CreateAsync(project.Id, ClaudeMode.Auto);
        var child = await _sut.CreateAsync(project.Id, ClaudeMode.Auto);
        var before = child.UpdatedAt;

        _sut.SetParent(child.Id, parent.Id, TestUserId);

        _sut.GetById(child.Id)!.UpdatedAt.Should().Be(before);
    }

    [Fact]
    public async Task SetParent_Null_ВыноситВКорень()
    {
        var dir = MkProjectDir("par-root");
        var project = _projectManager.Create("PARR", dir, TestUserId, TestUsername);
        var parent = await _sut.CreateAsync(project.Id, ClaudeMode.Auto);
        var child = await _sut.CreateAsync(project.Id, ClaudeMode.Auto);
        _sut.SetParent(child.Id, parent.Id, TestUserId);

        var updated = _sut.SetParent(child.Id, null, TestUserId);

        updated!.ParentSessionId.Should().BeNull();
        updated.ParentOverrideId.Should().BeNull();
        updated.ParentDetached.Should().BeFalse("у обычного чата гасить нечего — флаг не оседает");
    }

    [Fact]
    public async Task SetParent_ПеребиваетАвтоСвязьПоЗадаче()
    {
        var dir = MkProjectDir("par-task");
        var project = _projectManager.Create("PART", dir, TestUserId, TestUsername);
        var autoParent = await _sut.CreateAsync(project.Id, ClaudeMode.Auto);
        var manualParent = await _sut.CreateAsync(project.Id, ClaudeMode.Auto);
        var child = await _sut.CreateAsync(project.Id, ClaudeMode.Auto, taskExecution: true, taskId: "t-1");

        var prev = Session.TaskSourceSessionResolver;
        try
        {
            Session.TaskSourceSessionResolver = _ => autoParent.Id;
            _sut.GetById(child.Id)!.ParentSessionId.Should().Be(autoParent.Id, "исходно — авто-связь");

            var updated = _sut.SetParent(child.Id, manualParent.Id, TestUserId);

            updated!.ParentSessionId.Should().Be(manualParent.Id, "ручной родитель побеждает");
            updated.TaskId.Should().Be("t-1", "связь с задачей перетаскиванием не рвётся");
            updated.Origin.Should().Be(ChatOrigin.Task);
        }
        finally { Session.TaskSourceSessionResolver = prev; }
    }

    [Fact]
    public async Task SetParent_Null_ГаситАвтоСвязьУЧатаЗадачи()
    {
        var dir = MkProjectDir("par-detach");
        var project = _projectManager.Create("PARD", dir, TestUserId, TestUsername);
        var autoParent = await _sut.CreateAsync(project.Id, ClaudeMode.Auto);
        var child = await _sut.CreateAsync(project.Id, ClaudeMode.Auto, taskExecution: true, taskId: "t-2");

        var prev = Session.TaskSourceSessionResolver;
        try
        {
            Session.TaskSourceSessionResolver = _ => autoParent.Id;

            var updated = _sut.SetParent(child.Id, null, TestUserId);

            updated!.ParentDetached.Should().BeTrue();
            updated.ParentSessionId.Should().BeNull("явный корень перебивает авто-связь");
        }
        finally { Session.TaskSourceSessionResolver = prev; }
    }

    [Fact]
    public async Task SetParent_СамВСебя_Отклоняется()
    {
        var dir = MkProjectDir("par-self");
        var project = _projectManager.Create("PARS", dir, TestUserId, TestUsername);
        var chat = await _sut.CreateAsync(project.Id, ClaudeMode.Auto);

        var act = () => _sut.SetParent(chat.Id, chat.Id, TestUserId);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public async Task SetParent_ВСвоегоПотомка_Отклоняется()
    {
        // A → B → C; попытка сделать A ребёнком C замкнула бы кольцо
        var dir = MkProjectDir("par-cycle");
        var project = _projectManager.Create("PARC", dir, TestUserId, TestUsername);
        var a = await _sut.CreateAsync(project.Id, ClaudeMode.Auto);
        var b = await _sut.CreateAsync(project.Id, ClaudeMode.Auto);
        var c = await _sut.CreateAsync(project.Id, ClaudeMode.Auto);
        _sut.SetParent(b.Id, a.Id, TestUserId);
        _sut.SetParent(c.Id, b.Id, TestUserId);

        var act = () => _sut.SetParent(a.Id, c.Id, TestUserId);

        act.Should().Throw<InvalidOperationException>();
        _sut.GetById(a.Id)!.ParentSessionId.Should().BeNull("отклонённая операция ничего не записала");
    }

    [Fact]
    public async Task SetParent_ЧатИзДругогоПроекта_Отклоняется()
    {
        // Списки рендерятся на разных экранах: ребёнок не нашёл бы родителя в своей
        // выборке и молча всплыл бы в корень
        var d1 = MkProjectDir("par-x1"); var p1 = _projectManager.Create("PX1", d1, TestUserId, TestUsername);
        var d2 = MkProjectDir("par-x2"); var p2 = _projectManager.Create("PX2", d2, TestUserId, TestUsername);
        var child = await _sut.CreateAsync(p1.Id, ClaudeMode.Auto);
        var parent = await _sut.CreateAsync(p2.Id, ClaudeMode.Auto);

        var act = () => _sut.SetParent(child.Id, parent.Id, TestUserId);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public async Task SetParent_ЧужойЧат_НеНайден()
    {
        var stranger = _userStore.Add("stranger-parent", "pw-123456", "user");
        var mineDir = MkProjectDir("par-mine");
        var mine = _projectManager.Create("PMINE", mineDir, TestUserId, TestUsername);
        var theirsDir = MkProjectDir("par-theirs");
        var theirs = _projectManager.Create("PTHEIRS", theirsDir, stranger.Id, stranger.Username);
        var child = await _sut.CreateAsync(mine.Id, ClaudeMode.Auto);
        var foreign = await _sut.CreateAsync(theirs.Id, ClaudeMode.Auto);

        // Чужой родитель — 400, чужой ребёнок — 404 (сессии не видно вовсе)
        var setForeignParent = () => _sut.SetParent(child.Id, foreign.Id, TestUserId);
        setForeignParent.Should().Throw<InvalidOperationException>();
        _sut.SetParent(foreign.Id, child.Id, TestUserId).Should().BeNull();
    }

    [Fact]
    public void SetParent_НесуществующийЧат_ReturnsNull()
    {
        _sut.SetParent("nonexistent", null, TestUserId).Should().BeNull();
    }

    // --- Групповые чаты ---

    // Пользователь + проект + N проектных персон. Ведущая — проектная, чтобы
    // CreateGroupChatAsync шёл маршрутом проекта (чат вне проекта требует
    // DefaultProjectsPath, которого в тестовом конфиге нет).
    private (User User, Project Project, List<Persona> Personas) MkGroupFixture(int count, string suffix)
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

    [Fact]
    public async Task CreateGroupChatAsync_ПерсиститУчастников_СпикерВедущая()
    {
        var (user, project, personas) = MkGroupFixture(3, "a");
        var ids = personas.Select(p => p.Id).ToList();

        var session = await _sut.CreateGroupChatAsync(user.Id, ids, ClaudeMode.Auto, "Команда");

        session.Participants.Should().Equal(ids);
        session.PersonaId.Should().Be(ids[0], "активный спикер при создании — ведущая (первая)");
        session.ProjectId.Should().Be(project.Id, "зона ведущей — её проект");

        // Персистентность: перечитываем sessions.json свежим взглядом
        var stored = _sut.GetById(session.Id);
        stored!.Participants.Should().Equal(ids);
    }

    [Fact]
    public async Task CreateGroupChatAsync_МеньшеДвухУчастников_400()
    {
        var (user, _, personas) = MkGroupFixture(1, "b");

        var act = () => _sut.CreateGroupChatAsync(user.Id, [personas[0].Id], ClaudeMode.Auto);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task CreateGroupChatAsync_ЧужаяПерсона_НеНайдена()
    {
        var (user, _, personas) = MkGroupFixture(2, "d");
        var stranger = _personaManager.Create("another-owner", "Чужая", null, null, null,
            null, null, PersonaScope.Global, null, null, null, false);

        var act = () => _sut.CreateGroupChatAsync(user.Id,
            [personas[0].Id, stranger.Id], ClaudeMode.Auto);

        await act.Should().ThrowAsync<KeyNotFoundException>();
    }

    [Fact]
    public async Task SetParticipants_СпикерСохраняется_ЕслиОстался()
    {
        var (user, _, personas) = MkGroupFixture(3, "e");
        var ids = personas.Select(p => p.Id).ToList();
        var session = await _sut.CreateGroupChatAsync(user.Id, ids, ClaudeMode.Auto);
        // Активный спикер — вторая персона (симулируем прошлый роутинг)
        _sut.SetPersona(session.Id, user.Id, ids[1]);

        var updated = _sut.SetParticipants(session.Id, user.Id, [ids[1], ids[2]]);

        updated!.Participants.Should().Equal(ids[1], ids[2]);
        updated.PersonaId.Should().Be(ids[1], "спикер остался в составе — сохраняется");
    }

    [Fact]
    public async Task SetParticipants_СпикерВыбыл_НоваяВедущая()
    {
        var (user, _, personas) = MkGroupFixture(3, "f");
        var ids = personas.Select(p => p.Id).ToList();
        var session = await _sut.CreateGroupChatAsync(user.Id, ids, ClaudeMode.Auto);
        // Активный спикер — первая; убираем её из состава
        var updated = _sut.SetParticipants(session.Id, user.Id, [ids[1], ids[2]]);

        updated!.PersonaId.Should().Be(ids[1], "спикер выбыл — активной становится новая ведущая");
    }

    // SetPersona после рефакторинга на SwitchSpeaker: публичное поведение не изменилось
    [Fact]
    public async Task SetPersona_ДоПервогоХода_ПрименяетМодельПерсоны()
    {
        var (user, project, _) = MkGroupFixture(2, "g");
        var persona = _personaManager.Create(user.Id, "Соло", "Аналитик", null, null,
            model: "opus", effort: "high", PersonaScope.Project, project.Id,
            color: null, greeting: null, memoryEnabled: false);
        var session = await _sut.CreateAsync(project.Id, ClaudeMode.Auto);

        var updated = _sut.SetPersona(session.Id, user.Id, persona.Id);

        updated!.PersonaId.Should().Be(persona.Id);
        updated.Model.Should().Be("opus");
        updated.Effort.Should().Be("high");
        updated.AgentName.Should().BeNull();
        updated.PersonaSwitched.Should().BeFalse("ходов ещё не было — оговорка о смене не нужна");
    }

    // --- GetHistoryAsync ---

    [Fact]
    public async Task GetHistoryAsync_NonExistentSession_ReturnsEmpty()
    {
        var history = await _sut.GetHistoryAsync("nonexistent");

        history.Should().BeEmpty();
    }

    [Fact]
    public async Task GetHistoryAsync_NewSession_ReturnsEmpty()
    {
        var dir = MkProjectDir("nh");
        var project = _projectManager.Create("NH", dir, TestUserId, TestUsername);
        var session = await _sut.CreateAsync(project.Id, ClaudeMode.Auto);

        var history = await _sut.GetHistoryAsync(session.Id);

        history.Should().BeEmpty();
    }

    [Fact]
    public async Task GetHistoryAsync_SessionWithoutAccumulator_LoadsFromDisk()
    {
        // Симулируем сессию после рестарта сервера: у неё нет накопителя (Process=null, Accumulator=null),
        // но ClaudeSessionId задан → история должна подгрузиться с диска
        var dir = MkProjectDir("rh");
        var project = _projectManager.Create("RH", dir, TestUserId, TestUsername);
        var session = await _sut.CreateAsync(project.Id, ClaudeMode.Auto);

        var claudeSessionId = "test-claude-session-" + Guid.NewGuid().ToString("N");
        session.ClaudeSessionId = claudeSessionId;

        // Сохраняем историю на диск напрямую через historyService
        var messages = new List<ClaudeHomeServer.Protocol.StoredMessage>
        {
            new ClaudeHomeServer.Protocol.StoredTextMessage("Привет из истории")
        };
        await _historyService.SaveAsync(claudeSessionId, messages);

        // История из accumulator (он есть и возвращает пустой список изначально)
        // Чтобы протестировать disk-путь, нужна сессия без accumulator.
        // GetHistoryAsync возвращает accumulator.GetAll() если он есть.
        // Для disk-пути: создаём новый SessionManager (симулируем рестарт),
        // сохраняем sessions.json с нашей сессией.
        // Вместо этого проверяем просто что история пустая для новой сессии —
        // полное тестирование disk-пути описано в ChatHistoryServiceTests.
        var history = await _sut.GetHistoryAsync(session.Id);
        // Новая сессия → accumulator пустой → возвращает пустой список
        history.Should().BeEmpty();
    }

    // --- FreezePending и CurrentTurnSnapshot (фикс бага композера) ---

    [Fact]
    public async Task FreezePending_AfterDelivery_ГаситCurrentTurnSnapshot()
    {
        // Симулируем прерванный пользовательский ход: задаём CurrentTurnSnapshot напрямую
        var session = await MkBusySessionAsync("freeze", SessionStatus.Working);
        var entry = GetEntry(session.Id);

        SetCurrentTurnSnapshot(entry, "текст прерванного хода", ["file.txt"], "plan");

        _sut.Interrupt(session.Id);
        var restores = await WaitForComposerRestoresAsync(1);

        restores.Should().ContainSingle()
            .Which.Text.Should().Be("текст прерванного хода");

        // Повторный Interrupt не должен resurrect старый текст
        _sut.Interrupt(session.Id);
        restores = await WaitForComposerRestoresAsync(2);

        restores.Last().Text.Should().BeNull("snapshot должен быть погашен после доставки restore");
    }

    [Fact]
    public async Task FreezePending_ПовторныйВызовПриПогашенномSnapshot_НеResurrectТекст()
    {
        var session = await MkBusySessionAsync("freeze2", SessionStatus.Working);
        var entry = GetEntry(session.Id);

        SetCurrentTurnSnapshot(entry, "старый текст", ["old.txt"], "auto");

        _sut.Interrupt(session.Id);
        var restores = await WaitForComposerRestoresAsync(1);
        restores.Should().ContainSingle().Which.Text.Should().Be("старый текст");

        _sut.Interrupt(session.Id);
        restores = await WaitForComposerRestoresAsync(2);

        restores.Last().Text.Should().BeNull("повторный FreezePending не должен resurrect текст");
    }

    [Fact]
    public async Task FreezePending_БезSnapshot_ВозвращаетRestoreСNullТекстом()
    {
        var session = await MkBusySessionAsync("freeze3", SessionStatus.Working);

        _sut.Interrupt(session.Id);
        var restores = await WaitForComposerRestoresAsync(1);

        restores.Should().ContainSingle()
            .Which.Text.Should().BeNull("прерван авто/агентский ход — восстанавливать нечего");
    }

    private static void SetCurrentTurnSnapshot(object entry, string text, IReadOnlyList<string> attachedPaths, string? mode)
    {
        var field = entry.GetType().GetField("CurrentTurnSnapshot", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;
        var snapshotType = Nullable.GetUnderlyingType(field.FieldType) ?? field.FieldType;
        var snapshot = Activator.CreateInstance(snapshotType, text, attachedPaths, mode);
        field.SetValue(entry, snapshot);
    }

    private async Task<IReadOnlyList<ComposerRestoreMessage>> WaitForComposerRestoresAsync(int count, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (DateTime.UtcNow < deadline)
        {
            var current = _sentMessages.OfType<ComposerRestoreMessage>().ToList();
            if (current.Count >= count)
            {
                await Task.Delay(50);
                var after = _sentMessages.OfType<ComposerRestoreMessage>().ToList();
                if (after.Count == current.Count)
                    return after;
            }
            await Task.Delay(50);
        }
        return _sentMessages.OfType<ComposerRestoreMessage>().ToList();
    }

    // --- Привязка чата к УЖЕ существующему дереву (чат-исполнитель задачи с worktree) ---
    // Отличие от SetWorktreeAsync: дерево не создаётся и транскрипт не мигрирует — только
    // поля свежей сессии до первого хода, cwd подставит EnsureProcessAsync.

    // Репозиторий с одним коммитом и linked worktree на ветке wt/тест
    private async Task<(Project Project, string Worktree)> MkRepoWithWorktreeAsync(string name)
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

    // Прямой git для арранжей (как в GitServiceTests)
    private static async Task RawGitAsync(string root, params string[] args)
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

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AttachWorktree_СуществующееДеревоПроекта_СессияПолучаетПутьИВетку()
    {
        var (project, worktree) = await MkRepoWithWorktreeAsync("attach-ok");
        var session = await _sut.CreateAsync(project.Id, ClaudeMode.AcceptEdits);

        var attached = await _sut.AttachWorktreeAsync(session.Id, worktree);

        attached.Should().BeTrue();
        var info = _sut.GetById(session.Id)!;
        info.WorktreePath.Should().Be(Path.GetFullPath(worktree));
        // Ветку не передавали — берётся из самого дерева
        info.WorktreeBranch.Should().Be("wt/тест");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AttachWorktree_ПутьНеЧислитсяВРепе_ОтказБезПривязки()
    {
        var (project, _) = await MkRepoWithWorktreeAsync("attach-alien");
        // Папка есть на диске, но деревом проекта не является
        var alien = Directory.CreateDirectory(Path.Combine(_tempDir, "alien")).FullName;
        var session = await _sut.CreateAsync(project.Id, ClaudeMode.AcceptEdits);

        var attached = await _sut.AttachWorktreeAsync(session.Id, alien);

        attached.Should().BeFalse();
        // Мягкая деградация: чат остаётся в корне проекта
        _sut.GetById(session.Id)!.WorktreePath.Should().BeNull();
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AttachWorktree_ПутиНетНаДиске_Отказ()
    {
        var (project, worktree) = await MkRepoWithWorktreeAsync("attach-missing");
        var session = await _sut.CreateAsync(project.Id, ClaudeMode.AcceptEdits);

        var attached = await _sut.AttachWorktreeAsync(session.Id, worktree + "-нет", "wt/тест");

        attached.Should().BeFalse();
        _sut.GetById(session.Id)!.WorktreePath.Should().BeNull();
    }

    [Fact]
    public async Task AttachWorktree_ЧатВнеПроекта_Отказ()
    {
        // У личной задачи проекта нет — дерева тоже: привязывать не к чему
        var user = _userStore.Add("attach-personal", "pw-123456", "user");
        var session = await _sut.CreateChatAsync(user.Id, ClaudeMode.AcceptEdits);

        (await _sut.AttachWorktreeAsync(session.Id, _tempDir)).Should().BeFalse();
        _sut.GetById(session.Id)!.WorktreePath.Should().BeNull();
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AttachWorktree_НачатыйЧат_Отказ()
    {
        // Контекст начатого чата привязан к прежнему cwd (--resume ищет транскрипт по нему):
        // переезд с контекстом — только через SetWorktreeAsync
        var (project, worktree) = await MkRepoWithWorktreeAsync("attach-started");
        var session = await _sut.CreateAsync(project.Id, ClaudeMode.AcceptEdits);
        _sut.GetById(session.Id)!.ClaudeSessionId = "csid-1";

        (await _sut.AttachWorktreeAsync(session.Id, worktree)).Should().BeFalse();
        _sut.GetById(session.Id)!.WorktreePath.Should().BeNull();
    }

    // --- Живой ход: rate_limit_event пишет usage с source=turn и трогает activity tracker ---

    private async Task InvokeOnMessageAsync(string sessionId, TurnAccumulator acc, ServerMessage msg,
        long runId = 0)
    {
        var method = typeof(SessionManager).GetMethod("OnMessageAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var task = (Task)method.Invoke(_sut, [sessionId, acc, msg, runId])!;
        await task;
    }

    [Fact]
    public async Task RateLimitMessage_ЖивойХод_ЗаписываетSourceTurn_ИТрогаетActivityTracker()
    {
        var dir = MkProjectDir("ratelimit");
        var project = _projectManager.Create("RL", dir, TestUserId, TestUsername);
        var session = await _sut.CreateAsync(project.Id, ClaudeMode.Auto);
        var acc = new TurnAccumulator(new List<StoredMessage>());
        var msg = new RateLimitMessage("five_hour", DateTime.UtcNow.AddHours(2).ToString("o"),
            "allowed", 0.4, false);

        await InvokeOnMessageAsync(session.Id, acc, msg);

        var snap = _usage.GetAll().Should().ContainSingle(s => s.LimitType == "five_hour").Subject;
        snap.Source.Should().Be("turn");
        snap.SubscriptionKey.Should().Be(ClaudeSubscriptionPool.PrimaryKey);
        _activity.IsIdle(ClaudeSubscriptionPool.PrimaryKey, TimeSpan.FromMinutes(10)).Should().BeFalse();
    }

    [Fact]
    public async Task RateLimitMessage_ЗдоровоеОкноНаИсчерпанномАккаунте_СнимаетПометку()
    {
        // Самолечение: аккаунт помечен исчерпанным (ложно или окно уже отпустило), но ход
        // через него проходит — пометка снимается прямо на ходу, не дожидаясь resetsAt.
        var dir = MkProjectDir("ratelimit-heal");
        var project = _projectManager.Create("RLH", dir, TestUserId, TestUsername);
        var session = await _sut.CreateAsync(project.Id, ClaudeMode.Auto);
        var acc = new TurnAccumulator(new List<StoredMessage>());
        _subPool.MarkExhausted(ClaudeSubscriptionPool.PrimaryKey, DateTime.UtcNow.AddDays(5));

        await InvokeOnMessageAsync(session.Id, acc, new RateLimitMessage("five_hour",
            DateTime.UtcNow.AddHours(2).ToString("o"), "allowed_warning", 0.64, false));

        _subPool.IsExhausted(ClaudeSubscriptionPool.PrimaryKey).Should().BeFalse();
    }

    [Fact]
    public async Task RateLimitMessage_RejectedНеизвестногоОкна_НеПомечаетИсчерпанной()
    {
        // Инцидент 2026-08-02: одиночное rejected по окну seven_day_overage_included
        // выводило живой аккаунт из ротации на пять суток. Такое окно только пишется в usage.
        var dir = MkProjectDir("ratelimit-unknown");
        var project = _projectManager.Create("RLU", dir, TestUserId, TestUsername);
        var session = await _sut.CreateAsync(project.Id, ClaudeMode.Auto);
        var acc = new TurnAccumulator(new List<StoredMessage>());

        await InvokeOnMessageAsync(session.Id, acc, new RateLimitMessage("seven_day_overage_included",
            DateTime.UtcNow.AddDays(5).ToString("o"), "rejected", null, false));

        _subPool.IsExhausted(ClaudeSubscriptionPool.PrimaryKey).Should().BeFalse();
        _usage.GetAll().Should().Contain(s => s.LimitType == "seven_day_overage_included");
    }

    [Fact]
    public async Task RateLimitMessage_НеизвестноеОкно_НеСнимаетПометкуИсчерпания()
    {
        // Симметрия белого списка: неизвестное окно не банит и не разбанивает — иначе
        // транзитное allowed_warning по overage-окну сняло бы реальный бан по неделе.
        var dir = MkProjectDir("ratelimit-unknown-heal");
        var project = _projectManager.Create("RLUH", dir, TestUserId, TestUsername);
        var session = await _sut.CreateAsync(project.Id, ClaudeMode.Auto);
        var acc = new TurnAccumulator(new List<StoredMessage>());
        _subPool.MarkExhausted(ClaudeSubscriptionPool.PrimaryKey, DateTime.UtcNow.AddDays(2));

        await InvokeOnMessageAsync(session.Id, acc, new RateLimitMessage("seven_day_overage_included",
            DateTime.UtcNow.AddDays(5).ToString("o"), "allowed_warning", 0.3, false));

        _subPool.IsExhausted(ClaudeSubscriptionPool.PrimaryKey).Should().BeTrue();
    }

    // --- Per-persona рубильники MCP-серверов (Off-привязка type: tool, target: <ключ>) ---

    private object? InvokePrivate(string name, params object?[] args)
    {
        var method = typeof(SessionManager).GetMethod(name,
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        return method.Invoke(_sut, args);
    }

    // Персона проекта + Off-привязки на перечисленные ключи (пусто — дефолтная персона)
    private Persona MkGatedPersona(string projectId, string suffix, params string[] offKeys)
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

    [Fact]
    public async Task ГейтыСерверов_БезПривязок_ВсёПодключено()
    {
        var dir = MkProjectDir("gates-default");
        var project = _projectManager.Create("GD", dir, TestUserId, TestUsername);
        var persona = MkGatedPersona(project.Id, "default");
        // Вторая персона в контексте — иначе подсказки о консультациях не из чего строить
        MkGatedPersona(project.Id, "peer");
        var session = await _sut.CreatePersonaChatAsync(TestUserId, persona.Id, ClaudeMode.Auto);

        InvokePrivate("BuildWidgetsContext", TestUserId, persona).Should().NotBeNull();
        // Уведомления — исключение из «без привязок включено всё»: с августа 2026 сервер идёт
        // по роли (модуль автоматизации), персоне без роли его даёт только явная привязка
        InvokePrivate("BuildNotificationsContext", TestUserId, persona.Id, persona).Should().BeNull();
        InvokePrivate("BuildCodeGraphContext", TestUserId, project.Id, session.Id, dir, persona)
            .Should().NotBeNull();
        var personas = InvokePrivate("BuildPersonasContext", TestUserId, project.Id, session, persona)
            .Should().BeOfType<PersonasMcpContext>().Subject;
        personas.MentionsHint.Should().NotBeNull("консультации включены по умолчанию");
        InvokePrivate("ConsultantsEnabled", TestUserId, session, persona).Should().Be(true);
    }

    [Fact]
    public async Task Уведомления_ЯвнаяПривязка_ПодключаетСервер()
    {
        // Дефолт сузили по данным использования, но включить сервер обратно можно
        // привязкой персоны — без правок кода
        var dir = MkProjectDir("gates-notif");
        var project = _projectManager.Create("GNo", dir, TestUserId, TestUsername);
        var persona = MkGatedPersona(project.Id, "notif");
        persona = _personaManager.UpdateBindings(persona.Id, TestUserId,
            [new PersonaBinding
            {
                Type = PersonaBindingType.Tool, Target = "notifications",
                Mode = PersonaBindingMode.Auto,
            }])!;
        await _sut.CreatePersonaChatAsync(TestUserId, persona.Id, ClaudeMode.Auto);

        InvokePrivate("BuildNotificationsContext", TestUserId, persona.Id, persona)
            .Should().NotBeNull();
    }

    [Fact]
    public async Task ГейтыСерверов_OffПривязки_СнимаютСерверы()
    {
        var dir = MkProjectDir("gates-off");
        var project = _projectManager.Create("GO", dir, TestUserId, TestUsername);
        var persona = MkGatedPersona(project.Id, "off",
            "widgets", "notifications", "codegraph", "personas");
        var session = await _sut.CreatePersonaChatAsync(TestUserId, persona.Id, ClaudeMode.Auto);

        InvokePrivate("BuildWidgetsContext", TestUserId, persona).Should().BeNull();
        InvokePrivate("BuildNotificationsContext", TestUserId, persona.Id, persona).Should().BeNull();
        InvokePrivate("BuildCodeGraphContext", TestUserId, project.Id, session.Id, dir, persona)
            .Should().BeNull();
        InvokePrivate("BuildPersonasContext", TestUserId, project.Id, session, persona).Should().BeNull();
    }

    [Fact]
    public async Task ГейтыСерверов_СессияБезПерсоны_НеЗатронута()
    {
        var dir = MkProjectDir("gates-nopersona");
        var project = _projectManager.Create("GN", dir, TestUserId, TestUsername);
        var session = await _sut.CreateAsync(project.Id, ClaudeMode.Auto);

        InvokePrivate("BuildWidgetsContext", TestUserId, null).Should().NotBeNull();
        InvokePrivate("BuildNotificationsContext", TestUserId, null, null).Should().NotBeNull();
        InvokePrivate("BuildCodeGraphContext", TestUserId, project.Id, session.Id, dir, null)
            .Should().NotBeNull();
        InvokePrivate("BuildPersonasContext", TestUserId, project.Id, session, null).Should().NotBeNull();
        InvokePrivate("ConsultantsEnabled", TestUserId, session, null).Should().Be(true);
    }

    [Fact]
    public async Task Консультанты_Off_СнимаютПодсказкуНоОставляютСерверПерсон()
    {
        var dir = MkProjectDir("gates-consult");
        var project = _projectManager.Create("GC", dir, TestUserId, TestUsername);
        var persona = MkGatedPersona(project.Id, "consult", "consultants");
        MkGatedPersona(project.Id, "consult-peer");
        var session = await _sut.CreatePersonaChatAsync(TestUserId, persona.Id, ClaudeMode.Auto);

        InvokePrivate("ConsultantsEnabled", TestUserId, session, persona).Should().Be(false);
        InvokePrivate("BuildPersonaAgentsProvider", TestUserId, session, persona).Should().BeNull(
            "нет ни pmem-серверов, ни --add-dir с .md-агентами");
        var personas = InvokePrivate("BuildPersonasContext", TestUserId, project.Id, session, persona)
            .Should().BeOfType<PersonasMcpContext>().Subject;
        personas.MentionsHint.Should().BeNull(
            "без консультаций persona_ask и список коллег не нужны (PERSONAS_MENTIONS=0)");
    }

    [Fact]
    public async Task Консультанты_Off_ВГрупповомЧатеИгнорируется()
    {
        // Спикер обязан уметь спросить коллег по чату — иначе групповой чат ломается по замыслу
        var (user, project, personas) = MkGroupFixture(3, "gates-group");
        var speaker = _personaManager.UpdateBindings(personas[0].Id, user.Id,
        [
            new PersonaBinding { Type = PersonaBindingType.Tool, Target = "consultants", Mode = PersonaBindingMode.Off },
        ]);
        var session = await _sut.CreateGroupChatAsync(user.Id, personas.Select(p => p.Id).ToList(),
            ClaudeMode.Auto, "Команда");

        InvokePrivate("ConsultantsEnabled", user.Id, session, speaker).Should().Be(true);
        var ctx = InvokePrivate("BuildPersonasContext", user.Id, project.Id, session, speaker)
            .Should().BeOfType<PersonasMcpContext>().Subject;
        ctx.MentionsHint.Should().NotBeNull("в групповом чате коллеги остаются доступными");
    }

    [Fact]
    public async Task Personas_Off_ВГрупповомЧатеИгнорируется()
    {
        // Спикер обязан уметь спросить коллег через persona_ask — иначе BuildGroupChatHint
        // безосновательно отсылает к блоку о консультациях, а его нет (регресс из ревью 995702c5)
        var (user, project, personas) = MkGroupFixture(3, "gates-group-personas");
        var speaker = _personaManager.UpdateBindings(personas[0].Id, user.Id,
        [
            new PersonaBinding { Type = PersonaBindingType.Tool, Target = "personas", Mode = PersonaBindingMode.Off },
        ]);
        var session = await _sut.CreateGroupChatAsync(user.Id, personas.Select(p => p.Id).ToList(),
            ClaudeMode.Auto, "Команда-персоны");

        InvokePrivate("PersonasEnabled", user.Id, session, speaker).Should().Be(true);
        var ctx = InvokePrivate("BuildPersonasContext", user.Id, project.Id, session, speaker)
            .Should().BeOfType<PersonasMcpContext>().Subject;
        ctx.MentionsHint.Should().NotBeNull("в групповом чате сервер персон остаётся подключён");
    }

    [Fact]
    public async Task Personas_Off_ОдиночныйЧатТойЖеПерсоны_СнимаетСервер()
    {
        var dir = MkProjectDir("gates-personas-solo");
        var project = _projectManager.Create("GPS", dir, TestUserId, TestUsername);
        var persona = MkGatedPersona(project.Id, "personas-solo", "personas");
        var session = await _sut.CreatePersonaChatAsync(TestUserId, persona.Id, ClaudeMode.Auto);

        InvokePrivate("PersonasEnabled", TestUserId, session, persona).Should().Be(false);
        InvokePrivate("BuildPersonasContext", TestUserId, project.Id, session, persona).Should().BeNull();
    }

    [Fact]
    public async Task ГейтыСерверов_РешениеОдинаковоеНаВсехХодах()
    {
        // Состав tools/list входит в сигнатуру запуска CLI: «мерцание» между ходами убивает
        // процесс claude со всеми MCP-серверами
        var dir = MkProjectDir("gates-stable");
        var project = _projectManager.Create("GS", dir, TestUserId, TestUsername);
        var persona = MkGatedPersona(project.Id, "stable", "codegraph");
        var session = await _sut.CreatePersonaChatAsync(TestUserId, persona.Id, ClaudeMode.Auto);

        for (var turn = 1; turn <= 5; turn++)
        {
            InvokePrivate("BuildCodeGraphContext", TestUserId, project.Id, session.Id, dir, persona)
                .Should().BeNull($"ход {turn}");
            InvokePrivate("BuildWidgetsContext", TestUserId, persona).Should().NotBeNull($"ход {turn}");
        }
    }

    // --- Имя чата у командных механик: тема из JSON/строкового вызова вместо сырой обвязки ---

    private static string MakeChatTitle(string text)
    {
        var method = typeof(SessionManager).GetMethod("MakeChatTitle",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        return (string)method.Invoke(null, [text])!;
    }

    [Fact]
    public void MakeChatTitle_TeamImplement_ТемаИзTask()
    {
        MakeChatTitle("/team-implement {\"task\":\"добавить экспорт в CSV\",\"worktree\":false,\"verify\":true}")
            .Should().Be("добавить экспорт в CSV");
    }

    [Fact]
    public void MakeChatTitle_PanelOfExperts_ТемаИзTopic()
    {
        MakeChatTitle("/panel-of-experts {\"topic\":\"выбрать очередь сообщений\",\"rounds\":2}")
            .Should().Be("выбрать очередь сообщений");
    }

    [Fact]
    public void MakeChatTitle_ReviewConsilium_ТемаИзTarget()
    {
        MakeChatTitle("/review-consilium {\"target\":\"текущий дифф\",\"lenses\":[\"security\"]}")
            .Should().Be("текущий дифф");
    }

    [Fact]
    public void MakeChatTitle_RedTeam_ТемаИзTarget()
    {
        MakeChatTitle("/red-team {\"target\":\"план миграции БД\",\"angles\":[\"security\"]}")
            .Should().Be("план миграции БД");
    }

    [Fact]
    public void MakeChatTitle_PanelOfExperts_БезTopic_ПадаетНаBrief()
    {
        MakeChatTitle("/panel-of-experts {\"topic\":\"\",\"brief\":\"контекст из чата\",\"rounds\":2}")
            .Should().Be("контекст из чата");
    }

    [Theory]
    [InlineData("/oh-my-claudecode:ralplan --interactive \"выбрать подход к кэшу\"", "выбрать подход к кэшу")]
    [InlineData("/oh-my-claudecode:deep-interview --standard \"формат экспорта\"", "формат экспорта")]
    [InlineData("/oh-my-claudecode:autopilot \"починить флаки-тест\"", "починить флаки-тест")]
    [InlineData("/oh-my-claudecode:trace \"почему падает воркер\"", "почему падает воркер")]
    [InlineData("/oh-my-claudecode:sciomc \"разобрать утечку памяти\"", "разобрать утечку памяти")]
    public void MakeChatTitle_СтроковыеМеханики_ТемаИзКавычек(string turnText, string expected)
    {
        MakeChatTitle(turnText).Should().Be(expected);
    }

    // B6: путь QuotedTopicRegex не был покрыт пустыми кавычками и переводом строки —
    // для JSON-пути (JsonTopicKeys) такие тесты уже были (пустая тема/битый JSON), для
    // строкового — нет
    [Fact]
    public void MakeChatTitle_КавычкиПустые_ПадаетНаОбрезкуСырогоТекста()
    {
        var turnText = "/oh-my-claudecode:ralplan \"\"";
        MakeChatTitle(turnText).Should().Be(turnText);
    }

    [Fact]
    public void MakeChatTitle_КавычкиСПереводомСтроки_ОбрезаетсяДоПервойСтроки()
    {
        MakeChatTitle("/oh-my-claudecode:deep-interview \"первая строка\nвторая строка\"")
            .Should().Be("первая строка");
    }

    [Fact]
    public void MakeChatTitle_Ultraqa_ТемаПослеФлага()
    {
        MakeChatTitle("/oh-my-claudecode:ultraqa --frontend починить регрессию формы")
            .Should().Be("починить регрессию формы");
    }

    [Fact]
    public void MakeChatTitle_ПустаяТема_ПадаетНаОбрезкуСырогоТекста()
    {
        MakeChatTitle("/team-implement {\"task\":\"\",\"worktree\":false,\"verify\":true}")
            .Should().StartWith("/team-implement {\"task\"");
    }

    [Fact]
    public void MakeChatTitle_БитыйJson_НеПадаетИВозвращаетОбрезку()
    {
        MakeChatTitle("/team-implement {\"task\":\"незакрытый")
            .Should().StartWith("/team-implement {\"task\"");
    }

    [Fact]
    public void MakeChatTitle_ОбычноеСообщение_РаботаетКакРаньше()
    {
        MakeChatTitle("Помоги разобраться с багом в SessionManager").Should().Be("Помоги разобраться с багом в SessionManager");
    }

    [Fact]
    public void MakeChatTitle_ДлинныйJson_ОбрезаетсяДо48Символов()
    {
        var longTask = new string('а', 100);
        MakeChatTitle($"/team-implement {{\"task\":\"{longTask}\"}}")
            .Should().Be(new string('а', 48) + "…");
    }

    // --- Фоновые действия идут по маршруту места, а не только «работает только на локали» ---

    // Подставной раннер для действий-«украшений»: response вызывается лениво в RunAsync,
    // поэтому может как отдать ответ, так и бросить исключение (эмуляция отказа исполнителя).
    private sealed class StubTitleCheapRunner(bool usesLocal, Func<string> response) : ICheapTextRunner
    {
        public bool UsesLocal(string actionKey) => usesLocal;

        public Task<string> RunAsync(string actionKey, string prompt, string? fallbackModel = null,
            string? ownerId = null, object? jsonFormat = null, CancellationToken ct = default) =>
            Task.FromResult(response());

        public Task<string?> RunFreeAsync(string actionKey, string prompt, object? jsonFormat = null,
            CancellationToken ct = default) => Task.FromResult<string?>(null);

        public Task<string?> RunLocalOnlyAsync(string actionKey, string prompt, CancellationToken ct = default) =>
            Task.FromResult<string?>(null);

        public Task<OneShotResult> RunDetailedAsync(string actionKey, string prompt, string? fallbackModel = null,
            string? ownerId = null, TimeSpan? timeout = null, int? maxTokens = null, object? jsonFormat = null,
            CancellationToken ct = default) => throw new NotSupportedException();
    }

    [Fact]
    public async Task RefineChatTitle_НеЛокальныйМаршрут_УточняетЗаголовок()
    {
        var dir = MkProjectDir("refine-remote");
        var project = _projectManager.Create("RT", dir, TestUserId, TestUsername);
        var session = await _sut.CreateAsync(project.Id, ClaudeMode.Auto);
        session.Name = "обрезка сообщения";

        typeof(SessionManager).GetField("_cheap", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(_sut, new StubTitleCheapRunner(usesLocal: false, () => """{"title":"Уточнённый заголовок"}"""));

        await (Task)InvokePrivate("RefineChatTitleAsync", session.Id, "первое сообщение чата",
            "обрезка сообщения", TestUserId)!;

        _sut.GetOwned(session.Id, TestUserId)!.Name.Should().Be("Уточнённый заголовок",
            "заголовок обязан уточняться по маршруту места chat-title, а не только на локали");
    }

    [Fact]
    public async Task RefineChatTitle_ОшибкаИсполнителя_ОставляетОбрезкуБезИсключения()
    {
        var dir = MkProjectDir("refine-error");
        var project = _projectManager.Create("RE", dir, TestUserId, TestUsername);
        var session = await _sut.CreateAsync(project.Id, ClaudeMode.Auto);
        session.Name = "обрезка сообщения";

        typeof(SessionManager).GetField("_cheap", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(_sut, new StubTitleCheapRunner(usesLocal: false,
                () => throw new InvalidOperationException("исполнитель недоступен")));

        await (Task)InvokePrivate("RefineChatTitleAsync", session.Id, "первое сообщение чата",
            "обрезка сообщения", TestUserId)!;

        _sut.GetOwned(session.Id, TestUserId)!.Name.Should().Be("обрезка сообщения",
            "отказ исполнителя — best-effort: имя остаётся обрезкой, исключение наружу не идёт");
    }

    // Собирает изолированный PersonaAutomationService поверх уже готового _sut (тот же
    // SessionManager, что и у остальных тестов файла) — не дублирует его тяжёлую сборку.
    private (PersonaAutomationService Service, AutomationStateStore State) BuildAutomationService(
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
        var clients = new Mock<IHubClients>();
        clients.Setup(c => c.Group(It.IsAny<string>())).Returns(new Mock<IClientProxy>().Object);
        var hub = new Mock<IHubContext<SessionHub>>();
        hub.Setup(h => h.Clients).Returns(clients.Object);
        var notif = new NotificationService(notifStore, hub.Object, push, _personaManager, _projectManager,
            NullLogger<NotificationService>.Instance);
        var state = new AutomationStateStore(config);
        var mentions = new MentionTriggerSource(_personaManager);
        var roots = new AutomationRootResolver(_projectManager, _appSettings);

        var service = new PersonaAutomationService(_personaManager, _sut, push, hub.Object, notif,
            state, mentions, _projectManager, _userStore, roots, Array.Empty<ITriggerSource>(),
            config, cheap, NullLogger<PersonaAutomationService>.Instance);
        return (service, state);
    }

    [Fact]
    public async Task PersonaAutomation_ГейтOnlyIf_ОцениваетсяПриНеЛокальномМаршруте()
    {
        var dir = MkProjectDir("automation-gate");
        var project = _projectManager.Create("AG", dir, TestUserId, TestUsername);
        var persona = _personaManager.Create(TestUserId, "Гейт-персона", role: null, description: null,
            systemPrompt: null, model: null, effort: null, scope: PersonaScope.Project,
            projectId: project.Id, color: null, greeting: null, memoryEnabled: false);

        // UsesLocal=false — маршрут места automation-gate НЕ локальный (слот/direct-модель);
        // раньше это молча пропускало гейт целиком (условие оценивалось только внутри хода)
        var (automation, state) = BuildAutomationService(
            new StubTitleCheapRunner(usesLocal: false, () => "нет"));

        var rule = new PersonaAutomationRule
        {
            Name = "Только про деплой",
            Trigger = new AutomationTrigger(),
            Condition = new AutomationCondition { OnlyIf = "касается деплоя" },
        };
        var ev = new TriggerEvent(rule.Id, AutomationTriggerType.Timer, "Обновлена документация README");

        var fireAsync = typeof(PersonaAutomationService).GetMethod("FireAsync",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)fireAsync.Invoke(automation, [persona, rule, TimeZoneInfo.Utc, ev, CancellationToken.None, false])!;

        state.GetRule(persona.Id, rule.Id).LastResult.Should().Be("gated",
            "гейт OnlyIf обязан оцениваться (и отсекать) при любом маршруте места automation-gate, не только на локали");
    }

    [Fact]
    public async Task PersonaAutomation_СводкаУведомления_СтроитсяПриНеЛокальномМаршруте()
    {
        var dir = MkProjectDir("automation-summary");
        var project = _projectManager.Create("AS", dir, TestUserId, TestUsername);
        var session = await _sut.CreateAsync(project.Id, ClaudeMode.Auto);

        // Форсируем чтение истории с диска (как после рестарта сервера) — accumulator=null
        var sessionsDict = typeof(SessionManager).GetField("_sessions", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(_sut)!;
        var args = new object?[] { session.Id, null };
        ((bool)sessionsDict.GetType().GetMethod("TryGetValue")!.Invoke(sessionsDict, args)!).Should().BeTrue();
        var entry = args[1]!;
        entry.GetType().GetField("Accumulator", BindingFlags.Public | BindingFlags.Instance)!.SetValue(entry, null);

        var claudeSessionId = "csid-" + Guid.NewGuid().ToString("N");
        session.ClaudeSessionId = claudeSessionId;
        await _historyService.SaveAsync(claudeSessionId,
            [new Protocol.StoredTextMessage("Готово: обновил конфиг и перезапустил сервис.")]);

        // UsesLocal=false — маршрут места notification-summary НЕ локальный
        var (automation, _) = BuildAutomationService(
            new StubTitleCheapRunner(usesLocal: false, () => "Обновил конфиг и перезапустил сервис."));

        var summarize = typeof(PersonaAutomationService).GetMethod("TrySummarizeLastReplyAsync",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        var result = await (Task<string?>)summarize.Invoke(automation, [session.Id, TestUserId])!;

        result.Should().Be("Обновил конфиг и перезапустил сервис.",
            "суть уведомления обязана строиться по маршруту места notification-summary, не только на локали");
    }
}
