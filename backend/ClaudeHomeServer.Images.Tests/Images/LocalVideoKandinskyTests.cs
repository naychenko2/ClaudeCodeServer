using System.Text.Json.Nodes;
using ClaudeHomeServer.Services.Images.LocalMedia;
using ClaudeHomeServer.Services.ImageEditor;
using ClaudeHomeServer.Services.Media;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services;

// Kandinsky 6 Lite (Ш5): граф по образцу ночного замера media-bench и ветка адаптера шва ILocalVideoMedia
public class LocalVideoKandinskyTests
{
    private static JsonObject Inputs(JsonObject wf, string node) => wf[node]!["inputs"]!.AsObject();

    private static string Link(JsonObject wf, string node, string input) => Inputs(wf, node)[input]!.ToJsonString();

    [Fact]
    public void Граф_по_тексту_без_кадра_и_без_ссылочного_латента()
    {
        var wf = ComfyWorkflows.Kandinsky6Video("лиса идёт по снегу", null, 11, "ccs/x");

        Inputs(wf, "unet")["unet_name"]!.GetValue<string>().Should().Be(ComfyWorkflows.Kandinsky6UnetName);
        Inputs(wf, "clip")["type"]!.GetValue<string>().Should().Be("kandinsky5");
        var lat = Inputs(wf, "lat");
        lat["width"]!.GetValue<int>().Should().Be(864);
        lat["height"]!.GetValue<int>().Should().Be(480);
        lat["length"]!.GetValue<int>().Should().Be(121);
        wf.Should().NotContainKey("i2v");
        wf.Should().NotContainKey("rr");
        wf.Should().NotContainKey("img");
        Link(wf, "smp", "positive").Should().Be("[\"pos\",0]");
        Link(wf, "smp", "latent_image").Should().Be("[\"lat\",0]");
        Inputs(wf, "smp")["seed"]!.GetValue<long>().Should().Be(11);
        Link(wf, "vdec", "samples").Should().Be("[\"smp\",0]");
        Link(wf, "adec", "joint_latent").Should().Be("[\"smp\",0]");
        Inputs(wf, "pos")["video_caption"]!.GetValue<string>().Should().Be("лиса идёт по снегу");
        Inputs(wf, "video")["audio"]!.ToJsonString().Should().Be("[\"adec\",0]");
    }

    [Fact]
    public void Граф_по_кадру_ведёт_ссылочный_латент_через_i2v_и_снимает_его_перед_декодом()
    {
        var wf = ComfyWorkflows.Kandinsky6Video("оживи кафе", "lv_in1.png", 5, "ccs/x");

        Inputs(wf, "img")["image"]!.GetValue<string>().Should().Be("lv_in1.png");
        Inputs(wf, "isc")["crop"]!.GetValue<string>().Should().Be("center");
        Link(wf, "i2v", "reference_latent").Should().Be("[\"ienc\",0]");
        Link(wf, "smp", "positive").Should().Be("[\"i2v\",0]");
        Link(wf, "smp", "negative").Should().Be("[\"i2v\",1]");
        Link(wf, "smp", "latent_image").Should().Be("[\"i2v\",2]");
        Link(wf, "rr", "joint_latent").Should().Be("[\"smp\",0]");
        Link(wf, "vdec", "samples").Should().Be("[\"rr\",0]");
        Link(wf, "adec", "joint_latent").Should().Be("[\"rr\",0]");
    }

    [Fact]
    public void Граф_тяжёлый_для_арбитра_по_маркеру_video()
    {
        // Маркеры арбитра GPU на Video (ARBITER_HEAVY_MARKERS): подстрока class_type или строкового входа
        string[] markers = ["h3", "minimax", "video", "fasth3", "whisper"];
        var text = ComfyWorkflows.Kandinsky6Video("x", null, 1, "ccs/x").ToJsonString().ToLowerInvariant();
        markers.Should().Contain(m => text.Contains(m));
    }

    private static LocalVideoMediaAdapter Build(FakeComfy comfy)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(Path.GetTempPath(), "k6_" + Guid.NewGuid().ToString("N"), "projects.json"),
            ["LocalMedia:Enabled"] = "true",
            ["LocalMedia:ComfyUrl"] = "http://comfy.test:8188",
        }).Build();
        var client = new ComfyClient(new FakeComfyFactory(comfy), config);
        var images = new LocalImageMediaAdapter(client, config, NullLogger<LocalImageMediaAdapter>.Instance);
        return new LocalVideoMediaAdapter(client, config, images, NullLogger<LocalVideoMediaAdapter>.Instance);
    }

    private static LocalVideoRequest K(byte[]? first = null, int seconds = 5, byte[]? last = null) =>
        new("лиса идёт по снегу", first ?? [], last, seconds, "half", null, 7, Model: LocalVideoModels.Kandinsky6Lite);

    [Fact]
    public async Task Адаптер_T2V_ставит_граф_Kandinsky_без_загрузки_кадра()
    {
        var comfy = new FakeComfy();
        var adapter = Build(comfy);

        var submitted = await adapter.SubmitAsync(K(), default);

        submitted.Ticket.Should().NotBeNull(submitted.Error);
        submitted.EtaSeconds.Should().Be(LocalVideoMediaAdapter.KandinskyEtaSeconds);
        comfy.Uploads.Should().BeEmpty();
        var graph = comfy.Prompts.Single()["prompt"]!.AsObject();
        graph.Should().ContainKey("smp");
        graph.Should().NotContainKey("i2v");
    }

    [Fact]
    public async Task Адаптер_I2V_грузит_кадр_и_отдаёт_ролик_со_звуком()
    {
        var comfy = new FakeComfy();
        var adapter = Build(comfy);

        var submitted = await adapter.SubmitAsync(K(LocalMediaTestImages.Png(864, 480)), default);

        submitted.Ticket.Should().NotBeNull(submitted.Error);
        comfy.Uploads.Should().ContainSingle();
        comfy.Prompts.Single()["prompt"]!.AsObject().Should().ContainKey("i2v");
        comfy.CompleteVideo(submitted.Ticket!, comfy.Uploads[0][..35], [1, 2, 3], withLatents: false);
        var poll = await adapter.PollAsync(submitted.Ticket!, default);
        poll.State.Should().Be(LocalVideoState.Completed);
        poll.File!.HasSound.Should().BeTrue();
        adapter.CanUpscale(submitted.Ticket!).Should().BeFalse("у Kandinsky нет латентов для увеличения");
    }

    [Theory]
    [InlineData(10, false, "5 секунд")]
    [InlineData(5, true, "последний кадр")]
    public async Task Адаптер_отказывает_в_том_чего_модель_не_умеет(int seconds, bool withLast, string part)
    {
        var comfy = new FakeComfy();
        var adapter = Build(comfy);

        var submitted = await adapter.SubmitAsync(K(seconds: seconds, last: withLast ? LocalMediaTestImages.Png(64, 64) : null), default);

        submitted.Ticket.Should().BeNull();
        submitted.Error.Should().Contain(part);
        comfy.Prompts.Should().BeEmpty();
    }

    [Fact]
    public void Время_только_для_5_секунд_и_без_апскейла()
    {
        var adapter = Build(new FakeComfy());
        adapter.EtaSeconds(K()).Should().Be(LocalVideoMediaAdapter.KandinskyEtaSeconds);
        adapter.EtaSeconds(K(seconds: 8)).Should().BeNull();
        adapter.UpscaleEtaSeconds(K()).Should().BeNull();
    }
}
