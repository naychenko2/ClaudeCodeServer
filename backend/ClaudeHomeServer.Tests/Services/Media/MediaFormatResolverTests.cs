using ClaudeHomeServer.Services.ChatContext;
using ClaudeHomeServer.Services.ImageEditor;
using ClaudeHomeServer.Services.Media;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services.Media;

// Единственная арифметика формата (ADR-025): сценарии 2, 3 и 9 записки макета дословно.
public sealed class MediaFormatResolverTests
{
    private static readonly Dictionary<string, FormatSize> NoSizes = [];

    private static MediaFormat Aspect(string aspect, FormatTier? tier = null) =>
        new(FormatModes.Aspect, aspect, tier, null, null, ContextActor.Human, DateTime.UtcNow);

    private static MediaFormat Exact(int w, int h) =>
        new(FormatModes.Exact, null, null, w, h, ContextActor.Human, DateTime.UtcNow);

    // FLUX: четыре пропорции, три ступени, «Точно…» кратно 64 до 2048 по длинной стороне
    private static readonly MediaFormatCaps Flux = new("FLUX", ["1:1", "16:9", "9:16", "3:2"],
    [
        new FormatTierSpec(FormatTier.Standard, "1 Мп", new Dictionary<string, FormatSize> { ["16:9"] = new(1344, 768) }, "1"),
        new FormatTierSpec(FormatTier.High, "2 Мп", new Dictionary<string, FormatSize> { ["16:9"] = new(1792, 1024) }, "2"),
        new FormatTierSpec(FormatTier.Max, "4 Мп", new Dictionary<string, FormatSize> { ["16:9"] = new(2048, 1152) }, "4"),
    ], new ExactSizeSpec(64, 256, 2048), "megapixels");

    // GPT Image: 1:1, 3:2, 2:3 и один размер ~1,6 Мп; «Точно…» нет
    private static readonly MediaFormatCaps Gpt = new("GPT Image", ["1:1", "3:2", "2:3"],
        [new FormatTierSpec(FormatTier.Standard, "1,6 Мп", new Dictionary<string, FormatSize>
        {
            ["1:1"] = new(1024, 1024), ["3:2"] = new(1536, 1024), ["2:3"] = new(1024, 1536),
        })], null, null, "один размер ~1,6 Мп");

    [Fact]
    public void Сценарий_2_нет_16_9_у_GPT_Image_взяли_3_2()
    {
        var resolved = MediaFormatResolver.Resolve(Gpt, Aspect("16:9", FormatTier.Standard));

        resolved.Aspect.Should().Be("3:2");
        resolved.Substituted.Should().BeTrue();
        resolved.Note.Should().Be("16:9 нет у GPT Image — взяли 3:2.");
        (resolved.Width, resolved.Height).Should().Be((1536, 1024));
    }

    [Fact]
    public void Желание_хранится_а_не_подмена_возврат_к_модели_с_16_9_возвращает_16_9()
    {
        var wish = Aspect("16:9", FormatTier.High);

        var onFlux = MediaFormatResolver.Resolve(Flux, wish);
        var onGpt = MediaFormatResolver.Resolve(Gpt, wish);
        var backOnFlux = MediaFormatResolver.Resolve(Flux, wish);

        onFlux.Aspect.Should().Be("16:9");
        onGpt.Aspect.Should().Be("3:2");
        onGpt.Note.Should().NotBeNull();
        backOnFlux.Should().BeEquivalentTo(onFlux, "желание не менялось, подмена была временной");
        wish.Aspect.Should().Be("16:9");
    }

    [Fact]
    public void Сценарий_3_точный_размер_округляется_кратно_64_с_пометкой()
    {
        var resolved = MediaFormatResolver.Resolve(Flux, Exact(1920, 1080));

        (resolved.Width, resolved.Height).Should().Be((1920, 1088));
        resolved.Rounded.Should().BeTrue();
        resolved.Note.Should().Be("Модель рисует кратно 64: 1920×1088.");
    }

    [Fact]
    public void Сценарий_3_длинная_сторона_ограничена_потолком_модели()
    {
        var resolved = MediaFormatResolver.Resolve(Flux, Exact(2600, 1400));

        (resolved.Width, resolved.Height).Should().Be((2048, 1088));
        resolved.Note.Should().Be("Модель рисует до 2048 px по длинной стороне; Модель рисует кратно 64: 2048×1088.");
    }

    [Fact]
    public void Точный_размер_уже_кратный_не_округляется_и_без_пометки()
    {
        var resolved = MediaFormatResolver.Resolve(Flux, Exact(1920, 1088));

        resolved.Rounded.Should().BeFalse();
        resolved.Note.Should().BeNull();
    }

    [Fact]
    public void Точный_размер_у_модели_без_Точно_заменяется_ближайшей_пропорцией()
    {
        var resolved = MediaFormatResolver.Resolve(Gpt, Exact(1920, 1080));

        resolved.Substituted.Should().BeTrue();
        resolved.Aspect.Should().Be("3:2");
        resolved.Note.Should().StartWith("Точный размер 1920×1080 GPT Image не умеет — взяли 3:2.");
    }

    [Fact]
    public void Сценарий_9_модель_без_размеров_даёт_Как_у_модели()
    {
        MediaFormatResolver.Resolve(null, Aspect("16:9", FormatTier.High)).Mode.Should().Be(FormatModes.Model);
        MediaFormatResolver.Resolve(Flux, null).Mode.Should().Be(FormatModes.Model);
        var state = MediaFormatResolver.State(null, Aspect("16:9"));
        state.Caps.Should().BeNull();
        state.Tiers.Should().BeEmpty();
        state.Resolved.Aspect.Should().BeNull();
    }

    [Fact]
    public void Ступени_без_размера_модели_серые_с_причиной()
    {
        var state = MediaFormatResolver.State(Gpt, Aspect("1:1", FormatTier.High));

        state.Tiers.Select(t => t.DisabledReason is null).Should().Equal(true, false, false);
        state.Tiers[1].DisabledReason.Should().Be(
            "Модель рисует один размер ~1,6 Мп — «Высокое» и «Макс» у неё недоступны");
        state.ExactUnavailableReason.Should().Be(
            "«Точно…» недоступно: GPT Image рисует только три размера — 1024×1024, 1536×1024 и 1024×1536.");
    }

    [Fact]
    public void Макс_без_родного_размера_серая_а_с_PostStep_апскейла_нет()
    {
        var withoutPost = MediaFormatResolver.TierOptions(Gpt, "1:1").Single(t => t.Tier == FormatTier.Max);
        withoutPost.DisabledReason.Should().NotBeNull();

        var local = new MediaFormatCaps("MiniMax H3 (локально)", ["16:9"],
        [
            new FormatTierSpec(FormatTier.Standard, "480p", NoSizes, "half"),
            new FormatTierSpec(FormatTier.High, "768p", NoSizes, "full"),
            new FormatTierSpec(FormatTier.Max, "1440p", NoSizes, "full", PostStep: FormatPostSteps.Upscale),
        ]);
        var max = MediaFormatResolver.TierOptions(local, "16:9").Single(t => t.Tier == FormatTier.Max);
        max.DisabledReason.Should().BeNull();
        max.PostStep.Should().Be(FormatPostSteps.Upscale);
    }

    [Fact]
    public void Нет_нужной_ступени_берётся_ближайшая_ниже_с_пометкой()
    {
        var resolved = MediaFormatResolver.Resolve(Gpt, Aspect("1:1", FormatTier.Max));

        resolved.Tier.Should().Be(FormatTier.Standard);
        resolved.Substituted.Should().BeTrue();
        resolved.Note.Should().Be("У GPT Image другие ступени: «Макс» — 1,6 Мп.");
    }

    [Fact]
    public void Как_у_исходника_формата_модели_не_трогает()
    {
        var source = new MediaFormat(FormatModes.Source, null, null, null, null, ContextActor.Human, DateTime.UtcNow);

        var resolved = MediaFormatResolver.Resolve(Flux, source);

        resolved.Mode.Should().Be(FormatModes.Source);
        resolved.Aspect.Should().BeNull();
    }

    [Fact]
    public void AspectCrop_режет_бока_и_верх_с_якорем()
    {
        // 4:3 → 1:1: режутся бока, доля ширины 0.75
        var center = AspectCrop.Rect(1600, 1200, "1:1", "center")!;
        center.Width.Should().BeApproximately(0.75, 1e-9);
        center.X.Should().BeApproximately(0.125, 1e-9);
        AspectCrop.Rect(1600, 1200, "1:1", "left")!.X.Should().Be(0);
        AspectCrop.Rect(1600, 1200, "1:1", "right")!.X.Should().BeApproximately(0.25, 1e-9);

        // 3:4 → 1:1: режутся верх и низ
        var top = AspectCrop.Rect(1200, 1600, "1:1", "top")!;
        top.Y.Should().Be(0);
        top.Height.Should().BeApproximately(0.75, 1e-9);
        AspectCrop.ResultSize(1600, 1200, "1:1", "left").Should().Be(new FormatSize(1200, 1200));
    }

    [Fact]
    public void AspectCrop_плохие_пропорции_и_якорь_дают_null_а_те_же_пропорции_всю_картинку()
    {
        AspectCrop.Rect(100, 100, "abc", null).Should().BeNull();
        AspectCrop.Rect(100, 100, "1:1", "диагональ").Should().BeNull();
        AspectCrop.Rect(100, 100, "1:1", null).Should().Be(new ImageFractionRect(0, 0, 1, 1));
    }

    // Правка Глеба по Б1: модель без «Точно…» и без списка пропорций (fal MiniMax H3 берёт пропорцию из кадра) не получает
    // подменённой пропорции — причина есть, а подставлять нечего
    [Fact]
    public void Модель_без_Exact_и_без_Aspects_даёт_причину_и_не_подменяет_пропорцию()
    {
        var byFrame = new MediaFormatCaps("MiniMax H3", [],
            [new FormatTierSpec(FormatTier.Standard, "768P", NoSizes, "768P")]);

        var resolved = MediaFormatResolver.Resolve(byFrame, Exact(1920, 1080));

        resolved.Aspect.Should().BeNull("пропорции у модели нет — подменять нечем, «ближайшей» не выдумываем");
        resolved.Substituted.Should().BeTrue();
        resolved.Note.Should().Contain("Точный размер 1920×1080 MiniMax H3 не умеет").And.Contain("размер модели");
        resolved.Tier.Should().Be(FormatTier.Standard, "ступень при этом сохраняется");
    }

    // Правка Глеба по Б1: текст не маскирует «пропорции» без пропорций под «как у модели» (валидацию проверяет ImageEditor.Tests)
    [Fact]
    public void Желание_пропорции_без_пропорций_не_маскируется_под_как_у_модели()
    {
        var broken = new MediaFormat(FormatModes.Aspect, null, null, null, null, ContextActor.Human, DateTime.UtcNow);

        MediaFormatText.Wish(broken).Should().NotBe("как у модели").And.Contain("не заданы");
    }

    // Правка Глеба Б2.7: оговорка хвоста различает «модель не выбрана» и «модель сообщает только пропорции»
    [Fact]
    public void Строка_хвоста_честно_разводит_модель_не_выбрана_и_модель_без_размеров()
    {
        var wish = new MediaFormat(FormatModes.Aspect, "16:9", FormatTier.High, null, null, ContextActor.Human, DateTime.UtcNow);
        var model = MediaFormatResolver.Resolve(null, wish);

        MediaFormatText.Line(wish, model, modelChosen: false).Should()
            .Be("16:9 · Высокое (модель не выбрана — формат применится, когда выберете модель) — выбор человека");
        MediaFormatText.Line(wish, model, modelChosen: true).Should()
            .Be("16:9 · Высокое (модель сообщает только пропорции — ступень и точный размер не учтутся) — выбор человека");
    }
}
