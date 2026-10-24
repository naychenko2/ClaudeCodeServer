using System.Diagnostics;
using System.IO;
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
// текущей фичы — тест с текущей краснеет по Count; убрать UpdatedAt из ревизии — тест
// инвалидации красный (кэш не сбрасывается); убрать кэш — тест тёплого запроса красный
// (журнал читается снова); считать ревизию по всем задачам владельца — тест чужого
// проекта красный (активность в другом проекте сбрасывает кэш).
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

    // ─── хвост вердикта Глеба (b684cff84): кэш по проекту, инвалидация, waitingCount ────────────

    // Тёплый запрос: тот же набор данных → второй Build не читает журнал (счётчик вызовов).
    // Мутация «не хранить выборку в кэше» → счётчик растёт → тест красный.
    [Fact]
    public void Typical_ТёплыйЗапросНеЧитаетЖурнал()
    {
        var tasks = new List<TaskItem>();
        tasks.AddRange(MergedFeature("a", "t6", "pW", 100, 1));
        tasks.AddRange(MergedFeature("b", "t6", "pW", 200, 2));
        tasks.AddRange(MergedFeature("c", "t6", "pW", 300, 3));

        int reads = 0;
        Func<string, FeatureJournalReadResult?> read = id => { reads++; return JournalWithWaiting(id, 10); };

        FeatureTypical.Build(tasks, "t6", "pW", "cur", read, NOW).Should().NotBeNull();
        reads.Should().Be(3, "холодный запрос: одно чтение журнала на смерженную фичу");

        var warm = FeatureTypical.Build(tasks, "t6", "pW", "cur", read, NOW);
        warm!.Count.Should().Be(3);
        reads.Should().Be(3, "ревизия не изменилась — журнал не читается снова");
    }

    // Ревизия — по задачам проекта: активность в другом проекте (новые смерженные фичи с
    // новыми UpdatedAt) не сбрасывает кэш и не меняет числа. Мутация «ревизия по всем
    // задачам владельца» (как было) → второй вызов читает журнал заново → тест красный.
    [Fact]
    public void Typical_АктивностьВЧужомПроектНеСбрасываетКэш()
    {
        var tasks = new List<TaskItem>();
        tasks.AddRange(MergedFeature("a", "t7", "pA", 100, 1));
        tasks.AddRange(MergedFeature("b", "t7", "pA", 200, 2));
        tasks.AddRange(MergedFeature("c", "t7", "pA", 300, 3));

        int reads = 0;
        Func<string, FeatureJournalReadResult?> read = id => { reads++; return JournalWithWaiting(id, 10); };

        var cold = FeatureTypical.Build(tasks, "t7", "pA", "cur", read, NOW);
        cold!.Duration.Median.Should().Be(200.0);
        int afterCold = reads;

        tasks.AddRange(MergedFeature("x1", "t7", "pB", 500, 5));
        tasks.AddRange(MergedFeature("x2", "t7", "pB", 600, 6));
        tasks.AddRange(MergedFeature("x3", "t7", "pB", 700, 7));

        var warm = FeatureTypical.Build(tasks, "t7", "pA", "cur", read, NOW);
        warm!.Duration.Median.Should().Be(200.0, "выборка проекта pA не изменилась");
        reads.Should().Be(afterCold, "ревизия pA не изменилась — журнал не читается снова");
    }

    // Инвалидация: изменение задачи проекта (сдвиг мержа) → следующий Build даёт новые числа.
    // Мутация «убрать UpdatedAt из ревизии» (ревизия = только число эпиков) → кэш не
    // сбрасывается, числа старые → тест красный.
    [Fact]
    public void Typical_ИзменениеЗадачиПроектаМеняетЧисла()
    {
        var tasks = new List<TaskItem>();
        tasks.AddRange(MergedFeature("a", "t8", "pI", 100, 1));
        tasks.AddRange(MergedFeature("b", "t8", "pI", 200, 2));
        tasks.AddRange(MergedFeature("c", "t8", "pI", 300, 3));

        var before = FeatureTypical.Build(tasks, "t8", "pI", "cur", null, NOW);
        before!.Duration.Median.Should().Be(200.0);

        // Фича «a» смержена на 400 минут позже — её длительность входит в выборку.
        var mergeA = tasks.First(t => t.Id == "m-a");
        mergeA.CompletedAt = T0 + TimeSpan.FromMinutes(400);
        mergeA.UpdatedAt = NOW;

        var after = FeatureTypical.Build(tasks, "t8", "pI", "cur", null, NOW);
        after!.Duration.Median.Should().Be(300.0, "длительности [400,200,300] — медиана 300");
    }

    // Размер выборки ожидания: count — все смерженные, waitingCount — только фичи с полным
    // журналом. Метрика ожидания считается, когда фич с журналом ≥ 3.
    [Fact]
    public void Typical_WaitingCountРазмерВыборкиОжидания()
    {
        var tasks = new List<TaskItem>();
        for (int i = 0; i < 5; i++)
            tasks.AddRange(MergedFeature($"f{i}", "t9", "pC", 100 + 100 * i, i));

        var journals = new Dictionary<string, FeatureJournalReadResult>
        {
            ["f0"] = JournalWithWaiting("f0", 10),
            ["f1"] = JournalWithWaiting("f1", 20),
            ["f2"] = JournalWithWaiting("f2", 30),
        };

        var stats = FeatureTypical.Build(tasks, "t9", "pC", "cur",
            id => journals.TryGetValue(id, out var j) ? j : null, NOW);

        stats!.Count.Should().Be(5);
        stats.WaitingCount.Should().Be(3, "полный журнал — только у трёх");
        stats.Waiting.Should().NotBeNull("фич с журналом ровно три — метрика считается");
        stats.Waiting!.Median.Should().Be(20.0);
    }

    // Квантили на n = 4 и на одинаковых значениях (хвост вердикта): [100,200,300,400] →
    // median 250, p25 175, p75 325; все 100 → 100/100/100.
    [Fact]
    public void Typical_КвантилиN4ИОдинаковыхЗначений()
    {
        var tasks = new List<TaskItem>();
        for (int i = 0; i < 4; i++)
            tasks.AddRange(MergedFeature($"q{i}", "t10", "pQ", 100 * (i + 1), 0));

        var stats = FeatureTypical.Build(tasks, "t10", "pQ", "cur", null, NOW);
        stats!.Count.Should().Be(4);
        stats.Duration.Median.Should().Be(250.0);
        stats.Duration.P25.Should().Be(175.0);
        stats.Duration.P75.Should().Be(325.0);

        var same = new List<TaskItem>();
        for (int i = 0; i < 3; i++)
            same.AddRange(MergedFeature($"s{i}", "t11", "pS", 100, 0));

        var flat = FeatureTypical.Build(same, "t11", "pS", "cur", null, NOW);
        flat!.Duration.Median.Should().Be(100.0);
        flat!.Duration.P25.Should().Be(100.0);
        flat!.Duration.P75.Should().Be(100.0);
    }

    // Цена холодного и тёплого запроса на фикстуре из 100 смерженных фич (100 FeatureProjector
    // .Build + 100 чтений журнала против только квантилей). Числа пишутся в файл — они идут
    // в отчёт задачи. Тест красный, если тёплый запрос не дешевле холодного.
    [Fact]
    public void Typical_ЦенаХолодногоИТёплогоЗапросаНа100Фичах()
    {
        var tasks = new List<TaskItem>();
        var journals = new Dictionary<string, FeatureJournalReadResult>();
        for (int i = 0; i < 100; i++)
        {
            tasks.AddRange(MergedFeature($"f{i}", "t12", "pB", 100 + i, i % 5));
            journals[$"f{i}"] = JournalWithWaiting($"f{i}", 10 + i);
        }
        Func<string, FeatureJournalReadResult?> read = id => journals.TryGetValue(id, out var j) ? j : null;

        var coldSw = Stopwatch.StartNew();
        var cold = FeatureTypical.Build(tasks, "t12", "pB", "cur", read, NOW);
        coldSw.Stop();
        long coldTicks = coldSw.ElapsedTicks;

        var warmSw = Stopwatch.StartNew();
        var warm = FeatureTypical.Build(tasks, "t12", "pB", "cur", read, NOW);
        warmSw.Stop();
        long warmTicks = warmSw.ElapsedTicks;

        cold!.Count.Should().Be(100);
        warm!.Count.Should().Be(100);
        warmTicks.Should().BeLessThan(coldTicks,
            "тёплый запрос: ни FeatureProjector.Build, ни чтений журнала");

        double ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "typical-bench.txt"),
            $"cold={ms(coldTicks):F1} ms, warm={ms(warmTicks):F3} ms, features=100");
    }
}
