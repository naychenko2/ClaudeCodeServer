using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeHomeServer.Services.Images.LocalMedia;
using ClaudeHomeServer.Services.Media;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services;

// Снимок медиа-стенда ComfyUI (IComfyStats, шаг 26): статус (disabled/down/busy/working),
// слоты, running/queue/lastHour, DownSince, изоляция чужих задач (снимок пользователя B при
// задаче A — только kind/heavy/остаток, без ownerId/sessionId/projectId/prompt/slug/prompt_id)
public class ComfyStatsTests
{
    private const string OwnerA = "owner-a";
    private const string OwnerB = "owner-b";
    private const string SessionA = "session-a";
    private const string SessionB = "session-b";

    private static LocalJobOrigin OriginA() => new(OwnerA, SessionA, "image", "generate_image", Heavy: false);
    private static LocalJobOrigin OriginB() => new(OwnerB, SessionB, "video", "image_to_video", Heavy: true);

    // ─── Инфраструктура: фейковый ComfyUI + реестр + монитор + стор ─────────────────────────

    private sealed record QueueItem(string PromptId, string? ClientId, string ClassType, string? InputsText);

    private sealed class QueueFake : HttpMessageHandler
    {
        public List<QueueItem> Running { get; } = [];
        public List<QueueItem> Pending { get; } = [];
        public bool Down { get; set; }
        public string? Version { get; set; } = "v0.37";
        // Маршрут /ccs/stats узла (шаг 31): «загружено сейчас» + карты
        public string? CcsBody { get; set; }
        public bool CcsNotFound { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.PathAndQuery;
            if (Down) throw new HttpRequestException("connection refused");

            if (request.Method == HttpMethod.Get && path == "/queue")
                return Json(new JsonObject
                {
                    ["queue_running"] = new JsonArray([.. Running.Select((i, n) => (JsonNode)Item(n, i))]),
                    ["queue_pending"] = new JsonArray([.. Pending.Select((i, n) => (JsonNode)Item(100 + n, i))]),
                });

            if (request.Method == HttpMethod.Get && path == "/system_stats")
                return Json(new JsonObject { ["system"] = new JsonObject { ["comfyui_version"] = Version } });

            if (request.Method == HttpMethod.Get && path == "/ccs/stats")
            {
                if (CcsNotFound) return new HttpResponseMessage(HttpStatusCode.NotFound);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(CcsBody ?? "", Encoding.UTF8, "application/json"),
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);

            JsonArray Item(int number, QueueItem i) => new()
            {
                number,
                i.PromptId,
                new JsonObject
                {
                    ["1"] = new JsonObject
                    {
                        ["class_type"] = i.ClassType,
                        ["inputs"] = new JsonObject { ["prompt"] = i.InputsText ?? "" },
                    },
                },
                new JsonObject { ["client_id"] = i.ClientId ?? "" },
            };
        }

        private static HttpResponseMessage Json(JsonNode body) =>
            new(HttpStatusCode.OK) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
    }

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed record Harness(
        QueueFake Fake,
        ComfyPromptLedger Ledger,
        ComfyQueueMonitor Monitor,
        LocalMediaJobStore Store,
        ComfyStats Stats,
        ManualTime Time);

    private static Harness Build(int maxQueue = 4, bool enabled = true)
    {
        var fake = new QueueFake();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["LocalMedia:ComfyUrl"] = "http://comfy.test:8188",
            ["LocalMedia:Enabled"] = enabled ? "true" : "false",
            ["LocalMedia:MaxComfyQueue"] = maxQueue.ToString(),
            ["DataPath"] = Path.Combine(Path.GetTempPath(), $"ccs-test-{Guid.NewGuid():N}", "projects.json"),
        }).Build();
        var time = new ManualTime();
        var ledger = new ComfyPromptLedger(time);
        var client = new ComfyClient(new FakeComfyFactory(fake), config);
        var monitor = new ComfyQueueMonitor(client, ledger, config, NullLogger<ComfyQueueMonitor>.Instance, time);
        var store = new LocalMediaJobStore(config, NullLogger<LocalMediaJobStore>.Instance);
        var stats = new ComfyStats(monitor, ledger, store, null, config);
        return new Harness(fake, ledger, monitor, store, stats, time);
    }

    // Поместить задачу в очередь ComfyUI (fake) + в реестр (ledger) + в стор
    private static void Enqueue(Harness h, string promptId, LocalJobOrigin origin, int? eta, bool heavy, string storeOp)
    {
        h.Fake.Running.Add(new QueueItem(promptId, "ccs-local-media-abc", "KSampler", null));
        h.Ledger.Register(promptId, origin, eta);
        if (origin is not null)
        {
            h.Store.Add(new LocalMediaJob
            {
                Id = "lm_" + promptId,
                OwnerId = origin.OwnerId,
                SessionId = origin.SessionId,
                Op = storeOp,
                PromptId = promptId,
                Status = LocalMediaStatuses.Running,
                Heavy = heavy,
                EtaSeconds = eta,
                StartedAt = h.Time.GetUtcNow().DateTime,
            });
        }
    }

    // ─── Статус: disabled / down / busy / working ───────────────────────────────────────────

    [Fact]
    public async Task Disabled_КогдаLocalMediaВыключен()
    {
        var h = Build(enabled: false);
        var snap = await h.Stats.GetMediaAsync(CancellationToken.None);
        snap!.Should().NotBeNull();
        snap!.Status.Should().Be("disabled");
        snap.Configured.Should().BeFalse();
    }

    [Fact]
    public async Task Down_КогдаComfyUIНедоступен_СтавитDownSince()
    {
        var h = Build();
        h.Fake.Down = true;
        await h.Monitor.TickAsync(CancellationToken.None);

        var snap = await h.Stats.GetMediaAsync(CancellationToken.None);
        snap!.Status.Should().Be("down");
        snap.DownSince.Should().NotBeNull("DownSince — момент первого сбоя");
    }

    // Touch (финальное ревью): опрос вкладки «Медиа» (GetMediaAsync) трогает монитор — быстрый
    // опрос очереди (2 с) на время окна; по окну (PanelLinger) возврат в простой (15 с).
    // Мутация: убрать monitor.Touch() в GetMediaAsync — быстрый опрос не включится, тест краснеет.
    [Fact]
    public async Task GetMediaAsync_Touch_БыстрыйОпрос_ИВозвратВПростой()
    {
        var h = Build();

        // Без touch: нет активных — простой 15 с
        await h.Monitor.TickAsync(CancellationToken.None);
        h.Monitor.LastDelay.Should().Be(TimeSpan.FromSeconds(15), "без touch — простой");

        // Опрос вкладки «Медиа» — touch: быстрый 2 с
        await h.Stats.GetMediaAsync(CancellationToken.None);
        await h.Monitor.TickAsync(CancellationToken.None);
        h.Monitor.LastDelay.Should().Be(TimeSpan.FromSeconds(2), "GetMediaAsync трогает монитор — быстрый опрос");

        // Окно touch (10 с) прошло, активных нет — возврат в простой
        h.Time.Advance(TimeSpan.FromSeconds(11));
        await h.Monitor.TickAsync(CancellationToken.None);
        h.Monitor.LastDelay.Should().Be(TimeSpan.FromSeconds(15), "окно touch прошло — простой");
    }

    [Fact]
    public async Task Busy_КогдаСлотыПолны_4из4()
    {
        var h = Build(maxQueue: 4);
        // 4 задачи в очереди (идущая + 3 ждущие) = 4 из 4
        for (var i = 0; i < 4; i++)
        {
            var id = $"p{i}";
            h.Fake.Running.Add(new QueueItem(id, "ccs-local-media-abc", "KSampler", null));
            h.Ledger.Register(id, OriginA(), 30);
        }
        await h.Monitor.TickAsync(CancellationToken.None);

        var snap = await h.Stats.GetMediaAsync(CancellationToken.None);
        snap!.SlotsUsed.Should().Be(4);
        snap.SlotsLimit.Should().Be(4);
        snap.Status.Should().Be("busy", "4 из 4 слотов — занят");
    }

    [Fact]
    public async Task Busy_КогдаИдётТяжёлая_ДажеЕслиСлотыНеПолны()
    {
        var h = Build(maxQueue: 4);
        // Одна тяжёлая задача (heavy) — слоты не полны, но идёт тяжёлая
        h.Fake.Running.Add(new QueueItem("heavy1", "ccs-local-media-abc", "KSampler", null));
        h.Ledger.Register("heavy1", OriginB(), 600);
        h.Store.Add(new LocalMediaJob
        {
            Id = "lm_heavy1",
            OwnerId = OwnerB,
            SessionId = SessionB,
            Op = "image_to_video",
            PromptId = "heavy1",
            Status = LocalMediaStatuses.Running,
            Heavy = true,
            EtaSeconds = 600,
            StartedAt = h.Time.GetUtcNow().DateTime,
        });
        await h.Monitor.TickAsync(CancellationToken.None);

        var snap = await h.Stats.GetMediaAsync(CancellationToken.None);
        snap!.SlotsUsed.Should().Be(1);
        snap.SlotsLimit.Should().Be(4);
        snap.Status.Should().Be("busy", "идёт тяжёлая — занят, даже если слоты не полны");
    }

    [Fact]
    public async Task Working_КогдаСлотыСвободны_И_НетТяжёлых()
    {
        var h = Build(maxQueue: 4);
        // Одна лёгкая задача — слоты не полны, тяжёлых нет
        h.Fake.Running.Add(new QueueItem("light1", "ccs-local-media-abc", "KSampler", null));
        h.Ledger.Register("light1", OriginA(), 30);
        h.Store.Add(new LocalMediaJob
        {
            Id = "lm_light1",
            OwnerId = OwnerA,
            SessionId = SessionA,
            Op = "generate_image",
            PromptId = "light1",
            Status = LocalMediaStatuses.Running,
            Heavy = false,
            EtaSeconds = 30,
            StartedAt = h.Time.GetUtcNow().DateTime,
        });
        await h.Monitor.TickAsync(CancellationToken.None);

        var snap = await h.Stats.GetMediaAsync(CancellationToken.None);
        snap!.Status.Should().Be("working", "слоты свободны и тяжёлых нет");
    }

    // ─── Изоляция чужих задач (главный тест) ─────────────────────────────────────────────────

    [Fact]
    public async Task СнимокПользователяB_ПриЗадачеA_СодержитТолькоKind_Heavy_ИОстаток()
    {
        var h = Build(maxQueue: 4);
        // Задача пользователя A: image, не тяжёлая, eta 30с
        Enqueue(h, "job-a", OriginA(), 30, heavy: false, storeOp: "generate_image");
        // Задача пользователя B: video, тяжёлая, eta 600с
        Enqueue(h, "job-b", OriginB(), 600, heavy: true, storeOp: "image_to_video");
        await h.Monitor.TickAsync(CancellationToken.None);

        // Снимок для пользователя B: своя задача (job-b) — полная, чужая (job-a) — только kind/heavy/остаток
        var result = await h.Stats.GetForOwnerAsync(OwnerB, CancellationToken.None);
        result.Should().NotBeNull();
        var ownerSnap = result!;

        // Своя задача: полная (kind, op, heavy, owner, session, eta, remaining)
        var own = ownerSnap.Own.Should().ContainSingle("у B одна своя задача").Subject;
        own.Kind.Should().Be("video");
        own.Op.Should().Be("image_to_video");
        own.Heavy.Should().BeTrue();
        own.OwnerId.Should().Be(OwnerB);
        own.SessionId.Should().Be(SessionB);
        own.EtaSeconds.Should().Be(600);

        // Чужая задача: только kind/heavy/остаток, без ownerId/sessionId/projectId/prompt/slug/prompt_id
        var foreign = ownerSnap.Foreign.Should().ContainSingle("у A одна чужая задача").Subject;
        foreign.Kind.Should().Be("image");
        foreign.Heavy.Should().BeFalse();
        // Остаток: eta 30с, стартовала «сейчас» → остаток ≈ 30
        foreign.RemainingSeconds.Should().Be(30, "остаток = eta (задача только что стартовала)");

        // Сериализованный JSON чужой записи НЕ содержит ownerId/sessionId/projectId/prompt/slug/prompt_id
        var json = JsonSerializer.Serialize(foreign, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var doc = JsonDocument.Parse(json);
        doc.RootElement.TryGetProperty("ownerId", out _).Should().BeFalse("ownerId — JsonIgnore'd");
        doc.RootElement.TryGetProperty("sessionId", out var sid).Should().BeTrue();
        sid.ValueKind.Should().Be(JsonValueKind.Null, "sessionId чужой записи — null");
        doc.RootElement.TryGetProperty("projectId", out var pid).Should().BeTrue();
        pid.ValueKind.Should().Be(JsonValueKind.Null, "projectId чужой записи — null");
        // prompt_id не в ComfyJobRow (это внутренний), но проверим что op не утекает
        doc.RootElement.TryGetProperty("op", out var op).Should().BeTrue();
        op.GetString().Should().BeNullOrEmpty("op чужой записи — пустой");
    }

    // Мутация: если в чужую запись добавить ownerId — тест должен покраснеть
    [Fact]
    public async Task Мутация_ДобавитьOwnerIdВЧужуюЗапись_Красит()
    {
        var h = Build(maxQueue: 4);
        Enqueue(h, "job-a", OriginA(), 30, heavy: false, storeOp: "generate_image");
        Enqueue(h, "job-b", OriginB(), 600, heavy: true, storeOp: "image_to_video");
        await h.Monitor.TickAsync(CancellationToken.None);

        var ownerSnap = await h.Stats.GetForOwnerAsync(OwnerB, CancellationToken.None);
        var foreign = ownerSnap!.Foreign.Single();

        // Мутация: если бы мы поставили ownerId в чужую запись — он бы появился в JSON
        // Сейчас ownerId = "" (пустая строка), а не OwnerA. Проверяем, что он НЕ OwnerA
        foreign.OwnerId.Should().NotBe(OwnerA, "ownerId чужой записи — не A (иначе утечка)");

        // Сериализация: ownerId — JsonIgnore'd, но если бы мы его заполнили значением A —
        // он бы не появился в JSON (JsonIgnore). Проверяем, что в JSON нет OwnerA
        var json = JsonSerializer.Serialize(foreign, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        json.Should().NotContain(OwnerA, "ownerId A не должен утечь в сериализацию");
    }

    // ─── Чужие классифицированы ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Чужие_Классифицированы_И_ПоказаныВСнимке()
    {
        var h = Build(maxQueue: 4);
        // Чужие прогоны (не в реестре): instance (client_id), transcription (TranscriptionGate), external
        h.Fake.Running.Add(new QueueItem("foreign-instance", "ccs-local-media-xyz", "KSampler", null));
        h.Fake.Pending.Add(new QueueItem("foreign-transcription", null, "TranscriptionGate", null));
        h.Fake.Pending.Add(new QueueItem("foreign-external", null, "KSampler", null));
        await h.Monitor.TickAsync(CancellationToken.None);

        var snap = await h.Stats.GetMediaAsync(CancellationToken.None);
        // Все 3 чужие + 0 наших = 3 в очереди
        snap!.Running.Should().HaveCount(1);
        snap.Queue.Should().HaveCount(2);

        // Чужая instance: kind = "other", foreign = "instance"
        var inst = snap.Running.Single();
        inst.Foreign.Should().Be("instance");
        inst.Kind.Should().Be("other");

        // Чужая transcription: kind = "audio", foreign = "transcription"
        var trans = snap.Queue.First(q => q.Foreign == "transcription");
        trans.Kind.Should().Be("audio");

        // Чужая external: kind = "other", foreign = "external"
        var ext = snap.Queue.First(q => q.Foreign == "external");
        ext.Kind.Should().Be("other");
    }

    // ─── LastHour: завершённые за последний час ─────────────────────────────────────────────

    [Fact]
    public async Task LastHour_СодержитЗавершённыеЗадачи()
    {
        var h = Build(maxQueue: 4);
        // Завершённая задача в реестре
        h.Ledger.Register("done1", OriginA(), 30);
        h.Ledger.MarkStarted("done1");
        h.Ledger.Complete("done1", "completed", runSeconds: 25, error: null);

        var snap = await h.Stats.GetMediaAsync(CancellationToken.None);
        snap!.LastHour.Should().ContainSingle();
        var row = snap.LastHour.Single();
        row.Kind.Should().Be("image");
        row.Op.Should().Be("generate_image");
        row.Outcome.Should().Be("completed");
        row.RunSeconds.Should().Be(25);
    }

    // ─── «Загружено сейчас» + карты узла (маршрут /ccs/stats, шаг 31) ───────────────────

    // Узел сообщает: имена моделей + карта в снимке; unsupported пуст (маршрут установлен)
    [Fact]
    public async Task ЗагруженныеМодели_И_Карты_Из_Узла_ПопадаютВСнимок()
    {
        var h = Build();
        h.Fake.CcsBody = """{"version":1,"loadedModels":[{"name":"qwen_image.safetensors"}],"gpus":[{"index":0,"name":"NVIDIA GeForce RTX 3090","utilPerc":97,"memUsedMb":21874,"memTotalMb":24576,"tempC":71,"powerW":338,"powerLimitW":350}]}""";
        await h.Monitor.TickAsync(CancellationToken.None);

        var snap = await h.Stats.GetMediaAsync(CancellationToken.None);
        snap!.LoadedModels.Should().Equal("qwen_image.safetensors");
        snap.Gpus.Should().ContainSingle();
        snap.Gpus[0].Index.Should().Be(0);
        snap.Gpus[0].Name.Should().Be("NVIDIA GeForce RTX 3090");
        snap.Gpus[0].UtilPerc.Should().Be(97);
        snap.Gpus[0].MemUsedMb.Should().Be(21874);
        snap.Gpus[0].MemTotalMb.Should().Be(24576);
        snap.Gpus[0].TempC.Should().Be(71);
        snap.Gpus[0].PowerW.Should().Be(338);
        snap.Gpus[0].PowerLimitW.Should().Be(350);
        // Маршрут установлен (CcsSupported) — пометки «не сообщает» нет
        snap.Unsupported.Should().BeEmpty("маршрут установлен — пометки нет");
    }

    // Мутация: убрать пометку «не сообщает» — тест краснеет.
    // Узел не сообщает (404): gpus/loadedModels пустые, unsupported несёт пометку
    [Fact]
    public async Task НетМаршрута_Unsupported_Пометка_И_КартыПусты()
    {
        var h = Build();
        h.Fake.CcsNotFound = true;   // 404 — маршрут не установлен
        await h.Monitor.TickAsync(CancellationToken.None);

        var snap = await h.Stats.GetMediaAsync(CancellationToken.None);
        snap!.Gpus.Should().BeEmpty("нет маршрута — карт нет");
        snap.LoadedModels.Should().BeEmpty("нет маршрута — моделей нет");
        snap.Unsupported.Should().Contain("узел не сообщает загруженные модели");
    }

    // gpus:null (pynvml-сбой) при установленном маршруте: loadedModels есть, gpus пустые,
    // пометки «не сообщает» НЕТ (маршрут работает, просто pynvml молчит)
    [Fact]
    public async Task GpusNull_ПриУстановленномМаршруте_БезПометки()
    {
        var h = Build();
        h.Fake.CcsBody = """{"version":1,"loadedModels":[{"name":"m.safetensors"}],"gpus":null,"gpusError":"pynvml: ModuleNotFoundError"}""";
        await h.Monitor.TickAsync(CancellationToken.None);

        var snap = await h.Stats.GetMediaAsync(CancellationToken.None);
        snap!.LoadedModels.Should().Equal("m.safetensors");
        snap.Gpus.Should().BeEmpty("gpus null — карт нет");
        snap.Unsupported.Should().BeEmpty("маршрут установлен (version 1) — пометки «не сообщает» нет");
    }
}
