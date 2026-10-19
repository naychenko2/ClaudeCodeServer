using System.IO;
using System.Net;
using System.Net.Http;
using ClaudeHomeServer.Services.Llm;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services.LocalStats;

/// <summary>
/// Тесты VllmMetricsSource: фейковый HttpMessageHandler отдаёт текст живой
/// фикстуры vLLM; проверяется заполнение EngineSnapshot и вариант «метрики
/// не найдены» → поля null, Unsupported непуст.
/// </summary>
public class VllmMetricsSourceTests
{
    private static string LoadFixture()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "LocalStats", "vllm-metrics.txt");
        File.Exists(path).Should().BeTrue($"фикстура не скопирована в {path}");
        return File.ReadAllText(path);
    }

    // Фейковый HttpMessageHandler: отдаёт заданный текст как 200 OK.
    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly string _body;
        public FakeHandler(string body) => _body = body;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_body)
            };
            return Task.FromResult(resp);
        }
    }

    // Фейковый HttpMessageHandler: бросает исключение (симуляция ECONNREFUSED/таймаута).
    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => throw new HttpRequestException("connection refused");
    }

    [Fact]
    public async Task ReadAsync_Fixture_FillsSnapshot()
    {
        var http = new HttpClient(new FakeHandler(LoadFixture()));
        var source = new VllmMetricsSource(http, "http://localhost:8000/metrics");

        var snapshot = await source.ReadAsync();
        snapshot.Should().NotBeNull();

        snapshot!.Running.Should().Be(1);
        snapshot.Waiting.Should().Be(0);
        snapshot.KvUsagePerc.Should().Be(0.317716747442111);
        snapshot.RamTierPerc.Should().Be(0.0);
        snapshot.PrefixCacheHits.Should().Be(537565952);
        snapshot.PrefixCacheQueries.Should().Be(759336288);
        snapshot.Preemptions.Should().Be(17);
        snapshot.GenerationTokens.Should().Be(3916204);
        snapshot.PromptTokens.Should().Be(759244632);
        snapshot.DecodeTimeSum.Should().Be(63686.26873381428);
        snapshot.PrefillTimeSum.Should().Be(21638.794842731793);
        snapshot.PrefillKvComputedTokens.Should().Be(31087402.0);
        snapshot.RamTierHits.Should().Be(953624);
        snapshot.RamTierQueries.Should().Be(5231151);
        snapshot.InterTokenLatencySum.Should().Be(64492.1099384452);
        snapshot.InterTokenLatencyCount.Should().Be(953047);
        snapshot.QueueTimeSum.Should().Be(51512.094793356744);
        snapshot.Unsupported.Should().BeEmpty();
    }

    // Gauge-метрики с процентом (kv_cache_usage_perc, kv_offload_cpu_cache_usage_perc)
    // при нескольких сериях берут МАКСИМУМ, а не первую серию.
    [Fact]
    public async Task ReadAsync_MultiSeriesGauge_TakesMaxAcrossSeries()
    {
        // Две серии kv_cache_usage_perc (0.2 и 0.7) + две серии kv_offload_cpu_cache_usage_perc
        // (0.9 и 0.1): максимум 0.7 и 0.9, а не первая серия (0.2 / 0.9).
        const string text = """
            # HELP vllm:kv_cache_usage_perc KV cache usage
            # TYPE vllm:kv_cache_usage_perc gauge
            vllm:kv_cache_usage_perc{model_name="a"} 0.2
            vllm:kv_cache_usage_perc{model_name="b"} 0.7
            # HELP vllm:kv_offload_cpu_cache_usage_perc CPU cache usage
            # TYPE vllm:kv_offload_cpu_cache_usage_perc gauge
            vllm:kv_offload_cpu_cache_usage_perc{tier="0:primary"} 0.9
            vllm:kv_offload_cpu_cache_usage_perc{tier="1:fs"} 0.1
            """;
        var http = new HttpClient(new FakeHandler(text));
        var source = new VllmMetricsSource(http, "http://localhost:8000/metrics");

        var snapshot = await source.ReadAsync();
        snapshot.Should().NotBeNull();
        snapshot!.KvUsagePerc.Should().Be(0.7, "максимум по сериям, а не первая серия");
        snapshot.RamTierPerc.Should().Be(0.9, "максимум по сериям, а не первая серия");
    }

    [Fact]
    public async Task ReadAsync_MetricsNotFound_FieldsNullAndUnsupportedNonEmpty()
    {
        // Текст без нужных метрик vLLM: только одна нерелевантная метрика.
        const string text = """
            # HELP unrelated_metric Some unrelated metric
            # TYPE unrelated_metric gauge
            unrelated_metric 1.0
            """;
        var http = new HttpClient(new FakeHandler(text));
        var source = new VllmMetricsSource(http, "http://localhost:8000/metrics");

        var snapshot = await source.ReadAsync();
        snapshot.Should().NotBeNull();

        snapshot!.Running.Should().BeNull();
        snapshot.Waiting.Should().BeNull();
        snapshot.KvUsagePerc.Should().BeNull();
        snapshot.RamTierPerc.Should().BeNull();
        snapshot.PrefixCacheHits.Should().BeNull();
        snapshot.PrefixCacheQueries.Should().BeNull();
        snapshot.Preemptions.Should().BeNull();
        snapshot.GenerationTokens.Should().BeNull();
        snapshot.PromptTokens.Should().BeNull();
        snapshot.DecodeTimeSum.Should().BeNull();
        snapshot.PrefillTimeSum.Should().BeNull();
        snapshot.PrefillKvComputedTokens.Should().BeNull();
        snapshot.RamTierHits.Should().BeNull();
        snapshot.RamTierQueries.Should().BeNull();
        snapshot.InterTokenLatencySum.Should().BeNull();
        snapshot.InterTokenLatencyCount.Should().BeNull();
        snapshot.QueueTimeSum.Should().BeNull();
        // Все блоки маппинга отсутствуют → Unsupported = все имена из AllMetrics.
        snapshot.Unsupported.Should().HaveCount(VllmMetricsSource.AllMetrics.Length);
    }

    [Fact]
    public async Task ReadAsync_HttpError_ReturnsNull()
    {
        var http = new HttpClient(new ThrowingHandler());
        var source = new VllmMetricsSource(http, "http://localhost:8000/metrics");

        var snapshot = await source.ReadAsync();
        snapshot.Should().BeNull();
    }

    // Мусор в /metrics (недоверенный источник): строки, которые не разбираются как
    // Prometheus (нет значения, незакрытая скобка, дробное число) → Parse не бросает,
    // ReadAsync возвращает снимок с null-полями, а не исключение (иначе BackgroundService
    // умрёт — ADR-029 «Недоверенный внешний ответ»). Мутация: Parse бросает на мусоре
    // (например, GetValue вместо TryGetValue) → тест краснеет.
    [Fact]
    public async Task ReadAsync_GarbageText_DoesNotThrow()
    {
        const string garbage = """
            totally-not-prometheus
            name_without_value
            {unclosed_brace
            metric_with_bad_value abc
            # just a comment
            """;
        var http = new HttpClient(new FakeHandler(garbage));
        var source = new VllmMetricsSource(http, "http://localhost:8000/metrics");

        var snapshot = await source.ReadAsync();
        snapshot.Should().NotBeNull("мусор не роняет источник");
        snapshot!.KvUsagePerc.Should().BeNull("не разобралось → null");
        snapshot.PrefixCacheHits.Should().BeNull();
        snapshot.Preemptions.Should().BeNull();
    }

    [Fact]
    public async Task NullEngineMetricsSource_ReturnsEmptySnapshotWithAllBlocksUnsupported()
    {
        var source = new NullEngineMetricsSource();
        var snapshot = await source.ReadAsync();
        snapshot.Should().NotBeNull();

        snapshot!.Running.Should().BeNull();
        snapshot.Waiting.Should().BeNull();
        snapshot.KvUsagePerc.Should().BeNull();
        snapshot.RamTierPerc.Should().BeNull();
        snapshot.PrefixCacheHits.Should().BeNull();
        snapshot.PrefixCacheQueries.Should().BeNull();
        snapshot.Preemptions.Should().BeNull();
        snapshot.GenerationTokens.Should().BeNull();
        snapshot.PromptTokens.Should().BeNull();
        snapshot.DecodeTimeSum.Should().BeNull();
        snapshot.PrefillTimeSum.Should().BeNull();
        snapshot.PrefillKvComputedTokens.Should().BeNull();
        snapshot.RamTierHits.Should().BeNull();
        snapshot.RamTierQueries.Should().BeNull();
        snapshot.InterTokenLatencySum.Should().BeNull();
        snapshot.InterTokenLatencyCount.Should().BeNull();
        snapshot.QueueTimeSum.Should().BeNull();
        snapshot.Unsupported.Should().HaveCount(VllmMetricsSource.AllMetrics.Length);
    }
}
