using ClaudeHomeServer.Hubs;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ClaudeHomeServer.Tests.Services;

// Тесты шага 6 плана «Архив чатов» (v4): автоправило за флагом chat-auto-archive.
// Порог/исключения отбора уже покрыты ChatArchivedEventTests (MatchesArchiveRule +
// GetArchiveRuleCandidates) — здесь проходы сервиса: идемпотентность TickAsync(nowUtc),
// потолок 200, выключенный флаг, гейт первого прохода (кнопка «Применить сейчас»),
// наследование порога проекта, откат пачки по ArchiveBatchId и агрегированное
// уведомление. Ожиданий на час вперёд нет — nowUtc всегда параметром.
public class ChatArchiveServiceTests : IDisposable
{
    private const string TestUserId = "test-user-id";
    private const string TestUsername = "test-user";

    private readonly string _tempDir;
    private readonly SessionManager _sessions;
    private readonly ProjectManager _projects;
    private readonly UserStore _users;
    private readonly FeatureFlagService _flags;
    private readonly NotificationStore _notifStore;
    private readonly ChatArchiveService _sut;

    public ChatArchiveServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "chat_archive_rule_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        (_sessions, _projects, _users, _flags, _notifStore) = BuildSut(_tempDir);
        _sut = new ChatArchiveService(_sessions, _projects, _users, _flags,
            BuildNotifications(_tempDir, _projects, _notifStore), NullLogger<ChatArchiveService>.Instance);
    }

    public void Dispose()
    {
        _sessions.Dispose();
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    // --- Прохождение порога ---

    [Fact]
    public async Task Тик_АрхивируетОстывшийЧат_НеТрогаетСвежий()
    {
        var owner = EnableRule(days: 7);
        var stale = NewStaleChat(_sessions, owner.Id, ageDays: 10);
        var fresh = await _sessions.CreateChatAsync(owner.Id, ClaudeMode.Auto);

        await _sut.TickAsync(DateTime.UtcNow);

        stale.IsArchived.Should().BeTrue("10 дней без активности при пороге 7 — кандидат");
        stale.ArchivedBy.Should().Be("rule");
        fresh.IsArchived.Should().BeFalse("только что созданный чат порог не прошёл");
    }

    [Fact]
    public async Task Тик_ПорогПроекта_АрхивируетЧатыЭтогоПроекта()
    {
        // Личного порога нет — работает только проектный (наследование: null = личный,
        // но и личный null ⇒ сфере «вне проектов» правило не настроено вовсе)
        var owner = EnableRule(days: null, firstRun: true);
        var dir = Directory.CreateDirectory(Path.Combine(_tempDir, "proj-rule")).FullName;
        var project = _projects.Create("Проект с порогом", dir, owner.Id, TestUsername);
        project.ArchiveAfterDays = 7;
        var stale = await _sessions.CreateAsync(project.Id, ClaudeMode.Auto);
        CoolDown(stale, ageDays: 10);
        var stalePersonal = NewStaleChat(_sessions, owner.Id, ageDays: 30);

        await _sut.TickAsync(DateTime.UtcNow);

        stale.IsArchived.Should().BeTrue("порог проекта 7 дней пройден");
        stalePersonal.IsArchived.Should().BeFalse("личный порог не задан — чаты вне проектов не трогаем");
    }

    // --- Гейты тика ---

    [Fact]
    public async Task Тик_ФлагВыключен_НичегоНеАрхивирует()
    {
        var owner = CreateUser(flagsOn: false);
        _users.SetArchiveAfterDays(owner.Id, 7);
        _users.SetArchiveRuleFirstRunAt(owner.Id, DateTime.UtcNow);
        var stale = NewStaleChat(_sessions, owner.Id, ageDays: 30);

        await _sut.TickAsync(DateTime.UtcNow);

        stale.IsArchived.Should().BeFalse("флаг chat-auto-archive выключен — тик владельца ничего не делает");
    }

    [Fact]
    public async Task Тик_БезПервогоПрохода_НичегоНеАрхивирует_КнопкаРазблокирует()
    {
        // Накопившиеся старые чаты правило само не разгребает: до «Применить сейчас»
        // фоновый тик владельца не архивирует ничего
        var owner = EnableRule(days: 7, firstRun: false);
        var stale = NewStaleChat(_sessions, owner.Id, ageDays: 30);

        await _sut.TickAsync(DateTime.UtcNow);
        stale.IsArchived.Should().BeFalse("первый проход ещё не запускался — залежи лежат как лежали");

        var (archived, batchId) = await _sut.RunNowAsync(owner.Id, DateTime.UtcNow);
        archived.Should().Be(1, "кнопка «Применить сейчас» запускает ровно один проход, включая залежи");
        batchId.Should().NotBeNull();
        stale.IsArchived.Should().BeTrue();
        _users.GetById(owner.Id)!.ArchiveRuleFirstRunAt.Should().NotBeNull("кнопка снимает гейт первого прохода");
    }

    // --- Идемпотентность и потолок ---

    [Fact]
    public async Task Тик_Идемпотентен_ПовторныйТикНичегоНеМеняет()
    {
        var owner = EnableRule(days: 7);
        var stale = NewStaleChat(_sessions, owner.Id, ageDays: 10);

        var now = DateTime.UtcNow;
        await _sut.TickAsync(now);
        var batchAfterFirst = stale.ArchiveBatchId;

        await _sut.TickAsync(now);

        stale.IsArchived.Should().BeTrue();
        stale.ArchiveBatchId.Should().Be(batchAfterFirst,
            "повторный тик с тем же nowUtc не создаёт новую пачку — архивный чат не кандидат");
    }

    [Fact]
    public async Task Тик_Потолок200ЗаПроход()
    {
        var owner = EnableRule(days: 7);
        var chats = new List<Session>();
        for (var i = 0; i < ChatArchiveService.MaxBatchSize + 5; i++)
            chats.Add(NewStaleChat(_sessions, owner.Id, ageDays: 10 + i));

        await _sut.TickAsync(DateTime.UtcNow);

        _sessions.GetProjectlessChats(owner.Id).Count(s => s.IsArchived)
            .Should().Be(ChatArchiveService.MaxBatchSize, "потолок пачки одного прохода — 200");
        var batchIds = _sessions.GetProjectlessChats(owner.Id)
            .Where(s => s.IsArchived).Select(s => s.ArchiveBatchId).Distinct().ToList();
        batchIds.Should().ContainSingle("один ArchiveBatchId на проход");
    }

    // --- Пачка и откат ---

    [Fact]
    public async Task Откат_ПоБатчу_ВозвращаетТолькоСвоюПачку()
    {
        var owner = EnableRule(days: 7);
        var first = NewStaleChat(_sessions, owner.Id, ageDays: 30);
        await _sut.RunNowAsync(owner.Id, DateTime.UtcNow);
        var firstBatch = first.ArchiveBatchId!;
        first.IsArchived.Should().BeTrue();

        // Второй проход: ещё один остывший чат, ДРУГОЙ батч (возврат первого чата сделал
        // его не-кандидатом — он ещё и свежее порога после SetPinned-подобной активности)
        var second = NewStaleChat(_sessions, owner.Id, ageDays: 20);
        await _sut.RunNowAsync(owner.Id, DateTime.UtcNow);
        var secondBatch = second.ArchiveBatchId!;
        secondBatch.Should().NotBe(firstBatch, "каждый проход — свой ArchiveBatchId");
        second.IsArchived.Should().BeTrue();

        var restored = await _sessions.RestoreArchiveBatchAsync(owner.Id, secondBatch);

        restored.Should().Be(1);
        second.IsArchived.Should().BeFalse("чаты отката вернулись");
        second.ArchiveBatchId.Should().BeNull();
        first.IsArchived.Should().BeTrue("чаты ДРУГОЙ пачки остались в архиве");
        first.ArchiveBatchId.Should().Be(firstBatch);
    }

    [Fact]
    public async Task Откат_ЧужойВладелец_НичегоНеВозвращает()
    {
        var owner = EnableRule(days: 7);
        var chat = NewStaleChat(_sessions, owner.Id, ageDays: 30);
        var (_, batchId) = await _sut.RunNowAsync(owner.Id, DateTime.UtcNow);
        chat.IsArchived.Should().BeTrue();

        var stranger = CreateUser(flagsOn: false);
        var restored = await _sessions.RestoreArchiveBatchAsync(stranger.Id, batchId!);

        restored.Should().Be(0, "батч чужого владельца не возвращает чужие чаты");
        chat.IsArchived.Should().BeTrue();
    }

    // --- Уведомление ---

    [Fact]
    public async Task Тик_ОдноАгрегированноеУведомлениеСоСсылкойВАрхив()
    {
        var owner = EnableRule(days: 7);
        NewStaleChat(_sessions, owner.Id, ageDays: 10);
        NewStaleChat(_sessions, owner.Id, ageDays: 15);

        await _sut.TickAsync(DateTime.UtcNow);

        var items = await _notifStore.GetListAsync(owner.Id);
        items.Where(n => n.Type == "chat_auto_archive").Should().HaveCount(1,
            "одно агрегированное уведомление на проход, а не по чату");
        var notif = items.Single(n => n.Type == "chat_auto_archive");
        notif.Url.Should().Be("#/chats");
        notif.Body.Should().Contain("2 чата");
    }

    [Fact]
    public async Task Тик_ПустаяПачка_УведомленияНет()
    {
        var owner = EnableRule(days: 7);
        await _sessions.CreateChatAsync(owner.Id, ClaudeMode.Auto);

        await _sut.TickAsync(DateTime.UtcNow);

        var items = await _notifStore.GetListAsync(owner.Id);
        items.Should().BeEmpty("нечего архивировать — не о чем и сообщать");
    }

    // --- Кнопка «Применить сейчас» ---

    [Fact]
    public async Task RunNow_РовноОдинПроход_ОдинБатч()
    {
        var owner = EnableRule(days: 7);
        var a = NewStaleChat(_sessions, owner.Id, ageDays: 10);
        var b = NewStaleChat(_sessions, owner.Id, ageDays: 20);
        var c = NewStaleChat(_sessions, owner.Id, ageDays: 30);

        var (archived, batchId) = await _sut.RunNowAsync(owner.Id, DateTime.UtcNow);

        archived.Should().Be(3);
        batchId.Should().NotBeNullOrEmpty();
        new[] { a, b, c }.Should().OnlyContain(s => s.ArchiveBatchId == batchId);
    }

    // --- Плюрализация текста уведомления ---

    [Theory]
    [InlineData(1, "1 чат")]
    [InlineData(2, "2 чата")]
    [InlineData(5, "5 чатов")]
    [InlineData(11, "11 чатов")]
    [InlineData(21, "21 чат")]
    [InlineData(22, "22 чата")]
    [InlineData(100, "100 чатов")]
    [InlineData(101, "101 чат")]
    public void PluralChats_РусскаяПлюрализация(int n, string expected) =>
        ChatArchiveService.PluralChats(n).Should().Be(expected);

    // --- Помощники ---

    // Пользователь с флагом; days/firstRun — состояние правила (гейт первого прохода)
    private User EnableRule(int? days, bool firstRun = true)
    {
        var owner = CreateUser(flagsOn: true);
        _users.SetArchiveAfterDays(owner.Id, days);
        if (firstRun) _users.SetArchiveRuleFirstRunAt(owner.Id, DateTime.UtcNow);
        return owner;
    }

    private User CreateUser(bool flagsOn)
    {
        var user = _users.Add("user_" + Guid.NewGuid().ToString("N"), "pass12345", "user");
        if (flagsOn) user.FeatureFlags = new Dictionary<string, bool>
        {
            [FeatureFlagKeys.ChatAutoArchive] = true,
        };
        return user;
    }

    // Чат вне проекта, остывший на ageDays (правило personal-сферы)
    private Session NewStaleChat(SessionManager sessions, string ownerId, int ageDays)
    {
        var chat = sessions.CreateChatAsync(ownerId, ClaudeMode.Auto).GetAwaiter().GetResult();
        CoolDown(chat, ageDays);
        return chat;
    }

    private static void CoolDown(Session chat, int ageDays) => chat.UpdatedAt = DateTime.UtcNow - TimeSpan.FromDays(ageDays);

    private static (SessionManager Sessions, ProjectManager Projects, UserStore Users,
        FeatureFlagService Flags, NotificationStore NotifStore) BuildSut(string tempDir)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(tempDir, "projects.json"),
            // Автосейв выключен (как в SessionManagerTests): фоновая запись стора не
            // вмешивается между правкой и ассертами
            ["Session:AutoSaveSeconds"] = "0",
            ["DefaultProjectsPath"] = Path.Combine(tempDir, "homes"),
            ["ClaudeUserProfileDir"] = Path.Combine(tempDir, "claude-profile"),
        }).Build();

        var users = new UserStore(config, new FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        var appSettings = new AppSettingsService(config);
        var projects = new ProjectManager(config, users, appSettings);
        var history = new ChatHistoryService(config);

        var clientProxy = new Mock<IClientProxy>();
        clientProxy
            .Setup(c => c.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var clients = new Mock<IHubClients>();
        clients.Setup(c => c.Group(It.IsAny<string>())).Returns(clientProxy.Object);
        var hub = new Mock<IHubContext<SessionHub>>();
        hub.Setup(h => h.Clients).Returns(clients.Object);

        var llmProviders = new LlmProviderRegistry(config);
        var subPool = new ClaudeSubscriptionPool(config);
        var adapters = new LlmSessionAdapterFactory(config, new SkillsService(),
            new WorkspaceKnowledgeStore(config), llmProviders, subPool);
        var falCost = new FalCostService(new Mock<IHttpClientFactory>().Object, config);
        var usage = new UsageService(config);
        var jwt = new JwtService(config, users, NullLogger<JwtService>.Instance);
        var server = new Mock<Microsoft.AspNetCore.Hosting.Server.IServer>();
        server.Setup(s => s.Features).Returns(new Microsoft.AspNetCore.Http.Features.FeatureCollection());
        var wkStore = new WorkspaceKnowledgeStore(config);
        var knowledge = new KnowledgeService(new Mock<IHttpClientFactory>().Object,
            Microsoft.Extensions.Options.Options.Create(new DifyOptions()), wkStore);
        var flags = new FeatureFlagService(users);
        var notesSvc = new NotesService(projects, config, NullLogger<NotesService>.Instance);
        var notesKb = new NotesKnowledgeService(knowledge, notesSvc, users, config,
            NullLogger<NotesKnowledgeService>.Instance);
        var personas = new PersonaManager(config);
        var personaMemory = new PersonaMemoryService(knowledge, personas, users, config,
            NullLogger<PersonaMemoryService>.Instance);
        var bindings = new PersonaBindingsService(personas, projects, wkStore, notesSvc, notesKb,
            knowledge, new SkillsService(), users, config, NullLogger<PersonaBindingsService>.Instance);
        var promptBuilder = new PersonaPromptBuilder(llmProviders);
        var sandbox = new ClaudeHomeServer.Services.Execution.SandboxManager(config,
            NullLogger<ClaudeHomeServer.Services.Execution.SandboxManager>.Instance);

        var sessions = new SessionManager(projects, hub.Object, history, config, adapters, falCost,
            usage, appSettings, users, jwt, server.Object, llmProviders, notesKb, flags, personas,
            personaMemory, bindings, promptBuilder, subPool, NullLogger<SessionManager>.Instance,
            TestLauncherFactory.Instance, sandbox);
        var notifStore = new NotificationStore(config, NullLogger<NotificationStore>.Instance);
        return (sessions, projects, users, flags, notifStore);
    }

    private static NotificationService BuildNotifications(string tempDir, ProjectManager projects,
        NotificationStore notifStore)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(tempDir, "projects.json"),
        }).Build();
        var clientProxy = new Mock<IClientProxy>();
        clientProxy
            .Setup(c => c.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var clients = new Mock<IHubClients>();
        clients.Setup(c => c.Group(It.IsAny<string>())).Returns(clientProxy.Object);
        var hub = new Mock<IHubContext<SessionHub>>();
        hub.Setup(h => h.Clients).Returns(clients.Object);
        var users = new UserStore(config, new FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        var push = new PushService(config,
            new PushSubscriptionStore(config), new JwtService(config, users, NullLogger<JwtService>.Instance),
            NullLogger<PushService>.Instance);
        return new NotificationService(notifStore, hub.Object, push, new PersonaManager(config), projects,
            NullLogger<NotificationService>.Instance);
    }
}
