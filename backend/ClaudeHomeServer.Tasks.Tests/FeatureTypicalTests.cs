using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Tasks.Journal;
using ClaudeHomeServer.Services.Tasks.Projector;

namespace ClaudeHomeServer.Tasks.Tests;

// ST-B1: «типичная фича проекта» (stats.typical) — FeatureTypical.Build: медиана и p25–p75
// длительности, ожидания и возвратов по СМЕРЖЕННЫМ фичам того же проекта и того же владельца;
// текущая фича исключена; смерженных меньше трёх — null. Квантили — линейная интерполяция
// (тип 7): для n = 3 p25 = (v0+v1)/2, p75 = (v1+v2)/2 — ожидаемые числа точные.
//
// Мутации: убрать фильтр по проекту/владельцу — тест изоляции красный; убрать исключение
// текущей фичы — тест с текущей краснеет по Count.
public class FeatureTypicalTests
{
    private static readonly DateTime T0 = new(2026, 10, 1, 8, 0, 0);
    private static readonly DateTime NOW = new(2026, 10, 11, 8, 0, 0);

    // Смерженная фича: эпик Done + мерж Done → State == merged. Длительность — минуты от
    // самого раннего начала (CreatedAt = T0) до CompletedAt мержа; возвраты — число
    // вердиктов Returned у ревью. Возвращает задачи фичи: эпик + мерж + ревью.
    private static List<TaskItem> MergedFeature(string id, string owner, string? project,
        int durationMinutes, int loops)
    {
        var epic = new TaskItem
        {
            Id = id, Role = TaskRoles.Feature, OwnerId = owner, ProjectId = project,
            Status = TaskItemStatus.Done, CreatedAt = T0, UpdatedAt = T0,
        };
        var merge = new TaskItem
        {
            Id = "m-" + id, FeatureId = id, Role = TaskRoles.Merge, Status = TaskItemStatus.Done,
            CreatedAt = T0, UpdatedAt = T0,
            ClaudeStartedAt = T0 + TimeSpan.FromMinutes(durationMinutes),
            CompletedAt = T0 + TimeSpan.FromMinutes(durationMinutes),
        };
        var list = new List<TaskItem> { epic, merge };
        for (int i = 0; i < loops; i++)
            list.Add(new TaskItem
            {
                Id = $"{id}-r{i}", FeatureId = id, Role = TaskRoles.Review,
                Status = TaskItemStatus.Done, CreatedAt = T0, UpdatedAt = T0,
                Verification = new TaskVerification { Verdict = TaskVerdicts.Returned },
            });
        return list;
    }

    // Открытая (не смерженная) фича — «текущая» в запросе.
    private static TaskItem OpenFeature(string id, string owner, string? project) => new()
    {
        Id = id, Role = TaskRoles.Feature, OwnerId = owner, ProjectId = project,
        Status = TaskItemStatus.InProgress, CreatedAt = T0, UpdatedAt = T0,
    };

    // Полный журнал с одним эпизодом ожидания заданной длины.
    private static FeatureJournalReadResult JournalWithWaiting(string epicId, int minutes) => new()
    {
        StartsWithCreated = true,
        Truncated = false,
        Entries =
        [
            new FeatureJournalEntry { At = T0, Kind = "created", TaskId = epicId },
            new FeatureJournalEntry { At = T0 + TimeSpan.FromMinutes(10), Kind = "waiting", TaskId = epicId },
            new FeatureJournalEntry
            {
                At = T0 + TimeSpan.FromMinutes(10 + minutes),
                Kind = "waiting_cleared",
                TaskId = epicId,
            },
        ],
    };

    // Три смерженные (100/10/1, 200/20/2, 300/30/3) + текущая смерженная с экстремальными
    // числами (1000/500/10) — текущая в выборку не входит. Квантили по [100,200,300],
    // [10,20,30], [1,2,3].
    [Fact]
    public void Typical_ТриСмерженныеМедианаИКвартилиТекущаяИсключена()
    {
        var tasks = new List<TaskItem>();
        tasks.AddRange(MergedFeature("a", "t1", "p1", 100, 1));
        tasks.AddRange(MergedFeature("b", "t1", "p1", 200, 2));
        tasks.AddRange(MergedFeature("c", "t1", "p1", 300, 3));
        tasks.AddRange(MergedFeature("cur", "t1", "p1", 1000, 10));

        var journals = new Dictionary<string, FeatureJournalReadResult>
        {
            ["a"] = JournalWithWaiting("a", 10),
            ["b"] = JournalWithWaiting("b", 20),
            ["c"] = JournalWithWaiting("c", 30),
            ["cur"] = JournalWithWaiting("cur", 500),
        };

        var stats = FeatureTypical.Build(tasks, "t1", "p1", "cur", id => journals[id], NOW);

        stats.Should().NotBeNull();
        stats!.Count.Should().Be(3, "текущая фича — вне выборки");
        stats.Duration.Median.Should().Be(200.0);
        stats.Duration.P25.Should().Be(150.0);
        stats.Duration.P75.Should().Be(250.0);
        stats.Waiting!.Median.Should().Be(20.0);
        stats.Waiting!.P25.Should().Be(15.0);
        stats.Waiting!.P75.Should().Be(25.0);
        stats.Loops.Median.Should().Be(2.0);
        stats.Loops.P25.Should().Be(1.5);
        stats.Loops.P75.Should().Be(2.5);
    }

    // Две смерженные — null: шкалы сравнения нет.
    [Fact]
    public void Typical_ДвеСмерженныеNull()
    {
        var tasks = new List<TaskItem>();
        tasks.AddRange(MergedFeature("a", "t2", "p1", 100, 1));
        tasks.AddRange(MergedFeature("b", "t2", "p1", 200, 2));
        tasks.Add(OpenFeature("cur", "t2", "p1"));

        FeatureTypical.Build(tasks, "t2", "p1", "cur", null, NOW).Should().BeNull(
            "смерженных меньше трёх — выборка бессмысленна");
    }

    // Изоляция: в целевом проекте и у целевого владельца — две смерженные; три в другом
    // проекте и три у другого владельца в том же проекте — не считаются. Без фильтра по
    // проекту/владельцу этот вызов вернул бы шкалу (тест красный).
    [Fact]
    public void Typical_ЧужойПроектИЧужойВладелецНеУчитываются()
    {
        var tasks = new List<TaskItem>();
        tasks.AddRange(MergedFeature("a", "t3", "p1", 100, 1));
        tasks.AddRange(MergedFeature("b", "t3", "p1", 200, 2));
        tasks.AddRange(MergedFeature("x1", "t3", "p2", 500, 5));
        tasks.AddRange(MergedFeature("x2", "t3", "p2", 600, 6));
        tasks.AddRange(MergedFeature("x3", "t3", "p2", 700, 7));
        tasks.AddRange(MergedFeature("y1", "t4", "p1", 800, 8));
        tasks.AddRange(MergedFeature("y2", "t4", "p1", 900, 9));
        tasks.AddRange(MergedFeature("y3", "t4", "p1", 1000, 10));

        FeatureTypical.Build(tasks, "t3", "p1", "cur", null, NOW).Should().BeNull(
            "в целевом проекте и у целевого владельца — только две смерженные");
    }

    // Смерженные без полного журнала: ожидание неизвестно → метрика null; длительность и
    // возвраты — считаются.
    [Fact]
    public void Typical_БезЖурналаОжиданиеNullДлительностьИВозвратыЕсть()
    {
        var tasks = new List<TaskItem>();
        tasks.AddRange(MergedFeature("a", "t5", "p1", 100, 1));
        tasks.AddRange(MergedFeature("b", "t5", "p1", 200, 2));
        tasks.AddRange(MergedFeature("c", "t5", "p1", 300, 3));

        var stats = FeatureTypical.Build(tasks, "t5", "p1", "cur", id => null, NOW);

        stats.Should().NotBeNull();
        stats!.Count.Should().Be(3);
        stats.Waiting.Should().BeNull("полного журнала нет — ожидание не известно");
        stats.Duration.Median.Should().Be(200.0);
        stats.Loops.Median.Should().Be(2.0);
    }
}
