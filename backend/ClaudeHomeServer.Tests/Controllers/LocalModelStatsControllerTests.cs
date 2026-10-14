using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Controllers;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Media;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ClaudeHomeServer.Tests.Controllers;

// REST-панель «Локальные модели» (вкладка «Текст»): авторизация (403/401),
// валидация window (400), since (только хвост), выключенная вертикаль (configured:false),
// unsupported из снимка.
public class LocalModelStatsControllerTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;
    private readonly HttpClient _admin;
    private readonly HttpClient _user;

    public LocalModelStatsControllerTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
        _admin = factory.CreateAuthenticatedClient();
        _user = factory.CreateAuthenticatedClient(
            TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);
    }

    // ─── Авторизация ─────────────────────────────────────────────────────────────

    // Не-админ (роль user) → 403. Мутация: убрать [Authorize(Roles="admin")] — тест краснеет.
    [Fact]
    public async Task НеАдмин_403()
    {
        var resp = await _user.GetAsync("/api/local-model/stats");
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // Аноним (без токена) → 401.
    [Fact]
    public async Task Аноним_401()
    {
        var anon = _factory.CreateClient();
        var resp = await anon.GetAsync("/api/local-model/stats");
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // Админ → 200 (даже если вертикаль включена и снимок пуст — configured может быть true/false).
    [Fact]
    public async Task Админ_200()
    {
        var resp = await _admin.GetAsync("/api/local-model/stats");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ─── Валидация window ─────────────────────────────────────────────────────────

    // window=bad (не число+m/h) → 400.
    [Fact]
    public async Task WindowBad_400()
    {
        var resp = await _admin.GetAsync("/api/local-model/stats?window=bad");
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // window=15m и window=1h — валидные → 200.
    [Theory]
    [InlineData("15m")]
    [InlineData("1h")]
    public async Task WindowВалидный_200(string window)
    {
        var resp = await _admin.GetAsync($"/api/local-model/stats?window={window}");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ─── since: только хвост ─────────────────────────────────────────────────────

    // since=<unix ms> — история отдаёт только точки новее этого момента.
    [Fact]
    public async Task Since_ОтдаётТолькоХвост()
    {
        // Фейковый reader: 4 точки по 5 с, t0..t0+15s. since = t0+10s → только 2 последние.
        var t0 = new DateTimeOffset(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);
        var points = new List<LocalModelStatsPoint>();
        for (var i = 0; i < 4; i++)
            points.Add(new LocalModelStatsPoint
            {
                Timestamp = t0.AddSeconds(5 * i),
                GenThroughput = 10.0 * i,
                Running = 0,
                Waiting = 0,
            });

        var reader = new FakeReader
        {
            Current = new LocalModelStatsCurrent { Configured = true, Status = "working" },
            Points = points,
        };
        var factory = BuildFactoryWithFakes(reader);
        using var client = factory.CreateAuthenticatedClient();

        var since = (long)t0.AddSeconds(10).ToUnixTimeMilliseconds();
        var resp = await client.GetAsync($"/api/local-model/stats?since={since}");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var history = body.GetProperty("history");
        // Точки t0+10s (i=2) и t0+15s (i=3) — 2 точки; t0, t0+5s — нет.
        history.GetArrayLength().Should().Be(2, "since фильтрует кольцо по моменту, не по окну");
    }

    // ─── history.t: unix-мс (long) ─────────────────────────────────────────────────

    // history[].t — long (unix-мс), не ISO-строка. Мутация: вернуть T как DateTimeOffset
    // (сериализуется ISO) → t в JSON станет строкой, тест краснеет.
    [Fact]
    public async Task History_TimeIsUnixMs()
    {
        var t0 = DateTimeOffset.UtcNow;
        var reader = new FakeReader
        {
            Current = new LocalModelStatsCurrent { Configured = true, Status = "working" },
            Points =
            [
                new LocalModelStatsPoint
                {
                    Timestamp = t0,
                    GenThroughput = 42.0,
                    Running = 1,
                    Waiting = 0,
                },
            ],
        };
        var factory = BuildFactoryWithFakes(reader);
        using var client = factory.CreateAuthenticatedClient();

        var resp = await client.GetAsync("/api/local-model/stats");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var history = body.GetProperty("history");
        history.GetArrayLength().Should().Be(1);
        var t = history[0].GetProperty("t");
        // t — число (long), не строка.
        t.ValueKind.Should().Be(JsonValueKind.Number, "t в wire — unix-мс (long), не ISO-строка");
        t.GetInt64().Should().Be(t0.ToUnixTimeMilliseconds());
    }

    // ─── speed: средняя скорость на поток за окно графика (avgGenTps/avgPromptTps) ─────

    // speed.avgGenTps/avgPromptTps — средняя скорость на поток за окно графика (15m/1h):
    // Δсчётчика/Δсуммы времени. Приходит из reader.GetAverageSpeed(window) и сериализуется
    // camelCase. Мутация: не передавать avg из reader в SpeedDto → поля null, тест краснеет.
    [Fact]
    public async Task Speed_AvgTps_Из_Окна()
    {
        var reader = new FakeReader
        {
            Current = new LocalModelStatsCurrent { Configured = true, Status = "working" },
            Average = new LocalModelStatsAverage { GenTps = 42.0, PromptTps = 1000.0 },
        };
        var factory = BuildFactoryWithFakes(reader);
        using var client = factory.CreateAuthenticatedClient();

        var resp = await client.GetAsync("/api/local-model/stats?window=15m");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("speed").GetProperty("avgGenTps").GetDouble().Should().Be(42.0);
        body.GetProperty("speed").GetProperty("avgPromptTps").GetDouble().Should().Be(1000.0);
    }

    // ─── chats: phase — required string ─────────────────────────────────────────────

    // Пустая активная сессия → chats: []. ChatDto.Phase — required string (default "unknown"):
    // если шаг 19 начнёт наполнять chats, phase не будет null. Тест минимальный:
    // проверяет, что DTO по умолчанию даёт "unknown" (required string, не null).
    [Fact]
    public void Chats_UnknownPhaseWhenEmpty()
    {
        // DTO: Phase — non-nullable string, default "unknown".
        var dto = new LocalModelStatsController.ChatDto { Own = true };
        dto.Phase.Should().Be("unknown", "Phase — required string; без явного значения → unknown");
    }

    // ─── chats: анонимизация и othersCount (шаг 19) ───────────────────────────────

    // Свой чат — id/проект/название; чужой — «Чат другого пользователя», id/проект null.
    // Мутация: отдать чужой sessionId (или чужой title) — тест краснеет.
    [Fact]
    public async Task Chats_АнонимизацияЧужих()
    {
        var meter = new FakeMeter();
        var reader = new FakeReader
        {
            Current = new LocalModelStatsCurrent { Configured = true, Status = "working", Running = 2, Waiting = 0 },
        };
        var factory = BuildFactoryWithFakes(reader, meter);
        using var admin = factory.CreateAuthenticatedClient();

        // OwnerId в grant — user.Id (sub в JWT), не username; id генерируется на фабрику,
        // поэтому берём токены из ТОЙ ЖЕ фабрики, что и запрос.
        var myId = SubOf(factory.GetToken(
            TestWebApplicationFactory.TestUsername, TestWebApplicationFactory.TestPassword));
        var otherId = SubOf(factory.GetToken(
            TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword));
        meter.Chats =
        [
            new TurnChatLive("sess-mine", myId, "proj-1", "Мой чат", 42.0, "generating"),
            new TurnChatLive("sess-other", otherId, "proj-2", "Чат второго", 17.5, "queued"),
        ];

        var resp = await admin.GetAsync("/api/local-model/stats");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var chats = body.GetProperty("chats");
        chats.GetArrayLength().Should().Be(2);

        var mine = chats[0];
        mine.GetProperty("own").GetBoolean().Should().BeTrue();
        mine.GetProperty("sessionId").GetString().Should().Be("sess-mine");
        mine.GetProperty("projectId").GetString().Should().Be("proj-1");
        mine.GetProperty("title").GetString().Should().Be("Мой чат");

        var other = chats[1];
        other.GetProperty("own").GetBoolean().Should().BeFalse();
        other.GetProperty("sessionId").ValueKind.Should().Be(JsonValueKind.Null, "чужой id наружу не уходит");
        other.GetProperty("projectId").ValueKind.Should().Be(JsonValueKind.Null, "чужой проект наружу не уходит");
        other.GetProperty("title").GetString().Should().Be("Чат другого пользователя");
    }

    // OwnerId — внутренний: в JSON чата его нет. Мутация: добавить ownerId в ChatDto — тест краснеет.
    [Fact]
    public async Task Chats_OwnerId_НеУтекает()
    {
        var meter = new FakeMeter
        {
            Chats = [new TurnChatLive("sess-1", "testuser", null, "Чат", null, "unknown")],
        };
        var reader = new FakeReader
        {
            Current = new LocalModelStatsCurrent { Configured = true, Status = "working" },
        };
        var factory = BuildFactoryWithFakes(reader, meter);
        using var admin = factory.CreateAuthenticatedClient();

        var resp = await admin.GetAsync("/api/local-model/stats");
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var chat = body.GetProperty("chats")[0];
        chat.TryGetProperty("ownerId", out _).Should().BeFalse("OwnerId — признак анонимизации, наружу не отдаём");
    }

    // othersCount = max(0, running + waiting − живых чатов): прогоны, которые счётчик не видит.
    // Мутация: вычесть running+waiting наоборот (active − running) — тест краснеет.
    [Fact]
    public async Task OthersCount_Формула()
    {
        var meter = new FakeMeter
        {
            Chats = [new TurnChatLive("sess-1", "testuser", null, "Чат", null, "generating")],
        };
        var reader = new FakeReader
        {
            Current = new LocalModelStatsCurrent { Configured = true, Status = "working", Running = 3, Waiting = 1 },
        };
        var factory = BuildFactoryWithFakes(reader, meter);
        using var admin = factory.CreateAuthenticatedClient();

        var resp = await admin.GetAsync("/api/local-model/stats");
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        // 3 + 1 − 1 живой чат = 3 «других» прогона.
        body.GetProperty("othersCount").GetInt32().Should().Be(3);
    }

    // Счётчик хода не в контейнере (вертикаль Llm выключена) → chats: [].
    [Fact]
    public async Task Chats_СчётчикВыключен_Пусто()
    {
        using var factory = new TestWebApplicationFactory
        {
            ExtraConfig = { ["Subsystems:llm:Enabled"] = "false" },
        };
        using var admin = factory.CreateAuthenticatedClient();
        var resp = await admin.GetAsync("/api/local-model/stats");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("chats").GetArrayLength().Should().Be(0, "без счётчика хода чатов нет");
    }

    // ─── unsupported из снимка ───────────────────────────────────────────────────

    // unsupported из снимка Current проходит в ответ без искажений.
    [Fact]
    public async Task Unsupported_ИзСнимка()
    {
        var reader = new FakeReader
        {
            Current = new LocalModelStatsCurrent
            {
                Configured = true,
                Status = "working",
                Unsupported = ["vllm:kv_cache_usage_perc", "vllm:ram_tier_hits"],
            },
        };
        var factory = BuildFactoryWithFakes(reader);
        using var client = factory.CreateAuthenticatedClient();

        var resp = await client.GetAsync("/api/local-model/stats");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("unsupported").EnumerateArray().Select(s => s.GetString()!)
            .Should().Equal(new[] { "vllm:kv_cache_usage_perc", "vllm:ram_tier_hits" });
    }

    // ─── Выключенная вертикаль Llm → configured:false, status:notConnected, 200 ───

    // Subsystems:llm:Enabled=false — ILocalModelStatsReader/IGpuSampler не в контейнере;
    // контроллер через GetService видит null → {configured:false, status:notConnected} (200, не 500).
    [Fact]
    public async Task ВертикальВыключена_ConfiguredFalse()
    {
        using var factory = new TestWebApplicationFactory
        {
            ExtraConfig = { ["Subsystems:llm:Enabled"] = "false" },
        };
        using var admin = factory.CreateAuthenticatedClient();
        var resp = await admin.GetAsync("/api/local-model/stats");
        resp.StatusCode.Should().Be(HttpStatusCode.OK, "выключенная вертикаль — 200, а не 500");
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("configured").GetBoolean().Should().BeFalse();
        body.GetProperty("status").GetString().Should().Be("notConnected");
    }

    // ─── Медиа: GET /api/local-model/stats/media ─────────────────────────────────

    // Не-админ (роль user) → 403. Мутация: убрать [Authorize(Roles="admin")] — тест краснеет.
    [Fact]
    public async Task Media_НеАдмин_403()
    {
        var resp = await _user.GetAsync("/api/local-model/stats/media");
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // Медиа-стенд не настроен (IComfyStats отдаёт снимок Configured:false, status:disabled)
    // → 200 {configured:false}, а не 500. Мутация: бросать при не настроенном / GetRequiredService
    // — 500, тест краснеет. (В тесте — фейк: реальный ComfyStats в Testing не строится,
    // т.к. его зависимость ComfyQueueMonitor — gated hosted, выключенный в тестовой среде.)
    [Fact]
    public async Task Media_НеНастроен_ConfiguredFalse()
    {
        var fake = new FakeComfyStats
        {
            Snapshot = new ComfyMediaStatsSnapshot(
                Configured: false,
                Status: "disabled",
                DownSince: null,
                Version: null,
                LoadedModels: [],
                SlotsUsed: 0,
                SlotsLimit: 4,
                Gpus: [],
                Running: [],
                Queue: [],
                LastHour: [],
                Unsupported: []),
        };
        var factory = BuildFactoryWithComfyStats(fake);
        using var admin = factory.CreateAuthenticatedClient();
        var resp = await admin.GetAsync("/api/local-model/stats/media");
        resp.StatusCode.Should().Be(HttpStatusCode.OK, "не настроенный медиа-стенд — 200, а не 500");
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("configured").GetBoolean().Should().BeFalse();
        body.GetProperty("comfy").GetProperty("status").GetString().Should().Be("disabled");
    }

    // Анонимизация чужих прогонов: свой — own=true, sessionId/projectId отдаются;
    // чужой — own=false, sessionId/projectId null, title «Чат другого пользователя».
    // Мутация: отдать чужой sessionId (или title) — тест краснеет.
    [Fact]
    public async Task Media_АнонимизацияЧужих()
    {
        var fake = new FakeComfyStats();
        var factory = BuildFactoryWithComfyStats(fake);
        using var admin = factory.CreateAuthenticatedClient();

        // Ids из ТОЙ ЖЕ фабрики, что и запрос: sub в JWT генерируется на фабрику,
        // поэтому OwnerId в строках должен совпадать с sub этого же хоста.
        var myId = SubOf(factory.GetToken(TestWebApplicationFactory.TestUsername, TestWebApplicationFactory.TestPassword));
        var otherId = SubOf(factory.GetToken(TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword));
        var t0 = new DateTimeOffset(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

        // Снимок заполняется ПОСЛЕ получения id: фейк — singleton, контроллер читает его при запросе.
        fake.Snapshot = new ComfyMediaStatsSnapshot(
            Configured: true,
            Status: "working",
            DownSince: null,
            Version: "1.0",
            LoadedModels: [],
            SlotsUsed: 2,
            SlotsLimit: 4,
            Gpus: [],
            Running:
            [
                new ComfyJobRow("image", "t2i", false, myId, "sess-mine", "proj-1", null, null, 40, true, 120, 80, null, t0),
                new ComfyJobRow("video", "t2v", true, otherId, "sess-other", "proj-2", null, null, null, false, 600, null, null, t0),
            ],
            Queue:
            [
                new ComfyJobRow("audio", "tts", false, otherId, "sess-q", "proj-q", "instance", null, null, false, 90, null, 30, null),
            ],
            LastHour:
            [
                new ComfyDoneRow("image", "t2i", otherId, "completed", 60, 120, t0),
            ],
            Unsupported: []);

        var resp = await admin.GetAsync("/api/local-model/stats/media");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();

        // running[0] — свой: sessionId/projectId отдаются, own=true.
        var mine = body.GetProperty("running")[0];
        mine.GetProperty("own").GetBoolean().Should().BeTrue();
        mine.GetProperty("sessionId").GetString().Should().Be("sess-mine");
        mine.GetProperty("projectId").GetString().Should().Be("proj-1");

        // running[1] — чужой: sessionId/projectId null, title «Чат другого пользователя».
        var other = body.GetProperty("running")[1];
        other.GetProperty("own").GetBoolean().Should().BeFalse();
        other.GetProperty("sessionId").ValueKind.Should().Be(JsonValueKind.Null, "чужой id наружу не уходит");
        other.GetProperty("projectId").ValueKind.Should().Be(JsonValueKind.Null, "чужой проект наружу не уходит");
        other.GetProperty("title").GetString().Should().Be("Чат другого пользователя");

        // queue[0] — чужой (instance): position=1, sessionId null, title «Чат другого пользователя».
        var q = body.GetProperty("queue")[0];
        q.GetProperty("position").GetInt32().Should().Be(1);
        q.GetProperty("own").GetBoolean().Should().BeFalse();
        q.GetProperty("sessionId").ValueKind.Should().Be(JsonValueKind.Null);
        q.GetProperty("title").GetString().Should().Be("Чат другого пользователя");
        q.GetProperty("foreign").GetString().Should().Be("instance");

        // lastHour[0] — чужой: own=false.
        var done = body.GetProperty("lastHour")[0];
        done.GetProperty("own").GetBoolean().Should().BeFalse();
        done.GetProperty("outcome").GetString().Should().Be("completed");
    }

    // OwnerId — внутренний: в JSON строк running/queue/lastHour его нет.
    // Мутация: добавить ownerId в любой из DTO — тест краснеет.
    [Fact]
    public async Task Media_OwnerId_НеУтекает()
    {
        var fake = new FakeComfyStats();
        var factory = BuildFactoryWithComfyStats(fake);
        using var admin = factory.CreateAuthenticatedClient();

        var otherId = SubOf(factory.GetToken(TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword));
        var t0 = new DateTimeOffset(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

        fake.Snapshot = new ComfyMediaStatsSnapshot(
            Configured: true,
            Status: "working",
            DownSince: null,
            Version: null,
            LoadedModels: [],
            SlotsUsed: 1,
            SlotsLimit: 4,
            Gpus: [],
            Running:
            [
                new ComfyJobRow("image", "t2i", false, otherId, "sess-x", "proj-x", null, null, null, false, 100, null, null, t0),
            ],
            Queue:
            [
                new ComfyJobRow("video", "t2v", false, otherId, "sess-y", "proj-y", "external", null, null, false, 200, null, 10, null),
            ],
            LastHour:
            [
                new ComfyDoneRow("audio", "tts", otherId, "failed", 30, 90, t0),
            ],
            Unsupported: []);

        var resp = await admin.GetAsync("/api/local-model/stats/media");
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();

        // Ни в одной строке running/queue/lastHour ownerId наружу не уходит.
        body.GetProperty("running")[0].TryGetProperty("ownerId", out _).Should().BeFalse("ownerId в running наружу не уходит");
        body.GetProperty("queue")[0].TryGetProperty("ownerId", out _).Should().BeFalse("ownerId в queue наружу не уходит");
        body.GetProperty("lastHour")[0].TryGetProperty("ownerId", out _).Should().BeFalse("ownerId в lastHour наружу не уходит");
    }

    // since=<unix ms> — lastHour отдаёт только завершённые новее момента (как хвост в «Текст»).
    [Fact]
    public async Task Media_Since_ФильтруетLastHour()
    {
        var fake = new FakeComfyStats();
        var factory = BuildFactoryWithComfyStats(fake);
        using var admin = factory.CreateAuthenticatedClient();

        var myId = SubOf(factory.GetToken(TestWebApplicationFactory.TestUsername, TestWebApplicationFactory.TestPassword));
        var t0 = new DateTimeOffset(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

        fake.Snapshot = new ComfyMediaStatsSnapshot(
            Configured: true,
            Status: "working",
            DownSince: null,
            Version: null,
            LoadedModels: [],
            SlotsUsed: 0,
            SlotsLimit: 4,
            Gpus: [],
            Running: [],
            Queue: [],
            LastHour:
            [
                new ComfyDoneRow("image", "t2i", myId, "completed", 60, 120, t0),
                new ComfyDoneRow("video", "t2v", myId, "failed", 300, 600, t0.AddSeconds(30)),
            ],
            Unsupported: []);

        // since = t0+15s → только t0+30s (1 запись); t0 — нет.
        var since = (long)t0.AddSeconds(15).ToUnixTimeMilliseconds();
        var resp = await admin.GetAsync($"/api/local-model/stats/media?since={since}");
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("lastHour").GetArrayLength().Should().Be(1, "since фильтрует lastHour по моменту, не по окну");
    }

    // ─── Медиа: gpus + loadedModels из узла (маршрут /ccs/stats, шаг 31) ───────────────

    // Узел сообщает: gpus (roles:media) + loadedModels в ответе
    [Fact]
    public async Task Media_Gpus_И_LoadedModels_Из_Узла()
    {
        var fake = new FakeComfyStats();
        var factory = BuildFactoryWithComfyStats(fake);
        using var admin = factory.CreateAuthenticatedClient();

        fake.Snapshot = new ComfyMediaStatsSnapshot(
            Configured: true,
            Status: "working",
            DownSince: null,
            Version: "1.0",
            Gpus: [new ComfyGpuInfo(0, "", "NVIDIA GeForce RTX 3090", 97, 21874, 24576, 71, 338, 350)],
            LoadedModels: ["qwen_image.safetensors", "AutoencoderKL"],
            SlotsUsed: 1,
            SlotsLimit: 4,
            Running: [],
            Queue: [],
            LastHour: [],
            Unsupported: []);

        var resp = await admin.GetAsync("/api/local-model/stats/media");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();

        // gpus: одна карта, roles ["media"], данные узла
        var gpus = body.GetProperty("gpus");
        gpus.GetArrayLength().Should().Be(1);
        gpus[0].GetProperty("roles").GetArrayLength().Should().Be(1);
        gpus[0].GetProperty("roles")[0].GetString().Should().Be("media");
        gpus[0].GetProperty("name").GetString().Should().Be("NVIDIA GeForce RTX 3090");
        gpus[0].GetProperty("index").GetInt32().Should().Be(0);
        gpus[0].GetProperty("utilPerc").GetInt32().Should().Be(97);
        gpus[0].GetProperty("memUsedMb").GetInt64().Should().Be(21874);

        // loadedModels: имена моделей
        var loaded = body.GetProperty("loadedModels");
        loaded.GetArrayLength().Should().Be(2);
        loaded[0].GetString().Should().Be("qwen_image.safetensors");
        loaded[1].GetString().Should().Be("AutoencoderKL");
    }

    // Узел не сообщает (gpus/loadedModels пустые): gpus null, loadedModels null
    [Fact]
    public async Task Media_БезДанных_Узла_GpusNull()
    {
        var fake = new FakeComfyStats();
        var factory = BuildFactoryWithComfyStats(fake);
        using var admin = factory.CreateAuthenticatedClient();

        fake.Snapshot = new ComfyMediaStatsSnapshot(
            Configured: true,
            Status: "working",
            DownSince: null,
            Version: "1.0",
            Gpus: [],
            LoadedModels: [],
            SlotsUsed: 0,
            SlotsLimit: 4,
            Running: [],
            Queue: [],
            LastHour: [],
            Unsupported: ["узел не сообщает загруженные модели"]);

        var resp = await admin.GetAsync("/api/local-model/stats/media");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();

        // gpus null (не пустой массив): узел не сообщает
        body.GetProperty("gpus").ValueKind.Should().Be(JsonValueKind.Null, "нет данных узла — gpus null");
        body.GetProperty("loadedModels").ValueKind.Should().Be(JsonValueKind.Null, "нет данных узла — loadedModels null");
        // пометка «не сообщает» проходит в unsupported
        body.GetProperty("unsupported").EnumerateArray().Select(e => e.GetString()!)
            .Should().Contain("узел не сообщает загруженные модели");
    }

    // ─── Хелперы ─────────────────────────────────────────────────────────────────

    // Фейковый ILocalModelStatsReader: тест задаёт Current, Points и Average;
    // GetHistory фильтрует по since, GetAverageSpeed отдаёт заданное среднее.
    private sealed class FakeReader : ILocalModelStatsReader
    {
        public LocalModelStatsCurrent Current { get; init; } = new() { Status = "notConnected", Configured = false };
        public List<LocalModelStatsPoint> Points { get; init; } = [];
        public LocalModelStatsAverage Average { get; set; } = new();

        public IReadOnlyList<LocalModelStatsPoint> GetHistory(TimeSpan window, long? sinceUnixMs = null)
        {
            var now = DateTimeOffset.UtcNow;
            var since = sinceUnixMs is long ms
                ? DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime
                : (now - window).UtcDateTime;
            return Points.Where(p => p.Timestamp >= since).ToList();
        }

        public LocalModelStatsAverage GetAverageSpeed(TimeSpan window) => Average;
    }

    // Фейковый IGpuSampler: null — нет nvidia-smi (блок не показывать).
    private sealed class NoGpuSampler : IGpuSampler
    {
        public Task<GpuSample?> ReadAsync(CancellationToken ct = default) => Task.FromResult<GpuSample?>(null);
    }

    // sub в JWT = user.Id (не username): декодируем payload токена.
    private static string SubOf(string token)
    {
        var payload = token.Split('.')[1].Replace('-', '+').Replace('_', '/');
        payload += new string('=', (4 - payload.Length % 4) % 4);
        var json = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(payload));
        return JsonSerializer.Deserialize<JsonElement>(json).GetProperty("sub").GetString()!;
    }

    // Фейковый ILocalModelTurnMeter: управляемый список живых чатов (settable — тест
    // заполняет его после старта фабрики, когда известен user.Id).
    private sealed class FakeMeter : ILocalModelTurnMeter
    {
        public List<TurnChatLive> Chats { get; set; } = [];
        public IReadOnlyList<TurnChatLive> ActiveChats() => Chats;
        public LocalModelTurnSummary? GetLastTurn(string sessionId) => null;
    }

    // Фабрика с подменой ILocalModelStatsReader/IGpuSampler/ILocalModelTurnMeter на фейки
    // (ExtraServices — после встроенных регистраций, перетирает forwarder подсистемы Llm).
    private static TestWebApplicationFactory BuildFactoryWithFakes(FakeReader reader, ILocalModelTurnMeter? meter = null)
    {
        var factory = new TestWebApplicationFactory();
        factory.ExtraServices = services =>
        {
            services.AddSingleton<ILocalModelStatsReader>(reader);
            services.AddSingleton<IGpuSampler>(new NoGpuSampler());
            if (meter is not null)
                services.AddSingleton(meter);
        };
        return factory;
    }

    // Фейковый IComfyStats: управляемый снимок (тест задаёт ComfyMediaStatsSnapshot).
    private sealed class FakeComfyStats : IComfyStats
    {
        public ComfyMediaStatsSnapshot? Snapshot { get; set; }
        public Task<ComfyMediaStatsSnapshot?> GetMediaAsync(CancellationToken ct) => Task.FromResult(Snapshot);
    }

    // Фабрика с подменой IComfyStats на фейк: ExtraServices идёт после встроенных регистраций,
    // поэтому перетирает ComfyStats подсистемы images (GetService<IComfyStats> → фейк).
    private static TestWebApplicationFactory BuildFactoryWithComfyStats(FakeComfyStats comfy)
    {
        var factory = new TestWebApplicationFactory();
        factory.ExtraServices = services => services.AddSingleton<IComfyStats>(comfy);
        return factory;
    }
}
