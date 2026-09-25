using ClaudeHomeServer.Services.ImageEditor;
using ClaudeHomeServer.Services.Images.Editing;
using ClaudeHomeServer.Tests.ImageEditor.Fakes;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.ImageEditor.Marks;

// Пометки → модель (ADR-017, раздел 3): маска отдельным каналом или образцом, размеченная
// копия — образцом, исходник уходит чистым, текст marks.json — в запрос
public class EditMarksTests
{
    private static readonly ImageEditModelInfo NativeMask =
        new("native", "Native", new ImageEditCaps([ImageEditOp.Edit, ImageEditOp.Inpaint], MaskSupport.Native, 3, 4, true));

    private static readonly ImageEditModelInfo ReferenceMask =
        new("asref", "AsRef", new ImageEditCaps([ImageEditOp.Edit, ImageEditOp.Inpaint], MaskSupport.AsReference, 3, 4, true));

    private static ImageEditJobInput Input(byte[] source, byte[]? mask = null, byte[]? annotated = null, string? marks = null) =>
        new("q", "убрать провод", marks, new ImageBytes(source, "image/png"),
            mask is null ? null : new ImageBytes(mask, "image/png"),
            annotated is null ? null : new ImageBytes(annotated, "image/png"), [], "images/hero.png");

    [Fact]
    public void ТекстПометок_Детерминирован()
    {
        const string marks = """
            {"marks":[
              {"type":"rect","x":0.62,"y":0.1,"w":0.26,"h":0.25,"text":"сюда лампу"},
              {"type":"arrow","x1":0.2,"y1":0.8,"x2":0.4,"y2":0.5},
              {"type":"text","x":0.1,"y":0.9,"text":"убрать"},
              {"type":"brush","points":[]}
            ]}
            """;

        EditMarksPrompt.Describe(marks).Should().Be(
            "Пометки на картинке (координаты — проценты от ширины и высоты):\n" +
            "- рамка вверху справа (x 62–88 %, y 10–35 %): сюда лампу\n" +
            "- стрелка от (x 20 %, y 80 %) к (x 40 %, y 50 %), в центре\n" +
            "- подпись внизу слева (x 10 %, y 90 %): «убрать»");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("не json")]
    [InlineData("""{"marks":"x"}""")]
    [InlineData("""[{"type":"brush"}]""")]
    public void ПустыеИБитыеПометки_ПустойТекст(string? marks)
    {
        EditMarksPrompt.Describe(marks).Should().BeEmpty();
    }

    [Fact]
    public void Запечённые_ИсходникУходитЧистым_КопияОбразцом_ТекстВЗапросе()
    {
        var source = TestImages.Png(10, 10, tail: 1);
        var annotated = TestImages.Png(10, 10, tail: 2);
        var sourceCopy = source.ToArray();

        var result = EditRequestComposer.Compose(
            Input(source, annotated: annotated, marks: """[{"type":"text","x":0.5,"y":0.5,"text":"сюда"}]"""),
            ImageEditOp.Edit, ReferenceMask, 2);

        var req = result.Value!;
        req.Source!.Bytes.Should().BeSameAs(source);
        source.Should().Equal(sourceCopy);
        req.References.Should().ContainSingle(r => r.Label == EditRequestComposer.AnnotatedLabel && r.Bytes == annotated);
        req.Prompt.Should().StartWith("убрать провод").And.Contain("«сюда»").And.Contain("«annotated»");
        req.Op.Should().Be(ImageEditOp.Edit);
    }

    [Fact]
    public void Маска_МодельСКаналом_ОтдельноИОперацияИнпейнт()
    {
        var png = TestImages.Png(10, 10);

        var req = EditRequestComposer.Compose(Input(png, mask: png), ImageEditOp.Edit, NativeMask, 1).Value!;

        req.Mask.Should().NotBeNull();
        req.Op.Should().Be(ImageEditOp.Inpaint);
        req.References.Should().BeEmpty();
    }

    [Fact]
    public void Маска_МодельБезКанала_ОбразцомСФразой()
    {
        var png = TestImages.Png(10, 10);

        var req = EditRequestComposer.Compose(Input(png, mask: png), ImageEditOp.Edit, ReferenceMask, 1).Value!;

        req.Mask.Should().BeNull();
        req.References.Should().ContainSingle(r => r.Label == EditRequestComposer.MaskLabel);
        req.Prompt.Should().Contain("только область");
    }

    [Fact]
    public void Маска_НеСовпадаетПоРазмеру_ОтказДоЗапуска()
    {
        var result = EditRequestComposer.Compose(
            Input(TestImages.Png(10, 10), mask: TestImages.Png(10, 11)), ImageEditOp.Edit, NativeMask, 1);

        result.Value.Should().BeNull();
        result.ErrorCode.Should().Be(ImageEditErrorCodes.InvalidRequest);
    }

    [Fact]
    public void Размеры_JpegИзМаркераSof()
    {
        byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x04, 0x00, 0x00, 0xFF, 0xC0, 0x00, 0x11, 0x08, 0x01, 0x2C, 0x02, 0x58, 0x03];

        ImageDimensions.Read(jpeg).Should().Be((600, 300));
    }
}
