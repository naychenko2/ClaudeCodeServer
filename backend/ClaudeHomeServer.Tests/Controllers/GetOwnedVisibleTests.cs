using System.Security.Claims;
using ClaudeHomeServer.Controllers;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Knowledge;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Notes;
using ClaudeHomeServer.Services.Skills;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace ClaudeHomeServer.Tests.Controllers;

/// <summary>
/// S2d-3a: <c>SessionManager.GetOwnedVisible</c> = <c>GetOwned</c> и <c>SessionAccess.CanSee</c> —
/// владелец проекта не проходит на чужой приватный чат (решение 28.10): отказ неотличим от
/// «нет такого чата». Первые три контроллера (Chats, Sessions, SessionMessages) проверяются
/// на настоящих ручках; <c>GetOwned</c> не меняется — машинные пути законно работают с
/// приватным чатом через арендатора. Общий чат и чат вне проекта — прежнее поведение.
/// Полный хост не нужен: контроллеры создаются напрямую с ClaimsPrincipal вызывающего
/// (образец — SharedProjectEditorScopeGatesTests), стор сессий сидируется sessions.json
/// (образец — SessionStatusTests).
/// </summary>
public sealed class GetOwnedVisibleTests : IDisposable
{
    private readonly string _tempDir;
    private readonly IConfiguration _config;
    private readonly ProjectManager _projects;
    private readonly string _ownerId;
    private readonly string _editorId;
    private readonly string _projectId;
    private readonly SessionManager _sessions;

    // Сидированные чаты: чужой приватный (автор — редактор), свой приватный (автор — владелец),
    // общий проектный, чат вне проекта владельца.
    private const string ForeignPrivateId = "priv-foreign";
    private const string OwnPrivateId = "priv-own";
    private const string OpenChatId = "open-chat";
    private const string OutsideChatId = "outside-chat";

    public GetOwnedVisibleTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "owned_visible_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        var sessionsJsonPath = Path.Combine(_tempDir, "sessions.json");

        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_tempDir, "projects.json"),
            })
            .Build();

        var userStore = new UserStore(_config, new ClaudeHomeServer.Tests.Helpers.FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        _ownerId = userStore.GetFirst()!.Id;
        _editorId = userStore.Add("kuzma-editor", "pwd", "user").Id;

        var appSettings = new AppSettingsService(_config);
        var projectRoot = Directory.CreateDirectory(Path.Combine(_tempDir, "project")).FullName;
        _projects = new ProjectManager(_config, userStore, appSettings);
        _projectId = _projects.Create("Общий проект", projectRoot, _ownerId, userStore.GetFirst()!.Username).Id;

        WriteSessions(sessionsJsonPath, [
            new Session { Id = ForeignPrivateId, ProjectId = _projectId, PrivateToUserId = _editorId, Status = SessionStatus.Finished },
            new Session { Id = OwnPrivateId, ProjectId = _projectId, PrivateToUserId = _ownerId, Status = SessionStatus.Finished },
            new Session { Id = OpenChatId, ProjectId = _projectId, Status = SessionStatus.Finished },
            new Session { Id = OutsideChatId, OwnerId = _ownerId, Status = SessionStatus.Finished },
        ]);

        _sessions = CreateSessionManager(userStore, appSettings);
    }

    public void Dispose() => Helpers.TestFs.DeleteDirectoryResilient(_tempDir);

    private static void WriteSessions(string path, IEnumerable<Session> sessions) =>
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(sessions.ToList()));

    // Состав — как в SessionStatusTests: живой SessionManager без процессов и LLM-вызовов
    private SessionManager CreateSessionManager(UserStore userStore, AppSettingsService appSettings)
    {
        var llmProviders = new ClaudeHomeServer.Services.Llm.LlmProviderRegistry(_config);
        var subPool = new ClaudeSubscriptionPool(_config);
        var adapters = new ClaudeHomeServer.Services.Llm.LlmSessionAdapterFactory(
            _config, new AgentPromptSourceAdapter(new SkillsService()), new WorkspaceDatasetLookup(new WorkspaceKnowledgeStore(_config)), llmProviders, subPool);
        var falCost = new FalCostService(new Mock<IHttpClientFactory>().Object, _config);
        var usage = new UsageService(_config);
        var jwt = new JwtService(_config, userStore, NullLogger<JwtService>.Instance);
        var server = new Mock<Microsoft.AspNetCore.Hosting.Server.IServer>();
        server.Setup(s => s.Features).Returns(new Microsoft.AspNetCore.Http.Features.FeatureCollection());
        var wkStore = new WorkspaceKnowledgeStore(_config);
        var knowledge = new KnowledgeService(new Mock<IHttpClientFactory>().Object,
            Microsoft.Extensions.Options.Options.Create(new DifyOptions()), wkStore);
        var flags = new FeatureFlagService(userStore);
        var notesSvc = new NotesService(_projects, _config, NullLogger<NotesService>.Instance);
        var notesKb = new NotesKnowledgeService(knowledge, notesSvc, userStore, _config,
            NullLogger<NotesKnowledgeService>.Instance);
        var personas = new PersonaManager(_config);
        var bindings = new PersonaBindingsService(personas, _projects, wkStore,
            knowledge, new SkillsService(), userStore, _config, NullLogger<PersonaBindingsService>.Instance, notes: notesSvc, notesKb: notesKb);
        var sandbox = new ClaudeHomeServer.Services.Execution.SandboxManager(_config,
            NullLogger<ClaudeHomeServer.Services.Execution.SandboxManager>.Instance);
        return new SessionManager(_projects, new ChatHistoryService(_config), _config, adapters, falCost, usage, appSettings, userStore, jwt, server.Object, llmProviders, flags, personas, bindings, subPool, NullLogger<SessionManager>.Instance, TestLauncherFactory.Instance, sandbox, new TestSessionBroadcaster());
    }

    private static ControllerContext AsUser(string userId) => new()
    {
        HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", userId)])) },
    };

    // --- точка метода ---

    [Fact]
    public void GetOwnedVisible_Владелец_НаЧужомПриватном_КакНаНесуществующем_GetOwnedБезИзменений()
    {
        _sessions.GetOwnedVisible(ForeignPrivateId, _ownerId).Should().BeNull(
            "приватный чат виден только автору (решение 28.10): владелец проекта — нет");
        _sessions.GetOwnedVisible("no-such-chat", _ownerId).Should().BeNull(
            "отказ неотличим от «нет такого чата»");
        // GetOwned не изменён: машинный путь (дайджест, продолжение, тулсеты своей сессии) проходит
        _sessions.GetOwned(ForeignPrivateId, _ownerId).Should().NotBeNull();
    }

    [Fact]
    public void GetOwnedVisible_АвторСвоегоПриватного_ОбщийЧатИЧатВнеПроекта_Проходят()
    {
        _sessions.GetOwnedVisible(OwnPrivateId, _ownerId).Should().NotBeNull();
        _sessions.GetOwnedVisible(OpenChatId, _ownerId).Should().NotBeNull();
        _sessions.GetOwnedVisible(OutsideChatId, _ownerId).Should().NotBeNull();
    }

    [Fact]
    public void GetOwnedVisible_НеАрендатор_НеПроходитНикак_КакИGetOwned()
    {
        _sessions.GetOwnedVisible(ForeignPrivateId, _editorId).Should().BeNull();
        _sessions.GetOwnedVisible(OpenChatId, _editorId).Should().BeNull("владение не изменилось: участник не арендатор");
    }

    // --- ручки трёх контроллеров шага ---

    [Fact]
    public async Task ChatsController_ЧужойПриватный404_КакНаНесуществующем_СвоиИОбщийПроходят()
    {
        var c = new ChatsController(_sessions, _projects, null!, null!, null!, null!, null!, null!, null!, null!,
            NullLogger<ChatsController>.Instance) { ControllerContext = AsUser(_ownerId) };

        // Гейт закрыт — 404 без тела, неотличимо от несуществующего чата; дальше по порядку —
        // отсутствие файла (400): оно и есть доказательство пройденного гейта
        (await c.Upload(ForeignPrivateId, null)).Should().BeOfType<NotFoundResult>();
        (await c.Upload("no-such-chat", null)).Should().BeOfType<NotFoundResult>();
        (await c.Upload(OwnPrivateId, null)).Should().BeOfType<BadRequestObjectResult>();
        (await c.Upload(OpenChatId, null)).Should().BeOfType<BadRequestObjectResult>();
        (await c.Upload(OutsideChatId, null)).Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task SessionsController_ЧужойПриватный_notChatAuthor403_СвойПриватныйИОбщийУдаляются()
    {
        var c = new SessionsController(_sessions, _projects, null!, null!, null!, null!) { ControllerContext = AsUser(_ownerId) };

        var denied = await c.Delete(_projectId, ForeignPrivateId);
        var obj = denied.Should().BeOfType<ObjectResult>().Subject;
        obj.StatusCode.Should().Be(403);
        System.Text.Json.JsonSerializer.Serialize(obj.Value).Should().Contain("not_chat_author");

        (await c.Delete(_projectId, OwnPrivateId)).Should().BeOfType<NoContentResult>();
        (await c.Delete(_projectId, OpenChatId)).Should().BeOfType<NoContentResult>();
    }

    [Fact]
    public void SessionMessagesController_ЧужойПриватный404_СвойПриватныйИОбщийПроходятГейт()
    {
        var c = new SessionMessagesController(_sessions, null!, null!, null!) { ControllerContext = AsUser(_ownerId) };

        // tool=null: за гейтом — «Не указан инструмент» (400), доказательство пройденного гейта
        c.RemoveAutoAllow(ForeignPrivateId, null).Should().BeOfType<NotFoundResult>();
        c.RemoveAutoAllow(OwnPrivateId, null).Should().BeOfType<BadRequestObjectResult>();
        c.RemoveAutoAllow(OpenChatId, null).Should().BeOfType<BadRequestObjectResult>();
    }
}
