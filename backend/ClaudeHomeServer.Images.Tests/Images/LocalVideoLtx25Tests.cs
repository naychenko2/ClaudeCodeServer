using System.Text.Json.Nodes;
using ClaudeHomeServer.Services.Images.LocalMedia;
using ClaudeHomeServer.Services.Media;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services;

// LTX-2.5: граф по шаблонам ComfyUI 0.45.2 (два прохода, латентный апскейл) и ветка адаптера шва ILocalVideoMedia
public class LocalVideoLtx25Tests
{
    private static JsonObject Inputs(JsonObject wf, string node) => wf[node]!["inputs"]!.AsObject();

    private static string Link(JsonObject wf, string node, string input) => Inputs(wf, node)[input]!.ToJsonString();

    [Fact]
    public void Граф_по_тексту_два_прохода_без_кадра()
    {
        var wf = ComfyWorkflows.Ltx25Video("кот на подоконнике", null, null, 5, portrait: false, 11, "ccs/x");

        Inputs(wf, "unet")["unet_name"]!.GetValue<string>().Should().Be(ComfyWorkflows.Ltx25UnetName);
        Inputs(wf, "clip")["type"]!.GetValue<string>().Should().Be("ltxv");
        var lat = Inputs(wf, "vlat");
        lat["width"]!.GetValue<int>().Should().Be(640);
        lat["height"]!.GetValue<int>().Should().Be(352);
        lat["length"]!.GetValue<int>().Should().Be(121);
        Inputs(wf, "alat")["frames_number"]!.GetValue<int>().Should().Be(121);
        wf.Should().NotContainKey("img");
        wf.Should().NotContainKey("i2v1");
        Link(wf, "av1", "video_latent").Should().Be("[\"vlat\",0]");
        Link(wf, "up", "samples").Should().Be("[\"sep1\",0]");
        Link(wf, "av2", "video_latent").Should().Be("[\"up\",0]");
        Link(wf, "av2", "audio_latent").Should().Be("[\"sep1\",1]");
        Inputs(wf, "n1")["noise_seed"]!.GetValue<long>().Should().Be(11);
        Link(wf, "vdec", "samples").Should().Be("[\"sep2\",0]");
        Link(wf, "adec", "samples").Should().Be("[\"sep2\",1]");
        Inputs(wf, "pos")["text"]!.GetValue<string>().Should().Be("кот на подоконнике");
        Inputs(wf, "video")["audio"]!.ToJsonString().Should().Be("[\"adec\",0]");
    }

    [Fact]
    public void Граф_по_кадру_вписывает_картинку_в_оба_прохода_портрет_меняет_стороны()
    {
        var wf = ComfyWorkflows.Ltx25Video("оживи маяк", "lv_in1.png", null, 3, portrait: true, 5, "ccs/x");

        Inputs(wf, "img")["image"]!.GetValue<string>().Should().Be("lv_in1.png");
        Inputs(wf, "rsz")["resize_type.longer_size"]!.GetValue<int>().Should().Be(1536);
        Link(wf, "av1", "video_latent").Should().Be("[\"i2v1\",0]");
        Inputs(wf, "i2v1")["strength"]!.GetValue<double>().Should().Be(0.7);
        Link(wf, "i2v2", "latent").Should().Be("[\"up\",0]");
        Link(wf, "av2", "video_latent").Should().Be("[\"i2v2\",0]");
        var lat = Inputs(wf, "vlat");
        lat["width"]!.GetValue<int>().Should().Be(352);
        lat["height"]!.GetValue<int>().Should().Be(640);
        lat["length"]!.GetValue<int>().Should().Be(73);
    }

    [Fact]
    public void Граф_тяжёлый_для_арбитра_по_маркеру_video()
    {
        string[] markers = ["h3", "minimax", "video", "fasth3", "whisper"];
        var text = ComfyWorkflows.Ltx25Video("x", null, null, 5, false, 1, "ccs/x").ToJsonString().ToLowerInvariant();
        markers.Should().Contain(m => text.Contains(m));
    }

    private static LocalVideoMediaAdapter Build(FakeComfy comfy)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(Path.GetTempPath(), "ltx_" + Guid.NewGuid().ToString("N"), "projects.json"),
            ["LocalMedia:Enabled"] = "true",
            ["LocalMedia:ComfyUrl"] = "http://comfy.test:8188",
        }).Build();
        var client = new ComfyClient(new FakeComfyFactory(comfy), config);
        var images = new LocalImageMediaAdapter(client, config, NullLogger<LocalImageMediaAdapter>.Instance);
        return new LocalVideoMediaAdapter(client, config, images, NullLogger<LocalVideoMediaAdapter>.Instance);
    }

    private static LocalVideoRequest L(byte[]? first = null, int seconds = 5, byte[]? last = null, string size = "landscape") =>
        new("кот на подоконнике", first ?? [], last, seconds, size, null, 7, Model: LocalVideoModels.Ltx25);

    [Fact]
    public async Task Адаптер_T2V_ставит_граф_LTX_без_загрузки_кадра()
    {
        var comfy = new FakeComfy();
        var adapter = Build(comfy);

        var submitted = await adapter.SubmitAsync(L(), default);

        submitted.Ticket.Should().NotBeNull(submitted.Error);
        submitted.EtaSeconds.Should().Be(LocalVideoMediaAdapter.LtxEtaSecondsAt5);
        comfy.Uploads.Should().BeEmpty();
        var graph = comfy.Prompts.Single()["prompt"]!.AsObject();
        graph.Should().ContainKey("run2");
        graph.Should().NotContainKey("i2v1");
    }

    [Fact]
    public async Task Адаптер_I2V_грузит_кадр_и_отдаёт_ролик_со_звуком_без_увеличения()
    {
        var comfy = new FakeComfy();
        var adapter = Build(comfy);

        var submitted = await adapter.SubmitAsync(L(LocalMediaTestImages.Png(1280, 720), size: "portrait"), default);

        submitted.Ticket.Should().NotBeNull(submitted.Error);
        comfy.Uploads.Should().ContainSingle();
        var graph = comfy.Prompts.Single()["prompt"]!.AsObject();
        graph.Should().ContainKey("i2v2");
        graph["vlat"]!["inputs"]!["height"]!.GetValue<int>().Should().Be(640);
        comfy.CompleteVideo(submitted.Ticket!, comfy.Uploads[0][..35], [1, 2, 3], withLatents: false);
        var poll = await adapter.PollAsync(submitted.Ticket!, default);
        poll.State.Should().Be(LocalVideoState.Completed);
        poll.File!.HasSound.Should().BeTrue();
        adapter.CanUpscale(submitted.Ticket!).Should().BeFalse("у LTX нет латентов для «Макс»");
    }

    [Theory]
    [InlineData(10, false, "от 3 до 5 секунд")]
    [InlineData(2, false, "от 3 до 5 секунд")]
    public async Task Адаптер_отказывает_в_том_чего_модель_не_умеет(int seconds, bool withLast, string part)
    {
        var comfy = new FakeComfy();
        var adapter = Build(comfy);

        var submitted = await adapter.SubmitAsync(L(seconds: seconds, last: withLast ? LocalMediaTestImages.Png(64, 64) : null), default);

        submitted.Ticket.Should().BeNull();
        submitted.Error.Should().Contain(part);
        comfy.Prompts.Should().BeEmpty();
    }

    [Fact]
    public void Граф_по_двум_кадрам_FLF2V_ставит_гайды_на_оба_прохода()
    {
        var wf = ComfyWorkflows.Ltx25Video("маяк и шторм", "lv_in1.png", "lv_in2.png", 5, portrait: false, 3, "ccs/x");

        wf.Should().ContainKey("g1c").And.ContainKey("g2c");
        Inputs(wf, "g1c")["frame_idx"]!.GetValue<int>().Should().Be(-1);
        Inputs(wf, "g1c")["image"]!.ToJsonString().Should().Be("[\"preb\",0]");
        Link(wf, "g1c", "latent").Should().Be("[\"i2v1\",0]");
        Link(wf, "g1", "positive").Should().Be("[\"g1c\",0]");
        Link(wf, "g1", "negative").Should().Be("[\"g1c\",1]");
        Link(wf, "g2c", "latent").Should().Be("[\"i2v2\",0]");
        Link(wf, "g2", "positive").Should().Be("[\"g2c\",0]");
        Link(wf, "g2", "negative").Should().Be("[\"g2c\",1]");
        // Латент конкатенации остаётся от i2v-узлов, гайд только в гайдере
        Link(wf, "av1", "video_latent").Should().Be("[\"i2v1\",0]");
        Link(wf, "av2", "video_latent").Should().Be("[\"i2v2\",0]");
        Inputs(wf, "imgb")["image"]!.GetValue<string>().Should().Be("lv_in2.png");
    }

    [Fact]
    public void Время_для_3_5_секунд_пропорционально_кадрам_и_без_апскейла()
    {
        var adapter = Build(new FakeComfy());
        adapter.EtaSeconds(L()).Should().Be(LocalVideoMediaAdapter.LtxEtaSecondsAt5);
        adapter.EtaSeconds(L(seconds: 3)).Should().BeLessThan(LocalVideoMediaAdapter.LtxEtaSecondsAt5);
        adapter.EtaSeconds(L(seconds: 8)).Should().BeNull();
        adapter.UpscaleEtaSeconds(L()).Should().BeNull();
    }
}
