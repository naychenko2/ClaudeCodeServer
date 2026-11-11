using System.Net;
using System.Text;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Knowledge;
using FluentAssertions;
using Moq;

namespace ClaudeHomeServer.Tests.Services;

// Двухшаговая запись метаданных Dify (дефект 87bb0af6): Dify молча отбрасывает doc_metadata
// из тела create_by_text — значение пишется только так: объявить поле на датасете
// (GET/POST metadata), затем назначить документу (POST documents/metadata, partial_update).
// Порядок, ключ кэша id и отказ назначения проверяются здесь; теги (поле "tags") — тем же
// механизмом, их поведение вне этого контракта.
public class KnowledgeServiceMetadataTests
{
    // Фейковый Dify: держит поля датасета (GET отдаёт, POST добавляет), заводит документы,
    // назначает метаданные настраиваемым кодом, помнит все запросы в порядке вызова.
    private sealed class FakeDify : HttpMessageHandler
    {
        public readonly List<(string Method, string Path, string? Body)> Calls = [];
        // datasetId → объявленные поля (id, name)
        public readonly Dictionary<string, List<(string Id, string Name)>> Fields = new();
        // Код ответа на POST documents/metadata (500 — сценарий отказа)
        public int AssignStatus = 200;
        // Задержка GET metadata: расширяет окно гонки — без блокировки оба параллельных
        // Ensure успевают пройти проверку кэша ДО того, как первый запишет id.
        public int GetMetadataDelayMs;
        private int _fieldSeq;
        private int _docSeq;

        public List<string> CallsOf(string method, string pathPart) =>
            Calls.Where(c => c.Method == method && c.Path.Contains(pathPart)).Select(c => c.Path).ToList();

        public List<string> Sequence => Calls.Select(c => $"{c.Method} {c.Path}").ToList();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            Calls.Add((request.Method.Method, path, body));
            var ds = path.Split('/', StringSplitOptions.RemoveEmptyEntries)[2]; // /v1/datasets/{id}/…

            if (request.Method == HttpMethod.Post && path.EndsWith("/documents/metadata"))
                return new HttpResponseMessage((HttpStatusCode)AssignStatus);
            if (request.Method == HttpMethod.Get && path.EndsWith("/metadata"))
            {
                if (GetMetadataDelayMs > 0) await Task.Delay(GetMetadataDelayMs, ct);
                var fields = string.Join(',', Fields.GetValueOrDefault(ds, []).Select(f =>
                    $"{{\"id\":\"{f.Id}\",\"name\":\"{f.Name}\",\"type\":\"string\"}}"));
                return Json($"{{\"doc_metadata\":[{fields}]}}");
            }
            if (request.Method == HttpMethod.Post && path.EndsWith("/metadata"))
            {
                var name = JsonDocument.Parse(body!).RootElement.GetProperty("name").GetString()!;
                var id = $"f-{ds}-{++_fieldSeq}";
                if (!Fields.TryGetValue(ds, out var list)) Fields[ds] = list = [];
                list.Add((id, name));
                return Json($"{{\"id\":\"{id}\",\"name\":\"{name}\",\"type\":\"string\"}}");
            }
            if (request.Method == HttpMethod.Post && path.Contains("/document/create_by_text"))
                return Json($"{{\"document\":{{\"id\":\"doc-{++_docSeq}\",\"name\":\"doc\",\"indexing_status\":\"completed\"}}}}");
            if (request.Method == HttpMethod.Delete)
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }

        private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
    }

    private static KnowledgeService Create(FakeDify dify)
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(() => new HttpClient(dify, disposeHandler: false));
        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build();
        return new KnowledgeService(factory.Object,
            Microsoft.Extensions.Options.Options.Create(new DifyOptions { ApiUrl = "http://dify.test/v1", ApiKey = "k" }),
            new WorkspaceKnowledgeStore(config));
    }

    // ─── 1. Порядок: create → объявить поле → назначить (c1) ──────────────────

    [Fact]
    public async Task IndexFileByTextAsync_СМетаданными_CreateПотомОбъявлениеПотомНазначение()
    {
        var dify = new FakeDify();
        var sut = Create(dify);

        var doc = await sut.IndexFileByTextAsync("ds-1", "Страница.md", "текст",
            new List<string> { "тег" }, metadata: new Dictionary<string, string> { ["wiki_section"] = "projects/p1" });

        dify.Sequence.Should().Equal(
            "POST /v1/datasets/ds-1/document/create_by_text",
            "GET /v1/datasets/ds-1/metadata",
            "POST /v1/datasets/ds-1/metadata",
            "POST /v1/datasets/ds-1/documents/metadata");

        // В теле create ключа метаданной нет — Dify отбрасывает её молча; теги остаются
        // в теле (старый контракт, этим шагом не тронуты).
        var createBody = JsonDocument.Parse(dify.Calls[0].Body!).RootElement;
        createBody.GetProperty("doc_metadata").TryGetProperty("wiki_section", out _).Should().BeFalse();
        createBody.GetProperty("doc_metadata").GetProperty("tags")[0].GetString().Should().Be("тег");

        // Назначение — id поля этого датасета, значение, partial_update.
        var op = JsonDocument.Parse(dify.Calls[3].Body!).RootElement.GetProperty("operation_data")[0];
        op.GetProperty("document_id").GetString().Should().Be(doc.Id);
        op.GetProperty("partial_update").GetBoolean().Should().BeTrue();
        var item = op.GetProperty("metadata_list")[0];
        item.GetProperty("id").GetString().Should().Be("f-ds-1-1");
        item.GetProperty("name").GetString().Should().Be("wiki_section");
        item.GetProperty("value").GetString().Should().Be("projects/p1");
    }

    [Fact]
    public async Task IndexFileByTextAsync_ПолеУжеОбъявлено_ВторичногоPOSTНаДатасетеНет()
    {
        var dify = new FakeDify();
        dify.Fields["ds-1"] = [("f-existing", "wiki_section")];
        var sut = Create(dify);

        await sut.IndexFileByTextAsync("ds-1", "Страница.md", "текст",
            metadata: new Dictionary<string, string> { ["wiki_section"] = "projects/p1" });

        dify.Sequence.Should().Equal(
            "POST /v1/datasets/ds-1/document/create_by_text",
            "GET /v1/datasets/ds-1/metadata",
            "POST /v1/datasets/ds-1/documents/metadata");
        JsonDocument.Parse(dify.Calls[2].Body!).RootElement
            .GetProperty("operation_data")[0].GetProperty("metadata_list")[0]
            .GetProperty("id").GetString().Should().Be("f-existing");
    }

    // ─── 2. Вызов без метаданных — те же HTTP-вызовы, что прежде (c4) ─────────

    [Fact]
    public async Task IndexFileByTextAsync_БезМетаданных_ТолькоCreateКакПрежде()
    {
        var dify = new FakeDify();
        var sut = Create(dify);

        await sut.IndexFileByTextAsync("ds-1", "ф.md", "текст", new List<string> { "т" });

        dify.Sequence.Should().Equal("POST /v1/datasets/ds-1/document/create_by_text");
        JsonDocument.Parse(dify.Calls[0].Body!).RootElement
            .GetProperty("doc_metadata").GetProperty("tags")[0].GetString().Should().Be("т");
    }

    // ─── 3. Кэш и гонка: ключ (datasetId, name), поле создаётся один раз (c3) ─

    [Fact]
    public async Task EnsureMetadataFieldAsync_ГонкаПоОдномуКлючу_РовноОдинPOSTПоля()
    {
        var dify = new FakeDify { GetMetadataDelayMs = 50 };
        var sut = Create(dify);

        var a = sut.EnsureMetadataFieldAsync("ds-1", "wiki_section");
        var b = sut.EnsureMetadataFieldAsync("ds-1", "wiki_section");
        var ids = await Task.WhenAll(a, b);
        var (idA, idB) = (ids[0], ids[1]);

        idA.Should().Be(idB);
        idA.Should().NotBeNullOrEmpty();
        dify.CallsOf("POST", "/metadata").Should().ContainSingle("параллельные Ensure создают поле один раз");
        dify.CallsOf("GET", "/metadata").Should().ContainSingle("второй Ensure берёт id из кэша после блокировки");
    }

    [Fact]
    public async Task EnsureMetadataFieldAsync_РазныеДатасетыОдноИмя_РазныеIdИЗапросНаКаждый()
    {
        var dify = new FakeDify();
        var sut = Create(dify);

        var id1 = await sut.EnsureMetadataFieldAsync("ds-1", "tags");
        var id2 = await sut.EnsureMetadataFieldAsync("ds-2", "tags");

        // id поля принадлежит датасету: кэш только по имени отдал бы чужой id.
        id1.Should().NotBe(id2);
        dify.CallsOf("GET", "/v1/datasets/ds-1/metadata").Should().ContainSingle();
        dify.CallsOf("GET", "/v1/datasets/ds-2/metadata").Should().ContainSingle();
    }

    // ─── 4. Отказ назначения: документ удалён, исключение вызывающему (c4) ────

    [Fact]
    public async Task IndexFileByTextAsync_ОтказНазначения_УдаляетДокументИБросает()
    {
        var dify = new FakeDify { AssignStatus = 500 };
        var sut = Create(dify);

        var act = () => sut.IndexFileByTextAsync("ds-1", "ф.md", "текст",
            metadata: new Dictionary<string, string> { ["wiki_section"] = "s" });

        await act.Should().ThrowAsync<HttpRequestException>();
        dify.CallsOf("DELETE", "/v1/datasets/ds-1/documents/doc-1").Should().ContainSingle(
            "документ без метаданных — сирота без видимости раздела, его удаляем best-effort");
    }

    // ─── 5. Теги идут тем же механизмом с именем "tags" ───────────────────────

    [Fact]
    public async Task UpdateDocumentTagsAsync_ОбъявляетTagsИНазначаетЧерезDocumentsMetadata()
    {
        var dify = new FakeDify();
        var sut = Create(dify);

        await sut.UpdateDocumentTagsAsync("ds-1", "doc-9", new List<string> { "а", "б" });

        dify.Sequence.Should().Equal(
            "GET /v1/datasets/ds-1/metadata",
            "POST /v1/datasets/ds-1/metadata",
            "POST /v1/datasets/ds-1/documents/metadata");
        var item = JsonDocument.Parse(dify.Calls[2].Body!).RootElement
            .GetProperty("operation_data")[0].GetProperty("metadata_list")[0];
        item.GetProperty("id").GetString().Should().Be("f-ds-1-1");
        item.GetProperty("name").GetString().Should().Be("tags");
        item.GetProperty("value").GetString().Should().Be("а,б");
    }
}
