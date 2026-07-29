using System.Diagnostics.Metrics;
using System.Net;
using System.Text;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Memory;
using ClaudeHomeServer.Telemetry;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace ClaudeHomeServer.Tests.Telemetry;

/// <summary>
/// Поведение <see cref="MemoryDify.DiffSyncAsync"/> при отказе Dify на ИНДЕКСАЦИИ.
///
/// Контекст: счётчик <c>ccs.dify.sync.errors</c> оборачивал только удаление документов,
/// а вызов индексации стоял голым. Между тем именно он отказывает чаще всего (429 при
/// потоке правок, таймаут на большом тексте), и последствий было два: метрика молчала,
/// а исключение вылетало из всего дифф-синка — вызывающий не доходил до Save(), теряя
/// прогресс по уже перенесённым записям.
///
/// Dify поднят фейковым HttpMessageHandler (как в ProjectKnowledgeSyncServiceTests) —
/// проверяется настоящий путь через KnowledgeService, а не «вызов не бросил исключение».
/// </summary>
public class DifyIndexErrorMetricTests : IDisposable
{
    // Фейковый Dify: индексация документа, чьё имя содержит FailFor, отвечает 429;
    // остальные — 200 с новым doc-id. DELETE всегда 204.
    private sealed class FlakyDifyHandler : HttpMessageHandler
    {
        public string FailFor = "";
        private int _seq;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);

            if (request.Method == HttpMethod.Post && path.Contains("/document/create_by_text"))
            {
                var name = JsonDocument.Parse(body).RootElement.GetProperty("name").GetString() ?? "";
                if (FailFor.Length > 0 && name.Contains(FailFor, StringComparison.Ordinal))
                    return new HttpResponseMessage(HttpStatusCode.TooManyRequests);

                return Json($"{{\"document\":{{\"id\":\"doc-{++_seq}\"," +
                            $"\"name\":{JsonSerializer.Serialize(name)},\"indexing_status\":\"completed\"}}}}");
            }
            if (request.Method == HttpMethod.Delete)
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            return Json("{}");
        }

        private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
    }

    private readonly string _tempDir;
    private readonly FlakyDifyHandler _dify = new();
    private readonly KnowledgeService _knowledge;

    public DifyIndexErrorMetricTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "difyerr_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_tempDir, "projects.json"),
            })
            .Build();

        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(() => new HttpClient(_dify, disposeHandler: false));

        _knowledge = new KnowledgeService(factory.Object,
            Options.Create(new DifyOptions { ApiUrl = "http://dify.test/v1", ApiKey = "key" }),
            new WorkspaceKnowledgeStore(config));
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* тест-мусор */ }
        GC.SuppressFinalize(this);
    }

    /// <summary>Значения тега reason у измерений ccs.dify.sync.errors за время действия.</summary>
    private static async Task<List<string?>> CaptureReasonsAsync(Func<Task> action)
    {
        var reasons = new List<string?>();

        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == ServerMetrics.MeterName && instrument.Name == "ccs.dify.sync.errors")
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            foreach (var t in tags)
                if (t.Key == "reason") lock (reasons) reasons.Add(t.Value as string);
        });
        listener.Start();

        await action();
        return reasons;
    }

    [Fact]
    public async Task IndexFailure_IsCounted_AndDoesNotAbortTheRest()
    {
        _dify.FailFor = "плохая";

        var items = new List<MemorySyncItem>
        {
            new("bad", "hash-source-1", "заметка-плохая", "текст 1", null),
            new("good", "hash-source-2", "заметка-хорошая", "текст 2", null),
        };

        var docs = new Dictionary<string, MemoryDocRef>();
        var changed = 0;

        var reasons = await CaptureReasonsAsync(async () =>
        {
            changed = await MemoryDify.DiffSyncAsync(
                _knowledge, "ds-1", items, new Dictionary<string, MemoryDocRef>(),
                (id, doc) => docs[id] = doc,
                id => docs.Remove(id),
                NullLogger.Instance);
        });

        // 1. Отказ индексации виден в метрике — и классифицирован, а не свален в other
        reasons.Should().Contain("429", "отказ индексации обязан попадать в ccs.dify.sync.errors");

        // 2. Сбой одной записи не срывает весь синк: раньше исключение вылетало наружу,
        //    и вызывающий не доходил до сохранения стора
        changed.Should().Be(1);
        docs.Keys.Should().BeEquivalentTo(["good"]);

        // 3. Хеш упавшей записи не сохранён — следующий синк попробует её снова
        docs.Should().NotContainKey("bad");
    }

    [Fact]
    public async Task SuccessfulSync_DoesNotTouchErrorCounter()
    {
        var items = new List<MemorySyncItem>
        {
            new("a", "hash-a", "заметка-a", "текст a", null),
        };
        var docs = new Dictionary<string, MemoryDocRef>();

        var reasons = await CaptureReasonsAsync(async () =>
        {
            await MemoryDify.DiffSyncAsync(
                _knowledge, "ds-1", items, new Dictionary<string, MemoryDocRef>(),
                (id, doc) => docs[id] = doc,
                id => docs.Remove(id),
                NullLogger.Instance);
        });

        // Тесты идут параллельно и общий Meter слышен всем, поэтому утверждаем не
        // «ни одного измерения», а «моих причин отказа тут нет»: успешный синк не
        // порождает ни 429, ни timeout
        reasons.Should().NotContain("429");
        docs.Should().ContainKey("a");
    }
}
