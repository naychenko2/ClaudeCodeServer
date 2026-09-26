using ClaudeHomeServer.Services.ImageEditor;
using SkiaSharp;

namespace ClaudeHomeServer.Services.Images.Editing.Raster;

// Реализация IImageRaster на SkiaSharp (ADR-018 §9, раздел «Чем SkiaSharp отличается»):
// - потолок мегапикселей проверяется по SKCodec.Info — заголовок, пиксели не декодируются;
// - AutoOrient делаем сами по SKCodec.EncodedOrigin, на выходе ориентация всегда нормальная;
// - цветовое пространство исходника едет в SKImageInfo результата, энкодер пишет ICC;
// - EXIF энкодеры Skia не пишут вовсе — геометка пропадает сама (держится тестом);
// - ресайз — Mitchell, при уменьшении больше чем в 2 раза ступенями по 2×; маска — nearest.
public sealed class SkiaImageRaster : IImageRaster
{
    public const int DefaultMaxMegapixels = 100;
    public const int DefaultMaxConcurrent = 2;
    public const int DefaultQuality = 90;
    public const int MinQuality = 40;
    public const int MaxQuality = 100;
    // Сторона результата: защита от ResizeOp на 100 000 px, который прошёл бы проверку входа
    public const int MaxSide = 16_384;

    private static readonly SKSamplingOptions Mitchell = new(SKCubicResampler.Mitchell);
    private static readonly SKSamplingOptions Nearest = new(SKFilterMode.Nearest, SKMipmapMode.None);

    private readonly long _maxPixels;
    private readonly SemaphoreSlim _gate;

    public SkiaImageRaster() : this(DefaultMaxMegapixels, new SemaphoreSlim(DefaultMaxConcurrent)) { }

    // Для тестов: свой потолок и свой семафор, чтобы занятость проверялась без гонок
    internal SkiaImageRaster(int maxMegapixels, SemaphoreSlim gate)
    {
        _maxPixels = (long)maxMegapixels * 1_000_000;
        _gate = gate;
    }

    public RasterProbe? Probe(byte[] data)
    {
        using var codec = CreateCodec(data);
        if (codec is null) return null;
        var info = codec.Info;
        return new RasterProbe(
            ToFormat(codec.EncodedFormat),
            codec.EncodedFormat.ToString().ToLowerInvariant(),
            info.Width,
            info.Height,
            (int)codec.EncodedOrigin,
            data.LongLength);
    }

    public RasterOutcome Apply(byte[] data, IReadOnlyList<ImageTransformOp> ops, ImageEncodeSpec? encode = null) =>
        Run(data, unpremul: false, (bitmap, sourceFormat) =>
        {
            var current = bitmap;
            try
            {
                foreach (var op in ops)
                {
                    var next = ApplyOp(current, op);
                    if (!ReferenceEquals(next, current)) { current.Dispose(); current = next; }
                }
                return EncodeResult(current, encode, sourceFormat);
            }
            finally
            {
                current.Dispose();
            }
        });

    public RasterOutcome Encode(byte[] data, ImageEncodeSpec encode) =>
        Apply(data, [], encode);

    public RasterOutcome ResizeMask(byte[] mask, int width, int height)
    {
        if (width < 1 || height < 1 || width > MaxSide || height > MaxSide)
            return RasterOutcome.Fail(RasterError.InvalidOp, $"Размер маски вне 1–{MaxSide} px");
        // Unpremul: у полупрозрачной маски премультипликация дала бы промежуточные значения
        return Run(mask, unpremul: true, (bitmap, _) =>
        {
            using (bitmap)
            {
                if (bitmap.Width == width && bitmap.Height == height)
                    return Success(bitmap, ImageEncodeFormat.Png, MaxQuality);
                using var scaled = new SKBitmap(bitmap.Info.WithSize(width, height));
                if (!bitmap.ScalePixels(scaled, Nearest))
                    throw new RasterFailure(RasterError.Unsupported, "Не удалось масштабировать маску");
                return Success(scaled, ImageEncodeFormat.Png, MaxQuality);
            }
        });
    }

    public RasterOutcome EraseMasked(byte[] image, byte[] mask) =>
        Run(image, unpremul: false, (bitmap, _) =>
        {
            using (bitmap)
            {
                // Маска маленькая и уже проверена по размеру вызывающим: декодируем её без второго
                // места в семафоре — вложенный Run мог бы упереться в собственный потолок
                using var codec = CreateCodec(mask)
                    ?? throw new RasterFailure(RasterError.Unsupported, "Формат маски не распознан");
                var decoded = Decode(codec, unpremul: true);
                using var maskBitmap = Orient(decoded, codec.EncodedOrigin);
                if (!ReferenceEquals(maskBitmap, decoded)) decoded.Dispose();
                if (maskBitmap.Width != bitmap.Width || maskBitmap.Height != bitmap.Height)
                    throw new RasterFailure(RasterError.InvalidOp,
                        $"Размер маски {maskBitmap.Width}×{maskBitmap.Height} не совпадает с картинкой {bitmap.Width}×{bitmap.Height}");

                var pixels = bitmap.GetPixelSpan();
                var marks = maskBitmap.GetPixelSpan();
                for (var i = 0; i < marks.Length; i += 4)
                {
                    // Rgba8888: яркость по трём каналам, прозрачное на маске — не отмечено
                    var lit = (marks[i] + marks[i + 1] + marks[i + 2]) / 3 * marks[i + 3] / 255;
                    if (lit <= 127) continue;
                    pixels[i] = pixels[i + 1] = pixels[i + 2] = EraseGray;
                    pixels[i + 3] = 255;
                }
                return Success(bitmap, ImageEncodeFormat.Png, MaxQuality);
            }
        });

    private const byte EraseGray = 128;

    // Общая обвязка: семафор, заголовок, потолок, декодирование с AutoOrient
    private RasterOutcome Run(byte[] data, bool unpremul, Func<SKBitmap, ImageEncodeFormat?, RasterOutcome> body)
    {
        if (!_gate.Wait(0))
            return RasterOutcome.Fail(RasterError.Busy, "Сервер занят обработкой картинок — повторите чуть позже");
        try
        {
            using var codec = CreateCodec(data);
            if (codec is null)
                return RasterOutcome.Fail(RasterError.Unsupported, "Формат картинки не распознан");
            var info = codec.Info;
            if ((long)info.Width * info.Height > _maxPixels)
                return RasterOutcome.Fail(RasterError.TooLarge,
                    $"Картинка слишком большая: {info.Width}×{info.Height}, потолок — {_maxPixels / 1_000_000} Мп");

            var decoded = Decode(codec, unpremul);
            SKBitmap oriented;
            try
            {
                oriented = Orient(decoded, codec.EncodedOrigin);
            }
            catch
            {
                decoded.Dispose();
                throw;
            }
            if (!ReferenceEquals(oriented, decoded)) decoded.Dispose();
            return body(oriented, ToFormat(codec.EncodedFormat));
        }
        catch (RasterFailure f)
        {
            return RasterOutcome.Fail(f.Error, f.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static SKCodec? CreateCodec(byte[] data)
    {
        if (data.Length == 0) return null;
        // SKCodec держит ссылку на данные: копия живёт, пока жив кодек
        var sk = SKData.CreateCopy(data);
        var codec = SKCodec.Create(sk);
        if (codec is null) sk.Dispose();
        return codec;
    }

    private static SKBitmap Decode(SKCodec codec, bool unpremul)
    {
        var src = codec.Info;
        var alpha = src.AlphaType == SKAlphaType.Opaque
            ? SKAlphaType.Opaque
            : unpremul ? SKAlphaType.Unpremul : SKAlphaType.Premul;
        // Цветовое пространство исходника сохраняем как есть: так профиль доедет до энкодера
        var info = new SKImageInfo(src.Width, src.Height, SKColorType.Rgba8888, alpha, src.ColorSpace);
        var bitmap = new SKBitmap(info);
        var result = codec.GetPixels(info, bitmap.GetPixels());
        if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
        {
            bitmap.Dispose();
            throw new RasterFailure(RasterError.Unsupported, $"Картинку не удалось декодировать: {result}");
        }
        return bitmap;
    }

    // EXIF 1..8 → пиксели в нормальной ориентации. 5 и 7 — транспонирование: поворот плюс отражение
    private static SKBitmap Orient(SKBitmap src, SKEncodedOrigin origin) => origin switch
    {
        SKEncodedOrigin.TopRight => Flip(src, ImageFlipAxis.Horizontal),
        SKEncodedOrigin.BottomRight => Rotate(src, 180),
        SKEncodedOrigin.BottomLeft => Flip(src, ImageFlipAxis.Vertical),
        SKEncodedOrigin.LeftTop => Chain(src, s => Rotate(s, 90), s => Flip(s, ImageFlipAxis.Horizontal)),
        SKEncodedOrigin.RightTop => Rotate(src, 90),
        SKEncodedOrigin.RightBottom => Chain(src, s => Rotate(s, 90), s => Flip(s, ImageFlipAxis.Vertical)),
        SKEncodedOrigin.LeftBottom => Rotate(src, 270),
        _ => src,
    };

    private static SKBitmap Chain(SKBitmap src, Func<SKBitmap, SKBitmap> first, Func<SKBitmap, SKBitmap> second)
    {
        using var middle = first(src);
        return second(middle);
    }

    private static SKBitmap ApplyOp(SKBitmap src, ImageTransformOp op) => op switch
    {
        // AutoOrient уже сделан на входе, повтор — пустая операция
        AutoOrientOp => src,
        CropOp c => Crop(src, c.Rect),
        RotateOp r when r.Degrees is 90 or 180 or 270 => Rotate(src, r.Degrees),
        RotateOp r => throw new RasterFailure(RasterError.InvalidOp, $"Поворот только на 90, 180 или 270°, а не на {r.Degrees}"),
        FlipOp f => Flip(src, f.Axis),
        ResizeOp r => Resize(src, r),
        _ => throw new RasterFailure(RasterError.InvalidOp, $"Неизвестная операция {op.GetType().Name}"),
    };

    private static SKBitmap Rotate(SKBitmap src, int degrees)
    {
        var swap = degrees != 180;
        var (w, h) = swap ? (src.Height, src.Width) : (src.Width, src.Height);
        return Redraw(src, w, h, canvas =>
        {
            switch (degrees)
            {
                case 90: canvas.Translate(w, 0); break;
                case 180: canvas.Translate(w, h); break;
                case 270: canvas.Translate(0, h); break;
            }
            canvas.RotateDegrees(degrees);
        });
    }

    private static SKBitmap Flip(SKBitmap src, ImageFlipAxis axis) =>
        Redraw(src, src.Width, src.Height, canvas =>
        {
            if (axis == ImageFlipAxis.Horizontal) { canvas.Translate(src.Width, 0); canvas.Scale(-1, 1); }
            else { canvas.Translate(0, src.Height); canvas.Scale(1, -1); }
        });

    private static SKBitmap Crop(SKBitmap src, ImageFractionRect rect)
    {
        const double eps = 1e-6;
        if (!double.IsFinite(rect.X) || !double.IsFinite(rect.Y) || !double.IsFinite(rect.Width) || !double.IsFinite(rect.Height)
            || rect.X < -eps || rect.Y < -eps || rect.Width <= 0 || rect.Height <= 0
            || rect.X + rect.Width > 1 + eps || rect.Y + rect.Height > 1 + eps)
            throw new RasterFailure(RasterError.InvalidOp, "Рамка обрезки вне картинки");

        var x0 = Math.Clamp((int)Math.Round(rect.X * src.Width), 0, src.Width);
        var y0 = Math.Clamp((int)Math.Round(rect.Y * src.Height), 0, src.Height);
        var x1 = Math.Clamp((int)Math.Round((rect.X + rect.Width) * src.Width), 0, src.Width);
        var y1 = Math.Clamp((int)Math.Round((rect.Y + rect.Height) * src.Height), 0, src.Height);
        if (x1 <= x0 || y1 <= y0)
            throw new RasterFailure(RasterError.InvalidOp, "Рамка обрезки меньше пикселя");
        return Redraw(src, x1 - x0, y1 - y0, canvas => canvas.Translate(-x0, -y0));
    }

    private static SKBitmap Resize(SKBitmap src, ResizeOp op)
    {
        var (w, h) = TargetSize(src.Width, src.Height, op);
        if (w < 1 || h < 1 || w > MaxSide || h > MaxSide)
            throw new RasterFailure(RasterError.InvalidOp, $"Размер результата {w}×{h} вне 1–{MaxSide} px");
        if (w == src.Width && h == src.Height) return src;

        // Ступени по 2×: Mitchell за один проход при сильном уменьшении даёт алиасинг
        var current = src;
        try
        {
            while (current.Width > 2 * w || current.Height > 2 * h)
            {
                var next = ScaleTo(current, Math.Max(w, (current.Width + 1) / 2), Math.Max(h, (current.Height + 1) / 2));
                if (!ReferenceEquals(current, src)) current.Dispose();
                current = next;
            }
            var result = ScaleTo(current, w, h);
            if (!ReferenceEquals(current, src)) current.Dispose();
            return result;
        }
        catch
        {
            if (!ReferenceEquals(current, src)) current.Dispose();
            throw;
        }
    }

    private static (int W, int H) TargetSize(int w, int h, ResizeOp op)
    {
        if (op.Percent is { } p)
        {
            if (!double.IsFinite(p) || p <= 0)
                throw new RasterFailure(RasterError.InvalidOp, "Процент ресайза должен быть больше нуля");
            return ((int)Math.Round(w * p / 100d), (int)Math.Round(h * p / 100d));
        }
        return (op.Width, op.Height) switch
        {
            ({ } tw, { } th) => (tw, th),
            ({ } tw, null) => (tw, op.LockAspect ? (int)Math.Round((double)h * tw / w) : h),
            (null, { } th) => (op.LockAspect ? (int)Math.Round((double)w * th / h) : w, th),
            _ => throw new RasterFailure(RasterError.InvalidOp, "У ресайза нет ни размеров, ни процента"),
        };
    }

    private static SKBitmap ScaleTo(SKBitmap src, int w, int h)
    {
        var dst = new SKBitmap(src.Info.WithSize(w, h));
        if (!src.ScalePixels(dst, Mitchell))
        {
            dst.Dispose();
            throw new RasterFailure(RasterError.Unsupported, "Не удалось изменить размер");
        }
        return dst;
    }

    // Перерисовка через холст той же конфигурации (тип, альфа, цветовое пространство — без
    // конвертации цвета). Nearest: для поворотов и отражений на целые пиксели он точен
    private static SKBitmap Redraw(SKBitmap src, int w, int h, Action<SKCanvas> transform)
    {
        var dst = new SKBitmap(src.Info.WithSize(w, h));
        using var canvas = new SKCanvas(dst);
        canvas.Clear(SKColors.Transparent);
        transform(canvas);
        using var image = SKImage.FromBitmap(src);
        canvas.DrawImage(image, 0, 0, Nearest);
        canvas.Flush();
        return dst;
    }

    private static RasterOutcome EncodeResult(SKBitmap bitmap, ImageEncodeSpec? spec, ImageEncodeFormat? sourceFormat)
    {
        var format = spec?.Format ?? sourceFormat ?? ImageEncodeFormat.Png;
        var quality = spec?.Quality ?? DefaultQuality;
        if (quality is < MinQuality or > MaxQuality)
            throw new RasterFailure(RasterError.InvalidOp, $"Качество {quality} вне {MinQuality}–{MaxQuality}");
        return Success(bitmap, format, quality);
    }

    private static RasterOutcome Success(SKBitmap bitmap, ImageEncodeFormat format, int quality) =>
        RasterOutcome.Success(new RasterImage(EncodeBitmap(bitmap, format, quality), format, bitmap.Width, bitmap.Height));

    private static byte[] EncodeBitmap(SKBitmap bitmap, ImageEncodeFormat format, int quality)
    {
        // У JPEG нет альфы: прозрачное без подложки стало бы чёрным — кладём на белый
        if (format == ImageEncodeFormat.Jpeg && bitmap.AlphaType != SKAlphaType.Opaque)
        {
            using var flat = Redraw(bitmap, bitmap.Width, bitmap.Height, canvas => canvas.Clear(SKColors.White));
            return EncodePixels(flat, format, quality);
        }
        return EncodePixels(bitmap, format, quality);
    }

    private static byte[] EncodePixels(SKBitmap bitmap, ImageEncodeFormat format, int quality)
    {
        using var pixmap = bitmap.PeekPixels()
            ?? throw new RasterFailure(RasterError.Unsupported, "Пиксели недоступны для кодирования");
        using var data = format switch
        {
            ImageEncodeFormat.Png => pixmap.Encode(new SKPngEncoderOptions(SKPngEncoderFilterFlags.AllFilters, 6)),
            ImageEncodeFormat.Jpeg => pixmap.Encode(new SKJpegEncoderOptions(
                quality, SKJpegEncoderDownsample.Downsample420, SKJpegEncoderAlphaOption.Ignore)),
            ImageEncodeFormat.Webp => pixmap.Encode(new SKWebpEncoderOptions(SKWebpEncoderCompression.Lossy, quality)),
            _ => null,
        } ?? throw new RasterFailure(RasterError.Unsupported, $"Не удалось закодировать в {format}");
        return data.ToArray();
    }

    private static ImageEncodeFormat? ToFormat(SKEncodedImageFormat format) => format switch
    {
        SKEncodedImageFormat.Png => ImageEncodeFormat.Png,
        SKEncodedImageFormat.Jpeg => ImageEncodeFormat.Jpeg,
        SKEncodedImageFormat.Webp => ImageEncodeFormat.Webp,
        _ => null,
    };

    // Внутренний сигнал отказа: ловится в Run и превращается в RasterOutcome, наружу не выходит
    private sealed class RasterFailure(RasterError error, string message) : Exception(message)
    {
        public RasterError Error { get; } = error;
    }
}
