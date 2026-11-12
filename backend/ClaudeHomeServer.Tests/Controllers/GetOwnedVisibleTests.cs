using System.Security.Claims;
using ClaudeHomeServer.Controllers;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using ClaudeHomeServer.Services.Sessions;

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
/// S2d-3e: шесть ручек, решавших право на чат мимо SessionAccess.CanSee (находка §3 ревью
/// R-S2d-3a, дефект d72e1b60): чтение (history/context проектной сессии) закрывается путём
/// видимости GetAccessible (ResolveSession → CanSee), запись (parent/branch/retitle/persona,
/// PUT context) — GetOwnedVisible. Read-ручки не переведены на GetOwned: автор своего
/// приватного чата — не арендатор, а свою историю и свой контекст он читал и до шага.
/// S2d-3f: две ручки вне закрытых шести — пакетный прогон значков (icon-batch: чужой приватный
/// не входит в выборку, его транскрипт не уходит в модель арендатора) и состав участников
/// (PUT participants: гейт GetOwnedVisible, как на /parent). Свой приватный, общий чат и
/// чат вне проекта — прежнее поведение.
/// S2d-3f.1 (доработка по вердикту R-S2d-3f): Update/Delete — отказ на чужом приватном отличим
/// от «не автор»: постороннему — то же, что несуществующему (404 / тот же идемпотентный 204),
/// участнику, которому чат виден, — прежний 403. QA-P1 (решение Андрея): правка видимому
/// участнику открыта как право записи (200), удаление видимому не-автору — тот же 403.
/// S2d-3f.2: источник отчёта «наверх» (POST report)
/// резолвится GetOwnedVisible — Name и PersonaId чужого приватного не уносятся в карточку родителя.
/// QA-R1 (дефект c12cd2a2): часть SetParticipants приведена к контракту S2d-7b-3 — приватный чат
/// не становится групповым (409 private_chat_mode); DI-корень фикстуры регистрирует ленивый шов
/// IFeatureChatLinks, как прод (NoFeatureChatLinks без подсистемы tasks).
/// Полный хост не нужен: контроллеры создаются напрямую с ClaimsPrincipal вызывающего
/// (образец — SharedProjectEditorScopeGatesTests), стор сессий сидируется sessions.json
/// (образец — SessionStatusTests). Для пути участника (read-ручки) в корень DI ставится
/// настоящая точка доступа ProjectAccessService: без неё GetAccessible в SessionManager
/// падает на «владение по ResolveOwnerId», и видимость участника не проверялась бы вовсе.
/// </summary>
public sealed class GetOwnedVisibleTests : IDisposable
{
    private readonly string _tempDir;
    private readonly IConfiguration _config;
    private readonly ProjectManager _projects;
    private readonly UserStore _users;
    private readonly string _ownerId;
    private readonly string _editorId;
    // Второй участник общего проекта: «участник на чужом приватном чате» — отдельный случай
    // от владельца проекта (их отказ одинаков по форме, но решается разными ветками CanSee)
    private readonly string _otherId;
    private readonly string _projectId;
    private readonly ChatHistoryService _history;
    private readonly CountingCheapRunner _cheap = new();
    private readonly SessionManager _sessions;
    // Персоны того же стора, что у SessionManager: нужны реальному составу участников
    // в тесте SetParticipants (S2d-3f) — валидация состава резолвит их по владельцу
    private readonly PersonaManager _personas;
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
                // Read-ручки S2d-3e идут через IProjectAccess.ResolveSession: участнику нужен
                // рубильник инстанса и личный флаг — как в проде
                [SharedProjectsGate.SwitchKey] = "true",
            })
            .Build();

        var userStore = _users = new UserStore(_config, new ClaudeHomeServer.Tests.Helpers.FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        _ownerId = userStore.GetFirst()!.Id;
        _editorId = userStore.Add("kuzma-editor", "pwd", "user").Id;
        _otherId = userStore.Add("other-editor", "pwd", "user").Id;
        foreach (var id in new[] { _editorId, _otherId })
            userStore.SetFeatureFlag(id, FeatureFlagKeys.SharedProjects, true);

        var appSettings = new AppSettingsService(_config);
        var projectRoot = Directory.CreateDirectory(Path.Combine(_tempDir, "project")).FullName;
        // Реальный файл корня: за гейтом Files локатор обязан его найти (200) — это и есть
        // доказательство пройденного гейта (образец — FilesControllerTests W4-LINK-B)
        File.WriteAllText(Path.Combine(projectRoot, "a.txt"), "один\n");
        _projects = new ProjectManager(_config, userStore, appSettings);
        _projectId = _projects.Create("Общий проект", projectRoot, _ownerId, userStore.GetFirst()!.Username).Id;
        // Оба — участники общего проекта: на read-ручках автор своего приватного проходит,
        // другой участник на чужом приватном — нет
        _projects.SetMembers(_projectId,
        [
            new ProjectMember { UserId = _editorId, Role = ProjectRole.Editor, AddedBy = _ownerId },
            new ProjectMember { UserId = _otherId, Role = ProjectRole.Editor, AddedBy = _ownerId },
        ]);

        WriteSessions(sessionsJsonPath, [
            new Session { Id = ForeignPrivateId, ProjectId = _projectId, PrivateToUserId = _editorId, Status = SessionStatus.Finished },
            new Session { Id = OwnPrivateId, ProjectId = _projectId, PrivateToUserId = _ownerId, Status = SessionStatus.Finished },
            new Session { Id = OpenChatId, ProjectId = _projectId, Status = SessionStatus.Finished },
            new Session { Id = OutsideChatId, OwnerId = _ownerId, Status = SessionStatus.Finished },
        ]);

        _history = new ChatHistoryService(_config);
        _personas = new PersonaManager(_config);
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
        var personas = _personas;
        var bindings = new PersonaBindingsService(personas, _projects, wkStore,
            knowledge, new SkillsService(), userStore, _config, NullLogger<PersonaBindingsService>.Instance, notes: notesSvc, notesKb: notesKb);
        var sandbox = new ClaudeHomeServer.Services.Execution.SandboxManager(_config,
            NullLogger<ClaudeHomeServer.Services.Execution.SandboxManager>.Instance);
        return SessionCoreTestHost.Build(_tempDir, s =>
        {
            s.AddSingleton<global::ClaudeHomeServer.Services.ProjectManager>(_projects);
            s.AddSingleton<global::ClaudeHomeServer.Services.ChatHistoryService>(_history);
            s.AddSingleton<global::Microsoft.Extensions.Configuration.IConfiguration>(_config);
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
            s.AddSingleton<global::ClaudeHomeServer.Services.Composition.ISessionBroadcaster>(new TestSessionBroadcaster());
            s.AddSingleton<global::ClaudeHomeServer.Services.Llm.ICheapTextRunner>(_cheap);
            AddAccess(s);
        }).Manager;
    }

    // Корень DI с настоящей точкой доступа (тот же ленивый шов, что в проде: прямая зависимость
    // замкнула бы цикл ProjectAccessService → ISessionDirectory → SessionManager). Лямбда
    // читает _sessions позже — к первому GetService он уже собран.
    private void AddAccess(IServiceCollection services)
    {
        services.AddSingleton<IProjectAccess>(_ => new ProjectAccessService(
            _projects, _sessions, new SharedProjectsGate(_config, new FeatureFlagService(_users))));
        // Шов IFeatureChatLinks — как в проде: CoreRegistration.cs:121 регистрирует NoFeatureChatLinks,
        // TasksSubsystem.cs:65 замещает его настоящим FeatureChatLinks при включённой подсистеме
        // tasks. Подсистемы tasks в этой фикстуре нет — резолвится ровно то, что прод резолвит
        // без неё (та же регистрация, что в ChatVisibilityEndpointTests и ChatsControllerPrivateAttachmentUploadTests).
        // Без неё ленивый шов SetChatVisibility (SessionManager.cs:2576) падает на GetRequiredService.
        services.AddSingleton<IFeatureChatLinks, NoFeatureChatLinks>();
    }

    // «Дешёвый» раннер со счётчиком вызовов: нужен одним — показать, что на отказанной ручке
    // модель не звалась вовсе (расход платит арендатор), и что счётчик не пустой: на пройденном
    // входе он растёт. Ответ годен и для retitle (поле title), и для значка (iconName —
    // валидное PascalCase-имя lucide-компонента, S2d-3f)
    private sealed class CountingCheapRunner : ClaudeHomeServer.Services.Llm.ICheapTextRunner
    {
        public int Calls;

        public bool UsesLocal(string actionKey) => false;
        public bool HasFreeRoute(string actionKey) => false;
        public bool HasRoute(string actionKey) => false;
        public string DescribeRoute(string actionKey, string? fallbackModel) => "stub";

        public Task<string> RunAsync(string actionKey, string prompt, string? fallbackModel = null,
            string? ownerId = null, object? jsonFormat = null, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult("""{"title":"Новое название","iconName":"Book"}""");
        }

        public async Task<string?> RunLocalOrChosenAsync(string actionKey, string prompt, string? ownerId = null,
            object? jsonFormat = null, CancellationToken ct = default) =>
            await RunAsync(actionKey, prompt, null, ownerId, jsonFormat, ct);

        public Task<ClaudeHomeServer.Services.Llm.OneShotResult> RunDetailedAsync(string actionKey,
            string prompt, string? fallbackModel = null, string? ownerId = null, TimeSpan? timeout = null,
            int? maxTokens = null, object? jsonFormat = null, CancellationToken ct = default) =>
            Task.FromResult(new ClaudeHomeServer.Services.Llm.OneShotResult("""{"title":"Новое название"}""", null, 0));
    }

    // История чата: ключ — HistoryKeyOf (у сидированных чатов ClaudeSessionId нет → id чата),
    // та же точка, что у GetHistoryAsync. Нужна, чтобы «дошёл до транскрипта» отличался от
    // «транскрипта нет»
    private Task SeedHistory(string sessionId) =>
        _history.SaveAsync(sessionId, [new StoredUserMessage("о чём был разговор")]);

    private SessionsController SessionsAs(string userId) =>
        new(_sessions, _projects, null!, null!, null!, null!, null!) { ControllerContext = AsUser(userId) };

    private ChatsController ChatsAs(string userId) =>
        new(_sessions, _projects, null!, null!, null!, null!, null!, null!, null!, null!, null!,
            NullLogger<ChatsController>.Instance) { ControllerContext = AsUser(userId) };

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
        var c = new ChatsController(_sessions, _projects, null!, null!, null!, null!, null!, null!, null!, null!, null!,
            NullLogger<ChatsController>.Instance) { ControllerContext = AsUser(_ownerId) };

        // Гейт закрыт — 404 без тела, неотличимо от несуществующего чата; дальше по порядку —
        // отсутствие файла (400): оно и есть доказательство пройденного гейта
        (await c.Upload(ForeignPrivateId, null)).Should().BeOfType<NotFoundResult>();
        (await c.Upload("no-such-chat", null)).Should().BeOfType<NotFoundResult>();
        (await c.Upload(OwnPrivateId, null)).Should().BeOfType<BadRequestObjectResult>();
        (await c.Upload(OpenChatId, null)).Should().BeOfType<BadRequestObjectResult>();
        (await c.Upload(OutsideChatId, null)).Should().BeOfType<BadRequestObjectResult>();
    }

    // S2d-3f.1 (R-S2d-3f §1): Update/Delete на чужом приватном — неотличимы от несуществующего:
    // Update — 404, Delete — тот же 204, что и несуществующему (прежний контракт идемпотентного
    // удаления, SessionsControllerTests.Delete_NonExistentSession_Returns204; сам чат не тронут).
    // QA-P1 (решение Андрея, продуктовый вопрос 2 плана v2): вторая половина изменилась —
    // правка видимому участнику (общий чат, свой приватный) больше не авторский отказ: право
    // записи открыто редактору и выше, имя пишется; удаление видимому не-автору — прежний 403
    // по контракту ChatAuthorGate, чат цел (сидированные чаты без CreatedByUserId — nil не автор).
    [Fact]
    public async Task SessionsController_UpdateDelete_ЧужойПриватныйКакНесуществующий_УчастникПишетВидимыйЧатНоНеУдаляет()
    {
        var asOwner = SessionsAs(_ownerId);

        // Update: постороннему — неотличимо от несуществующего
        (await asOwner.Update(_projectId, ForeignPrivateId, new UpdateSessionRequest(Name: "Перебито")))
            .Should().BeOfType<NotFoundResult>();
        (await asOwner.Update(_projectId, "no-such-chat", new UpdateSessionRequest(Name: "Перебито")))
            .Should().BeOfType<NotFoundResult>();
        // Отказ не оставил следа: чужой приватный не переименован
        _sessions.GetById(ForeignPrivateId)!.Name.Should().BeNull();

        // Delete: тот же 204, что несуществующему, — и сам чат цел
        (await asOwner.Delete(_projectId, ForeignPrivateId)).Should().BeOfType<NoContentResult>();
        (await asOwner.Delete(_projectId, "no-such-chat")).Should().BeOfType<NoContentResult>();
        _sessions.GetById(ForeignPrivateId).Should().NotBeNull("204 постороннему не должен удалить чат");

        // QA-P1: редактор на видимых чатах (общий и свой приватный) — право записи, имя пишется
        var asEditor = SessionsAs(_editorId);
        foreach (var sessionId in new[] { OpenChatId, ForeignPrivateId })
        {
            (await asEditor.Update(_projectId, sessionId, new UpdateSessionRequest(Name: "Перебил")))
                .Should().BeOfType<OkObjectResult>();
            _sessions.GetById(sessionId)!.Name.Should().Be("Перебил");
        }

        // …но не автор этих чатов (сид без CreatedByUserId — nil автором не считается, fail-closed):
        // удаление — прежний 403 not_chat_author (контракт ChatAuthorGate), чат цел
        foreach (var sessionId in new[] { OpenChatId, ForeignPrivateId })
        {
            var del = (await asEditor.Delete(_projectId, sessionId)).Should().BeOfType<ObjectResult>().Subject;
            del.StatusCode.Should().Be(403);
            System.Text.Json.JsonSerializer.Serialize(del.Value).Should().Contain("not_chat_author");
            _sessions.GetById(sessionId).Should().NotBeNull("отказанный отказ права не трогает чат");
        }

        // Владелец-арендатор: свой приватный и общий удаляются, как раньше
        (await asOwner.Delete(_projectId, OwnPrivateId)).Should().BeOfType<NoContentResult>();
        (await asOwner.Delete(_projectId, OpenChatId)).Should().BeOfType<NoContentResult>();
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

    // --- ручки S2d-3e: право на чат решалось мимо SessionAccess.CanSee (находка §3 R-S2d-3a) ---

    // Чтение (history) — путь видимости: автор своего приватного чата не арендатор, а свою
    // историю он читал и до шага; владелец проекта и другой участник на чужом приватном — 404,
    // неотличимый от «нет такого чата»
    [Fact]
    public async Task SessionsController_GetHistory_ЧужойПриватныйКакНесуществующий_АвторИОбщийКакРаньше()
    {
        var asOwner = SessionsAs(_ownerId);
        (await asOwner.GetHistory(_projectId, ForeignPrivateId)).Should().BeOfType<NotFoundResult>(
            "до шага [ProjectScope] + GetById отдавал полную историю чужого приватного чата владельцу проекта");
        (await asOwner.GetHistory(_projectId, "no-such-chat")).Should().BeOfType<NotFoundResult>();
        // За гейтом — пустая история (200): это и есть доказательство пройденного входа
        (await asOwner.GetHistory(_projectId, OwnPrivateId)).Should().BeOfType<OkObjectResult>();
        (await asOwner.GetHistory(_projectId, OpenChatId)).Should().BeOfType<OkObjectResult>();

        // Автор-участник своего приватного: на GetOwned он не прошёл бы (не арендатор) —
        // путь видимости его пускает, как и раньше
        (await SessionsAs(_editorId).GetHistory(_projectId, ForeignPrivateId)).Should().BeOfType<OkObjectResult>();
        // Другой участник на чужом приватном — тот же 404; общий чат — как раньше
        var asOther = SessionsAs(_otherId);
        (await asOther.GetHistory(_projectId, ForeignPrivateId)).Should().BeOfType<NotFoundResult>();
        (await asOther.GetHistory(_projectId, OpenChatId)).Should().BeOfType<OkObjectResult>();
    }

    // Состав контекста — чтение (GetAccessible); его запись (PUT) — GetOwnedVisible: без второй
    // половины GET на чужом приватном был бы закрыт, а PUT — открыт
    [Fact]
    public async Task SessionsController_Context_ЧужойПриватныйКакНесуществующий_GetЧитаетАвтор_PutТолькоАвторАрендатор()
    {
        using var tasks = new TaskManager(_config);
        var resolver = new SessionContextResolver(_projects, tasks);
        // GetContext — синхронный (состав считается на месте), PutContext — async (меняет стор)
        SessionsController As(string userId) => new(_sessions, _projects, null!, null!, resolver, null!, null!)
            { ControllerContext = AsUser(userId) };

        As(_ownerId).GetContext(_projectId, ForeignPrivateId).Should().BeOfType<NotFoundResult>();
        As(_ownerId).GetContext(_projectId, "no-such-chat").Should().BeOfType<NotFoundResult>();
        As(_ownerId).GetContext(_projectId, OwnPrivateId).Should().BeOfType<OkObjectResult>();
        As(_ownerId).GetContext(_projectId, OpenChatId).Should().BeOfType<OkObjectResult>();
        // Автор своего приватного — читает; другой участник — нет
        As(_editorId).GetContext(_projectId, ForeignPrivateId).Should().BeOfType<OkObjectResult>();
        As(_otherId).GetContext(_projectId, ForeignPrivateId).Should().BeOfType<NotFoundResult>();

        // PUT — запись: автор-арендатор проходит, чужой приватный — 404. Участника отбивал и до
        // шага фильтр Write (запись в проект участнику не открыта) — для него отказ не изменился
        (await As(_ownerId).PutContext(_projectId, ForeignPrivateId, []))
            .Should().BeOfType<NotFoundResult>();
        (await As(_ownerId).PutContext(_projectId, OpenChatId, []))
            .Should().BeOfType<OkObjectResult>();
        (await As(_editorId).PutContext(_projectId, OpenChatId, []))
            .Should().BeOfType<NotFoundResult>();
    }

    // Собеседник чата — настройка чата (W3-B4b): владелец проекта — любой чат проекта, участник —
    // только чат, автор которого он (по CreatedByUserId). Чужой приватный неотличим от «нет такого»
    // (S2d-3e — сохранён). Свой приватный без автора в сиде (поле не проставлено) автором не
    // считается: тот же fail-closed, что у удаления чата, — 403 owner_settings, чат цел.
    [Fact]
    public void SessionsController_SetPersona_ЧужойПриватныйКакНесуществующий_ВладелецЛюбойЧатУчастникуСвой()
    {
        var req = new SetPersonaRequest(null, null); // «снять собеседника» — без резолва персоны
        var asOwner = SessionsAs(_ownerId);

        asOwner.SetPersona(_projectId, ForeignPrivateId, req).Should().BeOfType<NotFoundResult>();
        asOwner.SetPersona(_projectId, "no-such-chat", req).Should().BeOfType<NotFoundResult>();
        asOwner.SetPersona(_projectId, OwnPrivateId, req).Should().BeOfType<OkObjectResult>();
        asOwner.SetPersona(_projectId, OpenChatId, req).Should().BeOfType<OkObjectResult>();

        var denied = SessionsAs(_editorId).SetPersona(_projectId, ForeignPrivateId, req)
            .Should().BeOfType<ObjectResult>().Subject;
        denied.StatusCode.Should().Be(StatusCodes.Status403Forbidden,
            "автор-участник своего приватного теперь проходит гейт — но автор в сиде неизвестен " +
            "(CreatedByUserId пуст): неизвестный автор автором не считается");
        System.Text.Json.JsonSerializer.Serialize(denied.Value).Should().Contain(OwnerSettingsGate.ErrorCode);
        _sessions.GetById(ForeignPrivateId)!.Should().NotBeNull("отказ не трогает чат");
    }

    [Fact]
    public void ChatsController_SetParent_ЧужойПриватныйКакНесуществующий_СвойПриватныйИОбщийПроходят()
    {
        var c = ChatsAs(_ownerId);
        var req = new SetParentRequest(null); // вынести в корень списка

        c.SetParent(ForeignPrivateId, req).Should().BeOfType<NotFoundResult>(
            "до шага ParentOverrideId чужого приватного чата ставил кто угодно, кто ему писал");
        c.SetParent("no-such-chat", req).Should().BeOfType<NotFoundResult>();
        c.SetParent(OwnPrivateId, req).Should().BeOfType<OkObjectResult>();
        c.SetParent(OpenChatId, req).Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task ChatsController_Branch_ЧужойПриватный404БезЧатаПреемника_СвойИОбщийПроходятГейт()
    {
        var c = ChatsAs(_ownerId);
        var req = new BranchChatRequest(0, "текст якоря", "all");
        var before = _sessions.GetByProject(_projectId).Count;

        (await c.Branch(ForeignPrivateId, req)).Should().BeOfType<NotFoundResult>();
        (await c.Branch("no-such-chat", req)).Should().BeOfType<NotFoundResult>();
        // Отказ неотличим от «нет такого чата» и чат-преемник с содержимым приватного не создан:
        // BranchAsync после гейта не позван вовсе
        _sessions.GetByProject(_projectId).Count.Should().Be(before);

        // За гейтом — свои гейты BranchAsync (у сидированного чата нет ClaudeSessionId → 409):
        // это и есть доказательство пройденного входа
        (await c.Branch(OwnPrivateId, req)).Should().BeOfType<ConflictObjectResult>();
        (await c.Branch(OpenChatId, req)).Should().BeOfType<ConflictObjectResult>();
    }

    [Fact]
    public async Task ChatsController_Retitle_ЧужойПриватный404БезВызоваМодели_СвойИОбщийПроходятГейт()
    {
        // Транскрипт есть у всех трёх: «дошёл до раннера» отличается от «транскрипта нет»
        await SeedHistory(ForeignPrivateId);
        await SeedHistory(OwnPrivateId);
        await SeedHistory(OpenChatId);
        var c = ChatsAs(_ownerId);

        (await c.Retitle(ForeignPrivateId, default)).Should().BeOfType<NotFoundResult>();
        (await c.Retitle("no-such-chat", default)).Should().BeOfType<NotFoundResult>();
        // LLM по чужому транскрипту не звался: расход платит арендатор
        _cheap.Calls.Should().Be(0);

        // За гейтом раннер зовётся и имя меняется — счётчик не пустой
        (await c.Retitle(OwnPrivateId, default)).Should().BeOfType<OkObjectResult>();
        (await c.Retitle(OpenChatId, default)).Should().BeOfType<OkObjectResult>();
        _cheap.Calls.Should().Be(2);
    }

    // --- ручки S2d-3f: пакетный прогон значков и состав участников (дефекты 183333fb, f1e4666d) ---

    // Пакетный прогон значков берёт чаты проекта фильтром по арендатору — для проектного чата
    // это владелец проекта, и в выборку попадали чужие приватные чаты: их транскрипт уходил
    // в модель арендатора, карточка получала значок и двигала UpdatedAt. Теперь — только
    // видимые вызывающему (тот же CanSee, что у GetOwnedVisible); чужой приватный не входит
    // в выборку вовсе: ни в processed, ни в skipped (его для вызывающего нет)
    [Fact]
    public async Task SessionsController_IconBatch_ЧужойПриватныйНеТронутБезВызоваМодели_СвойПриватныйИОбщийКакРаньше()
    {
        // Транскрипт есть у всех трёх: «дошёл до модели» отличается от «транскрипта нет»
        await SeedHistory(ForeignPrivateId);
        await SeedHistory(OwnPrivateId);
        await SeedHistory(OpenChatId);

        var res = (await SessionsAs(_ownerId).IconBatch(_projectId, default))
            .Should().BeOfType<OkObjectResult>().Subject;
        // Размечены ровно два видимых чата; чужой приватный не попал ни в processed, ни в skipped
        ((IconBatchResult)res.Value!).Processed.Should().Be(2);
        ((IconBatchResult)res.Value!).Skipped.Should().Be(0);
        // Модель звалась дважды — по транскриптам своего приватного и общего; по чужому приватному — ни разу
        _cheap.Calls.Should().Be(2);
        // Карточка чужого приватного не написана: значка нет
        _sessions.GetById(ForeignPrivateId)!.Topic.Should().BeNull();
        // Свой приватный и общий — прежнее поведение: значок на месте
        _sessions.GetById(OwnPrivateId)!.Topic.Should().Be("Book");
        _sessions.GetById(OpenChatId)!.Topic.Should().Be("Book");

        // Участник не арендатор: пакетный прогон его чатов не касался и до шага (фильтр по
        // арендатору) — шаг это не меняет; чужой приватный по-прежнему не тронут
        var asMember = (await SessionsAs(_otherId).IconBatch(_projectId, default))
            .Should().BeOfType<OkObjectResult>().Subject;
        ((IconBatchResult)asMember.Value!).Processed.Should().Be(0);
        _cheap.Calls.Should().Be(2);
        _sessions.GetById(ForeignPrivateId)!.Topic.Should().BeNull();
    }

    // Состав участников группового чата: сервис проверял только ResolveOwnerId — для проектного
    // чата это владелец проекта, и он переписывал состав чужого приватного чата (и спикера).
    // Гейт GetOwnedVisible в контроллере, тот же приём, что у /parent: чужой приватный — 404,
    // неотличимый от «нет такого чата»; отказ не оставил следа в карточке.
    [Fact]
    public void ChatsController_SetParticipants_ЧужойПриватный404БезПравкиСостава_АвторКакРаньше()
    {
        // Тот же запрос, что в репро дефекта: владелец проекта с РЕАЛЬНЫМ составом своих персон
        // на приватном чате участника — до правки это был 200 с записью состава и сменой спикера
        var suffix = Guid.NewGuid().ToString("N")[..6];
        var p1 = _personas.Create(_ownerId, $"Чужой-1-{suffix}", null, null, null, null, null,
            PersonaScope.Project, _projectId, null, null, false).Id;
        var p2 = _personas.Create(_ownerId, $"Чужой-2-{suffix}", null, null, null, null, null,
            PersonaScope.Project, _projectId, null, null, false).Id;
        var real = new SetParticipantsRequest([p1, p2]);
        var c = ChatsAs(_ownerId);

        c.SetParticipants(ForeignPrivateId, real).Should().BeOfType<NotFoundResult>(
            "владелец проекта — не адресат чужого приватного: 404, неотличимо от несуществующего чата");
        c.SetParticipants("no-such-chat", real).Should().BeOfType<NotFoundResult>();
        // Автор-участник своего приватного: владение не изменилось (он не арендатор) — 404 как до шага
        ChatsAs(_editorId).SetParticipants(ForeignPrivateId, real).Should().BeOfType<NotFoundResult>();
        // Отказ не оставил следа: состав и спикер чужого приватного не тронуты
        _sessions.GetById(ForeignPrivateId)!.Participants.Should().BeNullOrEmpty();
        _sessions.GetById(ForeignPrivateId)!.PersonaId.Should().BeNull();

        // За гейтом — доказательство пройденного входа. Свой приватный после S2d-7b-3:
        // 409 private_chat_mode (дверь «приватный чат не становится групповым» идёт за гейтом
        // GetOwnedVisible и перед ValidateParticipants); общий — прежний 400 за пустой состав.
        var empty = new SetParticipantsRequest([]);
        var ownDenied = c.SetParticipants(OwnPrivateId, empty).Should().BeOfType<ConflictObjectResult>().Subject;
        System.Text.Json.JsonSerializer.Serialize(ownDenied.Value).Should().Contain("private_chat_mode");
        c.SetParticipants(OpenChatId, empty).Should().BeOfType<BadRequestObjectResult>();
    }

    // Реальный состав на видимых чатах — прежнее поведение: владелец-арендатор меняет состав
    // общего чата (проектные персоны своего проекта — не глобальные, рамка EnsurePersonasVisibleInFrame
    // их пропускает). Свой приватный после S2d-7b-3 групповым не делается: 409 private_chat_mode,
    // состав не записан (тот же контракт, что ChatVisibilityEndpointTests.ПриватныйЧат_Участники_409privateChatMode_ОбщийЧат_ПрежнийПуть)
    [Fact]
    public void ChatsController_SetParticipants_ОбщийЧатМеняетСоставКакРаньше_СвойПриватный409БезПравки()
    {
        var suffix = Guid.NewGuid().ToString("N")[..6];
        var p1 = _personas.Create(_ownerId, $"Участник-1-{suffix}", null, null, null, null, null,
            PersonaScope.Project, _projectId, null, null, false).Id;
        var p2 = _personas.Create(_ownerId, $"Участник-2-{suffix}", null, null, null, null, null,
            PersonaScope.Project, _projectId, null, null, false).Id;
        var c = ChatsAs(_ownerId);

        var updated = c.SetParticipants(OpenChatId, new SetParticipantsRequest([p1, p2]))
            .Should().BeOfType<OkObjectResult>().Subject;
        ((Session)updated.Value!).Participants.Should().Equal(p1, p2);
        _sessions.GetById(OpenChatId)!.Participants.Should().Equal(p1, p2);
        // Спикер — первая из нового состава (прежнее правило SwitchSpeaker для чата без спикера)
        _sessions.GetById(OpenChatId)!.PersonaId.Should().Be(p1);

        // Свой приватный (автор — он же арендатор): после S2d-7b-3 групповым не делается —
        // 409 private_chat_mode, состав не тронут
        var denied = c.SetParticipants(OwnPrivateId, new SetParticipantsRequest([p1, p2]))
            .Should().BeOfType<ConflictObjectResult>().Subject;
        System.Text.Json.JsonSerializer.Serialize(denied.Value).Should().Contain("private_chat_mode");
        _sessions.GetById(OwnPrivateId)!.Participants.Should().BeNullOrEmpty();
    }

    // --- S2d-3f.2 (R-S2d-3f §2): источник отчёта «наверх» — POST /api/sessions/{sid}/report ---

    // ReportUpAsync резолвил источник через GetOwned: для проектного чата это владелец проекта,
    // и он мог указать приватный чат участника — карточка в родителе уносила его Name и PersonaId.
    // Гейт CanSee на входе ReportUpAsync: чужой приватный неотличим от «нет такого чата»,
    // карточки в родителе нет вовсе. Автор-арендатор своего приватного и общий чат — как раньше
    // (карточка с именем чата, тот же формат, что в SessionManagerTests.ReportUp_БезПерсоны_*).
    [Fact]
    public async Task SessionMessagesController_ReportUp_ЧужойПриватныйКакНесуществующийБезКарточки_СвойПриватныйИОбщийКакРаньше()
    {
        using var tasks = new TaskManager(_config);
        var c = new SessionMessagesController(_sessions, new SessionMessagingService(_sessions, _projects, tasks),
            null!, null!) { ControllerContext = AsUser(_ownerId) };

        // Пара «родитель → дети»: родитель — общий чат владельца; чужой приватный — с лицом
        // (проектная персона), свой приватный и общий ребёнок — без лица, как обычный исполнитель
        var parent = await _sessions.CreateAsync(_projectId, ClaudeMode.Auto, name: "Родитель", createdByUserId: _ownerId);
        parent.ClaudeSessionId = "cli-" + Guid.NewGuid().ToString("N");
        var suffix = Guid.NewGuid().ToString("N")[..6];
        var persona = _personas.Create(_ownerId, $"Персона-отчёт-{suffix}", null, null, null, null, null,
            PersonaScope.Project, _projectId, null, null, false).Id;
        var foreign = await _sessions.CreateAsync(_projectId, ClaudeMode.Auto, name: "Приватный чат участника",
            personaId: persona, createdByUserId: _editorId);
        _sessions.SetChatVisibility(foreign.Id, _editorId, true).Outcome.Should().Be(ChatVisibilityOutcome.Ok,
            "автор, общий проект (есть участники), сообщений нет — тот же путь, что у тумблера S2d-7a");
        var own = await _sessions.CreateAsync(_projectId, ClaudeMode.Auto, name: "Приватный чат владельца",
            createdByUserId: _ownerId);
        _sessions.SetChatVisibility(own.Id, _ownerId, true).Outcome.Should().Be(ChatVisibilityOutcome.Ok);
        var open = await _sessions.CreateAsync(_projectId, ClaudeMode.Auto, name: "Общий чат-ребёнок",
            createdByUserId: _ownerId);
        // Связь — сервисным SetParent по арендатору: владелец проекта — арендатор всех трёх детей
        _sessions.SetParent(foreign.Id, parent.Id, _ownerId).Should().NotBeNull();
        _sessions.SetParent(own.Id, parent.Id, _ownerId).Should().NotBeNull();
        _sessions.SetParent(open.Id, parent.Id, _ownerId).Should().NotBeNull();

        // Чужой приватный: неотличим от несуществующего, и отказа нет в ленте родителя —
        // ни карточки с именем, ни карточки с персоной
        (await c.ReportUp(foreign.Id, new ReportUpRequest("готово наполовину"))).Should().BeOfType<NotFoundResult>();
        (await c.ReportUp("no-such-chat", new ReportUpRequest("тот же текст"))).Should().BeOfType<NotFoundResult>();
        (await _sessions.GetHistoryAsync(parent.Id)).Should().BeEmpty(
            "отказанный отчёт не оставляет следа в ленте родителя: ни Name, ни PersonaId чужого приватного");

        // Автор-арендатор своего приватного и общий ребёнок — прежнее поведение: карточка с именем чата
        (await c.ReportUp(own.Id, new ReportUpRequest("свой отчёт"))).Should().BeOfType<OkObjectResult>();
        (await c.ReportUp(open.Id, new ReportUpRequest("общий отчёт"))).Should().BeOfType<OkObjectResult>();
        var history = await _sessions.GetHistoryAsync(parent.Id);
        history.OfType<StoredUserMessage>().Select(m => m.SenderChatName).Should()
            .Equal("Приватный чат владельца", "Общий чат-ребёнок");
    }
}
