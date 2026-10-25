using System.Reflection;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Tasks.Projector;

namespace ClaudeHomeServer.Tasks.Tests;

// Волна 6, шаг B1a: FeatureBrief.Build — чистая функция над готовыми FeatureRun/FeatureTimeline.
// Эталон считан руками из сценария каждого теста.
//
// Мутации (проверены прогоном, см. отчёт задачи):
//  • убрать строку из FeatureBrief.KindByState — тест таблицы краснеет;
//  • считать done своим счётом (StepProgress.Accepted вместо run.StepsAccepted) — тест «done/elapsed» красный;
//  • брать ElapsedMinutes вместо WorkWallMinutes — тот же тест красный;
//  • убрать Take(MaxNext) — тест лимита красный; убрать фильтр pending — тест пропусков красный.
public class FeatureBriefTests
{
    private static readonly DateTime NOW = new(2026, 10, 25, 12, 0, 0, DateTimeKind.Utc);

    // Ожидаемая таблица: ровно одна строка на каждую константу FeatureStates.
    // paused без Hold.Reason = queue даёт paused; со строкой queue — queued (отдельный тест).
    private static readonly Dictionary<string, string> ExpectedKinds = new()
    {
        [FeatureStates.Running] = "running",
        [FeatureStates.Merging] = "running",
        [FeatureStates.Waiting] = "waiting_you",
        [FeatureStates.MergePending] = "waiting_you",
        [FeatureStates.Stopped] = "error",
        [FeatureStates.Paused] = "paused",
        [FeatureStates.Draft] = "draft",
        [FeatureStates.NotStarted] = "idle",
        [FeatureStates.Idle] = "idle",
        [FeatureStates.Merged] = "closed",
        [FeatureStates.Closed] = "closed",
    };

    private static FeatureRun RunOf(string state, FeatureHold? hold = null) => new()
    {
        FeatureId = "ep",
        State = state,
        Hold = hold,
    };

    private static FeatureTimeline TimelineOf(int elapsed, int workWall) => new(
        NOW.AddHours(-1), NOW, [], [], null,
        new TimelineTotals(elapsed, workWall, workWall, elapsed - workWall,
            null, null, null, null, null, null, null));

    private static FeatureStage Stage(string key, string state, params string[] personas) => new()
    {
        Key = key, State = state, PersonaIds = personas.ToList(),
    };

    // ─── kind: полная таблица ────────────────────────────────────────────────────────────────

    // Все константы FeatureStates берутся рефлексией: новое состояние без строки в таблице —
    // красный тест (и в ожиданиях теста, и в FeatureBrief.KindByState).
    private static List<string> AllStates() =>
        typeof(FeatureStates)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();

    [Fact]
    public void Kind_ТаблицаПокрываетВсеКонстантыFeatureStates()
    {
        var states = AllStates();

        states.Should().BeEquivalentTo(ExpectedKinds.Keys,
            "в ожидаемой таблице теста — ровно по строке на каждую константу FeatureStates");
        FeatureBrief.KindByState.Keys.Should().BeEquivalentTo(states,
            "в таблице FeatureBrief.KindByState — ровно по строке на каждую константу FeatureStates");
    }

    [Fact]
    public void Kind_КаждоеСостояниеДаётОжидаемуюКатегорию()
    {
        foreach (var state in AllStates())
        {
            var brief = FeatureBrief.Build(RunOf(state), null, NOW);
            brief.Kind.Should().Be(ExpectedKinds[state], $"состояние {state}");
        }
    }

    [Fact]
    public void Kind_ПаузаРасщепляетсяПоПричинеУдержания()
    {
        var manual = new FeatureHold(FeatureHoldReasons.Manual, NOW, null, true);
        var queue = new FeatureHold(FeatureHoldReasons.Queue, NOW, null, true);

        FeatureBrief.Build(RunOf(FeatureStates.Paused, manual), null, NOW).Kind.Should().Be("paused");
        FeatureBrief.Build(RunOf(FeatureStates.Paused, queue), null, NOW).Kind.Should().Be("queued");
        // queue вне состояния paused (например, ошибочные данные) категорию не меняет.
        FeatureBrief.Build(RunOf(FeatureStates.Running, queue), null, NOW).Kind.Should().Be("running");
    }

    [Fact]
    public void Kind_НеизвестноеСостояниеБросаетИсключениеАНеПодставляетКатегорию()
    {
        var act = () => FeatureBrief.Build(RunOf("фиктивное_состояние"), null, NOW);

        act.Should().Throw<InvalidOperationException>().WithMessage("*фиктивное_состояние*");
    }

    // ─── done и elapsed: значения run/timeline как есть ──────────────────────────────────────

    [Fact]
    public void DoneИElapsed_БерутсяИзRunИTimelineБезСвоегоСчёта()
    {
        // Числа намеренно разные в соседних полях: StepsAccepted(2) ≠ StepProgress.Accepted(5),
        // WorkWall(17) ≠ Elapsed(40) — подмена источника не пройдёт незамеченной.
        var run = new FeatureRun
        {
            State = FeatureStates.Running,
            StepsAccepted = 2,
            StepsTotal = 6,
            Verdicts = new FeatureVerdicts { Returned = 2, AcceptedWithRemarks = 1, Accepted = 4 },
            StepProgress = new FeatureStepProgress { PlanState = "cut", Total = 6, Accepted = 5 },
        };

        var brief = FeatureBrief.Build(run, TimelineOf(elapsed: 40, workWall: 17), NOW);

        brief.Done.StepsAccepted.Should().Be(run.StepsAccepted).And.Be(2);
        brief.Done.StepsTotal.Should().Be(6);
        brief.Done.Loops.Should().Be(run.Verdicts.Loops).And.Be(3, "возвраты 2 + «с правками» 1");
        brief.Done.PlanState.Should().Be("cut");
        brief.ElapsedMinutes.Should().Be(17, "это work по часам стены (WorkWallMinutes), не общее время");
    }

    [Fact]
    public void Elapsed_БезTimelineNull()
    {
        FeatureBrief.Build(RunOf(FeatureStates.Idle), null, NOW).ElapsedMinutes.Should().BeNull();
    }

    // ─── next: ≤ 3, без skipped/absent ───────────────────────────────────────────────────────

    [Fact]
    public void Next_НеБольшеТрёх()
    {
        var run = RunOf(FeatureStates.Running);
        run.Stages.AddRange([
            Stage("cut", "done"), Stage("plan", "done"), Stage("prod", "active"),
            Stage("review", "pending", "gleb"), Stage("qa", "pending", "vera"),
            Stage("final", "pending"), Stage("merge", "pending"),
        ]);

        var next = FeatureBrief.Build(run, null, NOW).Next;

        next.Should().HaveCount(3);
        next.Select(n => n.Stage).Should().Equal("review", "qa", "final");
        next[0].PersonaId.Should().Be("gleb");
        next[1].PersonaId.Should().Be("vera");
        next[2].PersonaId.Should().BeNull("стадия не заведена — исполнителя нет");
    }

    [Fact]
    public void Next_ПропускаетSkippedИAbsent_ИНеБерётСтадииДоАктивной()
    {
        var run = RunOf(FeatureStates.Running);
        run.Stages.AddRange([
            Stage("cut", "pending"),    // до активной — не «дальше»
            Stage("design", "skipped"),
            Stage("plan", "done"),
            Stage("prod", "active"),
            Stage("review", "absent"),
            Stage("qa", "skipped"),
            Stage("final", "pending"),
            Stage("merge", "pending"),
        ]);

        var next = FeatureBrief.Build(run, null, NOW).Next;

        next.Select(n => n.Stage).Should().Equal("final", "merge");
    }

    [Fact]
    public void Next_БезАктивнойСтадииПусто()
    {
        var run = RunOf(FeatureStates.Merged);
        run.Stages.AddRange([Stage("cut", "done"), Stage("prod", "done"), Stage("merge", "done")]);

        FeatureBrief.Build(run, null, NOW).Next.Should().BeEmpty();
    }

    // ─── now и reason ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Now_ПерваяЗаписьNow_ПриЕёОтсутствииAttention_ИначеNull()
    {
        var since = NOW.AddMinutes(-30);

        // Now[0] выигрывает у Attention.
        var withNow = RunOf(FeatureStates.Running);
        withNow.Stages.Add(Stage("review", "active"));
        withNow.Now.Add(new FeatureNow
        {
            TaskId = "t1", Stage = "review", PersonaId = "gleb", StepRef = "Ш3", StartedAt = since,
        });
        withNow.Now.Add(new FeatureNow { TaskId = "t2", Stage = "prod" });
        withNow.StepChains.Add(new FeatureStepChain { StepRef = "Ш3", Lane = "back" });
        var b1 = FeatureBrief.Build(withNow, null, NOW);
        b1.Now.Should().BeEquivalentTo(new FeatureBrief.BriefNow("review", "t1", "gleb", "Ш3", "back", since));

        // Now пуст — берётся Attention; стадия — активная стадия конвейера.
        var withAttention = new FeatureRun
        {
            State = FeatureStates.Stopped,
            Attention = new FeatureAttention
            {
                Kind = "stopped", TaskId = "t3", Since = since, Reason = "упал", PersonaId = "denis",
            },
        };
        withAttention.Stages.Add(Stage("prod", "active"));
        var b2 = FeatureBrief.Build(withAttention, null, NOW);
        b2.Now.Should().BeEquivalentTo(new FeatureBrief.BriefNow("prod", "t3", "denis", null, null, since));
        b2.Reason.Should().Be("упал");

        // Ни Now, ни Attention.
        FeatureBrief.Build(RunOf(FeatureStates.Idle), null, NOW).Now.Should().BeNull();
    }

    [Fact]
    public void Reason_ПаузаИОчередьБезТревоги_БерутКодУдержания()
    {
        var manual = new FeatureHold(FeatureHoldReasons.Manual, NOW, null, true);
        var queue = new FeatureHold(FeatureHoldReasons.Queue, NOW, null, true);

        FeatureBrief.Build(RunOf(FeatureStates.Paused, manual), null, NOW).Reason.Should().Be("manual");
        FeatureBrief.Build(RunOf(FeatureStates.Paused, queue), null, NOW).Reason.Should().Be("queue");
        FeatureBrief.Build(RunOf(FeatureStates.Running), null, NOW).Reason.Should().BeNull();
    }

    // ─── заглушки B1a и время расчёта ────────────────────────────────────────────────────────

    [Fact]
    public void ЗаглушкиB1a_QueueEstimateNoteNull_ComputedAtРавенNow()
    {
        var brief = FeatureBrief.Build(RunOf(FeatureStates.Running), null, NOW);

        brief.Queue.Should().BeNull();
        brief.Estimate.Should().BeNull();
        brief.Note.Should().BeNull();
        brief.ComputedAt.Should().Be(NOW);
    }
}
