using System.Net;
using System.Text;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Knowledge;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ClaudeHomeServer.Tests.Services;

// Реконсайлер error-документов (шаг 3): лечение healable-документов, per-target backoff,
// сироты, карантин «ядовитых» записей, режимы observe/off. Тики зовутся напрямую
// (TickAsync), время — управляемый TimeProvider: ни одного Task.Delay.
public class KnowledgeIndexReconcilerTests
{
    private sealed class FakeTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now = now;
        public override DateTimeOffset GetUtcNow() => Now;
        public void Advance(TimeSpan by) => Now += by;
    }

    // Фейковый Dify: на листинг документов каждого датасета отдаёт настраиваемый список
    private sealed class FakeDifyHandler : HttpMessageHandler
    {
        public readonly List<string> Requests = new();
        // datasetId → JSON-массив документов листинга
        public readonly Dictionary<string, string> DocumentsJson = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri!;
            Requests.Add(uri.PathAndQuery);
            var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            // /v1/datasets/{id}/documents
            var datasetId = parts.Length >= 3 ? parts[2] : "";
            var docs = DocumentsJson.GetValueOrDefault(datasetId, "[]");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($"{{\"data\":{docs},\"has_more\":false,\"total\":0}}",
                    Encoding.UTF8, "application/json"),
            });
        }
    }

    // Фейковый участник: карта DocId→EntryKey задаётся тестом, мутации записываются
    private sealed class FakeParticipant : IKnowledgeSyncParticipant
    {
        public readonly List<KnowledgeSyncTarget> Targets = new();
        public readonly List<(string Label, IReadOnlyCollection<string> Keys)> Invalidations = new();
        public readonly List<string> Kicks = new();

        public KnowledgeSyncTarget AddTarget(string datasetId, string label, Dictionary<string, string> docToKey)
        {
            var target = new KnowledgeSyncTarget(
                datasetId, ["owner-1"], label,
                docIds => Task.FromResult<IReadOnlyList<(string, string)>>(
                    docIds.Where(docToKey.ContainsKey).Select(d => (d, docToKey[d])).ToList()),
                keys => { Invalidations.Add((label, keys)); return Task.CompletedTask; },
                () => Kicks.Add(label));
            Targets.Add(target);
            return target;
        }

        public IReadOnlyList<KnowledgeSyncTarget> ListTargets() => Targets;
    }

    private static string Doc(string id, string status = "error", string? error = null) =>
        JsonSerializer.Serialize(new { id, name = id, indexing_status = status, error });

    private readonly FakeDifyHandler _dify = new();
    private readonly FakeTime _time = new(DateTimeOffset.Parse("2026-08-13T12:00:00Z"));
    private readonly FakeParticipant _participant = new();

    private KnowledgeIndexReconciler Create(string mode, bool difyConfigured = true,
        int maxPerCycle = 100, int maxAttempts = 5)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Dify:Reconcile:Mode"] = mode,
                ["Dify:Reconcile:MaxPerCycle"] = maxPerCycle.ToString(),
                ["Dify:Reconcile:MaxAttemptsPerEntry"] = maxAttempts.ToString(),
            })
            .Build();
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(() => new HttpClient(_dify, disposeHandler: false));
        var knowledge = new KnowledgeService(factory.Object,
            Microsoft.Extensions.Options.Options.Create(new DifyOptions
            {
                ApiUrl = difyConfigured ? "http://dify.test/v1" : "",
                ApiKey = difyConfigured ? "key" : "",
            }),
            new WorkspaceKnowledgeStore(new ConfigurationBuilder().Build()));
        return new KnowledgeIndexReconciler(knowledge, [_participant], config,
            NullLogger<KnowledgeIndexReconciler>.Instance, _time);
    }

    [Fact]
    public async Task Healable_Лечится_И_Recovered_Считается_По_Исчезновению()
    {
        _participant.AddTarget("ds-1", "t1", new() { ["doc-1"] = "entry-1" });
        _dify.DocumentsJson["ds-1"] = $"[{Doc("doc-1", error: "Connection refused")}]";
        var sut = Create("heal");

        await sut.TickAsync();

        // Хеш сброшен по ключу записи, штатный синк пнут; recovered пока 0 — попытка не результат
        _participant.Invalidations.Should().ContainSingle()
            .Which.Keys.Should().BeEquivalentTo(["entry-1"]);
        _participant.Kicks.Should().BeEquivalentTo(["t1"]);
        sut.RecoveredTotal.Should().Be(0);
        sut.LastCounts.Should().ContainSingle().Which.Should().Be(new ReconcileTargetStatus("t1", 1, 0));

        // Следующий обход: документ вылечился (исчез из error) → recovered = 1
        _dify.DocumentsJson["ds-1"] = "[]";
        _time.Advance(TimeSpan.FromMinutes(16));
        await sut.TickAsync();

        sut.RecoveredTotal.Should().Be(1);
        sut.LastCounts.Should().ContainSingle().Which.Should().Be(new ReconcileTargetStatus("t1", 0, 0));
    }

    [Fact]
    public async Task Лежащий_Провайдер_Растит_Период_Цели_А_Соседняя_Обходится_По_Базовому()
    {
        _participant.AddTarget("ds-1", "залипшая", new() { ["doc-1"] = "entry-1" });
        _participant.AddTarget("ds-2", "здоровая", new() { ["doc-2"] = "entry-2" });
        _dify.DocumentsJson["ds-1"] = $"[{Doc("doc-1", error: "Connection refused: ollama")}]";
        _dify.DocumentsJson["ds-2"] = "[]";
        var sut = Create("heal");

        // Обход 1 (оба датасета), обход 2 через 16 мин: у залипшей healable не уменьшился →
        // её период вырос до 30 мин; здоровая остаётся на базовых 15
        await sut.TickAsync();
        _time.Advance(TimeSpan.FromMinutes(16));
        await sut.TickAsync();

        var callsAfterTwo = _dify.Requests.Count(r => r.Contains("ds-1"));
        // Ещё 16 минут: здоровой пора (15 мин), залипшей рано (30 мин от второго обхода)
        _time.Advance(TimeSpan.FromMinutes(16));
        await sut.TickAsync();

        _dify.Requests.Count(r => r.Contains("ds-1")).Should().Be(callsAfterTwo);   // не дергалась
        _dify.Requests.Count(r => r.Contains("ds-2")).Should().Be(3);               // каждый обход

        // А ещё через 16 минут (32 от второго обхода) залипшая снова обходится
        _time.Advance(TimeSpan.FromMinutes(16));
        await sut.TickAsync();
        _dify.Requests.Count(r => r.Contains("ds-1")).Should().Be(callsAfterTwo + 1);
    }

    [Fact]
    public async Task Попыток_За_Тик_Не_Больше_Потолка()
    {
        var docToKey = new Dictionary<string, string>();
        var docsJson = new List<string>();
        for (var i = 1; i <= 5; i++)
        {
            docToKey[$"doc-{i}"] = $"entry-{i}";
            docsJson.Add(Doc($"doc-{i}", error: "Connection refused"));
        }
        _participant.AddTarget("ds-1", "t1", docToKey);
        _dify.DocumentsJson["ds-1"] = $"[{string.Join(',', docsJson)}]";
        var sut = Create("heal", maxPerCycle: 3);

        await sut.TickAsync();

        _participant.Invalidations.Should().ContainSingle()
            .Which.Keys.Should().HaveCount(3);   // потолок, не все 5
    }

    [Fact]
    public async Task Сирота_Не_Трогается_Учитывается_Отдельно_И_Не_Растит_Backoff()
    {
        _participant.AddTarget("ds-1", "t1", new());   // стор пуст — все error-доки сироты
        _dify.DocumentsJson["ds-1"] = $"[{Doc("doc-orphan")}]";
        var sut = Create("heal");

        await sut.TickAsync();

        _participant.Invalidations.Should().BeEmpty();
        _participant.Kicks.Should().BeEmpty();
        sut.LastCounts.Should().ContainSingle().Which.Should().Be(new ReconcileTargetStatus("t1", 0, 1));

        // Backoff не вырос (healable=0 → базовый период): через 16 минут цель снова обходится
        var calls = _dify.Requests.Count;
        _time.Advance(TimeSpan.FromMinutes(16));
        await sut.TickAsync();
        _dify.Requests.Count.Should().Be(calls + 1);
    }

    [Fact]
    public async Task Ядовитая_Запись_Уходит_В_Карантин_Соседние_Лечатся()
    {
        // Контентная ошибка (не транзиентная) → карантин после 2 попыток
        _participant.AddTarget("ds-1", "t1", new() { ["doc-bad"] = "entry-bad", ["doc-ok"] = "entry-ok" });
        _dify.DocumentsJson["ds-1"] =
            $"[{Doc("doc-bad", error: "invalid content: parse failed")},{Doc("doc-ok", error: "Connection refused")}]";
        var sut = Create("heal");

        // Обходы 1 и 2: ядовитая ещё лечится (попытки 1 и 2), обход 3 — карантин
        await sut.TickAsync();
        _time.Advance(TimeSpan.FromMinutes(31));
        await sut.TickAsync();
        _time.Advance(TimeSpan.FromHours(3));
        await sut.TickAsync();

        _participant.Invalidations.Should().HaveCount(3);
        _participant.Invalidations[0].Keys.Should().BeEquivalentTo(["entry-bad", "entry-ok"]);
        _participant.Invalidations[1].Keys.Should().BeEquivalentTo(["entry-bad", "entry-ok"]);
        // Третий обход: ядовитая отброшена ДО мутации, сосед лечится дальше
        _participant.Invalidations[2].Keys.Should().BeEquivalentTo(["entry-ok"]);
        sut.QuarantinedKeys.Should().BeEquivalentTo(["t1:entry-bad"]);
    }

    [Fact]
    public async Task Observe_Считает_Но_Не_Мутирует()
    {
        _participant.AddTarget("ds-1", "t1", new() { ["doc-1"] = "entry-1" });
        _dify.DocumentsJson["ds-1"] = $"[{Doc("doc-1")},{Doc("doc-orphan")}]";
        var sut = Create("observe");

        await sut.TickAsync();

        _dify.Requests.Should().NotBeEmpty();   // читал
        _participant.Invalidations.Should().BeEmpty();
        _participant.Kicks.Should().BeEmpty();
        sut.LastCounts.Should().ContainSingle().Which.Should().Be(new ReconcileTargetStatus("t1", 1, 1));
    }

    [Fact]
    public async Task Off_Не_Делает_Ни_Одного_Вызова_Dify()
    {
        _participant.AddTarget("ds-1", "t1", new() { ["doc-1"] = "entry-1" });
        _dify.DocumentsJson["ds-1"] = $"[{Doc("doc-1")}]";
        var sut = Create("off");

        await sut.TickAsync();

        _dify.Requests.Should().BeEmpty();
        _participant.Invalidations.Should().BeEmpty();
    }

    [Fact]
    public async Task Упавшая_Цель_Не_Обрывает_Тик()
    {
        // Первая цель резолвится с исключением, вторая должна вылечиться
        _participant.Targets.Add(new KnowledgeSyncTarget(
            "ds-1", ["owner-1"], "падающая",
            _ => throw new InvalidOperationException("стор недоступен"),
            _ => Task.CompletedTask, () => { }));
        _participant.AddTarget("ds-2", "живая", new() { ["doc-2"] = "entry-2" });
        _dify.DocumentsJson["ds-1"] = $"[{Doc("doc-1")}]";
        _dify.DocumentsJson["ds-2"] = $"[{Doc("doc-2", error: "Connection refused")}]";
        var sut = Create("heal");

        await sut.TickAsync();

        _participant.Invalidations.Should().ContainSingle()
            .Which.Keys.Should().BeEquivalentTo(["entry-2"]);
    }

    [Fact]
    public async Task Смена_Режима_Сбрасывает_Backoff()
    {
        // В observe цель дважды обошлась с невылеченным доком → её период вырос.
        _participant.AddTarget("ds-1", "t1", new() { ["doc-1"] = "entry-1" });
        _dify.DocumentsJson["ds-1"] = $"[{Doc("doc-1", error: "Connection refused")}]";

        var configData = new Dictionary<string, string?> { ["Dify:Reconcile:Mode"] = "observe" };
        var config = new ConfigurationBuilder().AddInMemoryCollection(configData).Build();
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(() => new HttpClient(_dify, disposeHandler: false));
        var knowledge = new KnowledgeService(factory.Object,
            Microsoft.Extensions.Options.Options.Create(new DifyOptions { ApiUrl = "http://dify.test/v1", ApiKey = "key" }),
            new WorkspaceKnowledgeStore(new ConfigurationBuilder().Build()));
        var sut = new KnowledgeIndexReconciler(knowledge, [_participant], config,
            NullLogger<KnowledgeIndexReconciler>.Instance, _time);

        await sut.TickAsync();
        _time.Advance(TimeSpan.FromMinutes(16));
        await sut.TickAsync();   // период t1 теперь 30 мин

        // Горячее переключение observe → heal: backoff сброшен, цель обходится сразу
        config["Dify:Reconcile:Mode"] = "heal";
        var calls = _dify.Requests.Count;
        _time.Advance(TimeSpan.FromMinutes(1));
        await sut.TickAsync();

        _dify.Requests.Count.Should().Be(calls + 1);
        _participant.Invalidations.Should().ContainSingle();
    }

    [Fact]
    public void NextInterval_Чистая_Функция_Backoff()
    {
        var opts = new KnowledgeReconcileOptions("heal",
            TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), 100, TimeSpan.FromHours(2), 5);

        // Первый обход (lastHealable = -1) — базовый период
        KnowledgeIndexReconciler.NextInterval(TimeSpan.FromMinutes(15), opts, -1, 3)
            .Should().Be(TimeSpan.FromMinutes(15));
        // Не уменьшилось — удвоение
        KnowledgeIndexReconciler.NextInterval(TimeSpan.FromMinutes(15), opts, 3, 3)
            .Should().Be(TimeSpan.FromMinutes(30));
        // Потолок MaxBackoff
        KnowledgeIndexReconciler.NextInterval(TimeSpan.FromMinutes(90), opts, 3, 4)
            .Should().Be(TimeSpan.FromHours(2));
        // Уменьшилось — сброс к базе
        KnowledgeIndexReconciler.NextInterval(TimeSpan.FromHours(2), opts, 3, 2)
            .Should().Be(TimeSpan.FromMinutes(15));
        // Вылечилось всё — сброс
        KnowledgeIndexReconciler.NextInterval(TimeSpan.FromHours(2), opts, 3, 0)
            .Should().Be(TimeSpan.FromMinutes(15));
    }

    [Fact]
    public void IsTransientError_Классифицирует_По_Тексту()
    {
        KnowledgeIndexReconciler.IsTransientError("Connection refused: host").Should().BeTrue();
        KnowledgeIndexReconciler.IsTransientError("Read timed out").Should().BeTrue();
        KnowledgeIndexReconciler.IsTransientError("service unavailable").Should().BeTrue();
        KnowledgeIndexReconciler.IsTransientError("invalid document format").Should().BeFalse();
        KnowledgeIndexReconciler.IsTransientError("").Should().BeFalse();
    }
}
