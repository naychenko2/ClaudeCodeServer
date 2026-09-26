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
