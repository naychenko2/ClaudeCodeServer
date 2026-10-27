using System.IO;
using System.Net;
using System.Net.Http;
using ClaudeHomeServer.Services.Llm;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services.LocalStats;

/// <summary>
/// Тесты LlamaServerMetricsSource: фейковый HttpMessageHandler отдаёт текст живой фикстуры
/// llama-server (снят с 192.168.7.107:18030/metrics 27.10). Проверяется соответствие полей
/// снимка по таблице разреза v2 (§2), список Unsupported и поведение на мусорном тексте.
/// </summary>
public class LlamaServerMetricsSourceTests
{
    private static string LoadFixture()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "LocalStats", "llama-server-metrics.txt");
        File.Exists(path).Should().BeTrue($"фикстура не скопирована в {path}");
        return File.ReadAllText(path);
    }

    // Фейковый HttpMessageHandler: отдаёт заданный текст как 200 OK.
    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly string _body;
        public FakeHandler(string body) => _body = body;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(_body) });
    }

    // Фейковый HttpMessageHandler: бросает исключение (ECONNREFUSED/таймаут: стенд выгружен арбитром).
    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => throw new HttpRequestException("connection refused");
    }

    private static LlamaServerMetricsSource SourceOf(string text)
        => new(new HttpClient(new FakeHandler(text)), "http://192.168.7.107:18030/metrics");

    // Каждое поле таблицы §2 равно числу из фикстуры (строки /metrics живого стенда).
    [Fact]
    public async Task ReadAsync_Fixture_FillsMappedFields()
    {
        var snapshot = await SourceOf(LoadFixture()).ReadAsync();
        snapshot.Should().NotBeNull();

        snapshot!.Running.Should().Be(0, "llamacpp:requests_processing 0");
        snapshot.Waiting.Should().Be(0, "llamacpp:requests_deferred 0");
        snapshot.GenerationTokens.Should().Be(5L, "llamacpp:tokens_predicted_total 5");
        snapshot.PromptTokens.Should().Be(4220L, "llamacpp:prompt_tokens_total 4220 (без кэша)");
        snapshot.InterTokenLatencySum.Should().Be(0.106194, "llamacpp:tokens_predicted_seconds_total");
        snapshot.PrefillTimeSum.Should().Be(4.91791, "llamacpp:prompt_seconds_total");
        // Доля кэша: hits = cached (0), queries = prompt + cached = 4220 + 0.
        snapshot.PrefixCacheHits.Should().Be(0L);
        snapshot.PrefixCacheQueries.Should().Be(4220L);
        // Новые KV-токены prefill = prompt_tokens_total (токены без кэшированных).
        snapshot.PrefillKvComputedTokens.Should().Be(4220d);

        // «Токенов на шаг» из llama-счётчиков нет: n_decode_total считает и батчи промпта.
        snapshot.InterTokenLatencyCount.Should().BeNull();
    }

    // Unsupported — ровно блоки, которых llama-server не отдаёт ни при какой конфигурации
    // (разрез v2, §2). Список назван здесь, а не взят из продукта: иначе правка самого
    // списка в продукте тестом не ловится.
    [Fact]
    public async Task ReadAsync_Fixture_ListsUnsupportedExactly()
    {
        var snapshot = await SourceOf(LoadFixture()).ReadAsync();
        snapshot.Should().NotBeNull();

        snapshot!.Unsupported.Should().BeEquivalentTo(
            "KvUsagePerc", "RamTierPerc", "RamTierHits", "RamTierQueries",
            "Preemptions", "QueueTimeSum", "DecodeTimeSum", "InterTokenLatencyCount");

        // Поля, которые фикстура наполняет, в Unsupported не попадают.
        snapshot.Unsupported.Should().NotContain("PrefillKvComputedTokens");
        snapshot.Unsupported.Should().NotContain("Running");
        snapshot.Unsupported.Should().NotContain("GenerationTokens");
    }

    // Мусорный или пустой текст (недоверенный источник): снимок без исключения — иначе
    // дорожка поллера умрёт (ADR-029). Мутация: бросить исключение из BuildSnapshot.
    [Fact]
    public async Task ReadAsync_GarbageOrEmptyText_ReturnsSnapshotWithoutThrow()
    {
        const string garbage = """
            totally-not-prometheus
            llamacpp:requests_processing
            {unclosed_brace
            llamacpp:prompt_tokens_total abc
            # just a comment
            """;

        foreach (var text in new[] { garbage, "", "   \n\n  " })
        {
            var snapshot = await SourceOf(text).ReadAsync();
            snapshot.Should().NotBeNull($"«{text.Trim()}» не роняет источник");
            snapshot!.Running.Should().BeNull();
            snapshot.GenerationTokens.Should().BeNull();
            snapshot.PromptTokens.Should().BeNull();
            snapshot.PrefixCacheQueries.Should().BeNull();
            snapshot.InterTokenLatencySum.Should().BeNull();
            snapshot.PrefillTimeSum.Should().BeNull();
        }
    }

    // Пара счётчиков prefix-кэша — пара: без prompt_tokens_cached_total оба поля null
    // и оба имени в Unsupported, хотя PromptTokens заполнен. Иначе доля кэша врёт.
    [Fact]
    public async Task ReadAsync_PrefixCacheCountersIncomplete_BothNullAndUnsupported()
    {
        const string text = """
            llamacpp:prompt_tokens_total 100
            llamacpp:tokens_predicted_total 3
            """;
        var snapshot = await SourceOf(text).ReadAsync();
        snapshot.Should().NotBeNull();

        snapshot!.PromptTokens.Should().Be(100L);
        snapshot.GenerationTokens.Should().Be(3L);
        snapshot.PrefixCacheHits.Should().BeNull();
        snapshot.PrefixCacheQueries.Should().BeNull();
        snapshot.Unsupported.Should().Contain("PrefixCacheHits");
        snapshot.Unsupported.Should().Contain("PrefixCacheQueries");
    }

    // Недоверенный ответ: NaN, ±Inf и 1e400 (парсер даёт +Inf) не превращаются в мусорное
    // целое, а дают null; соседняя нормальная метрика не страдает.
    [Theory]
    [InlineData("NaN")]
    [InlineData("+Inf")]
    [InlineData("-Inf")]
    [InlineData("1e400")]
    [InlineData("1e30")]
    public async Task ReadAsync_NonFiniteOrHugeValues_GiveNull(string raw)
    {
        var text = $"llamacpp:requests_processing {raw}\n" +
                   $"llamacpp:tokens_predicted_total {raw}\n" +
                   $"llamacpp:prompt_tokens_total {raw}\n" +
                   "llamacpp:requests_deferred 2\n";
        var snapshot = await SourceOf(text).ReadAsync();
        snapshot.Should().NotBeNull();

        snapshot!.Running.Should().BeNull();
        snapshot.GenerationTokens.Should().BeNull();
        snapshot.PromptTokens.Should().BeNull();
        // PrefillKvComputedTokens — double: конечные огромные значения (1e30) валидны,
        // отсекаются только NaN/±Inf.
        if (raw != "1e30")
            snapshot.PrefillKvComputedTokens.Should().BeNull();
        snapshot.Waiting.Should().Be(2);
    }

    // Стенд не отвечает (выгружен арбитром) — null, как у vLLM-адаптера: дорожка ставит down.
    [Fact]
    public async Task ReadAsync_HttpError_ReturnsNull()
    {
        var source = new LlamaServerMetricsSource(
            new HttpClient(new ThrowingHandler()), "http://192.168.7.107:18030/metrics");

        var snapshot = await source.ReadAsync();
        snapshot.Should().BeNull();
    }
}
