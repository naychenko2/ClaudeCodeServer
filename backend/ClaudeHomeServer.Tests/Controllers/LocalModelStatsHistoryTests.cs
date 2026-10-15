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

    // Фиксированный «now» (UTC) — детерминированный разбор period (today/24h/7d). Локальный
    // день «today» считается от этого UTC через TimeZoneInfo.Local машины.
    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _utc;
        public FixedTimeProvider(DateTimeOffset utc) => _utc = utc;
        public override DateTimeOffset GetUtcNow() => _utc;
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

        public Harness(string source, Func<MediaHistoryRef, int, MediaHistoryResolution?>? resolve,
            TimeProvider? time = null)
        {
            Fake = new FakeHistorySource { Source = source, Resolve = resolve };
            Factory = new TestWebApplicationFactory();
            Factory.ExtraServices = s =>
            {
                s.AddSingleton<IMediaHistoryResultSource>(Fake);
                if (time is not null) s.AddSingleton<TimeProvider>(time);
            };
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

        // Запись с заданным временем ts (enqueued+finished в один момент) и датой файла fileDay.
        // ts управляет SortTime (фильтр from/to), fileDay — попаданием в 7-дневное окно индекса.
        public void WriteRecordAt(string promptId, string source, string ts, string fileDay)
        {
            var dir = Path.Combine(Factory.TempDir, "local-media-history");
            Directory.CreateDirectory(dir);
            var origin = new LocalJobOrigin(
                OwnerId: AdminId, SessionId: "chat-1", Kind: "image", Op: "generate_image",
                Heavy: false, ProjectId: "proj-1", Source: source, SourceJobId: "job-1");
            var wire = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
            var originNode = (JsonNode)JsonSerializer.SerializeToNode(origin, wire)!;
            var originNode2 = (JsonNode)JsonSerializer.SerializeToNode(origin, wire)!;
            var enqueued = new JsonObject
            {
                ["v"] = 1, ["t"] = "enqueued", ["ts"] = ts,
                ["id"] = promptId, ["origin"] = originNode,
                ["model"] = "sdxl", ["prompt"] = "a cat", ["params"] = null, ["card"] = null,
                ["outcome"] = null, ["runSeconds"] = null, ["error"] = null,
            };
            var finished = new JsonObject
            {
                ["v"] = 1, ["t"] = "finished", ["ts"] = ts,
                ["id"] = promptId, ["origin"] = originNode2,
                ["model"] = null, ["prompt"] = null, ["params"] = null, ["card"] = null,
                ["outcome"] = "completed", ["runSeconds"] = 5, ["error"] = null,
            };
            File.AppendAllText(Path.Combine(dir, fileDay + ".jsonl"), enqueued.ToJsonString() + "\n" + finished.ToJsonString() + "\n");
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

    // 410 source_disabled: запись от источника, у которого нет реализации. Мутация: 500 — краснеет.
    // lora здесь не годится: с ADR-030/026 у него есть реализация (LoraHistorySource), поэтому
    // берём вымышленный источник, которого в DI нет
    [Fact]
    public async Task SourceDisabled_410()
    {
        using var h = new Harness("test-source", null);
        h.WriteRecord("p1", "no-such-source");
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

    // Дефект e8c43aba: запись с причиной недоступности (no_result — failed/cancelled/interrupted,
    // missing — удалённый файл) отвечает 410 с причиной, даже при VariantCount==0 (n=0 «вне
    // диапазона»). У доступной записи (Reason==null) неверный n остаётся 404.
    // Мутация: вернуть проверку n ДО reason — 410 превращается в 404, тест краснеет.
    [Fact]
    public async Task Причина410_ПроверяетсяДоN()
    {
        // no_result: исход не completed, файлов нет (VariantCount==0)
        using (var h = new Harness("test-source",
            (r, n) => new MediaHistoryResolution(0, null, null, MediaHistoryGoneReason.NoResult)))
        {
            h.WriteRecord("p1", "test-source");
            var resp = await h.Admin.GetAsync("/api/local-model/history/p1/file?n=0");
            resp.StatusCode.Should().Be(HttpStatusCode.Gone);
            (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("reason").GetString()
                .Should().Be("no_result");
        }

        // missing: файл удалён (VariantCount==0)
        using (var h = new Harness("test-source",
            (r, n) => new MediaHistoryResolution(0, null, null, MediaHistoryGoneReason.Missing)))
        {
            h.WriteRecord("p2", "test-source");
            var resp = await h.Admin.GetAsync("/api/local-model/history/p2/file?n=0");
            resp.StatusCode.Should().Be(HttpStatusCode.Gone);
            (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("reason").GetString()
                .Should().Be("missing");
        }

        // available: Reason==null, 1 вариант; неверный n=5 → 404
        using (var h = new Harness("test-source",
            (r, n) => new MediaHistoryResolution(1, "/tmp/x.png", "image/png", null)))
        {
            h.WriteRecord("p3", "test-source");
            var resp = await h.Admin.GetAsync("/api/local-model/history/p3/file?n=5");
            resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }
    }

    // Дефект fd9dcfc0: незнакомое значение kind/outcome/period — 400 с понятной ошибкой
    // (а не 200 с пустым списком). Мутация: убрать валидацию — 400 → 200, тест краснеет.
    [Theory]
    [InlineData("kind", "foo")]
    [InlineData("outcome", "foo")]
    [InlineData("period", "foo")]
    public async Task НезнакомоеЗначениеФильтра_400(string param, string value)
    {
        using var h = new Harness("test-source", null);
        var resp = await h.Admin.GetAsync($"/api/local-model/history?{param}={value}");
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString()!
            .Should().NotBeNullOrWhiteSpace();
    }

    // Валидные значения фильтров — 200 (не 400). period — новый параметр.
    [Theory]
    [InlineData("kind", "image")]
    [InlineData("outcome", "completed")]
    [InlineData("period", "today")]
    public async Task ВалидноеЗначениеФильтра_200(string param, string value)
    {
        using var h = new Harness("test-source", null);
        var resp = await h.Admin.GetAsync($"/api/local-model/history?{param}={value}");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // Дефект e9a8de3b: period=today|24h|7d разбирается в окно from (раньше молча игнорировался,
    // фильтр шёл только через from/to). now фиксирован: today — локальный день, 24h/7d — назад от now.
    // Мутация: убрать разбор period — r_2d снова попадёт в today/24h, тест краснеет.
    [Fact]
    public async Task Period_ФильтруетПоВремени()
    {
        var now = new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);
        using var h = new Harness("test-source", null, new FixedTimeProvider(now));
        h.WriteRecordAt("r_today", "test-source", "2026-10-15T12:00:00Z", "2026-10-15"); // = now, сегодня
        h.WriteRecordAt("r_2d", "test-source", "2026-10-13T12:00:00Z", "2026-10-13");    // 2 дня назад

        // today: только r_today (r_2d — не сегодня)
        var today = await h.Admin.GetAsync("/api/local-model/history?period=today");
        today.StatusCode.Should().Be(HttpStatusCode.OK);
        Ids(await today.Content.ReadFromJsonAsync<JsonElement>()).Should().BeEquivalentTo("r_today");

        // 24h: только r_today (r_2d — вне 24 ч)
        var h24 = await h.Admin.GetAsync("/api/local-model/history?period=24h");
        h24.StatusCode.Should().Be(HttpStatusCode.OK);
        Ids(await h24.Content.ReadFromJsonAsync<JsonElement>()).Should().BeEquivalentTo("r_today");

        // 7d: обе записи (обе в пределах 7 дней)
        var d7 = await h.Admin.GetAsync("/api/local-model/history?period=7d");
        d7.StatusCode.Should().Be(HttpStatusCode.OK);
        Ids(await d7.Content.ReadFromJsonAsync<JsonElement>()).Should().BeEquivalentTo("r_today", "r_2d");
    }

    // id записей страницы (для сравнения набора, не порядка)
    private static List<string> Ids(JsonElement body) =>
        body.GetProperty("records").EnumerateArray().Select(r => r.GetProperty("id").GetString()!).ToList();
}
