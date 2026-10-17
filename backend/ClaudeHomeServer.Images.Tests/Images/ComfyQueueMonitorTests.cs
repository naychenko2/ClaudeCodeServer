using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using ClaudeHomeServer.Services.Images.LocalMedia;
using ClaudeHomeServer.Services.Media;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services;

// Монитор очереди ComfyUI (шаг 24): классификация чужих прогонов, финализация по /history,
// down с DownSince, переключение периода 2с ↔ 15с — на фейковых часах (ManualTime), без Task.Delay
public class ComfyQueueMonitorTests
{
    private const string Owner = "owner-1";

    private static LocalJobOrigin Origin() => new(Owner, "session-1", "image", "generate_image", Heavy: false);

    // ─── Инфраструктура: фейковый ComfyUI с настраиваемой очередью/историей ───────────────

    // Элемент очереди: prompt_id, client_id (extra_data), class_type + inputs узла графа
    private sealed record QueueItem(string PromptId, string? ClientId, string ClassType, string? InputsText);

    // Фейковый ComfyUI: /queue, /system_stats, /history/{id} с настраиваемым содержимым
    private sealed class QueueFake : HttpMessageHandler
    {
        public List<QueueItem> Running { get; } = [];
        public List<QueueItem> Pending { get; } = [];
        public Dictionary<string, (bool Completed, bool Failed, double? RunSeconds)> History { get; } = [];
        public bool Down { get; set; }
        public string? Version { get; set; } = "v0.37";
        public int QueueCalls { get; private set; }
        // Сбой /queue не-ComfyException (C1): симулирует мусорный тип, проскочивший разбор
        public Exception? ThrowOnQueue { get; set; }
        // Ворота: блок первого /queue, чтобы тест поймал Touch «во время тика» (между TickAsync и
        // DelayAsync). QueueGateHit — монитор уже в GetQueueAsync (LastDelay вычислен), Touch не потерян
        public TaskCompletionSource? QueueGate { get; set; }
        public bool QueueGateHit { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.PathAndQuery;
            if (Down) throw new HttpRequestException("connection refused");

            if (request.Method == HttpMethod.Get && path == "/queue")
            {
                if (ThrowOnQueue is { } ex) throw ex;
                if (QueueGate is { } gate)
                {
                    QueueGateHit = true;
                    await gate.Task;   // блокирует первый вызов; после SetResult — мгновенно
                }
                QueueCalls++;
                return Json(new JsonObject
                {
                    ["queue_running"] = new JsonArray([.. Running.Select((i, n) => (JsonNode)Item(n, i))]),
                    ["queue_pending"] = new JsonArray([.. Pending.Select((i, n) => (JsonNode)Item(100 + n, i))]),
                });
            }

            if (request.Method == HttpMethod.Get && path == "/system_stats")
                return Json(new JsonObject { ["system"] = new JsonObject { ["comfyui_version"] = Version } });

            if (request.Method == HttpMethod.Get && path.StartsWith("/history/", StringComparison.Ordinal))
            {
                var id = Uri.UnescapeDataString(path["/history/".Length..]);
                if (!History.TryGetValue(id, out var h)) return Json(new JsonObject());
                var start = 1_000_000L;
                var end = start + (long)((h.RunSeconds ?? 0) * 1000);
                var messages = new JsonArray
                {
                    new JsonArray("execution_start", new JsonObject { ["timestamp"] = start }),
                    new JsonArray(h.Failed ? "execution_error" : "execution_success", new JsonObject { ["timestamp"] = end }),
                };
                return Json(new JsonObject
                {
                    [id] = new JsonObject
                    {
                        ["status"] = new JsonObject
                        {
                            ["status_str"] = h.Failed ? "error" : "success",
                            ["completed"] = h.Completed,
                            ["messages"] = messages,
                        },
                        ["outputs"] = new JsonObject(),
                    },
                });
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);

            // [номер, prompt_id, граф, extra_data]
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

    // Фейковые часы: время двигает тест, а не сон
    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed record Harness(QueueFake Fake, ComfyClient Client, ComfyPromptLedger Ledger, ComfyQueueMonitor Monitor, ManualTime Time);

    private static Harness Build(bool enabled = true)
    {
        var fake = new QueueFake();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["LocalMedia:ComfyUrl"] = "http://comfy.test:8188",
            ["LocalMedia:Enabled"] = enabled ? "true" : "false",
        }).Build();
        var time = new ManualTime();
        var ledger = new ComfyPromptLedger(time);
        var client = new ComfyClient(new FakeComfyFactory(fake), config);
        var monitor = new ComfyQueueMonitor(client, ledger, config, NullLogger<ComfyQueueMonitor>.Instance, time);
        return new Harness(fake, client, ledger, monitor, time);
    }

    // ─── Классификация чужих прогонов ──────────────────────────────────────────────────────

    [Fact]
    public async Task Чужие_Классифицируются_ПоClientId_И_ТипамУзлов()
    {
        var h = Build();
        // Чужие прогоны (не в реестре): instance (client_id), transcription (TranscriptionGate), external
        h.Fake.Running.Add(new QueueItem("foreign-instance", "ccs-local-media-abc", "KSampler", null));
        h.Fake.Pending.Add(new QueueItem("foreign-transcription", null, "TranscriptionGate", null));
        h.Fake.Pending.Add(new QueueItem("foreign-external", null, "KSampler", null));

        await h.Monitor.TickAsync(CancellationToken.None);

        var snapshot = h.Monitor.Snapshot!;
        snapshot.Foreign.Should().HaveCount(3);
        Forefirst(snapshot, "foreign-instance").Should().Be(ComfyForeignKind.Instance);
        Forefirst(snapshot, "foreign-transcription").Should().Be(ComfyForeignKind.Transcription);
        Forefirst(snapshot, "foreign-external").Should().Be(ComfyForeignKind.External);
    }

    // Наш прогон (в реестре) не считается чужим
    [Fact]
    public async Task Наш_Прогон_НеСчитаетсяЧужим()
    {
        var h = Build();
        h.Ledger.Register("ours", Origin(), 42);
        h.Fake.Running.Add(new QueueItem("ours", "ccs-local-media-abc", "KSampler", null));

        await h.Monitor.TickAsync(CancellationToken.None);

        h.Monitor.Snapshot!.Foreign.Should().BeEmpty("наш прогон в реестре — не чужой");
    }

    // Текст промпта чужого графа (inputs) не читается: в сериализации снимка его нет
    [Fact]
    public async Task ТекстПромптаЧужогоГрафа_НеЧитается()
    {
        const string Secret = "SECRET_PROMPT_TEXT_XYZ";
        var h = Build();
        h.Fake.Running.Add(new QueueItem("foreign", null, "KSampler", Secret));

        await h.Monitor.TickAsync(CancellationToken.None);

        var json = System.Text.Json.JsonSerializer.Serialize(h.Monitor.Snapshot);
        json.Should().NotContain(Secret, "inputs чужого графа не сохраняются");
        json.Should().Contain("KSampler", "class_type узла сохраняется");
    }

    // ─── Финализация по /history ────────────────────────────────────────────────────────────

    [Fact]
    public async Task УшедшийИзОчереди_Финализируется_ПоИстории()
    {
        var h = Build();
        h.Ledger.Register("p1", Origin(), 42);
        h.Fake.Running.Add(new QueueItem("p1", "ccs-local-media-abc", "KSampler", null));

        // Первый тик: p1 в очереди (идёт)
        await h.Monitor.TickAsync(CancellationToken.None);
        h.Ledger.Contains("p1").Should().BeTrue("ещё в очереди — не финализирован");

        // p1 уходит из очереди, история: success, 30с
        h.Fake.Running.Clear();
        h.Fake.History["p1"] = (Completed: true, Failed: false, RunSeconds: 30);

        // Второй тик: p1 не в очереди → финализация
        await h.Monitor.TickAsync(CancellationToken.None);

        h.Ledger.Contains("p1").Should().BeFalse("завершённый не активен");
        h.Ledger.TryGet("p1", out var record).Should().BeTrue();
        record!.Outcome.Should().Be("completed");
        record.RunSeconds.Should().Be(30);
    }

    // История ещё не опубликована (гонка): запись не теряется, на следующем тике с историей финализируется
    [Fact]
    public async Task История_НеПубликована_ПервыйТик_Потом_Появилась_Запись_Завершена()
    {
        var h = Build();
        h.Ledger.Register("p1", Origin(), 42);
        h.Fake.Running.Add(new QueueItem("p1", "ccs-local-media-abc", "KSampler", null));

        // Первый тик: p1 в очереди
        await h.Monitor.TickAsync(CancellationToken.None);
        h.Ledger.Contains("p1").Should().BeTrue("ещё в очереди");

        // p1 уходит из очереди, история ещё не опубликована
        h.Fake.Running.Clear();

        // Второй тик: p1 не в очереди, история null → не финализируем, но запись не теряем
        await h.Monitor.TickAsync(CancellationToken.None);
        h.Ledger.Contains("p1").Should().BeTrue("история не опубликована — запись ещё активна");

        // История появилась
        h.Fake.History["p1"] = (Completed: true, Failed: false, RunSeconds: 30);

        // Третий тик: p1 не в очереди, история есть → финализация
        await h.Monitor.TickAsync(CancellationToken.None);
        h.Ledger.Contains("p1").Should().BeFalse("завершённый не активен");
        h.Ledger.TryGet("p1", out var record).Should().BeTrue();
        record!.Outcome.Should().Be("completed");
    }

    [Fact]
    public async Task УпавшийПрогон_Финализируется_КакFailed()
    {
        var h = Build();
        h.Ledger.Register("p1", Origin(), 42);
        h.Fake.Running.Add(new QueueItem("p1", "ccs-local-media-abc", "KSampler", null));
        await h.Monitor.TickAsync(CancellationToken.None);

        h.Fake.Running.Clear();
        h.Fake.History["p1"] = (Completed: false, Failed: true, RunSeconds: 10);

        await h.Monitor.TickAsync(CancellationToken.None);

        h.Ledger.TryGet("p1", out var record).Should().BeTrue();
        record!.Outcome.Should().Be("failed");
    }

    // Рестарт: prompt_id пропал из очереди и истории (MaxNullHistoryTicks=20 тиков null) —
    // исход interrupted, не failed (сбой графа). Мутация «failed» в MaxNullHistoryTicks → красный
    [Fact]
    public async Task Рестарт_ПропалИзИстории_MaxNullTicks_Interrupted()
    {
        var h = Build();
        h.Ledger.Register("p1", Origin(), 42);
        h.Fake.Running.Add(new QueueItem("p1", "ccs-local-media-abc", "KSampler", null));

        // Первый тик: p1 в очереди (идёт)
        await h.Monitor.TickAsync(CancellationToken.None);

        // p1 уходит из очереди, история не опубликована (рестарт стенда)
        h.Fake.Running.Clear();

        // 20 тиков с null-историей → финализация interrupted
        for (var i = 0; i < 20; i++)
            await h.Monitor.TickAsync(CancellationToken.None);

        h.Ledger.Contains("p1").Should().BeFalse("прерванный не активен");
        h.Ledger.TryGet("p1", out var record).Should().BeTrue();
        record!.Outcome.Should().Be("interrupted", "рестарт — interrupted, не failed");
    }

    // ─── down с DownSince на первом сбое ───────────────────────────────────────────────────

    [Fact]
    public async Task Down_СтавитDownSince_ПервогоСбоя_И_НеДвигает()
    {
        var h = Build();

        // Первый сбой
        h.Fake.Down = true;
        await h.Monitor.TickAsync(CancellationToken.None);
        h.Monitor.Down.Should().BeTrue();
        var firstDownSince = h.Monitor.DownSince;
        firstDownSince.Should().Be(h.Time.GetUtcNow(), "DownSince — момент первого сбоя");

        // Время идёт, сбой продолжается: DownSince не двигается
        h.Time.Advance(TimeSpan.FromSeconds(5));
        await h.Monitor.TickAsync(CancellationToken.None);
        h.Monitor.DownSince.Should().Be(firstDownSince, "повторный сбой не двигает DownSince");

        // Восстановление: down снят, DownSince очищен
        h.Fake.Down = false;
        await h.Monitor.TickAsync(CancellationToken.None);
        h.Monitor.Down.Should().BeFalse();
        h.Monitor.DownSince.Should().BeNull();
    }

    // ─── Период опроса переключается 2с ↔ 15с ───────────────────────────────────────────────

    [Fact]
    public async Task Период_Idle_15с_БезАктивных_И_БезПанели()
    {
        var h = Build();
        await h.Monitor.TickAsync(CancellationToken.None);
        h.Monitor.LastDelay.Should().Be(TimeSpan.FromSeconds(15), "нет активных и панель не трогала — живость 15с");
    }

    [Fact]
    public async Task Период_Active_2с_КогдаЕстьАктивные()
    {
        var h = Build();
        h.Ledger.Register("p1", Origin(), 42);
        await h.Monitor.TickAsync(CancellationToken.None);
        h.Monitor.LastDelay.Should().Be(TimeSpan.FromSeconds(2), "есть активные — 2с");
    }

    [Fact]
    public async Task Период_2с_ПокаПанельТрогала_В_Окне()
    {
        var h = Build();
        h.Monitor.Touch();
        await h.Monitor.TickAsync(CancellationToken.None);
        h.Monitor.LastDelay.Should().Be(TimeSpan.FromSeconds(2), "панель трогала в окне — 2с");
    }

    [Fact]
    public async Task Период_ВозвращаетсяК15с_ПослеОкна_БезАктивных()
    {
        var h = Build();
        h.Monitor.Touch();
        await h.Monitor.TickAsync(CancellationToken.None);
        h.Monitor.LastDelay.Should().Be(TimeSpan.FromSeconds(2));

        // Окно 10с прошло, активных нет — снова 15с
        h.Time.Advance(TimeSpan.FromSeconds(11));
        await h.Monitor.TickAsync(CancellationToken.None);
        h.Monitor.LastDelay.Should().Be(TimeSpan.FromSeconds(15), "окно прошло, активных нет — 15с");
    }

    // Выключенный LocalMedia: период idle, очередь не опрашивается
    [Fact]
    public async Task Выключенный_Сервер_НеОпрашиваетОчередь()
    {
        var h = Build(enabled: false);
        await h.Monitor.TickAsync(CancellationToken.None);
        h.Monitor.LastDelay.Should().Be(TimeSpan.FromSeconds(15));
        h.Fake.QueueCalls.Should().Be(0, "выключенный сервер /queue не трогает");
    }

    // C1: разбор /queue бросил не-ComfyException (мусорный тип) — общий catch в TickAsync
    // гасит его: цикл монитора не рвётся (иначе StopHost ронял бы весь сервер), следующий тик жив
    [Fact]
    public async Task МусорнаяОчередь_НеОстанавливаетЦикл_ОбщийCatch()
    {
        var h = Build();
        h.Fake.ThrowOnQueue = new InvalidOperationException(
            "An element of type 'Number' cannot be converted to a 'System.String'");

        // Тик со сбоем не-ComfyException: не бросает наружу (общий catch), снимок не обновлён
        await h.Monitor.TickAsync(CancellationToken.None);

        // Цикл жив: следующий тик (без сбоя) снова читает очередь и обновляет снимок
        h.Fake.ThrowOnQueue = null;
        h.Fake.Running.Add(new QueueItem("p1", "ccs-local-media-abc", "KSampler", null));
        await h.Monitor.TickAsync(CancellationToken.None);

        h.Monitor.Snapshot!.Running.Should().HaveCount(1, "после сбоя цикл продолжил работу");
    }

    // ─── Touch будит монитор, не досиживая текущую паузу (фантомная очередь) ───────────────

    // В простое монитор досиживает паузу 15с; Touch (опрос вкладки «Медиа») будит его сразу,
    // иначе снимок устаревает до 15с после открытия панели. ManualTime не двигает Task.Delay,
    // поэтому второй тик возможен ТОЛЬКО через отмену wake-токена. Мутация: убрать
    // _wakeCts?.Cancel() в Touch — монитор досиживает паузу, второй тик не происходит, тест краснеет.
    [Fact]
    public async Task Touch_БудитМонитор_НеДосиживаяТекущуюПаузу()
    {
        var h = Build();
        var cts = new CancellationTokenSource();
        await h.Monitor.StartAsync(cts.Token);

        // Первый тик прошёл (QueueCalls=1); ждём наблюдаемого признака «монитор вошёл в паузу»
        // (InDelay), а не 50мс по часам — на загруженном CI сон по таймеру мог мигать
        await WaitUntil(() => h.Fake.QueueCalls >= 1);
        await WaitUntil(() => h.Monitor.InDelay);

        // Touch будит монитор: второй тик происходит, не дожидаясь 15с
        h.Monitor.Touch();
        await WaitUntil(() => h.Fake.QueueCalls >= 2);

        cts.Cancel();
        await h.Monitor.StopAsync(CancellationToken.None);

        h.Fake.QueueCalls.Should().BeGreaterThanOrEqualTo(2, "Touch будит монитор — второй тик без ожидания 15с");
    }

    // Touch во время АКТИВНОЙ паузы (2с) монитор не будит: нижняя граница частоты опроса (2с)
    // не рвётся K опросчиками (адаптеры редакторов, цикл 2–3 с) при открытых панели и задачах.
    // Мутация: убрать `if (LastPauseIsIdle)` в Touch — wake-токен отменяется, лишний тик в пределах
    // 200мс, тест краснеет.
    [Fact]
    public async Task Touch_ВоВремяАктивнойПаузы_НеДаетЛишнегоТика()
    {
        var h = Build();
        h.Ledger.Register("p1", Origin(), 42);   // активная задача → активная пауза 2с
        var cts = new CancellationTokenSource();
        await h.Monitor.StartAsync(cts.Token);

        // Первый тик: активная задача → активная пауза 2с; ждём, пока монитор вошёл в неё
        await WaitUntil(() => h.Fake.QueueCalls >= 1);
        await WaitUntil(() => h.Monitor.InDelay && !h.Monitor.LastPauseIsIdle);

        var before = h.Fake.QueueCalls;
        h.Monitor.Touch();   // активная пауза → wake-токен НЕ отменяется

        // ~200мс (<< 2с): если бы Touch будил из активной паузы, был бы лишний тик
        await Task.Delay(200);
        h.Fake.QueueCalls.Should().Be(before, "активная пауза 2с не прервана Touch — лишнего тика нет");

        cts.Cancel();
        await h.Monitor.StopAsync(CancellationToken.None);
    }

    // Гонка: Touch, пришедший во время тика (между TickAsync и DelayAsync), не теряется. До
    // правки период брался из LastDelay (вычислен в TickAsync = IdlePoll, 15с), и Touch, отменив
    // уже отработавший wake-токен, терялся — монитор досиживал 15с. Теперь DelayAsync пересчитывает
    // период под lock: Touch ставит _lastTouch, и DelayAsync сразу берёт ActivePoll (2с).
    // Ворота QueueGate блокируют первый /queue: Touch приходит, пока TickAsync в GetQueueAsync.
    // Мутация: вернуть период в DelayAsync к LastDelay (без пересчёта) — следующий тик через 15с,
    // в пределах 5с второй тик не происходит, тест краснеет.
    [Fact]
    public async Task Touch_ВоВремяТика_НеТеряется_СледующийТик_В_АктивнойПаузе()
    {
        var h = Build();
        h.Fake.QueueGate = new TaskCompletionSource();
        var cts = new CancellationTokenSource();
        await h.Monitor.StartAsync(cts.Token);

        // Первый тик: TickAsync вычислил LastDelay = IdlePoll (нет активных, нет Touch) и ушёл в
        // GetQueueAsync (ворота). Ждём, пока монитор реально в опросе очереди (QueueGateHit)
        await WaitUntil(() => h.Fake.QueueGateHit);

        // Touch «во время тика»: ставит _lastTouch, пока TickAsync ещё в GetQueueAsync
        h.Monitor.Touch();

        // Открываем ворота: TickAsync завершится, DelayAsync пересчитает период на ActivePoll (2с)
        h.Fake.QueueGate!.SetResult();

        // Второй тик в пределах ~2с (ActivePoll), а не 15с (IdlePoll). Потолок 5с: 2с << 5с << 15с
        await WaitUntil(() => h.Fake.QueueCalls >= 2, 5000);
        h.Fake.QueueCalls.Should().BeGreaterThanOrEqualTo(2,
            "Touch во время тика не потерян — следующий тик в пределах ActivePoll, а не IdlePoll");

        cts.Cancel();
        await h.Monitor.StopAsync(CancellationToken.None);
    }

    // Хелпер: ждать условие (реальное время, с потолком) — для асинхронного цикла монитора
    private static async Task WaitUntil(Func<bool> cond, int timeoutMs = 2000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!cond() && sw.ElapsedMilliseconds < timeoutMs)
            await Task.Delay(5);
    }

    // Хелпер: классификация по prompt_id из снимка
    private static ComfyForeignKind Forefirst(ComfyQueueMonitorSnapshot snapshot, string promptId) =>
        snapshot.Foreign.First(f => f.PromptId == promptId).Kind;
}
