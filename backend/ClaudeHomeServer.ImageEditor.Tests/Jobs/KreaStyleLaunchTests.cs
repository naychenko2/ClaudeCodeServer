using ClaudeHomeServer.Services.ImageEditor;
using ClaudeHomeServer.Services.Images.Editing.Raster;
using ClaudeHomeServer.Tests.ImageEditor.Providers;
using FluentAssertions;
using Moq;

namespace ClaudeHomeServer.Tests.ImageEditor.Jobs;

// Стиль Krea 2 Turbo (Ш4, ревью п. 6): образцы, с которыми шов увёл бы запуск на Qwen и выбросил стиль, —
// понятный отказ сборщика, а не картинка без стиля
public class KreaStyleLaunchTests
{
    private const string Owner = "user-k";
    private const string Refusal = "Стиль Krea работает без образцов или с одним образцом-стилем";
    private static readonly ImageEditScope Scope = ImageEditScope.Of(new ClaudeHomeServer.Models.Session { Id = "chat-k", OwnerId = Owner });

    private static ImageEditLaunchAssembler Assembler(string model)
    {
        var editor = new LocalImageEditor(new LocalImageEditorTests.FakeMedia());
        var caps = editor.Models.Single(m => m.Id == model).Caps;
        var jobs = new Mock<IImageEditJobs>();
        jobs.Setup(j => j.QuoteInfo(Owner, ImageEditScope.Personal, "q1"))
            .Returns(new ImageEditQuoteInfo(ImageEditOp.Generate, null, "local", InputCaps: caps));
        return new ImageEditLaunchAssembler([editor], jobs.Object, new SkiaImageRaster());
    }

    private static ReferenceImage Ref(ReferenceRole role) => new([1, 2, 3], "image/png", role, null);

    private static ImageEditLaunchRequest Request(string? style, params ReferenceImage[] refs) =>
        new("q1", "кот на крыше", null, null, null, null, null, refs, [], null, Style: style);

    [Theory]
    [InlineData(ReferenceRole.Object)]
    [InlineData(ReferenceRole.Character)]
    public async Task Стиль_с_образцом_не_роли_стиль_отказ(ReferenceRole role)
    {
        var result = await Assembler(LocalImageEditor.Krea2Turbo)
            .AssembleAsync(Owner, Owner, Scope, Request("neondrip", Ref(role)), default);

        result.Value.Should().BeNull();
        result.Error.Should().Be(Refusal);
    }

    [Fact]
    public async Task Стиль_с_двумя_образцами_стиля_отказ()
    {
        var result = await Assembler(LocalImageEditor.Krea2Turbo)
            .AssembleAsync(Owner, Owner, Scope, Request("neondrip", Ref(ReferenceRole.Style), Ref(ReferenceRole.Style)), default);

        result.Error.Should().Be(Refusal);
    }

    [Fact]
    public async Task Стиль_без_образцов_и_с_одним_образцом_стилем_проходит()
    {
        var bare = await Assembler(LocalImageEditor.Krea2Turbo).AssembleAsync(Owner, Owner, Scope, Request("neondrip"), default);
        var byRef = await Assembler(LocalImageEditor.Krea2Turbo)
            .AssembleAsync(Owner, Owner, Scope, Request("neondrip", Ref(ReferenceRole.Style)), default);

        bare.Value.Should().NotBeNull(bare.Error);
        bare.Value!.Style.Should().Be("neondrip");
        byRef.Value.Should().NotBeNull(byRef.Error);
    }

    [Fact]
    public async Task Образцы_без_стиля_и_у_модели_без_стилей_не_отказывают()
    {
        var noStyle = await Assembler(LocalImageEditor.Krea2Turbo)
            .AssembleAsync(Owner, Owner, Scope, Request(null, Ref(ReferenceRole.Object)), default);
        var qwen = await Assembler(LocalImageEditor.QwenImage)
            .AssembleAsync(Owner, Owner, Scope, Request("neondrip", Ref(ReferenceRole.Object)), default);

        noStyle.Value.Should().NotBeNull(noStyle.Error);
        qwen.Value.Should().NotBeNull(qwen.Error);
    }
}
