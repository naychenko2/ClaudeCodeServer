using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Controllers;
using ClaudeHomeServer.Services;
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

    // window=30m — не в белом списке (5m/15m/1h) → 400. Мутация: вернуть приём любого <n>m
    // (старое поведение) — 30m снова 200, тест краснеет.
    [Fact]
    public async Task Window30m_400()
    {
        var resp = await _admin.GetAsync("/api/local-model/stats?window=30m");
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // window=1m/5m/15m/1h — валидные → 200 (1m — окно графика, v2.4).
    [Theory]
    [InlineData("1m")]
    [InlineData("5m")]
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

    // history: серии кэша (v2.1) — шесть полей сериализуются camelCase:
    // kvUsage (доля 0..1), prefixHits/prefixQueries/ramHits/ramQueries/preemptions (приращения).
    // Мутация: не маппить поле из LocalModelStatsPoint в HistoryPointDto → null в JSON, тест краснеет.
    [Fact]
    public async Task History_СерииКэша_Сериализуются()
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
                    KvUsage = 0.87,
                    PrefixHits = 1234,
                    PrefixQueries = 5000,
                    RamHits = 77,
                    RamQueries = 900,
                    Preemptions = 3,
                },
            ],
        };
        var factory = BuildFactoryWithFakes(reader);
        using var client = factory.CreateAuthenticatedClient();

        var resp = await client.GetAsync("/api/local-model/stats");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var p = body.GetProperty("history")[0];

        p.GetProperty("kvUsage").GetDouble().Should().Be(0.87);
        p.GetProperty("prefixHits").GetInt64().Should().Be(1234);
        p.GetProperty("prefixQueries").GetInt64().Should().Be(5000);
        p.GetProperty("ramHits").GetInt64().Should().Be(77);
        p.GetProperty("ramQueries").GetInt64().Should().Be(900);
        p.GetProperty("preemptions").GetInt64().Should().Be(3);
    }

    // history: скорость на поток (вторая линия графика) — genTokens/itlSeconds сериализуются
    // camelCase. Мутация: не маппить поле из LocalModelStatsPoint в HistoryPointDto → null в JSON,
    // тест краснеет.
    [Fact]
    public async Task History_СкоростьНаПоток_Сериализуются()
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
                    GenTokens = 600,
                    ItlSeconds = 10.5,
                },
            ],
        };
        var factory = BuildFactoryWithFakes(reader);
        using var client = factory.CreateAuthenticatedClient();

        var resp = await client.GetAsync("/api/local-model/stats");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var p = body.GetProperty("history")[0];

        p.GetProperty("genTokens").GetInt64().Should().Be(600);
        p.GetProperty("itlSeconds").GetDouble().Should().Be(10.5);
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

    // ─── gpus: ВСЕ карты хоста (ADR-029 п.7) ─────────────────────────────────────────

    // Сэмплер отдаёт 2 карты → gpus в ответе содержит ОБЕ (index 0 и 1).
    // Мутация «маппить только samples[0]» (одну карту) → gpus.length 1, тест краснеет.
    [Fact]
    public async Task Gpus_AllCards_FromSampler()
    {
        var reader = new FakeReader
        {
            Current = new LocalModelStatsCurrent { Configured = true, Status = "working" },
        };
        var gpus = new FakeGpuSampler
        {
            Samples =
            [
                new GpuSample { Index = 0, Uuid = "GPU-uuid-1", Name = "NVIDIA GeForce RTX 3090", Utilization = 99, MemoryUsed = 24000, MemoryTotal = 24576, Temperature = 71, PowerDraw = 338, PowerLimit = 350 },
                new GpuSample { Index = 1, Uuid = "GPU-uuid-2", Name = "NVIDIA GeForce RTX 3090", Utilization = 100, MemoryUsed = 24100, MemoryTotal = 24576, Temperature = 72, PowerDraw = 340, PowerLimit = 350 },
            ],
        };
        var factory = BuildFactoryWithFakes(reader, gpuSampler: gpus);
        using var admin = factory.CreateAuthenticatedClient();

        var resp = await admin.GetAsync("/api/local-model/stats");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();

        var g = body.GetProperty("gpus");
        g.GetArrayLength().Should().Be(2, "обе карты хоста, не только первая");
        g[0].GetProperty("index").GetInt32().Should().Be(0);
        g[0].GetProperty("utilPerc").GetInt32().Should().Be(99);
        g[1].GetProperty("index").GetInt32().Should().Be(1);
        g[1].GetProperty("utilPerc").GetInt32().Should().Be(100);
    }

    // Сэмплер отдаёт пустой список (нет nvidia-smi) → gpus null (без блока), а не пустой массив.
    [Fact]
    public async Task Gpus_EmptySampler_IsNull()
    {
        var reader = new FakeReader
        {
            Current = new LocalModelStatsCurrent { Configured = true, Status = "working" },
        };
        var factory = BuildFactoryWithFakes(reader, gpuSampler: new FakeGpuSampler { Samples = [] });
        using var admin = factory.CreateAuthenticatedClient();

        var resp = await admin.GetAsync("/api/local-model/stats");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("gpus").ValueKind.Should().Be(JsonValueKind.Null, "нет карт — gpus null (без блока)");
    }

    // ─── tensorParallel: из секции Stats провайдера ───────────────────────────────────

    // Stats:TensorParallel задано (2) → tensorParallel в ответе = 2.
    // Мутация: не передавать Current.TensorParallel в ответ → null, тест краснеет.
    [Fact]
    public async Task TensorParallel_FromConfig()
    {
        var reader = new FakeReader
        {
            Current = new LocalModelStatsCurrent { Configured = true, Status = "working", TensorParallel = 2 },
        };
        var factory = BuildFactoryWithFakes(reader);
        using var admin = factory.CreateAuthenticatedClient();

        var resp = await admin.GetAsync("/api/local-model/stats");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("tensorParallel").GetInt32().Should().Be(2);
    }

    // Stats:TensorParallel не задано (null) → tensorParallel null (фронт покажет число карт).
    [Fact]
    public async Task TensorParallel_NotSet_IsNull()
    {
        var reader = new FakeReader
        {
            Current = new LocalModelStatsCurrent { Configured = true, Status = "working" },
        };
        var factory = BuildFactoryWithFakes(reader);
        using var admin = factory.CreateAuthenticatedClient();

        var resp = await admin.GetAsync("/api/local-model/stats");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("tensorParallel").ValueKind.Should().Be(JsonValueKind.Null, "не задано → null, фронт покажет число карт");
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

    // Не-админ (роль user) → 200 (ADR-030, 2026-10-18: «Медиа» — любой аутентифицированный),
    // scope "self". Мутация: вернуть [Authorize(Roles="admin")] на /stats/media — 403, тест краснеет.
    [Fact]
    public async Task Media_НеАдмин_200_ScopeSelf()
    {
        var resp = await _user.GetAsync("/api/local-model/stats/media");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("scope").GetString().Should().Be("self");
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

    // ─── Медиа: изоляция не-админа (ADR-030, 2026-10-18) ─────────────────────────────

    // Снимок с двумя владельцами: myId (testuser/админ) и otherId (seconduser/не-админ).
    // running: по одному своему/чужому; queue: 4 строки (2 чужих, 2 своих, чередуясь);
    // lastHour: по одной. Не-админ — otherId.
    private static ComfyMediaStatsSnapshot TwoOwnerSnapshot(string myId, string otherId)
    {
        var t0 = new DateTimeOffset(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);
        return new ComfyMediaStatsSnapshot(
            Configured: true,
            Status: "working",
            DownSince: null,
            Version: "1.0",
            Gpus: [new ComfyGpuInfo(0, "", "RTX 3090", 97, 21874, 24576, 71, 338, 350)],
            LoadedModels: ["qwen_image.safetensors"],
            SlotsUsed: 2,
            SlotsLimit: 4,
            Running:
            [
                new ComfyJobRow("image", "t2i", false, otherId, "sess-mine", "proj-1", null, null, 40, true, 120, 80, null, t0),
                new ComfyJobRow("video", "t2v", true, myId, "sess-other", "proj-2", null, null, null, false, 600, null, null, t0),
            ],
            Queue:
            [
                new ComfyJobRow("audio", "tts", false, myId, "sess-a1", "proj-a1", null, null, null, false, 90, null, 30, null),
                new ComfyJobRow("image", "t2i", false, otherId, "sess-a2", "proj-a2", null, null, null, false, 90, null, 30, null),
                new ComfyJobRow("video", "t2v", false, myId, "sess-a3", "proj-a3", null, null, null, false, 90, null, 30, null),
                new ComfyJobRow("image", "t2i", false, otherId, "sess-a4", "proj-a4", null, null, null, false, 90, null, 30, null),
            ],
            LastHour:
            [
                new ComfyDoneRow("image", "t2i", otherId, "completed", 60, 120, t0),
                new ComfyDoneRow("video", "t2v", myId, "failed", 300, 600, t0),
            ],
            Unsupported: []);
    }

    // Не-админ видит только свои строки (OwnerId == sub, не Foreign): чужие (running/queue/lastHour)
    // не попадают в ответ вовсе. Мутация: не фильтровать по OwnerId/Foreign — чужая строка
    // просочится, тест краснеет.
    [Fact]
    public async Task Media_НеАдмин_ТолькоСвоиСтроки()
    {
        var fake = new FakeComfyStats();
        var factory = BuildFactoryWithComfyStats(fake);
        using var nonAdmin = factory.CreateAuthenticatedClient(
            TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);

        var myId = SubOf(factory.GetToken(TestWebApplicationFactory.TestUsername, TestWebApplicationFactory.TestPassword));
        var otherId = SubOf(factory.GetToken(TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword));
        fake.Snapshot = TwoOwnerSnapshot(myId, otherId);

        var resp = await nonAdmin.GetAsync("/api/local-model/stats/media");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();

        // running: только своя (1 строка), own=true; чужая (admin) не попала.
        var running = body.GetProperty("running");
        running.GetArrayLength().Should().Be(1, "не-админ видит только свои прогоны");
        running[0].GetProperty("own").GetBoolean().Should().BeTrue();
        running[0].GetProperty("sessionId").GetString().Should().Be("sess-mine");

        // queue: только свои (2 строки), позиции — в ПОЛНОЙ очереди (2 и 4), не перенумерованные.
        var queue = body.GetProperty("queue");
        queue.GetArrayLength().Should().Be(2, "не-админ видит только свои задачи в очереди");
        queue[0].GetProperty("position").GetInt32().Should().Be(2, "позиция в полной очереди, не в отфильтрованном");
        queue[1].GetProperty("position").GetInt32().Should().Be(4);
        queue[0].GetProperty("sessionId").GetString().Should().Be("sess-a2");

        // lastHour: только свой (1 строка), own=true; чужая (admin) не попала.
        var lastHour = body.GetProperty("lastHour");
        lastHour.GetArrayLength().Should().Be(1, "не-админ видит только свои завершённые");
        lastHour[0].GetProperty("own").GetBoolean().Should().BeTrue();
        lastHour[0].GetProperty("outcome").GetString().Should().Be("completed");
    }

    // Не-админ: gpus null (скрыты), queueAhead — по ПОЛНОЙ очереди (1 задача перед первой своей).
    // Мутация: gpus отдавать не-админу (или queueAhead считать по отфильтрованному) — тест краснеет.
    [Fact]
    public async Task Media_НеАдмин_GpusNull_И_QueueAhead()
    {
        var fake = new FakeComfyStats();
        var factory = BuildFactoryWithComfyStats(fake);
        using var nonAdmin = factory.CreateAuthenticatedClient(
            TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);

        var myId = SubOf(factory.GetToken(TestWebApplicationFactory.TestUsername, TestWebApplicationFactory.TestPassword));
        var otherId = SubOf(factory.GetToken(TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword));
        fake.Snapshot = TwoOwnerSnapshot(myId, otherId);

        var resp = await nonAdmin.GetAsync("/api/local-model/stats/media");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();

        // gpus скрыты у не-админа (null, не пустой массив).
        body.GetProperty("gpus").ValueKind.Should().Be(JsonValueKind.Null, "gpus у не-админа — null");
        // queueAhead: первая своя задача в полной очереди — позиция 2, перед ней 1 (чужая a1).
        body.GetProperty("queueAhead").GetInt32().Should().Be(1, "задач перед первой своей в полной очереди");
        // loadedModels/slots/comfy — как есть (не скрыты).
        body.GetProperty("loadedModels").GetArrayLength().Should().Be(1);
        body.GetProperty("slots").GetProperty("used").GetInt32().Should().Be(2);
    }

    // Админ: scope "all", gpus на месте, ВСЕ строки (свои и чужие), queueAhead по своим.
    // Мутация: scope "self" у админа (или фильтровать чужие) — тест краснеет.
    [Fact]
    public async Task Media_Админ_ScopeAll_ВсеСтроки()
    {
        var fake = new FakeComfyStats();
        var factory = BuildFactoryWithComfyStats(fake);
        using var admin = factory.CreateAuthenticatedClient();

        var myId = SubOf(factory.GetToken(TestWebApplicationFactory.TestUsername, TestWebApplicationFactory.TestPassword));
        var otherId = SubOf(factory.GetToken(TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword));
        fake.Snapshot = TwoOwnerSnapshot(myId, otherId);

        var resp = await admin.GetAsync("/api/local-model/stats/media");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();

        body.GetProperty("scope").GetString().Should().Be("all");
        // gpus на месте (1 карта).
        body.GetProperty("gpus").GetArrayLength().Should().Be(1);
        // ВСЕ строки: running 2, queue 4, lastHour 2.
        body.GetProperty("running").GetArrayLength().Should().Be(2);
        body.GetProperty("queue").GetArrayLength().Should().Be(4);
        body.GetProperty("lastHour").GetArrayLength().Should().Be(2);
        // queueAhead: первая СВОЯ (admin) задача в полной очереди — позиция 1, перед ней 0.
        body.GetProperty("queueAhead").GetInt32().Should().Be(0);
    }

    // Рубильник MediaForUsers=false: не-админ → 404 (как будто функции нет), админ работает (200).
    // Мутация: не проверять рубильник для не-админа — 200, тест краснеет.
    [Fact]
    public async Task Media_MediaForUsersFalse_НеАдмин404_Админ200()
    {
        var fake = new FakeComfyStats();
        var factory = BuildFactoryWithComfyStats(fake);
        factory.ExtraConfig["LocalModelStats:MediaForUsers"] = "false";
        using var nonAdmin = factory.CreateAuthenticatedClient(
            TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);
        using var admin = factory.CreateAuthenticatedClient();

        var myId = SubOf(factory.GetToken(TestWebApplicationFactory.TestUsername, TestWebApplicationFactory.TestPassword));
        var otherId = SubOf(factory.GetToken(TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword));
        fake.Snapshot = TwoOwnerSnapshot(myId, otherId);

        var respUser = await nonAdmin.GetAsync("/api/local-model/stats/media");
        respUser.StatusCode.Should().Be(HttpStatusCode.NotFound, "не-админ без MediaForUsers — 404");

        var respAdmin = await admin.GetAsync("/api/local-model/stats/media");
        respAdmin.StatusCode.Should().Be(HttpStatusCode.OK, "админ не зависит от MediaForUsers");
        var body = await respAdmin.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("scope").GetString().Should().Be("all");
    }

    // ─── Второй стенд (Б3c): регрессия основного ответа ─────────────────────────────────

    // Снапшот снят на коде ДО Б3c (временным StatsSnapshotCapture, та же фикстура): ответ
    // без engine не должен меняться ни байтом, когда появляются параметр engine и блок duty.
    // Мутация: снять [JsonIgnore(WhenWritingNull)] с Duty в DTO — в JSON появится "duty":null,
    // тест краснеет.
    [Fact]
    public async Task StatsБезEngine_СнапшотJSON()
    {
        var t0 = new DateTimeOffset(2026, 10, 28, 11, 0, 0, TimeSpan.Zero);
        var reader = new FakeReader
        {
            Current = new LocalModelStatsCurrent
            {
                Configured = true, Engine = "vllm", Model = "qwen35-9b", Status = "busy",
                DownSince = t0, Running = 2, Waiting = 1, SpeculativeSpeedup = 3.3, CleanStepMs = 58.0,
                Kv = new LocalModelStatsKv
                {
                    UsagePerc = 0.61, RamTierPerc = 0.39, PrefixHitPerc = 0.87, RamTierHitPerc = 0.52,
                    Preemptions = 7, GenPerStream = 38.0, PrefillPerStream = 640.0,
                },
                Unsupported = ["vllm:request_queue_time_seconds_sum"],
                TensorParallel = 2,
            },
            Points =
            [
                new LocalModelStatsPoint
                {
                    Timestamp = t0, GenThroughput = 42.0, Running = 2, Waiting = 1, KvUsage = 0.61,
                    PrefixHits = 1234, PrefixQueries = 5000, RamHits = 77, RamQueries = 900, Preemptions = 3,
                    GenTokens = 600, ItlSeconds = 10.5, ItlCount = 120, PrefillSeconds = 2.5, QueueSeconds = 1.5, PromptTokens = 900,
                },
            ],
        };
        var factory = BuildFactoryWithFakes(reader, gpuSampler: new FakeGpuSampler
        {
            Samples = [new GpuSample { Index = 0, Uuid = "GPU-1", Name = "NVIDIA GeForce RTX 3090", Utilization = 99, MemoryUsed = 24000, MemoryTotal = 24576, Temperature = 71, PowerDraw = 338, PowerLimit = 350 }],
        });
        using var admin = factory.CreateAuthenticatedClient();
        var since = (long)t0.AddSeconds(-60).ToUnixTimeMilliseconds();
        var body = await (await admin.GetAsync($"/api/local-model/stats?since={since}")).Content.ReadAsStringAsync();

        body.Should().Be("""{"configured":true,"engine":"vllm","model":"qwen35-9b","tensorParallel":2,"status":"busy","downSince":1793185200000,"running":2,"waiting":1,"othersCount":3,"speed":{"genPerStream":38,"prefillPerStream":640,"speculativeSpeedup":3.3},"cleanStepMs":58,"kv":{"usagePerc":0.61,"ramTierPerc":0.39,"prefixHitPerc":0.87,"ramTierHitPerc":0.52,"preemptions15m":7},"gpus":[{"index":0,"name":"NVIDIA GeForce RTX 3090","utilPerc":99,"memUsedMb":24000,"memTotalMb":24576,"tempC":71,"powerW":338,"powerLimitW":350}],"history":[{"t":1793185200000,"genThroughput":42,"waiting":1,"running":2,"kvUsage":0.61,"prefixHits":1234,"prefixQueries":5000,"ramHits":77,"ramQueries":900,"preemptions":3,"genTokens":600,"itlSeconds":10.5,"itlCount":120,"cleanStepMs":58,"prefillSeconds":2.5,"queueSeconds":1.5,"promptTokens":900}],"chats":[],"unsupported":["vllm:request_queue_time_seconds_sum"],"hardware":{"level":"ok","gpus":[]}}""");
    }

    // ─── Второй стенд (Б3c): GET /api/local-model/engines ───────────────────────────────

    // Роль на действии: не-админ → 403. Мутация: снять [Authorize(Roles="admin")] — 200, краснеет.
    [Fact]
    public async Task Engines_НеАдмин_403()
    {
        var factory = BonsaiFactory(new FakeDirectory());
        using var user = factory.CreateAuthenticatedClient(
            TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);
        (await user.GetAsync("/api/local-model/engines")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // Серверный флаг local-model-stats: без него 404 (образец — hardware), даже админу.
    // Мутация: убрать проверку флага — 200, тест краснеет.
    [Fact]
    public async Task Engines_БезФлага_404()
    {
        var factory = BonsaiFactory(new FakeDirectory(), flag: false);
        using var admin = factory.CreateAuthenticatedClient();
        (await admin.GetAsync("/api/local-model/engines")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // Список для переключателя: main — всегда первым; известный движок с дорожкой — configured.
    // «Bonsai без vLLM тоже configured» (Ф6): main не настроен, bonsai — настроен.
    // Мутация: не отдавать title/main — первые assert'ы краснеют.
    [Fact]
    public async Task Engines_СписокДвижков_MainИBonsai()
    {
        var dir = new FakeDirectory();
        dir.Currents["main"] = new LocalModelStatsCurrent { Configured = false, Status = "notConnected" };
        dir.Currents["bonsai"] = new LocalModelStatsCurrent { Configured = true, Engine = "llama-server", Status = "working" };
        var factory = BonsaiFactory(dir, mainConfigured: false);
        using var admin = factory.CreateAuthenticatedClient();

        var body = await (await admin.GetAsync("/api/local-model/engines")).Content.ReadFromJsonAsync<JsonElement>();
        var list = body.EnumerateArray().ToList();
        list.Count.Should().Be(2, "main + известный bonsai");

        list[0].GetProperty("name").GetString().Should().Be("main");
        list[0].GetProperty("title").GetString().Should().Be("Основная");
        list[0].GetProperty("configured").GetBoolean().Should().BeFalse("vLLM не настроен");
        list[1].GetProperty("name").GetString().Should().Be("bonsai");
        list[1].GetProperty("title").GetString().Should().Be("Bonsai", "подпись сегмента — ключ с заглавной");
        list[1].GetProperty("configured").GetBoolean().Should().BeTrue("Bonsai без vLLM тоже configured");
    }

    // Условие ревью 1 (R-F1, «Хвосты»): известный, но не настроенный движок — запись с
    // configured:false, а не отсутствие в списке. Фронт трактует пустой список как «ещё не
    // загружен» и держит сохранённый выбор вечно. Мутация: не включать бездорожный движок
    // в список — list.Count 1, тест краснеет.
    [Fact]
    public async Task Engines_ИзвестныйБезДорожки_ConfiguredFalse()
    {
        var dir = new FakeDirectory();
        dir.Currents["main"] = new LocalModelStatsCurrent { Configured = true, Status = "working" };
        var factory = BonsaiFactory(dir, mainConfigured: true, metricsUrl: false);
        using var admin = factory.CreateAuthenticatedClient();

        var body = await (await admin.GetAsync("/api/local-model/engines")).Content.ReadFromJsonAsync<JsonElement>();
        var list = body.EnumerateArray().ToList();
        list.Count.Should().Be(2);
        list[1].GetProperty("name").GetString().Should().Be("bonsai");
        list[1].GetProperty("configured").GetBoolean().Should().BeFalse("нет дорожки статистики — не настроен");
    }

    // «Ничего не настроено» — НЕ пустой список: {main, configured:false} (R-F1: пустой список
    // фронт держит как «не загружен» навсегда). Мутация: отдавать [] при пустом словаре — краснеет.
    [Fact]
    public async Task Engines_НичегоНеНастроено_НеПустойСписок()
    {
        var factory = BonsaiFactory(new FakeDirectory(), engineInConfig: false);
        using var admin = factory.CreateAuthenticatedClient();

        var body = await (await admin.GetAsync("/api/local-model/engines")).Content.ReadFromJsonAsync<JsonElement>();
        var list = body.EnumerateArray().ToList();
        list.Count.Should().Be(1, "main присутствует всегда");
        list[0].GetProperty("name").GetString().Should().Be("main");
        list[0].GetProperty("configured").GetBoolean().Should().BeFalse();
    }

    // Выключенная вертикаль Llm: engines отвечает без Llm-зависимостей — {main:false}, 200 (не 500).
    [Fact]
    public async Task Engines_LlmВыключена_НеПустойСписок()
    {
        var factory = new TestWebApplicationFactory();
        factory.ExtraConfig["Subsystems:llm:Enabled"] = "false";
        SetFlag(factory, true);
        using var admin = factory.CreateAuthenticatedClient();

        var resp = await admin.GetAsync("/api/local-model/engines");
        resp.StatusCode.Should().Be(HttpStatusCode.OK, "выключенная Llm — 200, не 500");
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var list = body.EnumerateArray().ToList();
        list.Count.Should().Be(1);
        list[0].GetProperty("name").GetString().Should().Be("main");
        list[0].GetProperty("configured").GetBoolean().Should().BeFalse();
    }

    // ─── Второй стенд (Б3c): GET /api/local-model/stats?engine= ──────────────────────────

    // Роль на действии, как у /stats без engine: не-админ → 403.
    [Fact]
    public async Task StatsEngine_НеАдмин_403()
    {
        var dir = new FakeDirectory();
        dir.Currents["bonsai"] = new LocalModelStatsCurrent { Configured = true, Engine = "llama-server", Status = "working" };
        var factory = BonsaiFactory(dir);
        using var user = factory.CreateAuthenticatedClient(
            TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);
        (await user.GetAsync("/api/local-model/stats?engine=bonsai")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // Флаг для новой формы ручки: без него 404. Без engine флаг не проверялся и не проверяется
    // (регрессионная guard: Админ_200 выше работает без флага).
    [Fact]
    public async Task StatsEngine_БезФлага_404()
    {
        var factory = BonsaiFactory(new FakeDirectory(), flag: false);
        using var admin = factory.CreateAuthenticatedClient();
        (await admin.GetAsync("/api/local-model/stats?engine=bonsai")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // Неизвестный движок — 200 {configured:false, status:notConnected}, не 404/500; блока duty нет.
    [Fact]
    public async Task StatsEngine_НеизвестныйДвижок_ConfiguredFalse()
    {
        var factory = BonsaiFactory(new FakeDirectory());
        using var admin = factory.CreateAuthenticatedClient();

        var resp = await admin.GetAsync("/api/local-model/stats?engine=nope");
        resp.StatusCode.Should().Be(HttpStatusCode.OK, "неизвестный движок — configured:false, не 500");
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("configured").GetBoolean().Should().BeFalse();
        body.GetProperty("status").GetString().Should().Be("notConnected");
        body.TryGetProperty("duty", out _).Should().BeFalse("нет дорожки — нет блока «Дежурство»");
    }

    // Выключенная Llm: тот же ответ, что для неизвестного движка (200, не 500).
    [Fact]
    public async Task StatsEngine_LlmВыключена_ConfiguredFalse()
    {
        var factory = new TestWebApplicationFactory();
        factory.ExtraConfig["Subsystems:llm:Enabled"] = "false";
        SetFlag(factory, true);
        using var admin = factory.CreateAuthenticatedClient();

        var resp = await admin.GetAsync("/api/local-model/stats?engine=bonsai");
        resp.StatusCode.Should().Be(HttpStatusCode.OK, "выключенная Llm — 200, не 500");
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("configured").GetBoolean().Should().BeFalse();
        body.GetProperty("status").GetString().Should().Be("notConnected");
    }

    // Стенд именованного движка целиком: снимок дорожки (engine — тип движка, не имя стенда),
    // карта Video из узла ComfyUI, чатов и hardware нет, unsupported — единый словарь (имена
    // полей снимка) проходит как есть, блок «Дежурство» — цепочка с insurance (у движка есть
    // FallbackRoute), арбитр из клиента, причины разбиты на kind/raw, «comfy» — вид по шву
    // Images с known=true, ответы часа — из реального журнала вызовов мест.
    [Fact]
    public async Task StatsEngine_Bonsai_СтенСБлокомДежурство()
    {
        var t0 = new DateTimeOffset(2026, 10, 28, 11, 0, 0, TimeSpan.Zero);
        var dir = new FakeDirectory();
        dir.Currents["bonsai"] = new LocalModelStatsCurrent
        {
            Configured = true, Engine = "llama-server", Model = "bonsai2-27b", Status = "busy",
            Running = 1, Waiting = 0,
            Kv = new LocalModelStatsKv { GenPerStream = 21.0, PrefillPerStream = 640.0 },
            Unsupported = ["KvUsagePerc", "Preemptions"],
        };
        dir.BonsaiPoints.Add(new LocalModelStatsPoint { Timestamp = t0, GenThroughput = 21.0, Running = 1, Waiting = 0 });

        var arbiter = new FakeArbiterReader();
        arbiter.Snapshots["bonsai"] = new EngineArbiterSnapshot(
            Reachable: true, Bonsai: "yielded",
            Reasons: ["comfy:a1b2c3d4", "lease:съёмка хода", "gpu:4217:llama-server --metrics"],
            SinceAt: t0, Yields: 14, Returns: 13, LastYieldS: 0.2, LastReturnS: 6.1,
            Mode: "shared", QuietLeftS: null,
            Leases: [new EngineArbiterLease("съёмка хода", 37.5)],
            SeenAt: t0);

        var factory = BonsaiFactory(dir, arbiter, new KindComfyStats());
        var ledger = factory.Services.GetRequiredService<EngineCallLedger>();
        var done = ledger.Begin("bonsai", "chat-retitle")!;
        done.Step(EngineCallLink.Insurance);
        done.End(EngineCallOutcome.Insurance); // закрыт: ушёл из «сейчас» в корзину часа

        using var admin = factory.CreateAuthenticatedClient();
        var resp = await admin.GetAsync($"/api/local-model/stats?engine=bonsai&since={(long)t0.AddSeconds(-60).ToUnixTimeMilliseconds()}");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();

        body.GetProperty("configured").GetBoolean().Should().BeTrue();
        body.GetProperty("engine").GetString().Should().Be("llama-server", "engine — тип движка, не имя стенда");
        body.GetProperty("model").GetString().Should().Be("bonsai2-27b");
        body.GetProperty("status").GetString().Should().Be("busy");
        body.GetProperty("running").GetInt32().Should().Be(1);
        // Счётчик хода не видит вызовов мест (они мимо шлюза): «прочие» — всё, что сообщает движок.
        body.GetProperty("othersCount").GetInt32().Should().Be(1);
        body.GetProperty("chats").GetArrayLength().Should().Be(0, "у именованного движка нет чатов");
        body.TryGetProperty("hardware", out _).Should().BeFalse("сводка «Видеокарты» хоста не относится к стенду Video");
        body.GetProperty("unsupported").EnumerateArray().Select(s => s.GetString()!)
            .Should().Equal(new[] { "KvUsagePerc", "Preemptions" }, "единый словарь сокрытия — имена полей снимка");

        var gpu = body.GetProperty("gpus")[0];
        gpu.GetProperty("index").GetInt32().Should().Be(0);
        gpu.GetProperty("name").GetString().Should().Be("NVIDIA GeForce RTX 3090");
        gpu.GetProperty("utilPerc").GetInt32().Should().Be(34);
        gpu.GetProperty("memUsedMb").GetInt64().Should().Be(8192);

        var duty = body.GetProperty("duty");
        duty.GetProperty("chain").EnumerateArray().Select(s => s.GetString()!)
            .Should().Equal(new[] { "bonsai", "insurance", "claude" }, "цепочка типичная: own → страховка (есть FallbackRoute) → облако");

        var ar = duty.GetProperty("arbiter");
        ar.GetProperty("reachable").GetBoolean().Should().BeTrue();
        ar.GetProperty("state").GetString().Should().Be("yielded");
        ar.GetProperty("mode").GetString().Should().Be("shared");
        ar.GetProperty("yields").GetInt32().Should().Be(14);
        ar.GetProperty("returns").GetInt32().Should().Be(13);
        ar.GetProperty("lastYieldS").GetDouble().Should().Be(0.2);
        ar.GetProperty("lastReturnS").GetDouble().Should().Be(6.1);
        ar.GetProperty("sinceAt").GetInt64().Should().Be(t0.ToUnixTimeMilliseconds());
        ar.GetProperty("lastSeenAt").GetInt64().Should().Be(t0.ToUnixTimeMilliseconds());

        var reasons = ar.GetProperty("reasons").EnumerateArray().ToList();
        reasons.Count.Should().Be(3);
        reasons[0].GetProperty("kind").GetString().Should().Be("comfy");
        reasons[0].GetProperty("raw").GetString().Should().Be("a1b2c3d4");
        reasons[0].GetProperty("jobKind").GetString().Should().Be("video", "вид по шву IComfyStats, таблица на бэкенде");
        reasons[0].GetProperty("known").GetBoolean().Should().BeTrue("prompt_id в реестре этого инстанса");
        reasons[1].GetProperty("kind").GetString().Should().Be("lease");
        reasons[1].GetProperty("raw").GetString().Should().Be("съёмка хода");
        reasons[1].TryGetProperty("jobKind", out _).Should().BeFalse("вид — только у comfy");
        reasons[2].GetProperty("kind").GetString().Should().Be("gpu");
        reasons[2].GetProperty("raw").GetString().Should().Be("4217:llama-server --metrics", "raw — текст без префикса вида");

        var lease = ar.GetProperty("leases")[0];
        lease.GetProperty("reason").GetString().Should().Be("съёмка хода");
        lease.GetProperty("leftS").GetDouble().Should().Be(37.5);

        var answers = duty.GetProperty("answersLastHour");
        answers.GetProperty("insurance").GetInt32().Should().Be(1);
        answers.GetProperty("bonsai").GetInt32().Should().Be(0);
        answers.GetProperty("missed").GetInt32().Should().Be(0);
        answers.GetProperty("sinceAt").GetInt64().Should().BeGreaterThan(0, "знаменатель доли — max(now−1ч, старт процесса)");
    }

    // Условие ревью 2 (контракт зафиксирован фронтом в ревью Ф0): callsNow[].link — явно null
    // для вызова, не вошедшего в цепочку («ждёт в очереди»): поле present и равно null, не
    // отсутствие поля и не строка. Б5 заводит записи без звена до первого Step.
    // Мутация: отдать вместо null "queued" — второй assert краснеет.
    [Fact]
    public async Task StatsEngine_Bonsai_CallsNow_LinkNullЖдётВОчереди()
    {
        var dir = new FakeDirectory();
        dir.Currents["bonsai"] = new LocalModelStatsCurrent { Configured = true, Engine = "llama-server", Status = "working" };
        var factory = BonsaiFactory(dir, new FakeArbiterReader());

        var ledger = factory.Services.GetRequiredService<EngineCallLedger>();
        ledger.Begin("bonsai", "chat-retitle"); // без Step: ждёт пробы готовности/слота
        var onLink = ledger.Begin("bonsai", "note-toc")!;
        onLink.Step(EngineCallLink.Bonsai);

        using var admin = factory.CreateAuthenticatedClient();
        var body = await (await admin.GetAsync("/api/local-model/stats?engine=bonsai")).Content.ReadFromJsonAsync<JsonElement>();

        var calls = body.GetProperty("duty").GetProperty("callsNow").EnumerateArray().ToList();
        calls.Count.Should().Be(2, "открытые вызовы — оба");
        var waiting = calls.Single(c => c.GetProperty("link").ValueKind == JsonValueKind.Null);
        waiting.GetProperty("place").GetString().Should().Be("Обновление названия чата", "название места из каталога");
        waiting.TryGetProperty("link", out var link).Should().BeTrue("link обязан присутствовать — явно null");
        link.ValueKind.Should().Be(JsonValueKind.Null, "null = «ждёт в очереди» (контракт фронта)");
        waiting.GetProperty("durationS").GetDouble().Should().BeGreaterThanOrEqualTo(0);
        calls.Should().Contain(c => c.GetProperty("link").GetString() == "bonsai", "войдящий в цепочку — со звеном");
    }

    // c3: в сериализованном JSON нет ни id аренд (их создал аноним, их снимает клиент), ни
    // маркера вида "gpu:" (разбит на kind/raw). Аренда — ровно reason/leftS.
    // Мутация: отдать в LeaseDto id — EnumerateObject краснеет; не разбивать префикс — NotContain краснеет.
    [Fact]
    public async Task StatsEngine_Bonsai_НетIdАрендИПрефиксаGpu()
    {
        var t0 = new DateTimeOffset(2026, 10, 28, 11, 0, 0, TimeSpan.Zero);
        var dir = new FakeDirectory();
        dir.Currents["bonsai"] = new LocalModelStatsCurrent { Configured = true, Engine = "llama-server", Status = "working" };
        var arbiter = new FakeArbiterReader();
        arbiter.Snapshots["bonsai"] = new EngineArbiterSnapshot(
            Reachable: true, Bonsai: "ready",
            Reasons: ["gpu:4217:llama-server --metrics"],
            SinceAt: t0, Yields: 0, Returns: 0, LastYieldS: null, LastReturnS: null,
            Mode: null, QuietLeftS: null,
            Leases: [new EngineArbiterLease("съёмка хода", 37.5)],
            SeenAt: t0);
        var factory = BonsaiFactory(dir, arbiter);
        using var admin = factory.CreateAuthenticatedClient();

        var raw = await (await admin.GetAsync("/api/local-model/stats?engine=bonsai")).Content.ReadAsStringAsync();
        raw.Should().NotContain("gpu:", "префикс вида наружу не отдаётся");
        raw.Should().NotContain("lease:", "префикс вида наружу не отдаётся");

        var body = JsonDocument.Parse(raw).RootElement;
        var lease = body.GetProperty("duty").GetProperty("arbiter").GetProperty("leases")[0];
        lease.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(new[] { "reason", "leftS" }, "id аренды наружу не уходит");
    }

    // c1: выключенная Images → у именованного стенда нет Gpus и jobKind (шва нет — вид не
    // утверждаем; known остаётся null). Мутация: звать IComfyStats без null-проверки — 500, краснеет.
    [Fact]
    public async Task StatsEngine_Bonsai_ImagesВыключена_GpusNull_JobKindNull()
    {
        var t0 = new DateTimeOffset(2026, 10, 28, 11, 0, 0, TimeSpan.Zero);
        var dir = new FakeDirectory();
        dir.Currents["bonsai"] = new LocalModelStatsCurrent { Configured = true, Engine = "llama-server", Status = "working" };
        var arbiter = new FakeArbiterReader();
        arbiter.Snapshots["bonsai"] = new EngineArbiterSnapshot(
            Reachable: true, Bonsai: "waiting-vram", Reasons: ["comfy:a1b2c3d4"],
            SinceAt: t0, Yields: 1, Returns: 0, LastYieldS: 0.2, LastReturnS: null,
            Mode: "shared", QuietLeftS: null, Leases: null, SeenAt: t0);
        var factory = BonsaiFactory(dir, arbiter);
        factory.ExtraConfig["Subsystems:images:Enabled"] = "false";

        using var admin = factory.CreateAuthenticatedClient();
        var resp = await admin.GetAsync("/api/local-model/stats?engine=bonsai");
        resp.StatusCode.Should().Be(HttpStatusCode.OK, "выключенная Images — 200, не 500");
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();

        body.GetProperty("gpus").ValueKind.Should().Be(JsonValueKind.Null, "нет узла ComfyUI — нет данных о карте");
        var reason = body.GetProperty("duty").GetProperty("arbiter").GetProperty("reasons")[0];
        reason.GetProperty("kind").GetString().Should().Be("comfy");
        reason.TryGetProperty("jobKind", out _).Should().BeFalse("шва Images нет — вид не утверждаем");
        reason.TryGetProperty("known", out _).Should().BeFalse("unknown остаётся unknown");
    }

    // Статус "yielded" (Б3b) проходит как есть: выгруженная под арбитром модель — не авария,
    // downSince null. Мутация: подменить "yielded" на "down" в контроллере — тест краснеет.
    [Fact]
    public async Task StatsEngine_Bonsai_StatusYieldedБезDownSince()
    {
        var dir = new FakeDirectory();
        dir.Currents["bonsai"] = new LocalModelStatsCurrent
        {
            Configured = true, Engine = "llama-server", Status = "yielded", DownSince = null,
            Running = 0, Waiting = 0,
        };
        var factory = BonsaiFactory(dir, new FakeArbiterReader());
        using var admin = factory.CreateAuthenticatedClient();

        var body = await (await admin.GetAsync("/api/local-model/stats?engine=bonsai")).Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("status").GetString().Should().Be("yielded");
        body.GetProperty("downSince").ValueKind.Should().Be(JsonValueKind.Null, "уступка — не сбой");
    }

    // ─── Хелперы ─────────────────────────────────────────────────────────────────

    // Фейковый ILocalModelStatsReader: тест задаёт Current и Points;
    // GetHistory фильтрует по since.
    private sealed class FakeReader : ILocalModelStatsReader
    {
        public LocalModelStatsCurrent Current { get; init; } = new() { Status = "notConnected", Configured = false };
        public List<LocalModelStatsPoint> Points { get; init; } = [];
        public double? TokensPerStep { get; set; }

        public IReadOnlyList<LocalModelStatsPoint> GetHistory(TimeSpan window, long? sinceUnixMs = null)
        {
            var now = DateTimeOffset.UtcNow;
            var since = sinceUnixMs is long ms
                ? DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime
                : (now - window).UtcDateTime;
            return Points.Where(p => p.Timestamp >= since).ToList();
        }
    }

    // Фейковый IGpuSampler: пустой список — нет nvidia-smi (блок не показывать).
    private sealed class NoGpuSampler : IGpuSampler
    {
        public Task<IReadOnlyList<GpuSample>> ReadAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<GpuSample>>([]);
    }

    // Фейковый IGpuSampler с управляемым списком карт: тест задаёт, сколько карт «видит» nvidia-smi.
    private sealed class FakeGpuSampler : IGpuSampler
    {
        public IReadOnlyList<GpuSample> Samples { get; set; } = [];
        public Task<IReadOnlyList<GpuSample>> ReadAsync(CancellationToken ct = default) => Task.FromResult(Samples);
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
    private static TestWebApplicationFactory BuildFactoryWithFakes(FakeReader reader, ILocalModelTurnMeter? meter = null, IGpuSampler? gpuSampler = null)
    {
        var factory = new TestWebApplicationFactory();
        factory.ExtraServices = services =>
        {
            services.AddSingleton<ILocalModelStatsReader>(reader);
            services.AddSingleton<IGpuSampler>(gpuSampler ?? new NoGpuSampler());
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

    // Фейковый справочник дорожек (Б3c): тест задаёт снимки дорожек по именам; HistoryOf —
    // фиксированные точки для "bonsai", пусто для остальных.
    private sealed class FakeDirectory : ILocalEngineStatsDirectory
    {
        public Dictionary<string, LocalModelStatsCurrent> Currents { get; } = new(StringComparer.Ordinal);
        public List<LocalModelStatsPoint> BonsaiPoints { get; } = [];

        public IReadOnlyList<string> Names => [.. Currents.Keys];

        public LocalModelStatsCurrent? CurrentOf(string name) =>
            Currents.TryGetValue(name, out var c) ? c : null;

        public IReadOnlyList<LocalModelStatsPoint> HistoryOf(string name, TimeSpan window, long? sinceUnixMs = null) =>
            name == "bonsai" ? BonsaiPoints : [];
    }

    // Фейковый читатель состояния арбитра: подготовленный снимок по имени движка;
    // null — у движка нет StateUrl (или движок неизвестен) — как настоящий клиент.
    private sealed class FakeArbiterReader : IEngineArbiterStateReader
    {
        public Dictionary<string, EngineArbiterSnapshot?> Snapshots { get; } = new(StringComparer.Ordinal);

        public Task<EngineArbiterSnapshot?> GetAsync(string engineName, CancellationToken ct = default) =>
            Task.FromResult(Snapshots.TryGetValue(engineName, out var s) ? s : null);
    }

    // IComfyStats с реализованным швом вида задания (B4): "a1b2c3d4" → video; снимок — одна
    // карта Video (общая с ComfyUI). Остальные методы — как FakeComfyStats.
    private sealed class KindComfyStats : IComfyStats
    {
        public Task<ComfyMediaStatsSnapshot?> GetMediaAsync(CancellationToken ct) =>
            Task.FromResult<ComfyMediaStatsSnapshot?>(new ComfyMediaStatsSnapshot(
                Configured: true, Status: "working", DownSince: null, Version: "1.0",
                Gpus: [new ComfyGpuInfo(0, "", "NVIDIA GeForce RTX 3090", 34, 8192, 24576, 52, 88, 350)],
                LoadedModels: [], SlotsUsed: 0, SlotsLimit: 4, Running: [], Queue: [], LastHour: [], Unsupported: []));

        public string? DescribePromptKind(string idPrefix) => idPrefix == "a1b2c3d4" ? "video" : null;
    }

    // Фабрика тестов второго стенда: настоящий NamedLocalEngines из конфига ("bonsai":
    // BaseUrl/Model/StateUrl/FallbackRoute, MetricsUrl — по просьбе), фейковые директор и
    // читатель арбитра вместо Llm-форвардеров (поллер при этом не строится), настоящий
    // EngineCallLedger из DI (тест пишет в него до запроса). Флаг — по просьбе (по умолчанию
    // выключен, как у свежего пользователя — образец HardwareEndpointTests).
    private static TestWebApplicationFactory BonsaiFactory(
        FakeDirectory dir, FakeArbiterReader? arbiter = null, IComfyStats? comfy = null,
        bool mainConfigured = false, bool flag = true, bool engineInConfig = true, bool metricsUrl = true)
    {
        var factory = new TestWebApplicationFactory();
        if (engineInConfig)
        {
            factory.ExtraConfig["LocalEngines:bonsai:BaseUrl"] = "http://127.0.0.1:18030";
            factory.ExtraConfig["LocalEngines:bonsai:Model"] = "bonsai2-27b";
            factory.ExtraConfig["LocalEngines:bonsai:StateUrl"] = "http://127.0.0.1:8090/state";
            factory.ExtraConfig["LocalEngines:bonsai:FallbackRoute"] = "direct:minimax";
            if (metricsUrl)
                factory.ExtraConfig["LocalEngines:bonsai:MetricsUrl"] = "http://127.0.0.1:18030/metrics";
        }
        factory.ExtraServices = services =>
        {
            services.AddSingleton<ILocalModelStatsReader>(new FakeReader
            {
                Current = new LocalModelStatsCurrent
                {
                    Configured = mainConfigured,
                    Status = mainConfigured ? "working" : "notConnected",
                },
            });
            services.AddSingleton<ILocalEngineStatsDirectory>(dir);
            if (arbiter is not null)
                services.AddSingleton<IEngineArbiterStateReader>(arbiter);
            if (comfy is not null)
                services.AddSingleton<IComfyStats>(comfy);
        };
        if (flag)
            SetFlag(factory, true);
        return factory;
    }

    // Флаг per-user, как в HardwareEndpointTests: admin TestUsername.
    private static void SetFlag(TestWebApplicationFactory factory, bool on)
    {
        var users = factory.Services.GetRequiredService<UserStore>();
        users.SetFeatureFlag(users.FindByUsername(TestWebApplicationFactory.TestUsername)!.Id, "local-model-stats", on);
    }
}
