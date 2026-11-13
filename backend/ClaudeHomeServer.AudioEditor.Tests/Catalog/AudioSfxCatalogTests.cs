using ClaudeHomeServer.Services.AudioEditor;
using ClaudeHomeServer.Services.AudioEditor.Catalog;
using ClaudeHomeServer.Services.AudioEditor.Engines;
using ClaudeHomeServer.Services.Media;
using FluentAssertions;

namespace ClaudeHomeServer.AudioEditor.Tests.Catalog;

// Эффекты по тексту и звук по видео (Ш4): локальные модели каталога, привязки к операциям шва, умолчания «Авто»
public class AudioSfxCatalogTests
{
    private static IReadOnlyList<AudioModelInfo> Models => AudioCatalog.Local.Select(m => m.Info).ToList();

    [Fact]
    public void Sfx_Auto_ПервойБерётсяMossSfx_ВторойMmAudio()
    {
        AudioCatalog.Resolve(Models, AudioOp.Sfx, AudioCatalog.AutoModelId)!.Id.Should().Be(AudioCatalog.MossSfx);
        Models.Where(m => m.Caps.Ops.Contains(AudioOp.Sfx)).Select(m => m.Id)
            .Should().Equal(AudioCatalog.MossSfx, AudioCatalog.MmAudio);
    }

    [Fact]
    public void VideoToAudio_Auto_ПервойБерётсяFoleyXl_ПотомXxlИMmAudio()
    {
        AudioCatalog.Resolve(Models, AudioOp.VideoToAudio, AudioCatalog.AutoModelId)!.Id.Should().Be(AudioCatalog.FoleyXl);
        Models.Where(m => m.Caps.Ops.Contains(AudioOp.VideoToAudio)).Select(m => m.Id)
            .Should().Equal(AudioCatalog.FoleyXl, AudioCatalog.FoleyXxl, AudioCatalog.MmAudio);
    }

    [Theory]
    [InlineData(AudioCatalog.MossSfx, AudioOp.Sfx, LocalAudioOp.Sfx, "moss")]
    [InlineData(AudioCatalog.MmAudio, AudioOp.Sfx, LocalAudioOp.Sfx, "mmaudio")]
    [InlineData(AudioCatalog.MmAudio, AudioOp.VideoToAudio, LocalAudioOp.VideoToAudio, "mmaudio")]
    [InlineData(AudioCatalog.FoleyXl, AudioOp.VideoToAudio, LocalAudioOp.VideoToAudio, "foley-xl")]
    [InlineData(AudioCatalog.FoleyXxl, AudioOp.VideoToAudio, LocalAudioOp.VideoToAudio, "foley-xxl")]
    public void Привязка_ОперацияИДвижокШва(string model, AudioOp op, LocalAudioOp seam, string engine)
    {
        var binding = AudioCatalog.FindLocal(model)!.Bindings[op];

        binding.Op.Should().Be(seam);
        binding.Args["engine"].Should().Be(engine);
    }

    [Fact]
    public void ТяжёлыеОперации_MossSfxИFoleyXxl()
    {
        AudioCatalog.FindLocal(AudioCatalog.MossSfx)!.Info.Caps.IsHeavy(AudioOp.Sfx).Should().BeTrue();
        AudioCatalog.FindLocal(AudioCatalog.FoleyXxl)!.Info.Caps.IsHeavy(AudioOp.VideoToAudio).Should().BeTrue();
        AudioCatalog.FindLocal(AudioCatalog.FoleyXl)!.Info.Caps.IsHeavy(AudioOp.VideoToAudio).Should().BeFalse();
    }

    [Fact]
    public void Пределы_СовпадаютСАдаптером()
    {
        AudioCatalog.FindLocal(AudioCatalog.MossSfx)!.Info.Caps.MaxDurationSec.Should().Be(Images10);
        AudioCatalog.FindLocal(AudioCatalog.MmAudio)!.Info.Caps.MaxDurationSec.Should().Be(8);
        AudioCatalog.FindLocal(AudioCatalog.FoleyXl)!.Info.Caps.InputMaxSec.Should().Be(10);
    }

    private const int Images10 = 10;

    [Fact]
    public void Схема_ЕстьУНовыхПривязок_БезЧастныхПолей()
    {
        AudioCatalog.LocalSchema(AudioCatalog.FoleyXl, AudioOp.VideoToAudio)!.Reserved.Should().Contain("video");
        AudioCatalog.LocalSchema(AudioCatalog.MossSfx, AudioOp.Sfx).Should().NotBeNull();
        AudioCatalog.LocalSchema(AudioCatalog.MossSfx, AudioOp.VideoToAudio).Should().BeNull();
    }

    [Fact]
    public void Compose_ПередаётВидеоИФиксированныйДвижок()
    {
        var video = new AudioBytes([1, 2, 3], "video/mp4");
        var req = new AudioRequest(AudioOp.VideoToAudio, AudioCatalog.FoleyXxl, new AudioEditScope("p", null), Prompt: "steps", Video: video);

        var run = LocalAudioEngine.Compose(req)!;

        run.Op.Should().Be(LocalAudioOp.VideoToAudio);
        run.Video.Should().Equal(1, 2, 3);
        run.Prompt.Should().Be("steps");
        run.Args!["engine"]!.GetValue<string>().Should().Be("foley-xxl");
    }

    [Fact]
    public void Лицензии_ЯвноПомечены()
    {
        AudioCatalog.FindLocal(AudioCatalog.MossSfx)!.Info.Caps.License.Kind.Should().Be(AudioLicenseKind.Permissive);
        AudioCatalog.FindLocal(AudioCatalog.MmAudio)!.Info.Caps.License.Kind.Should().Be(AudioLicenseKind.NonCommercial);
        AudioCatalog.FindLocal(AudioCatalog.FoleyXl)!.Info.Caps.License.Label.Should().Contain("Tencent");
    }
}
