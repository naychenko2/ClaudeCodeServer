namespace ClaudeHomeServer.HandsBridge.Scanning.Pixels;

/// <summary>
/// Прямоугольник в пиксельных координатах: левый верхний угол <c>(X, Y)</c> и размер
/// <c>(Width, Height)</c>. Используется для области содержимого окна и позиций плиток на
/// холсте. Координаты — целые, границы включительные.
/// </summary>
public readonly record struct Rect(int X, int Y, int Width, int Height)
{
    /// <summary>Координата правого края (включительно).</summary>
    public int Right => X + Width - 1;

    /// <summary>Координата нижнего края (включительно).</summary>
    public int Bottom => Y + Height - 1;

    /// <summary>Площадь в пикселях.</summary>
    public long Area => (long)Width * Height;

    /// <summary>Прямоугольник нулевого размера (пустая область).</summary>
    public static Rect Empty { get; } = new(0, 0, 0, 0);

    /// <summary>Содержит ли точка <c>(x, y)</c> (границы включены).</summary>
    public bool Contains(int x, int y) => x >= X && x <= Right && y >= Y && y <= Bottom;
}
