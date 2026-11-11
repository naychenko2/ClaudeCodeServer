using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Tasks.Journal;
using ClaudeHomeServer.Services.Tasks.Projector;

namespace ClaudeHomeServer.Tasks.Tests;

// FS-M478 (величины 4, 7, 8 §6): stats.estimateDrift — точность срока закрытой фичи, сдвиг
// срока с начала, смены знаменателя по журналу estimate_set. Эталон каждого теста считан
// руками из сценария, а не из вывода реализации.
public class FeatureEstimateDriftTests
{
    private static readonly DateTime T0 = new(2026, 11, 10, 10, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Now = T0.AddDays(1);
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    // Открытый эпик с базовой оценкой срока (InitialEtaMinutes — то же поле, что пишет TaskManager).
    private static TaskItem Feature(int? initialEta = null) => new()
    {
        Id = "ep", Role = TaskRoles.Feature, CreatedAt = T0, InitialEtaMinutes = initialEta,
    };

    // Закрытая фича: эпик Done в T0+900 → Summary.DurationMinutes = 900
    // (FeatureProjector.cs:585–591: от старта первой задачи до закрытия).
    private static TaskItem ClosedFeature(int? initialEta = null) => new()
    {
        Id = "ep", Role = TaskRoles.Feature, CreatedAt = T0, Status = TaskItemStatus.Done,
        CompletedAt = T0.AddMinutes(900), InitialEtaMinutes = initialEta,
    };

    private static FeatureRun Run(params TaskItem[] tasks) =>
        FeatureProjector.Build(tasks.First(t => t.Role == TaskRoles.Feature), tasks, Now);

    private static FeatureJournalReadResult Journal(params FeatureJournalEntry[] entries) => new()
    {
        StartsWithCreated = true,
        Truncated = false,
        Entries = entries.ToList(),
    };

    // Строка estimate_set (FS-O2a/FS-O2b): числа оценки в EtaMinutes/Total.
    private static FeatureJournalEntry Estimate(int atMinute, int? eta, int? total) => new()
    {
        At = T0.AddMinutes(atMinute), Kind = "estimate_set", EtaMinutes = eta, Total = total,
    };

    // ─── 7: сдвиг срока с начала ──────────────────────────────────────────────────────────────

    // К1: три строки (eta 600/720/900, total 10/10/13) → first=600, current=900, shift=300,
    // denominatorChanges=1; Initial — базовая оценка эпика.
    [Fact]
    public void Сдвиг_ТриСтроки600_720_900_Сдвиг300_СменаЗнаменателя1()
    {
        var run = Run(Feature(initialEta: 600));
        var journal = Journal(
            Estimate(10, 600, 10), Estimate(20, 720, 10), Estimate(30, 900, 13));

        var drift = FeatureEstimateDrift.Build(run, journal);

        drift.InitialEtaMinutes.Should().Be(600);
        drift.FirstEtaMinutes.Should().Be(600);
        drift.CurrentEtaMinutes.Should().Be(900);
        drift.ShiftMinutes.Should().Be(300);
        drift.DenominatorChanges.Should().Be(1);
    }

    // Одна строка с оценкой → first == current, но shift = null (меньше двух строк).
    [Fact]
    public void Сдвиг_ОднаСтрока_Null()
    {
        var run = Run(Feature());
        var journal = Journal(Estimate(10, 600, 10));

        var drift = FeatureEstimateDrift.Build(run, journal);

        drift.FirstEtaMinutes.Should().Be(600);
        drift.CurrentEtaMinutes.Should().Be(600);
        drift.ShiftMinutes.Should().BeNull();
    }

    // Журнала нет → first/current/shift = null, denominatorChanges = 0; Initial — из эпика.
    [Fact]
    public void Сдвиг_ЖурналаНет_NullИНульСмен_НачальнаяИзЭпика()
    {
        var run = Run(Feature(initialEta: 600));

        var drift = FeatureEstimateDrift.Build(run, null);

        drift.InitialEtaMinutes.Should().Be(600);
        drift.FirstEtaMinutes.Should().BeNull();
        drift.CurrentEtaMinutes.Should().BeNull();
        drift.ShiftMinutes.Should().BeNull();
        drift.DenominatorChanges.Should().Be(0);
    }

    // Строки без etaMinutes в выборе срока не участвуют: first/current — по строкам с оценкой.
    [Fact]
    public void Сдвиг_СтрокиБезОценки_Пропускаются()
    {
        var run = Run(Feature());
        var journal = Journal(
            Estimate(10, null, 10), Estimate(20, 600, null), Estimate(30, null, 13), Estimate(40, 900, 13));

        var drift = FeatureEstimateDrift.Build(run, journal);

        drift.FirstEtaMinutes.Should().Be(600);
        drift.CurrentEtaMinutes.Should().Be(900);
        drift.ShiftMinutes.Should().Be(300);
    }

    // Порядок — по At (контракт): строки даны перемешанно → first/current по времени, не по позиции.
    [Fact]
    public void Сдвиг_ПорядокПоAt_АНеПоПозицииВМассиве()
    {
        var run = Run(Feature());
        var journal = Journal(
            Estimate(40, 900, 13), Estimate(10, 600, 10), Estimate(20, 720, 10));

        var drift = FeatureEstimateDrift.Build(run, journal);

        drift.FirstEtaMinutes.Should().Be(600);
        drift.CurrentEtaMinutes.Should().Be(900);
        drift.ShiftMinutes.Should().Be(300);
    }

    // ─── 8: смены знаменателя ─────────────────────────────────────────────────────────────────

    // К2: total 10/null/10 → 0: строка без total выпадает из последовательности,
    // разрыв через неё сменой не считается.
    [Fact]
    public void Знаменатель_10_Null_10_НольСмен()
    {
        var run = Run(Feature());
        var journal = Journal(Estimate(10, null, 10), Estimate(20, null, null), Estimate(30, null, 10));

        FeatureEstimateDrift.Build(run, journal).DenominatorChanges.Should().Be(0);
    }

    // Соседние одинаковые total — не смена; 10/10/13/13/20 → 2 смены.
    [Fact]
    public void Знаменатель_ПовторыНеСмены_ДвеСмены()
    {
        var run = Run(Feature());
        var journal = Journal(
            Estimate(10, null, 10), Estimate(20, null, 10), Estimate(30, null, 13),
            Estimate(40, null, 13), Estimate(50, null, 20));

        FeatureEstimateDrift.Build(run, journal).DenominatorChanges.Should().Be(2);
    }

    // Строки других kind (turn/stopped) в знаменателе не участвуют.
    [Fact]
    public void Знаменатель_ЧужиеKindНеУчитываются()
    {
        var run = Run(Feature());
        var journal = Journal(
            Estimate(10, null, 10),
            new FeatureJournalEntry { At = T0.AddMinutes(15), Kind = "turn", Total = 99 },
            Estimate(20, null, 13));

        FeatureEstimateDrift.Build(run, journal).DenominatorChanges.Should().Be(1);
    }

    // ─── 4: точность срока закрытой ──────────────────────────────────────────────────────────

    // К3: закрытая фича (DurationMinutes=900), базовая оценка 600 → accuracy = 1.5.
    [Fact]
    public void Точность_Закрытая900_База600_Полтора()
    {
        var run = Run(ClosedFeature(initialEta: 600));
        run.Summary.Should().NotBeNull("закрытая фича даёт Summary — условие accuracy");

        FeatureEstimateDrift.Build(run, null).Accuracy.Should().Be(1.5);
    }

    // Открытая фича (Summary = null) → accuracy = null, даже с базовой оценкой.
    [Fact]
    public void Точность_Открытая_Null()
    {
        var run = Run(Feature(initialEta: 600));

        FeatureEstimateDrift.Build(run, null).Accuracy.Should().BeNull();
    }

    // Закрытая, но базовой оценки нет (или она нулевая — делить не на что) → null.
    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    public void Точность_БазовойОценкиНет_Null(int? initialEta)
    {
        var run = Run(ClosedFeature(initialEta: initialEta));

        FeatureEstimateDrift.Build(run, null).Accuracy.Should().BeNull();
    }

    // Точность считается и при отсутствии журнала (величина 4 — из Summary, не из журнала).
    [Fact]
    public void Точность_ЖурналаНет_ВсёРавноСчитается()
    {
        var run = Run(ClosedFeature(initialEta: 300));
        run.Summary!.DurationMinutes.Should().Be(900.0);

        FeatureEstimateDrift.Build(run, null).Accuracy.Should().Be(3.0);
    }

    // ─── Контракт JSON: новый ключ аддитивен, старые не меняются ──────────────────────────────

    [Fact]
    public void Json_НовыйКлючEstimateDrift_СтарыеНеТронуты()
    {
        var run = Run(ClosedFeature(initialEta: 600));
        var journal = Journal(Estimate(10, 600, 10), Estimate(20, 900, 13));
        var stats = new FeatureStats(
            Source: "journal", Truncated: false,
            Timeline: FeatureTimeline.Build(run, journal, Now),
            EstimateDrift: FeatureEstimateDrift.Build(run, journal));

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(stats, Web));
        var drift = doc.RootElement.GetProperty("estimateDrift");
        drift.GetProperty("initialEtaMinutes").GetInt32().Should().Be(600);
        drift.GetProperty("firstEtaMinutes").GetInt32().Should().Be(600);
        drift.GetProperty("currentEtaMinutes").GetInt32().Should().Be(900);
        drift.GetProperty("shiftMinutes").GetInt32().Should().Be(300);
        drift.GetProperty("denominatorChanges").GetInt32().Should().Be(1);
        drift.GetProperty("accuracy").GetDouble().Should().Be(1.5);
        // Старые ключи на месте.
        doc.RootElement.GetProperty("source").GetString().Should().Be("journal");
        doc.RootElement.GetProperty("truncated").GetBoolean().Should().BeFalse();
    }

    // Параметр не передан (default null) — ключ с null (фронт различает «нет данных»).
    [Fact]
    public void Json_EstimateDriftНеПередан_ВыходитКлючомNull()
    {
        var stats = new FeatureStats(
            Source: "approx", Truncated: false,
            Timeline: FeatureTimeline.Build(Run(Feature()), null, Now));

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(stats, Web));
        doc.RootElement.GetProperty("estimateDrift").ValueKind.Should().Be(JsonValueKind.Null);
    }
}
