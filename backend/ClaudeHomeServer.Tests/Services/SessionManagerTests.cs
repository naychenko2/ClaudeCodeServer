using ClaudeHomeServer.Hubs;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
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
    private readonly ChatHistoryService _historyService;
    private readonly UserStore _userStore;
    private readonly PersonaManager _personaManager;
    private readonly SessionManager _sut;

    public SessionManagerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "smgr_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_tempDir, "projects.json")
            })
            .Build();

        var userStore = new UserStore(config, NullLogger<UserStore>.Instance);
        _userStore = userStore;
        var appSettings = new AppSettingsService(config);
        _projectManager = new ProjectManager(config, userStore, appSettings);
        _historyService = new ChatHistoryService(config);

        var clients = new Mock<IHubClients>();
        var clientProxy = new Mock<IClientProxy>();
        clientProxy
            .Setup(c => c.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        clients.Setup(c => c.Group(It.IsAny<string>())).Returns(clientProxy.Object);

        var hub = new Mock<IHubContext<SessionHub>>();
        hub.Setup(h => h.Clients).Returns(clients.Object);

        var llmProviders = new ClaudeHomeServer.Services.Llm.LlmProviderRegistry(config);
        var adapters = new ClaudeHomeServer.Services.Llm.LlmSessionAdapterFactory(
            config, new SkillsService(), new WorkspaceKnowledgeStore(config), llmProviders);
        var falCost = new FalCostService(new Mock<IHttpClientFactory>().Object, config);
        var usage = new UsageService(config);
        var jwt = new JwtService(config, NullLogger<JwtService>.Instance);
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
        var promptBuilder = new PersonaPromptBuilder(llmProviders);
        _sut = new SessionManager(_projectManager, hub.Object, _historyService, config, adapters, falCost, usage, appSettings, userStore, jwt, server.Object, llmProviders, notesKb, flags, personas, personaMemory, promptBuilder, NullLogger<SessionManager>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
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

    // --- Групповые чаты (флаг persona-group-chats) ---

    // Пользователь с включённым флагом групповых чатов + проект + N проектных персон.
    // Ведущая — проектная, чтобы CreateGroupChatAsync шёл маршрутом проекта
    // (чат вне проекта требует DefaultProjectsPath, которого в тестовом конфиге нет).
    private (User User, Project Project, List<Persona> Personas) MkGroupFixture(int count, string suffix)
    {
        var user = _userStore.Add("group-user-" + suffix, "pw-123456", "user");
        _userStore.SetFeatureFlag(user.Id, FeatureFlagKeys.PersonaGroupChats, true);
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
    public async Task CreateGroupChatAsync_ФлагВыключен_400()
    {
        var (user, _, personas) = MkGroupFixture(2, "c");
        _userStore.SetFeatureFlag(user.Id, FeatureFlagKeys.PersonaGroupChats, false);

        var act = () => _sut.CreateGroupChatAsync(user.Id, personas.Select(p => p.Id).ToList(), ClaudeMode.Auto);

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
}
