using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Media;
using ClaudeHomeServer.Services.MediaHistory;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ClaudeHomeServer.Tests.Controllers;

// История медиа-генераций (ADR-030, шаг 3): GET /api/local-model/history и .../{id}/file.
// Обе ручки — [Authorize(Roles="admin")] (не-админ — 403) плюс серверный флаг local-model-stats
// (без него — 404). Файл — только по id записи и номеру варианта; 410 — файла нет (reason).
[Trait("Category", "Host")]
public class LocalModelStatsHistoryTests : IClassFixture<LocalModelStatsHistoryTests.Hosts>
{
    private readonly Hosts _hosts;

    public LocalModelStatsHistoryTests(Hosts hosts) => _hosts = hosts;

    // Хост на КОНФИГУРАЦИЮ, а не на тест: конфиг, часы и старт процесса — свойства хоста, а
    // журнал, источник результатов и флаг тест заводит сам (Harness → HistoryHost.Use). Хосты
    // поднимаются лениво и гасятся вместе с фикстурой
    public sealed class Hosts : IDisposable
    {
        public HistoryHost Main { get; } = new();
        // Часы и старт процесса — управляемые: старт индекса фиксирован 2026-10-15 12:00 UTC
        public HistoryHost Clock { get; } = new(processStart: new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero));
        public HistoryHost MediaOff { get; } = new(config: ("LocalModelStats:MediaForUsers", "false"));
        // Несуществующий ffmpeg: генерация миниатюры гарантированно не удаётся
        public HistoryHost NoFfmpeg { get; } = new(config: ("AudioDsp:FfmpegPath",
            Path.Combine(Path.GetTempPath(), "no-such-ffmpeg-" + Guid.NewGuid().ToString("N"))));

        public void Dispose()
        {
            Main.Dispose();
            Clock.Dispose();
            MediaOff.Dispose();
            NoFfmpeg.Dispose();
        }
    }

    // Часы хоста Clock: «сейчас» задаёт тест; null — настоящее время
    private sealed class SwitchableTime : TimeProvider
    {
        public DateTimeOffset? Now { get; set; }
        public override DateTimeOffset GetUtcNow() => Now ?? base.GetUtcNow();
    }

    // Источник результатов в DI один; что он отвечает, решает тест
    private sealed class SwitchableSource : IMediaHistoryResultSource
    {
        public FakeHistorySource Target { get; set; } = new();
        public string Source => Target.Source;
        public Task<MediaHistoryResolution?> ResolveAsync(MediaHistoryRef reference, int variant, CancellationToken ct)
            => Target.ResolveAsync(reference, variant, ct);
    }

    public sealed class HistoryHost : TestWebApplicationFactory
    {
        private readonly SwitchableSource _source = new();
        private readonly SwitchableTime? _time;

        public HistoryHost(DateTimeOffset? processStart = null, params (string Key, string Value)[] config)
        {
            foreach (var (key, value) in config) ExtraConfig[key] = value;
            if (processStart is not null) _time = new SwitchableTime();
            ExtraServices = s =>
            {
                s.AddSingleton<IMediaHistoryResultSource>(_source);
                if (_time is null) return;
                s.AddSingleton<TimeProvider>(_time);
                s.AddSingleton<MediaHistoryIndex>(sp => new MediaHistoryIndex(
                    sp.GetRequiredService<IConfiguration>(),
                    sp.GetRequiredService<UserStore>(),
                    sp.GetService<TimeProvider>(),
                    processStart));
            };
        }

        // Чистое состояние теста: журнал и индекс пусты, источник и «сейчас» — как просит тест,
        // флаг local-model-stats у админа включён
        internal void Use(FakeHistorySource fake, DateTimeOffset? now)
        {
            _source.Target = fake;
            if (_time is not null) _time.Now = now;
            else if (now is not null) throw new InvalidOperationException("Часы задаются только на хосте Clock");

            var index = Services.GetRequiredService<MediaHistoryIndex>();
            index.Dispose(); // очищает записи, но не снимает признак «загружено»
            typeof(MediaHistoryIndex).GetField("_loaded", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .SetValue(index, false);
            if (Directory.Exists(index.Dir))
            {
                foreach (var file in Directory.GetFiles(index.Dir, "*.jsonl")) File.Delete(file);
                var thumbs = Path.Combine(index.Dir, "thumbs");
                if (Directory.Exists(thumbs)) Directory.Delete(thumbs, true);
            }

            var users = Services.GetRequiredService<UserStore>();
            users.SetFeatureFlag(users.FindByUsername(TestUsername)!.Id, "local-model-stats", true);
        }
    }

    // Управляемый источник: тест задаёт Source и поведение Resolve (410-причины, число вариантов)
    public sealed class FakeHistorySource : IMediaHistoryResultSource
    {
        public string Source { get; set; } = "test-source";
        public Func<MediaHistoryRef, int, MediaHistoryResolution?>? Resolve { get; set; }
        public Task<MediaHistoryResolution?> ResolveAsync(MediaHistoryRef reference, int variant, CancellationToken ct)
            => Task.FromResult(Resolve?.Invoke(reference, variant));
    }

    // Файл-результат во временном каталоге: резолвер истории отдаёт путь, который тест создал
    // сам (платформонезависимость по CLAUDE.md: жёсткий /tmp/x.png красит тест на машине, где
    // его нет). Файл удаляется в Dispose — тест не оставляет мусора.
    private sealed class TempFile : IDisposable
    {
        public string FilePath { get; } = Path.Combine(
            Path.GetTempPath(), "lms-history-" + Guid.NewGuid().ToString("N") + ".png");
        public TempFile() => File.WriteAllText(FilePath, "png");
        public void Dispose() => File.Delete(FilePath);
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
        // Реальный id не-админа (seconduser): для тестов изоляции «только свои записи»
        public string NonAdminId { get; }

        public Harness(HistoryHost host, string source, Func<MediaHistoryRef, int, MediaHistoryResolution?>? resolve,
            DateTimeOffset? now = null)
        {
            Fake = new FakeHistorySource { Source = source, Resolve = resolve };
            Factory = host;
            host.Use(Fake, now);
            var users = Factory.Services.GetRequiredService<UserStore>();
            AdminId = users.FindByUsername(TestWebApplicationFactory.TestUsername)!.Id;
            NonAdminId = users.FindByUsername(TestWebApplicationFactory.SecondUsername)!.Id;
            Admin = Factory.CreateAuthenticatedClient();
            NonAdmin = Factory.CreateAuthenticatedClient(TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);
        }

        // Запись в журнал: строка v:1 (enqueued + finished) с заданным origin.
        // ownerId — владелец (по умолчанию админ); для тестов изоляции — id не-админа.
        public void WriteRecord(string promptId, string source, string? model = "sdxl", string? op = "generate_image",
            string? ownerId = null)
        {
            var dir = Path.Combine(Factory.TempDir, "local-media-history");
            Directory.CreateDirectory(dir);
            var day = DateTime.UtcNow.ToString("yyyy-MM-dd");
            var origin = new LocalJobOrigin(
                OwnerId: ownerId ?? AdminId, SessionId: "chat-1", Kind: "image", Op: op ?? "generate_image",
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

        // Запись «прерванная»: enqueued + started, но БЕЗ finished (процесс умер).
        // ts — время обоих событий; fileDay — дата файла журнала.
        public void WriteBareRunning(string promptId, string source, string ts, string fileDay)
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
            var started = new JsonObject
            {
                ["v"] = 1, ["t"] = "started", ["ts"] = ts,
                ["id"] = promptId, ["origin"] = originNode2,
                ["model"] = null, ["prompt"] = null, ["params"] = null, ["card"] = null,
                ["outcome"] = null, ["runSeconds"] = null, ["error"] = null,
            };
            File.AppendAllText(Path.Combine(dir, fileDay + ".jsonl"),
                enqueued.ToJsonString() + "\n" + started.ToJsonString() + "\n");
        }

        // Хост общий на класс — гасит его фикстура; здесь только клиенты теста
        public void Dispose()
        {
            Admin.Dispose();
            NonAdmin.Dispose();
        }
    }

    // Не-админ (seconduser, роль user) — 200 (ADR-030, 2026-10-18: история — любой
    // аутентифицированный), scope "self". Мутация: вернуть [Authorize(Roles="admin")] — 403,
    // тест краснеет.
    [Fact]
    public async Task НеАдмин_200_ScopeSelf()
    {
        using var h = new Harness(_hosts.Main, "test-source", null);
        var resp = await h.NonAdmin.GetAsync("/api/local-model/history");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("scope").GetString().Should().Be("self");
    }

    // ─── Изоляция не-админа (ADR-030, 2026-10-18) ───────────────────────────────────

    // Не-админ видит только свои записи (OwnerId == sub на уровне индекса): чужие не попадают.
    // Админ — все. Мутация: не фильтровать по OwnerId — чужая запись просочится, тест краснеет.
    [Fact]
    public async Task НеАдмин_ТолькоСвоиЗаписи()
    {
        using var h = new Harness(_hosts.Main, "test-source", null);
        h.WriteRecord("p_admin", "test-source");                       // админа
        h.WriteRecord("p_nonadmin", "test-source", ownerId: h.NonAdminId); // не-админа

        // Не-админ: только своя (1), scope self.
        var respUser = await h.NonAdmin.GetAsync("/api/local-model/history");
        respUser.StatusCode.Should().Be(HttpStatusCode.OK);
        var bodyUser = await respUser.Content.ReadFromJsonAsync<JsonElement>();
        bodyUser.GetProperty("scope").GetString().Should().Be("self");
        Ids(bodyUser).Should().BeEquivalentTo("p_nonadmin");

        // Админ: обе (2), scope all.
        var respAdmin = await h.Admin.GetAsync("/api/local-model/history");
        respAdmin.StatusCode.Should().Be(HttpStatusCode.OK);
        var bodyAdmin = await respAdmin.Content.ReadFromJsonAsync<JsonElement>();
        bodyAdmin.GetProperty("scope").GetString().Should().Be("all");
        Ids(bodyAdmin).Should().BeEquivalentTo("p_admin", "p_nonadmin");
    }

    // Не-админ: логин владельца в своих записях не раскрывается (owner null); админ видит логин.
    // Мутация: отдавать логин не-админу — тест краснеет.
    [Fact]
    public async Task НеАдмин_ЛогинНеРаскрывается()
    {
        using var h = new Harness(_hosts.Main, "test-source", null);
        h.WriteRecord("p_nonadmin", "test-source", ownerId: h.NonAdminId);

        // Не-админ: owner null (своя запись, логин не раскрываем).
        var respUser = await h.NonAdmin.GetAsync("/api/local-model/history");
        var bodyUser = await respUser.Content.ReadFromJsonAsync<JsonElement>();
        bodyUser.GetProperty("records")[0].GetProperty("owner").ValueKind
            .Should().Be(JsonValueKind.Null, "не-админ: логин владельца не раскрывается");

        // Админ: owner = логин не-админа (seconduser).
        var respAdmin = await h.Admin.GetAsync("/api/local-model/history");
        var bodyAdmin = await respAdmin.Content.ReadFromJsonAsync<JsonElement>();
        bodyAdmin.GetProperty("records")[0].GetProperty("owner").GetString()
            .Should().Be(TestWebApplicationFactory.SecondUsername);
    }

    // Не-админ: параметр login=чужой не расширяет выдачу (фильтр — только по OwnerId).
    // Мутация: учитывать login у не-админа — чужая запись просочится, тест краснеет.
    [Fact]
    public async Task НеАдмин_LoginЧужой_НеРасширяет()
    {
        using var h = new Harness(_hosts.Main, "test-source", null);
        h.WriteRecord("p_admin", "test-source");                        // админа (login=testuser)
        h.WriteRecord("p_nonadmin", "test-source", ownerId: h.NonAdminId); // не-админа

        // Не-админ с login=testuser (логин админа): всё равно только своя запись.
        var resp = await h.NonAdmin.GetAsync("/api/local-model/history?login=" + TestWebApplicationFactory.TestUsername);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        // login не расширяет выдачу не-админа: только своя запись
        Ids(body).Should().BeEquivalentTo("p_nonadmin");
    }

    // Не-админ: чужая запись (id) → 404 (не 403 — не подтверждаем существование чужой).
    // Мутация: 403 вместо 404 (или не проверять владельца) — тест краснеет.
    [Fact]
    public async Task НеАдмин_ЧужойId_404()
    {
        using var file = new TempFile();
        using var h = new Harness(_hosts.Main, "test-source", (r, n) => new MediaHistoryResolution(1, file.FilePath, "image/png", null));
        // Обе записи ДО первого запроса: индекс грузится один раз (Load идемпотентна).
        h.WriteRecord("p_admin", "test-source");                        // чужая (админа)
        h.WriteRecord("p_nonadmin", "test-source", ownerId: h.NonAdminId); // своя

        // Не-админ просит файл чужой (админской) записи → 404 (не 403).
        var resp = await h.NonAdmin.GetAsync("/api/local-model/history/p_admin/file");
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound, "чужая запись — 404, не 403");

        // Своя запись → 200 (файл отдаётся).
        var respOwn = await h.NonAdmin.GetAsync("/api/local-model/history/p_nonadmin/file");
        respOwn.StatusCode.Should().Be(HttpStatusCode.OK, "своя запись — 200");
    }

    // ─── fail-closed: пустой владелец не расширяет выдачу (ADR-030, 2026-10-18) ─────────

    // Индекс: scope "self" с пустым владельцем — ни одной записи (не «все»). В индексе
    // записи двух владельцев и Foreign (OwnerId ""). Мутация: убрать guard null/IsNullOrWhiteSpace
    // в OwnerMatches — записи просочатся, тест краснеет.
    [Fact]
    public void Index_ScopeSelf_ПустойВладелец_Пусто()
    {
        using var h = new Harness(_hosts.Main, "test-source", null);
        h.WriteRecord("p_a", "test-source", ownerId: h.AdminId);
        h.WriteRecord("p_b", "test-source", ownerId: h.NonAdminId);
        h.WriteRecord("p_foreign", "test-source", ownerId: "");

        var index = h.Factory.Services.GetRequiredService<MediaHistoryIndex>();
        index.Load();

        // null-владелец (scope self) — не «все», а пусто
        index.Query(new MediaHistoryQuery { Scope = "self", OwnerId = null }).Records
            .Should().BeEmpty("scope self без непустого владельца — ни одной записи");
        // пустой/пробельный владелец — пусто (Foreign с OwnerId "" не совпадает)
        index.Query(new MediaHistoryQuery { Scope = "self", OwnerId = "" }).Records
            .Should().BeEmpty();
        index.Query(new MediaHistoryQuery { Scope = "self", OwnerId = " " }).Records
            .Should().BeEmpty();
        // админ (scope all, владелец null) — все три записи
        index.Query(new MediaHistoryQuery { Scope = "all", OwnerId = null }).Records
            .Should().HaveCount(3);
    }

    // Рубильник MediaForUsers=false: не-админ → 404 (как будто функции нет), админ → 200.
    // Мутация: не проверять рубильник для не-админа — 200, тест краснеет.
    [Fact]
    public async Task MediaForUsersFalse_НеАдмин404_Админ200()
    {
        using var h = new Harness(_hosts.MediaOff, "test-source", null);
        h.WriteRecord("p_nonadmin", "test-source", ownerId: h.NonAdminId);

        var respUser = await h.NonAdmin.GetAsync("/api/local-model/history");
        respUser.StatusCode.Should().Be(HttpStatusCode.NotFound, "не-админ без MediaForUsers — 404");

        var respAdmin = await h.Admin.GetAsync("/api/local-model/history");
        respAdmin.StatusCode.Should().Be(HttpStatusCode.OK, "админ не зависит от MediaForUsers");
        var body = await respAdmin.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("scope").GetString().Should().Be("all");
    }

    // Админ без флага local-model-stats — 404. Мутация: убрать серверную проверку флага — краснеет
    [Fact]
    public async Task АдминБезФлага_404()
    {
        using var h = new Harness(_hosts.Main, "test-source", null);
        // Флаг не включён (как у пользователя без override)
        var users = h.Factory.Services.GetRequiredService<UserStore>();
        users.SetFeatureFlag(h.AdminId, "local-model-stats", false);

        var resp = await h.Admin.GetAsync("/api/local-model/history");
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // recordId с «..» — 404 (произвольное чтение диска не раскрываем). Мутация: убрать проверку — краснеет
    [Fact]
    public async Task RecordIdСДвумяТочками_404()
    {
        using var file = new TempFile();
        using var h = new Harness(_hosts.Main, "test-source", (r, n) => new MediaHistoryResolution(1, file.FilePath, "image/png", null));
        var resp = await h.Admin.GetAsync("/api/local-model/history/a..b/file");
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // Неверный n (записи 1 вариант, просим 5) — 404. Мутация: убрать проверку n — краснеет
    [Fact]
    public async Task НеверныйN_404()
    {
        using var file = new TempFile();
        using var h = new Harness(_hosts.Main, "test-source", (r, n) => new MediaHistoryResolution(1, file.FilePath, "image/png", null));
        h.WriteRecord("p1", "test-source");
        var resp = await h.Admin.GetAsync("/api/local-model/history/p1/file?n=5");
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // Дефект 8fccc75f: size=thumb при неудачной генерации — 404, а не оригинал.
    // Файл-оригинал настоящий (TempFile), ffmpeg намеренно битый (несуществующий FfmpegPath —
    // Process.Start кидает Win32Exception, сервис возвращает null). Контроль: size=full
    // отдаёт оригинал как раньше. Мутация «вернуть откат на оригинал» (старый код):
    // size=thumb даёт 200 с полным файлом — тест краснеет
    [Fact]
    public async Task ThumbНеСделалась_404_АНеОригинал()
    {
        using var file = new TempFile();
        using var h = new Harness(_hosts.NoFfmpeg, "test-source",
            (r, n) => new MediaHistoryResolution(1, file.FilePath, "image/png", null));
        h.WriteRecord("p1", "test-source");

        var resp = await h.Admin.GetAsync("/api/local-model/history/p1/file?size=thumb");
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound, "миниатюры нет и сделать её не удалось — 404, не оригинал");

        // контроль: full — оригинал отдаётся как раньше
        var full = await h.Admin.GetAsync("/api/local-model/history/p1/file?size=full");
        full.StatusCode.Should().Be(HttpStatusCode.OK);
        (await full.Content.ReadAsByteArrayAsync()).Should().Equal(File.ReadAllBytes(file.FilePath));
    }

    // 410 missing: источник говорит, что файла нет. Мутация: перепутать причину — краснеет
    [Fact]
    public async Task Missing_410()
    {
        using var h = new Harness(_hosts.Main, "test-source", (r, n) => new MediaHistoryResolution(1, null, null, MediaHistoryGoneReason.Missing));
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
        using var h = new Harness(_hosts.Main, "test-source", (r, n) => new MediaHistoryResolution(1, null, null, MediaHistoryGoneReason.Expired));
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
        using var h = new Harness(_hosts.Main, "test-source", null);
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
        using var h = new Harness(_hosts.Main, "test-source", null);
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
        using var h = new Harness(_hosts.Main, "test-source", null);
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
        using var h = new Harness(_hosts.Main, "test-source", null);
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
        using var h = new Harness(_hosts.Main, "test-source", null);
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
        using (var h = new Harness(_hosts.Main, "test-source",
            (r, n) => new MediaHistoryResolution(0, null, null, MediaHistoryGoneReason.NoResult)))
        {
            h.WriteRecord("p1", "test-source");
            var resp = await h.Admin.GetAsync("/api/local-model/history/p1/file?n=0");
            resp.StatusCode.Should().Be(HttpStatusCode.Gone);
            (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("reason").GetString()
                .Should().Be("no_result");
        }

        // missing: файл удалён (VariantCount==0)
        using (var h = new Harness(_hosts.Main, "test-source",
            (r, n) => new MediaHistoryResolution(0, null, null, MediaHistoryGoneReason.Missing)))
        {
            h.WriteRecord("p2", "test-source");
            var resp = await h.Admin.GetAsync("/api/local-model/history/p2/file?n=0");
            resp.StatusCode.Should().Be(HttpStatusCode.Gone);
            (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("reason").GetString()
                .Should().Be("missing");
        }

        // available: Reason==null, 1 вариант; неверный n=5 → 404
        using (var file = new TempFile())
        using (var h = new Harness(_hosts.Main, "test-source",
            (r, n) => new MediaHistoryResolution(1, file.FilePath, "image/png", null)))
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
        using var h = new Harness(_hosts.Main, "test-source", null);
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
        using var h = new Harness(_hosts.Main, "test-source", null);
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
        using var h = new Harness(_hosts.Clock, "test-source", null, now);
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

    // Блок 2 (ADR-030): запись без finished, чьё последнее событие старше старта процесса → interrupted.
    // Живой ход (после старта) остаётся running. Мутация: убрать проверку ProcessStart — interrupted
    // превращается в running, тест краснеет.
    // Дефект 477415dc: «now» фиксирован в Harness, а не взят из реальных часов — 7-дневное окно
    // индекса отсчитывается от него. Без этого записи 2026-10-15 выпадали из окна, и тест краснел
    // с 22.10 (мутация: вернуть time: null — records пуст, тест краснеет).
    [Fact]
    public async Task ПрерванныйХод_ПослеРестарта_Interrupted()
    {
        var now = new DateTimeOffset(2026, 10, 15, 13, 0, 0, TimeSpan.Zero);
        // Старт процесса — 2026-10-15 12:00 UTC, задан хосту Clock
        using var h = new Harness(_hosts.Clock, "test-source", null, now);

        // До рестарта: enqueued+started в 10:00, finished НЕ записан → interrupted
        h.WriteBareRunning("p_old", "test-source", "2026-10-15T10:00:00Z", "2026-10-15");
        // Живой ход: enqueued+started в 12:30 (после старта) → running
        h.WriteBareRunning("p_live", "test-source", "2026-10-15T12:30:00Z", "2026-10-15");

        var resp = await h.Admin.GetAsync("/api/local-model/history");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var records = body.GetProperty("records");
        records.GetArrayLength().Should().Be(2);

        var byId = records.EnumerateArray().ToDictionary(r => r.GetProperty("id").GetString()!);
        byId["p_old"].GetProperty("status").GetString().Should().Be("interrupted");
        byId["p_live"].GetProperty("status").GetString().Should().Be("running");
    }

    // id записей страницы (для сравнения набора, не порядка)
    private static List<string> Ids(JsonElement body) =>
        body.GetProperty("records").EnumerateArray().Select(r => r.GetProperty("id").GetString()!).ToList();
}
