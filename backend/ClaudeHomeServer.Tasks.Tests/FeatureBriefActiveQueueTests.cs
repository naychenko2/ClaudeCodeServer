using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Tasks.Projector;

namespace ClaudeHomeServer.Tasks.Tests;

// Волна 6, B2: brief.active[] и brief.queueStartEstimate (макет 2, «Данные» таблицы 2–3).
//
// Мутации (проверены прогоном, см. отчёт задачи):
//  • active: убрать ветку Interrupted — тест «held» красный; поменять приоритет error/waiting_you —
//    тест «стоп и ожидание вместе» красный; убрать сортировку по группам — тест порядка красный;
//    пустить в active Todo/Done — тест «только в работе» красный;
//  • queueStartEstimate: сложить remaining без слотов (limit=1 всегда) — тест двух слотов красный;
//    не учитывать занятые слоты — тест «после занятой» красный; брать чужой проект — тест «другой
//    проект» красный; вернуть частичное число при null у фичи впереди — тест «нет данных» красный.
public class FeatureBriefActiveQueueTests
{
    private static readonly DateTime NOW = new(2026, 10, 25, 12, 0, 0, DateTimeKind.Utc);

    private static TaskItem Task(string id, string role, string status, string? stepRef = null) => new()
    {
        Id = id, Role = role, Status = Enum.Parse<TaskItemStatus>(status), StepRef = stepRef,
        CreatedAt = NOW.AddHours(-5), PersonaId = "p-" + id,
    };

    private static FeatureRun RunWith(string state, TaskItem epic, List<TaskItem>? steps = null,
        List<TaskItem>? reviews = null, List<string>? interrupted = null, FeatureHold? hold = null) => new()
    {
        FeatureId = epic.Id, Epic = epic, State = state, Hold = hold,
        Steps = steps ?? [], Reviews = reviews ?? [], Interrupted = interrupted ?? [],
        StepProgress = new FeatureStepProgress { PlanState = "cut", Total = 3, Work = 3 },
    };

    private static TaskItem Epic(string id, string project = "P") => new()
    {
        Id = id, Role = TaskRoles.Feature, ProjectId = project, Status = TaskItemStatus.InProgress,
        CreatedAt = NOW.AddHours(-9),
    };

    // ─── active ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Active_ТолькоЗадачиВРаботе_ТодоИДоунНеПопадают()
    {
        var work = Task("w", TaskRoles.Step, "InProgress", "B1");
        work.ClaudeStartedAt = NOW.AddMinutes(-12);
        work.Lane = "back";
        var todo = Task("t", TaskRoles.Step, "Todo", "B2");
        var done = Task("d", TaskRoles.Step, "Done", "B0");
        var run = RunWith(FeatureStates.Running, Epic("e"), [work, todo, done]);

        var active = FeatureBrief.Build(run, null, null, NOW).Active;

        active.Should().ContainSingle();
        active[0].Should().Be(new FeatureBrief.BriefActive(
            "w", "prod", "B1", "back", "p-w", "working", NOW.AddMinutes(-12), null));
    }

    [Fact]
    public void Active_РевьюЭтоReview_СтадияПроходитКакЕсть()
    {
        var rev = Task("r", TaskRoles.Review, "InProgress", "B1");
        rev.ClaudeStartedAt = NOW.AddMinutes(-3);
        var run = RunWith(FeatureStates.Running, Epic("e"), reviews: [rev]);

        var a = FeatureBrief.Build(run, null, null, NOW).Active.Single();

        a.State.Should().Be("review");
        a.Stage.Should().Be("review");
    }

    [Fact]
    public void Active_ОжиданиеЧеловека_WaitingYouСПричиной()
    {
        var t = Task("w", TaskRoles.Step, "InProgress", "B4");
        t.ClaudeStartedAt = NOW.AddHours(-1);
        t.AwaitingHumanSince = NOW.AddMinutes(-20);
        t.AwaitingHumanReason = "какой вариант выбрать";
        var run = RunWith(FeatureStates.Waiting, Epic("e"), [t]);

        var a = FeatureBrief.Build(run, null, null, NOW).Active.Single();

        a.State.Should().Be("waiting_you");
        a.Since.Should().Be(NOW.AddMinutes(-20));
        a.Reason.Should().Be("какой вариант выбрать");
    }

    [Fact]
    public void Active_СтопИОжиданиеВместе_ОшибкаВажнееКакУAttention()
    {
        var t = Task("w", TaskRoles.Step, "InProgress", "B4");
        t.ClaudeStartedAt = NOW.AddHours(-2);
        t.AwaitingHumanSince = NOW.AddMinutes(-50);
        t.ExecutorStoppedAt = NOW.AddMinutes(-30);
        t.ExecutorStopReason = "упала сборка";
        var run = RunWith(FeatureStates.Stopped, Epic("e"), [t]);

        var a = FeatureBrief.Build(run, null, null, NOW).Active.Single();

        a.State.Should().Be("error");
        a.Since.Should().Be(NOW.AddMinutes(-30));
        a.Reason.Should().Be("упала сборка");
    }

    [Fact]
    public void Active_ПрерваннаяПаузой_Held()
    {
        var t = Task("w", TaskRoles.Step, "InProgress", "B4");
        t.ClaudeStartedAt = NOW.AddHours(-2);
        t.ExecutorStoppedAt = NOW.AddMinutes(-10);
        t.ExecutorStopReason = "feature_held";
        var hold = new FeatureHold(FeatureHoldReasons.Manual, NOW.AddMinutes(-10), null, true);
        var run = RunWith(FeatureStates.Paused, Epic("e"), [t], interrupted: ["w"], hold: hold);

        var a = FeatureBrief.Build(run, null, null, NOW).Active.Single();

        a.State.Should().Be("held", "пауза гасит error: остановка — не поломка");
        a.Reason.Should().BeNull();
    }

    [Fact]
    public void Active_ЭпикЖдущийЧеловека_ВходитСтадияFeature()
    {
        var epic = Epic("e");
        epic.AwaitingHumanSince = NOW.AddMinutes(-5);
        epic.AwaitingHumanReason = "нужен ответ";
        var run = RunWith(FeatureStates.Waiting, epic);

        var a = FeatureBrief.Build(run, null, null, NOW).Active.Single();

        a.Stage.Should().Be("feature");
        a.State.Should().Be("waiting_you");
    }

    [Fact]
    public void Active_Порядок_ЖдётЧеловекаПотомОшибкаПотомСвежиеВверху()
    {
        var w1 = Task("w1", TaskRoles.Step, "InProgress", "B1");
        w1.ClaudeStartedAt = NOW.AddMinutes(-60);
        var w2 = Task("w2", TaskRoles.Step, "InProgress", "B2");
        w2.ClaudeStartedAt = NOW.AddMinutes(-5);
        var err = Task("err", TaskRoles.Step, "InProgress", "B3");
        err.ClaudeStartedAt = NOW.AddHours(-3);
        err.ExecutorStoppedAt = NOW.AddHours(-1);
        var wait = Task("wait", TaskRoles.Step, "InProgress", "B4");
        wait.ClaudeStartedAt = NOW.AddHours(-3);
        wait.AwaitingHumanSince = NOW.AddMinutes(-2);
        var run = RunWith(FeatureStates.Running, Epic("e"), [w1, w2, err, wait]);

        var ids = FeatureBrief.Build(run, null, null, NOW).Active.Select(a => a.TaskId);

        ids.Should().Equal("wait", "err", "w2", "w1");
    }

    // ─── queueStartEstimate ────────────────────────────────────────────────────────────────────

    // remaining = stepMedian(10) × work + хвост 0 → work=3 даёт 30 мин
    private static readonly FeatureTypical.TypicalFeatureStats Typical = new(
        Count: 5, WaitingCount: 0,
        Duration: new FeatureTypical.TypicalPercentiles(0, 0, 0), Waiting: null,
        Loops: new FeatureTypical.TypicalPercentiles(0, 0, 0), StepCount: 5,
        StepMinutes: new FeatureTypical.TypicalPercentiles(10, 8, 12),
        TailMinutes: new FeatureTypical.TypicalPercentiles(0, 0, 0));

    private static FeatureRun Running(string id, int work, string project = "P") => new()
    {
        FeatureId = id, Epic = Epic(id, project), State = FeatureStates.Running,
        StepProgress = new FeatureStepProgress { PlanState = "cut", Total = work, Work = work },
    };

    private static FeatureRun Queued(string id, int minutesAgo, int work, string project = "P",
        string planState = "cut") => new()
    {
        FeatureId = id, Epic = Epic(id, project), State = FeatureStates.Paused,
        Hold = new FeatureHold(FeatureHoldReasons.Queue, NOW.AddMinutes(-minutesAgo), null, true),
        StepProgress = new FeatureStepProgress { PlanState = planState, Total = work, Work = work },
    };

    private static DateTime? Start(FeatureRun subject, IReadOnlyList<FeatureRun> all, int limit = 1) =>
        FeatureBrief.Build(subject, null, Typical, NOW, all, limit).QueueStartEstimate;

    [Fact]
    public void QueueStart_ПослеЗанятойИОчередиВпереди_СуммаОстатков()
    {
        var a = Running("a", 3);       // 30
        var b = Queued("b", 30, 2);    // 20, раньше
        var c = Queued("c", 10, 1);    // наша
        Start(c, [a, b, c]).Should().Be(NOW.AddMinutes(50));
    }

    [Fact]
    public void QueueStart_ПерваяВОчереди_ЖдётТолькоЗанятую()
    {
        var a = Running("a", 3);
        var c = Queued("c", 10, 1);
        Start(c, [a, c]).Should().Be(NOW.AddMinutes(30));
    }

    [Fact]
    public void QueueStart_ДваСлота_ФичаВпередиБерётСвободныйСлот()
    {
        var a = Running("a", 3);       // слоты: [0, 30]
        var b = Queued("b", 30, 2);    // берёт свободный слот → 20
        var c = Queued("c", 10, 1);    // min(20, 30) = 20
        Start(c, [a, b, c], limit: 2).Should().Be(NOW.AddMinutes(20));
    }

    [Fact]
    public void QueueStart_СлотСвободен_СтартСейчас()
    {
        var c = Queued("c", 10, 1);
        Start(c, [c]).Should().Be(NOW);
    }

    [Fact]
    public void QueueStart_НетДанныхУФичиВпереди_Null()
    {
        var a = Running("a", 3);
        var b = Queued("b", 30, 2, planState: "pending"); // план не нарезан — remaining неизвестен
        var c = Queued("c", 10, 1);
        Start(c, [a, b, c]).Should().BeNull("частичное число обещать нельзя");
    }

    [Fact]
    public void QueueStart_БезВыборкиИстории_Null()
    {
        var a = Running("a", 3);
        var c = Queued("c", 10, 1);
        FeatureBrief.Build(c, null, null, NOW, [a, c], 1).QueueStartEstimate.Should().BeNull();
    }

    [Fact]
    public void QueueStart_ЧужойПроектНеСчитается()
    {
        var other = Running("o", 9, project: "Q");
        var c = Queued("c", 10, 1);
        Start(c, [other, c]).Should().Be(NOW);
    }

    [Fact]
    public void QueueStart_НеВОчереди_Null()
    {
        var a = Running("a", 3);
        Start(a, [a]).Should().BeNull();
        FeatureBrief.Build(Queued("c", 1, 1), null, Typical, NOW).QueueStartEstimate
            .Should().BeNull("набор фич проекта не передан");
    }
}
