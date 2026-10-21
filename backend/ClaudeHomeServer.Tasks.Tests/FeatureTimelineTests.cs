using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Tasks.Projector;

namespace ClaudeHomeServer.Tasks.Tests;

// Золотые тесты графика времени фичи (волна 3 «Обзор фич», вкладка «Статистика», Б5a):
// FeatureTimeline.Build — чистая проекция FeatureRun → FeatureTimeline (режим approx, по задачам).
// Время передаётся параметром (now) — детерминизм. Мутация Build красит кейсы.
//
// Покрытие: ночная пауза > 2 ч (notWorking); параллельные задачи (объединение, не сумма);
// «ждёт согласия» → current=waiting; идущая задача (сегмент до now); пустая фича (нет стартов);
// потолок 500 сегментов; в approx Waiting*/Idle*/Error* = null.
public class FeatureTimelineTests
{
    // ── Хелперы сборки ──────────────────────────────────────────────────────────────────────

    private static readonly DateTime T0 = new(2026, 10, 1, 8, 0, 0);

    // Эпик фичи (role=feature, FeatureId=null).
    private static TaskItem Epic(string id, TaskItemStatus status = TaskItemStatus.Todo,
        DateTime? completedAt = null, DateTime? awaitingSince = null, string? awaitingReason = null) => new()
    {
        Id = id,
        Role = TaskRoles.Feature,
        FeatureId = null,
        Status = status,
        CreatedAt = T0,
        CompletedAt = completedAt,
        AwaitingHumanSince = awaitingSince,
        AwaitingHumanReason = awaitingReason,
    };

    // Дочерняя задача фичи (FeatureId = epicId).
    private static TaskItem Child(string id, string epicId, string role = TaskRoles.Step,
        string? stepRef = null, TaskItemStatus status = TaskItemStatus.Todo,
        DateTime? completedAt = null, DateTime? startedAt = null,
        DateTime? stoppedAt = null, string? stopReason = null,
        DateTime? awaitingSince = null, string? awaitingReason = null) => new()
    {
        Id = id,
        FeatureId = epicId,
        Role = role,
        StepRef = stepRef,
        Status = status,
        CreatedAt = T0,
        CompletedAt = completedAt,
        ClaudeStartedAt = startedAt,
        ExecutorStoppedAt = stoppedAt,
        ExecutorStopReason = stopReason,
        AwaitingHumanSince = awaitingSince,
        AwaitingHumanReason = awaitingReason,
    };

    // FeatureRun (ПОЛНЫЙ, необлегщённый) из эпика + дочерних.
    private static FeatureRun Run(TaskItem epic, params TaskItem[] children)
    {
        var all = new List<TaskItem> { epic };
        all.AddRange(children);
        return FeatureProjector.BuildCore(epic, all);
    }

    // Build с фиксированным now (журнал null — approx).
    private static FeatureTimeline Timeline(FeatureRun run, DateTime now) =>
        FeatureTimeline.Build(run, null, now);

    // Сегменты заданного вида.
    private static List<TimelineSegment> Kinds(FeatureTimeline tl, string kind) =>
        tl.Segments.Where(s => s.Kind == kind).ToList();

    // ── 1. Ночная пауза > 2 ч → notWorking ───────────────────────────────────────────────────

    // Две последовательные работы с 2-часовым разрывом (ночная пауза): разрыв — notWorking.
    // approx: «ожидание/простой/ошибка» не различаем → Waiting*/Idle*/Error* = null.
    [Fact]
    public void Build_НочнаяПаузаБолее2Ч_ВходитВNotWorking()
    {
        var now = new DateTime(2026, 10, 1, 20, 0, 0);
        var epic = Epic("epic");
        // s1: 10:00–12:00 (2 ч работы); s2: 14:00–16:00 (2 ч работы). Разрыв 12:00–14:00 = 2 ч.
        var s1 = Child("s1", "epic", status: TaskItemStatus.Done,
            startedAt: new DateTime(2026, 10, 1, 10, 0, 0),
            completedAt: new DateTime(2026, 10, 1, 12, 0, 0));
        var s2 = Child("s2", "epic", status: TaskItemStatus.Done,
            startedAt: new DateTime(2026, 10, 1, 14, 0, 0),
            completedAt: new DateTime(2026, 10, 1, 16, 0, 0));

        var tl = Timeline(Run(epic, s1, s2), now);

        // From = создание эпика (08:00); To = now (20:00); Elapsed = 12 ч = 720 мин.
        tl.From.Should().Be(T0);
        tl.To.Should().Be(now);
        tl.Totals.ElapsedMinutes.Should().Be(720);
        // Work: 2 × 2 ч = 4 ч = 240 мин (не пересекаются → объединение = сумма).
        tl.Totals.WorkWallMinutes.Should().Be(240);
        tl.Totals.AgentsSumMinutes.Should().Be(240);
        // NotWorking = 720 − 240 = 480 мин (включая ночную паузу 12:00–14:00 = 120 мин).
        tl.Totals.NotWorkingMinutes.Should().Be(480);

        // Ночная пауза 12:00–14:00 — отдельный notWorking-сегмент.
        var night = Kinds(tl, "notWorking").Single(s =>
            s.From == new DateTime(2026, 10, 1, 12, 0, 0)
            && s.To == new DateTime(2026, 10, 1, 14, 0, 0));
        night.Kind.Should().Be("notWorking");

        // approx: ожидание/простой/ошибка не различаем → null.
        tl.Totals.WaitingMinutes.Should().BeNull();
        tl.Totals.IdleMinutes.Should().BeNull();
        tl.Totals.WaitingCount.Should().BeNull();
        tl.Totals.LongestWaitMinutes.Should().BeNull();
        tl.Totals.ErrorCount.Should().BeNull();
        tl.Totals.ErrorMinutes.Should().BeNull();
    }

    // ── 2. Две параллельные задачи: объединение, не сумма ───────────────────────────────────

    // Две задачи на одной стадии (prod) пересекаются: WorkWall = объединение (6 ч),
    // AgentsSum = сумма (8 ч). Мутация «WorkWall = сумма» → 480 ≠ 360 → краснеет.
    [Fact]
    public void Build_ПараллельныеЗадачи_ОбъединениеНеСумма()
    {
        var now = new DateTime(2026, 10, 1, 20, 0, 0);
        var epic = Epic("epic");
        // s1: 10:00–14:00 (4 ч); s2: 12:00–16:00 (4 ч). Пересечение 12:00–14:00.
        var s1 = Child("s1", "epic", status: TaskItemStatus.Done,
            startedAt: new DateTime(2026, 10, 1, 10, 0, 0),
            completedAt: new DateTime(2026, 10, 1, 14, 0, 0));
        var s2 = Child("s2", "epic", status: TaskItemStatus.Done,
            startedAt: new DateTime(2026, 10, 1, 12, 0, 0),
            completedAt: new DateTime(2026, 10, 1, 16, 0, 0));

        var tl = Timeline(Run(epic, s1, s2), now);

        // Объединение [10:00–16:00] = 6 ч = 360 мин (не 480 = сумма).
        tl.Totals.WorkWallMinutes.Should().Be(360);
        // Сумма (агентные минуты) = 4 + 4 = 8 ч = 480 мин.
        tl.Totals.AgentsSumMinutes.Should().Be(480);

        // Параллельные задачи склеены в ОДИН work-сегмент на (main, prod), taskId=null.
        var prodWork = Kinds(tl, "work").Where(s => s.Stage == "prod" && s.Track == "main").ToList();
        prodWork.Should().HaveCount(1);
        prodWork[0].From.Should().Be(new DateTime(2026, 10, 1, 10, 0, 0));
        prodWork[0].To.Should().Be(new DateTime(2026, 10, 1, 16, 0, 0));
        prodWork[0].TaskId.Should().BeNull();
    }

    // ── 3. «Ждёт согласия» → current = waiting ───────────────────────────────────────────────

    // Задача InProgress с AwaitingHumanSince → тревога wait → current = waiting (Since/TaskId).
    [Fact]
    public void Build_ЖдётСогласия_CurrentWaiting()
    {
        var now = new DateTime(2026, 10, 1, 20, 0, 0);
        var epic = Epic("epic");
        var s1 = Child("s1", "epic", status: TaskItemStatus.InProgress,
            startedAt: new DateTime(2026, 10, 1, 10, 0, 0),
            awaitingSince: new DateTime(2026, 10, 1, 15, 0, 0),
            awaitingReason: "decision");

        var tl = Timeline(Run(epic, s1), now);

        tl.Current.Should().NotBeNull();
        tl.Current!.Kind.Should().Be("waiting");
        tl.Current.Since.Should().Be(new DateTime(2026, 10, 1, 15, 0, 0));
        tl.Current.TaskId.Should().Be("s1");
    }

    // ── 4. Идущая задача: сегмент до now ─────────────────────────────────────────────────────

    // Задача InProgress без CompletedAt → work-интервал [ClaudeStartedAt, now].
    [Fact]
    public void Build_ИдущаяЗадача_СегментДоNow()
    {
        var now = new DateTime(2026, 10, 1, 20, 0, 0);
        var epic = Epic("epic");
        var s1 = Child("s1", "epic", status: TaskItemStatus.InProgress,
            startedAt: new DateTime(2026, 10, 1, 18, 0, 0));

        var tl = Timeline(Run(epic, s1), now);

        // Work-сегмент: [18:00, now=20:00] = 2 ч = 120 мин.
        var work = Kinds(tl, "work").Single(s => s.Stage == "prod");
        work.From.Should().Be(new DateTime(2026, 10, 1, 18, 0, 0));
        work.To.Should().Be(now);
        tl.Totals.WorkWallMinutes.Should().Be(120);
        tl.Totals.AgentsSumMinutes.Should().Be(120);
    }

    // ── 5. Пустая фича (нет стартов): пустые сегменты, From=To/нули ──────────────────────────

    // Эпик + дочерние без ClaudeStartedAt → нет work; To = now; From = To? Нет: From = 08:00,
    // To = now. Но «пустая фича» по постановке — нет стартов → сегменты work пусты, итоги нули.
    // Если now == From (эпик создан «сейчас») → From = To, Elapsed = 0.
    [Fact]
    public void Build_ПустаяФича_НетСтартов_ПустыеСегменты()
    {
        // now = T0: эпик создан «сейчас» → From = To = T0, Elapsed = 0.
        var now = T0;
        var epic = Epic("epic");
        var s1 = Child("s1", "epic", status: TaskItemStatus.Todo); // без ClaudeStartedAt

        var tl = Timeline(Run(epic, s1), now);

        tl.From.Should().Be(T0);
        tl.To.Should().Be(T0);
        tl.Totals.ElapsedMinutes.Should().Be(0);
        tl.Totals.WorkWallMinutes.Should().Be(0);
        tl.Totals.AgentsSumMinutes.Should().Be(0);
        tl.Totals.NotWorkingMinutes.Should().Be(0);
        // Work-сегментов нет (ни одна задача не стартовала).
        Kinds(tl, "work").Should().BeEmpty();
        // NotWorking: From == To → нулевой промежуток, сегментов нет.
        Kinds(tl, "notWorking").Should().BeEmpty();
    }

    // ── 6. Потолок 500 сегментов ─────────────────────────────────────────────────────────────

    // 600 work-интервалов (1 мин) с 1-минутными разрывами: work-сегменты (600) + notWorking-
    // промежутки (600) = 1200 > 500 → склейка по возрастающему порогу сжимает до ≤ 500.
    // Окно 12 ч (08:00 → 20:00) вмещает 1200 мин.
    private static (FeatureRun Run, DateTime Now) DenseRun()
    {
        var now = new DateTime(2026, 10, 1, 20, 0, 0);
        var epic = Epic("epic");
        var children = new List<TaskItem>();
        var t0 = T0; // 08:00
        for (var i = 0; i < 600; i++)
        {
            var start = t0.AddMinutes(i * 2);
            var end = start.AddMinutes(1);
            children.Add(Child($"s{i}", "epic", status: TaskItemStatus.Done,
                startedAt: start, completedAt: end));
        }
        return (Run(epic, children.ToArray()), now);
    }

    [Fact]
    public void Build_Потолок500Сегментов_Сжимается()
    {
        var (run, now) = DenseRun();
        var tl = Timeline(run, now);
        // После склейки ближайших пар сегментов ≤ DefaultMaxSegments (500).
        tl.Segments.Count.Should().BeLessOrEqualTo(FeatureTimeline.DefaultMaxSegments);
    }

    // Мутация «снять потолок»: maxSegments = int.MaxValue → склейка ближайших пар не срабатывает →
    // сегментов > 500. Доказывает, что именно потолок (не что-то другое) держит число ≤ 500:
    // кейс 6a (с потолком) зелёный, а здесь (без потолка) > 500. Параметр maxSegments — внутренний
    // хук без глобального состояния (минор 9: мутабельный static убран).
    [Fact]
    public void Build_Мутация_СнятьПотолок_Более500()
    {
        var (run, now) = DenseRun();
        var tl = FeatureTimeline.Build(run, null, now, maxSegments: int.MaxValue);
        tl.Segments.Count.Should().BeGreaterThan(500,
            "без склейки по потолку сегментов больше 500 — потолок и есть ограничитель");
    }

    // ── 7. approx: Waiting*/Idle*/Error* = null (повторно, на любой фиче) ───────────────────

    [Fact]
    public void Build_Approx_СпециальныеПоляNull()
    {
        var now = new DateTime(2026, 10, 1, 20, 0, 0);
        var epic = Epic("epic");
        var s1 = Child("s1", "epic", status: TaskItemStatus.Done,
            startedAt: new DateTime(2026, 10, 1, 10, 0, 0),
            completedAt: new DateTime(2026, 10, 1, 12, 0, 0));

        var tl = Timeline(Run(epic, s1), now);

        tl.Totals.WaitingMinutes.Should().BeNull();
        tl.Totals.IdleMinutes.Should().BeNull();
        tl.Totals.WaitingCount.Should().BeNull();
        tl.Totals.LongestWaitMinutes.Should().BeNull();
        tl.Totals.ErrorCount.Should().BeNull();
        tl.Totals.ErrorMinutes.Should().BeNull();
    }

    // ── 8. Мутация: WorkWallMinutes = сумма → краснеет ───────────────────────────────────────

    // Две пересекающиеся задачи: если WorkWall считать суммой (480), а не объединением (360),
    // тест краснеет. (Дублирует проверку из кейса 2, но изолированно для мутации.)
    [Fact]
    public void Build_Мутация_WorkWallСумма_Краснеет()
    {
        var now = new DateTime(2026, 10, 1, 20, 0, 0);
        var epic = Epic("epic");
        var s1 = Child("s1", "epic", status: TaskItemStatus.Done,
            startedAt: new DateTime(2026, 10, 1, 10, 0, 0),
            completedAt: new DateTime(2026, 10, 1, 14, 0, 0));
        var s2 = Child("s2", "epic", status: TaskItemStatus.Done,
            startedAt: new DateTime(2026, 10, 1, 12, 0, 0),
            completedAt: new DateTime(2026, 10, 1, 16, 0, 0));

        var tl = Timeline(Run(epic, s1, s2), now);

        // Объединение = 360; если бы считали сумму — 480. Утверждаем 360.
        tl.Totals.WorkWallMinutes.Should().Be(360,
            "WorkWall — объединение интервалов, не сумма (сумма дала бы 480)");
        tl.Totals.AgentsSumMinutes.Should().Be(480);
    }
}
