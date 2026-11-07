using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
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

// S2d-2a (M1): утечка событий приватного чата в общем проекте ловится на уровне SessionManager, а не
// на чистой функции адресации. Инвариант: событие приватного чата не адресовано ни одному участнику
// проекта (ни группе project_{id}), а уходит автору (user_{author}) и в session-группу.
// Пять точек рассылки: статус, архив, переименование, удаление, внеходовое сообщение.
public class PrivateChatBroadcastLeakTests : IDisposable
{
    private string _ownerId = null!;
    private const string TestUsername = "test-user";
    private const string Author = "author-user";

    private readonly string _tempDir;
    private readonly TestSessionBroadcaster _broadcaster = new();

    public PrivateChatBroadcastLeakTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "private_chat_leak_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose() => Helpers.TestFs.DeleteDirectoryResilient(_tempDir);

    // Точки рассылки: имя точки → вызов на сессии (приватные методы — через рефлексию, публичные напрямую)
    public static TheoryData<string> Points => new() { "status", "archived", "renamed", "deleted", "message" };

    [Theory]
    [MemberData(nameof(Points))]
    public async Task ПриватныйЧатОбщегоПроекта_НеАдресованГруппеПроекта_УходитАвтору(string point)
    {
        var (sut, projects) = BuildSut();
        var chat = await CreateChatInSharedProject(sut, projects);
        chat.PrivateToUserId = Author;

        await Fire(sut, chat, point);

        _broadcaster.Project.Should().BeEmpty($"точка «{point}»: приватный чат не раскрываем участникам проекта");
        _broadcaster.Owner.Select(t => t.OwnerId).Should().Contain(Author, $"точка «{point}»: автор получает событие");
    }

    [Theory]
    [MemberData(nameof(Points))]
    public async Task ПустойPrivateToUserId_FailClosed_НеАдресованГруппеПроекта(string point)
    {
        var (sut, projects) = BuildSut();
        var chat = await CreateChatInSharedProject(sut, projects);
        chat.PrivateToUserId = "";

        await Fire(sut, chat, point);

        _broadcaster.Project.Should().BeEmpty($"точка «{point}»: пустая строка = «чат не видит никто», как в SessionAccess.CanSee");
    }

    [Fact]
    public async Task ОбщийЧат_ПоПрежнему_ШлётВГруппуПроекта()
    {
        var (sut, projects) = BuildSut();
        var chat = await CreateChatInSharedProject(sut, projects);

        await Fire(sut, chat, "status");

        _broadcaster.Project.Should().NotBeEmpty("контроль: у общего чата адресация группой проекта сохранена");
    }

    private async Task<Session> CreateChatInSharedProject(SessionManager sut, ProjectManager projects)
    {
        var dir = Directory.CreateDirectory(Path.Combine(_tempDir, "proj_" + Guid.NewGuid().ToString("N"))).FullName;
        var project = projects.Create("Общий проект", dir, _ownerId, TestUsername);
        var chat = await sut.CreateAsync(project.Id, ClaudeMode.Auto);
        _broadcaster.Clear();
        return sut.GetById(chat.Id)!;
    }

    private static async Task Fire(SessionManager sut, Session chat, string point)
    {
        switch (point)
        {
            case "status":
                await Invoke(sut, "BroadcastStatusChangeAsync", chat.Id, chat, SessionStatus.Active, null, 0);
                break;
            case "archived":
                await Invoke(sut, "BroadcastChatArchivedAsync", chat.Id, chat, true);
                break;
            case "renamed":
                await Invoke(sut, "BroadcastChatRenamedAsync", chat.Id, chat, "Новое имя");
                break;
            case "deleted":
                await Invoke(sut, "BroadcastChatDeletedAsync", chat.Id, chat);
                break;
            case "message":
                await sut.BroadcastSessionMessageAsync(chat.Id, new ChatRenamedMessage("Имя", null));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(point));
        }
    }

    // S3-3: точки «архив» и «переименование» живут в ролях ядра (ChatSettings, ChatNaming).
    // Метод ищется на фасаде, а если его там нет — на его полях-ролей: проверяется адресация
    // события, а не то, в каком классе лежит тело.
    private static Task Invoke(SessionManager sut, string method, params object?[] args)
    {
        foreach (var target in CoreObjects(sut))
        {
            var mi = target.GetType().GetMethod(method,
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            if (mi is not null) return (Task)mi.Invoke(target, args)!;
        }
        throw new MissingMethodException(nameof(SessionManager), method);
    }

    private static IEnumerable<object> CoreObjects(SessionManager sut)
    {
        yield return sut;
        foreach (var f in typeof(SessionManager).GetFields(
                     System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic))
        {
            if (f.FieldType.Namespace != "ClaudeHomeServer.Services.Sessions") continue;
            if (f.GetValue(sut) is { } role) yield return role;
        }
    }

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
        var owner = userStore.Add("leak-owner-" + Guid.NewGuid().ToString("N")[..8], "pw-123456", "user");
        _ownerId = owner.Id;
        var appSettings = new AppSettingsService(config);
        var projectManager = new ProjectManager(config, userStore, appSettings);
        var history = new ChatHistoryService(config);

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

        return (new SessionManager(projectManager, history, config, adapters, falCost,
            usage, appSettings, userStore, jwt, server.Object, llmProviders, flags, personas,
            bindings, subPool, NullLogger<SessionManager>.Instance,
            TestLauncherFactory.Instance, sandbox, broadcaster: _broadcaster), projectManager);
    }
}
