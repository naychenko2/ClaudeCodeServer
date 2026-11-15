using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Tasks;
using ClaudeHomeServer.Services.Tasks.Journal;
using FluentAssertions;
using ClaudeHomeServer.Services.Access;
using Microsoft.Extensions.Configuration;
using Moq;
using Xunit;

namespace ClaudeHomeServer.Tasks.Tests;

// B07: сводка шапки этапа — исполнитель, KPI, задачи ветки, соседняя ветка, чат исполнителя, легаси.
public class StageSummaryTests : IDisposable
{
    private const string Owner = "owner", Stranger = "stranger";

    private sealed class FakePersonas(params Persona[] all) : IPersonaLookup
    {
        public Persona? GetByIdInternal(string id) => all.FirstOrDefault(p => p.Id == id);
        public IReadOnlyCollection<Persona> GetAllInternal() => all;
    }

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "stagesum_" + Guid.NewGuid().ToString("N"));
    private readonly TaskManager _tasks;
    private readonly Persona _tim = new() { Name = "Тимур", Role = "Backend", Model = "opus-x" };
    private readonly Persona _kira = new() { Name = "Кира", Role = "Frontend" };
    private readonly StageScreenService _sut;

    public StageSummaryTests()
    {
        Directory.CreateDirectory(_dir);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["DataPath"] = Path.Combine(_dir, "projects.json") })
            .Build();
        _tasks = new TaskManager(config);
        _sut = new StageScreenService(_tasks, personas: new FakePersonas(_tim, _kira));
    }

    public void Dispose()
    {
        _tasks.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* уборка best-effort */ }
    }

    private TaskItem Epic(bool routed)
    {
        var epic = _tasks.Create(null, Owner, new CreateTaskRequest("фича", Role: TaskRoles.Feature));
        if (routed)
            epic.Workflow = new FeatureWorkflow
            {
                TemplateId = "t", ConfirmedVersion = 3,
                Stages =
                [
                    new RouteStage { Id = "plan", Role = TaskRoles.Plan, After = [] },
                    new RouteStage { Id = "back", Role = TaskRoles.Step, Title = "Бэк", After = ["plan"] },
                    new RouteStage { Id = "front", Role = TaskRoles.Step, Title = "Фронт", After = ["plan"] },
                    new RouteStage { Id = "merge", Role = TaskRoles.Merge, After = ["back", "front"] },
                ]
            };
        return epic;
    }

    private TaskItem Step(TaskItem epic, string stepRef, string stageId, Persona p, string role = TaskRoles.Step, List<string>? reviewOf = null)
    {
        // маршрут снимаем на время Create: он потребовал бы stageId, а слепок мы ставим сами
        var wf = epic.Workflow;
        epic.Workflow = null;
        var t = _tasks.Create(null, Owner, new CreateTaskRequest("шаг " + stepRef, FeatureId: epic.Id, Role: role, StepRef: stepRef, ReviewOf: reviewOf));
        epic.Workflow = wf;
        t.PersonaId = p.Id;
        t.Stage = new StageSnapshot { TemplateId = "t", TemplateVersion = 3, StageId = stageId, Role = role,
            StallMinutes = 20, StallPolicy = WorkflowStallPolicies.StopAlert, TierMedium = "sonnet-cell" };
        return t;
    }

    private TaskItem Review(TaskItem target, string verdict)
    {
        var epic = _tasks.GetById(target.FeatureId!)!;
        var wf = epic.Workflow;
        epic.Workflow = null;
        var r = _tasks.Create(null, Owner, new CreateTaskRequest("ревью", FeatureId: target.FeatureId, Role: TaskRoles.Review, ReviewOf: [target.Id]));
        epic.Workflow = wf;
        r.Verification = new TaskVerification { Verdict = verdict, VerifiedAt = DateTime.UtcNow };
        return r;
    }

    private sealed class CountingTasks(IConfiguration config) : TaskManager(config)
    {
        public int GetAllCalls;
        public override IReadOnlyList<TaskItem> GetAll() { GetAllCalls++; return base.GetAll(); }
    }

    // Число обходов стора на экран не зависит от числа задач этапа: список снимается один раз,
    // а не на каждую задачу (BuildMetrics/ReviewsOf) и каждую соседнюю ветку (BuildParallelNote)
    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    public void Сводка_этапа_снимает_список_задач_один_раз_а_не_на_каждую_задачу(int steps)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["DataPath"] = Path.Combine(_dir, "count.json") })
            .Build();
        using var counting = new CountingTasks(config);
        var epic = counting.Create(null, Owner, new CreateTaskRequest("фича", Role: TaskRoles.Feature));
        epic.Workflow = new FeatureWorkflow
        {
            TemplateId = "t", ConfirmedVersion = 3,
            Stages =
            [
                new RouteStage { Id = "plan", Role = TaskRoles.Plan, After = [] },
                new RouteStage { Id = "back", Role = TaskRoles.Step, After = ["plan"] },
                new RouteStage { Id = "front", Role = TaskRoles.Step, After = ["plan"] },
            ]
        };
        TaskItem? first = null;
        for (var i = 0; i < steps + 2; i++)
        {
            var stage = i < steps ? "back" : "front";
            var wf = epic.Workflow; epic.Workflow = null;
            var t = counting.Create(null, Owner, new CreateTaskRequest("шаг", FeatureId: epic.Id, Role: TaskRoles.Step, StepRef: "S" + i));
            t.Stage = new StageSnapshot { TemplateId = "t", TemplateVersion = 3, StageId = stage, Role = TaskRoles.Step };
            first ??= t;
            var rv = counting.Create(null, Owner, new CreateTaskRequest("ревью", FeatureId: epic.Id, Role: TaskRoles.Review, ReviewOf: [t.Id]));
            epic.Workflow = wf;
            rv.Verification = new TaskVerification { Verdict = TaskVerdicts.Accepted, VerifiedAt = DateTime.UtcNow };
        }
        var sut = new StageScreenService(counting, personas: new FakePersonas(_tim, _kira));
        counting.GetAllCalls = 0;

        sut.Build(first!.Id, Owner).Value.Should().NotBeNull();

        // константа (6) не растёт с числом шагов: Chain, снимок этапа, метрики и ревью экрана, коммиты
        counting.GetAllCalls.Should().Be(6);
    }

    [Fact]
    public void Этап_с_двумя_задачами_возвратом_и_соседней_веткой()
    {
        var epic = Epic(routed: true);
        var a = Step(epic, "B1", "back", _tim);
        var b = Step(epic, "B2", "back", _tim);
        var front = Step(epic, "F1", "front", _kira);
        a.ClaudeStartedAt = DateTime.UtcNow.AddMinutes(-30);
        a.Status = TaskItemStatus.Done; a.CompletedAt = DateTime.UtcNow.AddMinutes(-10);
        a.LinkedSessionId = "sess-1";
        var returned = Review(a, TaskVerdicts.Returned);
        _ = Step(epic, "B1", "back", _tim, TaskRoles.Rework, [returned.Id]);
        Review(a, TaskVerdicts.Accepted);
        front.Status = TaskItemStatus.Done;

        var s = _sut.Build(a.Id, Owner).Value!.StageSummary;

        s.Executor.Should().NotBeNull();
        s.Executor!.Name.Should().Be("Тимур");
        s.Executor.RoleTitle.Should().Be("Backend");
        s.Executor.ModelLabel.Should().Be("sonnet-cell");
        s.Executor.ModelIsOwnCell.Should().BeTrue();
        s.Executor.SilenceMinutes.Should().Be(20);
        s.Executor.SilenceAction.Should().Be("stop");
        s.TemplateVersion.Should().Be(3);
        s.ExecutorSessionId.Should().Be("sess-1");

        // два шага этапа (B1 вместе с доработкой, B2); возврат один
        s.Tasks.Should().HaveCount(2);
        var row = s.Tasks.Single(t => t.StepRef == "B1");
        row.Returns.Should().Be(1);
        row.Verdict.Should().Be("accepted");
        row.Executor!.Name.Should().Be("Тимур");
        row.Minutes.Should().BeGreaterThanOrEqualTo(0);
        s.Tasks.Single(t => t.StepRef == "B2").Verdict.Should().BeNull();
        s.Returns.Should().Be(1);
        s.Kpi.TotalMinutes.Should().Be(s.Kpi.ExecMinutes + s.Kpi.ReviewMinutes + s.Kpi.WaitedHumanMinutes);

        s.ParallelNote.Should().NotBeNull();
        s.ParallelNote!.StageId.Should().Be("front");
        s.ParallelNote.Title.Should().Be("Фронт");
        s.ParallelNote.DoneCount.Should().Be(1);
        s.ParallelNote.TotalCount.Should().Be(1);
        s.ParallelNote.PersonaName.Should().Be("Кира");
        _ = b;
    }

    // Читатель видит задачу (доступ к проекту), но не чат исполнителя: sessionId не отдаётся
    [Fact]
    public void Общее_ревью_двух_задач_этапа_входит_в_KPI_один_раз()
    {
        var epic = Epic(routed: true);
        var a = Step(epic, "B1", "back", _tim);
        var b = Step(epic, "B2", "back", _tim);
        var wf = epic.Workflow;
        epic.Workflow = null;
        var r = _tasks.Create(null, Owner, new CreateTaskRequest("общее ревью", FeatureId: epic.Id, Role: TaskRoles.Review, ReviewOf: [a.Id, b.Id]));
        epic.Workflow = wf;
        var t0 = a.CreatedAt; // окно этапа начинается с CreatedAt задачи, журнал отсчитываем от него
        a.ClaudeStartedAt = t0;
        a.Status = TaskItemStatus.Done; a.CompletedAt = t0.AddMinutes(20);
        b.Status = TaskItemStatus.Done; b.CompletedAt = t0.AddMinutes(20); // окно b тоже накрывает ревью
        r.ClaudeStartedAt = t0.AddMinutes(100);
        r.Status = TaskItemStatus.Done; r.CompletedAt = t0.AddMinutes(160);
        var journal = new FeatureJournalStore(Path.Combine(_dir, "feature-journal"));
        FeatureJournalEntry E(string kind, DateTime at, string taskId, DateTime? from = null, DateTime? to = null) =>
            new() { At = at, Kind = kind, TaskId = taskId, StartedAt = from, EndedAt = to, Outcome = kind == "turn" ? "success" : null };
        journal.Append(Owner, epic.Id, E("created", t0, epic.Id));
        journal.Append(Owner, epic.Id, E("turn", t0.AddMinutes(20), a.Id, t0, t0.AddMinutes(20)));
        journal.Append(Owner, epic.Id, E("turn", t0.AddMinutes(130), r.Id, t0.AddMinutes(100), t0.AddMinutes(130)));
        journal.Append(Owner, epic.Id, E("waiting", t0.AddMinutes(140), r.Id));
        journal.Append(Owner, epic.Id, E("waiting_cleared", t0.AddMinutes(150), r.Id));
        var sut = new StageScreenService(_tasks, journal: journal, personas: new FakePersonas(_tim, _kira));

        var kpi = sut.Build(a.Id, Owner).Value!.StageSummary.Kpi;

        kpi.ExecMinutes.Should().Be(20);
        kpi.ReviewMinutes.Should().Be(30, "ревью на обе задачи считается один раз, а не по разу на задачу");
        kpi.WaitedHumanMinutes.Should().Be(10);
        kpi.Turns.Should().Be(2, "ход исполнителя и ход ревью, без задвоения");
        kpi.TotalMinutes.Should().Be(60);
    }

    [Fact]
    public void Читатель_проекта_не_получает_сессию_исполнителя_даже_когда_транскрипт_ему_открыт()
    {
        var epic = Epic(routed: true);
        var a = Step(epic, "B1", "back", _tim);
        a.ProjectId = "p1";
        a.LinkedSessionId = "sess-1";
        var access = new Mock<IProjectAccess>();
        var reader = new RequestScope(Stranger, Owner, ProjectRole.Reader);
        access.Setup(x => x.Resolve(Stranger, "p1", It.IsAny<ProjectRight>())).Returns(reader);
        access.Setup(x => x.ResolveSession(Stranger, "sess-1", It.IsAny<ProjectRight>())).Returns(reader);
        var sut = new StageScreenService(_tasks, access.Object, personas: new FakePersonas(_tim, _kira));

        sut.Build(a.Id, Stranger).Value!.StageSummary.ExecutorSessionId.Should().BeNull();
        sut.Build(a.Id, Owner).Value!.StageSummary.ExecutorSessionId.Should().Be("sess-1");
    }

    [Fact]
    public void Чужой_чат_исполнителя_не_отдаётся_а_свой_отдаётся()
    {
        var epic = Epic(routed: true);
        var a = Step(epic, "B1", "back", _tim);
        a.ProjectId = "p1";
        a.LinkedSessionId = "sess-1";
        var access = new Mock<IProjectAccess>();
        access.Setup(x => x.Resolve(Stranger, "p1", It.IsAny<ProjectRight>()))
            .Returns(new RequestScope(Stranger, Owner, ProjectRole.Reader));
        var sut = new StageScreenService(_tasks, access.Object, personas: new FakePersonas(_tim, _kira));

        sut.Build(a.Id, Stranger).Value!.StageSummary.ExecutorSessionId.Should().BeNull();
        sut.Build(a.Id, Owner).Value!.StageSummary.ExecutorSessionId.Should().Be("sess-1");
    }

    [Fact]
    public void Легаси_без_маршрута_и_журнала_approx_без_токенов_и_соседа()
    {
        var epic = Epic(routed: false);
        var step = _tasks.Create(null, Owner, new CreateTaskRequest("шаг", FeatureId: epic.Id, Role: TaskRoles.Step, StepRef: "B1"));

        var s = _sut.Build(step.Id, Owner).Value!.StageSummary;

        s.Kpi.Approx.Should().BeTrue();
        s.Kpi.Tokens.Should().BeNull();
        s.Executor.Should().BeNull();
        s.ParallelNote.Should().BeNull();
        s.ExecutorSessionId.Should().BeNull();
        s.TemplateName.Should().BeNull();
        s.Tasks.Should().ContainSingle().Which.StepRef.Should().Be("B1");
    }

    [Fact]
    public void Без_соседней_ветки_когда_этап_один_в_цепочке()
    {
        var epic = Epic(routed: true);
        var merge = Step(epic, "M1", "merge", _tim);
        _ = Step(epic, "B1", "back", _tim);

        // back — предок merge: ветками они не считаются
        _sut.Build(merge.Id, Owner).Value!.StageSummary.ParallelNote.Should().BeNull();
    }
}
