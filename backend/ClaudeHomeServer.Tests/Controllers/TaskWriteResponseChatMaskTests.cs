using Microsoft.Extensions.DependencyInjection;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ClaudeHomeServer.Controllers;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Access;
using ClaudeHomeServer.Services.ChatContext;
using ClaudeHomeServer.Services.Composition;
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
/// S2d-4i (находка ревью R-S2d-4b, Глеб): write-ответы TasksController отдавали хранимый TaskItem
/// без маски чужих приватных чатов, хотя GET той же задачи её ставил. Задача общего проекта лежит
/// под владельцем-арендатором (TaskManager), а её чат-источник — приватный чат участника, из
/// которого её заводили: PUT/POST от владельца проекта не должны нести ни id, ни названия этого
/// чата (инвариант S2d, разрез 4bc3d04f §1). Автор своего чата и общий чат проекта — как раньше.
///
/// Ответ отдаёт копию (TaskView.CopyOf + MaskForeignChats, ChatAudience.Viewer(UserId)) — та же
/// функция и тот же состав полей, что в GET; запись стора не правится.
///
/// Полный хост не нужен: контроллеры создаются напрямую с ClaimsPrincipal вызывающего и живым
/// SessionManager без процессов (образцы — GetOwnedVisibleTests, SharedProjectEditorScopeGatesTests);
/// сериализация ответа — теми же опциями, что MVC. Ветка мержа здесь не проверяется: она требует
/// живого git-worktree и принятого финала (machinery TasksControllerTests, хост-набор).
///
/// S2d-7b-2 (97ed3bf23) убрал чат-источник из тела человеческого REST: оба Create обнуляют
/// SourceSessionId (TasksController.cs:113 и :356), поэтому задача, созданная через REST, источника
/// не имеет вовсе, и маске в create-ответе нечего маскировать. Create-часть класса проверяет новое
/// правило на уровне ответа: тело игнорируется — ни id, ни формы ни в ответе, ни в записи.
/// Положительная проверка маски (форма рядом с обнулённым id) — на пути Update: источник ставит
/// только сервер (SeedTask).
///
/// Мутации: убрать `SourceSessionId = null` в одном из Create — соответствующий «Создание…» красный;
/// в Update вернуть WithWarning(updated) без MaskForeignChats — «Правка…» краснеет: в JSON владельца
/// снова стоит id чужого приватного чата, а на своих чатах появляется форма-заглушка.
/// </summary>
public sealed class TaskWriteResponseChatMaskTests : IDisposable
{
    // Сидированные чаты общего проекта: чужой приватный (автор — редактор), свой приватный
    // (автор — владелец), общий проектный. Названия отличны от подписи-заглушки — иначе
    // «в JSON нет названия» было бы тривиально зелёным.
    private const string ForeignPrivateId = "priv-foreign";
    private const string OwnPrivateId = "priv-own";
    private const string OpenChatId = "open-chat";
    private const string ForeignPrivateTitle = "Рабочий разговор редактора";
    private const string OwnPrivateTitle = "Мой разговор владельца";
    private const string OpenChatTitle = "Общий разговор проекта";

    private readonly string _tempDir;
    private readonly IConfiguration _config;
    private readonly ProjectManager _projects;
    private readonly TaskManager _tasks;
    private readonly PersonaManager _personas;
    private readonly SessionManager _sessions;
    private readonly Capture _board = new();
    private readonly string _ownerId;
    private readonly string _editorId;
    private readonly string _projectId;

    public TaskWriteResponseChatMaskTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "task_write_mask_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        _config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(_tempDir, "projects.json"),
            [SharedProjectsGate.SwitchKey] = "true",
        }).Build();

        var userStore = new UserStore(_config, new FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        _ownerId = userStore.GetFirst()!.Id;
        _editorId = userStore.Add("write-mask-editor", "pwd", "user").Id;
        userStore.SetFeatureFlag(_editorId, FeatureFlagKeys.SharedProjects, true);

        var appSettings = new AppSettingsService(_config);
        var root = Directory.CreateDirectory(Path.Combine(_tempDir, "project")).FullName;
        _projects = new ProjectManager(_config, userStore, appSettings);
        _projectId = _projects.Create("Общий проект", root, _ownerId, userStore.GetFirst()!.Username).Id;
        _projects.SetMembers(_projectId,
            [new ProjectMember { UserId = _editorId, Role = ProjectRole.Editor, AddedBy = _ownerId }]);

        // Стор чатов сидируется файлом (образец — GetOwnedVisibleTests): маска резолвит их
        // через тот же SessionManager, что и боевой путь.
        File.WriteAllText(Path.Combine(_tempDir, "sessions.json"), JsonSerializer.Serialize(new List<Session>
        {
            new() { Id = ForeignPrivateId, ProjectId = _projectId, PrivateToUserId = _editorId, Name = ForeignPrivateTitle, Status = SessionStatus.Finished },
            new() { Id = OwnPrivateId, ProjectId = _projectId, PrivateToUserId = _ownerId, Name = OwnPrivateTitle, Status = SessionStatus.Finished },
            new() { Id = OpenChatId, ProjectId = _projectId, Name = OpenChatTitle, Status = SessionStatus.Finished },
        }));

        _personas = new PersonaManager(_config);
        _tasks = new TaskManager(_config);
        _sessions = CreateSessionManager(userStore, appSettings, _personas);
    }

    public void Dispose()
    {
        _tasks.Dispose();
        TestFs.DeleteDirectoryResilient(_tempDir);
    }

    // Состав — как в GetOwnedVisibleTests: живой SessionManager без процессов и LLM-вызовов.
    private SessionManager CreateSessionManager(UserStore userStore, AppSettingsService appSettings, PersonaManager personas)
    {
        var llmProviders = new LlmProviderRegistry(_config);
        var subPool = new ClaudeSubscriptionPool(_config);
        var adapters = new LlmSessionAdapterFactory(
            _config, new AgentPromptSourceAdapter(new SkillsService()),
            new WorkspaceDatasetLookup(new WorkspaceKnowledgeStore(_config)), llmProviders, subPool);
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
        var bindings = new PersonaBindingsService(personas, _projects, wkStore, knowledge, new SkillsService(),
            userStore, _config, NullLogger<PersonaBindingsService>.Instance, notes: notesSvc, notesKb: notesKb);
        var sandbox = new ClaudeHomeServer.Services.Execution.SandboxManager(_config,
            NullLogger<ClaudeHomeServer.Services.Execution.SandboxManager>.Instance);
        return SessionCoreTestHost.Build(_tempDir, s =>
        {
            s.AddSingleton<global::ClaudeHomeServer.Services.ProjectManager>(_projects);
            s.AddSingleton<global::ClaudeHomeServer.Services.ChatHistoryService>(new ChatHistoryService(_config));
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
        }).Manager;
    }

    // ─── вызывающий — владелец проекта (его JWT, без заголовка агента: путь UI) ───────────

    private TasksController AsOwner() =>
        new(_tasks, _board, null!, _projects, _personas, null!, null!, _sessions, null!)
        { ControllerContext = AsUser(_ownerId) };

    private ProjectTasksController AsProjectOwner() =>
        new(_tasks, _projects, _personas, _board, null!, null!, _sessions)
        { ControllerContext = AsUser(_ownerId) };

    private static ControllerContext AsUser(string userId) => new()
    {
        HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", userId)])) },
    };

    /// <summary>Задача общего проекта под арендатором с чатом-источником: тот же результат, что
    /// MCP-путь участника (tasks_create под токеном арендатора, SourceSessionId — id его
    /// приватного чата).</summary>
    private string SeedTask(string title, string sourceSessionId) =>
        _tasks.Create(_projectId, _ownerId, new CreateTaskRequest(title, SourceSessionId: sourceSessionId)).Id;

    // Форма ответа как в MVC (camelCase, null не пишется) + relaxed-энкодер: без него кириллица
    // ушла бы в \u-последовательности, и «в JSON нет названия» стало бы тривиально зелёным.
    private static readonly JsonSerializerOptions WireOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static string WireOf(IActionResult result)
    {
        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        return JsonSerializer.Serialize(ok.Value, WireOptions);
    }

    private static JsonObject NodeOf(string wire) => JsonNode.Parse(wire)!.AsObject();

    /// <summary>Отпечаток маски в ответе: ни id, ни названия чужого приватного чата; рядом —
    /// форма-заглушка с признаком и автором. Одна форма во всех write-ответах.</summary>
    private void AssertMasked(string wire, string where)
    {
        wire.Should().NotContain(ForeignPrivateId, $"{where}: id чужого приватного чата не уходит владельцу проекта");
        wire.Should().NotContain(ForeignPrivateTitle, $"{where}: название чужого приватного чата не уходит владельцу проекта");

        var formNode = NodeOf(wire)["sourceChat"];
        formNode.Should().NotBeNull($"{where}: маска ставит форму рядом с обнулённым id");
        var form = formNode!.AsObject();
        form["private"]!.GetValue<bool>().Should().BeTrue($"{where}: рядом форма-заглушка");
        form["title"]!.GetValue<string>().Should().Be(ChatRef.PrivateFormTitle, $"{where}: форма не несёт названия чата");
        form["authorUserId"]!.GetValue<string>().Should().Be(_editorId, $"{where}: форма говорит, чей чат");
    }

    // ─── PUT /api/tasks/{taskId} — тот же вектор, что закрытый в GET ──────────────────────

    [Fact]
    public async Task ПравкаЗадачиУчастникаВладельцем_ВJSONНетНиIdНиНазвания_ЗаписьНеМеняется()
    {
        var taskId = SeedTask("Задача из приватного чата участника", ForeignPrivateId);

        var wire = WireOf(await AsOwner().Update(taskId, new UpdateTaskRequest(Title: "переписал владелец")));

        AssertMasked(wire, "PUT /api/tasks/{id}");
        // Состав ответа не изменился ничем, кроме маски: та же карточка, правка применена.
        var node = NodeOf(wire);
        node["id"]!.GetValue<string>().Should().Be(taskId);
        node["title"]!.GetValue<string>().Should().Be("переписал владелец");

        // Проецируется копия ответа, не запись: хранимая задача осталась со своим источником.
        _tasks.GetById(taskId)!.SourceSessionId.Should().Be(ForeignPrivateId);
    }

    [Theory]
    [InlineData(OwnPrivateId)]
    [InlineData(OpenChatId)]
    public async Task ПравкаЗадачиСвоимИлиОбщимЧатом_ИдНаМесте_ФормыНет(string chatId)
    {
        var taskId = SeedTask("Задача своего или общего чата", chatId);

        var node = NodeOf(WireOf(await AsOwner().Update(taskId, new UpdateTaskRequest(Title: "переписал владелец"))));

        node["sourceSessionId"]!.GetValue<string>().Should().Be(chatId,
            chatId == OpenChatId ? "общий чат проекта виден всем участникам" : "автор своего приватного чата видит его id");
        node["sourceChat"].Should().BeNull("формы-заглушки нет там, где id на месте");
    }

    // ─── POST создания задачи: тело не задаёт источник (S2d-7b-2), маска живёт на PUT ──────

    /// <summary>S2d-7b-2: человеческий Create не принимает id чата из тела (TasksController.cs:113
    /// и :356) — запись создаётся без источника, поэтому в ответе ни id, ни названия, ни формы.
    /// Мутация: убрать `SourceSessionId = null` в соответствующем Create — id или форма появятся,
    /// тест красный.</summary>
    private static JsonObject AssertBodyIgnored(string wire, string where)
    {
        wire.Should().NotContain(ForeignPrivateId, $"{where}: id чужого приватного чата не принимается из тела");
        wire.Should().NotContain(ForeignPrivateTitle, $"{where}: название чужого приватного чата не принимается из тела");
        var node = NodeOf(wire);
        node["sourceSessionId"].Should().BeNull($"{where}: запись создаётся без источника");
        node["sourceChat"].Should().BeNull($"{where}: источника нет — формы нет");
        return node;
    }

    [Fact]
    public async Task СозданиеПроектнойЗадачиСЧужимПриватнымЧатомВТеле_Игнорируется_НиВОтветеНиВЗаписи()
    {
        var wire = WireOf(await AsProjectOwner().Create(_projectId,
            new CreateTaskRequest("задача проекта с чужим источником", SourceSessionId: ForeignPrivateId)));

        var node = AssertBodyIgnored(wire, "POST /api/projects/{id}/tasks");
        _tasks.GetById(node["id"]!.GetValue<string>())!.SourceSessionId
            .Should().BeNull("человеческое тело не ставит источник: запись без него");
    }

    /// <summary>Личная задача (вне проекта): та же единая правило — id из тела не принимается,
    /// ни в ответе, ни в записи. Тело — wire-форма, как её присылает клиент.</summary>
    [Fact]
    public async Task СозданиеЛичнойЗадачиСЧужимПриватнымЧатомВТеле_Игнорируется_НиВОтветеНиВЗаписи()
    {
        var body = JsonSerializer.SerializeToElement(
            new { title = "личная задача с чужим источником", sourceSessionId = ForeignPrivateId });

        var wire = WireOf(await AsOwner().Create(body));

        var node = AssertBodyIgnored(wire, "POST /api/tasks");
        _tasks.GetById(node["id"]!.GetValue<string>())!.SourceSessionId
            .Should().BeNull("человеческое тело не ставит источник: запись без него");
    }

    /// <summary>Единое правило привязанности (S2d-7b-2): человеческое тело не несёт id чата — ни
    /// свой, ни общий. Мутация «обнулять только чужой приватный»: id своего/общего чата появится
    /// в ответе и в записи — этот тест красный.</summary>
    [Theory]
    [InlineData(OwnPrivateId)]
    [InlineData(OpenChatId)]
    public async Task СозданиеЗадачиСвоимИлиОбщимЧатомВТеле_ТожеИгнорируется(string chatId)
    {
        var wire = WireOf(await AsProjectOwner().Create(_projectId,
            new CreateTaskRequest("задача со своим или общим чатом", SourceSessionId: chatId)));

        var node = NodeOf(wire);
        node["sourceSessionId"].Should().BeNull($"{chatId}: id не принимается из тела — правило единое");
        node["sourceChat"].Should().BeNull("источника нет — формы нет");
        _tasks.GetById(node["id"]!.GetValue<string>())!.SourceSessionId.Should().BeNull();
    }

    // Копия группы в событии доски и копия владельца — не этот шаг (S2d-4b закрыл их своим
    // тестом); здесь проверяется, что контроллер не отдал в ответ хранимый объект.
    private sealed class Capture : ISessionBroadcaster
    {
        public List<ServerMessage> ToOwnerMsgs = [];
        public Task ToSession(string sessionId, ServerMessage message) => Task.CompletedTask;
        public Task ToSessionExcept(string sessionId, string exceptConnectionId, ServerMessage message) => Task.CompletedTask;
        public Task ToOwner(string ownerId, ServerMessage message) { ToOwnerMsgs.Add(message); return Task.CompletedTask; }
        public Task ToProject(string projectId, ServerMessage message) => Task.CompletedTask;
        public Task ToPreviewLog(string projectId, string serviceId, ServerMessage message) => Task.CompletedTask;
    }
}
