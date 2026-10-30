using System.Collections.Concurrent;
using System.Security.Cryptography;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.ImageEditor;
using ClaudeHomeServer.Services.Images.Editing.Raster;
using ClaudeHomeServer.Tests.ImageEditor.Fakes;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;

namespace ClaudeHomeServer.Tests.ImageEditor.Jobs;

// Сторожа ADR-018 §9: автоуменьшение перед моделью и возврат размера после скачивания.
// Маска после ужатия совпадает с исходником и остаётся бинарной, оригинал в проекте не
// меняется, варианты той же пропорции приводятся к размеру исходника, другой — нет и с пометкой.
public class InputFitterTests : IDisposable
{
    private const string Project = "p1";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ie-fit-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly SkiaImageRaster _raster = new();
    private readonly List<ImageEditJobService> _services = [];

    public void Dispose()
    {
        foreach (var s in _services) s.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (DirectoryNotFoundException) { }
        GC.SuppressFinalize(this);
    }

    private static readonly ImageEditCaps Limit2048 =
        new([ImageEditOp.Edit, ImageEditOp.Inpaint, ImageEditOp.Outpaint], MaskSupport.AsReference, 3, 4, true,
            MaxInputSide: 2048);

    [Fact]
    [Trait("Category", "Slow")]
    public async Task Ужатие_6000x4000_под_2048_маска_и_исходник_равны_маска_бинарна()
    {
        var fitter = new InputFitter(_raster);
        var input = Input(Png(6000, 4000, SKColors.SteelBlue), mask: StripedMask(6000, 4000));

        var result = await fitter.FitAsync(input, Limit2048, default);

        result.Value.Should().NotBeNull(result.Error);
        var fitted = result.Value!;
        var source = _raster.Probe(fitted.Input.Source!.Bytes)!;
        var mask = _raster.Probe(fitted.Input.Mask!.Bytes)!;
        (source.Width, source.Height).Should().Be((2048, 1365));
        (mask.Width, mask.Height).Should().Be((source.Width, source.Height));
        MaskValues(fitted.Input.Mask.Bytes).Should().Equal([0, 255], "маска после ужатия обязана остаться бинарной");
        (fitted.SourceWidth, fitted.SourceHeight).Should().Be((6000, 4000), "приводить варианты — к размеру оригинала");
    }

    // Генерация по тексту: исходника нет, а фото персонажа всё равно ужимаются под лимит модели
    [Fact]
    public async Task Без_исходника_образцы_всё_равно_ужимаются()
    {
        var fitter = new InputFitter(_raster);
        var input = new ImageEditJobInput("q", "Тимур в кафе", null, null, null, null,
            [new ReferenceImage(Png(3000, 3000, SKColors.SteelBlue), "image/png", ReferenceRole.Character, "Тимур")], null);

        var result = await fitter.FitAsync(input, Limit2048, default);

        result.Value.Should().NotBeNull(result.Error);
        var probe = _raster.Probe(result.Value!.Input.References[0].Bytes)!;
        (probe.Width, probe.Height).Should().Be((2048, 2048));
        result.Value.SourceWidth.Should().BeNull();
    }

    [Fact]
    public async Task Маска_с_телефона_растягивается_до_исходника_и_остаётся_бинарной()
    {
        var fitter = new InputFitter(_raster);
        var noLimits = Limit2048 with { MaxInputSide = null };
        var input = Input(Png(3000, 2000, SKColors.SteelBlue), mask: StripedMask(1536, 1024));

        var result = await fitter.FitAsync(input, noLimits, default);

        var fitted = result.Value!.Input;
        var mask = _raster.Probe(fitted.Mask!.Bytes)!;
        (mask.Width, mask.Height).Should().Be((3000, 2000));
        fitted.Source!.Bytes.Should().BeSameAs(input.Source!.Bytes, "исходник под лимитом едет как пришёл");
        MaskValues(fitted.Mask.Bytes).Should().Equal([0, 255], "полосы обоих цветов доехали, промежуточных нет");
    }

    // Исполнитель не знает корня проекта, поэтому файл на диске он не тронет по построению —
    // проверка файла одна была бы вакуумной. Сторожим то, до чего конвейер реально дотягивается:
    // байты оригинала в памяти, из которых читается исходник, ужимаются в НОВЫЙ массив
    [Fact]
    [Trait("Category", "Slow")]
    public async Task Автоуменьшение_не_пишет_в_проект_хеш_оригинала_не_меняется()
    {
        var project = Path.Combine(_dir, "project");
        Directory.CreateDirectory(project);
        var file = Path.Combine(project, "hero.png");
        await File.WriteAllBytesAsync(file, Png(6000, 4000, SKColors.OliveDrab));
        var before = Hash(file);
        var original = await File.ReadAllBytesAsync(file);
        var editor = new SizedEditor(Png(1024, 683, SKColors.Red));
        var service = Service(editor);

        var job = await RunAsync(service, Input(original, mask: StripedMask(6000, 4000)));

        job.Status.Should().Be(ImageEditJobStatus.Completed, job.Error);
        var received = editor.Received!.Source!.Bytes;
        _raster.Probe(received)!.Width.Should().Be(2048, "драйвер получил ужатый исходник");
        received.Should().NotBeSameAs(original, "ужатие пишет в новый массив, а не поверх оригинала");
        Hash(original).Should().Be(before, "байты оригинала в памяти не изменены");
        Hash(file).Should().Be(before);
        Directory.GetFiles(project, "*", SearchOption.AllDirectories).Should().Equal(file);
    }

    [Fact]
    public async Task Возврат_размера_1к1_приводит_к_1024_квадрату()
    {
        var service = Service(new SizedEditor(Png(512, 512, SKColors.Red)));

        var job = await RunAsync(service, Input(Png(1024, 1024, SKColors.Gray)));

        job.Status.Should().Be(ImageEditJobStatus.Completed, job.Error);
        job.SizeNote.Should().BeNull();
        var variant = service.OpenVariant("u", Project, job.JobId, 1)!;
        var probe = _raster.Probe(variant.Bytes)!;
        (probe.Width, probe.Height).Should().Be((1024, 1024));
    }

    [Fact]
    public async Task Возврат_размера_16к9_при_квадратном_исходнике_свой_размер_и_пометка()
    {
        var service = Service(new SizedEditor(Png(1024, 576, SKColors.Red)));

        var job = await RunAsync(service, Input(Png(1024, 1024, SKColors.Gray)));

        job.Status.Should().Be(ImageEditJobStatus.Completed, job.Error);
        job.SizeNote.Should().Be(ImageEditSizeNotes.AspectMismatch);
        var probe = _raster.Probe(service.OpenVariant("u", Project, job.JobId, 1)!.Bytes)!;
        (probe.Width, probe.Height).Should().Be((1024, 576));
    }

    [Theory]
    [InlineData(false, ImageEditOp.Edit)]
    [InlineData(true, ImageEditOp.Outpaint)]
    public async Task Без_возврата_размера_вариант_не_трогается(bool match, ImageEditOp op)
    {
        var service = Service(new SizedEditor(Png(512, 512, SKColors.Red)));

        var job = await RunAsync(service, Input(Png(1024, 1024, SKColors.Gray)) with { MatchSourceSize = match }, op);

        job.Status.Should().Be(ImageEditJobStatus.Completed, job.Error);
        _raster.Probe(service.OpenVariant("u", Project, job.JobId, 1)!.Bytes)!.Width.Should().Be(512);
    }

    // Боевая модель подстановки на лицо с курируемыми лимитами входа (1664 px, 1,8 Мп)
    private static readonly ImageEditModelInfo Qwen = ImageEditCatalog.WithInputLimits(
        new LocalImageEditor(null).Models.Single(m => m.Id == LocalImageEditor.QwenImage));

    // Д5 (ADR-028): кроп лица посчитан в пикселях базовой версии, поэтому исходник 2400×1800 (4,3 Мп,
    // больше лимита модели) с кропом не ужимается; маска и KeepMask остаются ему в размер, кроп —
    // внутри холста и на месте лица, Compose проходит. Мутация «ужимать и при FaceCrop» → красный
    [Theory]
    [InlineData(2150, 700, 2390, 1000)] // лицо у правого края: после ужатия кроп ушёл бы за холст
    [InlineData(10, 700, 250, 1000)]    // лицо у левого края: после ужатия кроп молча съехал бы с лица
    public async Task Кроп_лица_исходник_2400x1800_не_ужимается_маски_в_размер_Compose_проходит(
        int x1, int y1, int x2, int y2)
    {
        var built = new FaceMaskBuilder(_raster).Build(2400, 1800,
            [new LocalDetectedFace(1, x1, y1, x2, y2, 0.9, false)], 1, Qwen.Caps);
        built.Error.Should().BeNull();
        var mask = new ImageBytes(built.Png!, "image/png");
        var crop = built.Crop!;
        var input = Input(Png(2400, 1800, SKColors.SteelBlue)) with { Mask = mask, KeepMask = mask, FaceCrop = crop };

        var result = await new InputFitter(_raster).FitAsync(input, Qwen.Caps, default);

        result.Value.Should().NotBeNull(result.Error);
        var fitted = result.Value!.Input;
        var source = _raster.Probe(fitted.Source!.Bytes)!;
        (source.Width, source.Height).Should().Be((2400, 1800), "исходник с кропом лица не ужимается");
        fitted.Mask!.Bytes.Should().Equal(mask.Bytes, "маска прежняя");
        var keep = _raster.Probe(fitted.KeepMask!.Bytes)!;
        (keep.Width, keep.Height).Should().Be((source.Width, source.Height), "KeepMask в размер исходника");
        var fittedMask = _raster.Probe(fitted.Mask.Bytes)!;
        (fittedMask.Width, fittedMask.Height).Should().Be((source.Width, source.Height));

        // Кроп внутри холста и на месте лица — те же проверки, что у адаптера
        fitted.FaceCrop.Should().Be(crop);
        (crop.X + crop.Width).Should().BeLessOrEqualTo(source.Width);
        (crop.Y + crop.Height).Should().BeLessOrEqualTo(source.Height);
        crop.X.Should().BeLessOrEqualTo(x1);
        crop.Y.Should().BeLessOrEqualTo(y1);
        (crop.X + crop.Width).Should().BeGreaterOrEqualTo(x2);
        (crop.Y + crop.Height).Should().BeGreaterOrEqualTo(y2);

        var composed = EditRequestComposer.Compose(fitted, ImageEditOp.Edit, Qwen, 1);
        composed.Value.Should().NotBeNull(composed.Error);
        composed.Value!.FaceCrop.Should().Be(crop);
    }

    // Регрессия: без кропа лица тот же исходник 2400×1800 ужимается под лимит модели, маска — следом
    [Fact]
    public async Task Без_кропа_лица_исходник_2400x1800_ужимается_как_раньше()
    {
        var input = Input(Png(2400, 1800, SKColors.SteelBlue), mask: StripedMask(2400, 1800));

        var result = await new InputFitter(_raster).FitAsync(input, Qwen.Caps, default);

        result.Value.Should().NotBeNull(result.Error);
        var fitted = result.Value!.Input;
        var source = _raster.Probe(fitted.Source!.Bytes)!;
        // 1,8 Мп: масштаб √(1,8e6 / 4,32e6) ≈ 0,6455 → 1549×1161
        (source.Width, source.Height).Should().Be((1549, 1161));
        var mask = _raster.Probe(fitted.Mask!.Bytes)!;
        (mask.Width, mask.Height).Should().Be((source.Width, source.Height));
    }

    [Fact]
    public void Каталог_отдаёт_курируемые_лимиты_входа()
    {
        var model = new ImageEditModelInfo("fal-ai/nano-banana-2/edit", "NB",
            new ImageEditCaps([ImageEditOp.Edit], MaskSupport.AsReference, 3, 4, true));

        var caps = ImageEditCatalog.WithInputLimits(model).Caps;

        caps.MaxInputSide.Should().Be(2048);
        caps.MaxInputMegapixels.Should().NotBeNull();
        caps.MaxInputMb.Should().NotBeNull();
    }

    // Ш3 (ADR-028): холст 40 МБ с кропом лица не пережимается — модель видит кроп, а не холст,
    // поэтому потолок холста (MaxCanvasMb = 90) выше 40 МБ: байты те же (PNG, без перекодирования)
    [Fact]
    public async Task Холст40МБ_С_Кропом_НеПережимается()
    {
        var canvas = PngOf(40 * 1024 * 1024);
        var input = Input(canvas) with { FaceCrop = new LocalImageCrop(0, 0, 8, 8, 16, 16) };

        var result = await new InputFitter(_raster).FitAsync(input, Qwen.Caps, default);

        result.Value.Should().NotBeNull(result.Error);
        result.Value!.Input.Source!.Bytes.Should().Equal(canvas, "40 МБ ≤ 90 МБ — без перекодирования");
    }

    // Ш3 (ADR-028): тот же холст 40 МБ БЕЗ кропа — потолок холста (MaxCanvasMb) не действует,
    // действует общий потолок загрузки (MaxInputMb = 30): 40 > 30 → перекодировано, как раньше.
    // Заглушка (16×16 + нули) при перекодировании становится маленьким PNG: проверяем, что вес
    // упал под 30 МБ (регрессия: без кропа потолок 30 МБ по-прежнему действует)
    [Fact]
    public async Task Холст40МБ_Без_Кропа_Ужимается_До30МБ()
    {
        var canvas = PngOf(40 * 1024 * 1024);
        var input = Input(canvas);

        var result = await new InputFitter(_raster).FitAsync(input, Qwen.Caps, default);

        result.Value.Should().NotBeNull(result.Error);
        var fitted = result.Value!.Input.Source!;
        fitted.Bytes.Should().NotEqual(canvas, "40 МБ > 30 МБ — перекодировано");
        fitted.Bytes.LongLength.Should().BeLessThanOrEqualTo(30 * 1024 * 1024, "под общим потолком загрузки");
    }

    // Ш3 (ADR-028): FitWeightAsync — выход прохода k легче потолка холста (MaxCanvasMb) — байты
    // те же, без перекодирования. Шумный PNG 256×192 (~150 КБ) с MaxCanvasMb = 0.5 (500 КБ)
    [Fact]
    public async Task FitWeight_Легче_Потолка_Холста_Без_Перекодирования()
    {
        var heavy = NoisePng(256, 192);
        heavy.LongLength.Should().BeLessThan(500 * 1024, "предусловие: легче потолка холста");
        var caps = Qwen.Caps with { MaxCanvasMb = 0.5 };

        var result = await new InputFitter(_raster).FitWeightAsync(new ImageBytes(heavy, "image/png"), caps, default);

        result.Value.Should().NotBeNull(result.Error);
        result.Value!.Bytes.Should().Equal(heavy, "легче потолка холста — без перекодирования");
    }

    // Ш3 (ADR-028): FitWeightAsync — выход прохода k тяжелее потолка холста (MaxCanvasMb) —
    // лестница JPEG: вес уменьшился, размер тот же. Шумный PNG 256×192 (~150 КБ) с MaxCanvasMb = 0.1
    [Fact]
    public async Task FitWeight_Тяжелее_Потолка_Холста_Лестница_РазмерТотЖе()
    {
        var heavy = NoisePng(256, 192);
        heavy.LongLength.Should().BeGreaterThan(100 * 1024, "предусловие: тяжелее потолка холста");
        var caps = Qwen.Caps with { MaxCanvasMb = 0.1 };
        var before = _raster.Probe(heavy)!;

        var result = await new InputFitter(_raster).FitWeightAsync(new ImageBytes(heavy, "image/png"), caps, default);

        result.Value.Should().NotBeNull(result.Error);
        var fitted = result.Value!;
        fitted.Bytes.LongLength.Should().BeLessThan(heavy.LongLength, "вес уменьшился");
        fitted.Bytes.LongLength.Should().BeLessThanOrEqualTo(100 * 1024, "под потолком холста");
        var after = _raster.Probe(fitted.Bytes)!;
        (after.Width, after.Height).Should().Be((before.Width, before.Height), "размер тот же");
    }

    // ── Хелперы ──────────────────────────────────────────────────────────────────

    private readonly FinishBroadcaster _finished = new();

    private ImageEditJobService Service(IImageEditor editor)
    {
        var service = new ImageEditJobService([editor], new ImageEditWorkspace(Path.Combine(_dir, "image-editor")),
            NullLogger<ImageEditJobService>.Instance, broadcaster: _finished, raster: _raster);
        _services.Add(service);
        return service;
    }

    private async Task<ImageEditJobDto> RunAsync(ImageEditJobService service, ImageEditJobInput input,
        ImageEditOp op = ImageEditOp.Edit)
    {
        var quote = await service.QuoteAsync("u", Project,
            new ImageEditQuoteRequest("test", "auto", EditMode.Fast, op, 1, false, 0, false, null, null), default);
        quote.Value.Should().NotBeNull(quote.Error);
        var started = await service.StartAsync("u", Project, input with { QuoteId = quote.Value!.QuoteId }, default);
        started.Value.Should().NotBeNull(started.Error);
        // Событие завершения исполнитель шлёт после смены статуса — ждём его, а не опрашиваем
        await _finished.For(started.Value!.JobId).WaitAsync(TimeSpan.FromSeconds(30));
        return service.Get("u", Project, started.Value.JobId)!;
    }

    // Завершение задачи — по событию completed/failed исполнителя. Событие может прийти раньше,
    // чем тест спросит, поэтому источник заводится с любой стороны
    private sealed class FinishBroadcaster : ISessionBroadcaster
    {
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _jobs = new();

        public Task For(string jobId) => Source(jobId).Task;

        private TaskCompletionSource Source(string jobId) =>
            _jobs.GetOrAdd(jobId, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

        public Task ToOwner(string ownerId, ServerMessage message)
        {
            var jobId = message switch
            {
                ImageEditCompletedMessage m => m.JobId,
                ImageEditFailedMessage m => m.JobId,
                _ => null,
            };
            if (jobId is not null) Source(jobId).TrySetResult();
            return Task.CompletedTask;
        }

        public Task ToSession(string sessionId, ServerMessage message) => Task.CompletedTask;
        public Task ToSessionExcept(string sessionId, string exceptConnectionId, ServerMessage message) => Task.CompletedTask;
        public Task ToProject(string projectId, ServerMessage message) => Task.CompletedTask;
        public Task ToPreviewLog(string projectId, string serviceId, ServerMessage message) => Task.CompletedTask;
    }

    private static ImageEditJobInput Input(byte[] source, byte[]? mask = null) =>
        new("q", "поменять фон", null, new ImageBytes(source, "image/png"),
            mask is null ? null : new ImageBytes(mask, "image/png"), null, [], "images/hero.png");

    // Драйвер, который отдаёт заданную картинку и запоминает, что получил
    private sealed class SizedEditor(byte[] result) : IImageEditor
    {
        public ImageEditRequest? Received;
        private static readonly ImageEditModelInfo Model = new("m", "M", Limit2048);

        public string Key => "test";
        public string Label => "Test";
        public string PriceUnit => ImageEditPriceUnits.Usd;
        public bool Enabled => true;
        public IReadOnlyList<ImageEditModelInfo> Models => [Model];
        public ImageEditModelInfo? PickModel(ImageEditOp op, EditMode mode, EditTraits traits) => Model;

        public Task<ImageEditResult> RunAsync(ImageEditRequest req, IProgress<EditProgress> progress, CancellationToken ct)
        {
            Received = req;
            return Task.FromResult(new ImageEditResult(EditOutcome.Ok, [new EditedImage(result, "image/png")],
                null, true, null, null));
        }

        public Task<bool> CancelRemoteAsync(string remoteId, CancellationToken ct) => Task.FromResult(false);
    }

    private static byte[] Png(int w, int h, SKColor color)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Opaque));
        bitmap.Erase(color);
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    // Холст нужного веса: настоящий маленький PNG + нули до длины. Не декодируем реальную картинку
    // такого веса — растр читает только заголовок (размер) и длину файла
    private static byte[] PngOf(int length)
    {
        var png = Png(16, 16, SKColors.SteelBlue);
        var result = new byte[length];
        Array.Copy(png, result, Math.Min(png.Length, length));
        return result;
    }

    // Настоящий PNG из шума: не сжимается, вес ≈ 3 байта на пиксель
    private static byte[] NoisePng(int w, int h)
    {
        var rnd = new Random(42);
        using var bitmap = new SKBitmap(w, h);
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                bitmap.SetPixel(x, y, new SKColor((byte)rnd.Next(256), (byte)rnd.Next(256), (byte)rnd.Next(256)));
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    // Полосы по 3 px: любая интерполяция вместо nearest дала бы на их краях серые значения
    private static byte[] StripedMask(int w, int h)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Opaque));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Black);
            using var paint = new SKPaint { Color = SKColors.White, IsAntialias = false };
            for (var x = 0; x < w; x += 6) canvas.DrawRect(x, 0, 3, h, paint);
        }
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    // Различные значения каналов по возрастанию
    private static int[] MaskValues(byte[] png)
    {
        using var bitmap = SKBitmap.Decode(png);
        return [.. bitmap.Pixels.SelectMany(p => new int[] { p.Red, p.Green, p.Blue }).Distinct().Order()];
    }

    private static string Hash(string path) => Hash(File.ReadAllBytes(path));

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
}
