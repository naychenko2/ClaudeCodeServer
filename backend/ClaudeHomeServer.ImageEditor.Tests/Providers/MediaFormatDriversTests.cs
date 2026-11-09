using System.Net;
using System.Text.Json.Nodes;
using ClaudeHomeServer.Services.ChatContext;
using ClaudeHomeServer.Services.Higgsfield;
using ClaudeHomeServer.Services.ImageEditor;
using ClaudeHomeServer.Services.Images.LocalMedia;
using ClaudeHomeServer.Services.Media;
using ClaudeHomeServer.Tests.ImageEditor.Fakes;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.ImageEditor.Providers;

// «Формат» (ADR-025) доходит до модели и до цены: ступень и размер в теле запроса каждого драйвера,
// ступень в котировке. Тела проверяются на фейках HTTP, облако не трогается. Мутация: убрать параметр из
// драйвера — тест краснеет.
public class MediaFormatDriversTests
{
    private const string Queue = "https://queue.test";
    private const string Api = "https://api.test/v1";

    private static MediaFormat Wish(string aspect, FormatTier tier) =>
        new(FormatModes.Aspect, aspect, tier, null, null, ContextActor.Human, DateTime.UtcNow);

    private static ResolvedFormat ResolvedOn(ImageEditModelInfo model, string aspect, FormatTier tier) =>
        MediaFormatResolver.Resolve(model.Caps.Format, Wish(aspect, tier));

    private static FalImageEditor Fal(FakeHttp http) =>
        new(http, TestImages.Config(("Fal:ApiKey", "k"), ("Fal:QueueBase", Queue), ("Fal:ApiBase", Api)),
            NullLogger<FalImageEditor>.Instance) { PollInterval = TimeSpan.Zero, Downloader = http.Downloader() };

    [Fact]
    public void Каталоги_трёх_драйверов_объявляют_формат_у_сверенных_моделей()
    {
        var fal = Fal(new FakeHttp(_ => FakeHttp.Json("{}")));
        fal.Models.Single(m => m.Id == FalImageEditor.NanoBananaGenerate).Caps.Format.Should().NotBeNull();
        fal.Models.Single(m => m.Id == FalImageEditor.KontextPro).Caps.Format.Should().NotBeNull();
        fal.Models.Single(m => m.Id == FalImageEditor.FluxFill).Caps.Format.Should().BeNull("схема не сверена — «Как у модели»");

        var (hf, _, _) = HiggsfieldImageEditorTests.Create();
        hf.Models.Single(m => m.Id == HiggsfieldImageEditor.GptImage).Caps.Format.Should().NotBeNull();
        hf.Models.Single(m => m.Id == HiggsfieldImageEditor.BackgroundRemover).Caps.Format.Should().BeNull();

        new LocalImageEditor(new LocalImageEditorTests.FakeMedia()).Models
            .Single(m => m.Id == LocalImageEditor.QwenImage).Caps.Format.Should().NotBeNull();
    }

    [Fact]
    public void Fal_ступень_и_пропорции_уходят_в_тело()
    {
        var fal = Fal(new FakeHttp(_ => FakeHttp.Json("{}")));
        var model = fal.Models.Single(m => m.Id == FalImageEditor.NanoBananaGenerate);
        var resolved = ResolvedOn(model, "16:9", FormatTier.High);

        var body = FalImageEditor.BuildBody(new ImageEditRequest(ImageEditOp.Generate, "кот", null, null, [], 1,
            resolved.Aspect, null, model.Id, null, Format: resolved));

        body["aspect_ratio"].Should().Be("16:9");
        body["resolution"].Should().Be("2K");
    }

    [Fact]
    public void Fal_модель_без_параметра_ступени_resolution_не_получает()
    {
        var fal = Fal(new FakeHttp(_ => FakeHttp.Json("{}")));
        var model = fal.Models.Single(m => m.Id == FalImageEditor.KontextPro);
        var resolved = ResolvedOn(model, "3:2", FormatTier.Standard);

        var body = FalImageEditor.BuildBody(new ImageEditRequest(ImageEditOp.Edit, "x",
            new ImageBytes(TestImages.Png(4, 4), "image/png"), null, [], 1, resolved.Aspect, null, model.Id, null, Format: resolved));

        body["aspect_ratio"].Should().Be("3:2");
        body.Should().NotContainKey("resolution", "у Kontext размер задаёт модель: лишний параметр даёт 422");
    }

    [Fact]
    public async Task Fal_котировка_учитывает_ступень_и_помечает_оценку()
    {
        var http = new FakeHttp(_ => FakeHttp.Json("""{"prices":[{"endpoint_id":"fal-ai/nano-banana-2","unit_price":0.08,"unit":"images"}]}"""));
        var fal = Fal(http);
        var model = fal.Models.Single(m => m.Id == FalImageEditor.NanoBananaGenerate);

        ImageEditQuoteRequest Quote(FormatTier tier) => new("fal", model.Id, EditMode.Auto, ImageEditOp.Generate, 1, false)
        {
            Resolved = ResolvedOn(model, "1:1", tier),
        };

        var standard = await fal.EstimateAsync(model, Quote(FormatTier.Standard), default);
        var max = await fal.EstimateAsync(model, Quote(FormatTier.Max), default);

        max.Amount.Should().BeGreaterThan(standard.Amount!.Value, "прайс fal ступень не различает, цена ступени — множитель каталога");
        max.Approx.Should().BeTrue();
        standard.Approx.Should().BeFalse();
    }

    [Fact]
    public async Task Higgsfield_запуск_и_проба_цены_несут_пропорции_и_ступень()
    {
        var (editor, http, _) = HiggsfieldImageEditorTests.Create();
        var model = editor.Models.Single(m => m.Id == HiggsfieldImageEditor.NanoBanana2);
        var resolved = ResolvedOn(model, "9:16", FormatTier.High);

        await editor.EstimateAsync(model,
            new ImageEditQuoteRequest("higgsfield", model.Id, EditMode.Auto, ImageEditOp.Generate, 1, false) { Resolved = resolved }, default);
        var probe = HiggsfieldImageEditorTests.GenerateArgs(http.Calls.Single())!;
        probe["aspect_ratio"]!.GetValue<string>().Should().Be("9:16");
        probe["resolution"]!.GetValue<string>().Should().Be("2k");

        await editor.RunAsync(new ImageEditRequest(ImageEditOp.Generate, "кот", null, null, [], 1, resolved.Aspect, null,
            model.Id, null, Format: resolved), new SyncProgress(_ => { }), default);
        var launch = HiggsfieldImageEditorTests.GenerateArgs(HiggsfieldImageEditorTests.Launch(http))!;
        launch["aspect_ratio"]!.GetValue<string>().Should().Be("9:16");
        launch["resolution"]!.GetValue<string>().Should().Be("2k");
    }

    [Fact]
    public async Task Higgsfield_проба_цены_кешируется_на_десять_минут_по_модели_пропорции_и_ступени()
    {
        var (editor, http, _) = HiggsfieldImageEditorTests.Create();
        var now = new DateTime(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc);
        editor.Clock = () => now;
        var model = editor.Models.Single(m => m.Id == HiggsfieldImageEditor.NanoBanana2);
        ImageEditQuoteRequest Quote(string aspect) => new("higgsfield", model.Id, EditMode.Auto, ImageEditOp.Generate, 1, false)
        {
            Resolved = ResolvedOn(model, aspect, FormatTier.Standard),
        };

        await editor.EstimateAsync(model, Quote("1:1"), default);
        await editor.EstimateAsync(model, Quote("1:1"), default);
        http.Calls.Count(HiggsfieldImageEditorTests.IsCostCall).Should().Be(1, "вторая котировка той же тройки — из кеша");

        await editor.EstimateAsync(model, Quote("16:9"), default);
        http.Calls.Count(HiggsfieldImageEditorTests.IsCostCall).Should().Be(2, "другая пропорция — новая проба");

        now += TimeSpan.FromMinutes(11);
        await editor.EstimateAsync(model, Quote("1:1"), default);
        http.Calls.Count(HiggsfieldImageEditorTests.IsCostCall).Should().Be(3, "через 10 минут кеш истёк");
    }

    [Fact]
    public async Task Local_точный_размер_уходит_холстом_генерации()
    {
        var media = new LocalImageEditorTests.FakeMedia();
        var editor = new LocalImageEditor(media) { PollInterval = TimeSpan.FromMilliseconds(1) };
        var model = editor.Models.Single(m => m.Id == LocalImageEditor.QwenImage);
        var resolved = MediaFormatResolver.Resolve(model.Caps.Format,
            new MediaFormat(FormatModes.Exact, null, null, 1920, 1080, ContextActor.Human, DateTime.UtcNow));

        await editor.RunAsync(new ImageEditRequest(ImageEditOp.Generate, "кот", null, null, [], 1, null, null, model.Id, null,
            Format: resolved), new SyncProgress(_ => { }), default);

        var run = media.Submitted.Single();
        run.Width.Should().Be(1664);
        run.Height.Should().Be(944, "1920×1080 сужено до потолка 1664 по длинной стороне и округлено кратно 16");
    }

    [Fact]
    public async Task Local_без_формата_холст_по_пропорции_как_раньше()
    {
        var media = new LocalImageEditorTests.FakeMedia();
        var editor = new LocalImageEditor(media) { PollInterval = TimeSpan.FromMilliseconds(1) };

        await editor.RunAsync(new ImageEditRequest(ImageEditOp.Generate, "кот", null, null, [], 1, "16:9", null,
            LocalImageEditor.QwenImage, null), new SyncProgress(_ => { }), default);

        var run = media.Submitted.Single();
        run.Aspect.Should().Be("16:9");
        run.Width.Should().BeNull();
    }

    // Зеркало таблицы размеров Qwen-Image в модуле не должно разойтись с источником в Images
    [Fact]
    public void Размеры_Qwen_в_каталоге_совпадают_с_ComfyWorkflows()
    {
        var source = ClaudeHomeServer.Services.Images.LocalMedia.ComfyWorkflows.ImageSizes;

        LocalImageEditor.QwenSizes.Keys.Should().BeEquivalentTo(source.Keys);
        foreach (var (aspect, size) in LocalImageEditor.QwenSizes)
            (size.W, size.H).Should().Be(source[aspect], aspect);
    }

    // Зеркало потолка холста (ADR-028, Ш3): MaxCanvasMb у Qwen-Image = потолок загрузки узла
    // (LocalMediaService.MaxMediaInputBytes, 90 МБ) — адаптер принимает холст с кропом до него,
    // поэтому редактор не пережимает его; расхождение чисел 90 ↔ адаптер → красный
    [Fact]
    public void Потолок_Холста_Qwen_совпадает_С_Загрузкой_Узла()
    {
        var qwen = ImageEditCatalog.WithInputLimits(
            new LocalImageEditor(null).Models.Single(m => m.Id == LocalImageEditor.QwenImage));

        qwen.Caps.MaxCanvasMb.Should().Be(LocalMediaService.MaxMediaInputBytes / 1024 / 1024);
    }
}
