using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Knowledge;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Notes;
using ClaudeHomeServer.Services.Skills;
using ClaudeHomeServer.Services.Spend;
using ClaudeHomeServer.Services.Spend.Controllers;
using ClaudeHomeServer.Services.Tasks;
using ClaudeHomeServer.Tests.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Moq;

namespace ClaudeHomeServer.Tests.Controllers;

// Unit-тесты перенесённого SpendController (Этап 5, шаг 3).
// Покрывают: HTTP-мэппинг гейта SpendAccess → 403/404,
// Pivot-валидацию groupBy, Turn-клампы (limit/offset),
// Passport-семантику (чужой ход = 404, не 403),
// Badge/TaskPrompt-авторизацию.
// Таблицу правил SpendAccess НЕ дублируем — она покрыта в SpendAnalyticsTests.
public class SpendControllerTests : IDisposable
{
    private readonly string _dir;
    private readonly UserStore _userStore;
    private readonly ProjectManager _projectManager;
    private readonly PersonaManager _personas;
    private readonly TaskManager _tasks;
    private readonly ITaskLookup _taskLookup;
    private readonly ChatHistoryService _history;
    private readonly SessionManager _sessions;
    private readonly ClaudeHomeServer.Services.Llm.LlmProviderRegistry _llmProviders;
    private readonly ClaudeHomeServer.Services.Llm.IModelResolver _llmResolver;

    public SpendControllerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "spend_ctl_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_dir, "projects.json"),
                ["Glif:McpToken"] = "glif-test-token",
            })
            .Build();

        _userStore = new UserStore(config, new FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        var appSettings = new AppSettingsService(config);
        _projectManager = new ProjectManager(config, _userStore, appSettings);
        _personas = new PersonaManager(config);
        _tasks = new TaskManager(config, personas: _personas);
        _taskLookup = new TaskLookupAdapter(_tasks);
        _history = new ChatHistoryService(config);

        var broadcaster = new TestSessionBroadcaster();

        var llmProviders = new LlmProviderRegistry(config);
        _llmProviders = llmProviders;
        _llmResolver = new LlmModelResolverAdapter(llmProviders);
        var subPool = new ClaudeSubscriptionPool(config);
        var adapters = new LlmSessionAdapterFactory(
            config, new AgentPromptSourceAdapter(new SkillsService()),
            new WorkspaceDatasetLookup(new WorkspaceKnowledgeStore(config)),
            llmProviders, subPool);
        var falCost = new FalCostService(new Mock<IHttpClientFactory>().Object, config);
        var glif = new GlifAccountService(new Mock<IHttpClientFactory>().Object, config);
        var usage = new UsageService(config);
        var jwt = new JwtService(config, _userStore, NullLogger<JwtService>.Instance);
        var server = new Mock<Microsoft.AspNetCore.Hosting.Server.IServer>();
        server.Setup(s => s.Features).Returns(new FeatureCollection());
        var wkStore = new WorkspaceKnowledgeStore(config);
        var knowledge = new KnowledgeService(new Mock<IHttpClientFactory>().Object,
            Microsoft.Extensions.Options.Options.Create(new DifyOptions()), wkStore);
        var flags = new FeatureFlagService(_userStore);
        var notesSvc = new NotesService(_projectManager, config, NullLogger<NotesService>.Instance);
        var notesKb = new NotesKnowledgeService(knowledge, notesSvc, _userStore, config,
            NullLogger<NotesKnowledgeService>.Instance);
        var bindings = new PersonaBindingsService(_personas, _projectManager, wkStore,
            knowledge, new SkillsService(), _userStore, config,
            NullLogger<PersonaBindingsService>.Instance, notes: notesSvc, notesKb: notesKb);
        var sandbox = new ClaudeHomeServer.Services.Execution.SandboxManager(config,
            NullLogger<ClaudeHomeServer.Services.Execution.SandboxManager>.Instance);
        _sessions = new SessionManager(_projectManager, _history, config, adapters, falCost, usage,
            appSettings, _userStore, jwt, server.Object, llmProviders, flags, _personas,
            bindings, subPool, NullLogger<SessionManager>.Instance, TestLauncherFactory.Instance, sandbox,
            broadcaster, glif: glif);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    // --- Хелперы ---

    private SpendController NewController(SpendStore store, string userId, bool admin)
    {
        var analytics = new SpendAnalyticsService(store,
            new SessionDirectoryAdapter(_sessions),
            _projectManager, _taskLookup,
            new PersonaLookupAdapter(_personas),
            _userStore, _llmResolver);
        var ctl = new SpendController(analytics,
            new SessionDirectoryAdapter(_sessions),
            _taskLookup,
            new TaskPromptMetricsStore(Path.Combine(_dir, "spend_metrics_" + Guid.NewGuid().ToString("N"))));
        ctl.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = Principal(userId, admin) },
        };
        return ctl;
    }

    // Claim ТИПА "sub" — иначе CurrentUserId пуст; роль — ClaimTypes.Role
    // (у ClaimsIdentity это RoleClaimType по умолчанию, User.IsInRole сработает)
    private static ClaimsPrincipal Principal(string userId, bool admin)
    {
        var claims = new List<Claim> { new(JwtRegisteredClaimNames.Sub, userId) };
        if (admin) claims.Add(new(ClaimTypes.Role, "admin"));
        return new(new ClaimsIdentity(claims, "test"));
    }

    // Фабрика SpendStore с уникальным подкаталогом (тесты изолированы)
    private SpendStore NewStore(string suffix) =>
        new(Path.Combine(_dir, "store_" + suffix), detailDays: 30);

    // Запись расхода с детерминированными полями
    private static SpendRecord Rec(DateTime ts, string owner, string? session = null,
        string? project = null, long input = 10) => new()
        {
            Timestamp = ts,
            OwnerId = owner,
            SessionId = session,
            ProjectId = project,
            Provider = "claude",
            Model = "opus",
            Source = SpendSources.ChatTurn,
            InputTokens = input,
            OutputTokens = 5,
        };

    // --- A. Pivot: валидация groupBy ---

    [Fact]
    public void Pivot_НеизвестныйGroupBy_BadRequest()
    {
        var store = NewStore("pivot_bad");
        var ctl = NewController(store, "u1", admin: true);

        var result = ctl.Pivot(groupBy: "bogus");

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Theory]
    [InlineData("user")]
    [InlineData("project")]
    [InlineData("chat")]
    [InlineData("task")]
    [InlineData("persona")]
    [InlineData("provider")]
    [InlineData("model")]
    [InlineData("source")]
    public void Pivot_ВсеУровни_Админ_Ok(string groupBy)
    {
        var store = NewStore("pivot_" + groupBy);
        store.Record(Rec(DateTime.UtcNow, "u1"));
        var ctl = NewController(store, "u1", admin: true);

        var result = ctl.Pivot(groupBy: groupBy);

        result.Should().BeOfType<OkObjectResult>();
    }

    // --- B. Гейт → HTTP-статус ---

    [Fact]
    public void Gate_НеАдмин_ScopeAll_Overview_403()
    {
        var store = NewStore("gate_ov");
        var ctl = NewController(store, "u1", admin: false);

        var result = ctl.Overview(scope: "all");

        var or = result.Should().BeOfType<ObjectResult>().Which;
        or.StatusCode.Should().Be(403, "не-админ в scope=all → 403, не 401/500");
    }

    [Fact]
    public void Gate_НеАдмин_ScopeAll_Pivot_403()
    {
        var store = NewStore("gate_piv");
        var ctl = NewController(store, "u1", admin: false);

        var result = ctl.Pivot(groupBy: "model", scope: "all");

        var or = result.Should().BeOfType<ObjectResult>().Which;
        or.StatusCode.Should().Be(403);
    }

    [Fact]
    public void Gate_НеАдмин_ScopeAll_Turns_403()
    {
        var store = NewStore("gate_turns");
        var ctl = NewController(store, "u1", admin: false);

        var result = ctl.Turns(scope: "all");

        var or = result.Should().BeOfType<ObjectResult>().Which;
        or.StatusCode.Should().Be(403);
    }

    [Fact]
    public void Gate_НеАдмин_ЧужойUser_Overview_403()
    {
        var store = NewStore("gate_user");
        var ctl = NewController(store, "u1", admin: false);

        var result = ctl.Overview(user: "other");

        var or = result.Should().BeOfType<ObjectResult>().Which;
        or.StatusCode.Should().Be(403);
    }

    [Fact]
    public void Gate_НеАдмин_GroupByUser_Pivot_403()
    {
        var store = NewStore("gate_gb");
        var ctl = NewController(store, "u1", admin: false);

        var result = ctl.Pivot(groupBy: "user");

        var or = result.Should().BeOfType<ObjectResult>().Which;
        or.StatusCode.Should().Be(403);
    }

    [Fact]
    public void Gate_Админ_ScopeAll_Overview_200()
    {
        var store = NewStore("gate_admin");
        store.Record(Rec(DateTime.UtcNow, "u1"));
        var ctl = NewController(store, "u1", admin: true);

        var result = ctl.Overview(scope: "all");

        result.Should().BeOfType<OkObjectResult>();
    }

    // --- C. Badge: авторизация сессии ---

    [Fact]
    public void Badge_НеизвестнаяСессия_NotFound()
    {
        var store = NewStore("badge_nf");
        var ctl = NewController(store, "u1", admin: false);

        var result = ctl.Badge(sessionId: "nonexistent-session-id");

        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Badge_ЧужаяСессия_НеАдмин_Forbidden()
    {
        var store = NewStore("badge_other");
        var u1 = _userStore.Add("badge-u1", "pw-123456", "user");
        var u2 = _userStore.Add("badge-u2", "pw-123456", "user");
        var dir2 = Directory.CreateDirectory(Path.Combine(_dir, "proj_badge_u2")).FullName;
        var proj2 = _projectManager.Create("BadgeU2", dir2, u2.Id, u2.Username);
        var session2 = await _sessions.CreateAsync(proj2.Id, ClaudeMode.Auto);

        var ctl = NewController(store, u1.Id, admin: false);

        var result = ctl.Badge(sessionId: session2.Id);

        var or = result.Should().BeOfType<ObjectResult>().Which;
        or.StatusCode.Should().Be(403);
    }

    [Fact]
    public async Task Badge_СвояСессия_Ok()
    {
        var store = NewStore("badge_own");
        var u1 = _userStore.Add("badge-own", "pw-123456", "user");
        var dir1 = Directory.CreateDirectory(Path.Combine(_dir, "proj_badge_own")).FullName;
        var proj1 = _projectManager.Create("BadgeOwn", dir1, u1.Id, u1.Username);
        var session1 = await _sessions.CreateAsync(proj1.Id, ClaudeMode.Auto);

        var ctl = NewController(store, u1.Id, admin: false);

        var result = ctl.Badge(sessionId: session1.Id);

        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task Badge_ЧужаяСессия_Админ_Ok()
    {
        var store = NewStore("badge_admin");
        var u1 = _userStore.Add("badge-admin-a", "pw-123456", "user");
        var u2 = _userStore.Add("badge-admin-b", "pw-123456", "user");
        var dir2 = Directory.CreateDirectory(Path.Combine(_dir, "proj_badge_admin")).FullName;
        var proj2 = _projectManager.Create("BadgeAdmin", dir2, u2.Id, u2.Username);
        var session2 = await _sessions.CreateAsync(proj2.Id, ClaudeMode.Auto);

        // Админ (u1) смотрит чужую сессию (u2)
        var ctl = NewController(store, u1.Id, admin: true);

        var result = ctl.Badge(sessionId: session2.Id);

        result.Should().BeOfType<OkObjectResult>();
    }

    // --- D. TaskPrompt: авторизация задачи ---

    [Fact]
    public void TaskPrompt_НеизвестнаяЗадача_NotFound()
    {
        var store = NewStore("tp_nf");
        var ctl = NewController(store, "u1", admin: false);

        var result = ctl.TaskPrompt(taskId: "nonexistent-task-id");

        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public void TaskPrompt_ЧужаяЗадача_НеАдмин_Forbidden()
    {
        var store = NewStore("tp_other");
        var u1 = _userStore.Add("tp-u1", "pw-123456", "user");
        var u2 = _userStore.Add("tp-u2", "pw-123456", "user");
        var task = _tasks.Create(projectId: null, ownerId: u2.Id,
            new CreateTaskRequest("Чужая задача"));

        var ctl = NewController(store, u1.Id, admin: false);

        var result = ctl.TaskPrompt(taskId: task.Id);

        var or = result.Should().BeOfType<ObjectResult>().Which;
        or.StatusCode.Should().Be(403);
    }

    [Fact]
    public void TaskPrompt_СвояЗадача_Ok()
    {
        var store = NewStore("tp_own");
        var u1 = _userStore.Add("tp-own", "pw-123456", "user");
        var task = _tasks.Create(projectId: null, ownerId: u1.Id,
            new CreateTaskRequest("Своя задача"));

        var ctl = NewController(store, u1.Id, admin: false);

        // Пустой runs — это норма: задача запускалась до появления стора
        var result = ctl.TaskPrompt(taskId: task.Id);

        result.Should().BeOfType<OkObjectResult>();
    }

    // --- E. Passport: чужой ход → 404 (не 403!) ---

    [Fact]
    public void Passport_НеизвестныйХод_NotFound()
    {
        var store = NewStore("pass_nf");
        var ctl = NewController(store, "u1", admin: false);

        var result = ctl.Passport(id: "nonexistent-turn-id");

        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public void Passport_ЧужойХод_НеАдмин_NotFound_НеForbidden()
    {
        var store = NewStore("pass_other");
        // Ход принадлежит u2, читаем не-админ u1
        store.Record(Rec(DateTime.UtcNow, "u2"));
        var turnId = store.DetailsBetween(DateOnly.MinValue, DateOnly.MaxValue).First().Id;

        var ctl = NewController(store, "u1", admin: false);

        // Ключевой: Passport возвращает null для чужого хода → 404 (не 403)
        var result = ctl.Passport(id: turnId);

        result.Should().BeOfType<NotFoundResult>(
            "чужой ход не-администратору = 404, не 403: Passport отдаёт null");
    }

    [Fact]
    public void Passport_СвойХод_Ok()
    {
        var store = NewStore("pass_own");
        store.Record(Rec(DateTime.UtcNow, "u1"));
        var turnId = store.DetailsBetween(DateOnly.MinValue, DateOnly.MaxValue).First().Id;

        var ctl = NewController(store, "u1", admin: false);

        var result = ctl.Passport(id: turnId);

        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public void Passport_ЧужойХод_Админ_Ok()
    {
        var store = NewStore("pass_admin");
        store.Record(Rec(DateTime.UtcNow, "u2"));
        var turnId = store.DetailsBetween(DateOnly.MinValue, DateOnly.MaxValue).First().Id;

        // Админ (u1) читает чужой ход (u2)
        var ctl = NewController(store, "u1", admin: true);

        var result = ctl.Passport(id: turnId);

        result.Should().BeOfType<OkObjectResult>();
    }

    // --- F. Клампы Turns: limit/offset ---

    [Fact]
    public void Turns_LimitZero_МинимумОдинЭлемент()
    {
        var store = NewStore("clamp_lz");
        var now = DateTime.UtcNow;
        for (var i = 0; i < 3; i++)
            store.Record(Rec(now, "u1", input: 10 + i));

        var ctl = NewController(store, "u1", admin: false);

        // limit=0 → Math.Clamp(0,1,500)=1 → минимум 1 элемент
        var result = ctl.Turns(limit: 0) as OkObjectResult;
        result.Should().NotBeNull();
        var dto = result!.Value.Should().BeOfType<SpendTurnsPageDto>().Subject;
        dto.Items.Should().HaveCount(1, "limit=0 клампуется в 1, не 0");
    }

    [Fact]
    public void Turns_МногоЗаписей_Limit1000_Максимум500()
    {
        var store = NewStore("clamp_l1000");
        var now = DateTime.UtcNow;
        // 600 записей — больше лимита
        for (var i = 0; i < 600; i++)
            store.Record(Rec(now, "u1", input: (long)(i + 1) * 10));

        var ctl = NewController(store, "u1", admin: false);

        // limit=1000 → Math.Clamp(1000,1,500)=500 → не больше 500 элементов
        var result = ctl.Turns(limit: 1000) as OkObjectResult;
        result.Should().NotBeNull();
        var dto = result!.Value.Should().BeOfType<SpendTurnsPageDto>().Subject;
        dto.Items.Should().HaveCount(500, "limit=1000 клампуется в 500");
    }

    [Fact]
    public void Turns_ОтрицательныйOffset_ТотЖеПервыйЭлемент_ЧтоOffsetZero()
    {
        var store = NewStore("clamp_off");
        var now = DateTime.UtcNow;
        for (var i = 0; i < 10; i++)
            store.Record(Rec(now, "u1", input: (long)(i + 1) * 100));

        var ctl = NewController(store, "u1", admin: false);

        var r0 = (ctl.Turns(offset: 0) as OkObjectResult)!.Value.Should()
            .BeOfType<SpendTurnsPageDto>().Subject;
        var rNeg = (ctl.Turns(offset: -5) as OkObjectResult)!.Value.Should()
            .BeOfType<SpendTurnsPageDto>().Subject;

        r0.Items.First().Should().Be(rNeg.Items.First(),
            "offset=-5 → Math.Max(-5,0)=0 → тот же первый элемент, что offset=0");
    }
}