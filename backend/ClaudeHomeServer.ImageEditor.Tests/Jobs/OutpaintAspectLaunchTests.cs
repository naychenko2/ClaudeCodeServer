using ClaudeHomeServer.Services.ImageEditor;
using ClaudeHomeServer.Services.Images.Editing.Raster;
using ClaudeHomeServer.Tests.ImageEditor.Providers;
using FluentAssertions;
using Moq;

namespace ClaudeHomeServer.Tests.ImageEditor.Jobs;

// «Дорисовать» из «Формата» (ADR-025): пропорции холста — любые «W:H», а не три значения старого пути правки
public class OutpaintAspectLaunchTests
{
    private const string Owner = "user-o";
    private static readonly ImageEditScope Scope = ImageEditScope.Of(new ClaudeHomeServer.Models.Session { Id = "chat-o", OwnerId = Owner });

    private static ImageEditLaunchAssembler Assembler(ImageEditOp quoteOp)
    {
        var jobs = new Mock<IImageEditJobs>();
        jobs.Setup(j => j.QuoteInfo(Owner, ImageEditScope.Personal, "q1")).Returns(new ImageEditQuoteInfo(quoteOp, null));
        return new ImageEditLaunchAssembler([HiggsfieldImageEditorTests.Create().Editor], jobs.Object, new SkiaImageRaster());
    }

    private static ImageEditLaunchRequest Request(string aspect) =>
        new("q1", "", null, null, null, null, null, [], [], null, AspectRatio: aspect);

    [Theory]
    [InlineData("4:3")]
    [InlineData("21:9")]
    [InlineData("3:4")]
    public async Task Дорисовка_принимает_любые_читаемые_пропорции(string aspect)
    {
        var result = await Assembler(ImageEditOp.Outpaint).AssembleAsync(Owner, Scope, Request(aspect), default);

        result.Value.Should().NotBeNull(result.Error);
        result.Value!.AspectRatio.Should().Be(aspect);
    }

    [Fact]
    public async Task Дорисовка_отказывает_непрочитанным_пропорциям()
    {
        var result = await Assembler(ImageEditOp.Outpaint).AssembleAsync(Owner, Scope, Request("широкая"), default);

        result.Value.Should().BeNull();
        result.Error.Should().Contain("не читаются");
    }

    [Fact]
    public async Task Белый_список_трёх_пропорций_остался_у_правки()
    {
        var result = await Assembler(ImageEditOp.Edit).AssembleAsync(Owner, Scope, Request("4:3"), default);

        result.Value.Should().BeNull();
        result.Error.Should().Contain("не поддерживаются");
    }
}
