using ClaudeHomeServer.Hubs;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ClaudeHomeServer.Tests.Services;

// Шаг 3 плана «Архив чатов» (v4): событие ленты chat_archived уходит в project-группу
// для проектной сессии и в user-группу для чата вне проекта (образец адресации —
// BroadcastChatDeletedAsync), chat_deleted суррогатом не используется. Здесь же отбор
// кандидатов автоправила: GetArchiveRuleCandidates — та же функция, что позовёт тик.
// Сборка SessionManager своя (как ChatArchiveFlagTests), но мок хаба ЗАПИСЫВАЕТ группы.
public class ChatArchivedEventTests : IDisposable
{
    // Владелец чатов: свой пользователь на каждую сборку (CreateChatAsync резолвит
    // домашнюю папку по UserStore — пользователя с несуществующим id он не найдет)
    private string _ownerId = null!;
    private const string TestUsername = "test-user";

    private readonly string _tempDir;

    public ChatArchivedEventTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "chat_archive_event_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    // (группа, сообщение) каждого SendAsync — детектор адресации chat_archived
    private readonly List<(string Group, ServerMessage Msg)> _sent = [];
    private ChatHistoryService? _historyForBuild;
    private UserStore? _userStoreForBuild;

    // --- Событие chat_archived: адресация ---

    [Fact]
    public async Task Архивация_ПроектнойСессии_ШлётВProjectГруппу()
    {
        var (sut, projects) = BuildSut();
        var dir = Directory.CreateDirectory(Path.Combine(_tempDir, "proj_" + Guid.NewGuid().ToString("N"))).FullName;
        var project = projects.Create("Проект события", dir, _ownerId, TestUsername);
        var chat = await sut.CreateAsync(project.Id, ClaudeMode.Auto);

        await sut.SetArchivedAsync(chat.Id, _ownerId, archived: true);

        var archivedMsgs = _sent.Where(t => t.Msg is ChatArchivedMessage).ToList();
        archivedMsgs.Should().HaveCount(2, "session-группа + project-группа: по копии каждому адресату");
        archivedMsgs.Select(t => t.Group).Should().BeEquivalentTo([chat.Id, "project_" + project.Id],
            "адресация как у BroadcastChatDeletedAsync: session-группа всегда, дальше project_X");
        archivedMsgs.Should().AllSatisfy(t =>
            ((ChatArchivedMessage)t.Msg).Archived.Should().BeTrue("событие несёт направление"));
        _sent.Should().NotContain(t => t.Msg is ChatDeletedMessage,
            "chat_deleted — семантика «чата больше нет», суррогатом архива не является");
    }

    [Fact]
    public async Task Архивация_ЧатаВнеПроекта_ШлётВUserГруппу()
    {
        var (sut, projects) = BuildSut();
        var chat = await sut.CreateChatAsync(_ownerId, ClaudeMode.Auto);

        await sut.SetArchivedAsync(chat.Id, _ownerId, archived: true);

        var groups = _sent.Where(t => t.Msg is ChatArchivedMessage).Select(t => t.Group).ToList();
        groups.Should().BeEquivalentTo([chat.Id, "user_" + _ownerId],
            "чат вне проекта — user-группа владельца вместо project-группы");
    }

    [Fact]
    public async Task Возврат_ШлётСобытиеСНаправлениемFalse()
    {
        var (sut, projects) = BuildSut();
        var chat = await sut.CreateChatAsync(_ownerId, ClaudeMode.Auto);
        await sut.SetArchivedAsync(chat.Id, _ownerId, archived: true);
        _sent.Clear();

        await sut.SetArchivedAsync(chat.Id, _ownerId, archived: false);

        _sent.Where(t => t.Msg is ChatArchivedMessage)
            .Should().OnlyContain(t => ((ChatArchivedMessage)t.Msg).Archived == false);
    }

    // --- Чистый предикат правила (MatchesArchiveRule) ---

    [Fact]
    public void Предикат_СтарыйНезакреплённыйОбычныйЧат_Кандидат()
    {
        var s = Old();
        SessionManager.MatchesArchiveRule(s, cutoff: s.UpdatedAt.AddMinutes(1)).Should().BeTrue();
    }

    [Fact]
    public void Предикат_Исключения()
    {
        var cutoff = DateTime.UtcNow.AddDays(-30);
        Old().Tap(s => s.IsPinned = true)
            .Matches(cutoff, "закреплённый — «чат нужен»");
        Old().Tap(s => s.ExpiresAfterMinutes = 60)
            .Matches(cutoff, "временным управляет их собственный срок");
        Old().Tap(s => s.ArchivedAt = DateTime.UtcNow)
            .Matches(cutoff, "уже в архиве повторно не архивируем");
        Old().Tap(s => s.TaskId = "task-1")
            .Matches(cutoff, "чат живой задачи-исполнителя");
        Old().Tap(s =>
        {
            s.TaskId = "task-1";
            Session.TaskDoneResolver = _ => true;
            try
            {
                SessionManager.MatchesArchiveRule(s, cutoff).Should().BeTrue(
                    "задача выполнена — чат артефакт, правилу можно");
            }
            finally { Session.TaskDoneResolver = null; }
        });
    }

    // Исключение снято 28.08.2026: брошенное знакомство иначе не архивировалось никогда
    [Theory]
    [InlineData("user")]
    [InlineData("project")]
    public void Предикат_СтарыйОнбординговыйЧат_Кандидат(string kind)
    {
        var s = Old();
        s.OnboardingKind = kind;
        SessionManager.MatchesArchiveRule(s, cutoff: s.UpdatedAt.AddMinutes(1)).Should().BeTrue(
            "порог сам по себе означает, что знакомство не продолжают");
    }

    // Исключение снято 28.08.2026: штаб, остывший дольше порога, — тоже кандидат,
    // в какой бы стадии его ни бросили
    [Theory]
    [InlineData(TeamImplementStage.Idle, 2, 2)]
    [InlineData(TeamImplementStage.Wave, 4, 4)]
    [InlineData(TeamImplementStage.Planning, 0, 0)]
    [InlineData(TeamImplementStage.AwaitingDecision, 1, 0)]
    public void Предикат_СтарыйШтаб_Кандидат(TeamImplementStage stage, int wave, int closed)
    {
        var s = Old();
        s.TeamImplement = new SessionTeamImplement
        {
            Stage = stage, WaveNumber = wave, ClosedWave = closed,
        };
        SessionManager.MatchesArchiveRule(s, cutoff: s.UpdatedAt.AddMinutes(1)).Should().BeTrue(
            "живой ход и фоновые агенты отсекаются отдельно, в GetArchiveRuleCandidates");
    }

    [Fact]
    public void Предикат_ПорогНеПройден_НеКандидат()
    {
        var s = Old(); // UpdatedAt = now-100д
        SessionManager.MatchesArchiveRule(s, cutoff: s.UpdatedAt.AddDays(-1))
            .Should().BeFalse("cutoff раньше последней активности — порог не пройден");
    }

    // --- GetArchiveRuleCandidates: области владения ---

    [Fact]
    public async Task Кандидаты_ЛичныйДефолтТолькоЧатыВнеПроектов()
    {
        var (sut, projects) = BuildSut();
        var dir = Directory.CreateDirectory(Path.Combine(_tempDir, "proj_" + Guid.NewGuid().ToString("N"))).FullName;
        var project = projects.Create("Проект кандидатов", dir, _ownerId, TestUsername);
        var projectChat = await sut.CreateAsync(project.Id, ClaudeMode.Auto);
        AgeOut(sut, projectChat.Id);
        var ownChat = await sut.CreateChatAsync(_ownerId, ClaudeMode.Auto);
        AgeOut(sut, ownChat.Id);
        var stranger = _userStoreForBuild!.Add("archive-stranger-" + Guid.NewGuid().ToString("N")[..8], "pw-123456", "user");
        var strangerChat = await sut.CreateChatAsync(stranger.Id, ClaudeMode.Auto);
        AgeOut(sut, strangerChat.Id);

        var candidates = sut.GetArchiveRuleCandidates(_ownerId, projectId: null, days: 30, DateTime.UtcNow);

        candidates.Select(c => c.Id).Should().BeEquivalentTo([ownChat.Id],
            "личный дефолт не лезет в чужие проекты и к чужим владельцам");
    }

    [Fact]
    public async Task Кандидаты_ПоПроекту_ТолькоЧатыПроекта()
    {
        var (sut, projects) = BuildSut();
        var dir = Directory.CreateDirectory(Path.Combine(_tempDir, "proj_" + Guid.NewGuid().ToString("N"))).FullName;
        var project = projects.Create("Проект отбора", dir, _ownerId, TestUsername);
        var inProject = await sut.CreateAsync(project.Id, ClaudeMode.Auto);
        AgeOut(sut, inProject.Id);
        var chatless = await sut.CreateChatAsync(_ownerId, ClaudeMode.Auto);
        AgeOut(sut, chatless.Id);

        var candidates = sut.GetArchiveRuleCandidates(_ownerId, project.Id, days: 30, DateTime.UtcNow);

        candidates.Select(c => c.Id).Should().BeEquivalentTo([inProject.Id]);
    }

    // --- Сборка ---

    private static Session Old() => new() { UpdatedAt = DateTime.UtcNow.AddDays(-100) };

    private static void AgeOut(SessionManager sut, string sessionId) =>
        sut.GetById(sessionId)!.UpdatedAt = DateTime.UtcNow.AddDays(-100);

    private (SessionManager Sut, ProjectManager Projects) BuildSut()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(_tempDir, "projects.json"),
            ["Session:AutoSaveSeconds"] = "0",
            ["DefaultProjectsPath"] = Path.Combine(_tempDir, "homes"),
            ["ClaudeUserProfileDir"] = Path.Combine(_tempDir, "claude-profile"),
        }).Build();

        var userStore = new UserStore(config, new FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        _userStoreForBuild = userStore;
        var owner = userStore.Add("archive-owner-" + Guid.NewGuid().ToString("N")[..8], "pw-123456", "user");
        _ownerId = owner.Id;
        var appSettings = new AppSettingsService(config);
        var projectManager = new ProjectManager(config, userStore, appSettings);
        _historyForBuild = new ChatHistoryService(config);

        // Мок хаба с записью групп: Group(name) запоминает адресата, SendCoreAsync — пару
        string? currentGroup = null;
        var clientProxy = new Mock<IClientProxy>();
        clientProxy
            .Setup(c => c.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
            .Callback<string, object[], CancellationToken>((_, args, _) =>
                _sent.Add((currentGroup!, (ServerMessage)args[0]!)))
            .Returns(Task.CompletedTask);
        var clients = new Mock<IHubClients>();
        clients.Setup(c => c.Group(It.IsAny<string>()))
            .Callback<string>(g => currentGroup = g)
            .Returns(clientProxy.Object);
        var hub = new Mock<IHubContext<SessionHub>>();
        hub.Setup(h => h.Clients).Returns(clients.Object);

        var llmProviders = new LlmProviderRegistry(config);
        var subPool = new ClaudeSubscriptionPool(config);
        var adapters = new LlmSessionAdapterFactory(config, new SkillsService(),
            new WorkspaceKnowledgeStore(config), llmProviders, subPool);
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
        var personaMemory = new PersonaMemoryService(knowledge, personas, userStore, config,
            NullLogger<PersonaMemoryService>.Instance);
        var bindings = new PersonaBindingsService(personas, projectManager, wkStore, notesSvc, notesKb,
            knowledge, new SkillsService(), userStore, config, NullLogger<PersonaBindingsService>.Instance);
        var promptBuilder = new PersonaPromptBuilder(llmProviders);
        var sandbox = new ClaudeHomeServer.Services.Execution.SandboxManager(config,
            NullLogger<ClaudeHomeServer.Services.Execution.SandboxManager>.Instance);

        return (new SessionManager(projectManager, hub.Object, _historyForBuild, config, adapters, falCost,
            usage, appSettings, userStore, jwt, server.Object, llmProviders, notesKb, flags, personas,
            personaMemory, bindings, promptBuilder, subPool, NullLogger<SessionManager>.Instance,
            TestLauncherFactory.Instance, sandbox), projectManager);
    }
}

// Локальный хелпер читаемости для «настроить сессию и проверить, что НЕ кандидат»
internal static class ArchivePredicateCheck
{
    public static void Matches(this Session s, DateTime cutoff, string because) =>
        SessionManager.MatchesArchiveRule(s, cutoff).Should().BeFalse(because);

    public static T Tap<T>(this T value, Action<T> configure)
    {
        configure(value);
        return value;
    }
}
