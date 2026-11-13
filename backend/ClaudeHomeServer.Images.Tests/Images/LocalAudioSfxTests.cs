using System.Text.Json.Nodes;
using ClaudeHomeServer.Services.Images.LocalMedia;
using ClaudeHomeServer.Services.Media;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services;

// Эффекты по тексту и звук по видео (Ш4) через шов ILocalAudioMedia поверх фейкового ComfyUI: граф каждой
// модели, пределы, ETA из общей статики, сбор результата одним файлом main
public class LocalAudioSfxTests
{
    private readonly FakeComfy _comfy = new();

    private LocalAudioMediaAdapter Build()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["LocalMedia:Enabled"] = "true",
            ["LocalMedia:AudioEnabled"] = "true",
            ["LocalMedia:ComfyUrl"] = "http://comfy.test:8188",
        }).Build();
        var client = new ComfyClient(new FakeComfyFactory(_comfy), config);
        var images = new LocalImageMediaAdapter(client, config, NullLogger<LocalImageMediaAdapter>.Instance);
        return new LocalAudioMediaAdapter(client, config, images, NullLogger<LocalAudioMediaAdapter>.Instance);
    }

    private static LocalAudioRequest Sfx(string engine, double? seconds = null, string prompt = "glass breaks") =>
        new(LocalAudioOp.Sfx, prompt, new JsonObject { ["engine"] = engine }.With("duration_seconds", seconds));

    private static LocalAudioRequest V2A(string engine, double videoSeconds = 5, string prompt = "footsteps") =>
        new(LocalAudioOp.VideoToAudio, prompt, new JsonObject { ["engine"] = engine }, Video: LocalMediaTestImages.Mp4(864, 480, videoSeconds));

    private JsonObject Graph(int k = 0) => _comfy.Prompts[k]["prompt"]!.AsObject();

    [Fact]
    public async Task MossSfx_идёт_воркером_sfx_moss_с_параметрами_и_ETA()
    {
        var submitted = await Build().SubmitAsync(Sfx("moss", 5), default);

        submitted.Error.Should().BeNull();
        submitted.EtaSeconds.Should().Be(LocalMediaService.MossSfxEta(5));
        var worker = Graph()["worker"]!["inputs"]!.AsObject();
        worker["op"]!.GetValue<string>().Should().Be("sfx_moss");
        var p = JsonNode.Parse(worker["params_json"]!.GetValue<string>())!.AsObject();
        p["prompt"]!.GetValue<string>().Should().Be("glass breaks");
        p["duration"]!.GetValue<double>().Should().Be(5);
        worker["inputs"]!.GetValue<string>().Should().BeEmpty();
    }

    [Fact]
    public async Task MmAudio_по_тексту_без_видео_в_графе()
    {
        var submitted = await Build().SubmitAsync(Sfx("mmaudio", 6), default);

        submitted.Error.Should().BeNull();
        var g = Graph();
        g["smp"]!["class_type"]!.GetValue<string>().Should().Be("MMAudioSampler");
        g["smp"]!["inputs"]!["images"].Should().BeNull();
        g.ContainsKey("lv").Should().BeFalse();
        g["save"]!["class_type"]!.GetValue<string>().Should().Be("SaveAudio");
        submitted.EtaSeconds.Should().Be(LocalMediaService.MmAudioTextEta(6));
    }

    [Theory]
    [InlineData("moss", 11)]
    [InlineData("mmaudio", 9)]
    public async Task Sfx_длиннее_предела_модели_отказ_без_постановки(string engine, double seconds)
    {
        var submitted = await Build().SubmitAsync(Sfx(engine, seconds), default);

        submitted.Ticket.Should().BeNull();
        submitted.Error.Should().Contain("duration_seconds");
        _comfy.Prompts.Should().BeEmpty();
    }

    [Fact]
    public async Task Sfx_без_prompt_отказ()
    {
        var submitted = await Build().SubmitAsync(Sfx("moss", 5, prompt: " "), default);

        submitted.Error.Should().Contain("prompt");
        _comfy.Prompts.Should().BeEmpty();
    }

    [Theory]
    [InlineData("foley-xl", ComfyWorkflows.FoleyXl)]
    [InlineData("foley-xxl", ComfyWorkflows.FoleyXxl)]
    public async Task Foley_берёт_видео_и_частоту_кадров_из_LoadVideo(string engine, string model)
    {
        var submitted = await Build().SubmitAsync(V2A(engine), default);

        submitted.Error.Should().BeNull();
        _comfy.Uploads.Should().ContainSingle(u => u.EndsWith("-src.mp4"));
        var g = Graph();
        g["hm"]!["inputs"]!["model_name"]!.GetValue<string>().Should().Be(model);
        g["lv"]!["inputs"]!["file"]!.GetValue<string>().Should().EndWith("-src.mp4");
        var smp = g["smp"]!["inputs"]!;
        smp["image"]!.ToJsonString().Should().Be("[\"comp\",0]");
        smp["frame_rate"]!.ToJsonString().Should().Be("[\"comp\",2]");
        smp["prompt"]!.GetValue<string>().Should().Be("footsteps");
        submitted.EtaSeconds.Should().Be(LocalMediaService.FoleyEta(engine == "foley-xxl", 5));
    }

    [Fact]
    public async Task MmAudio_по_видео_подаёт_кадры_в_сэмплер()
    {
        var submitted = await Build().SubmitAsync(V2A("mmaudio", 5), default);

        submitted.Error.Should().BeNull();
        Graph()["smp"]!["inputs"]!["images"]!.ToJsonString().Should().Be("[\"comp\",0]");
        submitted.EtaSeconds.Should().Be(LocalMediaService.MmAudioVideoEta(5));
    }

    [Theory]
    [InlineData("mmaudio", 9)]
    [InlineData("foley-xl", 11)]
    public async Task Видео_длиннее_предела_модели_отказ(string engine, double seconds)
    {
        var submitted = await Build().SubmitAsync(V2A(engine, seconds), default);

        submitted.Ticket.Should().BeNull();
        submitted.Error.Should().Contain("озвучивает клипы до");
        _comfy.Prompts.Should().BeEmpty();
    }

    [Fact]
    public async Task VideoToAudio_без_видео_отказ_а_звук_вместо_видео_не_годится()
    {
        var adapter = Build();

        (await adapter.SubmitAsync(new LocalAudioRequest(LocalAudioOp.VideoToAudio, "x", new JsonObject()), default))
            .Error.Should().Contain("video");
        (await adapter.SubmitAsync(new LocalAudioRequest(LocalAudioOp.VideoToAudio, "x", new JsonObject(), Video: [1, 2, 3, 4]), default))
            .Error.Should().Contain("не видео");
        _comfy.Prompts.Should().BeEmpty();
    }

    [Theory]
    [InlineData("foley-xl", 5, false)]
    [InlineData("foley-xxl", 12, true)]
    [InlineData("mmaudio", 20, false)]
    public void Котировка_звука_по_видео_без_клипа_берёт_длину_из_запроса(string engine, double seconds, bool xxl)
    {
        var request = new LocalAudioRequest(LocalAudioOp.VideoToAudio, "x",
            new JsonObject { ["engine"] = engine, ["duration_seconds"] = seconds });

        var eta = Build().EtaSeconds(request);

        var clamped = Math.Min(seconds, engine == "mmaudio" ? LocalMediaService.MmAudioMaxSeconds : LocalMediaService.FoleyMaxSeconds);
        eta.Should().Be(engine == "mmaudio" ? LocalMediaService.MmAudioVideoEta(clamped) : LocalMediaService.FoleyEta(xxl, clamped));
        Build().EtaSeconds(new LocalAudioRequest(LocalAudioOp.VideoToAudio, "x", new JsonObject { ["engine"] = engine })).Should().BeNull();
    }

    [Fact]
    public async Task Результат_flac_собирается_одним_main()
    {
        var adapter = Build();
        var submitted = await adapter.SubmitAsync(V2A("foley-xl"), default);

        _comfy.CompleteAudio(submitted.Ticket!, [], ("la_x_00001.flac", [1, 2, 3]));
        var done = await adapter.PollAsync(submitted.Ticket!, default);

        done.State.Should().Be(LocalAudioState.Completed);
        done.Files.Should().ContainSingle().Which.Should().Match<LocalAudioFile>(f =>
            f.Role == LocalAudioRoles.Main && f.Extension == ".flac" && f.ContentType == "audio/flac");
    }

    [Fact]
    public void Узел_воркера_и_белый_список_знают_sfx_moss()
    {
        ComfyWorkflows.WorkerOps.Should().Contain("sfx_moss");
        LocalMediaOps.IsAudio(LocalMediaOps.AudioSfx).Should().BeTrue();
        LocalMediaOps.IsAudio(LocalMediaOps.VideoToAudio).Should().BeTrue();
        LocalMediaOps.KindOf(LocalMediaOps.VideoToAudio).Should().Be("audio");
    }
}

internal static class JsonObjectExtensions
{
    public static JsonObject With(this JsonObject obj, string key, double? value)
    {
        if (value is { } v) obj[key] = v;
        return obj;
    }
}
