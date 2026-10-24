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
        // ST-B2: эпизодов ожидания нет → longestWait null.
        tl.Totals.LongestWait.Should().BeNull();
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

    // ── 3b. Дефект 8d9b5718: LongestWait обрезается по `to` (закрытая фича) ────────────────

    // Фича закрыта в 08:30 (epic Done, CompletedAt=08:30 → to=08:30), now=20:00.
    // Ожидание 08:00 БЕЗ waiting_cleared: интервал sweep'ом режется к [from, to] → [08:00, 08:30]
    // = 30 мин. LongestWait обязан обрезаться тем же `to` (30), а не тянуться до now (720):
    // иначе Waiting=30, LongestWait=720 — «самое длинное ожидание» длиннее всего окна фичи.
    // Мутация «убрать обрезку по to» (LongestWait = wEnd − e.At, wEnd=now) → 720 ≠ 30 → краснеет.
    [Fact]
    public void Build_ЗакрытаяФича_LongestWaitОбрезанПоTo()
    {
        var now = new DateTime(2026, 10, 1, 20, 0, 0);
        var closedAt = new DateTime(2026, 10, 1, 8, 30, 0);
        var epic = Epic("epic");
        epic.Status = TaskItemStatus.Done;
        epic.CompletedAt = closedAt;
        var s1 = Child("s1", "epic");

        var journal = Journal(
            Entry("created", new DateTime(2026, 10, 1, 8, 0, 0), taskId: "epic"),
            Entry("waiting", new DateTime(2026, 10, 1, 8, 0, 0), taskId: "s1"));

        var tl = FeatureTimeline.Build(Run(epic, s1), journal, now);

        // Границы: 08:00 → 08:30 (закрыто) = 30 мин, не до now.
        tl.To.Should().Be(closedAt);
        tl.Totals.ElapsedMinutes.Should().Be(30);
        // Ожидание [08:00, 08:30] = 30 мин (обрезано по to).
        tl.Totals.WaitingMinutes.Should().Be(30);
        tl.Totals.WaitingCount.Should().Be(1);
        // Самое длинное ожидание = 30 (обрезано по to), НЕ 720 (до now).
        tl.Totals.LongestWaitMinutes.Should().Be(30);
    }

    // ── 3c. Граница `to` ровно на конце ожидания: без off-by-one ───────────────────────────

    // Фича закрыта в 08:30 (to = 08:30), waiting_cleared — ровно в 08:30: конец ожидания
    // совпадает с границей окна. Обрезка `wEnd <= to` на границе noop: интервал [08:00, 08:30]
    // считается целиком (30 мин), LongestWait = 30 = Elapsed. Сдвиг границы на минуту
    // (обрезка до to − 1 мин) дал бы LongestWait = 29 при Waiting = 30 — противоречие,
    // которую этот тест ловит (мутация краснит его).
    [Fact]
    public void Build_ГраницаTo_РовноНаКонцеОжидания_БезOffByOne()
    {
        var now = new DateTime(2026, 10, 1, 20, 0, 0);
        var closedAt = new DateTime(2026, 10, 1, 8, 30, 0);
        var epic = Epic("epic");
        epic.Status = TaskItemStatus.Done;
        epic.CompletedAt = closedAt;
        var s1 = Child("s1", "epic");

        var journal = Journal(
            Entry("created", new DateTime(2026, 10, 1, 8, 0, 0), taskId: "epic"),
            Entry("waiting", new DateTime(2026, 10, 1, 8, 0, 0), taskId: "s1"),
            Entry("waiting_cleared", closedAt, taskId: "s1"));

        var tl = FeatureTimeline.Build(Run(epic, s1), journal, now);

        tl.To.Should().Be(closedAt);
        tl.Totals.ElapsedMinutes.Should().Be(30);
        // Ожидание [08:00, 08:30] = 30 мин: конец ровно на `to` не обрезается и не сдвигается.
        tl.Totals.WaitingMinutes.Should().Be(30);
        tl.Totals.WaitingCount.Should().Be(1);
        tl.Totals.LongestWaitMinutes.Should().Be(30);
        // Инвариант: сумма видов = elapsed.
        (tl.Totals.WorkWallMinutes + tl.Totals.WaitingMinutes + tl.Totals.ErrorMinutes
            + tl.Totals.IdleMinutes).Should().Be(tl.Totals.ElapsedMinutes);
    }

    // ── 3d. device_wait на закрытой фиче: longestWait обрезается по `to` (находка 2 ревью Глеба) ──

    // Фича закрыта в 08:30 (to = 08:30), now = 20:00. device_wait 08:00 БЕЗ device_wait_cleared:
    // интервал sweep'ом режется к [08:00, 08:30] = 30 мин. LongestWait обязан обрезаться тем же
    // `to` (30), а не тянуться до now (720): иначе «самое длинное ожидание» длиннее всего окна.
    // Обрезка dEnd тестом не покрыта — эта проба её ловит: мутация «снять обрезку у dEnd» →
    // LongestWait = 720 ≠ 30 → краснеет.
    [Fact]
    public void Build_ЗакрытаяФича_DeviceWait_LongestWaitОбрезанПоTo()
    {
        var now = new DateTime(2026, 10, 1, 20, 0, 0);
        var closedAt = new DateTime(2026, 10, 1, 8, 30, 0);
        var epic = Epic("epic");
        epic.Status = TaskItemStatus.Done;
        epic.CompletedAt = closedAt;
        var s1 = Child("s1", "epic");

        var journal = Journal(
            Entry("created", new DateTime(2026, 10, 1, 8, 0, 0), taskId: "epic"),
            Entry("device_wait", new DateTime(2026, 10, 1, 8, 0, 0), taskId: "s1"));

        var tl = FeatureTimeline.Build(Run(epic, s1), journal, now);

        tl.To.Should().Be(closedAt);
        tl.Totals.ElapsedMinutes.Should().Be(30);
        // Ожидание по окну [08:00, 08:30] = 30 мин, посчитано один раз.
        tl.Totals.WaitingMinutes.Should().Be(30);
        tl.Totals.WaitingCount.Should().Be(1);
        // Самое длинное ожидание = 30 (обрезано по to), НЕ 720 (до now).
        tl.Totals.LongestWaitMinutes.Should().Be(30);
        tl.Totals.LongestWaitMinutes.Should().BeLessThanOrEqualTo(tl.Totals.ElapsedMinutes);
    }

    // ── 3e. Эпизоды, начавшиеся за пределами окна, не считаются (замечание 3 ревью Глеба) ────

    // Ожидание, начавшееся после `to`, даёт в окне ноль минут: прежнее `e.At >= from` пропускало
    // события за окном → WaitingCount = 1 при нулевых минутах. Считаются только эпизоды с
    // from <= At < to. Мутация «вернуть без верхней границы» → 1 ≠ 0 → краснеет.
    [Fact]
    public void Build_ОжиданиеПослеTo_НеСчитается()
    {
        var now = new DateTime(2026, 10, 1, 20, 0, 0);
        var closedAt = new DateTime(2026, 10, 1, 8, 30, 0);
        var epic = Epic("epic");
        epic.Status = TaskItemStatus.Done;
        epic.CompletedAt = closedAt;
        var s1 = Child("s1", "epic");

        var journal = Journal(
            Entry("created", new DateTime(2026, 10, 1, 8, 0, 0), taskId: "epic"),
            Entry("waiting", closedAt.AddMinutes(30), taskId: "s1"), // 09:00 — после to
            Entry("device_wait", closedAt.AddMinutes(60), taskId: "s1")); // 09:30 — после to

        var tl = FeatureTimeline.Build(Run(epic, s1), journal, now);

        tl.Totals.WaitingCount.Should().Be(0, "эпизод, начавшийся за окном, не считается");
        tl.Totals.WaitingMinutes.Should().Be(0);
        tl.Totals.LongestWaitMinutes.Should().Be(0);
        // ST-B2: эпизодов ожидания в окне нет → адрес тоже null.
        tl.Totals.LongestWait.Should().BeNull();
        // Инвариант не сломан: всё окно — idle.
        (tl.Totals.WorkWallMinutes + tl.Totals.WaitingMinutes + tl.Totals.ErrorMinutes
            + tl.Totals.IdleMinutes).Should().Be(tl.Totals.ElapsedMinutes);
    }

    // ── 3f. ST-B2: longestWait — адрес самого долгого эпизода ────────────────────────────────

    // Два эпизода ожидания в разных задачах: s1 10:00→11:00 (60 мин), s2 12:00→14:00 (120 мин).
    // longestWait обязан указать на s2 с from=12:00, minutes == LongestWaitMinutes. Мутация
    // «брать первый эпизод, а не самый длинный» (брать taskId/From первого) → TaskId s1 ≠ s2
    // → краснеет.
    [Fact]
    public void Build_LongestWait_УказываетНаЗадачуСамогоДолгогоЭпизода()
    {
        var now = new DateTime(2026, 10, 1, 20, 0, 0);
        var epic = Epic("epic");
        var s1 = Child("s1", "epic");
        var s2 = Child("s2", "epic");

        var journal = Journal(
            Entry("created", new DateTime(2026, 10, 1, 8, 0, 0), taskId: "epic"),
            Entry("waiting", new DateTime(2026, 10, 1, 10, 0, 0), taskId: "s1"),
            Entry("waiting_cleared", new DateTime(2026, 10, 1, 11, 0, 0), taskId: "s1"),
            Entry("waiting", new DateTime(2026, 10, 1, 12, 0, 0), taskId: "s2"),
            Entry("waiting_cleared", new DateTime(2026, 10, 1, 14, 0, 0), taskId: "s2"));

        var tl = FeatureTimeline.Build(Run(epic, s1, s2), journal, now);

        tl.Totals.WaitingCount.Should().Be(2);
        tl.Totals.LongestWaitMinutes.Should().Be(120);

        var lw = tl.Totals.LongestWait;
        lw.Should().NotBeNull("эпизоды ожидания есть");
        lw!.Minutes.Should().Be(tl.Totals.LongestWaitMinutes);
        lw!.TaskId.Should().Be("s2");
        lw!.From.Should().Be(new DateTime(2026, 10, 1, 12, 0, 0));
    }

    // Ожидание, начавшееся ровно в `to` (момент закрытия), длится в окне ноль минут — не
    // считается (граница to исключена). Без верхней границы мутации: WaitingCount = 1 → красный.
    [Fact]
    public void Build_ОжиданиеРовноВTo_НеСчитается()
    {
        var now = new DateTime(2026, 10, 1, 20, 0, 0);
        var closedAt = new DateTime(2026, 10, 1, 8, 30, 0);
        var epic = Epic("epic");
        epic.Status = TaskItemStatus.Done;
        epic.CompletedAt = closedAt;
        var s1 = Child("s1", "epic");

        var journal = Journal(
            Entry("created", new DateTime(2026, 10, 1, 8, 0, 0), taskId: "epic"),
            Entry("device_wait", closedAt, taskId: "s1")); // ровно в to

        var tl = FeatureTimeline.Build(Run(epic, s1), journal, now);

        tl.Totals.WaitingCount.Should().Be(0, "ожидание ровно в момент закрытия длится ноль минут");
        tl.Totals.WaitingMinutes.Should().Be(0);
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

    // ── 10. Замер: 20 000 записей журнала → сотни мс (major 3; абсолютный порог — страховка,
    // дискриминатор — относительный гейт линейности) ──

    // Абсолютный порог (3 с) был поднят с 1.5 с после одного падения на нагруженной машине
    // (замечание 6 ревью Глеба, Б-pr2-д2). На общей машине он хрупок: весь тест укладывается
    // в ~187 мс (запас ≈16×), а старый O(n²) (~4.5 с) превышает его лишь в 1.5× — регрессия
    // до ×15 не была бы замечена. Поэтому рядом с порогом — относительный гейт, не
    // зависящий от скорости машины: время на 20k записей не больше 6× времени на 5k.
    // Линейный алгоритм даёт ×4 (число записей), O(n²) — ×16. Прогревка (JIT) перед замером,
    // минимум нескольких прогонов — одиночный замер шумен.
    private static FeatureJournalReadResult ScaledJournal(int turns, int waits, int startedPairs)
    {
        var entries = new List<FeatureJournalEntry>();
        entries.Add(Entry("created", new DateTime(2026, 10, 1, 8, 0, 0), taskId: "epic"));
        var rng = new Random(7);
        var baseTime = new DateTime(2026, 10, 1, 0, 0, 0);

        // Ходы — основная масса журнала: [at, at+1..9 мин], finished/error.
        for (var i = 0; i < turns; i++)
        {
            var at = baseTime.AddMinutes(rng.NextDouble() * 1440);
            var outcome = rng.Next(5) == 0 ? "error" : "finished"; // ~20% error
            entries.Add(Entry("turn", at, taskId: "s1",
                startedAt: at, endedAt: at.AddMinutes(rng.Next(1, 10)), outcome: outcome));
        }
        // Ожидания: waiting + waiting_cleared.
        for (var i = 0; i < waits; i++)
        {
            var at = baseTime.AddMinutes(rng.NextDouble() * 1440);
            entries.Add(Entry("waiting", at, taskId: "s1"));
            entries.Add(Entry("waiting_cleared", at.AddMinutes(rng.Next(1, 30)), taskId: "s1"));
        }
        // Started/stopped — пары: ограниченные периоды (реалистично: задача стартует и стопится,
        // не 5000 бесконечных started, которые взрывали CutByWaiting).
        for (var i = 0; i < startedPairs; i++)
        {
            var at = baseTime.AddMinutes(rng.NextDouble() * 1440);
            var stop = at.AddMinutes(rng.Next(30, 300));
            entries.Add(Entry("started", at, taskId: "s1", startedAt: at));
            entries.Add(Entry("stopped", stop, taskId: "s1"));
        }

        return Journal(entries.ToArray());
    }

    // Минимум нескольких прогонов: одиночный замер шумен, минимум устойчив.
    private static double MinBuildMs(FeatureRun run, FeatureJournalReadResult journal, DateTime now,
        int attempts)
    {
        double best = double.MaxValue;
        for (var i = 0; i < attempts; i++)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            FeatureTimeline.Build(run, journal, now);
            sw.Stop();
            best = Math.Min(best, sw.Elapsed.TotalMilliseconds);
        }
        return best;
    }

    [Fact]
    public void Build_Замер_20kЗаписей_Быстрый()
    {
        var now = new DateTime(2026, 10, 1, 20, 0, 0);
        var epic = Epic("epic");
        var s1 = Child("s1", "epic", startedAt: new DateTime(2026, 10, 1, 9, 0, 0),
            completedAt: new DateTime(2026, 10, 1, 12, 0, 0));
        var run = Run(epic, s1);

        // Прогревка JIT и первых аллокаций — один прогон на большом журнале без замера.
        FeatureTimeline.Build(run, ScaledJournal(18000, 1000, 20), now);

        var ms5k = MinBuildMs(run, ScaledJournal(4500, 250, 5), now, 3);
        var ms20k = MinBuildMs(run, ScaledJournal(18000, 1000, 20), now, 3);

        // Sanity: лента реально строится и итоги заполнены.
        var tl = FeatureTimeline.Build(run, ScaledJournal(18000, 1000, 20), now);
        tl.Totals.ElapsedMinutes.Should().BeGreaterThan(0);

        // Абсолютный порог — грубая страховка (старый O(n²) ~4.5 с в него не влезает).
        ms20k.Should().BeLessThan(3000.0,
            $"sweep на 20k записей должен быть в сотнях мс, а не {ms20k:F0} мс");

        // Относительный гейт линейности: линейный алгоритм даёт ×4, O(n²) — ×16; порог 6×.
        (ms20k / ms5k).Should().BeLessOrEqualTo(6.0,
            $"нелинейный рост: 20k — {ms20k:F0} мс, 5k — {ms5k:F0} мс (отношение {(ms20k / ms5k):F1}×)");
    }

    // ── 11. (а) 600 ходов + ожидание 60 мин (> 500 сегментов): итоги не обнуляются (major 1) ──

    // 600 ходов по 1 мин (непрерывно) + 1 ожидание 60 мин = 601 сегмент > MaxSegments (500).
    // До фикса (итоги по СКЛЕЕННЫМ, склейка схлопывала всю шкалу в один сегмент): WaitingMinutes=0,
    // ErrorMinutes=0, IdleMinutes=0, WorkWall=660 (весь elapsed). После фикса: итоги из сегментов
    // ДО склейки → WaitingMinutes=60, WorkWall=600, ожидание остаётся видимым 60-мин сегментом.
    [Fact]
    public void Build_600Ходов_Ожидание60Мин_ИтогиНеОбнуляются()
    {
        var t0 = new DateTime(2026, 10, 1, 8, 0, 0);
        var now = t0.AddMinutes(660); // 08:00 → 19:00
        var epic = Epic("epic");
        var s1 = Child("s1", "epic");

        var entries = new List<FeatureJournalEntry>();
        entries.Add(Entry("created", t0, taskId: "epic"));
        // 600 ходов по 1 мин, непрерывно: [t0+i, t0+i+1] для i=0..599.
        for (var i = 0; i < 600; i++)
        {
            var at = t0.AddMinutes(i);
            entries.Add(Entry("turn", at.AddMinutes(1), taskId: "s1",
                startedAt: at, endedAt: at.AddMinutes(1), outcome: "finished"));
        }
        // 1 ожидание 60 мин в конце: [t0+600, t0+660].
        var wStart = t0.AddMinutes(600);
        entries.Add(Entry("waiting", wStart, taskId: "s1"));
        entries.Add(Entry("waiting_cleared", wStart.AddMinutes(60), taskId: "s1"));

        var tl = FeatureTimeline.Build(Run(epic, s1), Journal(entries.ToArray()), now);

        // Итоги (из сегментов ДО склейки): work=600, waiting=60, error=0, idle=0; elapsed=660.
        tl.Totals.ElapsedMinutes.Should().Be(660);
        tl.Totals.WorkWallMinutes.Should().Be(600);
        tl.Totals.WaitingMinutes.Should().Be(60);
        tl.Totals.ErrorMinutes.Should().Be(0);
        tl.Totals.IdleMinutes.Should().Be(0);
        // Инвариант: 600 + 60 + 0 + 0 = 660 = elapsed.
        (tl.Totals.WorkWallMinutes + tl.Totals.WaitingMinutes + tl.Totals.ErrorMinutes
            + tl.Totals.IdleMinutes).Should().Be(tl.Totals.ElapsedMinutes);

        // Сегментов ≤ MaxSegments (500) после склейки.
        tl.Segments.Count.Should().BeLessOrEqualTo(FeatureTimeline.DefaultMaxSegments);
        // Ожидание 60 мин остаётся видимым сегментом нужного вида (не уничтожено склейкой).
        Kinds(tl, "waiting").Should().Contain(s => (s.To - s.From) == TimeSpan.FromMinutes(60));
    }

    // ── 12. (б) Нецелые минуты: 2 хода по 30 с → инвариант work+waiting+error+idle = elapsed ──

    // Проба Глеба: 2 хода по 30 с. До фикса (округление КАЖДОГО сегмента вверх): work=2 (2×ceil(0.5)),
    // idle=2 (2×ceil(0.5)), elapsed=2, сумма видов=4 ≠ 2. После фикса (один ceil на суммарный TimeSpan
    // вида, idle=остаток): work=ceil(1.0)=1, idle=2−1=1, сумма=2=elapsed. Мутация «округление по
    // сегментам» → work=2, idle=0, сумма=2, но idle=0 ≠ 1 → краснеет.
    [Fact]
    public void Build_НецелыеМинуты_ДваХодаПо30С_Инвариант()
    {
        var t0 = new DateTime(2026, 10, 1, 8, 0, 0);
        var now = t0.AddMinutes(2); // 08:00 → 08:02
        var epic = Epic("epic");
        var s1 = Child("s1", "epic");

        // 2 хода по 30 с: [08:00,08:00:30] и [08:01,08:01:30]; между ними и после — по 30 с простоя.
        var journal = Journal(
            Entry("created", t0, taskId: "epic"),
            Entry("turn", t0.AddMinutes(0.5), taskId: "s1",
                startedAt: t0, endedAt: t0.AddMinutes(0.5), outcome: "finished"),
            Entry("turn", t0.AddMinutes(1.5), taskId: "s1",
                startedAt: t0.AddMinutes(1), endedAt: t0.AddMinutes(1.5), outcome: "finished"));

        var tl = FeatureTimeline.Build(Run(epic, s1), journal, now);

        tl.Totals.ElapsedMinutes.Should().Be(2);
        // work = 2×30с = 1 мин (один ceil от суммарного TimeSpan вида, не сумма ceil по сегментам).
        tl.Totals.WorkWallMinutes.Should().Be(1);
        tl.Totals.WaitingMinutes.Should().Be(0);
        tl.Totals.ErrorMinutes.Should().Be(0);
        // idle = остаток от elapsed = 2 − 1 = 1 мин (не 2, как при округлении каждого сегмента).
        tl.Totals.IdleMinutes.Should().Be(1);
        // Инвариант: 1 + 0 + 0 + 1 = 2 = elapsed.
        (tl.Totals.WorkWallMinutes + tl.Totals.WaitingMinutes + tl.Totals.ErrorMinutes
            + tl.Totals.IdleMinutes).Should().Be(tl.Totals.ElapsedMinutes);
    }

    // ── 13. (в) Склейка не уничтожает короткое, но заметное ожидание при потолке ───────────────

    // 1000 ходов по 1 мин (непрерывно) + 1 ожидание 10 мин = 1001 сегмент > MaxSegments (500).
    // Склейка (MergeToCap) склеивает ближайшие пары (все разрывы 0, потеря 1 мин) слева направо;
    // ожидание 10 мин (потеря пары с ним = min(1,10)=1, но оно правее) при 501 склейках с левого
    // края НЕ затрагивается → остаётся видимым 10-мин сегментом. Мутация «склейка схлопывает всё
    // подряд» (старый MergeByThreshold) → ожидание поглощается → краснеет.
    [Fact]
    public void Build_Склейка_НеУничтожаетЗаметноеОжидание()
    {
        var t0 = new DateTime(2026, 10, 1, 8, 0, 0);
        var now = t0.AddMinutes(1010); // 08:00 → 24:50
        var epic = Epic("epic");
        var s1 = Child("s1", "epic");

        var entries = new List<FeatureJournalEntry>();
        entries.Add(Entry("created", t0, taskId: "epic"));
        // 1000 ходов по 1 мин, непрерывно: [t0+i, t0+i+1] для i=0..999.
        for (var i = 0; i < 1000; i++)
        {
            var at = t0.AddMinutes(i);
            entries.Add(Entry("turn", at.AddMinutes(1), taskId: "s1",
                startedAt: at, endedAt: at.AddMinutes(1), outcome: "finished"));
        }
        // 1 ожидание 10 мин в конце: [t0+1000, t0+1010].
        var wStart = t0.AddMinutes(1000);
        entries.Add(Entry("waiting", wStart, taskId: "s1"));
        entries.Add(Entry("waiting_cleared", wStart.AddMinutes(10), taskId: "s1"));

        var tl = FeatureTimeline.Build(Run(epic, s1), Journal(entries.ToArray()), now);

        tl.Totals.ElapsedMinutes.Should().Be(1010);
        tl.Totals.WaitingMinutes.Should().Be(10);
        tl.Segments.Count.Should().BeLessOrEqualTo(FeatureTimeline.DefaultMaxSegments);
        // Ожидание 10 мин — видимый сегмент нужного вида (не поглощено склейкой).
        Kinds(tl, "waiting").Should().Contain(s => (s.To - s.From) == TimeSpan.FromMinutes(10));
    }

    // ── 14. (минор 7) Stage/TaskId в журнальном режиме: сегмент несёт подписи этапа ───────────

    // Мутация «подпись = ""» (Stage/TaskId всегда пустые) → тест краснеет. Ход с taskId задачи
    // (role=step → prod) даёт work-сегмент со Stage="prod" и TaskId="s1" (не пустые).
    [Fact]
    public void Build_Журнал_СегментНесётStageИTaskId()
    {
        var now = new DateTime(2026, 10, 1, 20, 0, 0);
        var epic = Epic("epic");
        var s1 = Child("s1", "epic"); // role=step → prod

        var journal = Journal(
            Entry("created", new DateTime(2026, 10, 1, 8, 0, 0), taskId: "epic"),
            Entry("turn", new DateTime(2026, 10, 1, 10, 0, 0), taskId: "s1",
                startedAt: new DateTime(2026, 10, 1, 9, 0, 0),
                endedAt: new DateTime(2026, 10, 1, 10, 0, 0), outcome: "finished"));

        var tl = FeatureTimeline.Build(Run(epic, s1), journal, now);

        // work-сегмент хода [09:00,10:00]: Stage="prod" (role=step), TaskId="s1".
        var work = Kinds(tl, "work").Single(s => s.From == new DateTime(2026, 10, 1, 9, 0, 0));
        work.Stage.Should().Be("prod");
        work.TaskId.Should().Be("s1");
    }

    // ── 15. (major) Инвариант на дробных минутах: числа из пробы Глеба ───────────────────────

    // Окно 600 мин; сырые span-ы видов: work=598.5, waiting=0.8, error=0.5, idle=0.2 (сумма 600).
    // Независимые ceil дали бы 599+1+1+1 = 602 ≠ 600. Метод наибольшего остатка: floor (598,0,0,0)
    // + дефицит 2 (elapsed 600 − 598) видам с наибольшей дробной (waiting 0.8, work 0.5) →
    // work=599, waiting=1, error=0, idle=0; сумма = 600 = elapsed РОВНО.
    [Fact]
    public void Build_Инвариант_ДробныеМинуты_ЧислаПробы()
    {
        var t0 = new DateTime(2026, 10, 1, 8, 0, 0);
        var now = t0.AddMinutes(600); // 08:00 → 18:00
        var epic = Epic("epic");
        var s1 = Child("s1", "epic");

        // work [08:00, 17:58:30] (598.5 мин); waiting [17:58:30, 17:59:18] (0.8 мин);
        // error [17:59:18, 17:59:48] (0.5 мин); idle [17:59:48, 18:00] (0.2 мин).
        var journal = Journal(
            Entry("created", t0, taskId: "epic"),
            Entry("turn", t0.AddMinutes(598.5), taskId: "s1",
                startedAt: t0, endedAt: t0.AddMinutes(598.5), outcome: "finished"),
            Entry("waiting", t0.AddMinutes(598.5), taskId: "s1"),
            Entry("waiting_cleared", t0.AddMinutes(599.3), taskId: "s1"),
            Entry("turn", t0.AddMinutes(599.8), taskId: "s1",
                startedAt: t0.AddMinutes(599.3), endedAt: t0.AddMinutes(599.8), outcome: "error"));

        var tl = FeatureTimeline.Build(Run(epic, s1), journal, now);

        tl.Totals.ElapsedMinutes.Should().Be(600);
        tl.Totals.WorkWallMinutes.Should().Be(599);
        tl.Totals.WaitingMinutes.Should().Be(1);
        tl.Totals.ErrorMinutes.Should().Be(0);
        tl.Totals.IdleMinutes.Should().Be(0);
        // Инвариант: 599 + 1 + 0 + 0 = 600 = elapsed (не 602, как при независимых ceil).
        (tl.Totals.WorkWallMinutes + tl.Totals.WaitingMinutes + tl.Totals.ErrorMinutes
            + tl.Totals.IdleMinutes).Should().Be(tl.Totals.ElapsedMinutes);
    }

    // ── 16. (major) Проба 30 с + 20 с + 10 с в окне 2 мин: сумма видов = elapsed ──────────────

    // Ход 30 с + ожидание 20 с + ошибка 10 с в окне 2 мин (остаток 60 с — idle). Независимые ceil
    // дали work=1, waiting=1, error=1, idle=0 → сумма 3 ≠ 2. Наибольший остаток: floor (0,0,0,1)
    // + дефицит 1 виду с наибольшей дробной (work 0.5) → work=1, waiting=0, error=0, idle=1;
    // сумма = 2 = elapsed.
    [Fact]
    public void Build_Инвариант_Ход30с_Ожидание20с_Ошибка10с()
    {
        var t0 = new DateTime(2026, 10, 1, 8, 0, 0);
        var now = t0.AddMinutes(2); // 08:00 → 08:02
        var epic = Epic("epic");
        var s1 = Child("s1", "epic");

        // work [08:00:00, 08:00:30] (30 с); waiting [08:00:30, 08:00:50] (20 с);
        // error [08:00:50, 08:01:00] (10 с); idle [08:01:00, 08:02:00] (60 с).
        var journal = Journal(
            Entry("created", t0, taskId: "epic"),
            Entry("turn", t0.AddSeconds(30), taskId: "s1",
                startedAt: t0, endedAt: t0.AddSeconds(30), outcome: "finished"),
            Entry("waiting", t0.AddSeconds(30), taskId: "s1"),
            Entry("waiting_cleared", t0.AddSeconds(50), taskId: "s1"),
            Entry("turn", t0.AddSeconds(60), taskId: "s1",
                startedAt: t0.AddSeconds(50), endedAt: t0.AddSeconds(60), outcome: "error"));

        var tl = FeatureTimeline.Build(Run(epic, s1), journal, now);

        tl.Totals.ElapsedMinutes.Should().Be(2);
        tl.Totals.WorkWallMinutes.Should().Be(1);
        tl.Totals.WaitingMinutes.Should().Be(0);
        tl.Totals.ErrorMinutes.Should().Be(0);
        tl.Totals.IdleMinutes.Should().Be(1);
        // Инвариант: 1 + 0 + 0 + 1 = 2 = elapsed (не 3, как при независимых ceil).
        (tl.Totals.WorkWallMinutes + tl.Totals.WaitingMinutes + tl.Totals.ErrorMinutes
            + tl.Totals.IdleMinutes).Should().Be(tl.Totals.ElapsedMinutes);
    }

    // ── 17. (major, свойство) Сумма видов == elapsed на нескольких наборах дробных минут ─────

    // МАЛОЕ окно (60 с): work+waiting+error почти заполняют его, idle — крошечный остаток.
    // Независимые ceil дают sum(ceil) = 3 > elapsed = 1 (каждый дробный вид округляется до 1),
    // а idle = max(0, 1 − 3) = 0 → сумма 3 ≠ 1. Наибольший остаток: floor (0,0,0,0) + дефицит 1
    // виду с наибольшей дробной → сумма = 1 = elapsed. Мутация «ceil + max(0, …)» красит ВСЕ
    // случаи (сумма 3 ≠ 1). Окно 60 с, не 10 мин: в большом окне sum(ceil)=3 < elapsed=10 и
    // idle поглощает разницу — свойство не нарушается, тест был бы слепым.
    [Theory]
    [InlineData(30, 15, 10)]   // 0.5 + 0.25 + 0.17 мин, idle 0.08 мин
    [InlineData(20, 20, 20)]   // 0.33 + 0.33 + 0.33 мин, idle 0
    [InlineData(40, 10, 10)]   // 0.67 + 0.17 + 0.17 мин, idle 0
    [InlineData(15, 20, 25)]   // 0.25 + 0.33 + 0.42 мин, idle 0
    public void Build_Свойство_СуммаВидов_РавнаElapsed(int workSec, int waitingSec, int errorSec)
    {
        var t0 = new DateTime(2026, 10, 1, 8, 0, 0);
        var now = t0.AddSeconds(60); // окно 60 с = 1 мин
        var epic = Epic("epic");
        var s1 = Child("s1", "epic");

        var wEnd = t0.AddSeconds(workSec);
        var waEnd = wEnd.AddSeconds(waitingSec);
        var eEnd = waEnd.AddSeconds(errorSec);
        var journal = Journal(
            Entry("created", t0, taskId: "epic"),
            Entry("turn", wEnd, taskId: "s1",
                startedAt: t0, endedAt: wEnd, outcome: "finished"),
            Entry("waiting", wEnd, taskId: "s1"),
            Entry("waiting_cleared", waEnd, taskId: "s1"),
            Entry("turn", eEnd, taskId: "s1",
                startedAt: waEnd, endedAt: eEnd, outcome: "error"));

        var tl = FeatureTimeline.Build(Run(epic, s1), journal, now);

        tl.Totals.ElapsedMinutes.Should().Be(1);
        // Свойство: сумма видов = elapsed ровно (для ЛЮБОГО расклада дробных минут).
        (tl.Totals.WorkWallMinutes + tl.Totals.WaitingMinutes + tl.Totals.ErrorMinutes
            + tl.Totals.IdleMinutes).Should().Be(tl.Totals.ElapsedMinutes,
            $"work={workSec}с waiting={waitingSec}с error={errorSec}с: сумма видов ≠ elapsed");
    }

    // ── 18. (минор 10) AgentsSum: started-период и его ходы — без двойного счёта ─────────────

    // started 08:00 + ход 08:00–08:10 + status Done 08:10. approx: одна задача = один период
    // [ClaudeStartedAt, CompletedAt] = 10 мин. journal (до фикса): started-период (10) + ход (10)
    // = 20 (двойной счёт). После фикса: AgentsSum = started-период = 10 (ходы внутри не
    // пересчитываются). Мутация «AgentsSum = сумма ВСЕХ work-интервалов (ходы + периоды)» → 20 →
    // краснеет.
    [Fact]
    public void Build_AgentsSum_StartedИХоды_БезДвойногоСчёта()
    {
        var t0 = new DateTime(2026, 10, 1, 8, 0, 0);
        var now = t0.AddMinutes(10); // 08:00 → 08:10
        var epic = Epic("epic");
        var s1 = Child("s1", "epic", startedAt: t0, completedAt: t0.AddMinutes(10));

        var journal = Journal(
            Entry("created", t0, taskId: "epic"),
            Entry("started", t0, taskId: "s1", startedAt: t0),
            Entry("turn", t0.AddMinutes(10), taskId: "s1",
                startedAt: t0, endedAt: t0.AddMinutes(10), outcome: "finished"),
            Entry("status", t0.AddMinutes(10), taskId: "s1", to: "Done"));

        var tl = FeatureTimeline.Build(Run(epic, s1), journal, now);

        // AgentsSum = started-период [08:00, 08:10] = 10 мин (не 20 = период + ход).
        tl.Totals.AgentsSumMinutes.Should().Be(10,
            "AgentsSum — один период на задачу (как в approx), ходы внутри не пересчитываются");
        // WorkWall = объединение = 10 (период и ход совпадают).
        tl.Totals.WorkWallMinutes.Should().Be(10);
    }

    // ── 18b. Хвост шова Александра (п.2в): AgentsSum — агентные минуты ВНУТРИ окна фичи ────────

    // Фича закрыта в 08:30 (to = 08:30), started-период 08:10 без stopped/done: его конец — now.
    // Без клипа по окну число растёт каждый день после закрытия (подсказка «агенты в сумме ≈ …»
    // не воспроизводится). После клипа: AgentsSum = [08:10, 08:30] = 20 мин — одно и то же при
    // двух разных now, и не больше параллельных (1) × Elapsed (30). Мутация «убрать клип» →
    // 710 ≠ 20 → краснеет.
    [Fact]
    public void Build_ЗакрытаяФича_AgentsSum_ОбрезанПоTo_НезависимОтNow()
    {
        var closedAt = new DateTime(2026, 10, 1, 8, 30, 0);
        var epic = Epic("epic");
        epic.Status = TaskItemStatus.Done;
        epic.CompletedAt = closedAt;
        var s1 = Child("s1", "epic");

        var journal = Journal(
            Entry("created", new DateTime(2026, 10, 1, 8, 0, 0), taskId: "epic"),
            Entry("started", new DateTime(2026, 10, 1, 8, 10, 0), taskId: "s1",
                startedAt: new DateTime(2026, 10, 1, 8, 10, 0)));

        var now1 = new DateTime(2026, 10, 1, 20, 0, 0);
        var now2 = new DateTime(2026, 10, 2, 20, 0, 0); // сутки спустя — число не меняется

        var tl1 = FeatureTimeline.Build(Run(epic, s1), journal, now1);
        var tl2 = FeatureTimeline.Build(Run(epic, s1), journal, now2);

        tl1.To.Should().Be(closedAt);
        tl1.Totals.ElapsedMinutes.Should().Be(30);
        tl1.Totals.AgentsSumMinutes.Should().Be(20, "агентные минуты внутри окна [from, to]");
        tl2.Totals.AgentsSumMinutes.Should().Be(20, "число не зависит от now");
        // Верхняя граница: параллельные (1) × Elapsed.
        tl1.Totals.AgentsSumMinutes.Should().BeLessThanOrEqualTo(tl1.Totals.ElapsedMinutes);
    }

    // ── 19. (минор 2) Ожидание в СЕРЕДИНЕ среди 1000 ходов: склейка не искажает его длительность ─

    // 1000 ходов по 1 мин + 10-мин ожидание В СЕРЕДИНЕ (после 500 ходов) = 1001 сегмент > 500.
    // Прежняя склейка (ключ (gap, loss, left)) деградировала в «слева направо»: левый work-блок
    // рос и поглощал ожидание → видимый waiting-сегмент 12 мин (10 + 2 хода) вместо 10. Новое
    // правило (не сливать разную семантику: work+work прежде work+waiting) схлопывает work-ходы
    // на обоих краях, и ожидание остаётся ~10-мин сегментом. Мутация «склейка слева направо»
    // (ключ (gap, loss, left)) → ожидание искажается → краснеет.
    [Fact]
    public void Build_Склейка_ОжиданиеВСередине_ДлительностьНеИскажается()
    {
        var t0 = new DateTime(2026, 10, 1, 8, 0, 0);
        var now = t0.AddMinutes(1010); // 08:00 → 24:50
        var epic = Epic("epic");
        var s1 = Child("s1", "epic");

        var entries = new List<FeatureJournalEntry>();
        entries.Add(Entry("created", t0, taskId: "epic"));
        // 500 ходов по 1 мин: [t0+i, t0+i+1] для i=0..499.
        for (var i = 0; i < 500; i++)
        {
            var at = t0.AddMinutes(i);
            entries.Add(Entry("turn", at.AddMinutes(1), taskId: "s1",
                startedAt: at, endedAt: at.AddMinutes(1), outcome: "finished"));
        }
        // Ожидание 10 мин В СЕРЕДИНЕ: [t0+500, t0+510].
        var wStart = t0.AddMinutes(500);
        entries.Add(Entry("waiting", wStart, taskId: "s1"));
        entries.Add(Entry("waiting_cleared", wStart.AddMinutes(10), taskId: "s1"));
        // 500 ходов по 1 мин после ожидания: [t0+510+i, t0+510+i+1] для i=0..499.
        for (var i = 0; i < 500; i++)
        {
            var at = t0.AddMinutes(510 + i);
            entries.Add(Entry("turn", at.AddMinutes(1), taskId: "s1",
                startedAt: at, endedAt: at.AddMinutes(1), outcome: "finished"));
        }

        var tl = FeatureTimeline.Build(Run(epic, s1), Journal(entries.ToArray()), now);

        tl.Totals.ElapsedMinutes.Should().Be(1010);
        tl.Totals.WaitingMinutes.Should().Be(10);
        tl.Segments.Count.Should().BeLessOrEqualTo(FeatureTimeline.DefaultMaxSegments);
        // Ожидание 10 мин — видимый сегмент, длительность НЕ искажена (не 12 = 10 + 2 хода).
        var waitSegs = Kinds(tl, "waiting");
        waitSegs.Should().NotBeEmpty("ожидание в середине не должно поглощаться склейкой");
        foreach (var s in waitSegs)
            (s.To - s.From).TotalMinutes.Should().BeLessOrEqualTo(10.5,
                "ожидание 10 мин не должно раздуться до 12+ мин склейкой");
    }

    // ── 20. (минор 3) Вид склеенного — ДОМИНАНТА по времени (мутация «от левого» краснеет) ─────

    // Две склеиваемые сущности разной длительности: короткий work (1 мин) + длинный waiting
    // (10 мин) — но чтобы они склеились, нужно > 500 сегментов. Проще: 501 сегмент, где на
    // границе пары «work(1) + waiting(10)» доминанта по времени — waiting. После склейки вид
    // склеенного = waiting (доминанта), НЕ work (левый). Мутация «вид = от левого» → work →
    // краснеет (waiting-сегментов станет меньше).
    //
    // Конструкция: 499 work-ходов по 1 мин + 1 waiting 10 мин + 1 work-ход 1 мин = 501 сегмент.
    // Потолок 500 → 1 склейка. Ключ (mixed, loss, left): пара (work[498], waiting) mixed=1,
    // пара (waiting, work[500]) mixed=1, пары work+work mixed=0. Склейка идёт по mixed=0 (work+work)
    // ПЕРВЫМИ — но их 498 (между 499 ходами), а нам нужно ровно 1 склейка (501 → 500). Единственная
    // склейка — минимальный ключ: mixed=0 (work+work) выигрывает у mixed=1. Хм — тогда waiting не
    // склеивается. Пересмотр: чтобы заставить склейку work+waiting, сделаем ВСЕ пары mixed, кроме
    // одной. Проще: 2 сегмента (work 1 мин + waiting 10 мин) + потолок 1 → 1 склейка, доминанта
    // waiting. Но 2 сегмента < 500. Используем maxSegments=1 (внутренний хук) на 2-сегментном входе.
    [Fact]
    public void Build_Склейка_ВидСклеенного_ДоминантаПоВремени()
    {
        var t0 = new DateTime(2026, 10, 1, 8, 0, 0);
        var now = t0.AddMinutes(11); // 08:00 → 08:11
        var epic = Epic("epic");
        var s1 = Child("s1", "epic");

        // work-ход 1 мин [08:00, 08:01] + waiting 10 мин [08:01, 08:11] = 2 сегмента.
        var journal = Journal(
            Entry("created", t0, taskId: "epic"),
            Entry("turn", t0.AddMinutes(1), taskId: "s1",
                startedAt: t0, endedAt: t0.AddMinutes(1), outcome: "finished"),
            Entry("waiting", t0.AddMinutes(1), taskId: "s1"),
            Entry("waiting_cleared", t0.AddMinutes(11), taskId: "s1"));

        // maxSegments=1 (внутренний хук): 2 сегмента → 1 склейка. Доминанта по времени — waiting
        // (10 > 1) → склеенный вид = waiting (не work = «от левого»).
        var tl = FeatureTimeline.Build(Run(epic, s1), journal, now, maxSegments: 1);

        tl.Segments.Should().HaveCount(1);
        // Вид склеенного — доминанта по времени (waiting 10 мин > work 1 мин).
        tl.Segments[0].Kind.Should().Be("waiting",
            "вид склеенного — доминанта по времени (waiting 10 мин), не «от левого» (work)");
    }

    // ── Хвост Б5b (а): AgentsSum — ОДНО правило округления (journal) ─────────────────────────

    // Две задачи по 30 с: AgentsSum = сумма started-периодов (60 с) + ОДИН CeilMinutes = 1 мин.
    // То же число, что в approx (FeatureTimelineTests.Build_Approx_AgentsSum_…): правило
    // округления выровнено — per-task ceil дал бы 2 в обоих режимах.
    [Fact]
    public void Build_Journal_AgentsSum_ОдноОкругление_ДвеТридцатьСекунд()
    {
        var now = new DateTime(2026, 10, 1, 20, 0, 0);
        var epic = Epic("epic");
        // Две последовательные задачи, каждая ровно 30 с (started…done).
        var s1 = Child("s1", "epic", startedAt: new DateTime(2026, 10, 1, 10, 0, 0),
            completedAt: new DateTime(2026, 10, 1, 10, 0, 30));
        var s2 = Child("s2", "epic", startedAt: new DateTime(2026, 10, 1, 10, 1, 0),
            completedAt: new DateTime(2026, 10, 1, 10, 1, 30));

        var journal = Journal(
            Entry("created", new DateTime(2026, 10, 1, 8, 0, 0), taskId: "epic"),
            Entry("started", new DateTime(2026, 10, 1, 10, 0, 0), taskId: "s1",
                startedAt: new DateTime(2026, 10, 1, 10, 0, 0)),
            Entry("status", new DateTime(2026, 10, 1, 10, 0, 30), taskId: "s1", to: "Done"),
            Entry("started", new DateTime(2026, 10, 1, 10, 1, 0), taskId: "s2",
                startedAt: new DateTime(2026, 10, 1, 10, 1, 0)),
            Entry("status", new DateTime(2026, 10, 1, 10, 1, 30), taskId: "s2", to: "Done"));

        var tl = FeatureTimeline.Build(Run(epic, s1, s2), journal, now);

        // Сумма started-периодов 60 с → один CeilMinutes = 1 мин (per-task ceil дал бы 2).
        tl.Totals.AgentsSumMinutes.Should().Be(1, "сумма 60 с → ceil = 1, не 2 (per-task)");
    }

    // ── Дефект 2c5e5d87 (проба карточки): WaitingCount и инвариант при ожидании ровно в from ──

    // Проба из карточки дефекта: created 08:00, waiting 08:00 (ровно в from), waiting_cleared
    // 08:10, now = 08:10. Прежнее `e.At > from` теряло эпизод: WaitingMinutes = 0, WaitingCount = 0,
    // 10 минут уходили в idle. После фикса: WaitingMinutes = 10, WaitingCount = 1, сумма видов
    // = elapsed (инвариант). Мутация «вернуть >» → 0 ≠ 10 → краснеет.
    [Fact]
    public void Build_Дефект2c5e5d87_ПробаКарточки_Count1_Инвариант()
    {
        var t0 = new DateTime(2026, 10, 1, 8, 0, 0);
        var now = t0.AddMinutes(10); // 08:00 → 08:10
        var epic = Epic("epic"); // CreatedAt = 08:00 = from
        var s1 = Child("s1", "epic");

        var journal = Journal(
            Entry("created", t0, taskId: "epic"),
            Entry("waiting", t0, taskId: "s1"),
            Entry("waiting_cleared", t0.AddMinutes(10), taskId: "s1"));

        var tl = FeatureTimeline.Build(Run(epic, s1), journal, now);

        tl.Totals.ElapsedMinutes.Should().Be(10);
        tl.Totals.WaitingMinutes.Should().Be(10,
            "ожидание ровно в from (08:00) не теряется (прежнее > давало 0)");
        tl.Totals.WaitingCount.Should().Be(1);
        tl.Totals.LongestWaitMinutes.Should().Be(10);
        // Сумма видов = elapsed: минуты ожидания не «стекают» в idle.
        (tl.Totals.WorkWallMinutes + tl.Totals.WaitingMinutes + tl.Totals.ErrorMinutes
            + tl.Totals.IdleMinutes).Should().Be(tl.Totals.ElapsedMinutes);
    }

    // ── Дефект 2c5e5d87: ожидание ровно в `from` не отбрасывается ─────────────────────────────

    // waiting-событие с At == from (момент создания эпика): прежнее `e.At > from` отбрасывало его
    // (08:00 > 08:00 = false) → WaitingMinutes = 0. После фикса `e.At >= from` → ожидание
    // [08:00, 09:00] = 60 мин. Мутация «вернуть >» → 0 ≠ 60 → краснеет.
    [Fact]
    public void Build_Journal_ОжиданиеРовноВFrom_НеОтбрасывается()
    {
        var now = new DateTime(2026, 10, 1, 20, 0, 0);
        var epic = Epic("epic"); // CreatedAt = 08:00 = from
        var s1 = Child("s1", "epic");

        // created 08:00 (= from); waiting 08:00 (ровно в from) → waiting_cleared 09:00.
        var journal = Journal(
            Entry("created", new DateTime(2026, 10, 1, 8, 0, 0), taskId: "epic"),
            Entry("waiting", new DateTime(2026, 10, 1, 8, 0, 0), taskId: "s1"),
            Entry("waiting_cleared", new DateTime(2026, 10, 1, 9, 0, 0), taskId: "s1"));

        var tl = FeatureTimeline.Build(Run(epic, s1), journal, now);

        tl.Totals.WaitingMinutes.Should().Be(60,
            "ожидание ровно в from (08:00) входит, не отбрасывается (прежнее > давало 0)");
    }

    // ── Хелпер: сегменты заданного вида ───────────────────────────────────────────────────────

    private static List<TimelineSegment> Kinds(FeatureTimeline tl, string kind) =>
        tl.Segments.Where(s => s.Kind == kind).ToList();
}
