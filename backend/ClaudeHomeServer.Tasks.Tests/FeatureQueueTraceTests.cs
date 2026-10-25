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
