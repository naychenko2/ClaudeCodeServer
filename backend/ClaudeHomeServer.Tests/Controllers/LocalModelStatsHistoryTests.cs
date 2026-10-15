using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Media;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ClaudeHomeServer.Tests.Controllers;

// История медиа-генераций (ADR-030, шаг 3): GET /api/local-model/history и .../{id}/file.
// Обе ручки — [Authorize(Roles="admin")] (не-админ — 403) плюс серверный флаг local-model-stats
// (без него — 404). Файл — только по id записи и номеру варианта; 410 — файла нет (reason).
public class LocalModelStatsHistoryTests
{
    // Управляемый источник: тест задаёт Source и поведение Resolve (410-причины, число вариантов)
    private sealed class FakeHistorySource : IMediaHistoryResultSource
    {
        public string Source { get; set; } = "test-source";
        public Func<MediaHistoryRef, int, MediaHistoryResolution?>? Resolve { get; set; }
        public Task<MediaHistoryResolution?> ResolveAsync(MediaHistoryRef reference, int variant, CancellationToken ct)
            => Task.FromResult(Resolve?.Invoke(reference, variant));
    }

    private sealed class Harness : IDisposable
    {
        public TestWebApplicationFactory Factory { get; }
        public HttpClient Admin { get; }
        public HttpClient NonAdmin { get; }
        public FakeHistorySource Fake { get; }
        // Реальный id админа (testuser): записи журнала ведутся от его имени, чтобы
        // OwnerLogin резолвил логин, а не null
        public string AdminId { get; }

        public Harness(string source, Func<MediaHistoryRef, int, MediaHistoryResolution?>? resolve)
        {
            Fake = new FakeHistorySource { Source = source, Resolve = resolve };
            Factory = new TestWebApplicationFactory();
            Factory.ExtraServices = s => s.AddSingleton<IMediaHistoryResultSource>(Fake);
            // Админу (testuser) включаем флаг local-model-stats
            var users = Factory.Services.GetRequiredService<UserStore>();
            AdminId = users.FindByUsername(TestWebApplicationFactory.TestUsername)!.Id;
            users.SetFeatureFlag(AdminId, "local-model-stats", true);
            Admin = Factory.CreateAuthenticatedClient();
            NonAdmin = Factory.CreateAuthenticatedClient(TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);
        }

        // Запись в журнал: строка v:1 (enqueued + finished) с заданным origin
        public void WriteRecord(string promptId, string source, string? model = "sdxl", string? op = "generate_image")
        {
            var dir = Path.Combine(Factory.TempDir, "local-media-history");
            Directory.CreateDirectory(dir);
            var day = DateTime.UtcNow.ToString("yyyy-MM-dd");
            var origin = new LocalJobOrigin(
                OwnerId: AdminId, SessionId: "chat-1", Kind: "image", Op: op ?? "generate_image",
                Heavy: false, ProjectId: "proj-1", Source: source, SourceJobId: "job-1");
            var line = new JsonObject
            {
                ["v"] = 1,
                ["t"] = "enqueued",
                ["ts"] = "2026-10-15T10:00:00.0000000Z",
                ["id"] = promptId,
                ["origin"] = (JsonNode)JsonSerializer.SerializeToNode(origin, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!,
                ["model"] = model,
                ["prompt"] = "a cat",
                ["params"] = null,
                ["card"] = null,
                ["outcome"] = null,
                ["runSeconds"] = null,
                ["error"] = null,
            };
            var finished = new JsonObject
            {
                ["v"] = 1,
                ["t"] = "finished",
                ["ts"] = "2026-10-15T10:00:47.0000000Z",
                ["id"] = promptId,
                ["origin"] = (JsonNode)JsonSerializer.SerializeToNode(origin, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!,
                ["model"] = null,
                ["prompt"] = null,
                ["params"] = null,
                ["card"] = null,
                ["outcome"] = "completed",
                ["runSeconds"] = 47,
                ["error"] = null,
            };
            File.AppendAllText(Path.Combine(dir, day + ".jsonl"), line.ToJsonString() + "\n" + finished.ToJsonString() + "\n");
        }

        // Запись «до правки» шага 2 (задача 5ee71dd7): enqueued БЕЗ model/prompt/params (null)
        // и finished с отдельным исходом interrupted + error (≤500). Читатель обязан показать
        // null без падения и принять новый исход
        public void WriteBareInterrupted(string promptId, string source)
        {
            var dir = Path.Combine(Factory.TempDir, "local-media-history");
            Directory.CreateDirectory(dir);
            var day = DateTime.UtcNow.ToString("yyyy-MM-dd");
            var origin = new LocalJobOrigin(
                OwnerId: AdminId, SessionId: "chat-1", Kind: "image", Op: "generate_image",
                Heavy: false, ProjectId: "proj-1", Source: source, SourceJobId: "job-1");
            var wire = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
            // Один JsonNode — один родитель: для каждого события свой экземпляр origin
            var originNode = (JsonNode)JsonSerializer.SerializeToNode(origin, wire)!;
            var originNode2 = (JsonNode)JsonSerializer.SerializeToNode(origin, wire)!;
            var enqueued = new JsonObject
            {
                ["v"] = 1, ["t"] = "enqueued", ["ts"] = "2026-10-15T10:00:00.0000000Z",
                ["id"] = promptId, ["origin"] = originNode,
                ["model"] = null, ["prompt"] = null, ["params"] = null, ["card"] = null,
                ["outcome"] = null, ["runSeconds"] = null, ["error"] = null,
            };
            var finished = new JsonObject
            {
                ["v"] = 1, ["t"] = "finished", ["ts"] = "2026-10-15T10:00:47.0000000Z",
                ["id"] = promptId, ["origin"] = originNode2,
                ["model"] = null, ["prompt"] = null, ["params"] = null, ["card"] = null,
                ["outcome"] = "interrupted", ["runSeconds"] = null, ["error"] = "GPU OOM",
            };
            File.AppendAllText(Path.Combine(dir, day + ".jsonl"), enqueued.ToJsonString() + "\n" + finished.ToJsonString() + "\n");
        }

        public void Dispose() => Factory.Dispose();
    }

    // Не-админ (seconduser, роль user) — 403. Мутация: убрать Roles="admin" — тест краснеет
    [Fact]
    public async Task НеАдмин_403()
    {
        using var h = new Harness("test-source", null);
        var resp = await h.NonAdmin.GetAsync("/api/local-model/history");
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // Админ без флага local-model-stats — 404. Мутация: убрать серверную проверку флага — краснеет
    [Fact]
    public async Task АдминБезФлага_404()
    {
        var factory = new TestWebApplicationFactory();
        factory.ExtraServices = s => { };
        using (factory)
        using (var admin = factory.CreateAuthenticatedClient())
        {
            // Флаг не включён (тестовый users.json без override)
            var resp = await admin.GetAsync("/api/local-model/history");
            resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }
    }

    // recordId с «..» — 404 (произвольное чтение диска не раскрываем). Мутация: убрать проверку — краснеет
    [Fact]
    public async Task RecordIdСДвумяТочками_404()
    {
        using var h = new Harness("test-source", (r, n) => new MediaHistoryResolution(1, "/tmp/x.png", "image/png", null));
        var resp = await h.Admin.GetAsync("/api/local-model/history/a..b/file");
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // Неверный n (записи 1 вариант, просим 5) — 404. Мутация: убрать проверку n — краснеет
    [Fact]
    public async Task НеверныйN_404()
    {
        using var h = new Harness("test-source", (r, n) => new MediaHistoryResolution(1, "/tmp/x.png", "image/png", null));
        h.WriteRecord("p1", "test-source");
        var resp = await h.Admin.GetAsync("/api/local-model/history/p1/file?n=5");
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // 410 missing: источник говорит, что файла нет. Мутация: перепутать причину — краснеет
    [Fact]
    public async Task Missing_410()
    {
        using var h = new Harness("test-source", (r, n) => new MediaHistoryResolution(1, null, null, MediaHistoryGoneReason.Missing));
        h.WriteRecord("p1", "test-source");
        var resp = await h.Admin.GetAsync("/api/local-model/history/p1/file");
        resp.StatusCode.Should().Be(HttpStatusCode.Gone);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("reason").GetString().Should().Be("missing");
    }

    // 410 expired: файл истёк. Мутация: перепутать причину — краснеет
    [Fact]
    public async Task Expired_410()
    {
        using var h = new Harness("test-source", (r, n) => new MediaHistoryResolution(1, null, null, MediaHistoryGoneReason.Expired));
        h.WriteRecord("p1", "test-source");
        var resp = await h.Admin.GetAsync("/api/local-model/history/p1/file");
        resp.StatusCode.Should().Be(HttpStatusCode.Gone);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("reason").GetString().Should().Be("expired");
    }

    // 410 source_disabled: запись от источника, у которого нет реализации (lora). Мутация: 500 — краснеет
    [Fact]
    public async Task SourceDisabled_410()
    {
        using var h = new Harness("test-source", null);
        h.WriteRecord("p1", "lora");
        var resp = await h.Admin.GetAsync("/api/local-model/history/p1/file");
        resp.StatusCode.Should().Be(HttpStatusCode.Gone);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("reason").GetString().Should().Be("source_disabled");
    }

    // Обезличивание: в списке виден логин владельца, но НЕ название проекта/чата.
    // Мутация: добавить project/chat в DTO — тест краснеет
    [Fact]
    public async Task Список_БезНазванияПроектаИЧата()
    {
        using var h = new Harness("test-source", null);
        h.WriteRecord("p1", "test-source");
        var resp = await h.Admin.GetAsync("/api/local-model/history");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var records = body.GetProperty("records");
        records.GetArrayLength().Should().Be(1);
        var record = records[0];
        // Логин владельца виден (OwnerId = id админа → username testuser)
        record.GetProperty("owner").GetString().Should().Be(TestWebApplicationFactory.TestUsername);
        // Названия проекта/чата не раскрываются: таких полей в записи нет
        var raw = record.GetRawText();
        raw.Should().NotContain("projName");
        raw.Should().NotContain("chatName");
    }

    // Список: фильтры и курсор. Запись с kind=image, outcome=completed; фильтр по kind/image даёт её
    [Fact]
    public async Task Список_ФильтрПоВиду()
    {
        using var h = new Harness("test-source", null);
        h.WriteRecord("p1", "test-source");
        var resp = await h.Admin.GetAsync("/api/local-model/history?kind=image");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("records").GetArrayLength().Should().Be(1);
        // Счётчики: по виду и по исходу
        body.GetProperty("counters").GetProperty("kinds").GetProperty("image").GetInt32().Should().Be(1);
    }

    // Правка шага 2 (5ee71dd7): interrupted — отдельный исход. Статус, фильтр по нему и error из
    // finished. Мутация: свести interrupted к failed/игнорировать — тест краснеет
    [Fact]
    public async Task Interrupted_ОтдельныйИсход()
    {
        using var h = new Harness("test-source", null);
        h.WriteBareInterrupted("p1", "test-source");
        var resp = await h.Admin.GetAsync("/api/local-model/history?outcome=interrupted");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("records").GetArrayLength().Should().Be(1);
        var record = body.GetProperty("records")[0];
        record.GetProperty("status").GetString().Should().Be("interrupted");
        record.GetProperty("outcome").GetString().Should().Be("interrupted");
        // error из finished (≤500) виден
        record.GetProperty("error").GetString().Should().Be("GPU OOM");
    }

    // Правка шага 2 (5ee71dd7): enqueued «до правки» без model/prompt/params → null, без падения.
    // Мутация: падение на null-полях enqueued — тест краснеет
    [Fact]
    public async Task ГолыйEnqueued_ПоляNull()
    {
        using var h = new Harness("test-source", null);
        h.WriteBareInterrupted("p1", "test-source");
        var resp = await h.Admin.GetAsync("/api/local-model/history");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var record = body.GetProperty("records")[0];
        // Запись «до правки»: model/prompt/params не записаны → null (фронт покажет «параметры не записаны»)
        record.GetProperty("model").ValueKind.Should().Be(JsonValueKind.Null);
        record.GetProperty("prompt").ValueKind.Should().Be(JsonValueKind.Null);
        record.GetProperty("params").ValueKind.Should().Be(JsonValueKind.Null);
    }
}
