using ClaudeHomeServer.Services.ImageEditor;
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

    private static readonly ImageEditModelInfo VeilModel =
        new("veil", "Veil", new ImageEditCaps([ImageEditOp.Edit, ImageEditOp.Inpaint], MaskSupport.Veil, 3, 4, true));

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
        req.Prompt.Should().StartWith("убрать провод").And.Contain("«сюда»").And.Contain("с пометками поверх");
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
        req.Prompt.Should().Contain("белое — область, которую менять");
    }

    [Fact]
    public void Маска_НеСовпадаетПоРазмеру_ОтказДоЗапуска()
    {
        var result = EditRequestComposer.Compose(
            Input(TestImages.Png(10, 10), mask: TestImages.Png(10, 11)), ImageEditOp.Edit, NativeMask, 1);

        result.Value.Should().BeNull();
        result.ErrorCode.Should().Be(ImageEditErrorCodes.InvalidRequest);
    }

    // Кроп вокруг цели (ADR-028, решение 12): маска не уходит образцом (модель видит кроп, а не
    // маску), а явным каналом KeepMask; FaceCrop прокидывается в запрос. Мутация «вернуть маску
    // в references» → красный
    [Fact]
    public void Кроп_Маска_ВКаналKeepMask_АНеВОбразцы()
    {
        var png = TestImages.Png(10, 10);
        var input = Input(png, mask: png) with
        {
            FaceCrop = new LocalImageCrop(0, 0, 10, 10, 16, 16),
        };

        var req = EditRequestComposer.Compose(input, ImageEditOp.Edit, ReferenceMask, 1).Value!;

        // Маска не среди образцов модели
        req.References.Should().NotContain(r => r.Label == EditRequestComposer.MaskLabel,
            "при кропе модель видит кроп, а не маску");
        // Явный канал KeepMask доехал
        req.KeepMask.Should().NotBeNull("маска — явный канал, не метка в References");
        req.KeepMask!.Bytes.Should().Equal(png);
        // Кроп прокинут
        req.FaceCrop.Should().Be(new LocalImageCrop(0, 0, 10, 10, 16, 16));
        // Роль «маска: белое — область, которую менять…» не написана
        req.Prompt.Should().NotContain("белое — область, которую менять");
    }

    // Кисть (без кропа) не изменилась: маска по-прежнему образец, KeepMask из References, кропа нет
    [Fact]
    public void Кисть_БезКропа_МаскаОбразцом_КропаНет()
    {
        var png = TestImages.Png(10, 10);

        var req = EditRequestComposer.Compose(Input(png, mask: png), ImageEditOp.Edit, ReferenceMask, 1).Value!;

        req.References.Should().ContainSingle(r => r.Label == EditRequestComposer.MaskLabel,
            "кисть: маска — образец, как раньше");
        req.KeepMask.Should().BeNull("кисть: явного канала KeepMask нет — драйвер возьмёт из References");
        req.FaceCrop.Should().BeNull("кисть: кропа нет");
    }

    // Вуаль (ADR-028, решение 12): маска идёт каналом (как у Native), а не образцом — мутация
    // «вернуть в references» → красный. KeepMask на уровне композитора null (драйвер возьмёт из канала)
    [Fact]
    public void Вуаль_МаскаВКанал_АНеВОбразцы()
    {
        var png = TestImages.Png(10, 10);

        var req = EditRequestComposer.Compose(Input(png, mask: png), ImageEditOp.Edit, VeilModel, 1).Value!;

        req.Mask.Should().NotBeNull("вуаль: маска — каналом, не образцом");
        req.Mask!.Bytes.Should().Equal(png);
        req.References.Should().NotContain(r => r.Label == EditRequestComposer.MaskLabel,
            "вуаль: маски-образца нет — иначе Qwen-Image перерисует её в результат");
        req.KeepMask.Should().BeNull("вуаль: явного канала KeepMask нет — драйвер возьмёт из канала");
        req.Op.Should().Be(ImageEditOp.Inpaint, "у вуали с маской композитор переводит Edit в Inpaint, как у Native");
    }

    // Вуаль: промпт оборачивается VeilHead, строки «выделено кистью…» из пометок убираются
    [Fact]
    public void Вуаль_ПромптНачинаетсяСВуали_БезСтрокКисти()
    {
        var png = TestImages.Png(10, 10);
        var marks = """[{"type":"brush","points":[]},{"type":"text","x":0.5,"y":0.5,"text":"сюда"}]""";

        var req = EditRequestComposer.Compose(Input(png, mask: png, marks: marks), ImageEditOp.Edit, VeilModel, 1).Value!;

        req.Prompt.Should().StartWith(EditRequestComposer.VeilHead,
            "вуаль: текст человека оборачивается, модель знает, что серая заливка — место правки");
        req.Prompt.Should().Contain("«сюда»", "пометки без кисти дошли");
        req.Prompt.Should().NotContain("выделено кистью", "строки кисти из промпта убраны");
        req.Prompt.Should().Contain(EditRequestComposer.VeilNote, "примечание о вуали дописано");
    }

    // Текст вуали — дословно из прогона Артёма 13.10 (вариант д2): на этих формулировках держится
    // результат модели, перефразировка или урезание должны краснеть
    [Fact]
    public void Вуаль_ТекстДословноИзПрогона()
    {
        EditRequestComposer.VeilHead.Should().Be(
            "На первой картинке место правки закрашено полупрозрачной серой заливкой. Что сделать с этим местом: ");
        EditRequestComposer.VeilNote.Should().Be(
            "Полупрозрачная серая заливка на первой картинке отмечает место правки; под ней видно, что там сейчас. " +
            "Измени отмеченное место так, как просят, сохранив форму и детали того, что под заливкой. Серой дымки в результате " +
            "остаться не должно. Всё вне отмеченного места оставь без изменений.");
    }

    // С размеченной копией вуаль не включается (драйвер даёт VeilMask = null): маска идёт образцом,
    // как до вуали, а промпт без VeilHead/VeilNote. Живой прогон Артёма 13.10: маска в канале до
    // модели не доходила, а промпт про серую заливку рисовал серый прямоугольник
    [Fact]
    public void Вуаль_СРазмеченнойКопией_МаскаОбразцом_БезВуали()
    {
        var png = TestImages.Png(10, 10);
        var annotated = TestImages.Png(10, 10, tail: 2);
        var marks = """[{"type":"brush","points":[[0.2,0.2],[0.4,0.4]]},{"type":"text","x":0.5,"y":0.5,"text":"сюда"}]""";

        var req = EditRequestComposer.Compose(
            Input(png, mask: png, annotated: annotated, marks: marks), ImageEditOp.Edit, VeilModel, 1).Value!;

        req.Mask.Should().BeNull("с размеченной копией канала маски нет — драйвер его не отдал бы модели");
        req.References.Should().Contain(r => r.Label == EditRequestComposer.AnnotatedLabel,
            "размеченная копия — образцом");
        req.References.Should().ContainSingle(r => r.Label == EditRequestComposer.MaskLabel,
            "маска — образцом, как до вуали")
            .Which.Bytes.Should().Equal(png);
        req.Prompt.Should().NotStartWith(EditRequestComposer.VeilHead, "вуали нет — текст человека не оборачивается");
        req.Prompt.Should().NotContain(EditRequestComposer.VeilNote, "вуали нет — примечания о серой заливке нет");
        req.Prompt.Should().Contain("белое — область, которую менять", "роль маски-образца названа");
        req.Prompt.Should().Contain("выделено кистью", "строки кисти на месте, как до вуали");
    }

    [Fact]
    public void Размеры_JpegИзМаркераSof()
    {
        byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x04, 0x00, 0x00, 0xFF, 0xC0, 0x00, 0x11, 0x08, 0x01, 0x2C, 0x02, 0x58, 0x03];

        ImageDimensions.Read(jpeg).Should().Be((600, 300));
    }
}
