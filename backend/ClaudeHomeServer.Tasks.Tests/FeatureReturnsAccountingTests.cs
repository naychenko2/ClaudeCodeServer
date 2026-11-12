using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Tasks.Projector;

namespace ClaudeHomeServer.Tasks.Tests;

// FP-B1: «возвратом» считается только returned. Замечания (accepted_with_remarks) — отдельная ось
// Remarks: ни в Count источника, ни в Open, ни в счётчик шагов линии Returns. Ось «сколько раз
// возвращали» (Verdicts.Loops, chain.Loops) при этом не меняется — она про работу, вернувшуюся к
// исполнителю. Эталон каждого теста считан руками из его сценария.
public class FeatureReturnsAccountingTests
{
    private const string Epic = "ep";
    private static readonly DateTime T0 = new(2026, 11, 11, 10, 0, 0, DateTimeKind.Utc);

    private static TaskItem Feature() => new() { Id = Epic, Role = TaskRoles.Feature, CreatedAt = T0 };

    private static TaskItem Step(string id, string stepRef, int minute, string? lane = "back") => new()
    {
        Id = id, FeatureId = Epic, Role = TaskRoles.Step, StepRef = stepRef, Status = TaskItemStatus.Done,
        Lane = lane, CreatedAt = T0.AddMinutes(minute), CompletedAt = T0.AddMinutes(minute + 1),
    };

    private static TaskItem Review(string id, string verdict, int minute, params string[] reviewOf) => new()
    {
        Id = id, FeatureId = Epic, Role = TaskRoles.Review, Status = TaskItemStatus.Done,
        ReviewOf = reviewOf.ToList(), CreatedAt = T0.AddMinutes(minute), CompletedAt = T0.AddMinutes(minute + 1),
        Verification = new TaskVerification { Verdict = verdict },
    };

    private static TaskItem Rework(string id, string stepRef, int minute, string reviewOf,
        TaskItemStatus status = TaskItemStatus.Done) => new()
    {
        Id = id, FeatureId = Epic, Role = TaskRoles.Rework, StepRef = stepRef, Status = status,
        ReviewOf = [reviewOf], CreatedAt = T0.AddMinutes(minute),
        CompletedAt = status == TaskItemStatus.Done ? T0.AddMinutes(minute + 1) : null,
    };

    private static TaskItem Plan(string id, int minute, params (string Ref, string? Lane)[] steps) => new()
    {
        Id = id, FeatureId = Epic, Role = TaskRoles.Plan, Status = TaskItemStatus.Done,
        CreatedAt = T0.AddMinutes(minute), CompletedAt = T0.AddMinutes(minute + 1),
        PlanSteps = steps.Select(x => new PlannedStep { StepRef = x.Ref, Title = x.Ref, Lane = x.Lane }).ToList(),
    };

    private static TaskItem Merge(string id, int minute) => new()
    {
        Id = id, FeatureId = Epic, Role = TaskRoles.Merge, Status = TaskItemStatus.Done,
        CreatedAt = T0.AddMinutes(minute), CompletedAt = T0.AddMinutes(minute + 1),
    };

    private static FeatureRun Run(params TaskItem[] tasks) =>
        FeatureProjector.Build(tasks.First(t => t.Role == TaskRoles.Feature), tasks, T0.AddDays(1));

    // ─── Замечания — не возврат ─────────────────────────────────────────────────────────────

    // Ревью на два шага одной линии с вердиктом accepted_with_remarks: ни одного возврата —
    // ни в источнике, ни в линии. Ось витков (Loops) при этом считает замечания, как раньше.
    [Fact]
    public void RemarksAreNotReturns()
    {
        var run = Run(Feature(), Step("s1", "A", 0), Step("s2", "B", 5),
            Review("r1", TaskVerdicts.AcceptedWithRemarks, 10, "s1", "s2"));

        run.ReturnsBySource.Review.Count.Should().Be(0, "accepted_with_remarks — не возврат");
        run.ReturnsBySource.Review.Remarks.Should().Be(1, "один виток принят с замечаниями");
        run.Lanes.Single(l => l.Lane == "back").Returns.Should().Be(0, "ни один шаг линии не возвращался");

        // «Вне шага» не тронуто: замечание по-прежнему виток работы (Loops), шаг — принят.
        run.Verdicts.AcceptedWithRemarks.Should().Be(1);
        run.Verdicts.Loops.Should().Be(1);
        run.StepChains.Should().HaveCount(2);
        run.StepChains.Select(c => c.Loops).Should().Equal(1, 1);
        run.StepChains.Select(c => c.Outcome).Should().Equal("accepted", "accepted");
    }

    // Та же пачка с вердиктом returned: один возврат источника (одна задача-ревью) на два шага —
    // связь многие-ко-многим не меняется: в Returns линии оба шага.
    [Fact]
    public void ReturnedBatchCountsEachStep()
    {
        var run = Run(Feature(), Step("s1", "A", 0), Step("s2", "B", 5),
            Review("r1", TaskVerdicts.Returned, 10, "s1", "s2"));

        run.ReturnsBySource.Review.Count.Should().Be(1, "задача-ревью несёт один возврат");
        run.ReturnsBySource.Review.Remarks.Should().Be(0);
        var back = run.Lanes.Single(l => l.Lane == "back");
        back.Returns.Should().Be(2, "ревью на два шага возвращает оба (поведение FeatureStepAccounting не менялся)");
        back.Total.Should().Be(2);

        // Сумма по источникам = Verdicts.Returned, сумма замечаний = Verdicts.AcceptedWithRemarks.
        (run.ReturnsBySource.Review.Count + run.ReturnsBySource.Qa.Count + run.ReturnsBySource.Final.Count)
            .Should().Be(run.Verdicts.Returned);
    }

    // Open — только среди возвратов: незакрытая доработка после замечаний бейдж не красит.
    [Fact]
    public void OpenCountsOnlyReturnedRounds()
    {
        var run = Run(Feature(),
            Step("s1", "A", 0), Step("s2", "B", 5),
            Review("r1", TaskVerdicts.AcceptedWithRemarks, 10, "s1"), Rework("w1", "A", 12, "r1", TaskItemStatus.InProgress),
            Review("r2", TaskVerdicts.Returned, 20, "s2"), Rework("w2", "B", 22, "r2", TaskItemStatus.InProgress));

        run.ReturnsBySource.Review.Count.Should().Be(1);
        run.ReturnsBySource.Review.Remarks.Should().Be(1);
        run.ReturnsBySource.Review.Open.Should().Be(1, "открыт только возврат с незакрытой доработкой");
        run.Lanes.Single(l => l.Lane == "back").Returns.Should().Be(1, "в линии вернулся один шаг");
    }

    // ─── Сверка (а): Σ линий = знаменатель, «other» входит ──────────────────────────────────

    // План: шаг в бэке, шаг во фронте, два шага без линии и с нераспознанной → линия «other».
    // Плюс шаг, заведённый после мержа (FollowUp): он вне знаменателя и вне линий — с обеих сторон.
    // Равенство Σ всех линий и Total — по замыслу: расхождение «33 + 43 ≠ 78» даёт сумма только
    // back+front, в которой нет линии «other». Линия «other» реальна: для заведённого шага она
    // берётся из Lane самой задачи-шага (FeatureStepAccounting.cs:453), для не заведённого — из
    // строки плана (:502), пустая или нераспознанная («прочее») даёт «other» (NormalizeLane).
    [Fact]
    public void StepsEqualSumOfLanes()
    {
        var run = Run(Feature(),
            Plan("pl", 0, ("A", "back"), ("B", "front"), ("C", null), ("D", "прочее")),
            Step("s1", "A", 5, "back"), Step("s2", "B", 6, "front"),
            Merge("m", 20),
            Step("s3", "F", 30));

        run.StepProgress.Total.Should().Be(4, "A, B (на ревью) + C, D (не заведены)");
        run.StepProgress.FollowUpTotal.Should().Be(1, "шаг после мержа — вне знаменателя");
        run.StepChains.Single(c => c.StepRef == "F").FollowUp.Should().BeTrue();

        run.Lanes.Sum(l => l.Total).Should().Be(run.StepProgress.Total,
            "Σ линий = StepProgress.Total (= Summary.Steps): линия «other» входит в сумму");
        run.Lanes.Single(l => l.Lane == "other").Total.Should().Be(2, "шаги без линии и с нераспознанной — в «other»");
        run.Lanes.Where(l => l.Lane != "other").Sum(l => l.Total).Should().Be(2,
            "сумма только back+front меньше Total — та самая разница «33 + 43 ≠ 78»");
        run.Lanes.Should().HaveCount(3, "линий ровно три: доработка после мержа (ключ «F») линии не даёт");
    }
}
