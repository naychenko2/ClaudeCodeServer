using System.Text.Json.Nodes;
using ClaudeHomeServer.Services.Images.LocalMedia;
using ClaudeHomeServer.Services.Media;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ClaudeHomeServer.Tests.Services;

// «Увеличить» готовый ролик ×2 / ×4 (RelayVSR): граф ComfyUI, таблица времени и шов адаптера поверх фейкового ComfyUI.
// ffmpeg — Moq: проверяется, что звук исходника возвращается на место, а не что делает сам ffmpeg
public class LocalVideoEnhanceTests
{
    private readonly FakeComfy _comfy = new();

    [Theory]
    [InlineData(2, "relayvsr_x2_lightx2v_int8_convrot.safetensors", "relayvsr_flash_vae_x2_fp32.safetensors")]
    [InlineData(4, "relayvsr_x4_int8_convrot.safetensors", "relayvsr_flash_vae_x4_fp32.safetensors")]
    public void Граф_берёт_веса_по_масштабу_и_не_несёт_звук(int scale, string unet, string vae)
    {
        var graph = ComfyWorkflows.RelayVsr("ccs-local-media/lv_x-enh.mp4", scale, 5, "ccs-local-media/lv_x");

        graph["lv"]!["inputs"]!["file"]!.GetValue<string>().Should().Be("ccs-local-media/lv_x-enh.mp4");
        graph["unet"]!["inputs"]!["unet_name"]!.GetValue<string>().Should().Be(unet);
        graph["vae"]!["inputs"]!["vae_name"]!.GetValue<string>().Should().Be(vae);
        graph["ks"]!["inputs"]!["steps"]!.GetValue<int>().Should().Be(1);
        graph["vid"]!["inputs"]!.AsObject().ContainsKey("audio").Should().BeFalse("звук возвращает ffmpeg после прогона");
        graph["save"]!["inputs"]!["filename_prefix"]!.GetValue<string>().Should().Be("ccs-local-media/lv_x");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void Граф_с_неверным_масштабом_не_строится(int scale) =>
        FluentActions.Invoking(() => ComfyWorkflows.RelayVsr("a.mp4", scale, 1, "p")).Should().Throw<ArgumentOutOfRangeException>();

    [Fact]
    public void Время_по_замеру_ночи_13_11_на_замеренных_точках()
    {
        LocalMediaService.VideoEnhanceEta(2, 864, 480, 5).Should().BeInRange(14, 17);
        LocalMediaService.VideoEnhanceEta(2, 1344, 768, 5).Should().Be(37);
        LocalMediaService.VideoEnhanceEta(4, 864, 480, 5).Should().BeInRange(47, 52);
        LocalMediaService.VideoEnhanceEta(4, 1344, 768, 5).Should().Be(140);
        LocalMediaService.VideoEnhanceEta(4, 768, 1344, 5).Should().Be(140, "портрет — те же секунды");
    }

    [Fact]
    public void Время_растёт_с_длиной_и_не_выдумывается_вне_замера()
    {
        LocalMediaService.VideoEnhanceEta(2, 1344, 768, 10).Should().Be(74);
        LocalMediaService.VideoEnhanceEta(2, 1344, 768, 11).Should().BeNull("длиннее 10 с не мерено");
        LocalMediaService.VideoEnhanceEta(4, 1920, 1080, 5).Should().BeNull("выше 768p ×4 не помещается в 24 ГБ");
        LocalMediaService.VideoEnhanceEta(3, 864, 480, 5).Should().BeNull();
    }

    private LocalVideoMediaAdapter Build(IVideoDsp? dsp, bool enabled = true, int maxQueue = 4)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["LocalMedia:Enabled"] = enabled ? "true" : "false",
            ["LocalMedia:ComfyUrl"] = "http://comfy.test:8188",
            ["LocalMedia:MaxComfyQueue"] = maxQueue.ToString(),
        }).Build();
        var client = new ComfyClient(new FakeComfyFactory(_comfy), config);
        var images = new LocalImageMediaAdapter(client, config, NullLogger<LocalImageMediaAdapter>.Instance);
        return new LocalVideoMediaAdapter(client, config, images, NullLogger<LocalVideoMediaAdapter>.Instance, dsp: dsp);
    }

    private static Mock<IVideoDsp> Dsp(int width = 864, int height = 480, double seconds = 5, bool audio = true,
        bool muxOk = true)
    {
        var dsp = new Mock<IVideoDsp>();
        dsp.SetupGet(d => d.Available).Returns(true);
        dsp.Setup(d => d.ProbeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VideoDspInfo(seconds, width, height, 24, audio));
        // Склейка: пишет в outPath байты «видео + звук», проверка — что в неё пришёл именно исходник со звуком
        dsp.Setup(d => d.MuxAudioAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, string, string, CancellationToken>((video, source, outPath, _) =>
            {
                if (!muxOk) return Task.FromResult(VideoDspResult.Fail("ffmpeg отказал"));
                File.ReadAllBytes(source).Should().Equal(Source, "звук берётся из исходника");
                File.WriteAllBytes(outPath, [.. File.ReadAllBytes(video), .. new byte[] { 0xA1 }]);
                return Task.FromResult(VideoDspResult.Success);
            });
        return dsp;
    }

    private static readonly byte[] Source = [1, 2, 3, 4];

    [Fact]
    public async Task Увеличение_ставит_граф_RelayVSR_и_отдаёт_ролик_со_звуком_исходника()
    {
        var adapter = Build(Dsp().Object);

        var submitted = await adapter.SubmitEnhanceAsync(new LocalVideoEnhanceRequest(Source, 2), default);

        submitted.Error.Should().BeNull();
        submitted.Ticket.Should().NotBeNullOrEmpty();
        submitted.EtaSeconds.Should().Be(LocalMediaService.VideoEnhanceEta(2, 864, 480, 5));
        _comfy.Uploads.Should().ContainSingle().Which.Should().MatchRegex("^lv_[0-9a-f]{32}-enh\\.mp4$");
        var graph = _comfy.Prompts[0]["prompt"]!.AsObject();
        graph["unet"]!["inputs"]!["unet_name"]!.GetValue<string>().Should().Be(ComfyWorkflows.RelayVsrX2Unet);
        graph["lv"]!["inputs"]!["file"]!.GetValue<string>().Should().EndWith("-enh.mp4");

        var enhanced = new byte[] { 9, 9, 9 };
        _comfy.CompleteVideo(submitted.Ticket!, _comfy.Uploads[0][..35], enhanced);
        var done = await adapter.PollAsync(submitted.Ticket!, default);

        done.State.Should().Be(LocalVideoState.Completed);
        done.File!.Bytes.Should().Equal(9, 9, 9, 0xA1);
        done.File.HasSound.Should().BeTrue();
        done.File.Width.Should().Be(864 * 2);
        done.File.Height.Should().Be(480 * 2);
    }

    [Fact]
    public async Task Ролик_без_звука_отдаётся_без_звука()
    {
        var adapter = Build(Dsp(audio: false).Object);
        var submitted = await adapter.SubmitEnhanceAsync(new LocalVideoEnhanceRequest(Source, 4), default);
        _comfy.CompleteVideo(submitted.Ticket!, _comfy.Uploads[0][..35], [5]);

        var done = await adapter.PollAsync(submitted.Ticket!, default);

        done.File!.HasSound.Should().BeFalse();
        done.File.Width.Should().Be(864 * 4);
    }

    [Fact]
    public async Task Сбой_склейки_звука_это_Failed_а_не_ролик_без_звука()
    {
        var adapter = Build(Dsp(muxOk: false).Object);
        var submitted = await adapter.SubmitEnhanceAsync(new LocalVideoEnhanceRequest(Source, 2), default);
        _comfy.CompleteVideo(submitted.Ticket!, _comfy.Uploads[0][..35], [5]);

        var done = await adapter.PollAsync(submitted.Ticket!, default);

        done.State.Should().Be(LocalVideoState.Failed);
        done.Error.Should().Contain("ffmpeg отказал");
        done.File.Should().BeNull();
    }

    [Fact]
    public async Task Отказы_без_постановки_в_ComfyUI()
    {
        (await Build(Dsp().Object, enabled: false).SubmitEnhanceAsync(new LocalVideoEnhanceRequest(Source, 2), default))
            .Error.Should().Contain("выключены");
        (await Build(null).SubmitEnhanceAsync(new LocalVideoEnhanceRequest(Source, 2), default))
            .Error.Should().Contain("ffmpeg");
        (await Build(Dsp().Object).SubmitEnhanceAsync(new LocalVideoEnhanceRequest(Source, 3), default))
            .Error.Should().Contain("×2 или ×4");
        (await Build(Dsp(width: 1920, height: 1080).Object).SubmitEnhanceAsync(new LocalVideoEnhanceRequest(Source, 2), default))
            .Error.Should().Contain("велик");
        (await Build(Dsp(seconds: 12).Object).SubmitEnhanceAsync(new LocalVideoEnhanceRequest(Source, 2), default))
            .Error.Should().Contain("длиннее");

        _comfy.Prompts.Should().BeEmpty();
        _comfy.Uploads.Should().BeEmpty();
    }

    [Fact]
    public async Task Занятая_очередь_честный_отказ_busy()
    {
        _comfy.Running.Add("чужой");
        var submitted = await Build(Dsp().Object, maxQueue: 1).SubmitEnhanceAsync(new LocalVideoEnhanceRequest(Source, 2), default);

        submitted.Busy.Should().BeTrue();
        submitted.Ticket.Should().BeNull();
        _comfy.Prompts.Should().BeEmpty();
    }

    [Fact]
    public async Task Отмена_снимает_идущую_задачу()
    {
        var adapter = Build(Dsp().Object);
        var submitted = await adapter.SubmitEnhanceAsync(new LocalVideoEnhanceRequest(Source, 2), default);

        (await adapter.CancelAsync(submitted.Ticket!, default)).Should().BeTrue();
    }
}
