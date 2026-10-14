using System.Globalization;
using System.IO;
using ClaudeHomeServer.Services.Llm;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services.LocalStats;

/// <summary>
/// Тесты парсера текстового формата Prometheus на живой фикстуре vLLM
/// (Fixtures/LocalStats/vllm-metrics.txt, снят со стенда) + синтетических
/// образцах для +Inf/NaN и экранирования меток.
/// </summary>
public class PrometheusTextParserTests
{
    // Путь к фикстуре в каталоге сборки: AppContext.BaseDirectory + относительный путь.
    private static string FixturePath =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "LocalStats", "vllm-metrics.txt");

    private static string LoadFixture()
    {
        var path = FixturePath;
        File.Exists(path).Should().BeTrue($"фикстура не скопирована в {path}");
        return File.ReadAllText(path);
    }

    // --- Живая фикстура: все метрики маппинга разобраны в ожидаемые числа ---

    [Fact]
    public void Parse_Fixture_ExtractsAllMappedMetrics()
    {
        var series = PrometheusTextParser.Parse(LoadFixture());

        // Одиночные метрики (одна серия).
        series.GetValue("vllm:num_requests_running").Should().Be(1.0);
        series.GetValue("vllm:num_requests_waiting").Should().Be(0.0);
        series.GetValue("vllm:kv_cache_usage_perc").Should().Be(0.317716747442111);
        series.GetValue("vllm:kv_offload_cpu_cache_usage_perc").Should().Be(0.0);

        // Счётчики (одна серия).
        series.GetValue("vllm:prefix_cache_hits_total").Should().Be(537565952.0);
        series.GetValue("vllm:prefix_cache_queries_total").Should().Be(759336288.0);
        series.GetValue("vllm:num_preemptions_total").Should().Be(17.0);
        series.GetValue("vllm:generation_tokens_total").Should().Be(3916204.0);
        series.GetValue("vllm:prompt_tokens_total").Should().Be(759244632.0);

        // Суммы гистограмм (одна серия).
        series.GetValue("vllm:request_decode_time_seconds_sum").Should().Be(63686.26873381428);
        series.GetValue("vllm:request_prefill_time_seconds_sum").Should().Be(21638.794842731793);
        series.GetValue("vllm:request_prefill_kv_computed_tokens_sum").Should().Be(31087402.0);
    }

    [Fact]
    public void Parse_Fixture_SumsLabeledMetricsAcrossSeries()
    {
        var series = PrometheusTextParser.Parse(LoadFixture());

        // kv_offload_tiering_chunk_hits_total: две серии (tier="1:fs" + tier="0:primary").
        // 788735.0 + 164889.0 = 953624.0
        series.Sum("vllm:kv_offload_tiering_chunk_hits_total").Should().Be(953624.0);

        // kv_offload_tiering_chunk_queries_total: две серии (tier="0:primary" + tier="1:fs").
        // 4356141.0 + 875010.0 = 5231151.0
        series.Sum("vllm:kv_offload_tiering_chunk_queries_total").Should().Be(5231151.0);
    }

    [Fact]
    public void Parse_Fixture_GetValueWithLabelFilter()
    {
        var series = PrometheusTextParser.Parse(LoadFixture());

        // Фильтр по метке tier: только серия tier="1:fs".
        var fsOnly = series.GetValue(
            "vllm:kv_offload_tiering_chunk_hits_total",
            new Dictionary<string, string> { ["tier"] = "1:fs" });
        fsOnly.Should().Be(788735.0);

        // Фильтр по метке tier: только серия tier="0:primary".
        var primaryOnly = series.GetValue(
            "vllm:kv_offload_tiering_chunk_hits_total",
            new Dictionary<string, string> { ["tier"] = "0:primary" });
        primaryOnly.Should().Be(164889.0);

        // Фильтр, который не совпадает ни с одной серией.
        var none = series.GetValue(
            "vllm:kv_offload_tiering_chunk_hits_total",
            new Dictionary<string, string> { ["tier"] = "nonexistent" });
        none.Should().BeNull();
    }

    [Fact]
    public void Parse_Fixture_HistogramSumAndCount()
    {
        var series = PrometheusTextParser.Parse(LoadFixture());

        // request_decode_time_seconds: гистограмма с _sum и _count.
        var (sum, count) = series.GetHistogram("vllm:request_decode_time_seconds");
        sum.Should().Be(63686.26873381428);
        count.Should().Be(11381.0);
    }

    [Fact]
    public void Parse_Fixture_MissingMetricReturnsNull()
    {
        var series = PrometheusTextParser.Parse(LoadFixture());

        // Метрики, которых нет в фикстуре (например, gpu_cache_usage_perc в новых vLLM).
        series.GetValue("vllm:gpu_cache_usage_perc").Should().BeNull();
        series.Sum("vllm:gpu_cache_usage_perc").Should().BeNull();
    }

    // --- Синтетические образцы: +Inf, NaN, экранирование меток ---

    [Fact]
    public void Parse_PosInfValue()
    {
        const string text = """
            some_metric{label="value"} +Inf
            """;
        var series = PrometheusTextParser.Parse(text);
        series.Samples.Should().HaveCount(1);
        series.Samples[0].Value.Should().Be(double.PositiveInfinity);
    }

    [Fact]
    public void Parse_NegativeInfValue()
    {
        const string text = """
            some_metric -Inf
            """;
        var series = PrometheusTextParser.Parse(text);
        series.Samples[0].Value.Should().Be(double.NegativeInfinity);
    }

    [Fact]
    public void Parse_NanValue()
    {
        const string text = """
            some_metric NaN
            """;
        var series = PrometheusTextParser.Parse(text);
        series.Samples[0].Value.Should().Be(double.NaN);
    }

    // Prometheus-спецификация допускает +Inf/+inf, -Inf/-inf, NaN/nan: проверяем нижний регистр.
    [Fact]
    public void Parse_LowercaseInfAndNan()
    {
        var posInf = PrometheusTextParser.Parse("m +inf");
        posInf.Samples[0].Value.Should().Be(double.PositiveInfinity);

        var negInf = PrometheusTextParser.Parse("m -inf");
        negInf.Samples[0].Value.Should().Be(double.NegativeInfinity);

        var nan = PrometheusTextParser.Parse("m nan");
        nan.Samples[0].Value.Should().Be(double.NaN);
    }

    [Fact]
    public void Parse_EscapedQuoteInLabelValue()
    {
        // В текстовом формате Prometheus кавычка в значении метки экранируется как \".
        const string text = """
            metric{key="va\"lue"} 1.0
            """;
        var series = PrometheusTextParser.Parse(text);
        series.Samples.Should().HaveCount(1);
        series.Samples[0].Labels["key"].Should().Be("va\"lue");
    }

    [Fact]
    public void Parse_EscapedBackslashInLabelValue()
    {
        // Бэкслэш в значении метки экранируется как \\.
        const string text = """
            metric{key="back\\slash"} 2.0
            """;
        var series = PrometheusTextParser.Parse(text);
        series.Samples[0].Labels["key"].Should().Be("back\\slash");
    }

    [Fact]
    public void Parse_MultipleLabels()
    {
        const string text = """
            http_requests_total{method="POST",status="200",path="/api"} 42.0
            """;
        var series = PrometheusTextParser.Parse(text);
        series.Samples.Should().HaveCount(1);
        series.Samples[0].Labels["method"].Should().Be("POST");
        series.Samples[0].Labels["status"].Should().Be("200");
        series.Samples[0].Labels["path"].Should().Be("/api");
        series.Samples[0].Value.Should().Be(42.0);
    }

    [Fact]
    public void Parse_ScientificNotation()
    {
        const string text = """
            metric 1.23456e+07
            """;
        var series = PrometheusTextParser.Parse(text);
        series.Samples[0].Value.Should().Be(12345600.0);
    }

    [Fact]
    public void Parse_NoLabels()
    {
        const string text = """
            process_virtual_memory_bytes 2.1623218176e+10
            """;
        var series = PrometheusTextParser.Parse(text);
        series.Samples.Should().HaveCount(1);
        series.Samples[0].Labels.Should().BeEmpty();
        series.Samples[0].Value.Should().Be(21623218176.0);
    }

    // --- Пустой ввод → пустой результат ---

    [Fact]
    public void Parse_EmptyInput_ReturnsEmptyResult()
    {
        var series = PrometheusTextParser.Parse("");
        series.Samples.Should().BeEmpty();
    }

    [Fact]
    public void Parse_NullInput_ReturnsEmptyResult()
    {
        var series = PrometheusTextParser.Parse(null);
        series.Samples.Should().BeEmpty();
    }

    [Fact]
    public void Parse_OnlyComments_ReturnsEmptyResult()
    {
        const string text = """
            # HELP foo bar
            # TYPE foo gauge
            """;
        var series = PrometheusTextParser.Parse(text);
        series.Samples.Should().BeEmpty();
    }
}
