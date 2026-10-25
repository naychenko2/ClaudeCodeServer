using System.Reflection;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Tasks.Projector;

namespace ClaudeHomeServer.Tasks.Tests;

// Волна 6, шаги B1a+B2b: FeatureBrief.Build — чистая функция над готовыми FeatureRun/FeatureTimeline
// и выборкой FeatureTypical. Эталон считан руками из сценария каждого теста.
//
// Мутации (проверены прогоном, см. отчёт задачи):
//  • убрать строку из FeatureBrief.KindByState — тест таблицы краснеет;
//  • считать done своим счётом (StepProgress.Accepted вместо run.StepsAccepted) — тест «done/elapsed» красный;
//  • брать ElapsedMinutes вместо WorkWallMinutes — тот же тест красный;
//  • убрать Take(MaxNext) — тест лимита красный; убрать фильтр pending — тест пропусков красный;
//  • ReviewCoefficient 0.5 → 1 — тест формулы красный (k = 9 вместо 7, remaining = 120 вместо 100);
//  • убрать фильтр PlanState — тест «нет плана» красный; убрать ветку partial — тест «частичный план»
//    красный; вернуть литералы вместо констант Plan* в EstimateOf — тест-сторож исходника красный;
//    убрать фильтр состояний — тесты веток красные;
//  • B5: убрать проверку стадии в StaleOf — тест «смена стадии» красный; убрать проверку категории —
//    тест «переход на паузу» красный; отдать в BriefNote поле BySessionId — тест контракта красный.
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

    // ─── фикстуры estimate (B2b) ───────────────────────────────────────────────────────────────

    // Run с нарезанным планом и известными корзинами знаменателя (Work/NotCreated/Review);
    // hold — удержание для веток paused/queued (init-only поле, только через инициализатор);
    // planState — состояние плана для тестов веток «оценки нет» (по умолчанию нарезанный «cut»).
    private static FeatureRun RunWithPlan(string state, int work, int notCreated, int review,
        FeatureHold? hold = null, string planState = "cut") => new()
    {
        FeatureId = "ep",
        State = state,
        Hold = hold,
        StepProgress = new FeatureStepProgress
        {
            PlanState = planState, Total = work + notCreated + review,
            Work = work, NotCreated = notCreated, Review = review,
        },
    };

    // Выборка FeatureTypical для тестов estimate: значимы только шаговые метрики и StepCount,
    // остальное — пустышки (FeatureBrief их не читает). Count намеренно больше StepCount:
    // тесты тогда доказывают, что sampleSize — шаговая выборка, а не число смерженных.
    private static FeatureTypical.TypicalFeatureStats TypicalOf(
        double stepMedian, double stepP25, double stepP75,
        double tailMedian, double tailP25, double tailP75, int stepCount) =>
        new(Count: stepCount + 2, WaitingCount: 0,
            Duration: new FeatureTypical.TypicalPercentiles(0, 0, 0), Waiting: null,
            Loops: new FeatureTypical.TypicalPercentiles(0, 0, 0),
            StepCount: stepCount,
            StepMinutes: new FeatureTypical.TypicalPercentiles(stepMedian, stepP25, stepP75),
            TailMinutes: new FeatureTypical.TypicalPercentiles(tailMedian, tailP25, tailP75));

    // ─── kind: полная таблица ──────────────────────────────────────────────────────────────────

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
            var brief = FeatureBrief.Build(RunOf(state), null, null, NOW);
            brief.Kind.Should().Be(ExpectedKinds[state], $"состояние {state}");
        }
    }

    [Fact]
    public void Kind_ПаузаРасщепляетсяПоПричинеУдержания()
    {
        var manual = new FeatureHold(FeatureHoldReasons.Manual, NOW, null, true);
        var queue = new FeatureHold(FeatureHoldReasons.Queue, NOW, null, true);

        FeatureBrief.Build(RunOf(FeatureStates.Paused, manual), null, null, NOW).Kind.Should().Be("paused");
        FeatureBrief.Build(RunOf(FeatureStates.Paused, queue), null, null, NOW).Kind.Should().Be("queued");
        // queue вне состояния paused (например, ошибочные данные) категорию не меняет.
        FeatureBrief.Build(RunOf(FeatureStates.Running, queue), null, null, NOW).Kind.Should().Be("running");
    }

    [Fact]
    public void Kind_НеизвестноеСостояниеБросаетИсключениеАНеПодставляетКатегорию()
    {
        var act = () => FeatureBrief.Build(RunOf("фиктивное_состояние"), null, null, NOW);

        act.Should().Throw<InvalidOperationException>().WithMessage("*фиктивное_состояние*");
    }

    // ─── done и elapsed: значения run/timeline как есть ────────────────────────────────────────

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

        var brief = FeatureBrief.Build(run, TimelineOf(elapsed: 40, workWall: 17), null, NOW);

        brief.Done.StepsAccepted.Should().Be(run.StepsAccepted).And.Be(2);
        brief.Done.StepsTotal.Should().Be(6);
        brief.Done.Loops.Should().Be(run.Verdicts.Loops).And.Be(3, "возвраты 2 + «с правками» 1");
        brief.Done.PlanState.Should().Be("cut");
        brief.ElapsedMinutes.Should().Be(17, "это work по часам стены (WorkWallMinutes), не общее время");
    }

    [Fact]
    public void Elapsed_БезTimelineNull()
    {
        FeatureBrief.Build(RunOf(FeatureStates.Idle), null, null, NOW).ElapsedMinutes.Should().BeNull();
    }

    // ─── next: ≤ 3, без skipped/absent ─────────────────────────────────────────────────────────

    [Fact]
    public void Next_НеБольшеТрёх()
    {
        var run = RunOf(FeatureStates.Running);
        run.Stages.AddRange([
            Stage("cut", "done"), Stage("plan", "done"), Stage("prod", "active"),
            Stage("review", "pending", "gleb"), Stage("qa", "pending", "vera"),
            Stage("final", "pending"), Stage("merge", "pending"),
        ]);

        var next = FeatureBrief.Build(run, null, null, NOW).Next;

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

        var next = FeatureBrief.Build(run, null, null, NOW).Next;

        next.Select(n => n.Stage).Should().Equal("final", "merge");
    }

    [Fact]
    public void Next_БезАктивнойСтадииПусто()
    {
        var run = RunOf(FeatureStates.Merged);
        run.Stages.AddRange([Stage("cut", "done"), Stage("prod", "done"), Stage("merge", "done")]);

        FeatureBrief.Build(run, null, null, NOW).Next.Should().BeEmpty();
    }

    // ─── now и reason ──────────────────────────────────────────────────────────────────────────

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
        var b1 = FeatureBrief.Build(withNow, null, null, NOW);
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
        var b2 = FeatureBrief.Build(withAttention, null, null, NOW);
        b2.Now.Should().BeEquivalentTo(new FeatureBrief.BriefNow("prod", "t3", "denis", null, null, since));
        b2.Reason.Should().Be("упал");

        // Ни Now, ни Attention.
        FeatureBrief.Build(RunOf(FeatureStates.Idle), null, null, NOW).Now.Should().BeNull();
    }

    [Fact]
    public void Reason_ПаузаИОчередьБезТревоги_БерутКодУдержания()
    {
        var manual = new FeatureHold(FeatureHoldReasons.Manual, NOW, null, true);
        var queue = new FeatureHold(FeatureHoldReasons.Queue, NOW, null, true);

        FeatureBrief.Build(RunOf(FeatureStates.Paused, manual), null, null, NOW).Reason.Should().Be("manual");
        FeatureBrief.Build(RunOf(FeatureStates.Paused, queue), null, null, NOW).Reason.Should().Be("queue");
        FeatureBrief.Build(RunOf(FeatureStates.Running), null, null, NOW).Reason.Should().BeNull();
    }

    // ─── estimate (B2b): формула, лента, источник ───────────────────────────────────────────────

    // Фикстура: k = Work(3) + NotCreated(2) + 0.5 × Review(4) = 7. Эталон считан руками:
    // remaining = 10 × 7 + 30 = 100; p25 = 8 × 7 + 20 = 76; p75 = 12 × 7 + 40 = 124.
    // Мутация ReviewCoefficient 0.5 → 1: k = 9, remaining = 120 — тест красный.
    [Fact]
    public void Estimate_ФормулаПоМедианамЛентаПоКвантилям_ИсточникИВыборка()
    {
        var run = RunWithPlan(FeatureStates.Running, work: 3, notCreated: 2, review: 4);
        var typical = TypicalOf(stepMedian: 10, stepP25: 8, stepP75: 12,
            tailMedian: 30, tailP25: 20, tailP75: 40, stepCount: 5);

        var e = FeatureBrief.Build(run, null, typical, NOW).Estimate;

        e.Should().NotBeNull();
        e!.RemainingMinutes.Should().Be(100, "10 × 7 + 30");
        e.P25.Should().Be(76, "8 × 7 + 20");
        e.P75.Should().Be(124, "12 × 7 + 40");
        e.P25.Should().BeLessThanOrEqualTo(e.RemainingMinutes);
        e.RemainingMinutes.Should().BeLessThanOrEqualTo(e.P75);
        e.Source.Should().Be("history");
        e.SampleSize.Should().Be(5, "n выборки — StepCount (шаговая выборка), не Count смерженных");
    }

    // waiting_you и error — оцениваются (разрез п.4: «да, с пометкой «часы стоят»» — пометка фронта).
    [Fact]
    public void Estimate_ЖдётВасИОшибкаТожеОцениваются()
    {
        var typical = TypicalOf(10, 8, 12, 30, 20, 40, 5);

        var waiting = FeatureBrief.Build(RunWithPlan(FeatureStates.Waiting, 0, 1, 1), null, typical, NOW).Estimate;
        waiting.Should().NotBeNull();
        waiting!.RemainingMinutes.Should().Be(45, "10 × (0 + 1 + 0.5) + 30");

        var error = FeatureBrief.Build(RunWithPlan(FeatureStates.Stopped, 1, 0, 0), null, typical, NOW).Estimate;
        error.Should().NotBeNull();
        error!.RemainingMinutes.Should().Be(40, "10 × 1 + 30");
    }

    // idle — оценивается, если есть план (разрез п.4: «да, если есть план»).
    [Fact]
    public void Estimate_IdleСПланомЕсть_СостояниеЕёНеБлокирует()
    {
        var e = FeatureBrief.Build(RunWithPlan(FeatureStates.Idle, work: 0, notCreated: 2, review: 0),
            null, TypicalOf(10, 8, 12, 30, 20, 40, 5), NOW).Estimate;

        e.Should().NotBeNull();
        e!.RemainingMinutes.Should().Be(50, "10 × 2 + 30");
    }

    // ─── estimate: ветки «оценки нет» (разрез п.3), по тесту на ветку ──────────────────────────

    [Fact]
    public void Estimate_НетКогдаВыборкиМеньшеТрёх()
    {
        var run = RunWithPlan(FeatureStates.Running, work: 1, notCreated: 0, review: 0);
        // Ровно тот вывод FeatureTypical при n < 3: шаговые метрики null.
        var few = new FeatureTypical.TypicalFeatureStats(
            Count: 2, WaitingCount: 0,
            Duration: new FeatureTypical.TypicalPercentiles(0, 0, 0), Waiting: null,
            Loops: new FeatureTypical.TypicalPercentiles(0, 0, 0),
            StepCount: 2, StepMinutes: null, TailMinutes: null);

        FeatureBrief.Build(run, null, few, NOW).Estimate.Should().BeNull("n < 3 — метрик выборки нет");
        FeatureBrief.Build(run, null, null, NOW).Estimate.Should().BeNull("выборки нет вовсе");
    }

    [Fact]
    public void Estimate_НетКогдаПланНеНарезан()
    {
        var run = RunOf(FeatureStates.Running); // PlanState по умолчанию "pending"

        FeatureBrief.Build(run, null, TypicalOf(10, 8, 12, 30, 20, 40, 5), NOW).Estimate.Should().BeNull(
            "PlanState = pending — знаменатель шагов неизвестен");
    }

    // Возврат ревью B2b (п.1): partial — знаменатель шагов неполон (NotCreated считает только шаги
    // нарезанных планов), оценка вышла бы заниженной. Решение Софьи: при частичном плане оценки нет,
    // как при pending — обещать число по неполному плану хуже, чем не обещать ничего.
    [Fact]
    public void Estimate_НетКогдаПланНарезанЧастично()
    {
        var run = RunWithPlan(FeatureStates.Running, work: 1, notCreated: 2, review: 0, planState: "partial");

        FeatureBrief.Build(run, null, TypicalOf(10, 8, 12, 30, 20, 40, 5), NOW).Estimate.Should().BeNull(
            "PlanState = partial — знаменатель шагов неполон, оценка занижена");
    }

    // Возврат ревью B2b (п.2): PlanState сравнивается только с константами FeatureStepAccounting.Plan*
    // (те же задают значения). Сторож по исходнику: тело EstimateOf ссылается на константы и не несёт
    // литералов "pending"/"partial" — сравнение с литералом разъехало бы со значением молча.
    // Мутация: вернуть в FeatureBrief.cs `== "pending"` — тест красный.
    [Fact]
    public void Estimate_СравнениеPlanStateТолькоСКонстантами_ВИсходникеБезЛитералов()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        string? path = null;
        while (dir is not null && path is null)
        {
            var candidate = Path.Combine(dir.FullName, "backend", "ClaudeHomeServer.Tasks",
                "Projector", "FeatureBrief.cs");
            if (File.Exists(candidate)) path = candidate;
            dir = dir.Parent;
        }
        path.Should().NotBeNull("FeatureBrief.cs не найден от дерева сборки — сторож вне дерева не работает");

        var source = File.ReadAllText(path!);
        var start = source.IndexOf("static BriefEstimate? EstimateOf(", StringComparison.Ordinal);
        start.Should().BeGreaterThan(0, "EstimateOf обязана быть в FeatureBrief");
        var end = source.IndexOf("static BriefNote? NoteOf(", start, StringComparison.Ordinal);
        end.Should().BeGreaterThan(start, "граница тела EstimateOf — следующий член класса");

        var body = source[start..end];
        body.Should().Contain("FeatureStepAccounting.PlanPending").And.Contain("FeatureStepAccounting.PlanPartial",
            "сравнение — с константами, которые задают значения PlanState");
        body.Should().NotContain("\"pending\"").And.NotContain("\"partial\"",
            "в теле EstimateOf нет литералов состояния плана");
    }

    [Fact]
    public void Estimate_НетУОчереди()
    {
        var run = RunWithPlan(FeatureStates.Paused, 1, 1, 1,
            new FeatureHold(FeatureHoldReasons.Queue, NOW, null, true));

        FeatureBrief.Build(run, null, TypicalOf(10, 8, 12, 30, 20, 40, 5), NOW)
            .Estimate.Should().BeNull("queued — фичу не ведут");
    }

    [Fact]
    public void Estimate_НетНаПаузе()
    {
        var run = RunWithPlan(FeatureStates.Paused, 1, 1, 1,
            new FeatureHold(FeatureHoldReasons.Manual, NOW, null, true));

        FeatureBrief.Build(run, null, TypicalOf(10, 8, 12, 30, 20, 40, 5), NOW)
            .Estimate.Should().BeNull("paused — часы стоят");
    }

    [Fact]
    public void Estimate_НетУЗакрытойФичи_МерженнойИЗакрытойВручную()
    {
        var typical = TypicalOf(10, 8, 12, 30, 20, 40, 5);

        FeatureBrief.Build(RunWithPlan(FeatureStates.Merged, 1, 1, 1), null, typical, NOW)
            .Estimate.Should().BeNull("closed по мержу — вместо оценки итог Summary");
        FeatureBrief.Build(RunWithPlan(FeatureStates.Closed, 1, 1, 1), null, typical, NOW)
            .Estimate.Should().BeNull("closed вручную — та же категория");
    }

    [Fact]
    public void Estimate_НетУЧерновика()
    {
        FeatureBrief.Build(RunWithPlan(FeatureStates.Draft, 1, 1, 1), null, TypicalOf(10, 8, 12, 30, 20, 40, 5), NOW)
            .Estimate.Should().BeNull("draft — фичу готовят, работа не разрешена");
    }

    // ─── note (B5): голос эпика из TaskItem.Note ───────────────────────────────────────────────

    // Фикстура: run с активной стадией activeStage и голосом эпика, записанным на stageAtWrite /
    // stateAtWrite (штамп как ставит контроллер B3). bySessionId в хранимом голосе есть — наружу
    // он не должен выйти ни при каком раскладе.
    private static FeatureRun RunWithVoice(string activeStage, string state, FeatureHold? hold,
        string stageAtWrite, string stateAtWrite) => new()
    {
        FeatureId = "ep",
        State = state,
        Hold = hold,
        Epic = new TaskItem
        {
            Id = "ep",
            Role = TaskRoles.Feature,
            Note = new FeatureNote("ревью началось", NOW.AddHours(-2), "sess-1", true,
                stageAtWrite, stateAtWrite),
        },
        Stages = [Stage(activeStage, "active")],
    };

    // c1: смена активной стадии после записи → stale = true (prod → review, категория та же).
    [Fact]
    public void Note_СменаАктивнойСтадииПослеЗаписиДаётStale()
    {
        var run = RunWithVoice("review", FeatureStates.Running, null, "prod", "running");

        FeatureBrief.Build(run, null, null, NOW).Note!.Stale.Should().BeTrue(
            "голос записан на стадии prod, фича уже на review");
    }

    // c1: смена категории состояния (переход на паузу) при НЕизменённой стадии → stale = true.
    // Стадия та же (prod), поэтому краснеет ровно проверка категории, не стадии.
    [Fact]
    public void Note_СменаКатегорииСостоянияПриТойЖеСтадииДаётStale()
    {
        var run = RunWithVoice("prod", FeatureStates.Paused,
            new FeatureHold(FeatureHoldReasons.Manual, NOW, null, true), "prod", "running");

        FeatureBrief.Build(run, null, null, NOW).Note!.Stale.Should().BeTrue(
            "stageAtWrite тот же, но категория сменилась: running → paused");
    }

    // c1: без смен → stale = false; поля контракта — как в хранимом голосе (кроме byPersonaId,
    // его хранимый FeatureNote B3 не несёт).
    [Fact]
    public void Note_БезСменStaleЛожный_ПоляРовноИзХранимогоГолоса()
    {
        var run = RunWithVoice("prod", FeatureStates.Running, null, "prod", "running");

        var note = FeatureBrief.Build(run, null, null, NOW).Note!;

        note.Stale.Should().BeFalse("стадия и категория не менялись");
        note.Text.Should().Be("ревью началось");
        note.At.Should().Be(NOW.AddHours(-2));
        note.ByUser.Should().BeTrue();
        note.StageAtWrite.Should().Be("prod");
        note.ByPersonaId.Should().BeNull("хранимый FeatureNote persona автора не несёт");
    }

    // c2: без Note поле note равно null — и у эпика без голоса, и у run без эпика (не исключение).
    [Fact]
    public void Note_БезГолосаNull_ИУЭпикаБезГолосаИБезЭпика()
    {
        var epicWithoutNote = new FeatureRun
        {
            State = FeatureStates.Running,
            Epic = new TaskItem { Id = "ep", Role = TaskRoles.Feature },
        };
        FeatureBrief.Build(epicWithoutNote, null, null, NOW).Note.Should().BeNull();
        // Run без эпика (частичные данные/тестовая фикстура) — тоже null, не падение.
        FeatureBrief.Build(RunOf(FeatureStates.Running), null, null, NOW).Note.Should().BeNull();
    }

    // c3: наружу — ровно поля контракта разреза п.6; bySessionId (и stateAtWrite) в BriefNote нет.
    // Мутация: добавить в BriefNote поле BySessionId — тест красный.
    [Fact]
    public void Note_НаружуРовноПоляКонтракта_БезBySessionId()
    {
        typeof(FeatureBrief.BriefNote)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            // bySessionId наружу не уходит; stateAtWrite служит только StaleOf.
            .Should().BeEquivalentTo("Text", "At", "ByPersonaId", "ByUser", "StageAtWrite", "Stale");
    }

    // Категория на момент записи не подтверждается (штамп вне таблицы FeatureStates) — голос
    // считается устаревшим (fail-closed), даже когда стадия не менялась.
    [Fact]
    public void Note_НеизвестныйШтампСостоянияСчитаетсяСменойКатегории()
    {
        var run = RunWithVoice("prod", FeatureStates.Running, null, "prod", "фиктивное_состояние");

        FeatureBrief.Build(run, null, null, NOW).Note!.Stale.Should().BeTrue();
    }

    // ─── заглушки и время расчёта ──────────────────────────────────────────────────────────────

    [Fact]
    public void Заглушки_QueueNoteNull_ComputedAtРавенNow()
    {
        var brief = FeatureBrief.Build(RunOf(FeatureStates.Running), null, null, NOW);

        brief.Queue.Should().BeNull();
        brief.Note.Should().BeNull();
        brief.ComputedAt.Should().Be(NOW);
    }
}
