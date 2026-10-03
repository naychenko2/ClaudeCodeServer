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

    private LocalVideoMediaAdapter Build(bool enabled = true, int maxQueue = 4, int maxSeconds = 10, bool fastDefault = true)
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
        return new LocalVideoMediaAdapter(client, config, images, NullLogger<LocalVideoMediaAdapter>.Instance);
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
}
