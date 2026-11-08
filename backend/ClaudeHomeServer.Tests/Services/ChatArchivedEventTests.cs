using Microsoft.Extensions.DependencyInjection;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Access;
using ClaudeHomeServer.Services.Skills;
using ClaudeHomeServer.Services.Notes;
using ClaudeHomeServer.Services.Knowledge;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Memory;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
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
        Helpers.TestFs.DeleteDirectoryResilient(_tempDir);
    }

    // Широковещатель сообщений, в котором SessionManager ведёт поток по группе адресации:
    // session-группа, user_X-группа или project_X-группа. Тесты проверяют адресацию по
    // парам «адресат × сообщение», как раньше через mock IHubClients.Group(...).SendCoreAsync(...).
    private readonly TestSessionBroadcaster _broadcaster = new();
    private ChatHistoryService? _historyForBuild;
    private UserStore? _userStoreForBuild;

    // Снимок адресации: приводим три канала broadcaster-а к единому виду (адресат × сообщение),
    // как был старый _sent — для проекта адресат у нас «project_X», для пользователя — «user_X».
    private IEnumerable<(string Group, ServerMessage Msg)> Broadcasts =>
        _broadcaster.Session.Select(t => (t.SessionId, t.Message))
            .Concat(_broadcaster.Project.Select(t => ("project_" + t.ProjectId, t.Message)))
            .Concat(_broadcaster.Owner.Select(t => ("user_" + t.OwnerId, t.Message)));

    // --- Событие chat_archived: адресация ---

    [Fact]
    public async Task Архивация_ПроектнойСессии_ШлётВProjectГруппу()
    {
        var (sut, projects) = BuildSut();
        var dir = Directory.CreateDirectory(Path.Combine(_tempDir, "proj_" + Guid.NewGuid().ToString("N"))).FullName;
        var project = projects.Create("Проект события", dir, _ownerId, TestUsername);
        var chat = await sut.CreateAsync(project.Id, ClaudeMode.Auto);

        await sut.SetArchivedAsync(chat.Id, _ownerId, archived: true);

        var archivedMsgs = Broadcasts.Where(t => t.Msg is ChatArchivedMessage).ToList();
        archivedMsgs.Should().HaveCount(2, "session-группа + project-группа: по копии каждому адресату");
        archivedMsgs.Select(t => t.Group).Should().BeEquivalentTo([chat.Id, "project_" + project.Id],
            "адресация как у BroadcastChatDeletedAsync: session-группа всегда, дальше project_X");
        archivedMsgs.Should().AllSatisfy(t =>
            ((ChatArchivedMessage)t.Msg).Archived.Should().BeTrue("событие несёт направление"));
        Broadcasts.Should().NotContain(t => t.Msg is ChatDeletedMessage,
            "chat_deleted — семантика «чата больше нет», суррогатом архива не является");
    }

    [Fact]
    public async Task Архивация_ЧатаВнеПроекта_ШлётВUserГруппу()
    {
        var (sut, projects) = BuildSut();
        var chat = await sut.CreateChatAsync(_ownerId, ClaudeMode.Auto);

        await sut.SetArchivedAsync(chat.Id, _ownerId, archived: true);

        var groups = Broadcasts.Where(t => t.Msg is ChatArchivedMessage).Select(t => t.Group).ToList();
        groups.Should().BeEquivalentTo([chat.Id, "user_" + _ownerId],
            "чат вне проекта — user-группа владельца вместо project-группы");
    }

    [Fact]
    public async Task Возврат_ШлётСобытиеСНаправлениемFalse()
    {
        var (sut, projects) = BuildSut();
        var chat = await sut.CreateChatAsync(_ownerId, ClaudeMode.Auto);
        await sut.SetArchivedAsync(chat.Id, _ownerId, archived: true);
        _broadcaster.Clear();
        _broadcaster.Clear();
        _broadcaster.Clear();

        await sut.SetArchivedAsync(chat.Id, _ownerId, archived: false);

        Broadcasts.Where(t => t.Msg is ChatArchivedMessage)
            .Should().OnlyContain(t => ((ChatArchivedMessage)t.Msg).Archived == false);
    }

    // --- Чистый предикат правила (MatchesArchiveRule) ---

    [Fact]
    public void Предикат_СтарыйНезакреплённыйОбычныйЧат_Кандидат()
    {
        var s = Old();
        SessionManager.MatchesArchiveRule(s, null, cutoff: s.UpdatedAt.AddMinutes(1)).Should().BeTrue();
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
        var done = Old();
        done.TaskId = "task-1";
        SessionManager.MatchesArchiveRule(done, new DoneTaskLookup("task-1"), cutoff).Should().BeTrue(
            "задача выполнена — чат артефакт, правилу можно");
    }

    // Исключение снято 28.08.2026: брошенное знакомство иначе не архивировалось никогда
    [Theory]
    [InlineData("user")]
    [InlineData("project")]
    public void Предикат_СтарыйОнбординговыйЧат_Кандидат(string kind)
    {
        var s = Old();
        s.OnboardingKind = kind;
        SessionManager.MatchesArchiveRule(s, null, cutoff: s.UpdatedAt.AddMinutes(1)).Should().BeTrue(
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
        SessionManager.MatchesArchiveRule(s, null, cutoff: s.UpdatedAt.AddMinutes(1)).Should().BeTrue(
            "живой ход и фоновые агенты отсекаются отдельно, в GetArchiveRuleCandidates");
    }

    [Fact]
    public void Предикат_ПорогНеПройден_НеКандидат()
    {
        var s = Old(); // UpdatedAt = now-100д
        SessionManager.MatchesArchiveRule(s, null, cutoff: s.UpdatedAt.AddDays(-1))
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

    // S2d-4h: правило проекта не действует над приватным чатом — ни чужим, ни своим своим
    // (свой архивирует автор ручкой); общий чат проекта — кандидат как раньше
    [Fact]
    public async Task Кандидаты_ПриватныйЧатИсключёнИзПравила()
    {
        var (sut, projects) = BuildSut();
        var dir = Directory.CreateDirectory(Path.Combine(_tempDir, "proj_private")).FullName;
        var project = projects.Create("Проект приватных", dir, _ownerId, TestUsername);
        var editor = _userStoreForBuild!.Add("archive-editor-" + Guid.NewGuid().ToString("N")[..8], "pw-123456", "user");
        projects.SetMembers(project.Id, [new ProjectMember { UserId = editor.Id, Role = ProjectRole.Editor, AddedBy = _ownerId }]);
        var common = await sut.CreateAsync(project.Id, ClaudeMode.Auto);
        AgeOut(sut, common.Id);
        var foreignPrivate = await sut.CreateAsync(project.Id, ClaudeMode.Auto);
        sut.GetById(foreignPrivate.Id)!.PrivateToUserId = editor.Id;
        AgeOut(sut, foreignPrivate.Id);
        var ownPrivate = await sut.CreateAsync(project.Id, ClaudeMode.Auto);
        sut.GetById(ownPrivate.Id)!.PrivateToUserId = _ownerId;
        AgeOut(sut, ownPrivate.Id);

        var candidates = sut.GetArchiveRuleCandidates(_ownerId, project.Id, days: 30, DateTime.UtcNow);

        candidates.Select(c => c.Id).Should().BeEquivalentTo([common.Id],
            "тот же отбор, что считает archive-preview и архивирует тик: приватных в нём нет");
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
        var llmProviders = new LlmProviderRegistry(config);
        var subPool = new ClaudeSubscriptionPool(config);
        var adapters = new LlmSessionAdapterFactory(config, new AgentPromptSourceAdapter(new SkillsService()),
            new WorkspaceDatasetLookup(new WorkspaceKnowledgeStore(config)), llmProviders, subPool);
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

        return (SessionCoreTestHost.Build(_tempDir, s =>
        {
            s.AddSingleton<global::ClaudeHomeServer.Services.ProjectManager>(projectManager);
            s.AddSingleton<global::ClaudeHomeServer.Services.ChatHistoryService>(_historyForBuild);
            s.AddSingleton<global::Microsoft.Extensions.Configuration.IConfiguration>(config);
            s.AddSingleton<global::ClaudeHomeServer.Services.Llm.ILlmSessionAdapterFactory>(adapters);
            s.AddSingleton<global::ClaudeHomeServer.Services.FalCostService>(falCost);
            s.AddSingleton<global::ClaudeHomeServer.Services.Llm.UsageService>(usage);
            s.AddSingleton<global::ClaudeHomeServer.Services.AppSettingsService>(appSettings);
            s.AddSingleton<global::ClaudeHomeServer.Services.UserStore>(userStore);
            s.AddSingleton<global::ClaudeHomeServer.Services.JwtService>(jwt);
            s.AddSingleton<global::Microsoft.AspNetCore.Hosting.Server.IServer>(server.Object);
            s.AddSingleton<global::ClaudeHomeServer.Services.Llm.LlmProviderRegistry>(llmProviders);
            s.AddSingleton<global::ClaudeHomeServer.Services.FeatureFlagService>(flags);
            s.AddSingleton<global::ClaudeHomeServer.Services.PersonaManager>(personas);
            s.AddSingleton<global::ClaudeHomeServer.Services.PersonaBindingsService>(bindings);
            s.AddSingleton<global::ClaudeHomeServer.Services.Llm.ClaudeSubscriptionPool>(subPool);
            s.AddSingleton<global::Microsoft.Extensions.Logging.ILogger<ClaudeHomeServer.Services.SessionManager>>(NullLogger<SessionManager>.Instance);
            s.AddSingleton<global::ClaudeHomeServer.Services.Execution.ILauncherFactory>(TestLauncherFactory.Instance);
            s.AddSingleton<global::ClaudeHomeServer.Services.Execution.SandboxManager>(sandbox);
            s.AddSingleton<global::ClaudeHomeServer.Services.Composition.ISessionBroadcaster>(_broadcaster);
        }).Manager, projectManager);
    }
}

// Локальный хелпер читаемости для «настроить сессию и проверить, что НЕ кандидат»
internal static class ArchivePredicateCheck
{
    public static void Matches(this Session s, DateTime cutoff, string because) =>
        SessionManager.MatchesArchiveRule(s, null, cutoff).Should().BeFalse(because);

    public static T Tap<T>(this T value, Action<T> configure)
    {
        configure(value);
        return value;
    }
}

// Стерильный ITaskLookup для юнита-теста MatchesArchiveRule: единственный известный id →
// задача в статусе Done (остальные id — не найдена). Заменяет прежнюю гонку на статический
// Session.TaskDoneResolver.
internal sealed class DoneTaskLookup(string doneId) : ITaskLookup
{
    public TaskItem? GetById(string id) => id == doneId
        ? new TaskItem { Id = id, Status = TaskItemStatus.Done }
        : null;
}
