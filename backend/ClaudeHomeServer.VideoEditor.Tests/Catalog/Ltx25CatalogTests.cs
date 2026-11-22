using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Media;
using ClaudeHomeServer.Services.VideoEditor;
using ClaudeHomeServer.Services.VideoEditor.Catalog;
using ClaudeHomeServer.Services.VideoEditor.Engines;
using FluentAssertions;
using Moq;
using Xunit;

namespace ClaudeHomeServer.VideoEditor.Tests.Catalog;

// LTX-2.5 — третья локальная видеомодель (решение Андрея 15.11): «Авто» берёт её для сцен без кадра, с кадром — H3
public class Ltx25CatalogTests
{
    private static LocalVideoEngine Engine()
    {
        var media = new Mock<ILocalVideoMedia>();
        media.SetupGet(m => m.Available).Returns(true);
        media.SetupGet(m => m.Configured).Returns(true);
        return new LocalVideoEngine(media.Object);
    }

    private static readonly VideoEditScope Scope = new("s", new Project { Id = "p", RootPath = "/p" });

    [Fact]
    public void Авто_без_кадра_берёт_LTX_с_кадром_остаётся_H3()
    {
        var e = Engine();
        VideoCatalog.Pick(e, VideoCatalog.AutoModelId, new VideoCatalog.Need(false, false, 5))!.Id.Should().Be(LocalVideoEngine.Ltx25);
        VideoCatalog.Pick(e, VideoCatalog.AutoModelId, new VideoCatalog.Need(true, false, 5))!.Id.Should().Be(LocalVideoEngine.MiniMaxH3);
    }

    [Fact]
    public void Ручной_выбор_по_тексту_по_кадру_и_по_двум_кадрам_не_длиннее_5_с()
    {
        var e = Engine();
        VideoCatalog.Pick(e, "ltx-2.5", new VideoCatalog.Need(false, false, 3)).Should().NotBeNull();
        VideoCatalog.Pick(e, "ltx-2.5", new VideoCatalog.Need(true, false, 5)).Should().NotBeNull();
        VideoCatalog.Pick(e, "ltx-2.5", new VideoCatalog.Need(true, true, 5)).Should().NotBeNull();
        VideoCatalog.Pick(e, "ltx-2.5", new VideoCatalog.Need(false, false, 8)).Should().BeNull();
    }

    [Fact]
    public void Каталог_даёт_одну_ступень_720p_в_обеих_пропорциях()
    {
        var dto = VideoCatalogView.Build([Engine()], Scope).Providers.Single().Models.Single(m => m.Id == LocalVideoEngine.Ltx25);

        dto.Experimental.Should().NotBe(true);
        dto.FirstFrameOptional.Should().BeTrue();
        dto.LastFrame.Should().BeTrue();
        dto.Sound.Should().BeTrue();
        dto.Durations.Should().Equal(3, 4, 5);
        dto.Aspects.Should().Equal("16:9", "9:16");
        var tier = dto.Format!.Tiers.Should().ContainSingle().Which;
        tier.Tier.Should().Be(FormatTier.Standard);
        tier.Sizes["9:16"].W.Should().Be(704);
        dto.Hint.Should().Contain("720p");
    }

    private static VideoRequest Req(VideoFrameBytes? a, VideoFrameBytes? b, int sec = 5, System.Text.Json.Nodes.JsonObject? p = null,
        string? aspect = null) =>
        new(LocalVideoEngine.Ltx25, Scope, "кот", a, b, sec, Params: p,
            Format: aspect is null ? null : new ResolvedFormat("tier", aspect, FormatTier.Standard, null, null, "720p", null, false, false, null));

    [Fact]
    public void Compose_по_тексту_и_по_кадру_пропорция_из_формата()
    {
        var t2v = LocalVideoEngine.Compose(Req(null, null, aspect: "9:16"));
        t2v.Error.Should().BeNull();
        t2v.Run!.Model.Should().Be(LocalVideoModels.Ltx25);
        t2v.Run.FirstFrame.Should().BeEmpty();
        t2v.Run.Size.Should().Be("portrait");

        var i2v = LocalVideoEngine.Compose(Req(new VideoFrameBytes([1, 2], "image/png"), null, 3));
        i2v.Run!.FirstFrame.Should().Equal(1, 2);
        i2v.Run.Seconds.Should().Be(3);
        i2v.Run.Size.Should().Be("landscape");
    }

    [Fact]
    public void Compose_принимает_оба_кадра_отказывает_по_длительности_и_параметрам()
    {
        var frame = new VideoFrameBytes([1], "image/png");
        LocalVideoEngine.Compose(Req(frame, frame)).Run.Should().NotBeNull();
        LocalVideoEngine.Compose(Req(frame, null, 10)).Error.Should().Contain("от 3 до 5");
        LocalVideoEngine.Compose(Req(frame, null, p: new() { ["fast"] = true })).Error.Should().Contain("fast");
    }
}
