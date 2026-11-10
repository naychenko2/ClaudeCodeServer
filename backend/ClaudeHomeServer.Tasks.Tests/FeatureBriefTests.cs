using System.Reflection;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Tasks;
using ClaudeHomeServer.Services.Tasks.Projector;

namespace ClaudeHomeServer.Tasks.Tests;

// Волна 6, шаги B1a+B2b: FeatureBrief.Build — чистая функция над готовыми FeatureRun/FeatureTimeline
// и выборкой FeatureTypical. Эталон считан руками из сценария каждого теста.
//
// Мутации (проверены прогоном, см. отчёт задачи):
//  • убрать строку из FeatureBrief.KindByState — тест таблицы краснеет;
//  • Б-1.1: убрать расщепление по run.Closed в KindOf — «МержРасщепляется» красный (Closed=true
//    даёт "merged" вместо "closed"); убрать merged из EstimateOf — «НетУЗакрытойФичи» красный
//    (оценка появляется у смерженной с открытым эпиком);
//  • считать done своим счётом (StepProgress.Accepted вместо run.StepsAccepted) — тест «done/elapsed» красный;
//  • брать ElapsedMinutes вместо WorkWallMinutes — тот же тест красный;
//  • убрать Take(MaxNext) — тест лимита красный; убрать фильтр pending — тест пропусков красный;
//  • ReviewCoefficient 0.5 → 1 — тест формулы красный (k = 9 вместо 7, remaining = 120 вместо 100);
//  • убрать фильтр PlanState — тест «нет плана» красный; убрать ветку partial — тест «частичный план»
//    красный; вернуть литералы вместо констант Plan* в EstimateOf — тест-сторож исходника красный;
//    убрать фильтр состояний — тесты веток красные;
//  • B5: убрать проверку стадии в StaleOf — тест «смена стадии» красный; убрать проверку категории —
//    тест «переход на паузу» красный; отдать в BriefNote поле BySessionId — тест контракта красный.
//  • B6 (дефект f2da9354): отдать в Brief.UpdatedAt момент now — тест «равен последнему событию» красный;
//    взять вместо событий состояния TaskItem.UpdatedAt — тест «настройки не двигают» красный; убрать
//    ветку ExecutorStoppedAt — тот же тест красный (NOW-4 ч вместо NOW-3 ч); убрать якорь CreatedAt —
//    тест «эпик якорь» красный; подставить «now» вместо null при отсутствии событий — тест «null а не сейчас» красный.
//  • B8 (MAJOR-1 ревью R-F8): взять вместо Hold.Since work-интервалы (ElapsedMinutes) — тест
//    «очередь не из work-интервалов» красный (7 вместо 25); убрать фильтр kind — тест «только queued»
//    красный (число у paused/running); убрать clamp к 0 — тест границы красный (отрицательное время).
//  • B14 (возврат FIN 74722f13): сверять StaleOf с Now[0] вместо ActiveStageOf — тест
//    «голос без идущих задач» красный (Now пуст → "prod" ≠ "").
//  • B15 (дефект 415c1c80): вернуть штамп голоса к состоянию (run.State) вместо категории —
//    тесты «голос в очереди» красные; не достраивать старый штамп-состояние через KindByState —
//    тест «старый штамп» красный.
//  • FS-V1 (разрез §2.1 П2): взять в CurrentStageOf только первую активную стадию (без Now[0]) —
//    тест «CurrentStageOf_СтадияИдущейЗадачи…» красный; убрать fallback при пустом Now —
//    «CurrentStageOf_БезИдущихЗадачей…» красный; вернуть в StaleOf первую активную —
//    «Note_ШтампПервойАктивной…» красный.
//  • FS-E4 (ask): убрать строку словаря StopReasonPhrases — рефлексивный тест «КаждыйКод…» красный;
//    взять для merge attention.Reason («мерж») — тест «Merge…» красный; вернуть null для wait без
//    текста — тест «WaitБезТекста…» красный; убрать clamp лимита — тест «ЛимитТекста120» красный.
//  • FS-V2 (eta): поменять в EtaOf приоритет note/history — красные ровно три случая приоритета
//    («ОценкаОркестратораГлавнее…», «Остановка…ДатаГасится…», «ЗначенияРовноВычисленные…» —
//    history 40 вместо note 90), остальные зелёные; убрать строку гашения endsAt при clockStopped —
//    красный ровно «Остановка…ДатаГасится…» (дата note.estimate остаётся при стоящих часах).
public class FeatureBriefTests
{
    private static readonly DateTime NOW = new(2026, 10, 25, 12, 0, 0, DateTimeKind.Utc);

    // Ожидаемая таблица: ровно одна строка на каждую константу FeatureStates.
    // paused без Hold.Reason = queue даёт paused; со строкой queue — queued (отдельный тест).
    // merged без Closed (эпик открыт) даёт merged; с Closed=true — closed (отдельный тест).
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
        [FeatureStates.Merged] = "merged",
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
        FeatureHold? hold = null, string planState = "cut", bool closed = false) => new()
    {
        FeatureId = "ep",
        State = state,
        Hold = hold,
        Closed = closed,
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

    // merged расщепляется по закрытости эпика (run.Closed, FeatureRun.cs:95-98) — то же расщепление,
    // что FeatureProjector.Bucket: смерженная с открытым эпиком остаётся merged (она среди открытых),
    // смерженная с эпиком Done — closed. Мутация: убрать расщепление по run.Closed в KindOf —
    // второй случай красный ("merged").
    [Fact]
    public void Kind_МержРасщепляетсяПоЗакрытостиЭпика()
    {
        FeatureBrief.Build(RunOf(FeatureStates.Merged), null, null, NOW).Kind.Should().Be("merged",
            "смержена с открытым эпиком (Closed=false) — фича не закрыта");

        var closedEpic = new FeatureRun { FeatureId = "ep", State = FeatureStates.Merged, Closed = true };
        FeatureBrief.Build(closedEpic, null, null, NOW).Kind.Should().Be("closed",
            "смержена с эпиком Done (Closed=true) — закрыта");
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

    // ─── ask (FS-E4): одна фраза «что нужно от тебя» из уже вычисленной тревоги ────────────────
    // Источник — run.Attention (FeatureProjector.cs:175-216): приоритет stopped → wait → merge решён
    // там, здесь только отображение единственного элемента — второго расчёта нет.

    // Фикстура тревоги: поля attention (не задачи) — тест доказывает, что ask берётся из Attention.
    private static FeatureRun RunWithAttention(string kind, string? reason, DateTime? since,
        string? personaId, string? sessionId) => new()
    {
        FeatureId = "ep",
        State = kind switch
        {
            "stopped" => FeatureStates.Stopped,
            "wait" => FeatureStates.Waiting,
            "merge" => FeatureStates.MergePending,
            _ => FeatureStates.Idle,
        },
        Attention = new FeatureAttention
        {
            Kind = kind, TaskId = "t1", Since = since, Reason = reason,
            PersonaId = personaId, LinkedSessionId = sessionId,
        },
    };

    // c1: тревоги нет — null. В том числе у идущей фичи с записями Now: ask только из Attention
    // (тот же источник, что now/reason, а не второй расчёт).
    [Fact]
    public void Ask_БезТревогиNull_ДажеУИдущейФичеСNow()
    {
        FeatureBrief.Build(RunOf(FeatureStates.Idle), null, null, NOW).Ask.Should().BeNull();

        var running = RunOf(FeatureStates.Running);
        running.Now.Add(new FeatureNow { TaskId = "t1", Stage = "prod", StartedAt = NOW.AddMinutes(-10) });
        FeatureBrief.Build(running, null, null, NOW).Ask.Should().BeNull("тревоги нет — нет и ask");
    }

    // c1: wait — text = AwaitingHumanReason (attention.Reason как есть), source = "wait",
    // since/personaId/sessionId — поля тревоги как есть.
    [Fact]
    public void Ask_WaitБерётТекстОжиданияИзТревоги()
    {
        var since = NOW.AddMinutes(-30);
        var ask = FeatureBrief.Build(
            RunWithAttention("wait", "нужен ответ по макету", since, "sofya", "sess-9"), null, null, NOW).Ask!;

        ask.Text.Should().Be("нужен ответ по макету");
        ask.Source.Should().Be("wait");
        ask.Since.Should().Be(since);
        ask.PersonaId.Should().Be("sofya");
        ask.SessionId.Should().Be("sess-9");
    }

    // Реальный wait всегда с текстом (SetAwaitingHuman пишет Since и Reason парой, TaskManager.cs:1611-1626).
    // Аномальные данные (null/пусто/пробелы) — общая фраза: сигнал «ждёт вас» не должен теряться.
    // Мутация: вернуть null вместо фразы — тест красный.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Ask_WaitБезТекстаДаётОбщуюФразуОжидания(string? reason) =>
        FeatureBrief.Build(RunWithAttention("wait", reason, NOW, null, null), null, null, NOW)
            .Ask!.Text.Should().Be(FeatureBrief.WaitAskFallback);

    // c1: merge — фиксированная фраза, а не attention.Reason («мерж» — техническое слово тревоги,
    // не «что нужно от человека»). Мутация: взять attention.Reason — тест красный («мерж»).
    [Fact]
    public void Ask_MergeФиксированнаяФразаАНеТехническийReasonТревоги()
    {
        var since = NOW.AddHours(-2);
        var ask = FeatureBrief.Build(RunWithAttention("merge", "мерж", since, "mark", "sess-8"), null, null, NOW).Ask!;

        ask.Text.Should().Be(FeatureBrief.MergeAskText).And.NotBe("мерж");
        ask.Source.Should().Be("merge");
        ask.Since.Should().Be(since);
    }

    // c1: stopped — человеческая фраза по коду ExecutorStopReason (словарь StopReasonPhrases).
    // Золотые фразы кодов словаря; хвосты ревью R-E4 (минор 1): три из них переформулированы под
    // вопрос «что нужно от тебя» (feature_held, interrupted_by_user, tool_call_leaked) — тексты
    // согласованы продактом, это осознанная правка словаря.
    [Theory]
    [InlineData(ExecutorStopClassifier.AuthFailedReason, "Нужна авторизация — исполнитель не вошёл")]
    [InlineData(ExecutorStopClassifier.InterruptedByUserReason, "Исполнитель остановлен вами — запустите снова или оставьте")]
    [InlineData(ExecutorStopClassifier.FeatureHeldReason, "Фича на паузе — снимите паузу, когда будете готовы")]
    [InlineData(ExecutorStopClassifier.ToolCallLeakedReason, "Исполнитель сбился — перезапустите шаг")]
    [InlineData(ExecutorStopClassifier.FeatureHeldExpiredReason, "Чат прерванного исполнителя удалён — шаг нужно перезапустить")]
    public void Ask_StoppedФразаПоКодуИзСловаря(string code, string expected)
    {
        var ask = FeatureBrief.Build(RunWithAttention("stopped", code, NOW.AddHours(-1), "denis", null),
            null, null, NOW).Ask!;

        ask.Text.Should().Be(expected);
        ask.Source.Should().Be("stopped");
    }

    // c1: неизвестный код или пометка остановки без кода — общая фраза, не сам код.
    [Theory]
    [InlineData(null)]
    [InlineData("нет_такого_кода")]
    public void Ask_StoppedНеизвестныйИлиПустойКодДаётОбщуюФразу(string? code) =>
        FeatureBrief.Build(RunWithAttention("stopped", code, NOW, null, null), null, null, NOW)
            .Ask!.Text.Should().Be(StopReasonPhrases.Fallback);

    // Инвариант: у каждого кода ExecutorStopClassifier есть своя фраза в словаре. Коды берутся
    // рефлексией по const string класса — public И internal (хвосты ревью R-E4, минор 3: код,
    // объявленный internal const, тоже обязан иметь фразу; тот же образец, что таблица
    // FeatureStates). Новый код без строки словаря или удалённая строка — красный этот тест (c2).
    // Мутация: добавить в классификатор internal const без фразы — тест красный.
    [Fact]
    public void Ask_КаждыйКодОстановкиИмеетСвоюФразу()
    {
        var codes = typeof(ExecutorStopClassifier)
            .GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();

        codes.Should().NotBeEmpty("в классификаторе есть коды-константы");
        foreach (var code in codes)
        {
            var phrase = StopReasonPhrases.PhraseFor(code);
            phrase.Should().NotBeNullOrEmpty().And.NotBe(StopReasonPhrases.Fallback,
                $"код «{code}» не имеет своей фразы в StopReasonPhrases");
            phrase.Length.Should().BeLessThanOrEqualTo(TaskManager.AwaitingAskMaxLength);
        }
    }

    // Лимит text ≤ 120 — тот же, что у хранимого текста ожидания (TaskManager.AwaitingAskMaxLength);
    // здесь — защита от данных, записанных минуя продюсера. Мутация: убрать clamp — тест красный (150).
    [Fact]
    public void Ask_ЛимитТекста120()
    {
        var text = FeatureBrief.Build(RunWithAttention("wait", new string('р', 150), NOW, null, null),
            null, null, NOW).Ask!.Text;

        text.Should().HaveLength(TaskManager.AwaitingAskMaxLength);
    }

    // Хвосты ревью R-E4 (минор 2): обрезка под лимит — по границе text-элемента (StringInfo), а не
    // по индексу UTF-16: суррогатная пара не рвётся пополам. Фикстура: 119 BMP-символов + эмодзи
    // (2 единицы UTF-16, позиции 119–120 → в 121-ю единицу не влезает) + хвост: префикс — 119
    // символов, граница ДО эмодзи; одинокого high surrogate в результате нет.
    // Мутация: вернуть text[..120] — тест красный (120 единиц, обрезанная пополам пара).
    [Fact]
    public void Ask_ЛимитНеРвётСуррогатнуюПару()
    {
        var text = FeatureBrief.Build(RunWithAttention("wait", new string('р', 119) + "\U0001F600" + new string('с', 10),
            NOW, null, null), null, null, NOW).Ask!.Text;

        text.Should().HaveLength(119, "эмодзи занимает 121-ю единицу — граница обрезки до него");
        text.Should().NotContain("\uD83D", "одинокий high surrogate — разорванная суррогатная пара");
        text.Should().EndWith(new string('р', 119));
    }

    // Виды attention — ровно три; иной (аномальные данные) — null: фразы неизвестному смыслу нет
    // и её не выдумываем.
    [Fact]
    public void Ask_НеизвестныйВидТревогиNull() =>
        FeatureBrief.Build(RunWithAttention("whatever", "текст", NOW, null, null), null, null, NOW)
            .Ask.Should().BeNull();

    // ─── queueWaitMinutes (B8, MAJOR-1 ревью R-F8): время в очереди, не из work-интервалов ──────

    // c1/c2: queued — время от момента постановки удержания (FeatureHold.Since), а не из таймлайна.
    // Фикстура намеренно даёт два разных числа: очередь 25 мин, workWall(7) — подмена источника
    // (ElapsedMinutes вместо Hold.Since) даёт 7 вместо 25, тест красный. elapsed при этом остаётся
    // work-временем: поля отдельные.
    [Fact]
    public void QueueWaitMinutes_QueuedБерётВремяОтМоментаУдержанияАНеИзWorkИнтервалов()
    {
        var run = RunOf(FeatureStates.Paused,
            new FeatureHold(FeatureHoldReasons.Queue, NOW.AddMinutes(-25), null, true));

        var brief = FeatureBrief.Build(run, TimelineOf(elapsed: 40, workWall: 7), null, NOW);

        brief.QueueWaitMinutes.Should().Be(25, "25 минут от Hold.Since, не workWall(7) и не elapsed(40)");
        brief.ElapsedMinutes.Should().Be(7, "elapsed остаётся work-временем по часам стены — отдельное поле");
    }

    // c1: поле заполнено ТОЛЬКО у kind=queued: без удержания, на паузе человека и у queue-удержания
    // вне paused (категория running) — null. Мутация: убрать фильтр kind в QueueWaitOf — красный.
    [Fact]
    public void QueueWaitMinutes_ТолькоУQueued_УОстальныхNull()
    {
        var queue = new FeatureHold(FeatureHoldReasons.Queue, NOW.AddMinutes(-25), null, true);
        var manual = new FeatureHold(FeatureHoldReasons.Manual, NOW.AddMinutes(-25), null, true);

        FeatureBrief.Build(RunOf(FeatureStates.Paused), null, null, NOW)
            .QueueWaitMinutes.Should().BeNull("без удержания — поля нет");
        FeatureBrief.Build(RunOf(FeatureStates.Paused, manual), null, null, NOW)
            .QueueWaitMinutes.Should().BeNull("пауза человека — не очередь");
        FeatureBrief.Build(RunOf(FeatureStates.Running, queue), null, null, NOW)
            .QueueWaitMinutes.Should().BeNull("queue-удержание вне paused — категория не queued");
        FeatureBrief.Build(RunOf(FeatureStates.Draft, queue), null, null, NOW)
            .QueueWaitMinutes.Should().BeNull("черновик — не очередь");
    }

    // Граница [Since, now): удержание, поставленное ровно в момент ответа, — 0 (поле есть, не null).
    // Future Since (аномалия данных) — тоже 0: отрицательного времени не обещаем.
    // Мутация: убрать clamp к 0 — второй случай красный (-5).
    [Fact]
    public void QueueWaitMinutes_ГраницаИБудущийУдержаниеДаютНоль()
    {
        FeatureBrief.Build(RunOf(FeatureStates.Paused,
                new FeatureHold(FeatureHoldReasons.Queue, NOW, null, true)), null, null, NOW)
            .QueueWaitMinutes.Should().Be(0, "ровно в момент ответа — 0, не null");
        FeatureBrief.Build(RunOf(FeatureStates.Paused,
                new FeatureHold(FeatureHoldReasons.Queue, NOW.AddMinutes(5), null, true)), null, null, NOW)
            .QueueWaitMinutes.Should().Be(0, "будущий Since — аномалия данных, отрицательного времени нет");
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

        // kind=merged (смержена с открытым эпиком) — оценки нет: фича уже смержена,
        // вместо «осталось» — итог Summary.
        var mergedOpen = FeatureBrief.Build(RunWithPlan(FeatureStates.Merged, 1, 1, 1), null, typical, NOW);
        mergedOpen.Kind.Should().Be(FeatureBrief.Kinds.Merged);
        mergedOpen.Estimate.Should().BeNull("merged с открытым эпиком — вместо оценки итог Summary");

        FeatureBrief.Build(RunWithPlan(FeatureStates.Merged, 1, 1, 1, closed: true), null, typical, NOW)
            .Estimate.Should().BeNull("merged с эпиком Done — категория closed");
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
    // он не должен выйти ни при каком раскладе. runningStage — идущая задача в этой стадии
    // (FS-V1: стадия идущей задачи и есть текущая); без него идущих нет — работает вторая
    // половина правила (первая активная стадия).
    private static FeatureRun RunWithVoice(string activeStage, string state, FeatureHold? hold,
        string stageAtWrite, string stateAtWrite, string? runningStage = null) => new()
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
        Now = runningStage is null
            ? new List<FeatureNow>()
            : new List<FeatureNow>
            {
                new() { TaskId = "r1", Stage = runningStage, PersonaId = "Глеб", StartedAt = NOW.AddHours(-1) },
            },
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
            .Should().BeEquivalentTo("Text", "At", "ByPersonaId", "ByUser", "StageAtWrite", "Stale", "Estimate");
    }

    // Категория на момент записи не подтверждается (штамп вне таблицы FeatureStates) — голос
    // считается устаревшим (fail-closed), даже когда стадия не менялась.
    [Fact]
    public void Note_НеизвестныйШтампСостоянияСчитаетсяСменойКатегории()
    {
        var run = RunWithVoice("prod", FeatureStates.Running, null, "prod", "фиктивное_состояние");

        FeatureBrief.Build(run, null, null, NOW).Note!.Stale.Should().BeTrue();
    }

    // B14 (возврат FIN 74722f13): единая база штампа и StaleOf (FeatureBrief.ActiveStageOf). Голос,
    // записанный на состоянии без идущих задач (not_started/idle/waiting/stopped — Now пуст), не
    // устаревший с момента записи: стадия не менялась, категория тоже. До правки такой голос
    // штамповался "" (Now[0] пуст) и сверялся с активной стадией → stale с первой секунды.
    // Мутация: сверять StaleOf с Now[0] вместо ActiveStageOf — тест красный ("prod" ≠ "").
    [Theory]
    [InlineData(FeatureStates.NotStarted)]
    [InlineData(FeatureStates.Idle)]
    [InlineData(FeatureStates.Waiting)]
    [InlineData(FeatureStates.Stopped)]
    public void Note_ГолосНаСостоянииБезИдущихЗадачейНеСразуУстаревший(string state)
    {
        var run = RunWithVoice("prod", state, null, "prod", state);

        var brief = FeatureBrief.Build(run, null, null, NOW);

        brief.Now.Should().BeNull("фикстура: ни идущих задач, ни тревоги");
        brief.Note!.StageAtWrite.Should().Be("prod");
        brief.Note.Stale.Should().BeFalse($"голос записан на активной стадии состояния {state}");
    }

    // ─── B15 (дефект 415c1c80): голос, записанный в очереди, не «устаревший» с первой секунды ──────────

    // Штамп голоса — КАТЕГОРИЯ (brief.kind), а не сырое состояние: у фичи в очереди состояние
    // «paused» (причина удержания не хранится), а категория — «queued». До правки штамп был
    // «paused», текущая категория — «queued» ⇒ stale сразу после записи.
    [Fact]
    public void Note_ГолосЗаписанныйВОчередиСразуСвежий()
    {
        var queue = new FeatureHold(FeatureHoldReasons.Queue, NOW, null, true);
        var run = RunWithVoice("prod", FeatureStates.Paused, queue, "prod", "queued");

        var brief = FeatureBrief.Build(run, null, null, NOW);

        brief.Kind.Should().Be("queued", "фикстура: удержание с причиной queue");
        brief.Note!.Stale.Should().BeFalse("категория не менялась — голос свежий");
    }

    // Смена категории из очереди (queued → paused(manual)) — голос устарел: стадия та же.
    [Fact]
    public void Note_СменаКатегорииОчередьВПаузуДаётStale()
    {
        var manual = new FeatureHold(FeatureHoldReasons.Manual, NOW, null, true);
        var run = RunWithVoice("prod", FeatureStates.Paused, manual, "prod", "queued");

        FeatureBrief.Build(run, null, null, NOW).Note!.Stale.Should().BeTrue(
            "очередь и пауза человека — разные категории (queued ≠ paused)");
    }

    // Смена категории из очереди (queued → running) — голос устарел: стадия та же.
    [Fact]
    public void Note_СменаКатегорииОчередьВРаботуДаётStale()
    {
        var queue = new FeatureHold(FeatureHoldReasons.Queue, NOW, null, true);
        var run = RunWithVoice("prod", FeatureStates.Running, queue, "prod", "queued");

        FeatureBrief.Build(run, null, null, NOW).Note!.Stale.Should().BeTrue(
            "фича поехала — категория сменилась (queued ≠ running)");
    }

    // Характеризация старого формата: голоса, записанные ДО B15, хранят СОСТОЯНИЕ («paused»,
    // «stopped»…) — они достраиваются до категории таблицей KindByState и ведут себя как раньше.
    [Theory]
    [InlineData(FeatureStates.Paused, "paused")]
    [InlineData(FeatureStates.Stopped, "stopped")]
    [InlineData(FeatureStates.Waiting, "waiting")]
    public void Note_СтарыйШтампСостоянияДостраиваетсяДоКатегории(string state, string legacyStamp)
    {
        var manual = new FeatureHold(FeatureHoldReasons.Manual, NOW, null, true);
        var run = RunWithVoice("prod", state, state == FeatureStates.Paused ? manual : null,
            "prod", legacyStamp);

        FeatureBrief.Build(run, null, null, NOW).Note!.Stale.Should().BeFalse(
            $"старый штамп «{legacyStamp}» достраивается до категории текущего состояния");
    }

    // Штамп для записи голоса — та же величина, что brief.kind (FeatureBrief.KindAtWriteOf).
    [Fact]
    public void KindAtWriteOf_ДаётКатегориюАНеСостояние()
    {
        var queue = new FeatureHold(FeatureHoldReasons.Queue, NOW, null, true);
        var manual = new FeatureHold(FeatureHoldReasons.Manual, NOW, null, true);

        FeatureBrief.KindAtWriteOf(RunOf(FeatureStates.Paused, queue)).Should().Be("queued",
            "очередь — категория, а не «paused»");
        FeatureBrief.KindAtWriteOf(RunOf(FeatureStates.Paused, manual)).Should().Be("paused");
        FeatureBrief.KindAtWriteOf(RunOf(FeatureStates.Running)).Should().Be("running");
        // Состояние вне таблицы — сырым state: запись голоса не падает, а голос будет устаревшим.
        FeatureBrief.KindAtWriteOf(RunOf("выдуманное")).Should().Be("выдуманное");
    }

    // Сторож: новая категория без строки в Kinds.All — красный тест (иначе её не узнает штамп).
    [Fact]
    public void Kinds_All_ПокрываетВсеКатегории()
    {
        var kinds = typeof(FeatureBrief.Kinds)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();

        kinds.Should().NotBeEmpty();
        FeatureBrief.Kinds.All.Should().BeEquivalentTo(kinds,
            "в наборе категорий — ровно по строке на каждую константу FeatureBrief.Kinds");
    }

    // ─── FS-V1 (разрез §2.1 П2): одна «текущая стадия» на всех потребителей ───────────────────────

    // Единая функция (FeatureBrief.CurrentStageOf): стадия первой идущей задачи прежде «первой
    // активной стадии» конвейера. Фикстура — ровно то, что до FS-V1 давало расхождение: stages[]
    // активна на prod (первая непройденная), а работа идёт на review. Мутация: взять в
    // CurrentStageOf только FirstActiveStageOf (без Now[0]) — тест красный ("prod" ≠ "review").
    [Fact]
    public void CurrentStageOf_СтадияИдущейЗадачиПреждеПервойАктивной()
    {
        var run = RunWithVoice("prod", FeatureStates.Running, null, "prod", "running", runningStage: "review");

        FeatureBrief.CurrentStageOf(run).Should().Be("review",
            "работа идёт на review — текущая стадия review, а не первая активная prod");
        FeatureBrief.ActiveStageOf(run).Should().Be("review",
            "внешний вход штампа голоса (TasksController/TasksToolset) — та же база, своего правила он не держит");
        FeatureBrief.Build(run, null, null, NOW).Now!.Stage.Should().Be("review", "brief.now.stage");
    }

    // Идущих задач нет — работает вторая половина правила: первая активная стадия (B14: голос на
    // паузе/idle/waiting/stopped не устаревший с первой секунды). Мутация: убрать fallback
    // FirstActiveStageOf (пусто при пустом Now) — тест «ГолосНаСостоянииБезИдущихЗадачей…» красный.
    [Fact]
    public void CurrentStageOf_БезИдущихЗадачей_ПерваяАктивнаяСтадия()
    {
        var run = RunWithVoice("prod", FeatureStates.Running, null, "prod", "running");

        FeatureBrief.CurrentStageOf(run).Should().Be("prod", "идущих нет — база та же, что у B14");
        FeatureBrief.ActiveStageOf(run).Should().Be("prod");
    }

    // Следствие, принятое разрезом: голос, записанный со штампом «первой активной» стадии (prod),
    // пока работа шла на review, по новому правилу устаревает. До FS-V1 он был свежим: штамп и
    // сравнение держались одной «первой активной». Мутация: вернуть в StaleOf первую активную
    // стадию вместо CurrentStageOf — тест красный (Stale = false).
    [Fact]
    public void Note_ШтампПервойАктивнойПриРаботеВДругойСтадииДаётStale()
    {
        var run = RunWithVoice("prod", FeatureStates.Running, null, "prod", "running", runningStage: "review");

        var note = FeatureBrief.Build(run, null, null, NOW).Note!;

        note.StageAtWrite.Should().Be("prod", "хранимый штамп не переписывается");
        note.Stale.Should().BeTrue(
            "текущая стадия — review (стадия идущей задачи), голос написан на prod");
    }

    // ─── updatedAt (B6, дефект f2da9354): время последнего записанного события состояния, не момента ответа ─

    // Фикстура задачи: отметки событий состояния (все даты старше NOW) + touchedAt — UpdatedAt задачи
    // (по умолчанию NOW: свежее любого события состояния — реализация, взявшая время касания записи,
    // красный тест).
    private static TaskItem TaskOf(string id, string role = TaskRoles.Step, DateTime? createdAt = null,
        DateTime? startedAt = null, DateTime? stoppedAt = null, DateTime? completedAt = null,
        DateTime? awaitingSince = null, DateTime? touchedAt = null) => new()
    {
        Id = id, Role = role,
        CreatedAt = createdAt ?? NOW.AddHours(-6),
        ClaudeStartedAt = startedAt, ExecutorStoppedAt = stoppedAt, CompletedAt = completedAt,
        AwaitingHumanSince = awaitingSince, UpdatedAt = touchedAt ?? NOW,
    };

    private static FeatureRun RunWithTasks(string state, TaskItem epic, params TaskItem[] children) => new()
    {
        FeatureId = "ep", State = state, Epic = epic, Steps = [.. children],
    };

    // c1 и сам случай дефекта: исполнитель встал 3 часа назад — строка «обновлено N назад» должна
    // печатать 3 часа, а не «только что»: updatedAt — момент остановки, computedAt остаётся моментом ответа.
    [Fact]
    public void UpdatedAt_РавенПоследнемуСобытиюСостоянияАНеМоментуОтвета()
    {
        var run = RunWithTasks(FeatureStates.Stopped,
            TaskOf("ep", TaskRoles.Feature),
            TaskOf("t1", createdAt: NOW.AddHours(-5), startedAt: NOW.AddHours(-5)),
            TaskOf("t2", createdAt: NOW.AddHours(-4), startedAt: NOW.AddHours(-4), stoppedAt: NOW.AddHours(-3)));

        var brief = FeatureBrief.Build(run, null, null, NOW);

        brief.UpdatedAt.Should().Be(NOW.AddHours(-3),
            "последнее событие состояния — остановка исполнителя (3 часа назад), не момент ответа");
        brief.ComputedAt.Should().Be(NOW, "computedAt остаётся моментом ответа — отдельное поле");
    }

    // c2: состояние не меняется — поле не «прыгает» между прогонами: два Build с разным «now» дают
    // один и тот же updatedAt при разных computedAt.
    [Fact]
    public void UpdatedAt_НеПрыгаетМеждуПрогонамиПриНеизменномСостоянии()
    {
        var run = RunWithTasks(FeatureStates.Stopped,
            TaskOf("ep", TaskRoles.Feature),
            TaskOf("t1", createdAt: NOW.AddHours(-4), startedAt: NOW.AddHours(-4), stoppedAt: NOW.AddHours(-3)));

        var b1 = FeatureBrief.Build(run, null, null, NOW);
        var b2 = FeatureBrief.Build(run, null, null, NOW.AddMinutes(30));

        b2.UpdatedAt.Should().Be(b1.UpdatedAt).And.Be(NOW.AddHours(-3),
            "события состояния не менялись — поле не следует за computedAt");
        b2.ComputedAt.Should().Be(NOW.AddMinutes(30));
    }

    // c2: состояние меняется — поле меняется. Новая закрытая задача (CompletedAt свежее всего)
    // переносит updatedAt на себя.
    [Fact]
    public void UpdatedAt_МеняетсяКогдаСостояниеМеняется()
    {
        var epic = TaskOf("ep", TaskRoles.Feature);
        var firstDone = TaskOf("t1", createdAt: NOW.AddHours(-2), startedAt: NOW.AddHours(-2),
            completedAt: NOW.AddHours(-2));
        FeatureBrief.Build(RunWithTasks(FeatureStates.Idle, epic, firstDone), null, null, NOW)
            .UpdatedAt.Should().Be(NOW.AddHours(-2), "до изменения — время закрытия первой задачи");

        var secondDone = TaskOf("t2", createdAt: NOW.AddHours(-1), startedAt: NOW.AddHours(-1),
            completedAt: NOW.AddMinutes(-10));
        FeatureBrief.Build(RunWithTasks(FeatureStates.Idle, epic, firstDone, secondDone), null, null, NOW)
            .UpdatedAt.Should().Be(NOW.AddMinutes(-10), "после изменения — время нового события");
    }

    // Поле — не TaskItem.UpdatedAt: настройки задачи (её UpdatedAt свежее любого события состояния —
    // пометили метку) время состояния не двигают. Мутация: заменить в UpdatedAtOf события на t.UpdatedAt.
    [Fact]
    public void UpdatedAt_НастройкиЗадачиЕгоНеДвигают()
    {
        var run = RunWithTasks(FeatureStates.Idle,
            TaskOf("ep", TaskRoles.Feature, createdAt: NOW.AddHours(-5)),
            TaskOf("t1", createdAt: NOW.AddHours(-3), completedAt: NOW.AddHours(-3), touchedAt: NOW));

        FeatureBrief.Build(run, null, null, NOW).UpdatedAt.Should().Be(NOW.AddHours(-3),
            "запись тронули в NOW (настройка), состояние не изменилось");
    }

    // Пауза эпика — событие состояния (paused/draft): Hold.Since свежее событий задач — updatedAt он.
    [Fact]
    public void UpdatedAt_ПаузаЭпицаЯвляетсяСобытиемСостояния()
    {
        var run = new FeatureRun
        {
            FeatureId = "ep",
            State = FeatureStates.Paused,
            Epic = TaskOf("ep", TaskRoles.Feature),
            Hold = new FeatureHold(FeatureHoldReasons.Manual, NOW.AddHours(-1), null, true),
            Steps = [TaskOf("t1", createdAt: NOW.AddHours(-4), completedAt: NOW.AddHours(-4))],
        };

        FeatureBrief.Build(run, null, null, NOW).UpdatedAt.Should().Be(NOW.AddHours(-1),
            "фичу поставили на паузу час назад — это последнее изменение состояния");
    }

    // Событий состояния нет вовсе (run без эпика и без задач — тестовая фикстура): честный null,
    // а не «сейчас» — подставить now значит повторить обман дефекта. Создание фичи — её первое
    // событие состояния: run только с эпиком — CreatedAt эпика (тот же якорь, что quietAnchor в Quiet).
    [Fact]
    public void UpdatedAt_БезСобытийСостоянияNull_АМоментСозданияЭпикаЯкорь()
    {
        FeatureBrief.Build(RunOf(FeatureStates.Idle), null, null, NOW).UpdatedAt.Should().BeNull(
            "событий нет — поле честный null, не computedAt");

        var epicOnly = TaskOf("ep", TaskRoles.Feature, createdAt: NOW.AddHours(-10));
        FeatureBrief.Build(RunWithTasks(FeatureStates.NotStarted, epicOnly), null, null, NOW)
            .UpdatedAt.Should().Be(NOW.AddHours(-10), "фичу создали 10 часов назад — это её состояние");
    }

    // Характеризующий тест (R-B3B6 замечание 2): снятие паузы/ожидания меняет состояние, но события
    // не создаёт — вклад отметки исчезает из максимума, updatedAt откатывается к предыдущему событию
    // и может уменьшиться. Сценарий Глеба: финал принят в 10:00 (NOW−2ч), пауза поставлена в 12:00
    // (NOW−1ч), снята в 12:05 (NOW) → updatedAt снова 10:00, а не 12:05. Код поведения не меняется —
    // тест описывает то, что есть (та же логика для ClearAwaitingHuman: TaskManager.cs:1357).
    [Fact]
    public void UpdatedAt_СнятиеПаузыИОжиданияНеСоздаётСобытияЗначениеОткатывается()
    {
        var epic = TaskOf("ep", TaskRoles.Feature);
        var finalAccepted = TaskOf("f", TaskRoles.Final, createdAt: NOW.AddHours(-8),
            startedAt: NOW.AddHours(-8), completedAt: NOW.AddHours(-2));

        // До паузы: последнее записанное событие — принятие финала.
        var beforeHold = new FeatureRun
        {
            FeatureId = "ep", State = FeatureStates.MergePending, Epic = epic, Final = finalAccepted,
        };
        FeatureBrief.Build(beforeHold, null, null, NOW).UpdatedAt.Should().Be(NOW.AddHours(-2),
            "последнее событие — принятие финала (10:00)");

        // Пауза час назад: отметка Hold.Since свежее принятия — updatedAt идёт вперёд.
        var withHold = new FeatureRun
        {
            FeatureId = "ep", State = FeatureStates.Paused, Epic = epic, Final = finalAccepted,
            Hold = new FeatureHold(FeatureHoldReasons.Manual, NOW.AddHours(-1), null, true),
        };
        FeatureBrief.Build(withHold, null, null, NOW).UpdatedAt.Should().Be(NOW.AddHours(-1),
            "последнее событие — постановка паузы (12:00)");

        // Пауза снята (12:05): новой отметки нет, вклад Hold.Since исчезает из максимума —
        // значение откатывается к принятию финала, т.е. уменьшается: «ровно моменты изменений» — не про это.
        var holdCleared = new FeatureRun
        {
            FeatureId = "ep", State = FeatureStates.MergePending, Epic = epic, Final = finalAccepted,
        };
        FeatureBrief.Build(holdCleared, null, null, NOW).UpdatedAt.Should().Be(NOW.AddHours(-2),
            "снятие паузы события не создаёт: значение откатывается к 10:00, а не 12:05");

        // То же со снятием ожидания: AwaitingHumanSince — событие, его снятие — нет.
        var waiting = TaskOf("t1", createdAt: NOW.AddHours(-5), startedAt: NOW.AddHours(-5),
            stoppedAt: NOW.AddHours(-4), awaitingSince: NOW.AddHours(-3));
        FeatureBrief.Build(RunWithTasks(FeatureStates.Waiting, epic, waiting), null, null, NOW)
            .UpdatedAt.Should().Be(NOW.AddHours(-3), "последнее событие — ожидание человека");
        var waitCleared = TaskOf("t1", createdAt: NOW.AddHours(-5), startedAt: NOW.AddHours(-5),
            stoppedAt: NOW.AddHours(-4));
        FeatureBrief.Build(RunWithTasks(FeatureStates.Stopped, epic, waitCleared), null, null, NOW)
            .UpdatedAt.Should().Be(NOW.AddHours(-4),
            "снятие ожидания события не создаёт: значение откатывается к остановке исполнителя");
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

    // ─── оценка оркестратора в голосе (макет 2 сводки): agreement, etaEndsAt ───────────

    private static FeatureRun RunWithEstimate(string state, FeatureHold? hold, FeatureEstimate? estimate,
        string stateAtWrite = "running")
    {
        var run = RunWithVoice("prod", state, hold, "prod", stateAtWrite);
        run.Epic!.Note = run.Epic.Note! with { Estimate = estimate };
        return run;
    }

    private static FeatureEstimate Est(int? eta = 120) =>
        new(["ревью", "проверка"], eta, "дольше всех — фронт");

    private static FeatureBrief.BriefEstimate History(int remaining = 120, int p25 = 90, int p75 = 150) =>
        new(remaining, p25, p75, "history", 5);

    [Fact]
    public void NoteEstimate_БезОценкиNull_ПоляИзХранимой()
    {
        FeatureBrief.Build(RunWithEstimate(FeatureStates.Running, null, null), null, null, NOW)
            .Note!.Estimate.Should().BeNull();

        var e = FeatureBrief.Build(RunWithEstimate(FeatureStates.Running, null, Est()), null, null, NOW)
            .Note!.Estimate!;
        (e.EtaMinutes, e.Why).Should().Be((120, "дольше всех — фронт"));
        e.Remaining.Should().Equal("ревью", "проверка");
    }

    [Fact]
    public void NoteEstimate_EtaEndsAtОтМоментаЗаписиАНеОтСейчас()
    {
        var run = RunWithEstimate(FeatureStates.Running, null, Est(eta: 90));
        var at = run.Epic!.Note!.At;
        FeatureBrief.Build(run, null, null, NOW).Note!.Estimate!.EtaEndsAt.Should().Be(at.AddMinutes(90));
        FeatureBrief.Build(run, null, null, NOW.AddHours(5)).Note!.Estimate!.EtaEndsAt.Should().Be(at.AddMinutes(90),
            "срок не уползает вперёд с обновлением страницы");
    }

    [Fact]
    public void NoteEstimate_EtaEndsAtNullБезСрока() =>
        FeatureBrief.Build(RunWithEstimate(FeatureStates.Running, null, Est(eta: null)), null, null, NOW)
            .Note!.Estimate!.EtaEndsAt.Should().BeNull();

    [Fact]
    public void NoteEstimate_EtaEndsAtNullНаПаузеВОчередиИВЖдётЧеловека()
    {
        var manual = new FeatureHold(FeatureHoldReasons.Manual, NOW.AddHours(-1), null, true);
        var queue = new FeatureHold(FeatureHoldReasons.Queue, NOW.AddHours(-1), null, false);
        FeatureBrief.Build(RunWithEstimate(FeatureStates.Paused, manual, Est(), "paused"), null, null, NOW)
            .Note!.Estimate!.EtaEndsAt.Should().BeNull("пауза");
        FeatureBrief.Build(RunWithEstimate(FeatureStates.Paused, queue, Est(), "queued"), null, null, NOW)
            .Note!.Estimate!.EtaEndsAt.Should().BeNull("очередь");
        FeatureBrief.Build(RunWithEstimate(FeatureStates.Waiting, null, Est(), "waiting_you"), null, null, NOW)
            .Note!.Estimate!.EtaEndsAt.Should().BeNull("ждёт человека");
    }

    // Правило двух оценок: вне [p25, p75] И разница с remainingMinutes ≥ 30 мин. Концы разброса
    // включены внутрь: ровно p25/p75 — within.
    [Theory]
    [InlineData(120, 120, 90, 150, "within")]
    [InlineData(150, 120, 90, 150, "within")]   // ровно p75
    [InlineData(90, 120, 90, 150, "within")]    // ровно p25
    [InlineData(151, 120, 90, 150, "above")]    // вне разброса, разница 31
    [InlineData(89, 120, 90, 150, "below")]     // вне разброса, разница 31
    public void NoteEstimate_Agreement_Границы(int eta, int remaining, int p25, int p75, string expected) =>
        FeatureBrief.AgreementOf(eta, History(remaining, p25, p75)).Should().Be(expected);

    [Theory]
    [InlineData(160, 140, 100, 150, "within")]  // вне разброса (>150), но разница 20 < 30 — без метки
    [InlineData(170, 140, 100, 150, "above")]   // вне и разница ровно 30
    [InlineData(110, 140, 100, 150, "within")]  // внутри разброса, разница 30 — within
    [InlineData(60, 140, 100, 150, "below")]
    public void NoteEstimate_Agreement_ОбаУсловия(int eta, int remaining, int p25, int p75, string expected) =>
        FeatureBrief.AgreementOf(eta, History(remaining, p25, p75)).Should().Be(expected);

    [Fact]
    public void NoteEstimate_Agreement_NullБезИсторииИлиСрока()
    {
        FeatureBrief.AgreementOf(100, null).Should().BeNull();
        FeatureBrief.AgreementOf(null, History()).Should().BeNull();
    }

    [Fact]
    public void NoteEstimate_УстареваниеТоЖеЧтоУГолоса()
    {
        var run = RunWithEstimate(FeatureStates.Running, null, Est());
        run.Epic!.Note = run.Epic.Note! with { StageAtWrite = "review" };
        var note = FeatureBrief.Build(run, null, null, NOW).Note!;
        note.Stale.Should().BeTrue("оценка стареет вместе с голосом: отдельного признака нет");
        note.Estimate.Should().NotBeNull();
    }

    [Fact]
    public void InitialEtaMinutes_ИзЭпика()
    {
        var run = RunWithEstimate(FeatureStates.Running, null, Est());
        FeatureBrief.Build(run, null, null, NOW).InitialEtaMinutes.Should().BeNull();
        run.Epic!.InitialEtaMinutes = 300;
        FeatureBrief.Build(run, null, null, NOW).InitialEtaMinutes.Should().Be(300);
    }

    // ─── eta (FS-V2): один срок с источником и признаком «часы стоят» ────────────────────────────

    // Фикстура: run с нарезанным планом (историческая оценка) и по желанию — оценкой оркестратора
    // в голосе эпика (голос на NOW−2ч; штамп совпадает с категорией — тест про eta, не про stale),
    // вовсе без голоса (noVoice) и тревогой (вид определяет состояние, как в RunWithAttention).
    private static FeatureRun RunWithEta(string state, int work, int notCreated, int review,
        FeatureAttention? attention = null, FeatureEstimate? estimate = null, bool noVoice = false) => new()
    {
        FeatureId = "ep",
        State = state,
        Attention = attention,
        Epic = noVoice
            ? new TaskItem { Id = "ep", Role = TaskRoles.Feature }
            : new TaskItem
            {
                Id = "ep",
                Role = TaskRoles.Feature,
                Note = new FeatureNote("ревью началось", NOW.AddHours(-2), "sess-1", true, "prod", "running")
                    with { Estimate = estimate },
            },
        StepProgress = new FeatureStepProgress
        {
            PlanState = "cut", Total = work + notCreated + review,
            Work = work, NotCreated = notCreated, Review = review,
        },
    };

    private static FeatureAttention AttentionOf(string kind) => new()
    {
        Kind = kind, TaskId = "t1", Since = NOW.AddHours(-1), Reason = "причина", PersonaId = "denis",
    };

    // Случай 1 (c1): «note есть» — оценка оркестратора главнее истории: минуты и дата из
    // note.estimate (то же вычисленное подполе), признака стоящих часов нет.
    [Fact]
    public void Eta_ОценкаОркестратораГлавнееИстории_МинутыИДатаИзNote()
    {
        var run = RunWithEta(FeatureStates.Running, 1, 0, 0, estimate: Est(eta: 90));
        var at = run.Epic!.Note!.At;

        var eta = FeatureBrief.Build(run, null, TypicalOf(10, 8, 12, 30, 20, 40, 5), NOW).Eta!;

        eta.Source.Should().Be("note");
        eta.Minutes.Should().Be(90, "у истории тоже есть число (40) — приоритет у оценки оркестратора");
        eta.EndsAt.Should().Be(at.AddMinutes(90), "дата — от момента записи голоса, та же база, что у note.estimate");
        eta.ClockStopped.Should().BeNull("тревоги нет — часы идут");
    }

    // Случай 2 (c1): «note скрыт + история есть» — число истории, даты нет никогда.
    // «Скрыт» в обеих формах: голос есть, оценки в нём нет; и голоса нет вовсе.
    [Fact]
    public void Eta_БезОценкиВГолосеБерётИсторию_ДатыНикогдаНет()
    {
        var typical = TypicalOf(10, 8, 12, 30, 20, 40, 5);

        var hidden = FeatureBrief.Build(RunWithEta(FeatureStates.Running, 1, 0, 0), null, typical, NOW).Eta!;
        hidden.Source.Should().Be("history");
        hidden.Minutes.Should().Be(40, "10 × 1 + 30 — число истории");
        hidden.EndsAt.Should().BeNull("у истории даты нет: «осталось», а не «к чему»");
        hidden.ClockStopped.Should().BeNull();

        FeatureBrief.Build(RunWithEta(FeatureStates.Running, 1, 0, 0, noVoice: true), null, typical, NOW)
            .Eta!.Source.Should().Be("history");
    }

    // Случай 3 (c1): «ничего → null» — ни одной оценки ни из одного источника.
    [Fact]
    public void Eta_НиОднойОценкиNull()
    {
        // Голос без оценки + нет выборки (истории не из чего строить).
        FeatureBrief.Build(RunWithEta(FeatureStates.Running, 1, 0, 0), null, null, NOW).Eta.Should().BeNull();
        // Ни голоса, ни плана (обычная тестовая фикстура RunOf) — то же.
        FeatureBrief.Build(RunOf(FeatureStates.Running), null, null, NOW).Eta.Should().BeNull();
    }

    // Случай 4 (c1): «wait» — clockStopped=wait и endsAt=null; число оставшей работы при этом
    // сохраняется (часы стоят — работа до конца всё ещё есть).
    [Fact]
    public void Eta_ЖдётВасЧасыСтоятПризнакWaitИБезДаты()
    {
        var eta = FeatureBrief.Build(
            RunWithEta(FeatureStates.Waiting, 0, 1, 1, attention: AttentionOf("wait")),
            null, TypicalOf(10, 8, 12, 30, 20, 40, 5), NOW).Eta!;

        eta.Source.Should().Be("history");
        eta.Minutes.Should().Be(45, "10 × (0 + 1 + 0.5) + 30");
        eta.ClockStopped.Should().Be("wait");
        eta.EndsAt.Should().BeNull();
    }

    // Случай 5 (c1): «stopped» — clockStopped=stopped, даты нет. В том числе главный случай
    // правила: у note.estimate на stopped дата есть (EstimateOfNote её не гасит) — при стоящих
    // часах она null: отсутствие объяснено признаком, а не молча спрятано.
    [Fact]
    public void Eta_ОстановкаЧасыСтоятПризнакStoppedИДатаГаситсяДажеУОценкиNote()
    {
        var typical = TypicalOf(10, 8, 12, 30, 20, 40, 5);

        var fromNote = FeatureBrief.Build(
            RunWithEta(FeatureStates.Stopped, 1, 0, 0, attention: AttentionOf("stopped"), estimate: Est(eta: 90)),
            null, typical, NOW).Eta!;
        fromNote.Source.Should().Be("note");
        fromNote.Minutes.Should().Be(90);
        fromNote.ClockStopped.Should().Be("stopped");
        fromNote.EndsAt.Should().BeNull("дата note.estimate при стоящих часах гасится");

        // Та же оценка без тревоги — дата остаётся: гасит признак, а не состояние как таковое.
        var running = FeatureBrief.Build(RunWithEta(FeatureStates.Running, 1, 0, 0, estimate: Est(eta: 90)), null, typical, NOW).Eta!;
        running.EndsAt.Should().NotBeNull();

        var fromHistory = FeatureBrief.Build(
            RunWithEta(FeatureStates.Stopped, 1, 0, 0, attention: AttentionOf("stopped")), null, typical, NOW).Eta!;
        fromHistory.Source.Should().Be("history");
        fromHistory.Minutes.Should().Be(40);
        fromHistory.ClockStopped.Should().Be("stopped");
        fromHistory.EndsAt.Should().BeNull();
    }

    // Край: оценка без срока — не срок (её «что дальше» и why остаются в note.estimate), срок
    // берётся из истории: иначе карточка получила бы пустый срок там, где число есть.
    [Fact]
    public void Eta_ОценкаБезСрокаНеСрокБерётИсторию()
    {
        var eta = FeatureBrief.Build(
            RunWithEta(FeatureStates.Running, 1, 0, 0, estimate: Est(eta: null)), null, TypicalOf(10, 8, 12, 30, 20, 40, 5), NOW).Eta!;

        eta.Source.Should().Be("history");
        eta.Minutes.Should().Be(40);
        eta.EndsAt.Should().BeNull();
    }

    // «Мерж» — не «часы стоят» (план FS-V2): фича дошла до решения по мержу сама, работа
    // не встала. (Дата здесь гасится EstimateOfNote по kind=waiting_you — та же база, что у
    // note.estimate, к признаку отношения не имеет.)
    [Fact]
    public void Eta_МержНеЧасыСтоят()
    {
        var eta = FeatureBrief.Build(
            RunWithEta(FeatureStates.MergePending, 1, 0, 0, attention: AttentionOf("merge"), estimate: Est(eta: 90)),
            null, null, NOW).Eta!;

        eta.Source.Should().Be("note");
        eta.Minutes.Should().Be(90);
        eta.ClockStopped.Should().BeNull("merge — не причина стоящих часов");
    }

    // Инвариант: значения eta — ровно вычисленные подполя brief (note.estimate / estimate),
    // «второго расчёта» нет: расхождение основного срока карточки с её зеркалом — дефект.
    [Fact]
    public void Eta_ЗначенияРовноВычисленныеПодполяBrief_БезВторогоРасчёта()
    {
        var typical = TypicalOf(10, 8, 12, 30, 20, 40, 5);

        var withNote = FeatureBrief.Build(RunWithEta(FeatureStates.Running, 1, 0, 0, estimate: Est(eta: 90)), null, typical, NOW);
        withNote.Eta!.Minutes.Should().Be(withNote.Note!.Estimate!.EtaMinutes!.Value);
        withNote.Eta.EndsAt.Should().Be(withNote.Note.Estimate.EtaEndsAt);
        withNote.Eta.Source.Should().Be("note");

        var noNote = FeatureBrief.Build(RunWithEta(FeatureStates.Running, 1, 0, 0, noVoice: true), null, typical, NOW);
        noNote.Eta!.Minutes.Should().Be(noNote.Estimate!.RemainingMinutes);
        noNote.Eta.Source.Should().Be("history");
    }
}
