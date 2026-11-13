using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Media;
using ClaudeHomeServer.Services.VideoEditor;
using ClaudeHomeServer.Services.VideoEditor.Catalog;
using ClaudeHomeServer.Services.VideoEditor.Engines;
using FluentAssertions;
using Moq;
using Xunit;

namespace ClaudeHomeServer.VideoEditor.Tests.Catalog;

// Kandinsky 6 Lite — экспериментальная модель только ручного выбора (Ш5)
public class KandinskyCatalogTests
{
    private static LocalVideoEngine Engine()
    {
        var media = new Mock<ILocalVideoMedia>();
        media.SetupGet(m => m.Available).Returns(true);
        media.SetupGet(m => m.Configured).Returns(true);
        return new LocalVideoEngine(media.Object);
    }

    private static VideoModelInfo K(LocalVideoEngine e) => e.Models.Single(m => m.Id == LocalVideoEngine.Kandinsky6Lite);

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public void Авто_никогда_не_берёт_Kandinsky(bool frameA, bool frameB)
    {
        var picked = VideoCatalog.Pick(Engine(), VideoCatalog.AutoModelId, new VideoCatalog.Need(frameA, frameB, 5));

        picked?.Id.Should().NotBe(LocalVideoEngine.Kandinsky6Lite);
        if (!frameA) picked.Should().BeNull("H3 без кадра не снимает, а Kandinsky только ручной");
        else picked!.Id.Should().Be(LocalVideoEngine.MiniMaxH3);
    }

    [Fact]
    public void Ручной_выбор_берёт_Kandinsky_с_кадром_и_без()
    {
        var e = Engine();
        VideoCatalog.Pick(e, "kandinsky-6-lite", new VideoCatalog.Need(false, false, 5)).Should().NotBeNull();
        VideoCatalog.Pick(e, "kandinsky-6-lite", new VideoCatalog.Need(true, false, 5)).Should().NotBeNull();
    }

    [Fact]
    public void Кадр_B_и_длительность_кроме_5_с_не_подходят()
    {
        var e = Engine();
        VideoCatalog.Pick(e, "kandinsky-6-lite", new VideoCatalog.Need(true, true, 5)).Should().BeNull();
        VideoCatalog.Pick(e, "kandinsky-6-lite", new VideoCatalog.Need(true, false, 10)).Should().BeNull();
    }

    [Fact]
    public void Каталог_помечает_модель_экспериментальной_с_подсказкой_и_одной_ступенью()
    {
        var e = Engine();
        var dto = VideoCatalogView.Build([e], new VideoEditScope("s", new Project { Id = "p", RootPath = "/p" })).Providers
            .Single().Models.Single(m => m.Id == LocalVideoEngine.Kandinsky6Lite);

        dto.Experimental.Should().BeTrue();
        dto.FirstFrameOptional.Should().BeTrue();
        dto.LastFrame.Should().BeFalse();
        dto.Hint.Should().Contain("медленнее H3");
        dto.Durations.Should().Equal(5);
        dto.Aspects.Should().Equal("16:9");
        dto.Format!.Tiers.Should().ContainSingle().Which.Tier.Should().Be(FormatTier.Standard);
        H3(e).ManualOnly.Should().BeFalse();
        VideoFormatCatalog.DefaultTier("local", LocalVideoEngine.Kandinsky6Lite).Should().Be(FormatTier.Standard);
    }

    private static VideoModelInfo H3(LocalVideoEngine e) => e.Models.Single(m => m.Id == LocalVideoEngine.MiniMaxH3);

    private static VideoRequest Req(VideoFrameBytes? a, VideoFrameBytes? b, int sec = 5, System.Text.Json.Nodes.JsonObject? p = null) =>
        new(LocalVideoEngine.Kandinsky6Lite, new VideoEditScope("s", new Project { Id = "p", RootPath = "/p" }), "лиса", a, b, sec, Params: p);

    [Fact]
    public void Compose_по_тексту_и_по_кадру_A()
    {
        var t2v = LocalVideoEngine.Compose(Req(null, null));
        t2v.Error.Should().BeNull();
        t2v.Run!.Model.Should().Be(LocalVideoModels.Kandinsky6Lite);
        t2v.Run.FirstFrame.Should().BeEmpty();

        var i2v = LocalVideoEngine.Compose(Req(new VideoFrameBytes([1, 2], "image/png"), null));
        i2v.Run!.FirstFrame.Should().Equal(1, 2);
    }

    [Fact]
    public void Compose_отказывает_по_кадру_B_длительности_и_параметрам()
    {
        var frame = new VideoFrameBytes([1], "image/png");
        LocalVideoEngine.Compose(Req(frame, frame)).Error.Should().Contain("кадр B");
        LocalVideoEngine.Compose(Req(frame, null, 10)).Error.Should().Contain("5 секунд");
        LocalVideoEngine.Compose(Req(frame, null, p: new() { ["fast"] = true })).Error.Should().Contain("fast");
    }

    [Fact]
    public void Compose_H3_не_несёт_модель_в_шов()
    {
        var run = LocalVideoEngine.Compose(new VideoRequest(LocalVideoEngine.MiniMaxH3,
            new VideoEditScope("s", new Project { Id = "p", RootPath = "/p" }), "x", new VideoFrameBytes([1], "image/png"), null, 5)).Run!;
        run.Model.Should().BeNull();
    }
}
