using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Tasks;
using ClaudeHomeServer.Services.Tasks.Journal;
using ClaudeHomeServer.Services.Tasks.Projector;
using Microsoft.Extensions.Configuration;

namespace ClaudeHomeServer.Tasks.Tests;

// Следы очереди фич в журнале и ленте (волна 6, шаг Q3B): by=queue у hold_cleared,
// totals.queued в таймлайне, событие «Фича запущена из очереди».
public class FeatureQueueTraceTests
{
    private static readonly DateTime T0 = new(2026, 10, 1, 8, 0, 0);

    private static (TaskManager Tasks, FeatureJournalStore Journal, string Dir) Make()
    {
        var dir = Path.Combine(Path.GetTempPath(), "q3b_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["DataPath"] = Path.Combine(dir, "projects.json") })
            .Build();
        var journal = new FeatureJournalStore(Path.Combine(dir, "feature-journal"));
        return (new TaskManager(config, journal: journal), journal, dir);
    }

    private static FeatureJournalEntry Held(DateTime at, string reason) =>
        new() { At = at, Kind = "held", TaskId = "epic", Reason = reason };

    private static FeatureJournalEntry Cleared(DateTime at, string reason, string? by = null) =>
        new() { At = at, Kind = "hold_cleared", TaskId = "epic", Reason = reason, By = by };

    private static FeatureRun EpicRun() =>
        FeatureProjector.BuildCore(
            new TaskItem { Id = "epic", Role = TaskRoles.Feature, Status = TaskItemStatus.Todo, CreatedAt = T0 }, []);

    private static FeatureJournalReadResult Full(params FeatureJournalEntry[] entries) => new()
    {
        StartsWithCreated = true,
        Truncated = false,
        Entries = [new FeatureJournalEntry { At = T0, Kind = "created", TaskId = "epic" }, .. entries],
    };

    [Fact]
    public void ClearHold_ByQueue_ПишетByВЖурнал_РучноеСнятиеБезBy()
    {
        var (tasks, journal, dir) = Make();
        try
        {
            var auto = tasks.Create(null, "u", new CreateTaskRequest("автоснятие", Role: TaskRoles.Feature));
            tasks.SetHold(auto.Id, FeatureHoldReasons.Queue);
            tasks.ClearHold(auto.Id, FeatureHoldBy.Queue);
            var manual = tasks.Create(null, "u", new CreateTaskRequest("ручное", Role: TaskRoles.Feature));
            tasks.SetHold(manual.Id, FeatureHoldReasons.Queue);
            tasks.ClearHold(manual.Id);

            var autoCleared = journal.Read("u", auto.Id).Entries.Single(e => e.Kind == "hold_cleared");
            autoCleared.Reason.Should().Be("queue");
            autoCleared.By.Should().Be("queue");
            var manualCleared = journal.Read("u", manual.Id).Entries.Single(e => e.Kind == "hold_cleared");
            manualCleared.Reason.Should().Be("queue");
            manualCleared.By.Should().BeNull("ручное снятие поле by не пишет");
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Read_ЗаписьБезBy_ЧитаетсяВерсияОстаётсяV1()
    {
        var dir = Path.Combine(Path.GetTempPath(), "q3b_" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(dir, "u", "feat.jsonl");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path,
                "{\"v\":1,\"at\":\"2026-10-01T08:00:00Z\",\"kind\":\"hold_cleared\",\"taskId\":\"feat\",\"reason\":\"manual\"}\n");

            var entry = new FeatureJournalStore(dir).Read("u", "feat").Entries.Single();

            entry.Kind.Should().Be("hold_cleared");
            entry.Reason.Should().Be("manual");
            entry.By.Should().BeNull();
            entry.V.Should().Be(1);
            new FeatureJournalEntry().V.Should().Be(1, "поле by необязательное: версия формата не растёт");
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    // Окно 08:00 → 12:00 (240 мин), без работы. Очередь 09:00–10:30 (90 мин), ручная пауза
    // 10:30–11:00 (30 мин): в queued попадает только очередь, простой уменьшается на неё.
    [Fact]
    public void Build_ИнтервалОчереди_ПопадаетВQueued_НеВIdle()
    {
        var journal = Full(
            Held(T0.AddHours(1), FeatureHoldReasons.Queue),
            Cleared(T0.AddHours(2.5), FeatureHoldReasons.Queue, FeatureHoldBy.Queue),
            Held(T0.AddHours(2.5), FeatureHoldReasons.Manual),
            Cleared(T0.AddHours(3), FeatureHoldReasons.Manual));

        var t = FeatureTimeline.Build(EpicRun(), journal, T0.AddHours(4)).Totals;

        t.ElapsedMinutes.Should().Be(240);
        t.QueuedMinutes.Should().Be(90);
        t.IdleMinutes.Should().Be(150, "простой без минут очереди; ручная пауза остаётся в простое");
        (t.WorkWallMinutes + t.WaitingMinutes + t.ErrorMinutes + t.IdleMinutes + t.QueuedMinutes)
            .Should().Be(t.ElapsedMinutes);
    }

    [Fact]
    public void Build_ПустойЖурналБезОчереди_QueuedНоль()
    {
        FeatureTimeline.Build(EpicRun(), Full(), T0.AddHours(1)).Totals.QueuedMinutes.Should().Be(0);
    }

    // Граница half-open: очередь, открытая в to, в окно не попадает.
    [Fact]
    public void Build_ОчередьНаГраницеОкна_НеСчитается()
    {
        var now = T0.AddHours(1);
        var t = FeatureTimeline.Build(EpicRun(), Full(Held(now, FeatureHoldReasons.Queue)), now).Totals;
        t.QueuedMinutes.Should().Be(0);
    }

    // Сценарий MAJOR-2 ревью RV-Q3B-Q4: фича «ждёт вас» (waiting без cleared) → поставлена в
    // очередь → 3 часа в очереди. Интервал очереди считается ЦЕЛИКОМ в queued и вычитается из
    // waiting: три часа не попадают в waitingMinutes (не двойной счёт).
    [Fact]
    public void Build_ОчередьПоверхОжидания_QueuedЦеликом_ИзWaitingВычтено()
    {
        var journal = Full(
            new FeatureJournalEntry { At = T0.AddHours(1), Kind = "waiting", TaskId = "epic" },
            Held(T0.AddHours(1), FeatureHoldReasons.Queue),
            Cleared(T0.AddHours(4), FeatureHoldReasons.Queue, FeatureHoldBy.Queue));

        var t = FeatureTimeline.Build(EpicRun(), journal, T0.AddHours(4)).Totals;

        t.ElapsedMinutes.Should().Be(240);
        t.QueuedMinutes.Should().Be(180, "весь интервал held(queue)..hold_cleared, а не только перекрытие с idle");
        t.WaitingMinutes.Should().Be(0, "ожидание целиком внутри очереди — его минуты вычтены из waiting");
        t.IdleMinutes.Should().Be(60);
        // R-Q3B-4: net-ожидание того же эпизода — 0: «ожидание 0 мин» не стоит рядом с
        // «дольше всех 3 ч». WaitingCount — счётчик событий, очередь его не меняет.
        t.WaitingCount.Should().Be(1);
        t.LongestWaitMinutes.Should().Be(0);
        t.LongestWait.Should().NotBeNull("эпизод ожидания был — адрес остаётся");
        t.LongestWait!.Minutes.Should().Be(t.LongestWaitMinutes);
        (t.WorkWallMinutes + t.WaitingMinutes + t.ErrorMinutes + t.IdleMinutes + t.QueuedMinutes)
            .Should().Be(t.ElapsedMinutes);
    }

    // Частичное перекрытие: очередь [09:00,10:30) поверх ожидания [09:00,12:00): в queued —
    // 90 мин очереди, оставшиеся 90 мин ожидания остаются в waiting.
    [Fact]
    public void Build_ОчередьЧастьОжидания_ОстатокОстаётсяВWaiting()
    {
        var journal = Full(
            new FeatureJournalEntry { At = T0.AddHours(1), Kind = "waiting", TaskId = "epic" },
            Held(T0.AddHours(1), FeatureHoldReasons.Queue),
            Cleared(T0.AddHours(2.5), FeatureHoldReasons.Queue, FeatureHoldBy.Queue));

        var t = FeatureTimeline.Build(EpicRun(), journal, T0.AddHours(4)).Totals;

        t.ElapsedMinutes.Should().Be(240);
        t.QueuedMinutes.Should().Be(90);
        t.WaitingMinutes.Should().Be(90, "ожидание 09:00–12:00 минус перекрытая очередью часть 09:00–10:30");
        t.IdleMinutes.Should().Be(60);
        // R-Q3B-4: longestWait — по тому же net-правилу: 180 − 90 = 90, а не сырые 180.
        t.LongestWaitMinutes.Should().Be(90);
        t.LongestWait!.Minutes.Should().Be(t.LongestWaitMinutes);
        (t.WorkWallMinutes + t.WaitingMinutes + t.ErrorMinutes + t.IdleMinutes + t.QueuedMinutes)
            .Should().Be(t.ElapsedMinutes);
    }

    // Хвост ревью Q6-TAIL (п.1): ветка device_wait — то же net-правило, что и waiting.
    // device_wait 09:00–12:00 (сырые 180) и очередь 09:00–10:30: остаток 90 остаётся в waiting,
    // longestWait = 90, а не сырые 180. Мутация «в device_wait считать сырой интервал»
    // (FeatureTimeline.cs:388, без NetSpan) → 180 ≠ 90 → красный.
    [Fact]
    public void Build_ОчередьЧастьDeviceWait_LongestWaitNet()
    {
        var journal = Full(
            new FeatureJournalEntry { At = T0.AddHours(1), Kind = "device_wait", TaskId = "epic" },
            new FeatureJournalEntry { At = T0.AddHours(4), Kind = "device_wait_cleared", TaskId = "epic" },
            Held(T0.AddHours(1), FeatureHoldReasons.Queue),
            Cleared(T0.AddHours(2.5), FeatureHoldReasons.Queue, FeatureHoldBy.Queue));

        var t = FeatureTimeline.Build(EpicRun(), journal, T0.AddHours(4)).Totals;

        t.ElapsedMinutes.Should().Be(240);
        t.QueuedMinutes.Should().Be(90);
        t.WaitingMinutes.Should().Be(90, "остаток device_wait после перекрытой очередью части");
        t.IdleMinutes.Should().Be(60);
        t.WaitingCount.Should().Be(1);
        t.LongestWaitMinutes.Should().Be(90, "net: сырые 180 минус 90 очереди — ветка device_wait net, как waiting");
        t.LongestWait!.From.Should().Be(T0.AddHours(1));
        (t.WorkWallMinutes + t.WaitingMinutes + t.ErrorMinutes + t.IdleMinutes + t.QueuedMinutes)
            .Should().Be(t.ElapsedMinutes);
    }

    // Хвост ревью Q6-TAIL (п.2): NetSpan вычитает ВСЕ пересекающиеся интервалы очереди, не первый.
    // Фича в очереди 09:00–10:00, вышла, снова в очереди 11:00–12:00; ожидание 09:00…now (15:00)
    // длится через оба: net = 360 − 60 − 60 = 240. Мутация «учесть только первое пересечение»
    // (break в NetSpan) → 300 ≠ 240 → красный.
    [Fact]
    public void Build_ОжиданиеЧерезДвеОчереди_NetВычитаетОбе()
    {
        var journal = Full(
            new FeatureJournalEntry { At = T0.AddHours(1), Kind = "waiting", TaskId = "epic" },
            Held(T0.AddHours(1), FeatureHoldReasons.Queue),
            Cleared(T0.AddHours(2), FeatureHoldReasons.Queue, FeatureHoldBy.Queue),
            Held(T0.AddHours(3), FeatureHoldReasons.Queue),
            Cleared(T0.AddHours(4), FeatureHoldReasons.Queue, FeatureHoldBy.Queue));

        var t = FeatureTimeline.Build(EpicRun(), journal, T0.AddHours(7)).Totals;

        t.ElapsedMinutes.Should().Be(420);
        t.QueuedMinutes.Should().Be(120, "оба интервала очереди");
        t.WaitingMinutes.Should().Be(240, "ожидание 09:00–15:00 минус оба пересечённых интервала очереди");
        t.IdleMinutes.Should().Be(60);
        t.WaitingCount.Should().Be(1);
        t.LongestWaitMinutes.Should().Be(240, "net вычитает оба интервала, а не только первый");
        t.LongestWait!.From.Should().Be(T0.AddHours(1));
        (t.WorkWallMinutes + t.WaitingMinutes + t.ErrorMinutes + t.IdleMinutes + t.QueuedMinutes)
            .Should().Be(t.ElapsedMinutes);
    }

    // R-Q3B-4: longestWait выбирает эпизод по net-ожиданию. Эпизод 09:00–12:00 целиком в
    // очереди (net 0), второй 13:00–13:40 вне очереди (net 40): самое долгое — 40 мин и адрес
    // второго эпизода, а не «сырые 3 ч» первого. WaitingCount = 2: очередь не отменяет факт
    // ожидания. Мутация «сравнивать сырые интервалы» (без NetSpan) → 180 ≠ 40 → красный.
    [Fact]
    public void Build_LongestWaitПоNet_ЭпизодВОчередиНеПобеждает()
    {
        var journal = Full(
            new FeatureJournalEntry { At = T0.AddHours(1), Kind = "waiting", TaskId = "epic" },
            new FeatureJournalEntry { At = T0.AddHours(4), Kind = "waiting_cleared", TaskId = "epic" },
            Held(T0.AddHours(1), FeatureHoldReasons.Queue),
            Cleared(T0.AddHours(4), FeatureHoldReasons.Queue, FeatureHoldBy.Queue),
            new FeatureJournalEntry { At = T0.AddHours(5), Kind = "waiting", TaskId = "epic" },
            new FeatureJournalEntry { At = T0.AddHours(5).AddMinutes(40), Kind = "waiting_cleared", TaskId = "epic" });

        var t = FeatureTimeline.Build(EpicRun(), journal, T0.AddHours(6)).Totals;

        t.WaitingCount.Should().Be(2);
        t.WaitingMinutes.Should().Be(40);
        t.QueuedMinutes.Should().Be(180);
        t.LongestWaitMinutes.Should().Be(40, "net-максимум: 40 мин вне очереди, а не 180 мин внутри неё");
        var lw = t.LongestWait;
        lw.Should().NotBeNull();
        lw!.Minutes.Should().Be(t.LongestWaitMinutes);
        lw!.From.Should().Be(T0.AddHours(5), "адрес — эпизод с наибольшим net, а не с наибольшим сырым интервалом");
        (t.WorkWallMinutes + t.WaitingMinutes + t.ErrorMinutes + t.IdleMinutes + t.QueuedMinutes)
            .Should().Be(t.ElapsedMinutes);
    }

    // Перекрытие с незакрытым started-периодом (исполнитель убит без stopped): минуты очереди
    // вычитаются из work, а не остаются неучтёнными.
    [Fact]
    public void Build_ОчередьПоверхНезакрытогоStarted_ВычитаетсяИзWork()
    {
        var started = new FeatureJournalEntry
        {
            At = T0.AddHours(1), StartedAt = T0.AddHours(1), Kind = "started", TaskId = "epic",
        };
        var journal = Full(
            started,
            Held(T0.AddHours(1), FeatureHoldReasons.Queue),
            Cleared(T0.AddHours(2), FeatureHoldReasons.Queue, FeatureHoldBy.Queue));

        var t = FeatureTimeline.Build(EpicRun(), journal, T0.AddHours(3)).Totals;

        t.ElapsedMinutes.Should().Be(180);
        t.QueuedMinutes.Should().Be(60);
        t.WorkWallMinutes.Should().Be(60, "started 09:00…now минус перекрытая очередью часть 09:00–10:00");
        t.IdleMinutes.Should().Be(60);
        (t.WorkWallMinutes + t.WaitingMinutes + t.ErrorMinutes + t.IdleMinutes + t.QueuedMinutes)
            .Should().Be(t.ElapsedMinutes);
    }

    // R-Q3B-5 (Major-1 ревью RV-K-Q3B-2): очередь вычитается и из «агентов суммарно» — по тем же
    // net-интервалам, что и workWall. Сценарий: ОДИН агент, started 09:00…11:00 + очередь
    // 09:00–10:00 → workWall=60, а gross-подсчёт давал agentsSum=120: плитка «работали 1 ч» с
    // подписью «агентов суммарно 2 ч» — числа противоречат друг другу. После фикса: agentsSum=60.
    // Мутация «вернуть gross-сумму started-периодов (без NetSpan)» → 120 ≠ 60 → красный.
    // Хвост ревью Q6-TAIL (п.3): равенство agentsSum и workWall здесь — свойство ЭТОГО сценария
    // (один агент, параллельной работы нет), а не инвариант кода: при параллельных агентах сумма
    // законно больше workWall (в approx это закреплено Build_ПараллельныеЗадачи_ОбъединениеНеСумма,
    // FeatureTimelineTests.cs:121). agentsSum — сумма по агентам, не min(agentsSum, workWall).
    [Fact]
    public void Build_ОчередьПоверхStarted_ОдинАгент_AgentsSumТожеNet()
    {
        var journal = Full(
            new FeatureJournalEntry
            {
                At = T0.AddHours(1), StartedAt = T0.AddHours(1), Kind = "started", TaskId = "epic",
            },
            Held(T0.AddHours(1), FeatureHoldReasons.Queue),
            Cleared(T0.AddHours(2), FeatureHoldReasons.Queue, FeatureHoldBy.Queue));

        var t = FeatureTimeline.Build(EpicRun(), journal, T0.AddHours(3)).Totals;

        t.WorkWallMinutes.Should().Be(60);
        t.AgentsSumMinutes.Should().Be(60, "агентные минуты — net, как у work: минуты в очереди не работа");
        t.AgentsSumMinutes.Should().BeLessThanOrEqualTo(t.WorkWallMinutes,
            "свойство этого сценария (один агент, параллельной работы нет), а не правило кода: " +
            "при параллельных агентах сумма законно больше workWall");
    }

    // Q6-TAIL2 (п.3, RV-K-Q3B-5 находка 3): предыдущий тест — один агент, и там net-СУММА
    // совпадает с net-ОБЪЕДИНЕНИЕМ: он их не различает. Здесь два перекрывающихся started-периода
    // (одинаковые 09:00…11:00, 120 мин каждый) + очередь 09:30–10:30 (60 мин):
    // agentsSum = 2×(120−60) = 120 — агентные минуты складываются; workWall = 120−60 = 60 —
    // время на графике считается один раз. Мутация «объединение вместо суммы»
    // (FeatureTimeline.cs, в цикле agentsSpan «+=» заменить на «=»: объединение двух одинаковых
    // периодов — один период) → agentsSum = 60 ≠ 120 → красный.
    [Fact]
    public void Build_ДваПерекрывающихсяStartedПериодаОчередь_AgentsSumСуммаНеОбъединение()
    {
        var journal = Full(
            new FeatureJournalEntry { At = T0.AddHours(1), StartedAt = T0.AddHours(1), Kind = "started", TaskId = "s1" },
            new FeatureJournalEntry { At = T0.AddHours(1), StartedAt = T0.AddHours(1), Kind = "started", TaskId = "s2" },
            Held(T0.AddHours(1.5), FeatureHoldReasons.Queue),
            Cleared(T0.AddHours(2.5), FeatureHoldReasons.Queue, FeatureHoldBy.Queue),
            new FeatureJournalEntry { At = T0.AddHours(3), Kind = "status", To = "Done", TaskId = "s1" },
            new FeatureJournalEntry { At = T0.AddHours(3), Kind = "status", To = "Done", TaskId = "s2" });

        var t = FeatureTimeline.Build(EpicRun(), journal, T0.AddHours(4)).Totals;

        t.ElapsedMinutes.Should().Be(240);
        t.QueuedMinutes.Should().Be(60);
        t.WorkWallMinutes.Should().Be(60, "net-ОБЪЕДИНЕНИЕ периодов: 120 минус очередь, время на графике один раз");
        t.AgentsSumMinutes.Should().Be(120, "net-СУММА: 2×(120−60) — агентные минуты складываются, " +
            "объединение дало бы 60");
        t.AgentsSumMinutes.Should().BeGreaterThan(t.WorkWallMinutes,
            "net-СУММА отличается от net-ОБЪЕДИНЕНИЯ ровно здесь: два агента, параллельная работа есть");
        t.IdleMinutes.Should().Be(120);
        (t.WorkWallMinutes + t.WaitingMinutes + t.ErrorMinutes + t.IdleMinutes + t.QueuedMinutes)
            .Should().Be(t.ElapsedMinutes);
    }

    [Fact]
    public void BuildEvents_ByQueue_ДаётСобытиеЗапущенаИзОчереди()
    {
        var at = T0.AddHours(2);
        var events = FeatureProjector.BuildEvents(EpicRun(), [],
            [Cleared(at, FeatureHoldReasons.Queue, FeatureHoldBy.Queue)]);

        var e = events.Single(x => x.Kind == "started_from_queue");
        e.Reason.Should().Be("Фича запущена из очереди");
        e.At.Should().Be(at);
        e.TaskId.Should().Be("epic");
    }

    [Fact]
    public void BuildEvents_РучноеСнятиеИБезЖурнала_СобытияОчередиНет()
    {
        var run = EpicRun();
        FeatureProjector.BuildEvents(run, [], [Cleared(T0.AddHours(2), FeatureHoldReasons.Queue)])
            .Should().NotContain(x => x.Kind == "started_from_queue");
        FeatureProjector.BuildEvents(run, []).Should().NotContain(x => x.Kind == "started_from_queue");
    }
}
