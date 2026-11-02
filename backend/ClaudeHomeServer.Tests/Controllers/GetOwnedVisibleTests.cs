using System.Security.Claims;
using ClaudeHomeServer.Controllers;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Access;
using ClaudeHomeServer.Services.ChatContext;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Git;
using ClaudeHomeServer.Services.Knowledge;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Notes;
using ClaudeHomeServer.Services.Skills;
using ClaudeHomeServer.Services.Tasks;
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
/// S2d-3b: тот же сдвиг в Board, ChatContext, McpSessionContext — ручки этих контроллеров
/// ниже тем же сидированным набором чатов.
/// S2d-3c: тот же сдвиг в Files (общий гейт GetOwnedSession content/stream/locate), Git
/// (RootFor по ?sessionId=) и MyWall (состав стены): чужой приватный неотличим от
/// несуществующего чата — 404 / мёртвый id; автор и общий чат — как раньше.
/// Полный хост не нужен: контроллеры создаются напрямую с ClaimsPrincipal вызывающего
/// (образец — SharedProjectEditorScopeGatesTests), стор сессий сидируется sessions.json
/// (образец — SessionStatusTests).
/// </summary>
public sealed class GetOwnedVisibleTests : IDisposable
{
    private readonly string _tempDir;
    private readonly IConfiguration _config;
    private readonly ProjectManager _projects;
    private readonly UserStore _users;
    private readonly string _ownerId;
    private readonly string _editorId;
    private readonly string _projectId;
    private readonly SessionManager _sessions;
    // Локатор без git: деградирует до корня проекта (fail-closed, ProjectTreeLocator:43-52) —
    // для доказательства пройденного гейта Files больше ничего не нужно
    private readonly ProjectTreeLocator _locator = new(TestLauncherFactory.Instance, NullLogger<ProjectTreeLocator>.Instance);

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

        var userStore = _users = new UserStore(_config, new ClaudeHomeServer.Tests.Helpers.FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        _ownerId = userStore.GetFirst()!.Id;
        _editorId = userStore.Add("kuzma-editor", "pwd", "user").Id;

        var appSettings = new AppSettingsService(_config);
        var projectRoot = Directory.CreateDirectory(Path.Combine(_tempDir, "project")).FullName;
        // Реальный файл корня: за гейтом Files локатор обязан его найти (200) — это и есть
        // доказательство пройденного гейта (образец — FilesControllerTests W4-LINK-B)
        File.WriteAllText(Path.Combine(projectRoot, "a.txt"), "один\n");
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

    // --- ручки трёх контроллеров шага S2d-3b ---

    [Fact]
    public void BoardController_ЧужойПриватный404_КакНаНесуществующем_СвойПриватныйИОбщийПроходятГейт()
    {
        // board/executions за гейтом не тронуты: на Finished-сессии RespondPermission
        // выходит на «протухшем ответе» (карточки нет) — NoContent и есть пройденный гейт
        var c = new BoardController(null!, _sessions, null!) { ControllerContext = AsUser(_ownerId) };

        c.AllowPermission(ForeignPrivateId, "r1").Should().BeOfType<NotFoundResult>();
        c.AllowPermission("no-such-chat", "r1").Should().BeOfType<NotFoundResult>();
        c.AllowPermission(OwnPrivateId, "r1").Should().BeOfType<NoContentResult>();
        c.AllowPermission(OpenChatId, "r1").Should().BeOfType<NoContentResult>();
        c.AllowPermission(OutsideChatId, "r1").Should().BeOfType<NoContentResult>();
    }

    [Fact]
    public void ChatContextController_ЧужойПриватный404_КакНаНесуществующем_СвойПриватныйИОбщийПроходятГейт()
    {
        // Пустой стор и пустой реестр: за гейтом — пустой контекст (200),
        // он и есть доказательство пройденного гейта
        var registry = new ContextKindRegistry([]);
        var store = new ChatContextStore(Path.Combine(_tempDir, "ctx"), registry);
        var c = new ChatContextController(_sessions, _projects, registry, store, []) { ControllerContext = AsUser(_ownerId) };

        c.Get(ForeignPrivateId).Should().BeOfType<NotFoundResult>();
        c.Get("no-such-chat").Should().BeOfType<NotFoundResult>();
        c.Get(OwnPrivateId).Should().BeOfType<OkObjectResult>();
        c.Get(OpenChatId).Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public void McpSessionContext_ЧужойПриватный403_КакНаЧужомЧате_СвойПриватныйИОбщийПроходятГейт()
    {
        // Сессия берётся из X-Caller-Session-Id (его ставит api() MCP-сервера), не из параметра
        using var tasks = new TaskManager(_config);
        var resolver = new SessionContextResolver(_projects, tasks);
        IActionResult Call(string sessionId)
        {
            var http = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", _ownerId)])),
            };
            http.Request.Headers.Add(McpEndpoints.CallerSessionHeader, sessionId);
            return new McpSessionContextController(_sessions, resolver)
            { ControllerContext = new ControllerContext { HttpContext = http } }.Get();
        }

        // Форма отказа — та же, что на чужом чате (403), а не 404: гейт контроллера различает
        // «нет сессии» и «чужая сессия»; чужой приватный теперь проходит по второй ветке
        Call(ForeignPrivateId).Should().BeOfType<ObjectResult>().Subject.StatusCode.Should().Be(403);
        Call(OwnPrivateId).Should().BeOfType<OkObjectResult>();
        Call(OpenChatId).Should().BeOfType<OkObjectResult>();
    }

    // --- ручки трёх контроллеров шага S2d-3c ---

    // Один параметр query для RootFor (DefaultHttpRequest.Query принимает IQueryCollection;
    // QueryString — struct, он сюда не присваивается)
    private sealed class SingleQuery(string key, string value) : IQueryCollection
    {
        private readonly Dictionary<string, Microsoft.Extensions.Primitives.StringValues> _items =
            new(StringComparer.OrdinalIgnoreCase) { [key] = value };
        public int Count => _items.Count;
        public ICollection<string> Keys => _items.Keys;
        public bool ContainsKey(string key) => _items.ContainsKey(key);
        public bool TryGetValue(string key, out Microsoft.Extensions.Primitives.StringValues value) =>
            _items.TryGetValue(key, out value);
        public Microsoft.Extensions.Primitives.StringValues this[string name] =>
            _items.TryGetValue(name, out var v) ? v : Microsoft.Extensions.Primitives.StringValues.Empty;
        public IEnumerator<KeyValuePair<string, Microsoft.Extensions.Primitives.StringValues>> GetEnumerator() => _items.GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => _items.GetEnumerator();
    }

    // Область запроса для TenantId в FilesController (фильтр [ProjectScope] при прямом вызове
    // не работает — область ставится явно, как это делает сам фильтр)
    private static ControllerContext AsUserScoped(string userId)
    {
        var http = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", userId)])) };
        http.SetRequestScope(new RequestScope(userId, userId, ProjectRole.Owner));
        return new ControllerContext { HttpContext = http };
    }

    [Fact]
    public async Task FilesController_ЧужойПриватный404_КакНаНесуществующем_СвойПриватныйИОбщийПроходятГейт()
    {
        // Гейт GetOwnedSession один для content/stream/locate; здесь — locate: за гейтом
        // локатор резолвит реальный файл корня (200), он и есть доказательство пройденного гейта
        var c = new FilesController(null!, _projects, null!, null!, _config, null!,
            NullLogger<FilesController>.Instance, null!, null!, _locator, sessions: _sessions)
        { ControllerContext = AsUserScoped(_ownerId) };

        var denied = (await c.Locate(_projectId, new LocateRequest(["a.txt"], ForeignPrivateId), default))
            .Should().BeOfType<NotFoundObjectResult>().Subject;
        // сериализатор экранирует кириллицу \uXXXX — тело сравниваем по распарсенному JSON
        System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(denied.Value))
            .RootElement.GetProperty("error").GetString().Should().Be("Чат не найден");

        // Тот же ответ, что на несуществующем: чужой приватный неотличим от «нет такого чата»
        var missing = (await c.Locate(_projectId, new LocateRequest(["a.txt"], "no-such-chat"), default))
            .Should().BeOfType<NotFoundObjectResult>().Subject;
        System.Text.Json.JsonSerializer.Serialize(missing.Value).Should().Be(
            System.Text.Json.JsonSerializer.Serialize(denied.Value));

        var own = (await c.Locate(_projectId, new LocateRequest(["a.txt"], OwnPrivateId), default))
            .Should().BeOfType<OkObjectResult>().Subject;
        ((IReadOnlyList<LocatedPath>)own.Value!).Should().ContainSingle(p => p.Path == "a.txt");
        var open = (await c.Locate(_projectId, new LocateRequest(["a.txt"], OpenChatId), default))
            .Should().BeOfType<OkObjectResult>().Subject;
        ((IReadOnlyList<LocatedPath>)open.Value!).Should().ContainSingle(p => p.Path == "a.txt");
    }

    [Fact]
    public async Task GitController_ЧужойПриватный404_КакНаНесуществующем_СвойПриватныйИОбщийПроходятГейт()
    {
        // RootFor бросает KeyNotFoundException → 404 (как на чужом/несуществующем чате).
        // За гейтом статус работает на не-git корне: IsRepo=false, 200 — это и есть
        // пройденный гейт (StatusAsync не кидает на не-репозитории, GitService:206-207)
        var git = new GitService(TestLauncherFactory.Instance);
        var attribution = new CommitAttributionService(git, _sessions, _projects,
            new ProjectFileSessionsIndex(_sessions, new ChatHistoryService(_config), _projects));
        var c = new GitController(git, null!, null!, _projects, null!, null!, _sessions, null!, attribution);

        async Task<IActionResult> StatusWith(string sessionId)
        {
            var http = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", _ownerId)])) };
            http.Request.Query = new SingleQuery("sessionId", sessionId);
            c.ControllerContext = new ControllerContext { HttpContext = http };
            return await c.Status(_projectId, default);
        }

        (await StatusWith(ForeignPrivateId)).Should().BeOfType<NotFoundResult>();
        (await StatusWith("no-such-chat")).Should().BeOfType<NotFoundResult>();
        (await StatusWith(OwnPrivateId)).Should().BeOfType<OkObjectResult>();
        (await StatusWith(OpenChatId)).Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public void MyWallController_ЧужойПриватныйВыпадаетИзСтены_КакМёртвыйId_АвторИОбщийНаСтене()
    {
        var c = new MyWallController(_users, _sessions) { ControllerContext = AsUser(_ownerId) };

        // Чужой приватный (автор — редактор) выпадает из состава молча, как удалённый id:
        // валидация стены молчаливая по построению (Put не различает мёртвые id)
        var put = c.Put(new PutWallRequest([ForeignPrivateId, OwnPrivateId, OpenChatId]))
            .Should().BeOfType<OkObjectResult>().Subject;
        ((WallDto)put.Value!).Chats.Select(s => s.Id).Should().Equal(OwnPrivateId, OpenChatId);

        // Тот же состав после чтения из users.json (GET) — чистка закреплена, а не разовая
        var get = c.Get().Should().BeOfType<OkObjectResult>().Subject;
        ((WallDto)get.Value!).Chats.Select(s => s.Id).Should().Equal(OwnPrivateId, OpenChatId);

        // Автор своего приватного (он и арендатор) — на стене проходит. Стена проектных
        // чатов — путь арендатора и была (GetOwned по арендатору, «не-арендатор не проходит
        // никак» выше): приватный чат редактора на стене редактора не стоял и до шага —
        // владение этим шагом не меняется.
        var own = c.Put(new PutWallRequest([OwnPrivateId])).Should().BeOfType<OkObjectResult>().Subject;
        ((WallDto)own.Value!).Chats.Select(s => s.Id).Should().Equal(OwnPrivateId);
    }
}
