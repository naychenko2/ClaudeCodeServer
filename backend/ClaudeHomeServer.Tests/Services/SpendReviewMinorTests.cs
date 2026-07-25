using System.Net;
using System.Text;
using ClaudeHomeServer.Controllers;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Spend;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services;

// Minor-находки ревью Глеба по Spend Analytics v2 (фоллоу-ап после мержа):
// 1) кламп периода /api/spend/* — не длиннее MaxPeriodDays и не раньше первой записи стора;
// 2) прокидка ownerId/label в записи free-расхода Ollama/CloudCheap (иначе всё уезжало
//    в «Система» без подписи действия).
public class SpendReviewMinorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "spend-minor-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    // --- minor-1: кламп периода ---

    [Fact]
    public void ClampPeriod_ПроизвольноРаннийFrom_ОграничиваетсяГодом()
    {
        var (f, t) = SpendController.ClampPeriod("0001-01-01", null, earliest: null);
        Assert.Equal(SpendController.MaxPeriodDays - 1, t.DayNumber - f.DayNumber);
    }

    [Fact]
    public void ClampPeriod_НеРаньшеПервойЗаписиСтора()
    {
        var earliest = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-10);
        var (f, t) = SpendController.ClampPeriod("2020-01-01", null, earliest);
        Assert.Equal(earliest, f);
        Assert.True(t >= f);
    }

    [Fact]
    public void ClampPeriod_ЧестныйПериод_НеТрогается()
    {
        var (f, t) = SpendController.ClampPeriod("2026-06-01", "2026-06-30", new DateOnly(2026, 1, 1));
        Assert.Equal(new DateOnly(2026, 6, 1), f);
        Assert.Equal(new DateOnly(2026, 6, 30), t);
    }

    [Fact]
    public void ClampPeriod_FromПозжеTo_СхлопываетсяВОдинДень()
    {
        var (f, t) = SpendController.ClampPeriod("2026-07-10", "2026-07-01", null);
        Assert.Equal(new DateOnly(2026, 7, 1), f);
        Assert.Equal(f, t);
    }

    [Fact]
    public void ClampPeriod_ЗапросЦеликомДоПервойЗаписи_СхлопываетсяВTo()
    {
        var (f, t) = SpendController.ClampPeriod("2020-01-01", "2020-12-31", new DateOnly(2026, 7, 1));
        Assert.Equal(new DateOnly(2020, 12, 31), f);
        Assert.Equal(f, t);
    }

    [Fact]
    public void EarliestDate_ПустойСторNull_ИначеМинимумПоДеталямИАгрегатам()
    {
        var store = new SpendStore(Path.Combine(_dir, "spend"), detailDays: 30);
        Assert.Null(store.EarliestDate);

        store.Record(NewRecord(new DateTime(2026, 7, 20, 12, 0, 0, DateTimeKind.Utc)));
        store.Record(NewRecord(new DateTime(2026, 7, 5, 12, 0, 0, DateTimeKind.Utc)));
        Assert.Equal(new DateOnly(2026, 7, 5), store.EarliestDate);

        // Свёрнутый в daily день продолжает считаться самой ранней датой
        store.RollupOlderThan(new DateOnly(2026, 7, 10));
        Assert.Equal(new DateOnly(2026, 7, 5), store.EarliestDate);
    }

    private static SpendRecord NewRecord(DateTime ts) => new()
    {
        Timestamp = ts,
        OwnerId = "u1",
        InputTokens = 10,
    };

    // --- minor-2: ownerId/label в записях free-расхода ---

    private const string OllamaJson =
        """{"message":{"content":"ответ"},"prompt_eval_count":10,"eval_count":5}""";

    private const string OpenRouterJson =
        """{"choices":[{"message":{"content":"ответ"}}],"usage":{"prompt_tokens":7,"completion_tokens":3}}""";

    [Fact]
    public async Task Ollama_RecordSpend_ПрокидываетВладельцаИПодпись()
    {
        var spend = new CollectingSpend();
        var client = NewOllama(spend);

        var text = await client.GenerateTextAsync("prompt", model: null, TimeSpan.FromSeconds(5),
            numPredict: 100, numCtx: 4096, ownerId: "u1", label: "notes.tags");

        Assert.Equal("ответ", text);
        var rec = Assert.Single(spend.Records);
        Assert.Equal("u1", rec.OwnerId);
        Assert.Equal("notes.tags", rec.Label);
        Assert.Equal(10, rec.InputTokens);
        Assert.Equal(5, rec.OutputTokens);
    }

    [Fact]
    public async Task Ollama_RecordSpend_БезВладельца_СистемнаяЗаписьКакРаньше()
    {
        var spend = new CollectingSpend();
        var client = NewOllama(spend);

        await client.GenerateTextAsync("prompt", model: null, TimeSpan.FromSeconds(5),
            numPredict: 100, numCtx: 4096);

        var rec = Assert.Single(spend.Records);
        Assert.Equal("", rec.OwnerId);
        Assert.Null(rec.Label);
    }

    [Fact]
    public async Task CloudCheap_RecordSpend_ПрокидываетВладельцаИПодпись()
    {
        var spend = new CollectingSpend();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LlmProviders:openrouter:ApiKey"] = "key",
                ["LlmProviders:openrouter:AnthropicBaseUrl"] = "https://openrouter.ai/api",
                ["LlmProviders:openrouter:ApiBaseUrl"] = "https://openrouter.ai/api/v1",
            })
            .Build();
        var providers = new LlmProviderRegistry(config);
        var client = new CloudCheapClient(new FakeHttpFactory(new FakeHandler(OpenRouterJson)),
            config, providers, NullLogger<CloudCheapClient>.Instance, spend);

        var text = await client.GenerateTextAsync("m:free", "prompt", TimeSpan.FromSeconds(5),
            maxTokens: 100, ownerId: "u2", label: "chat.title");

        Assert.Equal("ответ", text);
        var rec = Assert.Single(spend.Records);
        Assert.Equal("u2", rec.OwnerId);
        Assert.Equal("chat.title", rec.Label);
        Assert.Equal(7, rec.InputTokens);
        Assert.Equal(3, rec.OutputTokens);
    }

    // Полная цепочка: CheapTextRunner сам подписывает локальный вызов владельцем и ключом действия
    [Fact]
    public async Task CheapRunner_ЛокальныйШаг_ПодписываетРасходВладельцемИДействием()
    {
        var spend = new CollectingSpend();
        var config = OllamaConfig();
        var ollama = new OllamaClient(new FakeHttpFactory(new FakeHandler(OllamaJson)), config,
            NullLogger<OllamaClient>.Instance, spend);
        var overrides = new LocalActionOverridesStore(config, NullLogger<LocalActionOverridesStore>.Instance);
        var router = new LocalActionRouter(ollama, overrides, config, NullLogger<LocalActionRouter>.Instance);
        var providers = new LlmProviderRegistry(config);
        var cloud = new CloudCheapClient(new FakeHttpFactory(new FakeHandler(OpenRouterJson)),
            config, providers, NullLogger<CloudCheapClient>.Instance, spend);
        var claude = new OneShotClaudeRunner(providers, TestLauncherFactory.Instance, config);
        var runner = new CheapTextRunner(router, ollama, cloud, claude, NullLogger<CheapTextRunner>.Instance);

        var text = await runner.RunAsync(LocalActionCatalog.NotesTags, "prompt", ownerId: "u3");

        Assert.Equal("ответ", text);
        var rec = Assert.Single(spend.Records);
        Assert.Equal("u3", rec.OwnerId);
        Assert.Equal(LocalActionCatalog.NotesTags, rec.Label);
        Assert.Equal(SpendSources.Free, rec.Source);
    }

    private OllamaClient NewOllama(CollectingSpend spend) =>
        new(new FakeHttpFactory(new FakeHandler(OllamaJson)), OllamaConfig(),
            NullLogger<OllamaClient>.Instance, spend);

    private IConfiguration OllamaConfig() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Ollama:Model"] = "qwen3",
            ["DataPath"] = Path.Combine(_dir, "projects.json"),
        })
        .Build();

    private sealed class CollectingSpend : ISpendCollector
    {
        public List<SpendRecord> Records { get; } = [];
        public void Record(SpendRecord record) => Records.Add(record);
    }

    private sealed class FakeHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
    }

    private sealed class FakeHttpFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
