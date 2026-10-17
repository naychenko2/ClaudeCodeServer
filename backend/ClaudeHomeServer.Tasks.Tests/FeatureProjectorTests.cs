using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Tasks.Projector;
using ClaudeHomeServer.Tasks.Tests.Fixtures;

namespace ClaudeHomeServer.Tasks.Tests;

// Золотые тесты FeatureProjector (режим «Исполнение фичи»,
// docs/research/turn-plan-feature-mode-cut-2026-10.md): 12 фикстур feature-runs/*.json
// (задача 1f4e6a620) — 8 покрываем здесь, полная 12 — отдельный шаг.
//
// Каждый кейс: фикстура → FeatureProjector.Build → сверка конвейера с ЭТАЛОНОМ (числа шагов/
// ревью/проверок, вердикты, ↺, прогресс, готовность). Эталон — ручной срез фикстуры, НЕ вывод
// реализации: мутация Build (например, сброс Steps/Verdicts) краснит все кейсы.
public class FeatureProjectorTests
{
    // Эталон фичи: epic/branch + конвейер (steps=step+rework, reviews, qa, final/merge id) +
    // вердикты (acc/awr/ret/wd) + progress + ready.
    public sealed record Case(
        string Fixture, string EpicId, string Branch,
        int Steps, int Reviews, int Qa, string? FinalId, string? MergeId,
        int Acc, int Awr, int Ret, int Wd, double Progress, bool Ready);

    // Ручной срез фикстур (подсчёт ролей/вердиктов; сверка с expected.json: loops=ret+awr).
    // xUnit MemberData — object[][], каждая строка — один эталон (Case).
    public static readonly object[][] Cases =
    [
        Row("feat-curator-console.json", "epic-curator-console", "feat/curator-console",
            4, 3, 0, null, null, 1, 1, 0, 0, 0.6667, false),
        Row("spike-vdi-ocr.json", "epic-spike-vdi-ocr", "spike/vdi-ocr",
            9, 5, 0, null, null, 2, 0, 3, 0, 1.0, false),
        Row("feat-local-media-history.json", "epic-local-media-history", "feat/local-media-history",
            9, 5, 5, "mlh-f08b44510", null, 2, 0, 6, 0, 0.7273, false),
        Row("fix-local-model-live-ctx-and-stream-speed.json", "epic-local-model-live-ctx-and-stream-speed",
            "fix/local-model-live-ctx-and-stream-speed",
            4, 0, 0, null, null, 0, 0, 0, 0, 0, false),
        Row("fix-local-model-busy-throughput.json", "epic-local-model-busy-throughput",
            "fix/local-model-busy-throughput",
            2, 0, 0, null, null, 0, 0, 0, 0, 0, false),
        Row("feat-gw-hint-headers.json", "epic-gw-hint-headers", "feat/gw-hint-headers",
            1, 0, 0, null, null, 0, 0, 0, 0, 0, false),
        Row("feat-subagent-model-force.json", "epic-subagent-model-force", "feat/subagent-model-force",
            2, 0, 0, null, null, 0, 0, 0, 0, 0, false),
        Row("fix-haiku-1m-family.json", "epic-haiku-1m-family", "fix/haiku-1m-family",
            2, 0, 0, null, null, 0, 0, 0, 0, 0, false),
    ];

    private static object[] Row(string fixture, string epicId, string branch,
        int steps, int reviews, int qa, string? finalId, string? mergeId,
        int acc, int awr, int ret, int wd, double progress, bool ready) =>
        [new Case(fixture, epicId, branch, steps, reviews, qa, finalId, mergeId, acc, awr, ret, wd, progress, ready)];

    [Theory]
    [MemberData(nameof(Cases))]
    public void Build_СовпадаетСЭталоном(Case c)
    {
        var tasks = FeatureRunFixture.Load(c.Fixture);
        var epic = tasks.Single(t => t.Role == TaskRoles.Feature);
        epic.Id.Should().Be(c.EpicId);
        epic.WorktreeBranch.Should().Be(c.Branch);

        var run = FeatureProjector.Build(epic, tasks);

        run.FeatureId.Should().Be(c.EpicId);
        run.Epic.Id.Should().Be(c.EpicId);
        run.Steps.Should().HaveCount(c.Steps);
        run.Reviews.Should().HaveCount(c.Reviews);
        run.Qa.Should().HaveCount(c.Qa);
        run.Final?.Id.Should().Be(c.FinalId);
        run.Merge?.Id.Should().Be(c.MergeId);
        run.Verdicts.Accepted.Should().Be(c.Acc);
        run.Verdicts.AcceptedWithRemarks.Should().Be(c.Awr);
        run.Verdicts.Returned.Should().Be(c.Ret);
        run.Verdicts.Withdrawn.Should().Be(c.Wd);
        run.Verdicts.Loops.Should().Be(c.Ret + c.Awr);
        run.Progress.Should().BeApproximately(c.Progress, 0.0001);
        run.Ready.Should().Be(c.Ready);
    }

    // Прогноз «готово»: финал с accepted/accepted_with_remarks → Ready=true, иначе false.
    // Синтетический кейс (не из фикстур): финал есть и принят.
    [Fact]
    public void Build_ФиналПринят_Gотово()
    {
        var epic = new TaskItem { Id = "epic-x", Role = TaskRoles.Feature };
        var final = new TaskItem
        {
            Id = "f1", FeatureId = "epic-x", Role = TaskRoles.Final,
            Verification = new TaskVerification { Verdict = TaskVerdicts.Accepted },
        };
        var run = FeatureProjector.Build(epic, [epic, final]);

        run.Ready.Should().BeTrue();
        run.Final.Should().NotBeNull();
        run.Final!.Id.Should().Be("f1");
    }

    // Финал с returned → не готов; без финала → не готов.
    [Fact]
    public void Build_ФиналВозвращён_НеГотово()
    {
        var epic = new TaskItem { Id = "epic-y", Role = TaskRoles.Feature };
        var final = new TaskItem
        {
            Id = "f2", FeatureId = "epic-y", Role = TaskRoles.Final,
            Verification = new TaskVerification { Verdict = TaskVerdicts.Returned },
        };
        FeatureProjector.Build(epic, [epic, final]).Ready.Should().BeFalse();
        FeatureProjector.Build(epic, [epic]).Ready.Should().BeFalse();
    }

    // Минор 1: вход Build защищён. Пустой список → пустой конвейер (шагов/ревью/проверок/финала/мержа нет).
    [Fact]
    public void Build_ПустойВход_ПустыеШаги()
    {
        var epic = new TaskItem { Id = "epic-empty", Role = TaskRoles.Feature };
        var run = FeatureProjector.Build(epic, []);

        run.Steps.Should().BeEmpty();
        run.Reviews.Should().BeEmpty();
        run.Qa.Should().BeEmpty();
        run.Final.Should().BeNull();
        run.Merge.Should().BeNull();
    }

    // Минор 1: чужая задача (FeatureId ≠ epic.Id, и не сам эпик) → явный ArgumentException,
    // а не молчаливая фильтрация. Эпик сам (FeatureId=null) чужим не считается.
    [Fact]
    public void Build_ЧужойЭпик_Бросает()
    {
        var epic = new TaskItem { Id = "epic-a", Role = TaskRoles.Feature };
        var foreign = new TaskItem { Id = "f9", FeatureId = "epic-OTHER", Role = TaskRoles.Step };

        ((Action)(() => FeatureProjector.Build(epic, [epic, foreign]))).Should()
            .Throw<ArgumentException>("чужая задача обязана падать явно, а не фильтроваться молча");
    }

    // ─── Б1: стадии конвейера и честный мерж ───────────────────────────────────────────────

    // Финал accepted, merge-задачи нет → merge НЕ done (active, а не done).
    // Это «честный мерж»: финал зелёный, но мержа не было.
    [Fact]
    public void Build_ФиналAcceptedБезМержа_MergeActive()
    {
        const string epicId = "epic-merge-test";
        var epic = new TaskItem { Id = epicId, Role = TaskRoles.Feature };
        var cut = new TaskItem { Id = "c1", FeatureId = epicId, Role = TaskRoles.Cut, Status = TaskItemStatus.Done };
        var plan = new TaskItem { Id = "p1", FeatureId = epicId, Role = TaskRoles.Plan, Status = TaskItemStatus.Done };
        var step = new TaskItem { Id = "s1", FeatureId = epicId, Role = TaskRoles.Step, Status = TaskItemStatus.Done };
        var review = new TaskItem
        {
            Id = "r1", FeatureId = epicId, Role = TaskRoles.Review, Status = TaskItemStatus.Done,
            Verification = new TaskVerification { Verdict = TaskVerdicts.Accepted },
        };
        var qa = new TaskItem
        {
            Id = "q1", FeatureId = epicId, Role = TaskRoles.Qa, Status = TaskItemStatus.Done,
            Verification = new TaskVerification { Verdict = TaskVerdicts.Accepted },
        };
        var final = new TaskItem
        {
            Id = "f1", FeatureId = epicId, Role = TaskRoles.Final, Status = TaskItemStatus.Done,
            Verification = new TaskVerification { Verdict = TaskVerdicts.Accepted },
        };

        var run = FeatureProjector.Build(epic, [epic, cut, plan, step, review, qa, final]);

        // Все стадии до merge — done; merge — 0 задач → active (не done).
        var mergeStage = run.Stages.Single(s => s.Key == "merge");
        mergeStage.State.Should().Be("active",
            "финал зелёный, а merge-задачи нет — мерж не пройден, а активен");
        // Все стадии до merge — done.
        run.Stages.Where(s => s.Key != "merge").Should().OnlyContain(s => s.State == "done");
    }

    // Нет cut-задачи, а план начат (имеет задачи) → cut = absent («нет в трекере»).
    [Fact]
    public void Build_НетCut_ПланНачат_CutAbsent()
    {
        const string epicId = "epic-absent-test";
        var epic = new TaskItem { Id = epicId, Role = TaskRoles.Feature };
        var plan = new TaskItem { Id = "p1", FeatureId = epicId, Role = TaskRoles.Plan, Status = TaskItemStatus.InProgress };
        var step = new TaskItem { Id = "s1", FeatureId = epicId, Role = TaskRoles.Step, Status = TaskItemStatus.InProgress };

        var run = FeatureProjector.Build(epic, [epic, plan, step]);

        var cutStage = run.Stages.Single(s => s.Key == "cut");
        cutStage.State.Should().Be("absent",
            "у cut ноль задач, а следующая стадия (plan) уже начата — «нет в трекере»");
        cutStage.Passes.Should().Be(0);
        cutStage.TaskIds.Should().BeEmpty();
    }

    // Cut-задача done → cut = done.
    [Fact]
    public void Build_CutDone_CutDone()
    {
        const string epicId = "epic-cut-done";
        var epic = new TaskItem { Id = epicId, Role = TaskRoles.Feature };
        var cut = new TaskItem { Id = "c1", FeatureId = epicId, Role = TaskRoles.Cut, Status = TaskItemStatus.Done };
        var plan = new TaskItem { Id = "p1", FeatureId = epicId, Role = TaskRoles.Plan, Status = TaskItemStatus.InProgress };

        var run = FeatureProjector.Build(epic, [epic, cut, plan]);

        var cutStage = run.Stages.Single(s => s.Key == "cut");
        cutStage.State.Should().Be("done");
        cutStage.Passes.Should().Be(1);
        cutStage.TaskIds.Should().ContainSingle().Which.Should().Be("c1");
    }

    // У prod при 2 возвратах (2 review с returned) passes == 3 (1 + 2).
    [Fact]
    public void Build_ДваВозврата_Passes3()
    {
        const string epicId = "epic-passes-test";
        var epic = new TaskItem { Id = epicId, Role = TaskRoles.Feature };
        var step = new TaskItem { Id = "s1", FeatureId = epicId, Role = TaskRoles.Step, Status = TaskItemStatus.InProgress };
        var r1 = new TaskItem
        {
            Id = "r1", FeatureId = epicId, Role = TaskRoles.Review, Status = TaskItemStatus.Done,
            Verification = new TaskVerification { Verdict = TaskVerdicts.Returned },
        };
        var r2 = new TaskItem
        {
            Id = "r2", FeatureId = epicId, Role = TaskRoles.Review, Status = TaskItemStatus.Done,
            Verification = new TaskVerification { Verdict = TaskVerdicts.Returned },
        };

        var run = FeatureProjector.Build(epic, [epic, step, r1, r2]);

        var prodStage = run.Stages.Single(s => s.Key == "prod");
        prodStage.Passes.Should().Be(3, "1 (базовый проход) + 2 (возврата) = 3");
    }

    // Тон: финал вернул (returned), доработка (rework) идёт → активная prod с tone = loop.
    [Fact]
    public void Build_ФиналВернул_ДоработкаИдёт_ToneLoop()
    {
        const string epicId = "epic-loop-test";
        var epic = new TaskItem { Id = epicId, Role = TaskRoles.Feature };
        var step = new TaskItem { Id = "s1", FeatureId = epicId, Role = TaskRoles.Step, Status = TaskItemStatus.Done };
        var rework = new TaskItem { Id = "rw1", FeatureId = epicId, Role = TaskRoles.Rework, Status = TaskItemStatus.InProgress };
        var final = new TaskItem
        {
            Id = "f1", FeatureId = epicId, Role = TaskRoles.Final, Status = TaskItemStatus.Done,
            Verification = new TaskVerification { Verdict = TaskVerdicts.Returned },
        };

        var run = FeatureProjector.Build(epic, [epic, step, rework, final]);

        // Prod: step (done) + rework (inProgress) → не все done → active.
        var prodStage = run.Stages.Single(s => s.Key == "prod");
        prodStage.State.Should().Be("active");
        prodStage.Tone.Should().Be("loop",
            "финал вернул (returned), доработка (rework) идёт — цикл");
    }

    // Мутация на правило мержа: если merge done при `|| ready` (финал зелёный),
    // то тест Build_ФиналAcceptedБезМержа_MergeActive краснеет (merge будет done, а не active).
    // Мутация: в isDone для merge добавить `|| ready`.
    [Fact]
    public void Build_МутацияМержа_ReadyНеДелаетMergeDone()
    {
        // Сценарий: финал accepted (ready=true), merge-задачи нет.
        // Без мутации: merge active. С мутацией `|| ready`: merge done → тест краснеет.
        const string epicId = "epic-mutation-test";
        var epic = new TaskItem { Id = epicId, Role = TaskRoles.Feature };
        var cut = new TaskItem { Id = "c1", FeatureId = epicId, Role = TaskRoles.Cut, Status = TaskItemStatus.Done };
        var plan = new TaskItem { Id = "p1", FeatureId = epicId, Role = TaskRoles.Plan, Status = TaskItemStatus.Done };
        var step = new TaskItem { Id = "s1", FeatureId = epicId, Role = TaskRoles.Step, Status = TaskItemStatus.Done };
        var final = new TaskItem
        {
            Id = "f1", FeatureId = epicId, Role = TaskRoles.Final, Status = TaskItemStatus.Done,
            Verification = new TaskVerification { Verdict = TaskVerdicts.Accepted },
        };

        var run = FeatureProjector.Build(epic, [epic, cut, plan, step, final]);

        // Ready=true (финал accepted), но merge-задачи нет → merge НЕ done.
        run.Ready.Should().BeTrue();
        var mergeStage = run.Stages.Single(s => s.Key == "merge");
        // Мутация: если isDone[merge] = tasks.Count > 0 && all Done || ready → true (ready=true).
        // Без мутации: isDone[merge] = false (0 задач) → merge active.
        mergeStage.State.Should().Be("active",
            "ready=true не делает merge done: мерж — только при merge.Status == Done");
    }
}
