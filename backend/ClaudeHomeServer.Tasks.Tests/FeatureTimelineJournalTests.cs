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
    // major 2 (work исключает error): ход 11:00–12:00 с outcome=error даёт и work, и error на том
    // же диапазоне — в итогах он идёт как ERROR, не work. Поэтому:
    //   work = [09:00,10:00] = 60 мин (только finished-ход; error-ход не в work);
    //   waiting = [10:00,11:00] = 60 мин; error = [11:00,12:00] (turn) + точка 12:00 (0) = 60 мин;
    //   idle = 720 − 60 − 60 − 60 = 540 мин. Инвариант: 60+60+60+540 = 720 = elapsed.
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

        // work = [09:00,10:00] = 60 мин (только finished-ход; error-ход 11:00–12:00 не в work).
        tl.Totals.WorkWallMinutes.Should().Be(60);
        // waiting = [10:00,11:00] = 60 мин.
        tl.Totals.WaitingMinutes.Should().Be(60);
        // error = [11:00,12:00] (turn error) + точка 12:00 (0) = 60 мин.
        tl.Totals.ErrorMinutes.Should().Be(60);
        // idle = 720 − 60 − 60 − 60 = 540 мин.
        tl.Totals.IdleMinutes.Should().Be(540);
        // NotWorking = 720 − 60 = 660 = waiting + error + idle.
        tl.Totals.NotWorkingMinutes.Should().Be(660);

        // Инвариант (major 2): work + waiting + error + idle = elapsed.
        (tl.Totals.WorkWallMinutes + tl.Totals.WaitingMinutes + tl.Totals.ErrorMinutes
            + tl.Totals.IdleMinutes).Should().Be(tl.Totals.ElapsedMinutes);

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
        // error-сегмент [11:00,12:00] (error-ход), НЕ work: work-сегментов только [09:00,10:00].
        Kinds(tl, "error").Should().Contain(s =>
            s.From == new DateTime(2026, 10, 1, 11, 0, 0)
            && s.To == new DateTime(2026, 10, 1, 12, 0, 0));
        Kinds(tl, "work").Should().ContainSingle(s =>
            s.From == new DateTime(2026, 10, 1, 9, 0, 0)
            && s.To == new DateTime(2026, 10, 1, 10, 0, 0));
    }

    // ── 1b. РЕАЛИСТИЧНЫЙ журнал С started: ожидание рисуется, work его не включает (major 1) ──

    // Журнал: created 08:00; started 09:00 (work-период до done 12:00); turn 09:00–10:00 (finished);
    // waiting 10:00 → waiting_cleared 11:00; turn 11:00–12:00 (finished); status→Done 12:00.
    // Период started…done = [09:00,12:00] перекрывает ожидание [10:00,11:00]. major 1: work-период
    // разрезается на waiting-интервалы, поэтому ожидание рисуется как WAITING (не work), а work =
    // [09:00,10:00] + [11:00,12:00] = 120 мин (без 60 мин ожидания). До фикса (work > waiting)
    // ожидание нарисовалось бы как работа, waiting-сегментов было бы 0.
    [Fact]
    public void Build_РеалистичныйЖурналСStarted_ОжиданиеРисуется_WorkЕгоНеВключает()
    {
        var now = new DateTime(2026, 10, 1, 20, 0, 0);
        var epic = Epic("epic");
        var s1 = Child("s1", "epic", startedAt: new DateTime(2026, 10, 1, 9, 0, 0),
            completedAt: new DateTime(2026, 10, 1, 12, 0, 0));

        var journal = Journal(
            Entry("created", new DateTime(2026, 10, 1, 8, 0, 0), taskId: "epic"),
            Entry("started", new DateTime(2026, 10, 1, 9, 0, 0), taskId: "s1",
                startedAt: new DateTime(2026, 10, 1, 9, 0, 0)),
            Entry("turn", new DateTime(2026, 10, 1, 10, 0, 0), taskId: "s1",
                startedAt: new DateTime(2026, 10, 1, 9, 0, 0),
                endedAt: new DateTime(2026, 10, 1, 10, 0, 0), outcome: "finished"),
            Entry("waiting", new DateTime(2026, 10, 1, 10, 0, 0), taskId: "s1"),
            Entry("waiting_cleared", new DateTime(2026, 10, 1, 11, 0, 0), taskId: "s1"),
            Entry("turn", new DateTime(2026, 10, 1, 12, 0, 0), taskId: "s1",
                startedAt: new DateTime(2026, 10, 1, 11, 0, 0),
                endedAt: new DateTime(2026, 10, 1, 12, 0, 0), outcome: "finished"),
            Entry("status", new DateTime(2026, 10, 1, 12, 0, 0), taskId: "s1", to: "Done"));

        var tl = FeatureTimeline.Build(Run(epic, s1), journal, now);

        // Ожидание [10:00,11:00] — сегмент WAITING (не work): major 1.
        Kinds(tl, "waiting").Should().ContainSingle(s =>
            s.From == new DateTime(2026, 10, 1, 10, 0, 0)
            && s.To == new DateTime(2026, 10, 1, 11, 0, 0));

        // work = [09:00,10:00] + [11:00,12:00] = 120 мин (started-период без 60 мин ожидания).
        tl.Totals.WorkWallMinutes.Should().Be(120);
        // waiting = 60 мин; error = 0; idle = 720 − 120 − 60 − 0 = 540 мин.
        tl.Totals.WaitingMinutes.Should().Be(60);
        tl.Totals.ErrorMinutes.Should().Be(0);
        tl.Totals.IdleMinutes.Should().Be(540);
        // Инвариант: work + waiting + error + idle = elapsed.
        (tl.Totals.WorkWallMinutes + tl.Totals.WaitingMinutes + tl.Totals.ErrorMinutes
            + tl.Totals.IdleMinutes).Should().Be(tl.Totals.ElapsedMinutes);
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

    // ── 9. Эквивалентность: новый линейный sweep == старый O(n²) на случайных входах (major 3) ──

    // Новый sweep (линейный, активное множество) обязан дать те же (From, To, Kind), что старый
    // O(n²) «скан всех интервалов на каждой границе» (тот же приоритет error > waiting > work).
    // Случайные интервалы (фиксированный сид) — детерминизм; сверка по всем отрезкам.
    [Fact]
    public void Sweep_Линейный_ЭквивалентенСтаромуO2_СлучайныеВходы()
    {
        var from = new DateTime(2026, 10, 1, 0, 0, 0);
        var to = new DateTime(2026, 10, 2, 0, 0, 0);
        var rng = new Random(42);
        var kinds = new[] { ("work", 1), ("waiting", 2), ("error", 3) };

        for (var trial = 0; trial < 20; trial++)
        {
            var n = rng.Next(1, 60);
            var intervals = new List<FeatureTimeline.JournalInterval>();
            for (var i = 0; i < n; i++)
            {
                var a = from.AddMinutes(rng.NextDouble() * 1440);
                var b = from.AddMinutes(rng.NextDouble() * 1440);
                if (b < a) (a, b) = (b, a);
                var (kind, priority) = kinds[rng.Next(kinds.Length)];
                intervals.Add(new FeatureTimeline.JournalInterval(a, b, kind, priority));
            }

            var actual = FeatureTimeline.Sweep(intervals, from, to)
                .Select(s => (s.From, s.To, s.Kind)).ToList();
            var expected = ReferenceSweepQuadratic(intervals, from, to);
            actual.Should().Equal(expected,
                $"trial {trial}: линейный sweep дал сегменты, отличные от O(n²) (n={n})");
        }
    }

    // Старый O(n²) sweep (референс): на каждой границе — скан всех интервалов, вид по приоритету.
    private static List<(DateTime From, DateTime To, string Kind)> ReferenceSweepQuadratic(
        List<FeatureTimeline.JournalInterval> intervals, DateTime from, DateTime to)
    {
        var points = new SortedSet<DateTime> { from, to };
        foreach (var iv in intervals)
        {
            points.Add(Clamp(iv.Start, from, to));
            points.Add(Clamp(iv.End, from, to));
        }
        var list = points.ToList();
        var result = new List<(DateTime, DateTime, string)>();
        for (var i = 0; i + 1 < list.Count; i++)
        {
            var s = list[i];
            var e = list[i + 1];
            if (e <= s) continue;
            var covering = intervals.Where(iv => iv.Start <= s && e <= iv.End).ToList();
            var kind = covering.Count == 0
                ? "idle"
                : covering.OrderByDescending(iv => iv.Priority).First().Kind;
            result.Add((s, e, kind));
        }
        return result;
    }

    // Обрезка t к [from, to] (как в FeatureTimeline).
    private static DateTime Clamp(DateTime t, DateTime from, DateTime to) =>
        t < from ? from : (t > to ? to : t);

    // ── 10. Замер: 20 000 записей журнала → сотни мс (major 3; порог с запасом, без Task.Delay) ──

    // Журнал на 20k записей (ходы + ожидания + started/stopped): sweep обязан уложиться в сотни
    // мс (старый O(n²) давал ~4.5 с на 20k, по замеру Глеба). Порог 1.5 с — с запасом, чтобы не
    // флачить на медленной CI; без Task.Delay (детерминизм времени не нужен, только верхняя граница).
    [Fact]
    public void Build_Замер_20kЗаписей_Быстрый()
    {
        var now = new DateTime(2026, 10, 1, 20, 0, 0);
        var epic = Epic("epic");
        var s1 = Child("s1", "epic", startedAt: new DateTime(2026, 10, 1, 9, 0, 0),
            completedAt: new DateTime(2026, 10, 1, 12, 0, 0));

        var entries = new List<FeatureJournalEntry>();
        entries.Add(Entry("created", new DateTime(2026, 10, 1, 8, 0, 0), taskId: "epic"));
        var rng = new Random(7);
        var baseTime = new DateTime(2026, 10, 1, 0, 0, 0);

        // Ходы — основная масса журнала (~18k записей): [at, at+1..9 мин], finished/error.
        for (var i = 0; i < 18000; i++)
        {
            var at = baseTime.AddMinutes(rng.NextDouble() * 1440);
            var outcome = rng.Next(5) == 0 ? "error" : "finished"; // ~20% error
            entries.Add(Entry("turn", at, taskId: "s1",
                startedAt: at, endedAt: at.AddMinutes(rng.Next(1, 10)), outcome: outcome));
        }
        // Ожидания (~2k записей): waiting + waiting_cleared.
        for (var i = 0; i < 1000; i++)
        {
            var at = baseTime.AddMinutes(rng.NextDouble() * 1440);
            entries.Add(Entry("waiting", at, taskId: "s1"));
            entries.Add(Entry("waiting_cleared", at.AddMinutes(rng.Next(1, 30)), taskId: "s1"));
        }
        // Started/stopped — пары (~20): ограниченные периоды (реалистично: задача стартует и
        // стопится, не 5000 бесконечных started, которые взрывали CutByWaiting).
        for (var i = 0; i < 20; i++)
        {
            var at = baseTime.AddMinutes(rng.NextDouble() * 1440);
            var stop = at.AddMinutes(rng.Next(30, 300));
            entries.Add(Entry("started", at, taskId: "s1", startedAt: at));
            entries.Add(Entry("stopped", stop, taskId: "s1"));
        }

        var journal = Journal(entries.ToArray());
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var tl = FeatureTimeline.Build(Run(epic, s1), journal, now);
        sw.Stop();

        tl.Totals.ElapsedMinutes.Should().BeGreaterThan(0);
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1.5),
            $"sweep на 20k записей должен быть в сотнях мс, а не {sw.Elapsed.TotalMilliseconds:F0} мс");
    }

    // ── Хелпер: сегменты заданного вида ───────────────────────────────────────────────────────

    private static List<TimelineSegment> Kinds(FeatureTimeline tl, string kind) =>
        tl.Segments.Where(s => s.Kind == kind).ToList();
}
