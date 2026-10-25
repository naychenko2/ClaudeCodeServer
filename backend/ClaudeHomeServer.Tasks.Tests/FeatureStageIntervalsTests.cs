using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Tasks.Journal;
using ClaudeHomeServer.Services.Tasks.Projector;

namespace ClaudeHomeServer.Tasks.Tests;

// Интервалы этапов из журнала ходов (GC-B1, «Хронология»/Гант): FeatureStageIntervals.Build —
// стадия → склеенные интервалы работы (зазор ≤ GlueGap — один интервал) + живой хвост идущей
// задачи (Live, до now). Время параметром (now) — детерминизм. Мутации красят кейсы:
//   • GlueGap = TimeSpan.Zero → тест «зазор 90 с» красный;
//   • живой хвост не дотянут до now → тесты Live красные;
//   • гейт журнала снят → тест «гейты» красный.
public class FeatureStageIntervalsTests
{
    private static readonly DateTime T0 = new(2026, 10, 1, 8, 0, 0);

    // Эпик фичи (role=feature, FeatureId=null).
    private static TaskItem Epic(string id) => new()
    {
        Id = id,
        Role = TaskRoles.Feature,
        FeatureId = null,
        Status = TaskItemStatus.Todo,
        CreatedAt = T0,
    };

    // Дочерняя задача фичи; completedAt null → InProgress (живая, попадает в run.Now).
    private static TaskItem Child(string id, string epicId, string? role = TaskRoles.Step,
        DateTime? startedAt = null, DateTime? completedAt = null) => new()
    {
        Id = id,
        FeatureId = epicId,
        Role = role,
        Status = completedAt is not null ? TaskItemStatus.Done : TaskItemStatus.InProgress,
        CreatedAt = T0,
        ClaudeStartedAt = startedAt,
        CompletedAt = completedAt,
    };

    // FeatureRun (ПОЛНЫЙ) из эпика + дочерних — тот же путь, что у FeatureTimelineJournalTests.
    private static FeatureRun Run(TaskItem epic, params TaskItem[] children)
    {
        var all = new List<TaskItem> { epic };
        all.AddRange(children);
        return FeatureProjector.BuildCore(epic, all);
    }

    private static FeatureJournalEntry Entry(string kind, DateTime at, string? taskId = null,
        DateTime? startedAt = null, DateTime? endedAt = null, string? outcome = null,
        string? model = null) => new()
    {
        At = at,
        Kind = kind,
        TaskId = taskId,
        StartedAt = startedAt,
        EndedAt = endedAt,
        Outcome = outcome,
        Model = model,
    };

    private static FeatureJournalReadResult Journal(params FeatureJournalEntry[] entries) => new()
    {
        StartsWithCreated = true,
        Truncated = false,
        Entries = entries.ToList(),
    };

    private static FeatureJournalEntry Turn(string taskId, DateTime from, DateTime to,
        string outcome = "finished", string? model = null) =>
        Entry("turn", to, taskId, startedAt: from, endedAt: to, outcome: outcome, model: model);

    // ── 1. Склейка: зазор ≤ GlueGap → один интервал ─────────────────────────────────────────

    // Ходы 09:00–09:30 и 09:31:30–10:00 (зазор 90 с ≤ 2 мин) — ОДИН интервал [09:00,10:00];
    // зазор входит в работу этапа. Мутация GlueGap = Zero → здесь два интервала → красный.
    [Fact]
    public void Build_Зазор90Сейкунд_ОдинИнтервал()
    {
        var epic = Epic("epic");
        var s1 = Child("s1", "epic", startedAt: new DateTime(2026, 10, 1, 9, 0, 0),
            completedAt: new DateTime(2026, 10, 1, 10, 0, 0));
        var journal = Journal(
            Entry("created", T0, taskId: "epic"),
            Turn("s1", new DateTime(2026, 10, 1, 9, 0, 0), new DateTime(2026, 10, 1, 9, 30, 0)),
            Turn("s1", new DateTime(2026, 10, 1, 9, 31, 30), new DateTime(2026, 10, 1, 10, 0, 0)));

        var stages = FeatureStageIntervals.Build(Run(epic, s1), journal, T0.AddHours(12))!;

        var intervals = stages["prod"];
        intervals.Should().HaveCount(1, "зазор 90 с ≤ GlueGap (2 мин) — ходы одного этапа склеиваются");
        intervals[0].From.Should().Be(new DateTime(2026, 10, 1, 9, 0, 0));
        intervals[0].To.Should().Be(new DateTime(2026, 10, 1, 10, 0, 0));
        intervals[0].Minutes.Should().Be(60);
        intervals[0].Live.Should().BeFalse();
        intervals[0].TaskIds.Should().Equal("s1");
        intervals[0].Turns.Should().HaveCount(2);
    }

    // ── 2. Склейка: зазор > GlueGap → два интервала ─────────────────────────────────────────

    [Fact]
    public void Build_Зазор150Секунд_ДваИнтервала()
    {
        var epic = Epic("epic");
        var s1 = Child("s1", "epic", startedAt: new DateTime(2026, 10, 1, 9, 0, 0),
            completedAt: new DateTime(2026, 10, 1, 10, 0, 0));
        var journal = Journal(
            Entry("created", T0, taskId: "epic"),
            Turn("s1", new DateTime(2026, 10, 1, 9, 0, 0), new DateTime(2026, 10, 1, 9, 30, 0)),
            Turn("s1", new DateTime(2026, 10, 1, 9, 32, 30), new DateTime(2026, 10, 1, 10, 0, 0)));

        var stages = FeatureStageIntervals.Build(Run(epic, s1), journal, T0.AddHours(12))!;

        var intervals = stages["prod"];
        intervals.Should().HaveCount(2, "зазор 150 с > GlueGap — это разные интервалы");
        intervals[0].From.Should().Be(new DateTime(2026, 10, 1, 9, 0, 0));
        intervals[0].To.Should().Be(new DateTime(2026, 10, 1, 9, 30, 0));
        intervals[1].From.Should().Be(new DateTime(2026, 10, 1, 9, 32, 30));
        intervals[1].To.Should().Be(new DateTime(2026, 10, 1, 10, 0, 0));
    }

    // ── 3. Параллельные задачи одной стадии → один интервал с обоими TaskIds ────────────────

    [Fact]
    public void Build_ПараллельныеЗадачиОднойСтадии_ОдинИнтервалОбеTaskIds()
    {
        var epic = Epic("epic");
        var s1 = Child("s1", "epic", startedAt: new DateTime(2026, 10, 1, 9, 0, 0),
            completedAt: new DateTime(2026, 10, 1, 9, 30, 0));
        var s2 = Child("s2", "epic", startedAt: new DateTime(2026, 10, 1, 9, 10, 0),
            completedAt: new DateTime(2026, 10, 1, 9, 40, 0));
        var journal = Journal(
            Entry("created", T0, taskId: "epic"),
            Turn("s1", new DateTime(2026, 10, 1, 9, 0, 0), new DateTime(2026, 10, 1, 9, 30, 0)),
            Turn("s2", new DateTime(2026, 10, 1, 9, 10, 0), new DateTime(2026, 10, 1, 9, 40, 0)));

        var stages = FeatureStageIntervals.Build(Run(epic, s1, s2), journal, T0.AddHours(12))!;

        stages.Should().ContainKey("prod");
        var intervals = stages["prod"];
        intervals.Should().HaveCount(1, "перекрывающиеся ходы стадии — один интервал");
        intervals[0].From.Should().Be(new DateTime(2026, 10, 1, 9, 0, 0));
        intervals[0].To.Should().Be(new DateTime(2026, 10, 1, 9, 40, 0));
        // оба id, по первому появлению.
        intervals[0].TaskIds.Should().Equal("s1", "s2");
        intervals[0].Turns.Should().HaveCount(2);
    }

    // ── 4. LastOutcome: исход ПОСЛЕДНЕГО хода (cancelled/error), finished → null ────────────

    [Fact]
    public void Build_LastOutcome_ИсходПоследнегоХода()
    {
        var epic = Epic("epic");
        var s1 = Child("s1", "epic", startedAt: new DateTime(2026, 10, 1, 9, 0, 0),
            completedAt: new DateTime(2026, 10, 1, 10, 0, 0));
        var a = new DateTime(2026, 10, 1, 9, 0, 0);
        var run = Run(epic, s1);

        // Последний ход cancelled → LastOutcome = cancelled; Turns несёт модель последнего хода.
        var cancelled = FeatureStageIntervals.Build(run, Journal(
            Entry("created", T0, taskId: "epic"),
            Turn("s1", a, a.AddMinutes(30)),
            Turn("s1", a.AddMinutes(31), a.AddMinutes(60), outcome: "cancelled", model: "glm-4"))!,
            T0.AddHours(12))!;
        cancelled["prod"][0].LastOutcome.Should().Be("cancelled");
        cancelled["prod"][0].Turns[^1].Model.Should().Be("glm-4");

        // Последний ход error → error.
        var errored = FeatureStageIntervals.Build(run, Journal(
            Entry("created", T0, taskId: "epic"),
            Turn("s1", a, a.AddMinutes(30), outcome: "cancelled"),
            Turn("s1", a.AddMinutes(31), a.AddMinutes(60), outcome: "error"))!,
            T0.AddHours(12))!;
        errored["prod"][0].LastOutcome.Should().Be("error");

        // Последний ход finished → null (этап не оборвался, хотя первый ход был cancelled).
        var finished = FeatureStageIntervals.Build(run, Journal(
            Entry("created", T0, taskId: "epic"),
            Turn("s1", a, a.AddMinutes(30), outcome: "cancelled"),
            Turn("s1", a.AddMinutes(31), a.AddMinutes(60)))!,
            T0.AddHours(12))!;
        finished["prod"][0].LastOutcome.Should().BeNull();
    }

    // ── 5. Гейты журнала: null / не с created / Truncated → null ────────────────────────────

    [Fact]
    public void Build_ГейтыЖурнала_Null()
    {
        var epic = Epic("epic");
        var s1 = Child("s1", "epic", startedAt: new DateTime(2026, 10, 1, 9, 0, 0),
            completedAt: new DateTime(2026, 10, 1, 9, 30, 0));
        var run = Run(epic, s1);
        var now = T0.AddHours(12);

        FeatureStageIntervals.Build(run, null, now).Should().BeNull("журнала нет");

        FeatureStageIntervals.Build(run, new FeatureJournalReadResult
        {
            StartsWithCreated = false,
            Truncated = false,
            Entries = [Entry("started", new DateTime(2026, 10, 1, 9, 0, 0), "s1")],
        }, now).Should().BeNull("журнал не начинается с created");

        FeatureStageIntervals.Build(run, new FeatureJournalReadResult
        {
            StartsWithCreated = true,
            Truncated = true,
            Entries = [Entry("created", T0, "epic")],
        }, now).Should().BeNull("Truncated — точных интервалов нет");
    }

    // ── 6. Живая задача (run.Now) без закрытых ходов: Live=true, To == now ──────────────────

    [Fact]
    public void Build_ЖиваяЗадачаБезХодов_LiveДоNow()
    {
        var epic = Epic("epic");
        var s1 = Child("s1", "epic", startedAt: new DateTime(2026, 10, 1, 9, 0, 0)); // InProgress
        var now = new DateTime(2026, 10, 1, 10, 0, 0);
        var journal = Journal(Entry("created", T0, taskId: "epic"));

        var stages = FeatureStageIntervals.Build(Run(epic, s1), journal, now)!;

        var intervals = stages["prod"];
        intervals.Should().HaveCount(1);
        intervals[0].From.Should().Be(new DateTime(2026, 10, 1, 9, 0, 0), "старт — ClaudeStartedAt задачи");
        intervals[0].To.Should().Be(now, "живой интервал дотянут до now");
        intervals[0].Live.Should().BeTrue();
        intervals[0].Minutes.Should().Be(60);
        intervals[0].TaskIds.Should().Equal("s1");
        intervals[0].Turns.Should().BeEmpty("живой хвост — не ход журнала");
        intervals[0].LastOutcome.Should().BeNull();
    }

    // ── 7. Живой хвост после закрытого хода той же задачи: старт — конец последнего хода, ──
    // ──    склейка с закрытым интервалом (зазор 0 ≤ GlueGap) → один Live-интервал ───────────

    [Fact]
    public void Build_ЖиваяЗадачаПослеЗакрытогоХода_СклеенныйLiveИнтервал()
    {
        var epic = Epic("epic");
        // Задача идёт с 09:00 (ClaudeStartedAt не обновлялся), её ход 09:00–09:30 закрыт.
        var s1 = Child("s1", "epic", startedAt: new DateTime(2026, 10, 1, 9, 0, 0));
        var now = new DateTime(2026, 10, 1, 10, 0, 0);
        var journal = Journal(
            Entry("created", T0, taskId: "epic"),
            Turn("s1", new DateTime(2026, 10, 1, 9, 0, 0), new DateTime(2026, 10, 1, 9, 30, 0)));

        var stages = FeatureStageIntervals.Build(Run(epic, s1), journal, now)!;

        var intervals = stages["prod"];
        intervals.Should().HaveCount(1, "живой хвост начинается с конца последнего хода — зазора нет");
        intervals[0].From.Should().Be(new DateTime(2026, 10, 1, 9, 0, 0));
        intervals[0].To.Should().Be(now);
        intervals[0].Live.Should().BeTrue();
        intervals[0].Turns.Should().HaveCount(1, "живой хвост не добавляет ход");
    }
}
