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

    // Прогноз «готово»: финал ЗАКРЫТ (Done) и принят (accepted/accepted_with_remarks) → Ready=true.
    // Контроль (M1): Done + accepted → Ready=true.
    [Fact]
    public void Build_ФиналПринят_Gотово()
    {
        var epic = new TaskItem { Id = "epic-x", Role = TaskRoles.Feature };
        var final = new TaskItem
        {
            Id = "f1", FeatureId = "epic-x", Role = TaskRoles.Final,
            Status = TaskItemStatus.Done,
            Verification = new TaskVerification { Verdict = TaskVerdicts.Accepted },
        };
        var run = FeatureProjector.Build(epic, [epic, final]);

        run.Ready.Should().BeTrue();
        run.Final.Should().NotBeNull();
        run.Final!.Id.Should().Be("f1");
    }

    // M1: финал принят (accepted), но задача финала ещё НЕ закрыта (Todo/InProgress) → Ready=false.
    // Контракт «Ready» — «финал ЗАКРЫТ с accepted/accepted_with_remarks».
    [Fact]
    public void Build_ФиналПринят_НоНезакрыт_ReadyFalse()
    {
        var epic = new TaskItem { Id = "epic-z", Role = TaskRoles.Feature };

        // Todo: вердикт accepted, но статус не Done → не готов.
        var finalTodo = new TaskItem
        {
            Id = "f1", FeatureId = "epic-z", Role = TaskRoles.Final,
            Status = TaskItemStatus.Todo,
            Verification = new TaskVerification { Verdict = TaskVerdicts.Accepted },
        };
        FeatureProjector.Build(epic, [epic, finalTodo]).Ready.Should().BeFalse(
            "финал принят, но задача финала ещё Todo — не закрыт");

        // InProgress: тоже не Done → не готов.
        var finalInProgress = new TaskItem
        {
            Id = "f2", FeatureId = "epic-z", Role = TaskRoles.Final,
            Status = TaskItemStatus.InProgress,
            Verification = new TaskVerification { Verdict = TaskVerdicts.Accepted },
        };
        FeatureProjector.Build(epic, [epic, finalInProgress]).Ready.Should().BeFalse(
            "финал принят, но задача финала InProgress — не закрыт");
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

    // MAJOR-1: финал принят (accepted), merge-задачи нет → активная стадия merge с tone = wait.
    // Тон стадии — производное от Attention (wait/«мерж»), а не отдельный расчёт.
    [Fact]
    public void Build_ФиналAcceptedБезМержа_MergeToneWait()
    {
        const string epicId = "epic-merge-tone";
        var epic = new TaskItem { Id = epicId, Role = TaskRoles.Feature };
        var step = new TaskItem { Id = "s1", FeatureId = epicId, Role = TaskRoles.Step, Status = TaskItemStatus.Done };
        var final = new TaskItem
        {
            Id = "f1", FeatureId = epicId, Role = TaskRoles.Final, Status = TaskItemStatus.Done,
            CompletedAt = new DateTime(2026, 10, 17, 12, 0, 0),
            Verification = new TaskVerification { Verdict = TaskVerdicts.Accepted },
        };

        var run = FeatureProjector.Build(epic, [epic, step, final]);

        var mergeStage = run.Stages.Single(s => s.Key == "merge");
        mergeStage.State.Should().Be("active",
            "финал принят, merge-задачи нет — мерж активен");
        mergeStage.Tone.Should().Be("wait",
            "тон активной стадии — производное от Attention (wait/«мерж»), а не run");
        run.Attention.Should().NotBeNull("финал accepted, мержа нет — Attention = wait/мерж");
        run.Attention!.Reason.Should().Be("мерж");
    }

    // MAJOR-1: stopped с устаревшей пометкой (ExecutorStoppedAt раньше ClaudeStartedAt) →
    // тон НЕ stopped (guard из Attention защищает от stale-пометки).
    [Fact]
    public void Build_StoppedStale_ТонНеStopped()
    {
        const string epicId = "epic-stale-stopped";
        var epic = new TaskItem { Id = epicId, Role = TaskRoles.Feature };
        var task = new TaskItem
        {
            Id = "s1", FeatureId = epicId, Role = TaskRoles.Step, StepRef = "Ш1",
            Status = TaskItemStatus.InProgress,
            // ClaudeStartedAt (10:00) позже ExecutorStoppedAt (9:00) — исполнитель перезапущен.
            ClaudeStartedAt = new DateTime(2026, 10, 17, 10, 0, 0),
            ExecutorStoppedAt = new DateTime(2026, 10, 17, 9, 0, 0),
            ExecutorStopReason = "auth_failed",
        };

        var run = FeatureProjector.Build(epic, [epic, task]);

        // Guard: ExecutorStoppedAt (9:00) НЕ позже ClaudeStartedAt (10:00) → stopped нет.
        // Attention = null → тон активной стадии (prod) = run, не stopped.
        var prodStage = run.Stages.Single(s => s.Key == "prod");
        prodStage.State.Should().Be("active");
        prodStage.Tone.Should().Be("run",
            "stale-пометка: ExecutorStoppedAt раньше ClaudeStartedAt — stopped не показываем");
    }

    // ─── Б2: цепочки шагов, источники возврата, линии ───────────────────────────────────────
    // Фикстура feat-step-chains.json (в духе «Истории» v2): 6 шагов, одно ревью на два шага,
    // возвраты с ревью/проверки/финала, доработка без связи, снятый шаг, идущая доработка.

    private static (TaskItem Epic, List<TaskItem> Tasks) LoadChains()
    {
        var tasks = FeatureRunFixture.Load("feat-step-chains.json");
        var epic = tasks.Single(t => t.Role == TaskRoles.Feature);
        return (epic, tasks);
    }

    // Одно ревью (sc-r1) на два шага (Ш1 и Ш2) попадает в ОБЕ цепочки;
    // Loops фичи = число УНИКАЛЬНЫХ ревью-возвратов (10), а не сумма по шагам (11).
    [Fact]
    public void Build_РевьюНаДваШага_ВОбеХЦепочках_LoopsУникальные()
    {
        var (epic, tasks) = LoadChains();
        var run = FeatureProjector.Build(epic, tasks);

        var c1 = run.StepChains.Single(c => c.StepRef == "Ш1");
        var c2 = run.StepChains.Single(c => c.StepRef == "Ш2");

        // sc-r1 — в витках обеих цепочек.
        c1.Rounds.Select(r => r.ReviewTaskId).Should().Contain("sc-r1",
            "ревью на два шага — в цепочке Ш1");
        c2.Rounds.Select(r => r.ReviewTaskId).Should().Contain("sc-r1",
            "ревью на два шага — в цепочке Ш2");

        // Loops фичи = 10 уникальных возвратов (7 ревью + 2 проверки + 1 финал),
        // НЕ сумма по шагам (11): sc-r1 учтён один раз, а не дважды.
        run.Verdicts.Loops.Should().Be(10,
            "Loops фичи считает уникальные ревью/проверки/финалы, не сумму по шагам");
    }

    // Источник возврата: доработка после проверки Веры → qa; после ревью Глеба → review;
    // после финала → final; без связи ReviewOf → null.
    [Fact]
    public void Build_ИсточникВозврата_ПоСвязи()
    {
        var (epic, tasks) = LoadChains();
        var run = FeatureProjector.Build(epic, tasks);

        run.StepChains.Single(c => c.StepRef == "Ш1").Source.Should().Be("review",
            "Ш1: доработка по ревью Глеба");
        run.StepChains.Single(c => c.StepRef == "Ш3").Source.Should().Be("qa",
            "Ш3: доработка по проверке Веры");
        run.StepChains.Single(c => c.StepRef == "Ш5").Source.Should().Be("final",
            "Ш5: доработка по финалу");
        run.StepChains.Single(c => c.StepRef == "Ш4").Source.Should().BeNull(
            "Ш4: доработка без связи ReviewOf — метки нет");
    }

    // Три возврата и решение о снятии → outcome=withdrawn, status=снят с выкладки.
    [Fact]
    public void Build_ТриВозвратаИСнятие_Withdrawn()
    {
        var (epic, tasks) = LoadChains();
        var run = FeatureProjector.Build(epic, tasks);

        var c6 = run.StepChains.Single(c => c.StepRef == "Ш6");
        c6.Outcome.Should().Be("withdrawn");
        c6.Status.Should().Be("снят с выкладки");
        // Три витка-возврата (sc-r6a/b/c) + решение о снятии (sc-r6w, withdrawn).
        c6.Loops.Should().Be(3, "три возврата по шагу");
        c6.Rounds.Should().HaveCount(4);
        // Порядок: три возврата, затем решение о снятии (withdrawn) — последнее.
        c6.Rounds.Select(r => r.Verdict).Should().Equal(
            "returned", "returned", "returned", "withdrawn");
    }

    // verdict витка — строго из пяти разрешённых значений (тест перебирает ВСЕ цепочки фикстуры).
    [Fact]
    public void Build_ВердиктыВитков_ТолькоРазрешённые()
    {
        var (epic, tasks) = LoadChains();
        var run = FeatureProjector.Build(epic, tasks);

        var allowed = new HashSet<string> { "returned", "accepted_with_remarks", "accepted", "withdrawn", "running" };
        foreach (var chain in run.StepChains)
            foreach (var round in chain.Rounds)
                allowed.Contains(round.Verdict).Should().BeTrue(
                    $"вердикт витка «{round.Verdict}» (шаг {chain.StepRef}) из разрешённого набора");
    }

    // Линия: два исполнителя в одной линии (бэк: Денис и Кира) → оба в personaIds.
    [Fact]
    public void Build_Линия_ДваИсполнителя()
    {
        var (epic, tasks) = LoadChains();
        var run = FeatureProjector.Build(epic, tasks);

        var back = run.Lanes.Single(l => l.Lane == "back");
        back.PersonaIds.Should().BeEquivalentTo(new[] { "Денис", "Кира" },
            "оба исполнителя линии бэк — в personaIds");
        back.Total.Should().Be(4, "бэк: 4 шага (Ш1, Ш2, Ш5, Ш6)");
        var front = run.Lanes.Single(l => l.Lane == "front");
        front.PersonaIds.Should().ContainSingle().Which.Should().Be("Кира");
        front.Total.Should().Be(2, "фронт: 2 шага (Ш3, Ш4)");
    }

    // ReturnsBySource.review.open > 0, пока доработка (sc-rw-open) не закрыта.
    [Fact]
    public void Build_ВозвратыПоИсточникам_ОткрытыйРевью()
    {
        var (epic, tasks) = LoadChains();
        var run = FeatureProjector.Build(epic, tasks);

        run.ReturnsBySource.Review.Count.Should().Be(7,
            "7 уникальных ревью-возвратов (sc-r1, r2, r4, r5, r6a, r6b, r6c)");
        run.ReturnsBySource.Review.Open.Should().Be(1,
            "sc-r5: доработка sc-rw-open ещё идёт → открыт");
        run.ReturnsBySource.Qa.Count.Should().Be(2);
        run.ReturnsBySource.Qa.Open.Should().Be(0, "доработки по проверкам закрыты");
        run.ReturnsBySource.Final.Count.Should().Be(1);
        run.ReturnsBySource.Final.Open.Should().Be(0);
    }

    // Мутация (а): связь шага с ревью идёт ТОЛЬКО через пересечение ReviewOf.
    // Сломал: цепляем витки без пересечения (всегда true) → sc-r2 (только про sc-s3)
    // ошибочно попадает в цепочку Ш1. Без мутации тест зелёный.
    [Fact]
    public void Build_Мутация_СвязьБезПересеченияReviewOf()
    {
        var (epic, tasks) = LoadChains();
        var run = FeatureProjector.Build(epic, tasks);

        var c1 = run.StepChains.Single(c => c.StepRef == "Ш1");
        // sc-r2 ревьюит только sc-s3 (Ш3) — в цепочку Ш1 не попадает.
        c1.Rounds.Select(r => r.ReviewTaskId).Should().NotContain("sc-r2",
            "ревью без пересечения ReviewOf с задачами шага не цепляется");
        // Но sc-r1 (ревьюит sc-s1 и sc-s2) — в цепочке.
        c1.Rounds.Select(r => r.ReviewTaskId).Should().Contain("sc-r1");
    }

    // Мутация (б): Loops фичи НЕ сумма по шагам. Сломал: Loops = сумма loops цепочек (11)
    // вместо уникальных (10) → тест краснеет. Без мутации зелёный.
    [Fact]
    public void Build_Мутация_LoopsНеСуммаПоШагам()
    {
        var (epic, tasks) = LoadChains();
        var run = FeatureProjector.Build(epic, tasks);

        int sumPerStep = run.StepChains.Sum(c => c.Loops);
        sumPerStep.Should().Be(11, "сумма loops по шагам: sc-r1 учтён в Ш1 и Ш2");
        run.Verdicts.Loops.Should().Be(10,
            "Loops фичи = уникальные возвраты (10), не сумма по шагам (11)");
        run.Verdicts.Loops.Should().NotBe(sumPerStep);
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

    // ─── Б3: «Сейчас», тревоги, итог, порядок ───────────────────────────────────────────────

    // Две идущие задачи → Now из двух, порядок по убыванию ClaudeStartedAt.
    [Fact]
    public void Build_ДвеИдущие_NowДваПоУбываниюClaudeStartedAt()
    {
        const string epicId = "epic-now-test";
        var epic = new TaskItem { Id = epicId, Role = TaskRoles.Feature };
        var t1 = new TaskItem
        {
            Id = "t1", FeatureId = epicId, Role = TaskRoles.Step, StepRef = "Ш1",
            Status = TaskItemStatus.InProgress,
            ClaudeStartedAt = new DateTime(2026, 10, 17, 10, 0, 0),
            PersonaId = "Денис", Title = "шаг 1", LinkedSessionId = "sess-1",
        };
        var t2 = new TaskItem
        {
            Id = "t2", FeatureId = epicId, Role = TaskRoles.Step, StepRef = "Ш2",
            Status = TaskItemStatus.InProgress,
            ClaudeStartedAt = new DateTime(2026, 10, 17, 11, 0, 0), // позже t1
            PersonaId = "Кира", Title = "шаг 2", LinkedSessionId = "sess-2",
        };

        var run = FeatureProjector.Build(epic, [epic, t1, t2]);

        run.Now.Should().HaveCount(2, "две InProgress-задачи — два элемента Now");
        // Порядок по убыванию ClaudeStartedAt: t2 (11:00) перед t1 (10:00).
        run.Now[0].TaskId.Should().Be("t2");
        run.Now[1].TaskId.Should().Be("t1");
        run.Now[0].StartedAt.Should().Be(new DateTime(2026, 10, 17, 11, 0, 0));
        run.Now[1].StartedAt.Should().Be(new DateTime(2026, 10, 17, 10, 0, 0));
        // Поля элемента.
        run.Now[0].PersonaId.Should().Be("Кира");
        run.Now[0].StepRef.Should().Be("Ш2");
        run.Now[0].Title.Should().Be("шаг 2");
        run.Now[0].LinkedSessionId.Should().Be("sess-2");
        run.Now[0].Stage.Should().Be("prod");
    }

    // У закрытой фичи Now пуст (все задачи Done).
    [Fact]
    public void Build_ЗакрытаяФича_NowПуст()
    {
        const string epicId = "epic-closed-now";
        var epic = new TaskItem { Id = epicId, Role = TaskRoles.Feature, Status = TaskItemStatus.Done };
        var step = new TaskItem
        {
            Id = "s1", FeatureId = epicId, Role = TaskRoles.Step, StepRef = "Ш1",
            Status = TaskItemStatus.Done,
        };

        var run = FeatureProjector.Build(epic, [epic, step]);

        run.Now.Should().BeEmpty("у закрытой фичи ни одна задача не InProgress");
    }

    // m1: идущий эпик (контейнер) + идущая задача шага → Now содержит только задачу шага.
    // Эпик — контейнер фичи, не стадия конвейера: в «Сейчас» не показывается.
    [Fact]
    public void Build_ИдущийЭпик_НеВNow()
    {
        const string epicId = "epic-epic-now";
        // Эпик сам идёт (InProgress) — но это контейнер, в Now не попадает.
        var epic = new TaskItem
        {
            Id = epicId, Role = TaskRoles.Feature,
            Status = TaskItemStatus.InProgress,
            ClaudeStartedAt = new DateTime(2026, 10, 17, 10, 0, 0),
        };
        var step = new TaskItem
        {
            Id = "s1", FeatureId = epicId, Role = TaskRoles.Step, StepRef = "Ш1",
            Status = TaskItemStatus.InProgress,
            ClaudeStartedAt = new DateTime(2026, 10, 17, 11, 0, 0),
            PersonaId = "Денис", Title = "шаг 1",
        };

        var run = FeatureProjector.Build(epic, [epic, step]);

        run.Now.Should().HaveCount(1, "идущий эпик — контейнер, в Now не попадает");
        run.Now[0].TaskId.Should().Be("s1");
        run.Now[0].Stage.Should().Be("prod");
    }

    // Финал accepted, merge-задачи нет → Attention = wait / "мерж", since = CompletedAt финала.
    [Fact]
    public void Build_ФиналAcceptedБезМержа_AttentionWaitМерж()
    {
        const string epicId = "epic-merge-wait";
        var epic = new TaskItem { Id = epicId, Role = TaskRoles.Feature };
        var final = new TaskItem
        {
            Id = "f1", FeatureId = epicId, Role = TaskRoles.Final,
            Status = TaskItemStatus.Done,
            CompletedAt = new DateTime(2026, 10, 17, 12, 0, 0),
            Verification = new TaskVerification { Verdict = TaskVerdicts.Accepted },
            PersonaId = "Александр",
        };

        var run = FeatureProjector.Build(epic, [epic, final]);

        run.Attention.Should().NotBeNull("финал принят, мержа нет — ждём мержа");
        run.Attention!.Kind.Should().Be("wait");
        run.Attention.Reason.Should().Be("мерж");
        run.Attention.Since.Should().Be(new DateTime(2026, 10, 17, 12, 0, 0));
        run.Attention.TaskId.Should().Be("f1");
    }

    // Остановленный исполнитель И явный wait у той же фичи → Attention.kind = stopped (приоритет).
    [Fact]
    public void Build_ОстановленныйИЯвныйWait_StoppedПриоритет()
    {
        const string epicId = "epic-stopped-wait";
        var epic = new TaskItem
        {
            Id = epicId, Role = TaskRoles.Feature,
            AwaitingHumanSince = new DateTime(2026, 10, 17, 9, 0, 0),
            AwaitingHumanReason = "ждём решения",
        };
        var stopped = new TaskItem
        {
            Id = "s1", FeatureId = epicId, Role = TaskRoles.Step, StepRef = "Ш1",
            Status = TaskItemStatus.InProgress,
            ClaudeStartedAt = new DateTime(2026, 10, 17, 8, 0, 0),
            ExecutorStoppedAt = new DateTime(2026, 10, 17, 8, 30, 0), // позже ClaudeStartedAt
            ExecutorStopReason = "auth_failed",
            PersonaId = "Денис",
        };

        var run = FeatureProjector.Build(epic, [epic, stopped]);

        run.Attention.Should().NotBeNull();
        run.Attention!.Kind.Should().Be("stopped", "stopped приоритетнее явного wait");
        run.Attention.Reason.Should().Be("auth_failed");
        run.Attention.Since.Should().Be(new DateTime(2026, 10, 17, 8, 30, 0));
        run.Attention.TaskId.Should().Be("s1");
    }

    // Явный wait приоритетнее вычисленного «мерж».
    [Fact]
    public void Build_ЯвныйWait_ПриоритетнееВычисленногоМержа()
    {
        const string epicId = "epic-wait-merge";
        var epic = new TaskItem
        {
            Id = epicId, Role = TaskRoles.Feature,
            AwaitingHumanSince = new DateTime(2026, 10, 17, 9, 0, 0),
            AwaitingHumanReason = "ждём решения",
        };
        var final = new TaskItem
        {
            Id = "f1", FeatureId = epicId, Role = TaskRoles.Final,
            Status = TaskItemStatus.Done,
            CompletedAt = new DateTime(2026, 10, 17, 12, 0, 0),
            Verification = new TaskVerification { Verdict = TaskVerdicts.Accepted },
        };

        var run = FeatureProjector.Build(epic, [epic, final]);

        // Явный wait (AwaitingHumanSince) приоритетнее вычисленного «мерж».
        run.Attention.Should().NotBeNull();
        run.Attention!.Kind.Should().Be("wait");
        run.Attention.Reason.Should().Be("ждём решения", "явный wait, а не вычисленный «мерж»");
        run.Attention.Since.Should().Be(new DateTime(2026, 10, 17, 9, 0, 0));
    }

    // Перезапущенная задача (ClaudeStartedAt позже ExecutorStoppedAt) → stopped нет.
    [Fact]
    public void Build_Перезапущенная_StoppedНет()
    {
        const string epicId = "epic-rerun";
        var epic = new TaskItem { Id = epicId, Role = TaskRoles.Feature };
        var task = new TaskItem
        {
            Id = "s1", FeatureId = epicId, Role = TaskRoles.Step, StepRef = "Ш1",
            Status = TaskItemStatus.InProgress,
            // ClaudeStartedAt (10:00) позже ExecutorStoppedAt (9:00) — исполнитель перезапущен.
            ClaudeStartedAt = new DateTime(2026, 10, 17, 10, 0, 0),
            ExecutorStoppedAt = new DateTime(2026, 10, 17, 9, 0, 0),
            ExecutorStopReason = "auth_failed",
        };

        var run = FeatureProjector.Build(epic, [epic, task]);

        // Guard: ExecutorStoppedAt (9:00) НЕ позже ClaudeStartedAt (10:00) → stopped не показываем.
        run.Attention.Should().BeNull(
            "перезапущенная задача: ExecutorStoppedAt раньше ClaudeStartedAt — stale-пометка, stopped нет");
    }

    // Счётчик шагов: StepsAccepted (outcome=accepted) / StepsTotal (все цепочки).
    [Fact]
    public void Build_СчётчикШагов_ПринятыеИВсе()
    {
        const string epicId = "epic-steps-count";
        var epic = new TaskItem { Id = epicId, Role = TaskRoles.Feature };
        var s1 = new TaskItem
        {
            Id = "s1", FeatureId = epicId, Role = TaskRoles.Step, StepRef = "Ш1",
            Status = TaskItemStatus.Done,
        };
        var r1 = new TaskItem
        {
            Id = "r1", FeatureId = epicId, Role = TaskRoles.Review,
            Status = TaskItemStatus.Done,
            Verification = new TaskVerification { Verdict = TaskVerdicts.Accepted },
            ReviewOf = ["s1"],
        };
        var s2 = new TaskItem
        {
            Id = "s2", FeatureId = epicId, Role = TaskRoles.Step, StepRef = "Ш2",
            Status = TaskItemStatus.InProgress, // ещё не принят
        };

        var run = FeatureProjector.Build(epic, [epic, s1, r1, s2]);

        run.StepsTotal.Should().Be(2, "две цепочки шагов");
        run.StepsAccepted.Should().Be(1, "Ш1 принят (accepted), Ш2 ещё идёт");
    }

    // Summary: есть у закрытой фичи, null у открытой.
    [Fact]
    public void Build_Summary_ЗакрытаяИОткрытая()
    {
        // Открытая фича: Summary null.
        var epicOpen = new TaskItem { Id = "epic-open", Role = TaskRoles.Feature };
        var stepOpen = new TaskItem
        {
            Id = "s1", FeatureId = "epic-open", Role = TaskRoles.Step, StepRef = "Ш1",
            Status = TaskItemStatus.InProgress,
        };
        var runOpen = FeatureProjector.Build(epicOpen, [epicOpen, stepOpen]);
        runOpen.Closed.Should().BeFalse();
        runOpen.Summary.Should().BeNull("у открытой фичи Summary null");

        // Закрытая фича (эпик Done): Summary есть.
        var epicClosed = new TaskItem { Id = "epic-closed", Role = TaskRoles.Feature, Status = TaskItemStatus.Done };
        var stepClosed = new TaskItem
        {
            Id = "s2", FeatureId = "epic-closed", Role = TaskRoles.Step, StepRef = "Ш1",
            Status = TaskItemStatus.Done,
            ClaudeStartedAt = new DateTime(2026, 10, 17, 8, 0, 0),
            CompletedAt = new DateTime(2026, 10, 17, 10, 0, 0),
        };
        var runClosed = FeatureProjector.Build(epicClosed, [epicClosed, stepClosed]);
        runClosed.Closed.Should().BeTrue();
        runClosed.Summary.Should().NotBeNull("у закрытой фичи Summary есть");
        runClosed.Summary!.Tasks.Should().Be(2, "эпик + 1 шаг");
        runClosed.Summary.Steps.Should().Be(1, "1 цепочка шагов");
        runClosed.Summary.DurationMinutes.Should().Be(120, "2 часа от старта до закрытия");
    }

    // ─── n4: mergedAt только у завершённой merge-задачи ──────────────────────────────────────

    // Эпик закрыт (Done) БЕЗ merge-задачи → mergedAt = null (фронт: «закрыта», не «смержена»).
    // Дефект n4: раньше сюда падал CompletedAt эпика → закрытая без мержа показывалась
    // «смержена <дата закрытия эпика>».
    [Fact]
    public void Build_ЗакрытаБезМержа_MergedAtNull()
    {
        const string epicId = "epic-closed-no-merge";
        var epic = new TaskItem
        {
            Id = epicId, Role = TaskRoles.Feature,
            Status = TaskItemStatus.Done,
            CompletedAt = new DateTime(2026, 10, 18, 12, 0, 0),
        };
        var step = new TaskItem
        {
            Id = "s1", FeatureId = epicId, Role = TaskRoles.Step, StepRef = "Ш1",
            Status = TaskItemStatus.Done,
            ClaudeStartedAt = new DateTime(2026, 10, 17, 8, 0, 0),
            CompletedAt = new DateTime(2026, 10, 17, 10, 0, 0),
        };

        var run = FeatureProjector.Build(epic, [epic, step]);

        run.Closed.Should().BeTrue("эпик Done — фича закрыта");
        run.Summary.Should().NotBeNull("у закрытой фичи Summary есть");
        run.Summary!.MergedAt.Should().BeNull(
            "закрыта эпиком БЕЗ merge-задачи → mergedAt = null (не «смержена <дата эпика>»)");
    }

    // Эпик закрыт (Done) С завершённой merge-задачей → mergedAt = CompletedAt merge-задачи (как прежде).
    [Fact]
    public void Build_ЗакрытаСМержем_MergedAtКакПрежде()
    {
        const string epicId = "epic-closed-with-merge";
        var epic = new TaskItem
        {
            Id = epicId, Role = TaskRoles.Feature,
            Status = TaskItemStatus.Done,
            CompletedAt = new DateTime(2026, 10, 18, 12, 0, 0),
        };
        var merge = new TaskItem
        {
            Id = "m1", FeatureId = epicId, Role = TaskRoles.Merge,
            Status = TaskItemStatus.Done,
            CompletedAt = new DateTime(2026, 10, 18, 15, 0, 0),
        };

        var run = FeatureProjector.Build(epic, [epic, merge]);

        run.Closed.Should().BeTrue();
        run.Summary!.MergedAt.Should().Be(new DateTime(2026, 10, 18, 15, 0, 0),
            "merge-задача Done → mergedAt = CompletedAt merge-задачи (не CompletedAt эпика)");
    }

    // Мутация к условию: вернуть старое поведение (mergedAt = epic.CompletedAt, когда мержа нет).
    // Сломал: `: null` → `: epic.CompletedAt` → тест «ЗакрытаБезМержа_MergedAtNull» краснеет
    // (mergedAt = CompletedAt эпика ≠ null). Без мутации зелёный.
    [Fact]
    public void Build_Мутация_ЗакрытаБезМержа_MergedAtNull()
    {
        const string epicId = "epic-mut-no-merge";
        var epic = new TaskItem
        {
            Id = epicId, Role = TaskRoles.Feature,
            Status = TaskItemStatus.Done,
            CompletedAt = new DateTime(2026, 10, 18, 12, 0, 0),
        };
        var run = FeatureProjector.Build(epic, [epic]);

        // Мутация: если mergedAt = epic.CompletedAt (старое) → 12:00 ≠ null → краснеет.
        run.Summary!.MergedAt.Should().BeNull(
            "закрыта эпиком без мержа → mergedAt = null (мутация: epic.CompletedAt красит)");
    }

    // ─── Б3: порядок фич (правило v2) ────────────────────────────────────────────────────────

    // Порядок на трёх фичах (ждёт · идёт · спокойная) + закрытая — последней.
    [Fact]
    public void OrderRuns_ТриФичи_ПорядокПоПравилу()
    {
        // Фича A: ждёт (Attention, since 10:00) — первая.
        var epicA = new TaskItem { Id = "epic-a", Role = TaskRoles.Feature };
        var waitTaskA = new TaskItem
        {
            Id = "a1", FeatureId = "epic-a", Role = TaskRoles.Step, StepRef = "Ш1",
            Status = TaskItemStatus.InProgress,
            AwaitingHumanSince = new DateTime(2026, 10, 17, 10, 0, 0),
            AwaitingHumanReason = "ждём",
        };
        var runA = FeatureProjector.Build(epicA, [epicA, waitTaskA]);

        // Фича B: идёт (Now, ClaudeStartedAt 11:00) — вторая.
        var epicB = new TaskItem { Id = "epic-b", Role = TaskRoles.Feature };
        var runTaskB = new TaskItem
        {
            Id = "b1", FeatureId = "epic-b", Role = TaskRoles.Step, StepRef = "Ш1",
            Status = TaskItemStatus.InProgress,
            ClaudeStartedAt = new DateTime(2026, 10, 17, 11, 0, 0),
        };
        var runB = FeatureProjector.Build(epicB, [epicB, runTaskB]);

        // Фича C: спокойная (без Attention, не идёт) — третья.
        var epicC = new TaskItem { Id = "epic-c", Role = TaskRoles.Feature };
        var calmTaskC = new TaskItem
        {
            Id = "c1", FeatureId = "epic-c", Role = TaskRoles.Step, StepRef = "Ш1",
            Status = TaskItemStatus.Todo,
        };
        var runC = FeatureProjector.Build(epicC, [epicC, calmTaskC]);

        // Фича D: закрытая — последняя.
        var epicD = new TaskItem { Id = "epic-d", Role = TaskRoles.Feature, Status = TaskItemStatus.Done };
        var runD = FeatureProjector.Build(epicD, [epicD]);

        var ordered = FeatureProjector.OrderRuns([runA, runB, runC, runD]);

        ordered.Select(r => r.FeatureId).Should().Equal(
            new[] { "epic-a", "epic-b", "epic-c", "epic-d" },
            "1) ждёт (A) → 2) идёт (B) → 3) спокойная (C) → 4) закрытая (D)");
    }

    // Монотонность (план С0 §2): features[] в индексе идёт строго по группам
    // attention → running → idle → closed. OrderRuns группирует по Bucket (единая точка
    // правды), порядок групп фиксирован. Вход перемешан (не в порядке групп) — вывод обязан
    // быть строго по группам. Мутация: OrderRuns расходитcя с Bucket (группа выбирается
    // собственным условием, а не Bucket) → порядок групп в выводе ломается → тест краснеет.
    [Fact]
    public void OrderRuns_Монотонность_СтрогоПоГруппамBucket()
    {
        var t = new DateTime(2026, 10, 17);

        // attention: InProgress-задача с AwaitingHumanSince (Attention «wait», не closed).
        var epicA = new TaskItem { Id = "epic-a", Role = TaskRoles.Feature };
        var waitA = new TaskItem
        {
            Id = "a1", FeatureId = "epic-a", Role = TaskRoles.Step, StepRef = "Ш1",
            Status = TaskItemStatus.InProgress,
            AwaitingHumanSince = t.AddHours(8),
        };
        var runA = FeatureProjector.Build(epicA, [epicA, waitA]);

        // running: InProgress, без Attention.
        var epicR = new TaskItem { Id = "epic-r", Role = TaskRoles.Feature };
        var runR = new TaskItem
        {
            Id = "r1", FeatureId = "epic-r", Role = TaskRoles.Step, StepRef = "Ш1",
            Status = TaskItemStatus.InProgress,
            ClaudeStartedAt = t.AddHours(9),
        };
        var runRunning = FeatureProjector.Build(epicR, [epicR, runR]);

        // idle: только Todo, без Attention, не идёт.
        var epicI = new TaskItem { Id = "epic-i", Role = TaskRoles.Feature };
        var idleI = new TaskItem
        {
            Id = "i1", FeatureId = "epic-i", Role = TaskRoles.Step, StepRef = "Ш1",
            Status = TaskItemStatus.Todo,
        };
        var runI = FeatureProjector.Build(epicI, [epicI, idleI]);

        // closed: эпик Done.
        var epicC = new TaskItem { Id = "epic-c", Role = TaskRoles.Feature, Status = TaskItemStatus.Done };
        var runC = FeatureProjector.Build(epicC, [epicC]);

        // Sanity: четыре фичи — в четырёх разных bucket (иначе тест тривиален).
        FeatureProjector.Bucket(runA).Should().Be("attention");
        FeatureProjector.Bucket(runRunning).Should().Be("running");
        FeatureProjector.Bucket(runI).Should().Be("idle");
        FeatureProjector.Bucket(runC).Should().Be("closed");

        // Вход перемешан (не в порядке групп) — вывод строго по группам.
        var shuffled = new List<FeatureRun> { runI, runC, runRunning, runA };
        var ordered = FeatureProjector.OrderRuns(shuffled);

        ordered.Select(r => r.FeatureId).Should().Equal(
            new[] { "epic-a", "epic-r", "epic-i", "epic-c" },
            "features[] идёт строго по группам attention → running → idle → closed");
    }

    // Мутация (а): Now берёт одну задачу вместо списка.
    // Сломал: Now = [первая InProgress] вместо списка → тест «две идущие» краснеет (HaveCount(1) ≠ 2).
    [Fact]
    public void Build_Мутация_NowСписокНеОдна()
    {
        const string epicId = "epic-mut-now";
        var epic = new TaskItem { Id = epicId, Role = TaskRoles.Feature };
        var t1 = new TaskItem
        {
            Id = "t1", FeatureId = epicId, Role = TaskRoles.Step, StepRef = "Ш1",
            Status = TaskItemStatus.InProgress,
            ClaudeStartedAt = new DateTime(2026, 10, 17, 10, 0, 0),
        };
        var t2 = new TaskItem
        {
            Id = "t2", FeatureId = epicId, Role = TaskRoles.Step, StepRef = "Ш2",
            Status = TaskItemStatus.InProgress,
            ClaudeStartedAt = new DateTime(2026, 10, 17, 11, 0, 0),
        };

        var run = FeatureProjector.Build(epic, [epic, t1, t2]);

        // Мутация: если Now = [первая InProgress] (одна) → HaveCount(1) ≠ 2 → краснеет.
        run.Now.Should().HaveCount(2, "Now — список всех InProgress, не одна задача");
    }

    // Мутация (б): приоритет stopped ниже wait.
    // Сломал: wait явный проверяется ПЕРЕД stopped → тест «stopped + wait → stopped» краснеет.
    [Fact]
    public void Build_Мутация_StoppedВышеWait()
    {
        const string epicId = "epic-mut-stopped";
        var epic = new TaskItem
        {
            Id = epicId, Role = TaskRoles.Feature,
            AwaitingHumanSince = new DateTime(2026, 10, 17, 9, 0, 0),
            AwaitingHumanReason = "ждём",
        };
        var stopped = new TaskItem
        {
            Id = "s1", FeatureId = epicId, Role = TaskRoles.Step, StepRef = "Ш1",
            Status = TaskItemStatus.InProgress,
            ClaudeStartedAt = new DateTime(2026, 10, 17, 8, 0, 0),
            ExecutorStoppedAt = new DateTime(2026, 10, 17, 8, 30, 0),
            ExecutorStopReason = "auth_failed",
        };

        var run = FeatureProjector.Build(epic, [epic, stopped]);

        // Мутация: если wait проверяется перед stopped → Kind="wait", а не "stopped" → краснеет.
        run.Attention!.Kind.Should().Be("stopped", "stopped приоритетнее wait");
    }

    // Мутация (в): убрать вычисление «мерж».
    // Сломал: Attention = null (без вычисления мержа) → тест «финал accepted, мержа нет» краснеет.
    [Fact]
    public void Build_Мутация_МержВычисляется()
    {
        const string epicId = "epic-mut-merge";
        var epic = new TaskItem { Id = epicId, Role = TaskRoles.Feature };
        var final = new TaskItem
        {
            Id = "f1", FeatureId = epicId, Role = TaskRoles.Final,
            Status = TaskItemStatus.Done,
            CompletedAt = new DateTime(2026, 10, 17, 12, 0, 0),
            Verification = new TaskVerification { Verdict = TaskVerdicts.Accepted },
        };

        var run = FeatureProjector.Build(epic, [epic, final]);

        // Мутация: если вычисление «мерж» убрано → Attention=null → краснеет.
        run.Attention.Should().NotBeNull("финал accepted, мержа нет — Attention = wait/мерж");
        run.Attention!.Reason.Should().Be("мерж");
    }

    // ─── Б5: исполнители, число задач и исход у каждой стадии ────────────────────────────────

    // Стадия конвейера по ключу.
    private static FeatureStage Stage(FeatureRun run, string key) => run.Stages.Single(s => s.Key == key);

    // Два исполнителя на prod (Денис, Кира) → оба в PersonaIds в порядке появления;
    // пустой PersonaId не попадает; TaskCount = число задач стадии.
    [Fact]
    public void Build_Prod_ДваИсполнителя_PersonaIdsИTaskCount()
    {
        const string epicId = "epic-b5-prod";
        var epic = new TaskItem { Id = epicId, Role = TaskRoles.Feature };
        var s1 = new TaskItem
        {
            Id = "s1", FeatureId = epicId, Role = TaskRoles.Step, StepRef = "Ш1",
            Status = TaskItemStatus.Done, PersonaId = "Денис",
        };
        var s2 = new TaskItem
        {
            Id = "s2", FeatureId = epicId, Role = TaskRoles.Step, StepRef = "Ш2",
            Status = TaskItemStatus.Done, PersonaId = "Кира",
        };
        var s3 = new TaskItem
        {
            Id = "s3", FeatureId = epicId, Role = TaskRoles.Step, StepRef = "Ш3",
            Status = TaskItemStatus.Done, PersonaId = "", // пустой — не попадает
        };

        var run = FeatureProjector.Build(epic, [epic, s1, s2, s3]);

        var prod = Stage(run, "prod");
        prod.PersonaIds.Should().Equal(new[] { "Денис", "Кира" },
            "оба исполнителя prod в порядке появления; пустой PersonaId не попадает");
        prod.TaskCount.Should().Be(3, "prod: 3 задачи");
    }

    // Ревью: TaskCount = 2 (два ревью); PersonaIds — ревьюеры;
    // ревью вернуло (r1, 10:00), затем приняло (r2, 11:00) → Verdict = accepted.
    [Fact]
    public void Build_Review_ДваРевью_TaskCount2_ВернулоЗатемПриняло()
    {
        const string epicId = "epic-b5-review";
        var epic = new TaskItem { Id = epicId, Role = TaskRoles.Feature };
        var r1 = new TaskItem
        {
            Id = "r1", FeatureId = epicId, Role = TaskRoles.Review,
            Status = TaskItemStatus.Done, PersonaId = "Глеб",
            Verification = new TaskVerification { Verdict = TaskVerdicts.Returned },
            CompletedAt = new DateTime(2026, 10, 17, 10, 0, 0),
        };
        var r2 = new TaskItem
        {
            Id = "r2", FeatureId = epicId, Role = TaskRoles.Review,
            Status = TaskItemStatus.Done, PersonaId = "Глеб",
            Verification = new TaskVerification { Verdict = TaskVerdicts.Accepted },
            CompletedAt = new DateTime(2026, 10, 17, 11, 0, 0), // позже r1
        };

        var run = FeatureProjector.Build(epic, [epic, r1, r2]);

        var review = Stage(run, "review");
        review.TaskCount.Should().Be(2, "ревью: 2 задачи");
        review.PersonaIds.Should().Equal(new[] { "Глеб" }, "уникальные ревьюеры");
        review.Verdict.Should().Be("accepted",
            "последнее по времени ревью (r2, 11:00) приняло — исход = accepted");
    }

    // Последнее по времени ревью вернуло → Verdict = returned (а не accepted первого).
    [Fact]
    public void Build_Review_ПоследнееВернуло_Returned()
    {
        const string epicId = "epic-b5-review-ret";
        var epic = new TaskItem { Id = epicId, Role = TaskRoles.Feature };
        var r1 = new TaskItem
        {
            Id = "r1", FeatureId = epicId, Role = TaskRoles.Review,
            Status = TaskItemStatus.Done, PersonaId = "Глеб",
            Verification = new TaskVerification { Verdict = TaskVerdicts.Accepted },
            CompletedAt = new DateTime(2026, 10, 17, 10, 0, 0),
        };
        var r2 = new TaskItem
        {
            Id = "r2", FeatureId = epicId, Role = TaskRoles.Review,
            Status = TaskItemStatus.Done, PersonaId = "Глеб",
            Verification = new TaskVerification { Verdict = TaskVerdicts.Returned },
            CompletedAt = new DateTime(2026, 10, 17, 11, 0, 0), // позже r1
        };

        var run = FeatureProjector.Build(epic, [epic, r1, r2]);

        Stage(run, "review").Verdict.Should().Be("returned",
            "последнее по времени ревью (r2) вернуло — исход = returned");
    }

    // Идущая задача стадии (InProgress) → Verdict = running (даже если раньше было принято).
    [Fact]
    public void Build_Review_Идёт_Running()
    {
        const string epicId = "epic-b5-review-run";
        var epic = new TaskItem { Id = epicId, Role = TaskRoles.Feature };
        var r1 = new TaskItem
        {
            Id = "r1", FeatureId = epicId, Role = TaskRoles.Review,
            Status = TaskItemStatus.Done, PersonaId = "Глеб",
            Verification = new TaskVerification { Verdict = TaskVerdicts.Accepted },
            CompletedAt = new DateTime(2026, 10, 17, 10, 0, 0),
        };
        var r2 = new TaskItem
        {
            Id = "r2", FeatureId = epicId, Role = TaskRoles.Review,
            Status = TaskItemStatus.InProgress, PersonaId = "Глеб",
        };

        var run = FeatureProjector.Build(epic, [epic, r1, r2]);

        Stage(run, "review").Verdict.Should().Be("running", "идущая задача стадии → running");
    }

    // Стадия без задач → PersonaIds пусто, TaskCount = 0, Verdict = null (все 5 таких стадий).
    [Fact]
    public void Build_СтадияБезЗадач_Пусто()
    {
        const string epicId = "epic-b5-empty";
        var epic = new TaskItem { Id = epicId, Role = TaskRoles.Feature };
        var step = new TaskItem
        {
            Id = "s1", FeatureId = epicId, Role = TaskRoles.Step, StepRef = "Ш1",
            Status = TaskItemStatus.Done, PersonaId = "Денис",
        };

        var run = FeatureProjector.Build(epic, [epic, step]);

        foreach (var key in new[] { "cut", "plan", "qa", "final", "merge" })
        {
            var st = Stage(run, key);
            st.PersonaIds.Should().BeEmpty($"{key}: задач нет");
            st.TaskCount.Should().Be(0, $"{key}: задач нет");
            st.Verdict.Should().BeNull($"{key}: задач нет — исхода нет");
        }
    }

    // Мутация (Б5): исход стадии берётся НЕ последней, а ПЕРВОЙ по времени закрытой задачей.
    // Сломал: в StageVerdict `OrderByDescending` → `OrderBy` (первая по CompletedAt) →
    // тест «вернуло, затем приняло → accepted» краснеет (будет returned). Без мутации зелёный.
    [Fact]
    public void Build_Мутация_ВердиктПоследнейНеПервой()
    {
        const string epicId = "epic-b5-mut";
        var epic = new TaskItem { Id = epicId, Role = TaskRoles.Feature };
        // r1 (10:00) вернуло, r2 (11:00) приняло.
        var r1 = new TaskItem
        {
            Id = "r1", FeatureId = epicId, Role = TaskRoles.Review,
            Status = TaskItemStatus.Done, PersonaId = "Глеб",
            Verification = new TaskVerification { Verdict = TaskVerdicts.Returned },
            CompletedAt = new DateTime(2026, 10, 17, 10, 0, 0),
        };
        var r2 = new TaskItem
        {
            Id = "r2", FeatureId = epicId, Role = TaskRoles.Review,
            Status = TaskItemStatus.Done, PersonaId = "Глеб",
            Verification = new TaskVerification { Verdict = TaskVerdicts.Accepted },
            CompletedAt = new DateTime(2026, 10, 17, 11, 0, 0),
        };

        var run = FeatureProjector.Build(epic, [epic, r1, r2]);

        // Вердикт — последней (r2, 11:00) = accepted, НЕ первой (r1, 10:00) = returned.
        // Мутация (первая по CompletedAt) даст returned → краснеет.
        Stage(run, "review").Verdict.Should().Be("accepted",
            "исход стадии — последней по времени закрытой задачи, а не первой");
    }

    // ─── Дефект: consult → стадия «Разрез», не «Мерж» ─────────────────────────────────────

    // Эпик + одна живая consult (InProgress) → стадия «Разрез» active, «Мерж» НЕ active.
    // До фикса: consult не входил ни в одну стадию → все стадии пусты → fallback merge active.
    [Fact]
    public void Build_ТолькоConsultInProgress_CutActive()
    {
        const string epicId = "epic-consult-active";
        var epic = new TaskItem { Id = epicId, Role = TaskRoles.Feature };
        var consult = new TaskItem
        {
            Id = "c1", FeatureId = epicId, Role = TaskRoles.Consult,
            Status = TaskItemStatus.InProgress, PersonaId = "Александр",
            ClaudeStartedAt = new DateTime(2026, 10, 19, 10, 0, 0),
        };

        var run = FeatureProjector.Build(epic, [epic, consult]);

        // «Разрез» — active (consult входит в cut).
        Stage(run, "cut").State.Should().Be("active",
            "живая consult-задача делает стадию «Разрез» активной");
        Stage(run, "cut").Passes.Should().Be(1);

        // «Мерж» — НЕ active (задач нет → pending/absent, не active).
        Stage(run, "merge").State.Should().NotBe("active",
            "merge без задач не может быть active");

        // Остальные стадии — pending/absent (задач нет).
        foreach (var key in new[] { "plan", "prod", "review", "qa", "final" })
            Stage(run, key).State.Should().NotBe("active", $"{key}: задач нет");
    }

    // Эпик без единой дочерней задачи → стадия «Разрез» active (фича ещё не стартовала).
    // До фикса: все стадии пусты → fallback merge active.
    [Fact]
    public void Build_БезЗадач_CutActive()
    {
        const string epicId = "epic-no-tasks";
        var epic = new TaskItem { Id = epicId, Role = TaskRoles.Feature };

        var run = FeatureProjector.Build(epic, [epic]);

        Stage(run, "cut").State.Should().Be("active",
            "задач нет — текущий этап «Разрез» (фича не стартовала)");
        Stage(run, "merge").State.Should().NotBe("active",
            "merge без задач не active");
    }

    // Consult done, шагов/плана/ревью нет → «Разрез» done, «План» active (фича ждёт плана),
    // «Мерж» НЕ active. Сценарий: между разрезом и планом — ложного «Мерж · сейчас» быть не должно.
    [Fact]
    public void Build_ConsultDoneБезШагов_CutDone()
    {
        const string epicId = "epic-consult-done";
        var epic = new TaskItem { Id = epicId, Role = TaskRoles.Feature };
        var consult = new TaskItem
        {
            Id = "c1", FeatureId = epicId, Role = TaskRoles.Consult,
            Status = TaskItemStatus.Done, PersonaId = "Александр",
            CompletedAt = new DateTime(2026, 10, 19, 11, 0, 0),
        };

        var run = FeatureProjector.Build(epic, [epic, consult]);

        Stage(run, "cut").State.Should().Be("done",
            "consult done → «Разрез» пройден");
        // «План» — задач нет, но «Разрез» пройден → active (фича ждёт плана), не pending.
        Stage(run, "plan").State.Should().Be("active",
            "разрез пройден, плана/шагов нет → активен «План» (ждём плана)");
        Stage(run, "merge").State.Should().NotBe("active",
            "ложного «Мерж · сейчас» между разрезом и планом быть не должно");
    }

    // Разрез done + План in progress → «План» active (первая непройденная стадия с задачами).
    // До фикса это работало «случайно» (цикл находил plan) и не было покрыто.
    [Fact]
    public void Build_РазрезDone_ПланInProgress_PlanActive()
    {
        const string epicId = "epic-cut-done-plan-inprog";
        var epic = new TaskItem { Id = epicId, Role = TaskRoles.Feature };
        var cut = new TaskItem
        {
            Id = "c1", FeatureId = epicId, Role = TaskRoles.Cut,
            Status = TaskItemStatus.Done, PersonaId = "Александр",
            CompletedAt = new DateTime(2026, 10, 19, 10, 0, 0),
        };
        var plan = new TaskItem
        {
            Id = "p1", FeatureId = epicId, Role = TaskRoles.Plan,
            Status = TaskItemStatus.InProgress, PersonaId = "Егор",
            ClaudeStartedAt = new DateTime(2026, 10, 19, 11, 0, 0),
        };

        var run = FeatureProjector.Build(epic, [epic, cut, plan]);

        Stage(run, "cut").State.Should().Be("done");
        Stage(run, "plan").State.Should().Be("active",
            "разрез пройден, план идёт → «План» active");
        Stage(run, "merge").State.Should().NotBe("active");
    }

    // Все задачи фичи dropped (Status=Done + DroppedByHumanAt) → стадии «пройдены»,
    // ложного «Мерж · сейчас» быть не должно: активна стадия после последней заполненной.
    [Fact]
    public void Build_ВсеЗадачиDropped_НеМерж()
    {
        const string epicId = "epic-all-dropped";
        var epic = new TaskItem { Id = epicId, Role = TaskRoles.Feature };
        var cut = new TaskItem
        {
            Id = "c1", FeatureId = epicId, Role = TaskRoles.Cut,
            Status = TaskItemStatus.Done, PersonaId = "Александр",
            CompletedAt = new DateTime(2026, 10, 19, 10, 0, 0),
            DroppedByHumanAt = new DateTime(2026, 10, 19, 10, 30, 0),
        };
        var plan = new TaskItem
        {
            Id = "p1", FeatureId = epicId, Role = TaskRoles.Plan,
            Status = TaskItemStatus.Done, PersonaId = "Егор",
            CompletedAt = new DateTime(2026, 10, 19, 11, 0, 0),
            DroppedByHumanAt = new DateTime(2026, 10, 19, 11, 30, 0),
        };

        var run = FeatureProjector.Build(epic, [epic, cut, plan]);

        // cut/plan — Done (dropped считается пройденным); active = prod (после plan), merge НЕ active.
        Stage(run, "cut").State.Should().Be("done");
        Stage(run, "plan").State.Should().Be("done");
        Stage(run, "prod").State.Should().Be("active",
            "все задачи dropped → стадия после последней заполненной (prod), не Мерж");
        Stage(run, "merge").State.Should().NotBe("active",
            "ложного «Мерж · сейчас» при всех dropped быть не должно");
    }

    // План done, шагов (prod) нет → «Производство» active, «Мерж» НЕ active.
    // Промежуточный случай: ложного «Мерж · сейчас» между планом и производством быть не должно.
    [Fact]
    public void Build_ПланDoneБезШагов_ProdActive()
    {
        const string epicId = "epic-plan-done-no-steps";
        var epic = new TaskItem { Id = epicId, Role = TaskRoles.Feature };
        var cut = new TaskItem
        {
            Id = "c1", FeatureId = epicId, Role = TaskRoles.Cut,
            Status = TaskItemStatus.Done, PersonaId = "Александр",
            CompletedAt = new DateTime(2026, 10, 19, 10, 0, 0),
        };
        var plan = new TaskItem
        {
            Id = "p1", FeatureId = epicId, Role = TaskRoles.Plan,
            Status = TaskItemStatus.Done, PersonaId = "Егор",
            CompletedAt = new DateTime(2026, 10, 19, 11, 0, 0),
        };

        var run = FeatureProjector.Build(epic, [epic, cut, plan]);

        Stage(run, "cut").State.Should().Be("done");
        Stage(run, "plan").State.Should().Be("done");
        Stage(run, "prod").State.Should().Be("active",
            "план пройден, шагов нет → «Производство» active (ждём шагов), не Мерж");
        Stage(run, "merge").State.Should().NotBe("active");
    }

    // Мутация: consult НЕ входит в cut (только Role == Cut) → все стадии пусты →
    // fallback merge active. Тест Build_ТолькоConsultInProgress_CutActive краснеет
    // (cut будет pending, а не active).
    [Fact]
    public void Build_Мутация_ConsultНеВходитВCut()
    {
        const string epicId = "epic-mut-consult";
        var epic = new TaskItem { Id = epicId, Role = TaskRoles.Feature };
        var consult = new TaskItem
        {
            Id = "c1", FeatureId = epicId, Role = TaskRoles.Consult,
            Status = TaskItemStatus.InProgress, PersonaId = "Александр",
            ClaudeStartedAt = new DateTime(2026, 10, 19, 10, 0, 0),
        };

        // Мутация: в FeatureProjector.Build заменить
        //   t.Role is TaskRoles.Cut or TaskRoles.Consult
        // на
        //   t.Role == TaskRoles.Cut
        // → consult не входит в cut → cut пуст → activeIndex = -1 → fallback.
        // allEmpty = true → activeIndex = 0 (cut). Cut пуст → state = "active" (i == activeIndex).
        // Но passes = 0, а тест ожидает passes = 1 → краснеет.
        var run = FeatureProjector.Build(epic, [epic, consult]);

        Stage(run, "cut").Passes.Should().Be(1,
            "consult входит в cut: passes = 1; мутация (только Cut) → 0");
    }
}
