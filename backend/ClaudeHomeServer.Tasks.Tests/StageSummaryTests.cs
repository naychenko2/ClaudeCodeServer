using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
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

        s.ParallelNote.Should().NotBeNull();
        s.ParallelNote!.StageId.Should().Be("front");
        s.ParallelNote.Title.Should().Be("Фронт");
        s.ParallelNote.DoneCount.Should().Be(1);
        s.ParallelNote.TotalCount.Should().Be(1);
        s.ParallelNote.PersonaName.Should().Be("Кира");
        _ = b;
    }

    [Fact]
    public void Чат_исполнителя_закрыт_постороннему()
    {
        var epic = Epic(routed: true);
        var a = Step(epic, "B1", "back", _tim);
        a.LinkedSessionId = "sess-1";

        _sut.Build(a.Id, Stranger).Outcome.Should().Be(StageOutcome.NotFound);
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
