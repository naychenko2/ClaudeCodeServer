using ClaudeHomeServer.Services.Llm;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services.LocalStats;

/// <summary>
/// Тесты стабильной базы шага декода (cleanStepMs, ADR-029, v2.5): база считается бэкендом
/// по кольцу точек за час и НЕ зависит от окна запроса (раньше базу выбирал фронт из
/// интервалов своего окна, и полоса «Куда уходит время агентов» прыгала при сдвиге окна).
/// Проверяются: 10-й перцентиль по живой фикстуре (≈58 мс), fallback при малой выборке,
/// null без данных, гистерезис (не дёргаем базу при отличии < 10 %) и публикация базы
/// поллером (Current.CleanStepMs) — одна и та же для всех окон.
/// </summary>
public class LocalModelStatsCleanStepTests
{
    // Живая фикстура vLLM (72 замера по 5 с, 71 дельта itl). Данные из .cc-attachments
    // не коммитятся (папка вне git), поэтому пары (ΔitlSeconds, ΔitlCount) вшиты здесь.
    // В фикстуре есть «прыжки» (долгие шаги: 400–1000 мс — это «ждёт соседа»/prefill),
    // база обязана остаться на 10-м перцентиле, а не на среднем.
    private static readonly (double Seconds, long Count)[] Live =
    [
        (14, 78), (13, 190), (11, 68), (14, 107), (16, 218), (20, 233), (18, 256), (18, 241),
        (17, 223), (19, 240), (19, 257), (16, 111), (17, 226), (20, 272), (19, 224), (15, 270),
        (13, 168), (11, 244), (10, 195), (15, 245), (13, 180), (13, 208), (12, 180), (16, 257),
        (13, 195), (11, 157), (8, 66), (12, 168), (12, 146), (12, 170), (13, 166), (14, 201),
        (15, 261), (12, 208), (13, 138), (10, 24), (10, 22), (10, 20), (10, 20), (10, 18),
        (10, 18), (10, 16), (11, 16), (9, 14), (11, 16), (10, 14), (9, 12), (11, 14), (9, 12),
        (7, 8), (8, 8), (10, 12), (9, 10), (10, 10), (9, 26), (14, 172), (13, 198), (10, 125),
        (12, 114), (10, 96), (13, 130), (10, 155), (12, 224), (14, 241), (8, 171), (13, 165),
        (9, 146), (12, 129), (14, 218), (9, 110), (8, 164),
    ];

    private static List<(double? Seconds, long? Count)> AsSteps((double Seconds, long Count)[] pairs) =>
        pairs.Select(p => ((double?)p.Seconds, (long?)p.Count)).ToList();

    // База по числу потоков (батч-движок): уровни с достаточной выборкой получают свой
    // перцентиль, скудные — не публикуются; точки без running и с малыми дельтами не входят.
    [Fact]
    public void ComputeBaseByRunning_PerLevelPercentile_SkipsThinLevels()
    {
        var steps = new List<(double? Seconds, long? Count, int? Running)>();
        for (var i = 0; i < 12; i++) steps.Add((0.8 + i * 0.01, 100, 1));   // 1 поток: ~8 мс/шаг
        for (var i = 0; i < 12; i++) steps.Add((2.5 + i * 0.05, 100, 3));   // 3 потока: ~25 мс/шаг
        for (var i = 0; i < 4; i++) steps.Add((4.0, 100, 6));                // 6 потоков: мало точек
        steps.Add((9.0, 100, null));                                          // без running — не входит
        steps.Add((0.01, 2, 1));                                              // меньше MinItlCount — не входит

        var by = LocalModelStatsCleanStep.ComputeBaseByRunning(steps);

        by.Should().ContainKey(1).And.ContainKey(3).And.NotContainKey(6);
        by[1].Should().BeApproximately(8.1, 0.01, "10-й перцентиль уровня 1");
        by[3].Should().BeApproximately(25.5, 0.01, "10-й перцентиль уровня 3");
    }

    // Гистерезис по уровням: малое отличие не двигает базу уровня, исчезнувший уровень снимается.
    [Fact]
    public void ApplyHysteresisByRunning_PerLevel()
    {
        var current = new Dictionary<int, double> { [1] = 8.0, [3] = 25.0 };
        var candidate = new Dictionary<int, double> { [1] = 8.5, [2] = 15.0 };

        var next = LocalModelStatsCleanStep.ApplyHysteresisByRunning(current, candidate);

        next[1].Should().Be(8.0, "отличие < 10 % — базу уровня не трогаем");
        next[2].Should().Be(15.0, "новый уровень публикуется сразу");
        next.Should().NotContainKey(3, "уровень без кандидата снимается");
    }

    // (1) Живая фикстура: база = 10-й перцентиль шагов (≈58 мс), а не среднее по «прыжкам».
    // Мутация: заменить перцентиль на среднее — тест краснеет (среднее ≈ 190 мс).
    [Fact]
    public void ComputeBase_LiveFixture_IsTenthPercentile()
    {
        var baseMs = LocalModelStatsCleanStep.ComputeBase(AsSteps(Live));

        baseMs.Should().NotBeNull();
        baseMs!.Value.Should().BeApproximately(57.69, 0.01, "10-й перцентиль шагов по живой фикстуре");
        baseMs.Value.Should().BeLessThan(100, "база не должна тянуться к длинным шагам («ждёт соседа»)");
    }

    // (2) Малый объём выборки: валидных точек меньше десяти — перцентиль шумный,
    // берём накопленное по кольцу (ΣitlSeconds/ΣitlCnt).
    [Fact]
    public void ComputeBase_FewPoints_FallsBackToAccumulated()
    {
        // 5 точек по 1 с / 20 шагов = 50 мс на шаг.
        var steps = new List<(double? Seconds, long? Count)>();
        for (var i = 0; i < 5; i++) steps.Add((1.0, 20L));

        var baseMs = LocalModelStatsCleanStep.ComputeBase(steps);

        baseMs.Should().BeApproximately(50.0, 0.01, "перцентиль по 5 точкам шумный — накопленное по кольцу");
    }

    // (2a) Точки с ItlCount < 3 в выборку перцентиля не входят (дельта слишком грубая),
    // но в накопленное суммируются.
    [Fact]
    public void ComputeBase_SmallCounts_ExcludedFromPercentile()
    {
        // 9 точек по 1 шагу (невалидны для перцентиля) + 1 точка с 20 шагами = 50 мс.
        var steps = new List<(double? Seconds, long? Count)>();
        for (var i = 0; i < 9; i++) steps.Add((0.1, 1L));
        steps.Add((1.0, 20L));

        var baseMs = LocalModelStatsCleanStep.ComputeBase(steps);

        // Перцентиль не считается (1 валидная точка) → накопленное по кольцу:
        // (9×0.1 с + 1.0 с) / (9×1 + 20) = 65.52 мс.
        baseMs.Should().BeApproximately(65.52, 0.01);
    }

    // (3) Данных нет — null (и никакой выдуманной базы).
    [Fact]
    public void ComputeBase_NoData_IsNull()
    {
        LocalModelStatsCleanStep.ComputeBase([]).Should().BeNull();
        LocalModelStatsCleanStep.ComputeBase([(null, null)]).Should().BeNull();
        LocalModelStatsCleanStep.ComputeBase([(1.0, 0L)]).Should().BeNull();
    }

    // (4) Гистерезис: отличие меньше 10 % — базу не трогаем; 10 % и больше — обновляем.
    [Fact]
    public void ApplyHysteresis_SmallChange_KeepsValue()
    {
        LocalModelStatsCleanStep.ApplyHysteresis(100.0, 105.0).Should().Be(100.0, "отличие 5 % — база не дёргается");
        LocalModelStatsCleanStep.ApplyHysteresis(100.0, 91.0).Should().Be(100.0, "отличие 9 % — меньше порога 10 %");
        LocalModelStatsCleanStep.ApplyHysteresis(100.0, 90.0).Should().Be(90.0, "отличие ровно 10 % — порог достигнут, база обновляется");
    }

    [Fact]
    public void ApplyHysteresis_LargeChange_UpdatesValue()
    {
        LocalModelStatsCleanStep.ApplyHysteresis(100.0, 120.0).Should().Be(120.0, "отличие 20 % — база новая");
        LocalModelStatsCleanStep.ApplyHysteresis(100.0, 80.0).Should().Be(80.0);
    }

    // (4a) Первой публикации нет — берём новое значение; данных нет — публикуем null.
    [Fact]
    public void ApplyHysteresis_NoCurrentOrNoCandidate()
    {
        LocalModelStatsCleanStep.ApplyHysteresis(null, 42.0).Should().Be(42.0);
        LocalModelStatsCleanStep.ApplyHysteresis(42.0, null).Should().BeNull("данных нет — базы тоже нет");
    }

    // (5) Перцентиль по конвенции фронта: индекс floor(0.1*n), ограничен последним.
    [Fact]
    public void Percentile10_UsesFloorIndex()
    {
        var values = Enumerable.Range(1, 10).Select(i => (double)i).ToList(); // 1..10

        // floor(0.1*10) = 1 → второй по величине.
        LocalModelStatsCleanStep.Percentile10(values).Should().Be(2.0);
        LocalModelStatsCleanStep.Percentile10([5.0]).Should().Be(5.0);
    }
}
