using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Tasks;
using ClaudeHomeServer.Services.Tasks.Projector;

namespace ClaudeHomeServer.Tasks.Tests;

// B02: метастрока шапки страницы фичи — run.Meta и epic.ShortId/CreatedAt.
public class FeatureRunMetaTests
{
    private static readonly DateTime T = new(2026, 11, 13, 10, 0, 0, DateTimeKind.Utc);

    private static FeatureWorkflow TenStageRoute() => new()
    {
        TemplateId = "builtin-parallel-cycle", Scope = "global",
        Stages =
        [
            new RouteStage { Id = "cut", Role = "cut" },
            new RouteStage { Id = "design", Role = "design", After = ["cut"] },
            new RouteStage { Id = "plan", Role = "plan", After = ["design"] },
            new RouteStage { Id = "qa-plan", Role = "qa", After = ["design"] },
            new RouteStage { Id = "step-back", Role = "step", Lane = "back", After = ["plan"] },
            new RouteStage { Id = "step-front", Role = "step", Lane = "front", After = ["plan"] },
            new RouteStage { Id = "review", Role = "review", After = ["step-back", "step-front"] },
            new RouteStage { Id = "qa", Role = "qa", After = ["review", "qa-plan"] },
            new RouteStage { Id = "final", Role = "final", After = ["qa"] },
            new RouteStage { Id = "merge", Role = "merge", After = ["final"] },
        ],
        ConfirmedVersion = 1, ConfirmedAt = T.AddHours(-7), ConfirmedByUser = true,
    };

    private static TaskItem Done(string id, string epicId, string role, string? stageId = null) => new()
    {
        Id = id, FeatureId = epicId, Role = role, Status = TaskItemStatus.Done, CompletedAt = T.AddHours(-3),
        StepRef = role == TaskRoles.Step ? "G1" : null,
        Lane = role == TaskRoles.Step ? "back" : null,
        Stage = stageId is null ? null
            : new StageSnapshot { TemplateId = "builtin-parallel-cycle", Role = role, StageId = stageId },
    };

    [Fact]
    public void Meta_ДесятьЭтаповДваПараллельных_ЧетыреИзДесяти_ВеткиДва()
    {
        const string epicId = "0123456789abcdef-epic";
        var epic = new TaskItem { Id = epicId, Role = TaskRoles.Feature, CreatedAt = T.AddHours(-8), Workflow = TenStageRoute() };
        var tasks = new List<TaskItem>
        {
            epic,
            Done("c1", epicId, TaskRoles.Cut),
            Done("d1", epicId, TaskRoles.Design),
            Done("p1", epicId, TaskRoles.Plan),
            Done("s1", epicId, TaskRoles.Step, "step-back"),
            new() { Id = "s2", FeatureId = epicId, Role = TaskRoles.Step, StepRef = "G2", Lane = "front",
                Status = TaskItemStatus.InProgress, ClaudeStartedAt = T.AddHours(-1) },
        };

        var run = FeatureProjector.Build(epic, tasks, T);

        run.Meta.StagesDone.Should().Be(4);
        run.Meta.StagesTotal.Should().Be(10);
        run.Meta.ParallelBranches.Should().Be(2);
        run.Meta.ProgressPct.Should().Be(40);
        run.Meta.Approx.Should().BeFalse();
        run.Meta.EtaMinutes.Should().BeNull("срока ещё никто не проставил");
        run.Epic.ShortId.Should().Be("01234567");
        run.Epic.CreatedAt.Should().Be(T.AddHours(-8));

        var slim = FeatureProjector.SlimRun(run);
        slim.Meta.Should().BeEquivalentTo(run.Meta, "облегчение run не теряет meta");
        slim.Epic.ShortId.Should().Be("01234567");
    }

    [Fact]
    public void Meta_Легаси_ПриблизительноБезПроцента()
    {
        var epic = new TaskItem { Id = "legacy-epic-id", Role = TaskRoles.Feature, CreatedAt = T.AddHours(-2) };
        var run = FeatureProjector.Build(epic, [epic], T);

        run.Meta.Approx.Should().BeTrue();
        run.Meta.ProgressPct.Should().BeNull();
        run.Meta.ParallelBranches.Should().Be(0);
        run.Meta.StagesTotal.Should().Be(0);
        run.Meta.StagesDone.Should().Be(0);
    }

    [Fact]
    public void Meta_ЗамерСрока_ПроставляетсяИПереживаетОблегчение_БезЗамера_Null()
    {
        var epic = new TaskItem { Id = "eta-epic-0001", Role = TaskRoles.Feature, CreatedAt = T.AddHours(-2), Workflow = TenStageRoute() };
        var run = FeatureProjector.Build(epic, [epic], T);
        run.Meta.EtaMinutes.Should().BeNull();

        run.ApplyEta(95);
        run.Meta.EtaMinutes.Should().Be(95);
        FeatureProjector.SlimRun(run).Meta.EtaMinutes.Should().Be(95);

        run.ApplyEta(null);
        run.Meta.EtaMinutes.Should().BeNull();
    }
}
