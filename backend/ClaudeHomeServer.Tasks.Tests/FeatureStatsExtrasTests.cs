using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Tasks.Projector;

namespace ClaudeHomeServer.Tasks.Tests;

// FS-M56 (величины 5 и 6 списка «нужно от бэка»): stats.stepMedianMinutes — медиана
// DurationMinutes по цепочкам ЭТОЙ фичи в корзине «принят»; stats.spend.cacheShare — доля
// прочитанного кэша во входных токенах записей фичи. Эталон каждого теста считан руками из
// сценария, а не из вывода реализации.
public class FeatureStatsExtrasTests
{
    private const string Epic = "ep";
    private static readonly DateTime T0 = new(2026, 11, 10, 10, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Now = T0.AddDays(1);
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static TaskItem Feature() => new() { Id = Epic, Role = TaskRoles.Feature, CreatedAt = T0 };

    // Задача-шаг длительностью [start..end] минут от T0: цепочка без доработок состоит только
    // из неё, поэтому DurationMinutes = end − start (FeatureStepAccounting.cs:380–399).
    private static TaskItem Step(string id, string stepRef, int start, int end,
        TaskItemStatus status = TaskItemStatus.Done) => new()
    {
        Id = id, FeatureId = Epic, Role = TaskRoles.Step, StepRef = stepRef, Status = status,
        Lane = "back", CreatedAt = T0.AddMinutes(start),
        CompletedAt = status == TaskItemStatus.Done ? T0.AddMinutes(end) : null,
    };

    // Закрытый виток-финал с вердиктом по шагу (ReviewOf — id задачи-шага).
    private static TaskItem Verdict(string id, string verdict, int minute, string reviewOf) => new()
    {
        Id = id, FeatureId = Epic, Role = TaskRoles.Final, Status = TaskItemStatus.Done,
        ReviewOf = [reviewOf], CreatedAt = T0.AddMinutes(minute), CompletedAt = T0.AddMinutes(minute + 1),
        Verification = new TaskVerification { Verdict = verdict },
    };

    private static FeatureRun Run(params TaskItem[] tasks) =>
        FeatureProjector.Build(tasks.First(t => t.Role == TaskRoles.Feature), tasks, Now);

    // ─── 5: медиана шага этой фичи ────────────────────────────────────────────────────────────

    // К1: принятые цепочки [10, 30, 50] и одна в работе с 999 → медиана 30 (идущая вне выборки).
    [Fact]
    public void МедианаШага_Принятые10_30_50_Идущий999_Медиана30()
    {
        var run = Run(
            Feature(),
            Step("s1", "A", 0, 10), Verdict("f1", TaskVerdicts.Accepted, 11, "s1"),
            Step("s2", "B", 20, 50), Verdict("f2", TaskVerdicts.Accepted, 51, "s2"),
            Step("s3", "C", 60, 110), Verdict("f3", TaskVerdicts.Accepted, 111, "s3"),
            Step("s4", "D", 120, 1119, TaskItemStatus.InProgress));

        FeatureStats.StepMedianOf(run).Should().Be(30.0, "идущая цепочка (999) в медиану не входит");
    }

    // Принятые цепочки [10, 20, 30, 40] → та же функция квантилей, что у stats.typical
    // (FeatureTypical.Pct, тип 7): среднее двух средних = 25, а не нижнее из них.
    [Fact]
    public void МедианаШага_ЧётнаяВыборка_СреднееДвухСредних()
    {
        var run = Run(
            Feature(),
            Step("s1", "A", 0, 10), Verdict("f1", TaskVerdicts.Accepted, 11, "s1"),
            Step("s2", "B", 20, 40), Verdict("f2", TaskVerdicts.Accepted, 41, "s2"),
            Step("s3", "C", 50, 80), Verdict("f3", TaskVerdicts.Accepted, 81, "s3"),
            Step("s4", "D", 90, 130), Verdict("f4", TaskVerdicts.Accepted, 131, "s4"));

        FeatureStats.StepMedianOf(run).Should().Be(25.0);
    }

    // Одна принятая цепочка — её длительность (медиана одного значения).
    [Fact]
    public void МедианаШага_ОдинПринятый_ЕгоДлительность()
    {
        var run = Run(Feature(), Step("s1", "A", 0, 42), Verdict("f1", TaskVerdicts.Accepted, 43, "s1"));

        FeatureStats.StepMedianOf(run).Should().Be(42.0);
    }

    // Вне выборки — всё, что не корзина «принят»: снятый (withdrawn), идущий (work),
    // вернувшийся (returned). Принятые [10, 30] → медиана 20, чужие длительности её не сдвигают.
    [Fact]
    public void МедианаШага_ИсключаетНеПринятыеКорзины()
    {
        var run = Run(
            Feature(),
            Step("s1", "A", 0, 10), Verdict("f1", TaskVerdicts.Accepted, 11, "s1"),
            Step("s2", "B", 20, 120), Verdict("f2", TaskVerdicts.Withdrawn, 121, "s2"),
            Step("s3", "C", 130, 630, TaskItemStatus.InProgress),
            Step("s4", "D", 640, 1140), Verdict("f4", TaskVerdicts.Returned, 1141, "s4"),
            Step("s5", "E", 1150, 1180), Verdict("f5", TaskVerdicts.Accepted, 1181, "s5"));

        FeatureStats.StepMedianOf(run).Should().Be(20.0,
            "в выборке только принятые [10,30]; снятый/идущий/вернувшийся — вне");
    }

    // Ни одной принятой цепочки (есть только идущая) → null, а не 0.
    [Fact]
    public void МедианаШага_НетПринятых_Null()
    {
        var run = Run(Feature(), Step("s1", "A", 0, 10, TaskItemStatus.InProgress));

        FeatureStats.StepMedianOf(run).Should().BeNull();
    }

    // Пустой run (только эпик) → null.
    [Fact]
    public void МедианаШага_ПустойRun_Null()
    {
        var run = Run(Feature());

        FeatureStats.StepMedianOf(run).Should().BeNull();
    }

    // ─── 6: доля кэша ─────────────────────────────────────────────────────────────────────────

    // К2: 300 из 500 входных токенов (input 100 + cacheRead 300 + cacheCreation 100) → ровно 0.6,
    // без округления: знаменатель — ВСЕ входные токены.
    [Fact]
    public void ДоляКэша_ЗнаменательВсеВходныеТокены_Ровно0_6()
    {
        var spend = NewSpend(input: 100, cacheRead: 300, cacheCreation: 100);

        spend.CacheShare.Should().Be(0.6);
    }

    // Весь вход — кэш → ровно 1.0 (граница диапазона 0…1).
    [Fact]
    public void ДоляКэша_ВесьВходКэш_Один()
    {
        var spend = NewSpend(input: 0, cacheRead: 100, cacheCreation: 0);

        spend.CacheShare.Should().Be(1.0);
    }

    // Кэша нет → 0.0 (не null: знаменатель ненулевой).
    [Fact]
    public void ДоляКэша_КэшаНет_Ноль()
    {
        var spend = NewSpend(input: 100, cacheRead: 0, cacheCreation: 20);

        spend.CacheShare.Should().Be(0.0);
    }

    // Входных токенов нет (пустой расход) → null, а не деление на ноль.
    [Fact]
    public void ДоляКэша_НетВходныхТокенов_Null()
    {
        var spend = NewSpend(input: 0, cacheRead: 0, cacheCreation: 0);

        spend.CacheShare.Should().BeNull();
    }

    // ─── Контракт JSON: новые ключи аддитивны, старые не меняются ────────────────────────────

    [Fact]
    public void Json_НовыеКлючиStepMedianИCacheShare_СтарыеНеТронуты()
    {
        var run = Run(Feature(), Step("s1", "A", 0, 10), Verdict("f1", TaskVerdicts.Accepted, 11, "s1"));
        var timeline = FeatureTimeline.Build(run, null, Now);
        var stats = new FeatureStats(
            Source: "approx", Truncated: false, Timeline: timeline,
            Spend: NewSpend(input: 100, cacheRead: 300, cacheCreation: 0),
            StepMedianMinutes: FeatureStats.StepMedianOf(run));

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(stats, Web));
        doc.RootElement.GetProperty("stepMedianMinutes").GetDouble().Should().Be(10.0);
        doc.RootElement.GetProperty("spend").GetProperty("cacheShare").GetDouble().Should().Be(0.75);
        // Старые ключи на месте и с прежними значениями.
        doc.RootElement.GetProperty("source").GetString().Should().Be("approx");
        doc.RootElement.GetProperty("truncated").GetBoolean().Should().BeFalse();
        doc.RootElement.GetProperty("spend").GetProperty("inputTokens").GetInt64().Should().Be(100);
        doc.RootElement.GetProperty("spend").GetProperty("cacheReadTokens").GetInt64().Should().Be(300);
    }

    // null-значение тоже выходит ключом (фронт различает «нет данных» и отсутствие поля).
    [Fact]
    public void Json_CacheShareNull_ВыходитКлючомNull()
    {
        var stats = new FeatureStats(
            Source: "approx", Truncated: false,
            Timeline: FeatureTimeline.Build(Run(Feature()), null, Now),
            Spend: NewSpend(input: 0, cacheRead: 0, cacheCreation: 0));

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(stats, Web));
        doc.RootElement.GetProperty("spend").GetProperty("cacheShare").ValueKind
            .Should().Be(JsonValueKind.Null);
        // StepMedianMinutes не передан (default null) — тоже ключ с null.
        doc.RootElement.GetProperty("stepMedianMinutes").ValueKind.Should().Be(JsonValueKind.Null);
    }

    private static FeatureSpend NewSpend(long input, long cacheRead, long cacheCreation) => new(
        InputTokens: input, OutputTokens: 0, CacheReadTokens: cacheRead, CacheCreationTokens: cacheCreation,
        CostUsd: 0.0, CostPartial: false, HiddenCostModels: [], From: "2026-11-10");
}
