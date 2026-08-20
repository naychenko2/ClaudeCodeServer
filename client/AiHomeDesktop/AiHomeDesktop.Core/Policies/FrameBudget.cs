using AiHomeDesktop.Core.Protocol;

namespace AiHomeDesktop.Core.Policies;

/// <summary>
/// Бюджет кадра — правило протокола, а не оптимизация (ADR-008, «Протокол канала»).
/// 8 МБ — потолок HTTP-тела, а НЕ лимит изображения: один полноэкранный 4K-кадр кирпичит
/// чат навсегда, потому что уезжает в транскрипт и приезжает обратно каждым --resume.
///
/// Отсюда три числа: максимальная сторона, формат с качеством и потолок кадров в одном
/// результате (1–2, не 10).
/// </summary>
/// <param name="MaxSide">Максимальная сторона кадра в пикселях после масштабирования.</param>
/// <param name="MimeType">Формат кодирования кадра.</param>
/// <param name="Quality">Качество JPEG (для PNG не применяется).</param>
/// <param name="MaxFrames">Сколько кадров максимум едет в одном результате.</param>
/// <param name="MaxFrameBytes">Потолок закодированного кадра ДО base64.</param>
public sealed record FrameBudget(
    int MaxSide = 1600,
    string MimeType = "image/jpeg",
    int Quality = 78,
    int MaxFrames = 2,
    int MaxFrameBytes = 1_200_000)
{
    /// <summary>Бюджет по умолчанию: 1600 px по большей стороне, JPEG, максимум два кадра.</summary>
    public static readonly FrameBudget Default = new();

    /// <summary>
    /// Целевой размер кадра: пропорции сохраняются, увеличение не делается никогда —
    /// маленькое окно уезжает как есть.
    /// </summary>
    public (int Width, int Height) Scale(int width, int height)
    {
        if (width <= 0 || height <= 0) return (0, 0);

        var side = Math.Max(width, height);
        if (side <= MaxSide) return (width, height);

        var ratio = (double)MaxSide / side;
        return (Math.Max(1, (int)Math.Round(width * ratio)), Math.Max(1, (int)Math.Round(height * ratio)));
    }

    /// <summary>Уложился ли закодированный кадр в бюджет тела.</summary>
    public bool Fits(int encodedBytes) => encodedBytes <= MaxFrameBytes;

    /// <summary>
    /// Правило вытеснения: в результат едут ПОСЛЕДНИЕ кадры — при съёмке нескольких
    /// интересен свежий экран, а не тот, что был в начале вызова.
    /// </summary>
    public IReadOnlyList<T> Trim<T>(IReadOnlyList<T> frames) =>
        frames.Count <= MaxFrames ? frames : frames.Skip(frames.Count - MaxFrames).ToList();

    /// <summary>
    /// Влезет ли набор кадров в тело результата: base64 раздувает данные на треть, и
    /// потолок HTTP считается уже по нему.
    /// </summary>
    public bool FitsBody(IEnumerable<int> encodedSizes)
    {
        var total = encodedSizes.Sum(size => (long)Base64Length(size));
        return total <= DesktopProtocol.MaxResultBytes - ResultOverheadBytes;
    }

    /// <summary>Запас под шапку результата (исход, текст, метаданные кадра).</summary>
    private const int ResultOverheadBytes = 64 * 1024;

    private static int Base64Length(int bytes) => (bytes + 2) / 3 * 4;
}
