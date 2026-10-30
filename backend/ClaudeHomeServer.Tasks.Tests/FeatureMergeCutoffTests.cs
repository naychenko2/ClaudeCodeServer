using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Tasks.Projector;

namespace ClaudeHomeServer.Tasks.Tests;

// Проекция принятия и мержа (P1): принятый финал закрывает цели прошлых кругов; возврат по нескольким
// целям касается только шагов с доработкой; мерж — отсечка «в master», ортогональная корзинам.
// Эталон считан руками из сценария каждого теста.
public class FeatureMergeCutoffTests
{
    private const string Epic = "ep";
    private static readonly DateTime T0 = new(2026, 10, 1, 10, 0, 0, DateTimeKind.Utc);

    private static TaskItem Feature() => new() { Id = Epic, Role = TaskRoles.Feature, CreatedAt = T0 };

    // Шаг создан в minute, сдан через минуту (или длительность dur).
    private static TaskItem Step(string id, string stepRef, int minute, int dur = 1) => new()
    {
        Id = id, FeatureId = Epic, Role = TaskRoles.Step, StepRef = stepRef, Status = TaskItemStatus.Done,
        Lane = "back", CreatedAt = T0.AddMinutes(minute), CompletedAt = T0.AddMinutes(minute + dur),
    };

    private static TaskItem OpenStep(string id, string stepRef, int minute) => new()
    {
        Id = id, FeatureId = Epic, Role = TaskRoles.Step, StepRef = stepRef, Status = TaskItemStatus.InProgress,
        Lane = "back", CreatedAt = T0.AddMinutes(minute),
    };

    private static TaskItem Rework(string id, string stepRef, int minute, params string[] reviewOf) => new()
    {
        Id = id, FeatureId = Epic, Role = TaskRoles.Rework, StepRef = stepRef, Status = TaskItemStatus.Done,
        ReviewOf = reviewOf.ToList(), CreatedAt = T0.AddMinutes(minute), CompletedAt = T0.AddMinutes(minute + 1),
    };

    private static TaskItem Verdict(string id, string role, string verdict, int minute, params string[] reviewOf) => new()
    {
        Id = id, FeatureId = Epic, Role = role, Status = TaskItemStatus.Done, ReviewOf = reviewOf.ToList(),
        CreatedAt = T0.AddMinutes(minute), CompletedAt = T0.AddMinutes(minute + 1),
        Verification = new TaskVerification { Verdict = verdict },
    };

    // Мерж закрывается ровно в T0+doneAt.
    private static TaskItem Merge(string id, int doneAt, params string[] reviewOf) => new()
    {
        Id = id, FeatureId = Epic, Role = TaskRoles.Merge, Status = TaskItemStatus.Done,
        ReviewOf = reviewOf.Length == 0 ? null : reviewOf.ToList(),
        CreatedAt = T0.AddMinutes(doneAt - 1), CompletedAt = T0.AddMinutes(doneAt),
    };

    private static FeatureRun Run(params TaskItem[] tasks) =>
        FeatureProjector.Build(tasks.First(t => t.Role == TaskRoles.Feature), tasks, T0.AddDays(1));

    private static FeatureStepChain Chain(FeatureRun run, string stepRef) =>
        run.StepChains.Single(c => c.StepRef == stepRef);

    // (a) Промежуточный мерж: шаги, сданные до него, — в master; фича не смержена.
    [Fact]
    public void ПромежуточныйМерж_ШагиВMaster_ФичаНеСмержена()
    {
        var run = Run(Feature(), Step("s1", "Ш1", 1), Step("s2", "Ш2", 3), OpenStep("s3", "Ш3", 5),
            Merge("m1", 10));

        run.State.Should().NotBe(FeatureStates.Merged, "финал не принят — мерж промежуточный");
        Chain(run, "Ш1").InMaster.Should().BeTrue();
        Chain(run, "Ш2").InMaster.Should().BeTrue();
        Chain(run, "Ш3").InMaster.Should().BeFalse("шаг ещё в работе");
        run.StepProgress.InMaster.Should().Be(2);
        run.Merges.Should().ContainSingle().Which.StepRefs.Should().Equal("Ш1", "Ш2");
        run.Merges[0].TaskId.Should().Be("m1");
        run.Merges[0].At.Should().Be(T0.AddMinutes(10));
    }

    [Fact]
    public void МержСReviewOf_ВMasterТолькоПеречисленные()
    {
        var run = Run(Feature(), Step("s1", "Ш1", 1), Step("s2", "Ш2", 3), Merge("m1", 10, "s1"));

        Chain(run, "Ш1").InMaster.Should().BeTrue();
        Chain(run, "Ш2").InMaster.Should().BeFalse("мерж перечислил только s1");
    }

    [Fact]
    public void ШагСданРовноВМоментМержа_НеВMaster_Граница()
    {
        // s1: создан на 8-й минуте, сдан на 10-й; мерж закрыт ровно на 10-й → [.., mergedAt) не включает.
        var run = Run(Feature(), Step("s1", "Ш1", 8, dur: 2), Step("s2", "Ш2", 1), Merge("m1", 10));

        Chain(run, "Ш1").InMaster.Should().BeFalse();
        Chain(run, "Ш2").InMaster.Should().BeTrue();
    }

    [Fact]
    public void БезМержей_ПустойСписокИНольВMaster()
    {
        var run = Run(Feature(), Step("s1", "Ш1", 1));

        run.Merges.Should().BeEmpty();
        run.StepProgress.InMaster.Should().Be(0);
        run.WaveAfterMerge.Should().BeFalse();
    }

    // (b) Доработка/шаг после мержа снимает merged.
    [Fact]
    public void ДоработкаПослеМержа_СнимаетMerged()
    {
        var baseTasks = new List<TaskItem>
        {
            Feature(), Step("s1", "Ш1", 1), Verdict("r1", TaskRoles.Review, TaskVerdicts.Accepted, 3, "s1"),
            Verdict("f1", TaskRoles.Final, TaskVerdicts.Accepted, 5, "s1"), Merge("m1", 10),
        };
        FeatureProjector.Build(baseTasks[0], baseTasks, T0.AddDays(1)).State.Should().Be(FeatureStates.Merged);

        baseTasks.Add(Rework("w1", "Ш1", 20));
        var run = FeatureProjector.Build(baseTasks[0], baseTasks, T0.AddDays(1));

        run.State.Should().NotBe(FeatureStates.Merged);
        run.WaveAfterMerge.Should().BeTrue();
        run.Summary.Should().BeNull("терминального состояния больше нет");
        Chain(run, "Ш1").InMaster.Should().BeFalse("доработка сдана после мержа");
    }

    // (c) Повторный мерж: итог и MergedAt — по последнему, merged возвращается.
    [Fact]
    public void ПовторныйМерж_ИтогПоПоследнему_ВсеМержиВСписке()
    {
        var tasks = new List<TaskItem>
        {
            Feature(), Step("s1", "Ш1", 1), Verdict("f1", TaskRoles.Final, TaskVerdicts.Accepted, 3, "s1"),
            Merge("m1", 10),
            Step("s2", "Ш2", 20), Verdict("f2", TaskRoles.Final, TaskVerdicts.Accepted, 23, "s2"),
            Merge("m2", 30),
        };
        var run = FeatureProjector.Build(tasks[0], tasks, T0.AddDays(1));

        run.State.Should().Be(FeatureStates.Merged);
        run.WaveAfterMerge.Should().BeFalse();
        run.Summary!.MergedAt.Should().Be(T0.AddMinutes(30), "итог — по последнему мержу");
        run.Merges.Select(m => m.TaskId).Should().Equal("m1", "m2");
        run.Merges[0].StepRefs.Should().Equal("Ш1");
        run.Merges[1].StepRefs.Should().Equal("Ш2");
        Chain(run, "Ш1").MergedAt.Should().Be(T0.AddMinutes(10));
        Chain(run, "Ш2").MergedAt.Should().Be(T0.AddMinutes(30));
    }

    // (d) Возвращённый final по четырём шагам, доработка одного, принятый final → все приняты, 100 %.
    [Fact]
    public void ВозвращённыйФиналПоЧетыремШагам_ДоработкаОдного_ПринятыйФинал_ВсеПринятыСтоПроцентов()
    {
        var run = Run(Feature(),
            Step("s1", "Ш1", 1), Step("s2", "Ш2", 2), Step("s3", "Ш3", 3), Step("s4", "Ш4", 4),
            Verdict("f1", TaskRoles.Final, TaskVerdicts.Returned, 10, "s1", "s2", "s3", "s4"),
            Rework("w1", "Ш1", 15),
            Verdict("f2", TaskRoles.Final, TaskVerdicts.Accepted, 20, "f1"));

        run.StepProgress.Total.Should().Be(4);
        run.StepProgress.Accepted.Should().Be(4);
        run.StepChains.Should().OnlyContain(c => c.Outcome == "accepted");
        run.StepsAccepted.Should().Be(run.StepsTotal);
    }

    // Правило 2: возврат по нескольким целям касается только шагов с доработкой того же stepRef.
    [Fact]
    public void ВозвратПоНесколькимЦелям_ВозвращаетТолькоШагСДоработкой()
    {
        var run = Run(Feature(),
            Step("s1", "Ш1", 1), Step("s2", "Ш2", 2), Step("s3", "Ш3", 3),
            Verdict("f1", TaskRoles.Final, TaskVerdicts.Returned, 10, "s1", "s2", "s3"),
            Rework("w1", "Ш1", 15));

        Chain(run, "Ш1").Rounds.Should().ContainSingle(r => r.Verdict == TaskVerdicts.Returned);
        Chain(run, "Ш2").Rounds.Should().BeEmpty("доработки с тем же stepRef нет — вердикт «вернули» не получен");
        Chain(run, "Ш3").Rounds.Should().BeEmpty();
        Chain(run, "Ш2").Outcome.Should().Be("pending", "шаг сдан, витка по нему нет — ждёт ревью");
        run.StepProgress.Accepted.Should().Be(0);
    }

    [Fact]
    public void ВозвратПоОдномуШагу_БезДоработки_ВитокСчитается()
    {
        var run = Run(Feature(), Step("s1", "Ш1", 1),
            Verdict("r1", TaskRoles.Review, TaskVerdicts.Returned, 10, "s1"));

        Chain(run, "Ш1").Rounds.Should().ContainSingle();
        Chain(run, "Ш1").Outcome.Should().Be("returned");
    }
}
