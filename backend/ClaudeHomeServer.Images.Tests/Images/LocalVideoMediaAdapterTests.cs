using System.Text.Json.Nodes;
using ClaudeHomeServer.Services.Images.LocalMedia;
using ClaudeHomeServer.Services.Media;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services;

// Шов локальной съёмки видео модуля «Видео» поверх фейкового ComfyUI: тумблер, постановка шаблона
// ImageToVideo с last_frame, опрос с клипом, отмена, а пределы и ETA — из общей статики local-media
public class LocalVideoMediaAdapterTests
{
    private readonly FakeComfy _comfy = new();

    private LocalVideoMediaAdapter Build(bool enabled = true, int maxQueue = 4, int maxSeconds = 10, bool fastDefault = true,
        ComfyProgressTracker? steps = null, ComfyPromptLedger? ledger = null)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["LocalMedia:Enabled"] = enabled ? "true" : "false",
            ["LocalMedia:ComfyUrl"] = "http://comfy.test:8188",
            ["LocalMedia:MaxComfyQueue"] = maxQueue.ToString(),
            ["LocalMedia:MaxVideoSeconds"] = maxSeconds.ToString(),
            ["LocalMedia:VideoFastDefault"] = fastDefault ? "true" : "false",
        }).Build();
        var client = new ComfyClient(new FakeComfyFactory(_comfy), config);
        var images = new LocalImageMediaAdapter(client, config, NullLogger<LocalImageMediaAdapter>.Instance);
        return new LocalVideoMediaAdapter(client, config, images, NullLogger<LocalVideoMediaAdapter>.Instance, null, null, steps, ledger);
    }

    private static readonly (int Width, int Height) Full = ComfyWorkflows.VideoSizes["full"];
    private static readonly (int Width, int Height) Half = ComfyWorkflows.VideoSizes["half"];

    private static LocalVideoRequest Scene(byte[]? last = null, int seconds = 5, string size = "full", bool? fast = null) =>
        new("идёт снег", LocalMediaTestImages.Png(1344, 768), last, seconds, size, fast, Seed: 7);

    private JsonObject Graph(int k = 0) => _comfy.Prompts[k]["prompt"]!.AsObject();

    private JsonObject I2V(int k = 0) => Graph(k)["i2v"]!["inputs"]!.AsObject();

    [Fact]
    public async Task Выключенный_тумблер_недоступен_и_в_ComfyUI_не_ставит()
    {
        var adapter = Build(enabled: false);

        adapter.Configured.Should().BeFalse();
        adapter.Available.Should().BeFalse();
        (await adapter.QueueLengthAsync(default)).Should().BeNull();
        var submitted = await adapter.SubmitAsync(Scene(), default);

        submitted.Ticket.Should().BeNull();
        submitted.Error.Should().NotBeNullOrEmpty();
        _comfy.Prompts.Should().BeEmpty();
        _comfy.Uploads.Should().BeEmpty();
    }

    [Fact]
    public async Task Занятая_очередь_GPU_честный_отказ_busy_без_постановки()
    {
        _comfy.Running.Add("чужой");
        _comfy.Pending.Add("чужой-2");
        var adapter = Build(maxQueue: 2);

        var submitted = await adapter.SubmitAsync(Scene(), default);

        submitted.Busy.Should().BeTrue();
        submitted.Error.Should().Contain("Очередь локальной видеокарты занята (2 задач)");
        _comfy.Prompts.Should().BeEmpty();
        _comfy.Uploads.Should().BeEmpty();
    }

    [Fact]
    public async Task ComfyUI_лежит_busy()
    {
        _comfy.Down = true;
        var adapter = Build();

        var submitted = await adapter.SubmitAsync(Scene(), default);

        submitted.Busy.Should().BeTrue();
        submitted.Ticket.Should().BeNull();
    }

    [Fact]
    public async Task Кадр_A_и_B_ставятся_шаблоном_ImageToVideo_и_собираются_клипом()
    {
        _comfy.Running.Add("чужой");
        var adapter = Build();
        var last = LocalMediaTestImages.Png(1344, 768);

        var submitted = await adapter.SubmitAsync(Scene(last, seconds: 10, fast: false), default);

        submitted.Error.Should().BeNull();
        submitted.Ticket.Should().NotBeNullOrEmpty();
        submitted.QueuePosition.Should().Be(1);
        submitted.EtaSeconds.Should().Be(LocalMediaService.ImageToVideoEta(Full, 10, false));
        _comfy.Uploads.Should().HaveCount(2);
        _comfy.Uploads[0].Should().MatchRegex("^lv_[0-9a-f]{32}-in1\\.png$");
        _comfy.Uploads[1].Should().MatchRegex("^lv_[0-9a-f]{32}-last\\.png$");
        var i2v = I2V();
        i2v["width"]!.GetValue<int>().Should().Be(Full.Width);
        i2v["height"]!.GetValue<int>().Should().Be(Full.Height);
        i2v["length"]!.GetValue<int>().Should().Be(ComfyWorkflows.FramesFor(10));
        i2v["prompt"]!.GetValue<string>().Should().Be("идёт снег");
        i2v["first_frame"]!.ToJsonString().Should().Be("[\"img\",0]");
        i2v["last_frame"]!.ToJsonString().Should().Be("[\"img_last\",0]");

        var waiting = await adapter.PollAsync(submitted.Ticket!, default);
        waiting.State.Should().Be(LocalVideoState.Queued, "впереди чужой прогон");
        waiting.QueuePosition.Should().Be(1);

        var mp4 = LocalMediaTestImages.Mp4(Full.Width, Full.Height, 10.1);
        _comfy.CompleteVideo(submitted.Ticket!, _comfy.Uploads[0][..35], mp4);
        var done = await adapter.PollAsync(submitted.Ticket!, default);

        done.State.Should().Be(LocalVideoState.Completed);
        done.File!.Bytes.Should().Equal(mp4);
        done.File.ContentType.Should().Be("video/mp4");
        done.File.Extension.Should().Be(".mp4");
        done.File.HasSound.Should().BeTrue();
    }

    [Fact]
    public async Task Без_последнего_кадра_узла_last_frame_нет()
    {
        var adapter = Build();

        (await adapter.SubmitAsync(Scene(), default)).Error.Should().BeNull();

        _comfy.Uploads.Should().ContainSingle();
        I2V().ContainsKey("last_frame").Should().BeFalse();
        Graph().ContainsKey("img_last").Should().BeFalse();
    }

    [Fact]
    public async Task Портретный_кадр_даёт_портретное_видео_half()
    {
        var adapter = Build();

        var submitted = await adapter.SubmitAsync(Scene(size: "half") with { FirstFrame = LocalMediaTestImages.Png(480, 864) }, default);

        submitted.Error.Should().BeNull();
        I2V()["width"]!.GetValue<int>().Should().Be(Half.Height);
        I2V()["height"]!.GetValue<int>().Should().Be(Half.Width);
        submitted.EtaSeconds.Should().Be(LocalMediaService.ImageToVideoEta((Half.Height, Half.Width), 5, true));
    }

    // Пределы — из общей конфигурации и белого списка local-media, а не своей копии
    [Fact]
    public async Task Длительность_по_общему_потолку_конфига()
    {
        var adapter = Build(maxSeconds: 6);

        (await adapter.SubmitAsync(Scene(seconds: 6), default)).Error.Should().BeNull();
        var over = await adapter.SubmitAsync(Scene(seconds: 7), default);

        over.Error.Should().Be("Длительность — от 1 до 6 секунд.");
        over.Busy.Should().BeFalse();
        _comfy.Prompts.Should().ContainSingle();
    }

    [Theory]
    [InlineData("4k")]
    [InlineData("../full")]
    public async Task Размер_только_из_белого_списка(string size)
    {
        var adapter = Build();

        var over = await adapter.SubmitAsync(Scene(size: size), default);

        over.Error.Should().Contain("full").And.Contain("half");
        _comfy.Uploads.Should().BeEmpty();
    }

    [Fact]
    public async Task Кадр_не_картинка_отказ_до_загрузки()
    {
        var adapter = Build();

        var over = await adapter.SubmitAsync(Scene(last: [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12]), default);

        over.Error.Should().Contain("последний кадр").And.Contain("не картинка");
        _comfy.Uploads.Should().BeEmpty();
        _comfy.Prompts.Should().BeEmpty();
    }

    [Fact]
    public async Task Пустой_запрос_отказ()
    {
        var adapter = Build();

        var over = await adapter.SubmitAsync(Scene() with { Prompt = "  " }, default);

        over.Error.Should().NotBeNullOrEmpty();
        _comfy.Prompts.Should().BeEmpty();
    }

    [Fact]
    public void ETA_по_общей_таблице_без_загрузок_в_ComfyUI()
    {
        var adapter = Build(fastDefault: false);

        adapter.EtaSeconds(Scene()).Should().Be(LocalMediaService.ImageToVideoEta(Full, 5, false));
        adapter.EtaSeconds(Scene(fast: true, seconds: 10)).Should().Be(LocalMediaService.ImageToVideoEta(Full, 10, true));
        adapter.EtaSeconds(Scene(size: "half", seconds: 10)).Should().BeNull("10 с на half не мерены");
        adapter.EtaSeconds(Scene(seconds: 11)).Should().BeNull("неверный запрос времени не обещает");

        _comfy.Uploads.Should().BeEmpty();
        _comfy.Prompts.Should().BeEmpty();
    }

    // ── Апскейл «Макс» (ADR-025, решение 1): вторая тяжёлая задача по латенту завершённой съёмки ──────────────

    private async Task<(LocalVideoMediaAdapter Adapter, string Ticket, string Id)> ShotDone(bool latents = true, byte[]? first = null,
        string size = "full", int seconds = 5)
    {
        var adapter = Build(fastDefault: false);
        var submitted = await adapter.SubmitAsync(Scene(seconds: seconds, size: size) with { FirstFrame = first ?? LocalMediaTestImages.Png(1344, 768) },
            default);
        submitted.Ticket.Should().NotBeNull(submitted.Error);
        var id = _comfy.Uploads[0][..35]; // lv_ + 32 hex
        _comfy.CompleteVideo(submitted.Ticket!, id, [1, 2, 3], latents);
        (await adapter.PollAsync(submitted.Ticket!, default)).State.Should().Be(LocalVideoState.Completed);
        return (adapter, submitted.Ticket!, id);
    }

    [Fact]
    public async Task Апскейл_1440p_ставит_шаблон_по_латентам_съёмки_с_её_кадром_промптом_и_сидом()
    {
        var (adapter, ticket, id) = await ShotDone();

        var up = await adapter.SubmitUpscaleAsync(ticket, "1440p", default);

        up.Error.Should().BeNull();
        up.Ticket.Should().NotBeNullOrEmpty().And.NotBe(ticket);
        up.EtaSeconds.Should().Be(LocalMediaService.Upscale1440Eta(ComfyWorkflows.FramesFor(5), true));
        var graph = Graph(1);
        var scale = graph["up"]!["inputs"]!.AsObject();
        (scale["mode.width"]!.GetValue<int>(), scale["mode.height"]!.GetValue<int>()).Should().Be(ComfyWorkflows.Upscale1440);
        graph.ContainsKey("refine").Should().BeTrue("по умолчанию 1440p доводится тайлами");
        // Латенты съёмки скопированы в корень input под именем с id апскейла, первый кадр — тот же файл, что у съёмки
        _comfy.UploadPaths.Should().Contain(p => p.EndsWith("-video.latent") && p.Contains("lv_")).And.Contain(p => p.EndsWith("-audio.latent"));
        graph["img"]!["inputs"]!["image"]!.GetValue<string>().Should().EndWith(_comfy.Uploads[0]);
        graph["i2v_hi"]!["inputs"]!["prompt"]!.GetValue<string>().Should().Be("идёт снег");
        graph["refine_noise"]!["inputs"]!["noise_seed"]!.GetValue<long>().Should().Be(7, "сид съёмки");
        _ = id;
    }

    [Fact]
    public async Task У_портретной_съёмки_апскейл_меняет_стороны_местами()
    {
        var (adapter, ticket, _) = await ShotDone(first: LocalMediaTestImages.Png(768, 1344));

        var up = await adapter.SubmitUpscaleAsync(ticket, "1440p", default);

        up.Error.Should().BeNull();
        var scale = Graph(1)["up"]!["inputs"]!.AsObject();
        (scale["mode.width"]!.GetValue<int>(), scale["mode.height"]!.GetValue<int>())
            .Should().Be((ComfyWorkflows.Upscale1440.Height, ComfyWorkflows.Upscale1440.Width));
    }

    [Fact]
    public async Task Готовый_апскейл_опрашивается_как_съёмка_и_отдаёт_клип()
    {
        var (adapter, ticket, _) = await ShotDone();
        var up = await adapter.SubmitUpscaleAsync(ticket, "1440p", default);
        (await adapter.PollAsync(up.Ticket!, default)).State.Should().BeOneOf(LocalVideoState.Queued, LocalVideoState.Running);

        _comfy.CompleteVideo(up.Ticket!, "lv_" + new string('a', 32), [9, 9, 9], withLatents: false);
        var poll = await adapter.PollAsync(up.Ticket!, default);

        poll.State.Should().Be(LocalVideoState.Completed);
        poll.File!.Bytes.Should().Equal(9, 9, 9);
    }

    [Fact]
    public async Task Отмена_идущего_апскейла_снимает_его_тикет_адресно_а_не_съёмку()
    {
        var (adapter, ticket, _) = await ShotDone();
        var up = await adapter.SubmitUpscaleAsync(ticket, "1440p", default);
        _comfy.Pending.Remove(up.Ticket!);
        _comfy.Running.Add(up.Ticket!);

        (await adapter.CancelAsync(up.Ticket!, default)).Should().BeTrue();

        _comfy.Interrupted.Should().Equal(up.Ticket!);
    }

    [Fact]
    public async Task Апскейл_только_для_full_и_только_до_1440p_и_только_по_известной_съёмке()
    {
        var (adapter, ticket, _) = await ShotDone(size: "half");
        (await adapter.SubmitUpscaleAsync(ticket, "1440p", default)).Error.Should().Contain("full");
        (await adapter.SubmitUpscaleAsync("нет-такой", "1440p", default)).Error.Should().Contain("не доступна");

        var (full, fullTicket, _) = await ShotDone();
        (await full.SubmitUpscaleAsync(fullTicket, "2k", default)).Error.Should().Contain("1440p");
        _comfy.Prompts.Should().HaveCount(2, "ни один отказ ничего не ставит в ComfyUI");
    }

    [Fact]
    public async Task Без_сохранённого_латента_апскейл_честный_отказ_без_постановки()
    {
        var (adapter, ticket, _) = await ShotDone(latents: false);

        var up = await adapter.SubmitUpscaleAsync(ticket, "1440p", default);

        up.Ticket.Should().BeNull();
        up.Error.Should().Contain("латент");
        _comfy.Prompts.Should().ContainSingle();
    }

    [Fact]
    public async Task Занятая_очередь_апскейла_busy_без_постановки()
    {
        var (adapter, ticket, _) = await ShotDone();
        _comfy.Running.AddRange(["чужой-1", "чужой-2", "чужой-3", "чужой-4"]);

        var up = await adapter.SubmitUpscaleAsync(ticket, "1440p", default);

        up.Busy.Should().BeTrue();
        _comfy.Prompts.Should().ContainSingle();
    }

    [Fact]
    public void ETA_съёмки_и_апскейла_считается_без_кадров_и_для_длинного_ролика_апскейл_не_замерен()
    {
        var adapter = Build(fastDefault: false);
        var noFrames = new LocalVideoRequest("", [], null, 5, "full");

        adapter.EtaSeconds(noFrames).Should().Be(LocalMediaService.ImageToVideoEta(Full, 5, false), "котировка идёт до выбора кадров");
        adapter.UpscaleEtaSeconds(noFrames).Should().Be(LocalMediaService.Upscale1440Eta(ComfyWorkflows.FramesFor(5), true));
        adapter.UpscaleEtaSeconds(noFrames with { Seconds = 10 }).Should().BeNull("апскейл замерен только до 5 с");
        _comfy.Prompts.Should().BeEmpty();
    }

    [Fact]
    public async Task Отмена_ждущей_снимает_её_из_очереди()
    {
        _comfy.Running.Add("чужая");
        _comfy.Pending.Add("ждёт");
        var adapter = Build();

        (await adapter.CancelAsync("ждёт", default)).Should().BeTrue();
        (await adapter.CancelAsync("пропала", default)).Should().BeFalse();
        _comfy.Pending.Should().BeEmpty();
        _comfy.Running.Should().Equal("чужая");
        _comfy.Interrupted.Should().BeEmpty();
    }

    [Fact]
    public async Task Отмена_идущей_прерывает_граф_адресно_по_prompt_id()
    {
        _comfy.Running.Add("идёт");
        _comfy.Pending.Add("чужая-ждёт");
        var adapter = Build();

        (await adapter.CancelAsync("идёт", default)).Should().BeTrue();
        _comfy.Interrupted.Should().Equal("идёт");
        _comfy.Pending.Should().Equal("чужая-ждёт");
    }

    [Fact]
    public async Task Упавшая_задача_ComfyUI_отдаёт_ошибку()
    {
        var adapter = Build();
        var submitted = await adapter.SubmitAsync(Scene(), default);

        _comfy.Fail(submitted.Ticket!, "CUDA out of memory");
        var failed = await adapter.PollAsync(submitted.Ticket!, default);

        failed.State.Should().Be(LocalVideoState.Failed);
        failed.Error.Should().Contain("CUDA out of memory");
    }

    [Fact]
    public async Task Сбой_опроса_временный_задача_жива()
    {
        var adapter = Build();
        var submitted = await adapter.SubmitAsync(Scene(), default);
        _comfy.Down = true;

        var poll = await adapter.PollAsync(submitted.Ticket!, default);

        poll.State.Should().Be(LocalVideoState.Running);
        poll.Warning.Should().NotBeNullOrEmpty();
    }

    [Theory]
    [InlineData("""{"type":"progress","data":{"value":3,"max":8,"prompt_id":"p1","node":"ms"}}""", 0.375, false)]
    [InlineData("""{"type":"progress","data":{"value":3,"max":8,"prompt_id":"чужая"}}""", null, false)]
    [InlineData("""{"type":"progress","data":{"value":3,"max":0,"prompt_id":"p1"}}""", null, false)]
    [InlineData("""{"type":"executing","data":{"node":"ms","prompt_id":"p1"}}""", null, false)]
    [InlineData("""{"type":"execution_success","data":{"prompt_id":"p1"}}""", null, true)]
    [InlineData("""{"type":"execution_error","data":{"prompt_id":"p1"}}""", null, true)]
    [InlineData("не json", null, false)]
    public void Сообщение_сокета_ComfyUI_даёт_долю_шага_только_по_своей_задаче(string text, double? fraction, bool done)
    {
        var parsed = ComfyClient.ParseProgressMessage(text, "p1");

        parsed.Fraction.Should().Be(fraction);
        parsed.Done.Should().Be(done);
    }

    [Fact]
    public async Task Прогресс_видео_из_общего_трекера_и_реестр_снимается_на_выходах()
    {
        var ledger = new ComfyPromptLedger();
        var tracker = new ComfyProgressTracker(id => ledger.Contains(id));
        var adapter = Build(steps: tracker, ledger: ledger);

        var submitted = (await adapter.SubmitAsync(Scene(), default))!;
        var prompt = submitted.Ticket!;

        // После постановки запись в реестре активна
        ledger.Active().Should().HaveCount(1);

        // Переводим в Running (position 0), чтобы PollAsync читал процент
        _comfy.Pending.Clear();
        _comfy.Running.Add(prompt);

        // До события progress шагов нет
        var before = await adapter.PollAsync(prompt, default);
        before.State.Should().Be(LocalVideoState.Running);
        before.Percent.Should().BeNull();

        // Событие progress с реальным prompt_id: трекер хранит долю, опрос отдаёт процент
        tracker.Handle("""{"type":"progress","data":{"value":4,"max":8,"prompt_id":"PROMPT","node":"sampler"}}"""
            .Replace("PROMPT", prompt));
        var after = await adapter.PollAsync(prompt, default);
        after.Percent.Should().Be(0.5);

        // Отмена: Complete снимает запись из реестра (она больше не активна)
        (await adapter.CancelAsync(prompt, default)).Should().BeTrue();
        ledger.Active().Should().BeEmpty();
    }

    // Шаг 23: метка происхождения из запроса уходит в реестр; null-Origin — запись без падения.
    // Мутация: убрать ledger?.Register в SubmitAsync → тест краснеет (записи в реестре нет).
    [Fact]
    public async Task Метка_Происхождения_Из_Запроса_Уходит_В_Реестр()
    {
        var ledger = new ComfyPromptLedger();
        var adapter = Build(ledger: ledger);
        var origin = new LocalJobOrigin("owner-1", "chat-1", "video", "image_to_video", true);

        var submitted = await adapter.SubmitAsync(Scene() with { Origin = origin }, default);
        submitted.Ticket.Should().NotBeNullOrEmpty(submitted.Error);

        // Запись в реестре с переданным Origin
        ledger.Active().Should().ContainSingle();
        ledger.Active().Single().Origin.Should().Be(origin);

        // null-Origin: запись создаётся, не падает
        var submitted2 = await adapter.SubmitAsync(Scene(), default);
        submitted2.Ticket.Should().NotBeNullOrEmpty(submitted2.Error);
        ledger.Active().Should().HaveCount(2);
        ledger.Active().Should().Contain(r => r.Origin == null);
    }

    // Два сэмплера подряд: процент ТЕКУЩЕЙ ноды откатывается к нулю на втором,
    // но шкала не должна убывать — запоминаем максимум
    [Fact]
    public async Task Прогресс_Видео_Не_Убывает_На_Втором_Сэмплере()
    {
        var ledger = new ComfyPromptLedger();
        var tracker = new ComfyProgressTracker(id => ledger.Contains(id));
        var adapter = Build(steps: tracker, ledger: ledger);

        var submitted = (await adapter.SubmitAsync(Scene(), default))!;
        var prompt = submitted.Ticket!;

        _comfy.Pending.Clear();
        _comfy.Running.Add(prompt);

        // Первый сэмплер: 8/8 → 99% (потолок)
        tracker.Handle("""{"type":"progress","data":{"value":8,"max":8,"prompt_id":"PROMPT","node":"sampler1"}}"""
            .Replace("PROMPT", prompt));
        var first = await adapter.PollAsync(prompt, default);
        first.Percent.Should().Be(0.99, "первый сэмплер завершён — 99%");

        // Второй сэмплер: 1/8 → 12%, но шкала не должна упасть ниже 99%
        tracker.Handle("""{"type":"progress","data":{"value":1,"max":8,"prompt_id":"PROMPT","node":"sampler2"}}"""
            .Replace("PROMPT", prompt));
        var second = await adapter.PollAsync(prompt, default);
        second.Percent.Should().Be(0.99, "второй сэмплер: шкала не убывает, держит максимум первого");
    }

    // ─── Шаг 26б: Gpu из общего снимка монитора ─────────────────────────────────────────────

    private async Task<ComfyQueueMonitor> BuildMonitorWithSnapshot(
        string runningId, string pendingId, int? runningEta = 60, DateTimeOffset? runningStart = null)
    {
        // FakeComfy: одна идущая + одна ждущая
        _comfy.Running.Add(runningId);
        _comfy.Pending.Add(pendingId);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["LocalMedia:Enabled"] = "true",
            ["LocalMedia:ComfyUrl"] = "http://comfy.test:8188",
        }).Build();
        var client = new ComfyClient(new FakeComfyFactory(_comfy), config);
        var ledger = new ComfyPromptLedger();
        var monitor = new ComfyQueueMonitor(client, ledger, config, NullLogger<ComfyQueueMonitor>.Instance);
        await monitor.TickAsync(default);
        return monitor;
    }

    [Fact]
    public async Task Gpu_заполняется_из_снимка_монитора_место_старт_шаги()
    {
        // Идущая чужая + наша в очереди
        var monitor = await BuildMonitorWithSnapshot("чужой", "наш");
        var ledger = new ComfyPromptLedger();
        ledger.Register("наш", new LocalJobOrigin("owner1", "sess1", "video", "i2v", false), 120);
        var steps = new ComfyProgressTracker(_ => true);
        steps.Handle("""{"type":"progress","data":{"value":3,"max":10,"prompt_id":"наш","node":"sampler"}}""");

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["LocalMedia:Enabled"] = "true",
            ["LocalMedia:ComfyUrl"] = "http://comfy.test:8188",
            ["LocalMedia:MaxComfyQueue"] = "4",
            ["LocalMedia:MaxVideoSeconds"] = "10",
        }).Build();
        var client = new ComfyClient(new FakeComfyFactory(_comfy), config);
        var images = new LocalImageMediaAdapter(client, config, NullLogger<LocalImageMediaAdapter>.Instance);
        var adapter = new LocalVideoMediaAdapter(client, config, images, NullLogger<LocalVideoMediaAdapter>.Instance,
            null, null, steps, ledger, monitor);

        var poll = await adapter.PollAsync("наш", default);
        poll.Gpu.Should().NotBeNull("монитор есть и снимок заполнен");
        poll.Gpu!.Ahead.Should().Be(1, "одна задача впереди");
        poll.Gpu.Steps.Should().NotBeNull("шаги из трекера");
        poll.Gpu.Steps!.Step.Should().Be(3);
        poll.Gpu.Steps.Total.Should().Be(10);
        poll.Gpu.PercentExact.Should().BeTrue("первый этап — точные");
        poll.Gpu.Percent.Should().Be(30);
    }

    [Fact]
    public async Task Gpu_этап_больше_1_процент_по_ETA_а_не_шаговый()
    {
        // Идущая чужая + наша в очереди; наша на втором этапе (stage=2) — точный шаговый процент не отдаём
        _comfy.Running.Add("чужой");
        _comfy.Pending.Add("наш");
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["LocalMedia:Enabled"] = "true",
            ["LocalMedia:ComfyUrl"] = "http://comfy.test:8188",
            ["LocalMedia:MaxComfyQueue"] = "4",
            ["LocalMedia:MaxVideoSeconds"] = "10",
        }).Build();
        var client = new ComfyClient(new FakeComfyFactory(_comfy), config);
        var ledger = new ComfyPromptLedger();
        ledger.Register("наш", new LocalJobOrigin("owner1", "sess1", "video", "i2v", false), 120);
        ledger.MarkStarted("наш");
        // Второй этап: две разные ноды с прогрессом → stage=2
        var steps = new ComfyProgressTracker(_ => true);
        steps.Handle("""{"type":"progress","data":{"value":3,"max":10,"prompt_id":"наш","node":"sampler1"}}""");
        steps.Handle("""{"type":"progress","data":{"value":3,"max":10,"prompt_id":"наш","node":"sampler2"}}""");
        var monitor = new ComfyQueueMonitor(client, ledger, config, NullLogger<ComfyQueueMonitor>.Instance);
        await monitor.TickAsync(default);

        var images = new LocalImageMediaAdapter(client, config, NullLogger<LocalImageMediaAdapter>.Instance);
        var adapter = new LocalVideoMediaAdapter(client, config, images, NullLogger<LocalVideoMediaAdapter>.Instance,
            null, null, steps, ledger, monitor);

        var poll = await adapter.PollAsync("наш", default);
        poll.Gpu.Should().NotBeNull();
        // Этап > 1: шаговый процент (30) не выдаём как есть — оценка по ETA, PercentExact=false
        poll.Gpu!.PercentExact.Should().BeFalse("этап > 1 — не точный");
        poll.Gpu.Percent.Should().NotBe(30, "шаговый процент второго этапа не выдаётся как есть");
    }

    [Fact]
    public async Task Null_монитор_Gpu_null_поведение_прежнее()
    {
        // Без монитора: Gpu = null, остальное как раньше
        _comfy.Running.Add("наш");
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["LocalMedia:Enabled"] = "true",
            ["LocalMedia:ComfyUrl"] = "http://comfy.test:8188",
            ["LocalMedia:MaxComfyQueue"] = "4",
            ["LocalMedia:MaxVideoSeconds"] = "10",
        }).Build();
        var client = new ComfyClient(new FakeComfyFactory(_comfy), config);
        var images = new LocalImageMediaAdapter(client, config, NullLogger<LocalImageMediaAdapter>.Instance);
        var adapter = new LocalVideoMediaAdapter(client, config, images, NullLogger<LocalVideoMediaAdapter>.Instance);

        var poll = await adapter.PollAsync("наш", default);
        poll.State.Should().Be(LocalVideoState.Running);
        poll.Gpu.Should().BeNull("монитора нет — Gpu null");
    }

    // Minor 3 (адаптер, MAJOR): стенд лежит (GetHistoryAsync → ComfyException → catch адаптера),
    // задача в pending снимка монитора. Позиция из снимка (1), не хардкод 0: Gpu.Running=false,
    // Ahead=1 — не ложный «идёт». Мутация «Build(ticket, 0)» в catch (ComfyException) → Running=true → красный.
    [Fact]
    public async Task Gpu_адаптер_СтендDown_ЗадачаВPending_НеЛожныйRunning()
    {
        _comfy.Running.Add("чужой");
        _comfy.Pending.Add("наш");
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["LocalMedia:Enabled"] = "true",
            ["LocalMedia:ComfyUrl"] = "http://comfy.test:8188",
            ["LocalMedia:MaxComfyQueue"] = "4",
            ["LocalMedia:MaxVideoSeconds"] = "10",
        }).Build();
        var client = new ComfyClient(new FakeComfyFactory(_comfy), config);
        var ledger = new ComfyPromptLedger();
        var monitor = new ComfyQueueMonitor(client, ledger, config, NullLogger<ComfyQueueMonitor>.Instance);
        await monitor.TickAsync(default);
        _comfy.Down = true;
        var images = new LocalImageMediaAdapter(client, config, NullLogger<LocalImageMediaAdapter>.Instance);
        var adapter = new LocalVideoMediaAdapter(client, config, images, NullLogger<LocalVideoMediaAdapter>.Instance,
            null, null, null, ledger, monitor);

        var poll = await adapter.PollAsync("наш", default);

        poll.Gpu.Should().NotBeNull("задача в pending снимка — позиция есть");
        poll.Gpu!.Running.Should().BeFalse("задача не в queue_running снимка → Running=false, не ложный «идёт»");
        poll.Gpu.Ahead.Should().Be(1, "позиция из снимка: 1 идущая впереди");
    }

    // Minor 3 (адаптер, MAJOR): стенд лежит, задачи НЕТ в снимке монитора — позиции нет → Gpu=null.
    // Мутация «Build(ticket, 0)» → Gpu!=null, Running=true → красный.
    [Fact]
    public async Task Gpu_адаптер_СтендDown_ЗадачиНетВСнимке_GpuNull()
    {
        _comfy.Running.Add("чужой");
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["LocalMedia:Enabled"] = "true",
            ["LocalMedia:ComfyUrl"] = "http://comfy.test:8188",
            ["LocalMedia:MaxComfyQueue"] = "4",
            ["LocalMedia:MaxVideoSeconds"] = "10",
        }).Build();
        var client = new ComfyClient(new FakeComfyFactory(_comfy), config);
        var ledger = new ComfyPromptLedger();
        var monitor = new ComfyQueueMonitor(client, ledger, config, NullLogger<ComfyQueueMonitor>.Instance);
        await monitor.TickAsync(default);
        _comfy.Down = true;
        var images = new LocalImageMediaAdapter(client, config, NullLogger<LocalImageMediaAdapter>.Instance);
        var adapter = new LocalVideoMediaAdapter(client, config, images, NullLogger<LocalVideoMediaAdapter>.Instance,
            null, null, null, ledger, monitor);

        var poll = await adapter.PollAsync("наш", default);

        poll.Gpu.Should().BeNull("задачи нет в снимке — позиции нет, Gpu=null, ложного «идёт» нет");
    }

    [Fact]
    public async Task Тест_изоляция_Busy_содержит_только_kind_heavy_остаток()
    {
        // Чужая тяжёлая задача впереди: Busy = (kind, heavy, remaining), без ownerId/sessionId/prompt/slug
        _comfy.Running.Add("чужой");
        _comfy.Pending.Add("наш");
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["LocalMedia:Enabled"] = "true",
            ["LocalMedia:ComfyUrl"] = "http://comfy.test:8188",
            ["LocalMedia:MaxComfyQueue"] = "4",
            ["LocalMedia:MaxVideoSeconds"] = "10",
        }).Build();
        var client = new ComfyClient(new FakeComfyFactory(_comfy), config);
        var ledger = new ComfyPromptLedger();
        // Чужой: heavy, eta 300
        ledger.Register("чужой", new LocalJobOrigin("otherOwner", "otherSess", "video", "upscale", Heavy: true), 300);
        var monitor = new ComfyQueueMonitor(client, ledger, config, NullLogger<ComfyQueueMonitor>.Instance);
        await monitor.TickAsync(default);

        var images = new LocalImageMediaAdapter(client, config, NullLogger<LocalImageMediaAdapter>.Instance);
        var adapter = new LocalVideoMediaAdapter(client, config, images, NullLogger<LocalVideoMediaAdapter>.Instance,
            null, null, null, ledger, monitor);

        var poll = await adapter.PollAsync("наш", default);
        poll.Gpu.Should().NotBeNull();
        poll.Gpu!.Busy.Should().NotBeNull("чужая тяжёлая впереди");
        poll.Gpu.Busy!.Kind.Should().Be("video");
        poll.Gpu.Busy.Heavy.Should().BeTrue();
        // Сериализация: нет ownerId/sessionId/prompt/slug
        var json = System.Text.Json.JsonSerializer.SerializeToElement(poll.Gpu.Busy);
        var jsonStr = json.GetRawText();
        jsonStr.Should().NotContain("otherOwner");
        jsonStr.Should().NotContain("otherSess");
        jsonStr.Should().NotContain("upscale");
    }

    [Fact]
    public async Task Тест_Busy_наша_идёт_чужая_ждёт_null()
    {
        // Наша задача идёт (Running[0]), чужая тяжёлая ждёт (Pending[0]): мы не в очереди,
        // поэтому чужая нас не блокирует — Busy = null. Мутация «всегда ставить Busy» красит этот тест.
        _comfy.Running.Add("наш");
        _comfy.Pending.Add("чужой");
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["LocalMedia:Enabled"] = "true",
            ["LocalMedia:ComfyUrl"] = "http://comfy.test:8188",
            ["LocalMedia:MaxComfyQueue"] = "4",
            ["LocalMedia:MaxVideoSeconds"] = "10",
        }).Build();
        var client = new ComfyClient(new FakeComfyFactory(_comfy), config);
        var ledger = new ComfyPromptLedger();
        // Наша: идёт, не тяжёлая; чужая: тяжёлая, ждёт
        ledger.Register("наш", new LocalJobOrigin("owner1", "sess1", "video", "i2v", Heavy: false), 60);
        ledger.Register("чужой", new LocalJobOrigin("otherOwner", "otherSess", "video", "upscale", Heavy: true), 300);
        var monitor = new ComfyQueueMonitor(client, ledger, config, NullLogger<ComfyQueueMonitor>.Instance);
        await monitor.TickAsync(default);

        var images = new LocalImageMediaAdapter(client, config, NullLogger<LocalImageMediaAdapter>.Instance);
        var adapter = new LocalVideoMediaAdapter(client, config, images, NullLogger<LocalVideoMediaAdapter>.Instance,
            null, null, null, ledger, monitor);

        var poll = await adapter.PollAsync("наш", default);
        poll.Gpu.Should().NotBeNull();
        // M2: идущая задача — Running=true, Ahead=null (не «Следующая»/0)
        poll.Gpu!.Running.Should().BeTrue("наша задача идёт");
        poll.Gpu.Ahead.Should().BeNull("идущая — Ahead=null, а не 0");
        poll.Gpu.Busy.Should().BeNull("наша задача идёт — чужая в очереди нас не блокирует");
    }

    // Регрессия (дефект): монитор в простое, снимок старше отправки — промпт в живой очереди, не в снимке.
    // Статус решает живой /queue: задача Running, не «пропала». Мутация: прежняя логика → Failed → красный.
    [Fact]
    public async Task СнимокСтаршеОтправки_ПромптВЖивойОчереди_ЗадачаНеПропала()
    {
        _comfy.Running.Add("чужой");
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["LocalMedia:Enabled"] = "true",
            ["LocalMedia:ComfyUrl"] = "http://comfy.test:8188",
            ["LocalMedia:MaxComfyQueue"] = "4",
            ["LocalMedia:MaxVideoSeconds"] = "10",
        }).Build();
        var client = new ComfyClient(new FakeComfyFactory(_comfy), config);
        var ledger = new ComfyPromptLedger();
        var monitor = new ComfyQueueMonitor(client, ledger, config, NullLogger<ComfyQueueMonitor>.Instance);
        await monitor.TickAsync(default); // снимок: чужой в Running, нашего нет
        _comfy.Running.Add("наш"); // промпт отправлен ПОСЛЕ снимка
        var images = new LocalImageMediaAdapter(client, config, NullLogger<LocalImageMediaAdapter>.Instance);
        var adapter = new LocalVideoMediaAdapter(client, config, images, NullLogger<LocalVideoMediaAdapter>.Instance,
            null, null, null, ledger, monitor);

        var poll = await adapter.PollAsync("наш", default);

        poll.State.Should().Be(LocalVideoState.Running, "живой /queue видит промпт — не «пропала»");
    }
}
