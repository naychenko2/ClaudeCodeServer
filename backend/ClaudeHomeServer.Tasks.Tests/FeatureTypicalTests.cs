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
// текущей фичы — тест с текущей краснеет по Count; убрать UpdatedAt из ревизии фичи — тест
// инвалидации красный (кэш не сбрасывается); убрать кэш — тест тёплого запроса красный
// (журнал читается снова); считать ревизию по проекту целиком, а не по фичам — тест живой
// фичи красный (её активность пересчитывает журналы всех смерженных); убрать наличие
// журнала из ключа — тест доступности журнала красный; суммировать эпизоды ожидания без
// объединения — тест семантики ожидания красный; выселять весь словарь при переполнении —
// тест выселения красный; класть в кэш выборку с длительностью от `now` — тест «now не
// застывает» красный.
//
// B2a (stepMinutes/tailMinutes): мутация «хвост от первой AcceptedAt, не от последней» →
// медианный тест красный; «порог шаговой выборки убрать» → тест порога красный; «шаг/хвост не
// класть в кэш» или «считать их с новым чтением журнала» → тест счётчика красный.
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

    // Смерженная фича С ШАГАМИ (B2a): каждый шаг — задача (ClaudeStartedAt..CompletedAt,
    // длительность stepDurations[i]) и принятый виток ревью через reviewOf, закрывающий её через
    // reviewGap минут; затем принятый финал и мерж ровно через tailMinutes минут после последнего
    // принятия (финал — внутри хвоста, за 5 минут до мержа). Цепочка такого шага принята
    // (AcceptedAt = CompletedAt витка), её DurationMinutes —
    // ровно stepDurations[i] (FeatureStepAccounting: старт задачи → её закрытие). Финал без
    // reviewOf в цепочки не входит. Возвращает задачи фичи.
    private static List<TaskItem> MergedFeatureWithSteps(string id, string owner, string? project,
        int[] stepDurations, int reviewGap, int tailMinutes)
    {
        var list = new List<TaskItem>
        {
            new TaskItem
            {
                Id = id, Role = TaskRoles.Feature, OwnerId = owner, ProjectId = project,
                Status = TaskItemStatus.Done, CreatedAt = T0, UpdatedAt = T0,
            },
        };
        var lastAccepted = T0;
        for (int i = 0; i < stepDurations.Length; i++)
        {
            var start = lastAccepted + TimeSpan.FromMinutes(5);
            var done = start + TimeSpan.FromMinutes(stepDurations[i]);
            var accepted = done + TimeSpan.FromMinutes(reviewGap);
            list.Add(new TaskItem
            {
                Id = $"{id}-s{i}", FeatureId = id, Role = TaskRoles.Step, StepRef = $"S{i + 1}",
                Status = TaskItemStatus.Done, CreatedAt = T0, UpdatedAt = T0,
                ClaudeStartedAt = start, CompletedAt = done,
            });
            list.Add(new TaskItem
            {
                Id = $"{id}-r{i}", FeatureId = id, Role = TaskRoles.Review,
                Status = TaskItemStatus.Done, CreatedAt = T0, UpdatedAt = T0,
                ReviewOf = [$"{id}-s{i}"], CompletedAt = accepted,
                Verification = new TaskVerification { Verdict = TaskVerdicts.Accepted },
            });
            lastAccepted = accepted;
        }
        list.Add(new TaskItem
        {
            Id = $"{id}-fin", FeatureId = id, Role = TaskRoles.Final,
            Status = TaskItemStatus.Done, CreatedAt = T0, UpdatedAt = T0,
            CompletedAt = lastAccepted + TimeSpan.FromMinutes(tailMinutes - 5),
            Verification = new TaskVerification { Verdict = TaskVerdicts.Accepted },
        });
        list.Add(new TaskItem
        {
            Id = "m-" + id, FeatureId = id, Role = TaskRoles.Merge,
            Status = TaskItemStatus.Done, CreatedAt = T0, UpdatedAt = T0,
            CompletedAt = lastAccepted + TimeSpan.FromMinutes(tailMinutes),
        });
        return list;
    }

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

    // Полный журнал с несколькими эпизодами ожидания разных задач (отсценки от T0, минуты):
    // параллельное ожидание в бою — всегда разные задачи, одна cleared закрывает свой эпизод.
    private static FeatureJournalReadResult JournalWithEpisodes(string epicId,
        List<(string TaskId, double StartMin, double EndMin)> episodes)
    {
        var entries = new List<FeatureJournalEntry>
        {
            new FeatureJournalEntry { At = T0, Kind = "created", TaskId = epicId },
        };
        foreach (var (taskId, s, e) in episodes)
        {
            entries.Add(new FeatureJournalEntry { At = T0 + TimeSpan.FromMinutes(s), Kind = "waiting", TaskId = taskId });
            entries.Add(new FeatureJournalEntry { At = T0 + TimeSpan.FromMinutes(e), Kind = "waiting_cleared", TaskId = taskId });
        }
        return new FeatureJournalReadResult { StartsWithCreated = true, Truncated = false, Entries = entries };
    }

    // Три смерженные (100/10/1, 200/20/2, 300/30/3) + текущая смерженная с экстремальными
    // числами (1000/500/10) — текущая в выборку не входит. Квантили по [100,200,300],
    // [10,20,30], [1,2,3].
    [Fact]
    public void Typical_ТриСмерженныеМедианаИКвартилиТекущаяИсключена()
    {
        FeatureTypical.ClearCache();
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
        FeatureTypical.ClearCache();
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
        FeatureTypical.ClearCache();
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
        FeatureTypical.ClearCache();
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

    // ─── хвосты вердикта Глеба (b684cff84, ST-R-B1): кэш по фиче, семантика ожидания ────────────

    // Тёплый запрос: тот же набор данных → второй Build не читает журнал (счётчик вызовов).
    // Мутация «не хранить выборку в кэше» → счётчик растёт → тест красный.
    [Fact]
    public void Typical_ТёплыйЗапросНеЧитаетЖурнал()
    {
        FeatureTypical.ClearCache();
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
        reads.Should().Be(3, "ревизия фич не изменилась — журнал не читается снова");
    }

    // Ревизия — по фиче: активность живой фичи в том же проекте (её шаг двигается) не
    // пересчитывает журналы смерженных — только выборку самой живой фичи. Мутация «ревизия
    // по проекту целиком» (как было) → второй вызов читает журнал заново → тест красный.
    [Fact]
    public void Typical_АктивностьЖивойФичиНеСбрасываетКэшСмерженных()
    {
        FeatureTypical.ClearCache();
        var tasks = new List<TaskItem>();
        tasks.AddRange(MergedFeature("a", "t13", "pL", 100, 1));
        tasks.AddRange(MergedFeature("b", "t13", "pL", 200, 2));
        tasks.AddRange(MergedFeature("c", "t13", "pL", 300, 3));
        tasks.Add(OpenFeature("cur", "t13", "pL"));
        var liveStep = new TaskItem
        {
            Id = "cur-s0", FeatureId = "cur", Role = TaskRoles.Step,
            Status = TaskItemStatus.InProgress, CreatedAt = T0, UpdatedAt = T0,
        };
        tasks.Add(liveStep);

        int reads = 0;
        Func<string, FeatureJournalReadResult?> read = id => { reads++; return JournalWithWaiting(id, 10); };

        var cold = FeatureTypical.Build(tasks, "t13", "pL", "cur", read, NOW);
        cold!.Duration.Median.Should().Be(200.0);
        int afterCold = reads; // 3: журнал читается только для смерженных фич

        // Живая фича двигается: шаг её обновлён — ревизия проекта (старая) изменилась бы.
        liveStep.UpdatedAt = NOW;

        var warm = FeatureTypical.Build(tasks, "t13", "pL", "cur", read, NOW);
        warm!.Duration.Median.Should().Be(200.0, "выборка смерженных фич не изменилась");
        reads.Should().Be(afterCold, "ревизия по фиче: активность живой фичи не читает чужие журналы");
    }

    // Ревизия фичи: активность в другом проекте (новые смерженные фичи с новыми UpdatedAt)
    // не сбрасывает кэш и не меняет числа.
    [Fact]
    public void Typical_АктивностьВЧужомПроектНеСбрасываетКэш()
    {
        FeatureTypical.ClearCache();
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
        reads.Should().Be(afterCold, "ревизия фич pA не изменилась — журнал не читается снова");
    }

    // Инвалидация: изменение задачи фичи (сдвиг мержа) → следующий Build даёт новые числа.
    // Мутация «убрать UpdatedAt из ревизии» (ревизия = только id фичи) → кэш не
    // сбрасывается, числа старые → тест красный.
    [Fact]
    public void Typical_ИзменениеЗадачиПроектаМеняетЧисла()
    {
        FeatureTypical.ClearCache();
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
        FeatureTypical.ClearCache();
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
        FeatureTypical.ClearCache();
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

    // П.2 хвостов ST-R-B1: семантика ожидания — правило таймлайна. Пересекающиеся эпизоды
    // двух задач одной фичи считаются один раз (15, не 20); подминутные эпизоды суммируются
    // до округления (2, не 0). Значения [15, 2, 30]: median 15, p25 8.5. Прежняя сумма
    // эпизодов без объединения давала [20, 0, 30] — median 20, p25 10 (тест красный).
    [Fact]
    public void Typical_ОжиданиеОбъединяетПересеченияИОкругляетОдинРаз()
    {
        FeatureTypical.ClearCache();
        var tasks = new List<TaskItem>();
        tasks.AddRange(MergedFeature("a", "t14", "pO", 100, 0));
        tasks.AddRange(MergedFeature("b", "t14", "pO", 200, 0));
        tasks.AddRange(MergedFeature("c", "t14", "pO", 300, 0));

        var journals = new Dictionary<string, FeatureJournalReadResult>
        {
            // Два перекрывающихся эпизода по 10 минут разных задач: union [10..25] = 15.
            ["a"] = JournalWithEpisodes("a", [("a-s1", 10.0, 20.0), ("a-s2", 15.0, 25.0)]),
            // Три эпизода по 40 секунд: сумма 120 с → 2 минуты, не 0 по floor-сумме.
            ["b"] = JournalWithEpisodes("b", [("b-s1", 10.0, 10 + 2.0 / 3), ("b-s2", 11.0, 11 + 2.0 / 3), ("b-s3", 12.0, 12 + 2.0 / 3)]),
            // Один эпизод 30 минут.
            ["c"] = JournalWithEpisodes("c", [("c-s1", 10.0, 40.0)]),
        };

        var stats = FeatureTypical.Build(tasks, "t14", "pO", "cur", id => journals[id], NOW);
        stats!.Waiting!.Median.Should().Be(15.0, "пересечения эпизодов объединяются, а не складываются");
        stats.Waiting!.P25.Should().Be(8.5, "подминутные эпизоды суммируются до округления");
    }

    // П.3 хвостов: наличие журнала — часть ключа. Выборка, посчитанная без журнала
    // (waiting = null), не обслуживает запрос с журналом, и наоборот.
    [Fact]
    public void Typical_КлючКэшаУчитываетДоступностьЖурнала()
    {
        FeatureTypical.ClearCache();
        var tasks = new List<TaskItem>();
        for (int i = 0; i < 3; i++)
            tasks.AddRange(MergedFeature($"f{i}", "t15", "pJ", 100 + 100 * i, 0));
        var journals = new Dictionary<string, FeatureJournalReadResult>
        {
            ["f0"] = JournalWithWaiting("f0", 10),
            ["f1"] = JournalWithWaiting("f1", 20),
            ["f2"] = JournalWithWaiting("f2", 30),
        };

        var without = FeatureTypical.Build(tasks, "t15", "pJ", "cur", null, NOW);
        without!.Waiting.Should().BeNull();

        var with = FeatureTypical.Build(tasks, "t15", "pJ", "cur", id => journals[id], NOW);
        with!.Waiting.Should().NotBeNull("кэш не отдаёт выборку без ожидания запросу с журналом");
        with!.Waiting!.Median.Should().Be(20.0);

        var againWithout = FeatureTypical.Build(tasks, "t15", "pJ", "cur", null, NOW);
        againWithout!.Waiting.Should().BeNull("и наоборот: выборка с ожиданием не отдаётся без журнала");
    }

    // П.3 хвостов: переполнение потолка ключей выселяет один наименее recently used ключ,
    // а не весь словарь: после чужого ключа тёплый запрос теряет одну выборку, не все.
    [Fact]
    public void Typical_ПереполнениеКэшаСноситОдинКлючНеВесь()
    {
        FeatureTypical.ClearCache();
        const int keys = 256; // MaxCachedKeys
        var tasks = new List<TaskItem>();
        for (int i = 0; i < keys; i++)
            tasks.AddRange(MergedFeature($"k{i}", "t16", "pM", 100 + i, 0));

        int reads = 0;
        Func<string, FeatureJournalReadResult?> read = id => { reads++; return null; };

        var cold = FeatureTypical.Build(tasks, "t16", "pM", "cur", read, NOW);
        cold!.Count.Should().Be(keys);
        int afterCold = reads; // 256

        // 257-й ключ: другая фича того же владельца (свой счётчик не ведём).
        var other = MergedFeature("z1", "t16", "pZ", 500, 0);
        FeatureTypical.Build(other, "t16", "pZ", "cur", _ => null, NOW);

        var warm = FeatureTypical.Build(tasks, "t16", "pM", "cur", read, NOW);
        warm!.Count.Should().Be(keys, "выборка не обнулена: выселен один ключ, не все");
        reads.Should().Be(afterCold + 1, "один выселенный ключ — одно перечитывание, не 256");
    }

    // П.4 хвостов: длительность фичи без CompletedAt у мерж-задачи считается от `now` —
    // такая выборка не кладётся в кэш и не застывает: второй запрос с другим `now` даёт
    // другие числа. Мутация «класть такую выборку в кэш» → числа идентичны → тест красный.
    [Fact]
    public void Typical_ДлительностьБезМержаНеЗастываетВКэше()
    {
        FeatureTypical.ClearCache();
        var tasks = new List<TaskItem>();
        for (int i = 0; i < 3; i++)
            tasks.AddRange(MergedFeature($"g{i}", "t17", "pN", 100 + 100 * i, 0));

        // Фича «h»: мерж Done, но без CompletedAt — конец длительности = now (п.4).
        tasks.Add(new TaskItem
        {
            Id = "h", Role = TaskRoles.Feature, OwnerId = "t17", ProjectId = "pN",
            Status = TaskItemStatus.Done, CreatedAt = T0, UpdatedAt = T0,
        });
        tasks.Add(new TaskItem
        {
            Id = "m-h", FeatureId = "h", Role = TaskRoles.Merge,
            Status = TaskItemStatus.Done, CreatedAt = T0, UpdatedAt = T0,
        });

        var first = FeatureTypical.Build(tasks, "t17", "pN", "cur", null, NOW);
        var second = FeatureTypical.Build(tasks, "t17", "pN", "cur", null, NOW + TimeSpan.FromHours(1));

        // Длительность «h» — от now: p75 [100,200,300,14400] = 3825 против
        // [100,200,300,14460] = 3840. В кэше застыло бы первое.
        first!.Duration.P75.Should().Be(3825.0);
        second!.Duration.P75.Should().Be(3840.0, "зависящая от now длительность пересчитывается, а не застывает");
    }

    // ─── B2a (разрез п.3): stepMinutes и tailMinutes в той же выборке и кэше ───────────────────

    // Медианы на фикстуре из трёх смерженных фич с шагами, числа посчитаны в тесте руками:
    // a: одна цепочка 10 мин → 10, хвост 20; b: цепочки 20 и 30 → среднее 25, хвост 30;
    // c: одна 50 → 50, хвост 40. Шаговая выборка [10,25,50]: median 25, p25 17.5, p75 37.5;
    // хвост [20,30,40]: median 30, p25 25, p75 35. Фича d смержена без шагов — в Count и в
    // длительности, в шаговую выборку не входит (StepCount = 3). Мутация «хвост от ПЕРВОЙ
    // AcceptedAt» → b даёт 70, выборка [20,70,40] → median 40 ≠ 30 → тест красный (проверено
    // прогоном); «брать все цепочки, не только принятые» на этой фикстуре не видна (все
    // цепочки приняты), её сторожит тест порога.
    [Fact]
    public void Typical_ШагиИХвостМедианыПоТойЖеВыборке()
    {
        FeatureTypical.ClearCache();
        var tasks = new List<TaskItem>();
        tasks.AddRange(MergedFeatureWithSteps("a", "t20", "pS", [10], 5, 20));
        tasks.AddRange(MergedFeatureWithSteps("b", "t20", "pS", [20, 30], 5, 30));
        tasks.AddRange(MergedFeatureWithSteps("c", "t20", "pS", [50], 5, 40));
        tasks.AddRange(MergedFeature("d", "t20", "pS", 100, 0));

        var stats = FeatureTypical.Build(tasks, "t20", "pS", "cur", null, NOW);

        stats.Should().NotBeNull();
        stats!.Count.Should().Be(4, "d смержена и входит в общую выборку");
        stats.StepCount.Should().Be(3, "фича без принятых шагов — вне шаговой выборки");
        stats.StepMinutes!.Median.Should().Be(25.0, "средние по фичам [10,25,50]");
        stats.StepMinutes!.P25.Should().Be(17.5);
        stats.StepMinutes!.P75.Should().Be(37.5);
        stats.TailMinutes!.Median.Should().Be(30.0, "хвосты [20,30,40]");
        stats.TailMinutes!.P25.Should().Be(25.0);
        stats.TailMinutes!.P75.Should().Be(35.0);
    }

    // Порог шаговой выборки (c3): три смерженные, но с шагами только две — step/tail null,
    // длительность считается (40/60/300 → медиана 60). Меньше трёх смерженных вообще —
    // null вся выборка, обе величины вместе с ней.
    [Fact]
    public void Typical_МалоФичСоШагамиОбеМетрикиNull()
    {
        FeatureTypical.ClearCache();
        var tasks = new List<TaskItem>();
        tasks.AddRange(MergedFeatureWithSteps("a", "t21", "pT", [10], 5, 20));
        tasks.AddRange(MergedFeatureWithSteps("b", "t21", "pT", [20], 5, 30));
        tasks.AddRange(MergedFeature("c", "t21", "pT", 300, 0));

        var stats = FeatureTypical.Build(tasks, "t21", "pT", "cur", null, NOW);

        stats.Should().NotBeNull("смерженных три — шкала длительности есть");
        stats!.StepCount.Should().Be(2);
        stats.StepMinutes.Should().BeNull("фич с принятыми шагами меньше трёх");
        stats.TailMinutes.Should().BeNull();
        stats.Duration.Median.Should().Be(60.0, "длительности [40,60,300] — медиана 60");

        var few = new List<TaskItem>();
        few.AddRange(MergedFeatureWithSteps("x", "t22", "pU", [10], 5, 20));
        few.AddRange(MergedFeatureWithSteps("y", "t22", "pU", [20], 5, 30));
        FeatureTypical.Build(few, "t22", "pU", "cur", null, NOW).Should().BeNull(
            "смерженных меньше трёх — null вся выборка, обе величины вместе с ней");
    }

    // c4: новых чтений журнала нет — ни в холодном, ни в тёплом запросе (по одному на фичу,
    // как до правки); шаг и хвост живут в том же кэше по фиче. Мутация «шаг считает хвост
    // своим чтением журнала» → счётчик 6 → красный; «не класть шаг/хвост в кэш» → тёплый
    // перечитывает → 6 → красный.
    [Fact]
    public void Typical_ШагиИХвостНеДобавляютЧтенийЖурнала()
    {
        FeatureTypical.ClearCache();
        var tasks = new List<TaskItem>();
        tasks.AddRange(MergedFeatureWithSteps("a", "t23", "pR", [10], 5, 20));
        tasks.AddRange(MergedFeatureWithSteps("b", "t23", "pR", [20, 30], 5, 30));
        tasks.AddRange(MergedFeatureWithSteps("c", "t23", "pR", [50], 5, 40));

        int reads = 0;
        Func<string, FeatureJournalReadResult?> read = id => { reads++; return JournalWithWaiting(id, 10); };

        var cold = FeatureTypical.Build(tasks, "t23", "pR", "cur", read, NOW);
        cold!.StepMinutes!.Median.Should().Be(25.0);
        cold!.TailMinutes!.Median.Should().Be(30.0);
        reads.Should().Be(3, "по одному чтению на смерженную фичу — как до правки");

        var warm = FeatureTypical.Build(tasks, "t23", "pR", "cur", read, NOW);
        warm!.StepMinutes!.Median.Should().Be(25.0, "шаговые числа живут в том же кэше по фиче");
        warm!.TailMinutes!.Median.Should().Be(30.0);
        reads.Should().Be(3, "тёплый запрос не читает журнал снова");
    }

    // П.5 хвостов: цена измеряется на ФАЙЛОВЫХ журналах (как в бою), а не на выборках в
    // памяти: 100 журналов по ~500 строк на диске. Холодный путь читает все файлы, тёплый —
    // ни одного (счётчик), и обязан быть быстрее. Числа пишутся в файл тестовой папки —
    // они идут в отчёт задачи; папка удаляется.
    [Fact]
    public void Typical_ЦенаХолодногоИТёплогоЗапросаНа100ФичахСФайловымиЖурналами()
    {
        FeatureTypical.ClearCache();
        var dir = Path.Combine(Path.GetTempPath(), "typical-bench-" + Guid.NewGuid().ToString("N"));
        var store = new FeatureJournalStore(dir);
        try
        {
            var tasks = new List<TaskItem>();
            for (int i = 0; i < 100; i++)
            {
                var id = $"f{i}";
                tasks.AddRange(MergedFeature(id, "t12", "pB", 100 + i, i % 5));
                // Журнал ~500 строк: created + ходы + эпизод ожидания.
                store.Append("t12", id, new FeatureJournalEntry { At = T0, Kind = "created", TaskId = id });
                for (int j = 0; j < 500; j++)
                    store.Append("t12", id, new FeatureJournalEntry
                    {
                        At = T0 + TimeSpan.FromMinutes(j), Kind = "turn", TaskId = id,
                        SessionId = "s", TurnSeq = j,
                        StartedAt = T0 + TimeSpan.FromMinutes(j),
                        EndedAt = T0 + TimeSpan.FromMinutes(j + 1),
                        Outcome = "finished",
                    });
                store.Append("t12", id, new FeatureJournalEntry
                { At = T0 + TimeSpan.FromMinutes(10), Kind = "waiting", TaskId = id });
                store.Append("t12", id, new FeatureJournalEntry
                { At = T0 + TimeSpan.FromMinutes(10 + 10 + i), Kind = "waiting_cleared", TaskId = id });
            }

            int reads = 0;
            Func<string, FeatureJournalReadResult?> read = id => { reads++; return store.Read("t12", id); };

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
            reads.Should().Be(100, "тёплый запрос не читает ни одного файла журнала с диска");
            warmTicks.Should().BeLessThan(coldTicks,
                "тёплый путь: ни FeatureProjector.Build, ни чтений журнала с диска");

            double ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
            Console.WriteLine($"typical-bench: cold={ms(coldTicks):F1} ms, warm={ms(warmTicks):F3} ms, " +
                $"features=100, journalLines=502");
            File.WriteAllText(Path.Combine(dir, "typical-bench.txt"),
                $"cold={ms(coldTicks):F1} ms, warm={ms(warmTicks):F3} ms, features=100, journalLines=502");
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* временная папка — в порядке исключения */ }
        }
    }
}
