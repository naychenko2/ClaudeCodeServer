using ClaudeHomeServer.Services.ImageEditor;
using ClaudeHomeServer.Services.Images.Editing.Raster;

namespace ClaudeHomeServer.Services.Images.Editing;

// Автоуменьшение входа и возврат размера (ADR-018 §9, п. 3 и 4). Точка одна для всех запусков
// (ручка, агент, быстрые действия), поэтому закрытый редактор ничего не меняет:
// - исходник, размеченная копия и образцы ужимаются под лимиты модели (ImageEditCaps.MaxInput*);
// - маска приводится к размеру УЖЕ ужатого исходника методом nearest: она остаётся бинарной, а
//   совпадение размеров (ADR-017 §3) держится после ужатия. Так же растягивается маска с
//   телефона, снятая в разрешении предпросмотра;
// - оригинал в проекте не трогается: всё происходит над байтами в памяти.
// Файл, который растр не распознал, едет как пришёл: решать о нём будет поставщик.
public sealed class InputFitter(IImageRaster raster)
{
    // Пропорции «те же», если отношения сторон расходятся не больше чем на 2 %
    public const double AspectTolerance = 0.02;
    private const int BusyRetries = 30;
    private static readonly TimeSpan BusyPause = TimeSpan.FromMilliseconds(100);
    private static readonly int[] JpegFallbackQualities = [90, 75, 60];

    public async Task<ImageEditCallResult<FittedInput>> FitAsync(
        ImageEditJobInput input, ImageEditCaps caps, CancellationToken ct)
    {
        var source = input.Source is { Bytes.Length: > 0 } ? input.Source : null;
        var probe = source is null ? null : raster.Probe(source.Bytes);
        if (source is null || probe is null)
            return Ok(new FittedInput(input, null, null));

        var fittedSource = await FitImageAsync(source, probe, caps, ct);
        if (fittedSource.Error is not null) return Fail(fittedSource);
        var fitted = fittedSource.Image!;
        var size = raster.Probe(fitted.Bytes);
        var (width, height) = size is null ? (probe.DisplayWidth, probe.DisplayHeight) : (size.DisplayWidth, size.DisplayHeight);

        var annotated = input.Annotated;
        if (annotated is { Bytes.Length: > 0 } && raster.Probe(annotated.Bytes) is { } ap)
        {
            var r = await FitImageAsync(annotated, ap, caps, ct);
            if (r.Error is not null) return Fail(r);
            annotated = r.Image;
        }

        var references = new List<ReferenceImage>();
        foreach (var reference in input.References ?? [])
        {
            if (raster.Probe(reference.Bytes) is not { } rp)
            {
                references.Add(reference);
                continue;
            }
            var r = await FitImageAsync(new ImageBytes(reference.Bytes, reference.ContentType), rp, caps, ct);
            if (r.Error is not null) return Fail(r);
            references.Add(reference with { Bytes = r.Image!.Bytes, ContentType = r.Image.ContentType });
        }

        var mask = input.Mask;
        if (mask is { Bytes.Length: > 0 } && raster.Probe(mask.Bytes) is { } mp
            && (mp.DisplayWidth != width || mp.DisplayHeight != height)
            && SameAspect(mp.DisplayWidth, mp.DisplayHeight, width, height))
        {
            var outcome = await RunAsync(() => raster.ResizeMask(mask.Bytes, width, height), ct);
            if (!outcome.Ok) return FailOutcome(outcome);
            mask = new ImageBytes(outcome.Image!.Bytes, "image/png");
        }

        var result = input with { Source = fitted, Annotated = annotated, References = references, Mask = mask };
        return Ok(new FittedInput(result, probe.DisplayWidth, probe.DisplayHeight));
    }

    // Возврат размера оригинала: вариант той же пропорции растягивается или ужимается до
    // width×height. Другая пропорция — вариант не трогаем, Mismatch = true («Размер не приведён»).
    // Сбой растра не роняет задачу: вариант остаётся как пришёл
    public async Task<SizeMatch> MatchSizeAsync(EditedImage image, int width, int height, CancellationToken ct)
    {
        if (raster.Probe(image.Bytes) is not { } p) return new SizeMatch(image, false);
        if (!SameAspect(p.DisplayWidth, p.DisplayHeight, width, height)) return new SizeMatch(image, true);
        if (p.DisplayWidth == width && p.DisplayHeight == height && p.Orientation <= 1) return new SizeMatch(image, false);

        var outcome = await RunAsync(() => raster.Apply(image.Bytes, [new ResizeOp(width, height, LockAspect: false)]), ct);
        return outcome.Ok
            ? new SizeMatch(new EditedImage(outcome.Image!.Bytes, ContentTypeOf(outcome.Image.Format)), false)
            : new SizeMatch(image, false);
    }

    public static bool SameAspect(int w1, int h1, int w2, int h2)
    {
        if (w1 <= 0 || h1 <= 0 || w2 <= 0 || h2 <= 0) return false;
        var ratio = (double)w1 / h1 / ((double)w2 / h2);
        return Math.Abs(ratio - 1) <= AspectTolerance;
    }

    // Размер под лимит стороны и площади; null — ужимать не нужно
    public static (int Width, int Height)? TargetSize(int width, int height, ImageEditCaps caps)
    {
        var scale = 1d;
        if (caps.MaxInputSide is { } side && side > 0)
            scale = Math.Min(scale, (double)side / Math.Max(width, height));
        if (caps.MaxInputMegapixels is { } mp && mp > 0)
            scale = Math.Min(scale, Math.Sqrt(mp * 1_000_000d / ((double)width * height)));
        if (scale >= 1) return null;
        return (Math.Max(1, (int)Math.Floor(width * scale)), Math.Max(1, (int)Math.Floor(height * scale)));
    }

    private async Task<Fitted> FitImageAsync(ImageBytes image, RasterProbe probe, ImageEditCaps caps, CancellationToken ct)
    {
        var target = TargetSize(probe.DisplayWidth, probe.DisplayHeight, caps);
        IReadOnlyList<ImageTransformOp> ops = target is { } t ? [new ResizeOp(t.Width, t.Height, LockAspect: false)] : [];
        var maxBytes = caps.MaxInputMb is { } mb && mb > 0 ? (long)(mb * 1024 * 1024) : long.MaxValue;

        // Ни размер, ни ориентация, ни вес не мешают — байты едут как есть
        if (ops.Count == 0 && probe.Orientation <= 1 && probe.Bytes <= maxBytes)
            return new Fitted(image, null);

        var outcome = await RunAsync(() => raster.Apply(image.Bytes, ops), ct);
        if (!outcome.Ok) return new Fitted(null, outcome);
        // Всё ещё тяжелее лимита — JPEG со ступенями качества; не влезло и так — отдаём
        // самый лёгкий, пусть решает поставщик
        foreach (var quality in JpegFallbackQualities)
        {
            if (outcome.Image!.Bytes.LongLength <= maxBytes) break;
            var jpeg = await RunAsync(() => raster.Apply(image.Bytes, ops, new ImageEncodeSpec(ImageEncodeFormat.Jpeg, quality)), ct);
            if (!jpeg.Ok) return new Fitted(null, jpeg);
            outcome = jpeg;
        }
        return new Fitted(new ImageBytes(outcome.Image!.Bytes, ContentTypeOf(outcome.Image.Format)), null);
    }

    // Семафор растра не ждёт (Wait(0)); запуск задачи — ждёт недолго, прежде чем сдаться
    private async Task<RasterOutcome> RunAsync(Func<RasterOutcome> op, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var outcome = op();
            if (outcome.Error != RasterError.Busy || attempt >= BusyRetries) return outcome;
            await Task.Delay(BusyPause, ct);
        }
    }

    public static string ContentTypeOf(ImageEncodeFormat format) => format switch
    {
        ImageEncodeFormat.Jpeg => "image/jpeg",
        ImageEncodeFormat.Webp => "image/webp",
        _ => "image/png",
    };

    private sealed record Fitted(ImageBytes? Image, RasterOutcome? Error);

    private static ImageEditCallResult<FittedInput> Ok(FittedInput value) => ImageEditCallResult<FittedInput>.Ok(value);

    private static ImageEditCallResult<FittedInput> Fail(Fitted fitted) => FailOutcome(fitted.Error!);

    private static ImageEditCallResult<FittedInput> FailOutcome(RasterOutcome outcome) =>
        ImageEditCallResult<FittedInput>.Fail(
            outcome.Error == RasterError.Busy ? ImageEditErrorCodes.TooManyJobs : ImageEditErrorCodes.InvalidRequest,
            outcome.Message ?? "Картинку не удалось подготовить");
}

// SourceWidth/SourceHeight — размер исходника ДО ужатия (с учётом EXIF-ориентации): к нему
// приводятся варианты; null — исходника нет или растр его не распознал
public sealed record FittedInput(ImageEditJobInput Input, int? SourceWidth, int? SourceHeight);

public sealed record SizeMatch(EditedImage Image, bool Mismatch);
