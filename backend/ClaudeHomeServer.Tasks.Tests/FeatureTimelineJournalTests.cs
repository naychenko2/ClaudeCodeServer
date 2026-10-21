using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Tasks.Journal;
using ClaudeHomeServer.Services.Tasks.Projector;

namespace ClaudeHomeServer.Tasks.Tests;

// Точный режим графика времени фичи по журналу (волна 3 «Обзор фич», вкладка «Статистика», Б5b):
// FeatureTimeline.Build(run, journal, now) — когда journal полный (StartsWithCreated && !Truncated) →
// точные интервалы work/waiting/error + idle (остаток) + заполненные Waiting*/Idle*/Error* итоги.
// Время передаётся параметром (now) — детерминизм. Мутация Build красит кейсы.
//
// Покрытие:
//  • полный журнал → точные ожидание/простой/ошибки (work/waiting/error/idle + totals);
//  • висячий waiting_cleared (без начала) → игнорируется;
//  • незакрытое ожидание → до now;
//  • ожидание устройства (device_wait…device_wait_cleared) → как waiting;
//  • период started…(stopped|done|now) → work (без перекрытия с waiting);
//  • журнал без created → approx (Waiting*/Idle*/Error* = null);
//  • Truncated → approx;
//  • журнала нет (null) → approx.
public class FeatureTimelineJournalTests
{
    // ── Хелперы сборки ──────────────────────────────────────────────────────────────────────

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

    // Дочерняя задача фичи (FeatureId = epicId).
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

    // FeatureRun (ПОЛНЫЙ, необлегщённый) из эпика + дочерних.
    private static FeatureRun Run(TaskItem epic, params TaskItem[] children)
    {
        var all = new List<TaskItem> { epic };
        all.AddRange(children);
        return FeatureProjector.BuildCore(epic, all);
    }

    // Запись журнала.
    private static FeatureJournalEntry Entry(string kind, DateTime at, string? taskId = null,
        DateTime? startedAt = null, DateTime? endedAt = null, string? outcome = null,
        string? errorClass = null, string? to = null) => new()
    {
        At = at,
        Kind = kind,
        TaskId = taskId,
        StartedAt = startedAt,
        EndedAt = endedAt,
        Outcome = outcome,
        ErrorClass = errorClass,
        To = to,
    };

    // Полный журнал (StartsWithCreated && !Truncated) с заданными записями.
    private static FeatureJournalReadResult Journal(params FeatureJournalEntry[] entries) => new()
    {
        StartsWithCreated = true,
        Truncated = false,
        Entries = entries.ToList(),
    };

    // ── 1. Полный журнал → точные ожидание/простой/ошибки ───────────────────────────────────

    // Таймлайн 08:00 → now=20:00 (720 мин). Журнал:
    //   created 08:00; turn 09:00–10:00 (finished); waiting 10:00 → waiting_cleared 11:00 (60 мин);
    //   turn 11:00–12:00 (error, auth); stopped 12:00 (точка).
    // work = [09:00,10:00] ∪ [11:00,12:00] = 120 мин (turns; без started-периода → нет перекрытия);
    // waiting = [10:00,11:00] = 60 мин; error = [11:00,12:00] (turn) + точка 12:00 (0) = 60 мин;
    // idle = 720 − 120 − 60 − 60 = 480 мин.
    [Fact]
    public void Build_ПолныйЖурнал_ТочныеОжиданиеПростойОшибки()
    {
        var now = new DateTime(2026, 10, 1, 20, 0, 0);
        var epic = Epic("epic");
        var s1 = Child("s1", "epic", startedAt: new DateTime(2026, 10, 1, 9, 0, 0),
            completedAt: new DateTime(2026, 10, 1, 12, 0, 0));

        var journal = Journal(
            Entry("created", new DateTime(2026, 10, 1, 8, 0, 0), taskId: "epic"),
            Entry("turn", new DateTime(2026, 10, 1, 10, 0, 0), taskId: "s1",
                startedAt: new DateTime(2026, 10, 1, 9, 0, 0),
                endedAt: new DateTime(2026, 10, 1, 10, 0, 0), outcome: "finished"),
            Entry("waiting", new DateTime(2026, 10, 1, 10, 0, 0), taskId: "s1"),
            Entry("waiting_cleared", new DateTime(2026, 10, 1, 11, 0, 0), taskId: "s1"),
            Entry("turn", new DateTime(2026, 10, 1, 12, 0, 0), taskId: "s1",
                startedAt: new DateTime(2026, 10, 1, 11, 0, 0),
                endedAt: new DateTime(2026, 10, 1, 12, 0, 0), outcome: "error", errorClass: "auth"),
            Entry("stopped", new DateTime(2026, 10, 1, 12, 0, 0), taskId: "s1"));

        var tl = FeatureTimeline.Build(Run(epic, s1), journal, now);

        // Границы: 08:00 → 20:00 = 720 мин.
        tl.From.Should().Be(T0);
        tl.To.Should().Be(now);
        tl.Totals.ElapsedMinutes.Should().Be(720);

        // work = [09:00,10:00] ∪ [11:00,12:00] = 120 мин (turns; без started-периода).
        tl.Totals.WorkWallMinutes.Should().Be(120);
        // waiting = [10:00,11:00] = 60 мин.
        tl.Totals.WaitingMinutes.Should().Be(60);
        // error = [11:00,12:00] (turn error) + точка 12:00 (0) = 60 мин.
        tl.Totals.ErrorMinutes.Should().Be(60);
        // idle = 720 − 120 − 60 − 60 = 480 мин.
        tl.Totals.IdleMinutes.Should().Be(480);
        // NotWorking = 720 − 120 = 600 = waiting + error + idle.
        tl.Totals.NotWorkingMinutes.Should().Be(600);

        // Счётчики: 1 ожидание (60 мин, самое длинное), 2 ошибки (turn error + stopped).
        tl.Totals.WaitingCount.Should().Be(1);
        tl.Totals.LongestWaitMinutes.Should().Be(60);
        tl.Totals.ErrorCount.Should().Be(2);

        // Сегменты: work/waiting/error/idle на месте (виды по приоритету, idle — остаток).
        tl.Segments.Should().OnlyContain(s =>
            s.Kind == "work" || s.Kind == "waiting" || s.Kind == "error" || s.Kind == "idle");
        // waiting-сегмент [10:00,11:00] на месте (work не перекрывает: turns 09–10 и 11–12).
        Kinds(tl, "waiting").Should().Contain(s =>
            s.From == new DateTime(2026, 10, 1, 10, 0, 0)
            && s.To == new DateTime(2026, 10, 1, 11, 0, 0));
    }

    // ── 2. Висячий waiting_cleared (без начала) → игнорируется ──────────────────────────────

    // Журнал: created + started 09:00 (work [09:00,12:00]) + waiting_cleared 10:00 (БЕЗ начала)
    // + stopped 12:00. Ожидания нет → WaitingMinutes = 0, WaitingCount = 0 (висячий cleared
    // не создаёт интервал). work = [09:00,12:00] = 180 мин.
    [Fact]
    public void Build_ВисячийWaitingCleared_Игнорируется()
    {
        var now = new DateTime(2026, 10, 1, 20, 0, 0);
        var epic = Epic("epic");
        var s1 = Child("s1", "epic", startedAt: new DateTime(2026, 10, 1, 9, 0, 0),
            completedAt: new DateTime(2026, 10, 1, 12, 0, 0));

        var journal = Journal(
            Entry("created", new DateTime(2026, 10, 1, 8, 0, 0), taskId: "epic"),
            Entry("started", new DateTime(2026, 10, 1, 9, 0, 0), taskId: "s1",
                startedAt: new DateTime(2026, 10, 1, 9, 0, 0)),
            Entry("waiting_cleared", new DateTime(2026, 10, 1, 10, 0, 0), taskId: "s1"),
            Entry("stopped", new DateTime(2026, 10, 1, 12, 0, 0), taskId: "s1"));

        var tl = FeatureTimeline.Build(Run(epic, s1), journal, now);

        // work = [09:00,12:00] = 180 мин; waiting = 0 (висячий cleared без начала).
        tl.Totals.WorkWallMinutes.Should().Be(180);
        tl.Totals.WaitingMinutes.Should().Be(0);
        tl.Totals.WaitingCount.Should().Be(0);
        tl.Totals.LongestWaitMinutes.Should().Be(0);
        // error = точка stopped 12:00 = 0 мин; idle = 720 − 180 − 0 − 0 = 540 мин.
        tl.Totals.ErrorMinutes.Should().Be(0);
        tl.Totals.IdleMinutes.Should().Be(540);
        // Сегментов waiting нет (висячий cleared не создаёт интервал).
        Kinds(tl, "waiting").Should().BeEmpty();
    }

    // ── 3. Незакрытое ожидание → до now ──────────────────────────────────────────────────────

    // Журнал: created + waiting 10:00 (БЕЗ waiting_cleared) → ожидание до now=20:00:
    // [10:00,20:00] = 600 мин. Без work (нет started/turn) → нет перекрытия.
    // Мутация «не закрывать ожидание на waiting_cleared» → краснеет (ожидание считалось бы 0).
    [Fact]
    public void Build_НезакрытоеОжидание_ДоNow()
    {
        var now = new DateTime(2026, 10, 1, 20, 0, 0);
        var epic = Epic("epic");
        var s1 = Child("s1", "epic");

        var journal = Journal(
            Entry("created", new DateTime(2026, 10, 1, 8, 0, 0), taskId: "epic"),
            Entry("waiting", new DateTime(2026, 10, 1, 10, 0, 0), taskId: "s1"));

        var tl = FeatureTimeline.Build(Run(epic, s1), journal, now);

        // work = 0 (нет started/turn); waiting = [10:00, now=20:00] = 600 мин (незакрытое → до now).
        tl.Totals.WorkWallMinutes.Should().Be(0);
        tl.Totals.WaitingMinutes.Should().Be(600);
        tl.Totals.WaitingCount.Should().Be(1);
        tl.Totals.LongestWaitMinutes.Should().Be(600);
        // error = 0 (нет stopped/turn error); idle = 720 − 0 − 600 − 0 = 120 мин (08:00–10:00).
        tl.Totals.ErrorMinutes.Should().Be(0);
        tl.Totals.IdleMinutes.Should().Be(120);

        // Сегмент waiting: [10:00, 20:00] (до now).
        Kinds(tl, "waiting").Should().Contain(s =>
            s.From == new DateTime(2026, 10, 1, 10, 0, 0)
            && s.To == now);
    }

    // ── 4. Ожидание устройства (device_wait…device_wait_cleared) → как waiting ───────────────

    // Журнал: created + device_wait 10:00 → device_wait_cleared 11:00 (60 мин). Без work
    // (нет started/turn) → нет перекрытия. Ожидание устройства рисуется как waiting (60 мин).
    [Fact]
    public void Build_ОжиданиеУстройства_КакWaiting()
    {
        var now = new DateTime(2026, 10, 1, 20, 0, 0);
        var epic = Epic("epic");
        var s1 = Child("s1", "epic");

        var journal = Journal(
            Entry("created", new DateTime(2026, 10, 1, 8, 0, 0), taskId: "epic"),
            Entry("device_wait", new DateTime(2026, 10, 1, 10, 0, 0), taskId: "s1"),
            Entry("device_wait_cleared", new DateTime(2026, 10, 1, 11, 0, 0), taskId: "s1"));

        var tl = FeatureTimeline.Build(Run(epic, s1), journal, now);

        // work = 0; waiting (device) = [10:00,11:00] = 60 мин; idle = 720 − 0 − 60 − 0 = 660 мин.
        tl.Totals.WorkWallMinutes.Should().Be(0);
        tl.Totals.WaitingMinutes.Should().Be(60);
        tl.Totals.WaitingCount.Should().Be(1);
        tl.Totals.LongestWaitMinutes.Should().Be(60);
        tl.Totals.IdleMinutes.Should().Be(660);

        // Сегмент waiting: [10:00, 11:00].
        Kinds(tl, "waiting").Should().Contain(s =>
            s.From == new DateTime(2026, 10, 1, 10, 0, 0)
            && s.To == new DateTime(2026, 10, 1, 11, 0, 0));
    }

    // ── 5. Период started…(stopped|done|now) → work (без перекрытия с waiting) ───────────────

    // Журнал: created + started 09:00 + stopped 12:00 → work-период [09:00,12:00] = 180 мин.
    // Без waiting/turn → нет перекрытия; idle = 720 − 180 = 540 мин.
    [Fact]
    public void Build_ПериодStartedStopped_Work()
    {
        var now = new DateTime(2026, 10, 1, 20, 0, 0);
        var epic = Epic("epic");
        var s1 = Child("s1", "epic", startedAt: new DateTime(2026, 10, 1, 9, 0, 0),
            completedAt: new DateTime(2026, 10, 1, 12, 0, 0));

        var journal = Journal(
            Entry("created", new DateTime(2026, 10, 1, 8, 0, 0), taskId: "epic"),
            Entry("started", new DateTime(2026, 10, 1, 9, 0, 0), taskId: "s1",
                startedAt: new DateTime(2026, 10, 1, 9, 0, 0)),
            Entry("stopped", new DateTime(2026, 10, 1, 12, 0, 0), taskId: "s1"));

        var tl = FeatureTimeline.Build(Run(epic, s1), journal, now);

        // work = [09:00,12:00] = 180 мин (started…stopped); waiting = 0; error = точка (0).
        tl.Totals.WorkWallMinutes.Should().Be(180);
        tl.Totals.WaitingMinutes.Should().Be(0);
        tl.Totals.ErrorMinutes.Should().Be(0);
        // idle = 720 − 180 − 0 − 0 = 540 мин.
        tl.Totals.IdleMinutes.Should().Be(540);
        // work-сегмент [09:00,12:00] на месте.
        Kinds(tl, "work").Should().Contain(s =>
            s.From == new DateTime(2026, 10, 1, 9, 0, 0)
            && s.To == new DateTime(2026, 10, 1, 12, 0, 0));
    }

    // ── 6. Журнал без created → approx (Waiting*/Idle*/Error* = null) ───────────────────────

    // Мутация «включить journal без created» → краснеет: код должен дать approx (null),
    // а не journal (заполненные). StartsWithCreated=false → approx.
    [Fact]
    public void Build_ЖурналБезCreated_Approx()
    {
        var now = new DateTime(2026, 10, 1, 20, 0, 0);
        var epic = Epic("epic");
        var s1 = Child("s1", "epic", startedAt: new DateTime(2026, 10, 1, 9, 0, 0),
            completedAt: new DateTime(2026, 10, 1, 12, 0, 0));

        // Журнал НЕ начинается с created (первая запись — started) → approx.
        var journal = new FeatureJournalReadResult
        {
            StartsWithCreated = false,
            Truncated = false,
            Entries =
            [
                Entry("started", new DateTime(2026, 10, 1, 9, 0, 0), taskId: "s1",
                    startedAt: new DateTime(2026, 10, 1, 9, 0, 0)),
                Entry("waiting", new DateTime(2026, 10, 1, 10, 0, 0), taskId: "s1"),
                Entry("waiting_cleared", new DateTime(2026, 10, 1, 11, 0, 0), taskId: "s1"),
                Entry("stopped", new DateTime(2026, 10, 1, 12, 0, 0), taskId: "s1"),
            ],
        };

        var tl = FeatureTimeline.Build(Run(epic, s1), journal, now);

        // approx: Waiting*/Idle*/Error* = null (даже если в журнале есть waiting).
        tl.Totals.WaitingMinutes.Should().BeNull();
        tl.Totals.IdleMinutes.Should().BeNull();
        tl.Totals.WaitingCount.Should().BeNull();
        tl.Totals.LongestWaitMinutes.Should().BeNull();
        tl.Totals.ErrorCount.Should().BeNull();
        tl.Totals.ErrorMinutes.Should().BeNull();
        // work по задачам (approx): [09:00,12:00] = 180 мин.
        tl.Totals.WorkWallMinutes.Should().Be(180);
    }

    // ── 7. Truncated → approx ────────────────────────────────────────────────────────────────

    // Журнал начинается с created, но Truncated=true → approx (гибрида нет).
    [Fact]
    public void Build_Truncated_Approx()
    {
        var now = new DateTime(2026, 10, 1, 20, 0, 0);
        var epic = Epic("epic");
        var s1 = Child("s1", "epic", startedAt: new DateTime(2026, 10, 1, 9, 0, 0),
            completedAt: new DateTime(2026, 10, 1, 12, 0, 0));

        var journal = new FeatureJournalReadResult
        {
            StartsWithCreated = true,
            Truncated = true,
            Entries =
            [
                Entry("created", new DateTime(2026, 10, 1, 8, 0, 0), taskId: "epic"),
                Entry("waiting", new DateTime(2026, 10, 1, 10, 0, 0), taskId: "s1"),
                Entry("waiting_cleared", new DateTime(2026, 10, 1, 11, 0, 0), taskId: "s1"),
            ],
        };

        var tl = FeatureTimeline.Build(Run(epic, s1), journal, now);

        // approx: Waiting*/Idle*/Error* = null (Truncated → не точный).
        tl.Totals.WaitingMinutes.Should().BeNull();
        tl.Totals.IdleMinutes.Should().BeNull();
        tl.Totals.ErrorMinutes.Should().BeNull();
    }

    // ── 8. Журнала нет (null) → approx ───────────────────────────────────────────────────────

    [Fact]
    public void Build_ЖурналаНет_Approx()
    {
        var now = new DateTime(2026, 10, 1, 20, 0, 0);
        var epic = Epic("epic");
        var s1 = Child("s1", "epic", startedAt: new DateTime(2026, 10, 1, 9, 0, 0),
            completedAt: new DateTime(2026, 10, 1, 12, 0, 0));

        var tl = FeatureTimeline.Build(Run(epic, s1), null, now);

        // approx: Waiting*/Idle*/Error* = null.
        tl.Totals.WaitingMinutes.Should().BeNull();
        tl.Totals.IdleMinutes.Should().BeNull();
        tl.Totals.ErrorMinutes.Should().BeNull();
        // work по задачам: [09:00,12:00] = 180 мин.
        tl.Totals.WorkWallMinutes.Should().Be(180);
    }

    // ── Хелпер: сегменты заданного вида ───────────────────────────────────────────────────────

    private static List<TimelineSegment> Kinds(FeatureTimeline tl, string kind) =>
        tl.Segments.Where(s => s.Kind == kind).ToList();
}
