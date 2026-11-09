using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Tasks.Projector;

namespace ClaudeHomeServer.Tasks.Tests;

// Сторож контракта SlimRun (GC-B3): облегчение задачи для выхода агрегата (FeatureProjector.Slim)
// НЕ срезает acceptance/evidence/planSteps — они копируются рефлексией (TaskListItem.From
// копирует все свойства TaskItem), и фронт/сторож фичи читают их из облегчённого run.
// Тексты (Description/ResultMarkdown) при этом обнуляются намеренно: итог грузится лениво.
//
// Мутации (краснеют, если Slim начнёт срезать контракт):
//   • item.Acceptance = [] в FeatureProjector.Slim → тест «Acceptance» красный;
//   • item.PlanSteps = null в FeatureProjector.Slim → тест «PlanSteps» красный.
//
// Коллекция без параллелизации: тесты этого файла идут отдельным проходом, чтобы не сдвигать
// расписание параллельных коллекций — от этого страдает тайминг-гейт журнала
// (FeatureTimelineJournalTests.Build_Замер_20kЗаписей_Быстрый): он меряет отношение времени
// сборки 20k/5k записей и на параллельной нагрузке уезжает за порог нелинейности.
[CollectionDefinition("FeatureSlimRunContractSerial", DisableParallelization = true)]
public class FeatureSlimRunContractSerialCollection;

[Collection("FeatureSlimRunContractSerial")]
public class FeatureSlimRunContractTests
{
    private static readonly DateTime T0 = new(2026, 10, 25, 9, 0, 0, DateTimeKind.Utc);

    // Эпик фичи (role=feature, FeatureId=null).
    private static TaskItem Epic() => new()
    {
        Id = "epic",
        Role = TaskRoles.Feature,
        Status = TaskItemStatus.Todo,
        CreatedAt = T0,
    };

    // Задача плана (role=plan) с критериями приёмки, свидетельствами и шагами плана.
    private static TaskItem PlanTask() => new()
    {
        Id = "plan",
        FeatureId = "epic",
        Role = TaskRoles.Plan,
        Title = "План",
        Status = TaskItemStatus.Todo,
        CreatedAt = T0,
        ResultMarkdown = "Итог плана",
        Acceptance =
        [
            new AcceptanceCriterion("c1", "Первый критерий"),
            new AcceptanceCriterion("c2", "Второй критерий"),
        ],
        Evidence =
        [
            new AcceptanceEvidence("c1", "dotnet test", "2/2 зелёные"),
        ],
        PlanSteps =
        [
            new PlannedStep { StepRef = "B1", Title = "Шаг бэка", Lane = "back" },
            new PlannedStep { StepRef = "F2", Title = "Шаг фронта", Lane = "front" },
        ],
    };

    private static FeatureRun Run(TaskItem epic, params TaskItem[] children)
    {
        var all = new List<TaskItem> { epic };
        all.AddRange(children);
        return FeatureProjector.BuildCore(epic, all);
    }

    // ── 1. acceptance/evidence/planSteps после SlimRun совпадают с исходными ──────────────────

    [Fact]
    public void SlimRun_Acceptance_СовпадаетС_Исходным()
    {
        var plan = PlanTask();
        var slim = FeatureProjector.SlimRun(Run(Epic(), plan));

        var slimPlan = slim.Plan.Single();
        slimPlan.Acceptance.Select(a => (a.Id, a.Text)).Should()
            .Equal(("c1", "Первый критерий"), ("c2", "Второй критерий"));
    }

    [Fact]
    public void SlimRun_Evidence_СовпадаетС_Исходным()
    {
        var plan = PlanTask();
        var slim = FeatureProjector.SlimRun(Run(Epic(), plan));

        var slimPlan = slim.Plan.Single();
        slimPlan.Evidence.Should().HaveCount(1);
        slimPlan.Evidence[0].CriterionId.Should().Be("c1");
        slimPlan.Evidence[0].How.Should().Be("dotnet test");
        slimPlan.Evidence[0].Result.Should().Be("2/2 зелёные");
    }

    [Fact]
    public void SlimRun_PlanSteps_СовпадаютС_Исходными()
    {
        var plan = PlanTask();
        var slim = FeatureProjector.SlimRun(Run(Epic(), plan));

        var slimPlan = slim.Plan.Single();
        slimPlan.PlanSteps.Should().NotBeNull();
        slimPlan.PlanSteps!.Select(s => (s.StepRef, s.Title, s.Lane)).Should().Equal(
            ("B1", "Шаг бэка", "back"),
            ("F2", "Шаг фронта", "front"));
    }

    // Эпик — тоже задача фичи: его acceptance/evidence (критерии самой фичи) облегчение не срезает.
    [Fact]
    public void SlimRun_Acceptance_Эпика_НеСрезано()
    {
        var epic = Epic();
        epic.Acceptance = [new AcceptanceCriterion("f1", "Критерий фичи")];
        epic.Evidence = [new AcceptanceEvidence("f1", "dotnet test", "зелёные")];

        var slim = FeatureProjector.SlimRun(Run(epic, PlanTask()));

        slim.Epic.Acceptance.Select(a => a.Text).Should().Equal("Критерий фичи");
        slim.Epic.Evidence.Select(e => e.CriterionId).Should().Equal("f1");
    }

    // ── 2. Итог (ResultMarkdown) в агрегате остаётся null — грузится лениво на фронте ───────

    [Fact]
    public void SlimRun_ResultMarkdown_Null_ИтогГрузитсяЛениво()
    {
        var plan = PlanTask();
        var slim = FeatureProjector.SlimRun(Run(Epic(), plan));

        // В полном run итог есть, в облегчённом — null (фронт догружает карточку по id).
        Run(Epic(), plan).Plan.Single().ResultMarkdown.Should().Be("Итог плана");
        slim.Plan.Single().ResultMarkdown.Should().BeNull();
    }
}
