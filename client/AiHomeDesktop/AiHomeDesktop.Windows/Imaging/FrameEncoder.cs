using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using AiHomeDesktop.Core.Policies;

namespace AiHomeDesktop.Windows.Imaging;

/// <summary>Закодированный кадр — ровно то, что уезжает в результат вызова.</summary>
/// <param name="Data">Байты изображения (без base64).</param>
/// <param name="MimeType">Формат.</param>
/// <param name="Width">Ширина после масштабирования.</param>
/// <param name="Height">Высота после масштабирования.</param>
/// <param name="Scaled">Кадр уменьшен относительно оригинала.</param>
/// <param name="SourceWidth">Исходная ширина — по ней модель понимает, что видит уменьшённое.</param>
/// <param name="SourceHeight">Исходная высота.</param>
public sealed record EncodedFrame(
    byte[] Data,
    string MimeType,
    int Width,
    int Height,
    bool Scaled,
    int SourceWidth,
    int SourceHeight);

/// <summary>
/// Масштабирование и сжатие кадра НА КЛИЕНТЕ по бюджету протокола (ADR-008, «Протокол
/// канала»). 8 МБ — потолок HTTP-тела, а не лимит изображения: полноэкранный 4K-кадр
/// оседает в транскрипте CLI и приезжает обратно каждым --resume, то есть кирпичит чат
/// навсегда. Поэтому кадр режется до бюджета ЗДЕСЬ, а не «как-нибудь на сервере».
/// </summary>
public static class FrameEncoder
{
    /// <summary>Ступени качества JPEG, которыми ужимаемся, прежде чем терять пиксели.</summary>
    private static readonly int[] QualitySteps = [0, -18, -33];

    /// <summary>Ниже этой стороны уменьшать бессмысленно — текст на кадре уже не читается.</summary>
    private const int MinSide = 480;

    public static EncodedFrame Encode(Bitmap source, FrameBudget budget)
    {
        var (targetWidth, targetHeight) = budget.Scale(source.Width, source.Height);
        var scaled = targetWidth != source.Width || targetHeight != source.Height;

        var width = targetWidth;
        var height = targetHeight;

        for (var attempt = 0; ; attempt++)
        {
            using var frame = Resize(source, width, height);
            var quality = budget.Quality + QualitySteps[Math.Min(attempt, QualitySteps.Length - 1)];
            var data = ToBytes(frame, budget.MimeType, Math.Clamp(quality, 30, 100));

            var exhausted = attempt >= QualitySteps.Length - 1 && Math.Max(width, height) <= MinSide;
            if (budget.Fits(data.Length) || exhausted)
                return new EncodedFrame(data, budget.MimeType, frame.Width, frame.Height,
                    scaled || frame.Width != source.Width, source.Width, source.Height);

            // Качество кончилось — уменьшаем сторону: лучше меньший кадр, чем кадр, который
            // не влезет в тело результата.
            if (attempt >= QualitySteps.Length - 1)
            {
                width = Math.Max(MinSide, (int)(width * 0.8));
                height = Math.Max(1, (int)Math.Round(height * (double)width / Math.Max(1, frame.Width)));
                scaled = true;
            }
        }
    }

    private static Bitmap Resize(Bitmap source, int width, int height)
    {
        if (width == source.Width && height == source.Height)
            return new Bitmap(source);

        var target = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using var graphics = Graphics.FromImage(target);
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.SmoothingMode = SmoothingMode.HighQuality;
        graphics.DrawImage(source, 0, 0, width, height);
        return target;
    }

    private static byte[] ToBytes(Bitmap frame, string mimeType, int quality)
    {
        using var stream = new MemoryStream();
        if (string.Equals(mimeType, "image/png", StringComparison.OrdinalIgnoreCase))
        {
            frame.Save(stream, ImageFormat.Png);
            return stream.ToArray();
        }

        var codec = ImageCodecInfo.GetImageEncoders()
            .FirstOrDefault(c => string.Equals(c.MimeType, mimeType, StringComparison.OrdinalIgnoreCase));
        if (codec is null)
        {
            frame.Save(stream, ImageFormat.Png);
            return stream.ToArray();
        }

        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(Encoder.Quality, (long)quality);
        frame.Save(stream, codec, parameters);
        return stream.ToArray();
    }
}
