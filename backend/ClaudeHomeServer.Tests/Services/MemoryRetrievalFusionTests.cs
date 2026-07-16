using ClaudeHomeServer.Services.Memory;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Чистое слияние сигналов retrieval (гибрид #3): взвешенная сумма нормализованных сигналов
// (обе стороны / только один / нормализация по максимуму / веса из опций) и RRF-вариант.
public class MemoryRetrievalFusionTests
{
    private static readonly MemoryFusionOptions Default = MemoryFusionOptions.Default;

    [Fact]
    public void ВзвешеннаяСумма_ОбеСтороны_НормализуетПоМаксимуму()
    {
        // maxSem = 0.8, maxKw = 1.0; веса 0.7 / 0.3
        var semantic = new Dictionary<string, double> { ["a"] = 0.8, ["b"] = 0.4 };
        var keyword = new Dictionary<string, double> { ["a"] = 0.5, ["b"] = 1.0 };

        var fused = MemoryRetrievalFusion.Fuse(semantic, keyword, Default);

        // a: 0.7·(0.8/0.8) + 0.3·(0.5/1.0) = 0.85
        // b: 0.7·(0.4/0.8) + 0.3·(1.0/1.0) = 0.65
        fused["a"].Should().BeApproximately(0.85, 1e-9);
        fused["b"].Should().BeApproximately(0.65, 1e-9);
    }

    [Fact]
    public void ВзвешеннаяСумма_ТолькоSemantic_KeywordВкладаНеДаёт()
    {
        var semantic = new Dictionary<string, double> { ["a"] = 0.6, ["b"] = 0.3 };
        var keyword = new Dictionary<string, double>();

        var fused = MemoryRetrievalFusion.Fuse(semantic, keyword, Default);

        // keyword пуст → вклад 0; a нормализуется в 1.0
        fused["a"].Should().BeApproximately(0.7, 1e-9);   // 0.7·(0.6/0.6)
        fused["b"].Should().BeApproximately(0.35, 1e-9);  // 0.7·(0.3/0.6)
    }

    [Fact]
    public void ВзвешеннаяСумма_ТолькоKeyword_SemanticВкладаНеДаёт()
    {
        var semantic = new Dictionary<string, double>();
        var keyword = new Dictionary<string, double> { ["a"] = 0.4, ["b"] = 0.2 };

        var fused = MemoryRetrievalFusion.Fuse(semantic, keyword, Default);

        fused["a"].Should().BeApproximately(0.3, 1e-9);   // 0.3·(0.4/0.4)
        fused["b"].Should().BeApproximately(0.15, 1e-9);  // 0.3·(0.2/0.4)
    }

    [Fact]
    public void ВзвешеннаяСумма_ЗаписьТолькоВОдномСигнале_ПоВторому_Ноль()
    {
        var semantic = new Dictionary<string, double> { ["a"] = 1.0 };
        var keyword = new Dictionary<string, double> { ["b"] = 1.0 };

        var fused = MemoryRetrievalFusion.Fuse(semantic, keyword, Default);

        fused["a"].Should().BeApproximately(0.7, 1e-9);  // только semantic
        fused["b"].Should().BeApproximately(0.3, 1e-9);  // только keyword
    }

    [Fact]
    public void ВзвешеннаяСумма_НормализацияПоМаксимуму_МалыеScoreНеЗанижаютТоп()
    {
        // Оба semantic-score малы (0.2, 0.1), но топ всё равно получает полный вес после нормализации
        var semantic = new Dictionary<string, double> { ["a"] = 0.2, ["b"] = 0.1 };
        var keyword = new Dictionary<string, double>();

        var fused = MemoryRetrievalFusion.Fuse(semantic, keyword, Default);

        fused["a"].Should().BeApproximately(0.7, 1e-9);   // 0.2 → норм 1.0 → 0.7
        fused["b"].Should().BeApproximately(0.35, 1e-9);  // 0.1 → норм 0.5 → 0.35
    }

    [Fact]
    public void ВзвешеннаяСумма_ВесаБерутсяИзОпций()
    {
        // b есть только в semantic → его relevance = SemanticWeight (норм 1.0 по semantic, 0 по keyword)
        var semantic = new Dictionary<string, double> { ["a"] = 1.0, ["b"] = 1.0 };
        var keyword = new Dictionary<string, double> { ["a"] = 1.0 };

        var custom = Default with { SemanticWeight = 0.6, KeywordWeight = 0.4 };

        MemoryRetrievalFusion.Fuse(semantic, keyword, Default)["b"].Should().BeApproximately(0.7, 1e-9);
        MemoryRetrievalFusion.Fuse(semantic, keyword, custom)["b"].Should().BeApproximately(0.6, 1e-9);
    }

    [Fact]
    public void ВзвешеннаяСумма_ОбаСигналаПусты_ПустойРезультат()
    {
        var fused = MemoryRetrievalFusion.Fuse(
            new Dictionary<string, double>(), new Dictionary<string, double>(), Default);

        fused.Should().BeEmpty();
    }

    [Fact]
    public void ВзвешеннаяСумма_РезультатВШкале_0_1()
    {
        var semantic = new Dictionary<string, double> { ["a"] = 1.0, ["b"] = 0.5, ["c"] = 0.1 };
        var keyword = new Dictionary<string, double> { ["a"] = 1.0, ["c"] = 0.7 };

        var fused = MemoryRetrievalFusion.Fuse(semantic, keyword, Default);

        fused.Values.Should().OnlyContain(v => v >= 0.0 && v <= 1.0);
    }

    [Fact]
    public void Rrf_СливаетПоРангам_НормализуетВ_0_1()
    {
        // semantic ранги: a(1) b(2) c(3); keyword ранги: c(1) a(2)
        var semantic = new Dictionary<string, double> { ["a"] = 0.9, ["b"] = 0.5, ["c"] = 0.1 };
        var keyword = new Dictionary<string, double> { ["c"] = 0.8, ["a"] = 0.3 };

        var rrf = Default with { Method = MemoryFusionMethod.Rrf };
        var fused = MemoryRetrievalFusion.Fuse(semantic, keyword, rrf);

        // a: топ semantic + 2-й keyword → наибольший вклад, после нормализации = 1.0
        fused["a"].Should().BeApproximately(1.0, 1e-9);
        // c (топ keyword + 3-й semantic) обгоняет b (только 2-й semantic)
        fused["c"].Should().BeGreaterThan(fused["b"]);
        fused.Values.Should().OnlyContain(v => v >= 0.0 && v <= 1.0);
    }

    [Fact]
    public void Rrf_УчитываетВесаСигналов()
    {
        // При равных рангах бо́льший вес сигнала даёт бо́льший вклад: поднимем KeywordWeight
        var semantic = new Dictionary<string, double> { ["a"] = 0.9 };
        var keyword = new Dictionary<string, double> { ["b"] = 0.9 };

        // a — ранг 1 в semantic, b — ранг 1 в keyword; при доминирующем keyword b > a до нормализации
        var kwHeavy = new MemoryFusionOptions(
            SemanticWeight: 0.2, KeywordWeight: 0.8, Method: MemoryFusionMethod.Rrf);
        var fused = MemoryRetrievalFusion.Fuse(semantic, keyword, kwHeavy);

        // b получил больший вклад → после нормализации по максимуму b = 1.0, a < 1.0
        fused["b"].Should().BeApproximately(1.0, 1e-9);
        fused["a"].Should().BeLessThan(fused["b"]);
    }
}
