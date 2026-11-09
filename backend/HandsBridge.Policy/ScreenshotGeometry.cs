namespace ClaudeHomeServer.HandsBridge.Policy;

/// <summary>
/// Привязка кадра снимка к экрану: по ней модель переводит пиксель картинки в экранную точку
/// для <c>mouse_control</c>. «Экранный пиксель = начало + пиксель картинки × масштаб».
/// </summary>
/// <param name="OriginX">Экранная координата X левого верхнего угла кадра (у второго монитора может быть отрицательной).</param>
/// <param name="OriginY">Экранная координата Y левого верхнего угла кадра.</param>
/// <param name="ScaleX">Размер снятой области на экране / ширина отданной картинки; 1.0 — без уменьшения.</param>
/// <param name="ScaleY">Высота снятой области на экране / высота отданной картинки.</param>
/// <param name="ImageWidth">Ширина отданной картинки в пикселях.</param>
/// <param name="ImageHeight">Высота отданной картинки в пикселях.</param>
public readonly record struct ScreenshotFrame(
    int OriginX, int OriginY, double ScaleX, double ScaleY, int ImageWidth, int ImageHeight);

/// <summary>Чистый пересчёт координат снимка в экранные — без WinAPI, чтобы тестироваться на Linux.</summary>
public static class ScreenshotGeometry
{
    /// <summary>
    /// Кадр по снятой области и отданной картинке. Масштаб — отношение размера области на экране
    /// (<paramref name="sourceWidth"/> × <paramref name="sourceHeight"/>) к размеру картинки.
    /// </summary>
    public static ScreenshotFrame Create(
        int originX, int originY, int sourceWidth, int sourceHeight, int imageWidth, int imageHeight)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(sourceWidth), "Размер снятой области должен быть положительным.");
        if (imageWidth <= 0 || imageHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(imageWidth), "Размер картинки должен быть положительным.");

        return new ScreenshotFrame(
            originX, originY,
            (double)sourceWidth / imageWidth,
            (double)sourceHeight / imageHeight,
            imageWidth, imageHeight);
    }

    /// <summary>Экранная точка по пикселю картинки: <c>origin + image × scale</c>, округление к ближайшему.</summary>
    public static (int X, int Y) ToScreen(ScreenshotFrame frame, int imageX, int imageY) =>
    (
        frame.OriginX + (int)Math.Round(imageX * frame.ScaleX, MidpointRounding.AwayFromZero),
        frame.OriginY + (int)Math.Round(imageY * frame.ScaleY, MidpointRounding.AwayFromZero)
    );
}
