using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Tasks;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Controllers;

// B4b (разрезы docs/research/ai-fabrika-workflow-cut-2026-11.md §5 «Запуск»/«Подтверждение» и
// docs/research/ai-fabrika-workflow-2-cut-2026-11.md §1.2, §3 Г1): ручки подбора и подтверждения
// маршрута фичи на настоящем хосте.
//
// И7: epic.Workflow пишет только confirm — есть тест владельца и тест не-владельца (личная чужая
// фича и участник общего проекта: право ведения — Write, а не WriteContent, FeatureOwnerGate).
// Подтверждённый маршрут никогда не отдаётся вместе с предложением (Recommend_ПослеConfirm, R-B4b).
// Г1: Stages строит только WorkflowGraph.Resolve — тело с чужими полями графа игнорируется.
// Ключ: чужой/несуществующий/не-эпик — один 404, несъёмный или чужой id пропуска — 400.
// Конкурентность: два confirm — в эпике последний целиком; подбор, пришедший во время
// подтверждения, маршрут не меняет; сбой фонового подбора не роняет создание эпика.
//
// Мутация «убрать проверку владения в confirm» (EpicOfCaller в TaskWorkflowController) краснит
// ровно тесты чужого: Confirm_ЧужойЭпик_404 и Confirm_ЭпикОбщегоПроекта_Участник404_Владелец200.
[Trait("Category", "Host")]
public class TaskWorkflowControllerTests : IClassFixture<TaskWorkflowControllerTests.Host>
{
    private const string FullCycle = "builtin-full-cycle";
    private const string FastTrack = "builtin-fast-track";

    // Хост с управляемым раннером дешёвых one-shot: тест сам ставит ответ модели, её отказ или
    // держит вызов открытым (тест гонки). Дефолный стаб фабрики («[]») ответа не даёт, а сбой
    // модели от него не отличим — для этих тестов он неуправляем.
    //
    // ISessionBroadcaster подменён на тестовый: «ручка не вещает task_changed» — проверяемое
    // свойство Recommend_ПослеConfirm (R-B4b Major); concrete SessionHubBroadcaster (CoreSeamsGlue)
    // в DI остаётся, подменяется только регистрация интерфейса — другие потребители его не теряют.
    public sealed class Host : TestWebApplicationFactory
    {
        internal readonly StubRunner Runner = new();
        internal readonly TestSessionBroadcaster Bc = new();

        public Host()
        {
            ExtraConfig[SharedProjectsGate.SwitchKey] = "true";
            ExtraServices = s =>
            {
                s.AddSingleton<ICheapTextRunner>(Runner);
                s.AddSingleton<ISessionBroadcaster>(Bc);
            };
        }
    }

    // Ответ — текст, отказ — исключение, Hold — вызов висит до Release (для гонки с подтверждением)
    internal sealed class StubRunner : ICheapTextRunner
    {
        public string Text = "";
        public Exception? Failure;
        public bool Hold;
        public TaskCompletionSource<string> Entered = new();
        private TaskCompletionSource<string> _release = new();

        public void Reset()
        {
            Text = "";
            Failure = null;
            Hold = false;
            Entered = new();
            _release = new();
        }

        public bool UsesLocal(string actionKey) => true;
        public bool HasFreeRoute(string actionKey) => true;
        public bool HasRoute(string actionKey) => true;
        public string DescribeRoute(string actionKey, string? fallbackModel) => "local=stub";

        public async Task<string> RunAsync(string actionKey, string prompt, string? fallbackModel = null,
            string? ownerId = null, object? jsonFormat = null, CancellationToken ct = default)
        {
            Entered.TrySetResult("entered");
            if (Failure is not null) throw Failure;
            if (Hold) await _release.Task;
            return Text;
        }

        public void Release(string answer)
        {
            Text = answer;
            _release.TrySetResult("ok");
        }

        public async Task<string?> RunLocalOrChosenAsync(string actionKey, string prompt, string? ownerId = null,
            object? jsonFormat = null, CancellationToken ct = default) =>
            await RunAsync(actionKey, prompt, null, ownerId, jsonFormat, ct);

        public Task<OneShotResult> RunDetailedAsync(string actionKey, string prompt,
            string? fallbackModel = null, string? ownerId = null, TimeSpan? timeout = null,
            int? maxTokens = null, object? jsonFormat = null, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private readonly Host _host;
    private readonly HttpClient _owner;
    private readonly HttpClient _stranger;

    public TaskWorkflowControllerTests(Host host)
    {
        _host = host;
        _owner = host.CreateAuthenticatedClient();
        _stranger = host.CreateAuthenticatedClient(
            TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);
    }

    private TaskManager Tasks => _host.Services.GetRequiredService<TaskManager>();
    private WorkflowTemplateStore Templates => _host.Services.GetRequiredService<WorkflowTemplateStore>();
    private string OwnerId => _host.Services.GetRequiredService<UserStore>()
        .FindByUsername(TestWebApplicationFactory.TestUsername)!.Id;

    /// setup — через TaskManager: фоновый подбор при создании здесь не нужен, он — предмет
    /// отдельных тестов Create_*.
    private TaskItem Epic(string title = "фича") =>
        Tasks.Create(null, OwnerId, new CreateTaskRequest(title, Role: TaskRoles.Feature));

    private static string[] Ids(FeatureWorkflow wf) => wf.Stages.Select(s => s.Id).ToArray();

    /// <summary>Проект владельца; второй пользователь — участник роли role.</summary>
    private async Task<(string ProjectId, string EpicId)> SharedFeatureAsync(string role)
    {
        (await _owner.PutAsJsonAsync("/api/feature-flags/shared-projects", new { enabled = true })).EnsureSuccessStatusCode();
        (await _stranger.PutAsJsonAsync("/api/feature-flags/shared-projects", new { enabled = true })).EnsureSuccessStatusCode();

        var dir = Directory.CreateDirectory(Path.Combine(_host.TempDir, "wf_" + Guid.NewGuid().ToString("N")[..8])).FullName;
        var project = await _owner.PostAsJsonAsync("/api/projects",
            new { name = "Фичи общего проекта " + Guid.NewGuid().ToString("N")[..6], rootPath = dir });
        project.EnsureSuccessStatusCode();
        var projectId = JsonSerializer.Deserialize<JsonElement>(await project.Content.ReadAsStringAsync())
            .GetProperty("id").GetString()!;

        (await _owner.PostAsJsonAsync($"/api/projects/{projectId}/members",
            new { username = TestWebApplicationFactory.SecondUsername, role })).EnsureSuccessStatusCode();

        var epic = await _owner.PostAsJsonAsync($"/api/projects/{projectId}/tasks",
            new { title = "фича общего проекта", role = TaskRoles.Feature });
        epic.EnsureSuccessStatusCode();
        return (projectId, JsonSerializer.Deserialize<JsonElement>(await epic.Content.ReadAsStringAsync())
            .GetProperty("id").GetString()!);
    }

    // ─── подтверждение: владелец, граф, ключ ───────────────────────────────────────────

    [Fact]
    public async Task Confirm_Владелец_200_МаршрутИзResolve_СнятыеОбойдены()
    {
        var epic = Epic();
        var resp = await _owner.PostAsJsonAsync($"/api/tasks/{epic.Id}/workflow/confirm",
            new { templateId = FullCycle, scope = "global", skipped = new[] { "design", "qa" } });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
        body.GetProperty("workflow").GetProperty("confirmedByUser").GetBoolean().Should().BeTrue();

        var wf = Tasks.GetById(epic.Id)!.Workflow!;
        wf.ConfirmedByUser.Should().BeTrue();
        wf.TemplateId.Should().Be(FullCycle);
        // Область — та, где шаблон лежит в сторе (built-in — глобальный), а не та, что прислал клиент
        wf.Scope.Should().Be("global");
        wf.Skipped.Should().Equal("design", "qa");
        wf.ConfirmedVersion.Should().Be(Templates.Get(FullCycle, WorkflowTemplateScope.Global)!.Version);
        wf.ConfirmedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(5));

        // Граф — Resolve: снятые optional обойдены рёбрами, порядок списка — топологический
        Ids(wf).Should().Equal("cut", "plan", "step", "review", "final", "merge");
        wf.Stages.Select(s => s.Role).Should().Equal(
            TaskRoles.Cut, TaskRoles.Plan, TaskRoles.Step, TaskRoles.Review, TaskRoles.Final, TaskRoles.Merge);
        wf.Stages[1].After.Should().Equal("cut");
        wf.Stages[4].After.Should().Equal("review");
        // Совпадает с единственной точкой правды — WorkflowGraph.Resolve того же шаблона
        wf.Stages.Select(Shape).Should().Equal(
            WorkflowGraph.Resolve(Templates.Get(FullCycle, WorkflowTemplateScope.Global)!, new[] { "design", "qa" })
                .Select(Shape));
    }

    private static (string Id, string Role, string? Lane, string After) Shape(RouteStage s) =>
        (s.Id, s.Role, s.Lane, string.Join(",", s.After));

    [Fact]
    public async Task Confirm_ТелоСЧужимиПолямиГрафа_Игнорируется_StagesЭтоResolve()
    {
        var epic = Epic();
        var resp = await _owner.PostAsJsonAsync($"/api/tasks/{epic.Id}/workflow/confirm", new
        {
            templateId = FullCycle,
            scope = "global",
            skipped = new[] { "design" },
            // Г1: от клиента граф не принимается. Это тело было бы несвязным графом (cut после
            // merge, корень не один) — сервер обязан его не читать, а построить свой.
            stages = new
            {
                graph = new[]
                {
                    new { id = "cut", role = "cut", after = new[] { "merge" } },
                    new { id = "merge", role = "merge", after = Array.Empty<string>() },
                },
            },
        });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var wf = Tasks.GetById(epic.Id)!.Workflow!;
        Ids(wf).Should().Equal("cut", "plan", "step", "review", "qa", "final", "merge");
        wf.Stages[0].After.Should().BeEmpty("«after: merge» из тела не доезжает: After берётся из шаблона");
        wf.Skipped.Should().Equal("design");
    }

    [Fact]
    public async Task Confirm_ЧужойЭпик_404_И_НичегоНеЗаписано()
    {
        var epic = Epic();
        var resp = await _stranger.PostAsJsonAsync($"/api/tasks/{epic.Id}/workflow/confirm",
            new { templateId = FastTrack, skipped = Array.Empty<string>() });
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound, "чужая фича — «нет», её существование не раскрываем");
        Tasks.GetById(epic.Id)!.Workflow.Should().BeNull();
    }

    [Fact]
    public async Task Confirm_ЭпикОбщегоПроекта_Участник404_Владелец200()
    {
        var (_, epicId) = await SharedFeatureAsync("editor");
        var body = new { templateId = FastTrack, skipped = new[] { "plan" } };

        // Участник (редактор) имеет WriteContent, но не Write: ведение фичи — только владелец
        // (FeatureOwnerGate.cs:95-101, замок ProjectAccessService.cs:29). Для него фичи нет.
        var denied = await _stranger.PostAsJsonAsync($"/api/tasks/{epicId}/workflow/confirm", body);
        denied.StatusCode.Should().Be(HttpStatusCode.NotFound);
        Tasks.GetById(epicId)!.Workflow.Should().BeNull();

        var ok = await _owner.PostAsJsonAsync($"/api/tasks/{epicId}/workflow/confirm", body);
        ok.StatusCode.Should().Be(HttpStatusCode.OK);
        Ids(Tasks.GetById(epicId)!.Workflow!).Should().Equal("step", "review", "final", "merge");
    }

    [Fact]
    public async Task Confirm_НеЭпикИНетТакогоId_404()
    {
        var task = Tasks.Create(null, OwnerId, new CreateTaskRequest("обычная задача"));
        var notFeature = await _owner.PostAsJsonAsync($"/api/tasks/{task.Id}/workflow/confirm",
            new { templateId = FullCycle, skipped = Array.Empty<string>() });
        notFeature.StatusCode.Should().Be(HttpStatusCode.NotFound, "не-эпик и несуществующий id — один отказ");

        var missing = await _owner.PostAsJsonAsync($"/api/tasks/{Guid.NewGuid():N}/workflow/confirm",
            new { templateId = FullCycle, skipped = Array.Empty<string>() });
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Confirm_НесъёмныйИлиЧужойIdПропуска_ИНетШаблона_400()
    {
        var epic = Epic();
        // step — Presence=on: снять нельзя
        var onStage = await _owner.PostAsJsonAsync($"/api/tasks/{epic.Id}/workflow/confirm",
            new { templateId = FullCycle, skipped = new[] { "step" } });
        onStage.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // id, которого в шаблоне нет: опечатка не проигнорируется молча
        var unknown = await _owner.PostAsJsonAsync($"/api/tasks/{epic.Id}/workflow/confirm",
            new { templateId = FullCycle, skipped = new[] { "design", "no-such-stage" } });
        unknown.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // шаблона нет среди доступных этому проекту
        var noTemplate = await _owner.PostAsJsonAsync($"/api/tasks/{epic.Id}/workflow/confirm",
            new { templateId = "no-such-template", skipped = Array.Empty<string>() });
        noTemplate.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        Tasks.GetById(epic.Id)!.Workflow.Should().BeNull("отказ — до любой записи маршрута");
    }

    // ─── конкурентность ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Confirm_ДваПодряд_Оба200_ВЭпикеПоследнийЦеликом()
    {
        var epic = Epic();
        var first = await _owner.PostAsJsonAsync($"/api/tasks/{epic.Id}/workflow/confirm",
            new { templateId = FullCycle, skipped = new[] { "design" } });
        first.StatusCode.Should().Be(HttpStatusCode.OK);

        var second = await _owner.PostAsJsonAsync($"/api/tasks/{epic.Id}/workflow/confirm",
            new { templateId = FastTrack, skipped = new[] { "plan" } });
        second.StatusCode.Should().Be(HttpStatusCode.OK);

        // Второй маршрут — целый: не смесь полей двух подтверждений
        var wf = Tasks.GetById(epic.Id)!.Workflow!;
        wf.TemplateId.Should().Be(FastTrack);
        wf.Skipped.Should().Equal("plan");
        Ids(wf).Should().Equal("step", "review", "final", "merge");
        wf.Stages[0].After.Should().BeEmpty();
    }

    [Fact]
    public async Task Confirm_ПараллельноСRecommend_МаршрутИзТела()
    {
        var epic = Epic();
        _host.Runner.Reset();
        _host.Runner.Hold = true;

        // Подбор висит на вызове модели; подтверждение идёт параллельно
        var recommendTask = _owner.PostAsync($"/api/tasks/{epic.Id}/workflow/recommend", null);
        var entered = await Task.WhenAny(_host.Runner.Entered.Task, Task.Delay(5000));
        entered.Should().Be(_host.Runner.Entered.Task, "вызов модели не начался");

        var confirm = await _owner.PostAsJsonAsync($"/api/tasks/{epic.Id}/workflow/confirm",
            new { templateId = FastTrack, skipped = new[] { "plan" } });
        // Ответ модели приходит во время подтверждения — он про другое предложение
        _host.Runner.Release("""
            {"templateId":"builtin-full-cycle","skipped":["design"],"rationale":"полный цикл"}
            """);
        var recommend = await recommendTask;

        confirm.StatusCode.Should().Be(HttpStatusCode.OK);
        recommend.StatusCode.Should().Be(HttpStatusCode.OK);

        var wf = Tasks.GetById(epic.Id)!.Workflow!;
        wf.TemplateId.Should().Be(FastTrack, "решает тело подтверждения: предложение не меняет маршрут");
        Ids(wf).Should().Equal("step", "review", "final", "merge");
        wf.Skipped.Should().Equal("plan");
        // Предложение, пришедшее после решения человека, маршрута не тронуло: он либо не записан
        // (B4a не пишет поверх подтверждённого), либо остался предложением, а не маршрутом
        var current = Tasks.GetById(epic.Id)!;
        current.Workflow!.TemplateId.Should().Be(FastTrack);
        if (current.WorkflowProposal is not null)
            current.WorkflowProposal.TemplateId.Should().NotBe(current.Workflow!.TemplateId);
    }

    // ─── подбор ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Recommend_Владелец_200_ПредложениеЗаписаноВЭпик()
    {
        var epic = Epic();
        _host.Runner.Reset();
        _host.Runner.Text = """
            {"templateId":"builtin-fast-track","skipped":["plan"],"rationale":"малая правка"}
            """;

        var resp = await _owner.PostAsync($"/api/tasks/{epic.Id}/workflow/recommend", null);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
        body.GetProperty("proposal").GetProperty("templateId").GetString().Should().Be(FastTrack);
        body.GetProperty("proposal").GetProperty("skipped")[0].GetString().Should().Be("plan");

        var proposal = Tasks.GetById(epic.Id)!.WorkflowProposal!;
        proposal.TemplateId.Should().Be(FastTrack);
        proposal.Scope.Should().Be("global");
        proposal.Skipped.Should().Equal("plan");
        // И7: предложением маршрут не становится
        Tasks.GetById(epic.Id)!.Workflow.Should().BeNull();
    }

    [Fact]
    public async Task Recommend_СбойМодели_200БезПредложения_МаршрутаНет()
    {
        var epic = Epic();
        _host.Runner.Reset();
        _host.Runner.Failure = new InvalidOperationException("model down");

        var resp = await _owner.PostAsync($"/api/tasks/{epic.Id}/workflow/recommend", null);
        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            "подбор не удался — обычное рабочее состояние, маршрут выбирает человек");
        var body = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
        body.GetProperty("proposal").GetProperty("state").GetString().Should().Be("none");
        body.GetProperty("proposal").GetProperty("reason").GetString().Should().Be("no_answer");
        Tasks.GetById(epic.Id)!.WorkflowProposal.Should().BeNull();
        Tasks.GetById(epic.Id)!.Workflow.Should().BeNull();
    }

    // ─── B03: контракт предложения и подтверждения ──────────────────────────────────────

    [Fact]
    public async Task Recommend_МусорныйОтвет_StateNone_ReasonInvalid()
    {
        var epic = Epic();
        _host.Runner.Reset();
        _host.Runner.Text = "{\"templateId\":\"нет-такого\"}";

        var body = await PostJsonAsync($"/api/tasks/{epic.Id}/workflow/recommend");
        body.GetProperty("proposal").GetProperty("state").GetString().Should().Be("none");
        body.GetProperty("proposal").GetProperty("reason").GetString().Should().Be("invalid");
    }

    [Fact]
    public async Task Recommend_Предложение_StateProposed_СNeedsHumanDoubtReasonProposedAt()
    {
        var epic = Epic();
        _host.Runner.Reset();
        _host.Runner.Text = """
            {"templateId":"builtin-full-cycle","rationale":"выбор неочевиден","needsHuman":true,"doubtReason":"ambiguous"}
            """;

        var p = (await PostJsonAsync($"/api/tasks/{epic.Id}/workflow/recommend")).GetProperty("proposal");
        p.GetProperty("state").GetString().Should().Be("proposed");
        p.GetProperty("needsHuman").GetBoolean().Should().BeTrue();
        p.GetProperty("doubtReason").GetString().Should().Be("ambiguous");
        p.GetProperty("rationale").GetString().Should().Be("выбор неочевиден");
        p.GetProperty("proposedAt").GetDateTime().Should().BeAfter(DateTime.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public async Task ПослеConfirm_ConfirmedНесётRationaleПредложения_ПредложениеВОтветеNull()
    {
        var epic = Epic();
        _host.Runner.Reset();
        _host.Runner.Text = """
            {"templateId":"builtin-fast-track","rationale":"малая правка","needsHuman":true,"doubtReason":"low-confidence"}
            """;
        await PostJsonAsync($"/api/tasks/{epic.Id}/workflow/recommend");

        var confirm = await _owner.PostAsJsonAsync($"/api/tasks/{epic.Id}/workflow/confirm",
            new { templateId = FullCycle, skipped = Array.Empty<string>() });
        confirm.StatusCode.Should().Be(HttpStatusCode.OK);

        // Предложение сервер не стирает; в GET оно доезжает как confirmed.* (только чтение)
        var get = JsonSerializer.Deserialize<JsonElement>(
            await (await _owner.GetAsync($"/api/tasks/{epic.Id}/workflow")).Content.ReadAsStringAsync());
        get.GetProperty("proposal").ValueKind.Should().Be(JsonValueKind.Null);
        var c = get.GetProperty("confirmed");
        c.GetProperty("rationale").GetString().Should().Be("малая правка");
        c.GetProperty("proposedTemplateId").GetString().Should().Be(FastTrack);
        c.GetProperty("templateId").GetString().Should().Be(FullCycle);
        c.GetProperty("needsHuman").GetBoolean().Should().BeTrue();
        c.GetProperty("doubtReason").GetString().Should().Be("low-confidence");
        Tasks.GetById(epic.Id)!.WorkflowProposal.Should().NotBeNull();
    }

    /// Клон встроенного fast-track под уникальным id: поднимать версию встроенного нельзя —
    /// хост общий для всех тестов класса.
    private string OwnTemplate()
    {
        var t = Templates.Get(FastTrack, WorkflowTemplateScope.Global)!;
        t.Id = "b03-" + Guid.NewGuid().ToString("N")[..8];
        t.BuiltInKey = null;
        t.Name = "b03";
        return Templates.Save(t, WorkflowTemplateScope.Global).Id;
    }

    private void BumpTemplate(string id)
    {
        var t = Templates.Get(id, WorkflowTemplateScope.Global)!;
        Templates.Save(t, WorkflowTemplateScope.Global);
    }

    private async Task<JsonElement> PostJsonAsync(string url, object? body = null)
    {
        var resp = body is null ? await _owner.PostAsync(url, null) : await _owner.PostAsJsonAsync(url, body);
        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        return JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
    }

    private async Task<JsonElement> GetWorkflowAsync(string epicId) =>
        JsonSerializer.Deserialize<JsonElement>(
            await (await _owner.GetAsync($"/api/tasks/{epicId}/workflow")).Content.ReadAsStringAsync());

    [Fact]
    public async Task KeepVersion_СкрываетПредупреждениеДоСледующегоРоста()
    {
        var tpl = OwnTemplate();
        var epic = Epic();
        await PostJsonAsync($"/api/tasks/{epic.Id}/workflow/confirm", new { templateId = tpl });
        (await GetWorkflowAsync(epic.Id)).GetProperty("templateUpdate").ValueKind.Should().Be(JsonValueKind.Null);

        BumpTemplate(tpl); // v2
        var upd = (await GetWorkflowAsync(epic.Id)).GetProperty("templateUpdate");
        upd.GetProperty("fromVersion").GetInt32().Should().Be(1);
        upd.GetProperty("toVersion").GetInt32().Should().Be(2);
        (await GetWorkflowAsync(epic.Id)).GetProperty("updateAvailable").GetBoolean().Should().BeTrue();

        var kept = await PostJsonAsync($"/api/tasks/{epic.Id}/workflow/keep-version", new { templateVersion = 2 });
        kept.GetProperty("confirmed").GetProperty("dismissedUpdateVersion").GetInt32().Should().Be(2);
        kept.GetProperty("templateUpdate").ValueKind.Should().Be(JsonValueKind.Null);
        kept.GetProperty("updateAvailable").GetBoolean().Should().BeFalse();
        Tasks.GetById(epic.Id)!.Workflow!.ConfirmedVersion.Should().Be(1, "маршрут «Оставить v1» не меняет");
        (await GetWorkflowAsync(epic.Id)).GetProperty("templateUpdate").ValueKind.Should().Be(JsonValueKind.Null);

        BumpTemplate(tpl); // v3 — шаблон вырос дальше отклонённой версии
        (await GetWorkflowAsync(epic.Id)).GetProperty("templateUpdate").GetProperty("toVersion").GetInt32().Should().Be(3);
        (await GetWorkflowAsync(epic.Id)).GetProperty("updateAvailable").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task KeepVersion_НевернаяВерсияИНеПодтверждённыйИЧужой()
    {
        var tpl = OwnTemplate();
        var epic = Epic();
        var url = $"/api/tasks/{epic.Id}/workflow/keep-version";

        (await _owner.PostAsJsonAsync(url, new { templateVersion = 2 })).StatusCode
            .Should().Be(HttpStatusCode.Conflict, "маршрут не подтверждён");

        await PostJsonAsync($"/api/tasks/{epic.Id}/workflow/confirm", new { templateId = tpl });
        (await _owner.PostAsJsonAsync(url, new { templateVersion = 1 })).StatusCode
            .Should().Be(HttpStatusCode.BadRequest, "не новее подтверждённой");
        (await _owner.PostAsJsonAsync(url, new { templateVersion = 5 })).StatusCode
            .Should().Be(HttpStatusCode.BadRequest, "новее текущей версии шаблона");
        (await _stranger.PostAsJsonAsync(url, new { templateVersion = 2 })).StatusCode
            .Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Confirm_ОбновлениеВерсии_БезSkippedБерётПрежние_ЯвныйПустойСнимает()
    {
        var tpl = OwnTemplate();
        var epic = Epic();
        await PostJsonAsync($"/api/tasks/{epic.Id}/workflow/confirm", new { templateId = tpl, skipped = new[] { "plan" } });
        BumpTemplate(tpl);

        await PostJsonAsync($"/api/tasks/{epic.Id}/workflow/confirm", new { templateId = tpl });
        var wf = Tasks.GetById(epic.Id)!.Workflow!;
        wf.ConfirmedVersion.Should().Be(2);
        wf.Skipped.Should().Equal("plan");
        wf.Stages.Select(s => s.Id).Should().NotContain("plan");

        await PostJsonAsync($"/api/tasks/{epic.Id}/workflow/confirm", new { templateId = tpl, skipped = Array.Empty<string>() });
        Tasks.GetById(epic.Id)!.Workflow!.Skipped.Should().BeEmpty("явный [] — решение человека");
    }

    [Fact]
    public async Task Recommend_ЧужойЭпикИНеЭпик_404()
    {
        var epic = Epic();
        var stranger = await _stranger.PostAsync($"/api/tasks/{epic.Id}/workflow/recommend", null);
        stranger.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var task = Tasks.Create(null, OwnerId, new CreateTaskRequest("обычная задача"));
        var notFeature = await _owner.PostAsync($"/api/tasks/{task.Id}/workflow/recommend", null);
        notFeature.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>R-B4b Major: подтверждённый маршрут никогда не сопровождается «живым» предложением.
    /// ConfirmWorkflow предложения не очищает (TaskManager.cs:726) — устаревшее предложение остаётся
    /// в сторе, но в ответ не доезжает и о нём не вещается. Мутация «отдать epic.WorkflowProposal
    /// без проверки Workflow» краснит этот тест.</summary>
    [Fact]
    public async Task Recommend_ПослеConfirm_200БезПредложения_БезTaskChanged()
    {
        var epic = Epic();
        _host.Runner.Reset();
        _host.Runner.Text = """
            {"templateId":"builtin-fast-track","skipped":["plan"],"rationale":"малая правка"}
            """;

        // 1. подбор — предложение есть и доезжает до ответа
        var first = await _owner.PostAsync($"/api/tasks/{epic.Id}/workflow/recommend", null);
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        var firstBody = JsonSerializer.Deserialize<JsonElement>(await first.Content.ReadAsStringAsync());
        firstBody.GetProperty("proposal").ValueKind.Should().Be(JsonValueKind.Object);
        Tasks.GetById(epic.Id)!.WorkflowProposal.Should().NotBeNull();

        // 2. confirm — маршрут подтверждён; предложение ConfirmWorkflow не трогает: оно остаётся
        // в сторе именно как устаревшее
        var confirm = await _owner.PostAsJsonAsync($"/api/tasks/{epic.Id}/workflow/confirm",
            new { templateId = FullCycle, skipped = Array.Empty<string>() });
        confirm.StatusCode.Should().Be(HttpStatusCode.OK);
        var stored = Tasks.GetById(epic.Id)!;
        stored.Workflow.Should().NotBeNull();
        stored.WorkflowProposal.Should().NotBeNull("ConfirmWorkflow предложения не очищает");

        // 3. повторный подбор на подтверждённом эпике: RecommendAsync молчит (Workflow is not null,
        // WorkflowRecommender.cs:47), ответ — 200 с proposal = null и без task_changed
        _host.Bc.Clear();
        var second = await _owner.PostAsync($"/api/tasks/{epic.Id}/workflow/recommend", null);
        second.StatusCode.Should().Be(HttpStatusCode.OK);
        var secondBody = JsonSerializer.Deserialize<JsonElement>(await second.Content.ReadAsStringAsync());
        secondBody.GetProperty("proposal").ValueKind.Should().Be(JsonValueKind.Null);
        _host.Bc.Owner.Should().NotContain(e => e.Message is TaskChangedMessage,
            "устаревшего предложения в ответе нет — о нём нечего вещать");

        // Устаревшее предложение осталось в сторе (доработка его не чистит) — наружу не доезжает
        Tasks.GetById(epic.Id)!.WorkflowProposal.Should().NotBeNull();
    }

    // ─── фоновый подбор при создании эпика ─────────────────────────────────────────────

    [Fact]
    public async Task Create_Эпик_ПодборФоном_ПишетПредложение()
    {
        _host.Runner.Reset();
        _host.Runner.Text = """
            {"templateId":"builtin-fast-track","skipped":["plan"],"rationale":"малая правка"}
            """;

        var created = await _owner.PostAsJsonAsync("/api/tasks", new { title = "фича", role = TaskRoles.Feature });
        created.StatusCode.Should().Be(HttpStatusCode.OK);
        var epicId = JsonSerializer.Deserialize<JsonElement>(await created.Content.ReadAsStringAsync())
            .GetProperty("id").GetString()!;

        var epic = await WaitProposalAsync(epicId);
        epic.Should().NotBeNull("создание эпика запускает подбор фоном (разрез §5 «Запуск»)");
        epic!.WorkflowProposal!.TemplateId.Should().Be(FastTrack);
        epic.Workflow.Should().BeNull();
    }

    [Fact]
    public async Task Create_Эпик_СбойПодбора_СозданиеЖивоеИМаршрутНеТронут()
    {
        _host.Runner.Reset();
        _host.Runner.Failure = new InvalidOperationException("model down");

        var created = await _owner.PostAsJsonAsync("/api/tasks", new { title = "фича", role = TaskRoles.Feature });
        created.StatusCode.Should().Be(HttpStatusCode.OK,
            "best-effort: сбой подбора не роняет создание (в карточке приёмки — 201; фактический код этой ручки — 200)");
        var epicId = JsonSerializer.Deserialize<JsonElement>(await created.Content.ReadAsStringAsync())
            .GetProperty("id").GetString()!;

        (await Task.WhenAny(_host.Runner.Entered.Task, Task.Delay(5000))).Should().Be(_host.Runner.Entered.Task,
            "подбор при создании эпика не запущен");
        // Необработанного исключения в фоне нет: хост жив, эпик на месте, предложения нет
        var after = await _owner.GetAsync($"/api/tasks/{epicId}");
        after.StatusCode.Should().Be(HttpStatusCode.OK);
        Tasks.GetById(epicId)!.WorkflowProposal.Should().BeNull();
        Tasks.GetById(epicId)!.Workflow.Should().BeNull();
    }

    /// <summary>
    /// Фоновый подбор — вне ответа создания: наблюдаемый результат только в сторе. Ждём именно
    /// события (появления предложения) с верхним пределом 2 с, а не «поспим на всякий случай».
    /// </summary>
    private async Task<TaskItem?> WaitProposalAsync(string epicId)
    {
        for (var i = 0; i < 40; i++)
        {
            var epic = Tasks.GetById(epicId);
            if (epic?.WorkflowProposal is not null) return epic;
            await Task.Delay(50);
        }
        return null;
    }
}
