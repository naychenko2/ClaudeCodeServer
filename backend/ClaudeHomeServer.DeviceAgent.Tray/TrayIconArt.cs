namespace ClaudeHomeServer.DeviceAgent.Tray;

/// <summary>
/// Силуэт монитора для значка в трёх состояниях — рисуется кодом под любой размер значка
/// (16 px при 100 %, крупнее при масштабе), без файлов-ресурсов. Цвета средней яркости:
/// значок читается и на светлой, и на тёмной панели задач. Сглаживание — 4×4 подвыборки.
/// </summary>
internal static class TrayIconArt
{
    private const uint Accent = 0xD97757;
    private const uint Grey = 0x8A8A8A;
    private const int Samples = 4;

    /// <summary>Пиксели ARGB (незамноженная альфа), строки сверху вниз.</summary>
    public static uint[] Render(TrayIconKind kind, int size)
    {
        var stroke = Math.Max(1.3 / size, 0.1);
        var color = kind == TrayIconKind.Offline ? Grey : Accent;
        var pixels = new uint[size * size];
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            var hits = 0;
            for (var sy = 0; sy < Samples; sy++)
            for (var sx = 0; sx < Samples; sx++)
            {
                var px = (x + (sx + 0.5) / Samples) / size;
                var py = (y + (sy + 0.5) / Samples) / size;
                if (Inside(kind, px, py, stroke)) hits++;
            }
            if (hits == 0) continue;
            var alpha = (uint)(255 * hits / (Samples * Samples));
            pixels[y * size + x] = (alpha << 24) | color;
        }
        return pixels;
    }

    private static bool Inside(TrayIconKind kind, double x, double y, double stroke)
    {
        // Экран, ножка, подставка — в долях стороны значка
        var screen = In(x, y, 0.06, 0.12, 0.94, 0.70);
        var stand = In(x, y, 0.44, 0.70, 0.56, 0.82) || In(x, y, 0.26, 0.82, 0.74, 0.90);
        var frame = screen && !In(x, y, 0.06 + stroke, 0.12 + stroke, 0.94 - stroke, 0.70 - stroke);

        return kind switch
        {
            TrayIconKind.HandsActive => screen || stand,
            TrayIconKind.Offline => frame || stand || NearSlash(x, y, stroke),
            _ => frame || stand,
        };
    }

    private static bool In(double x, double y, double left, double top, double right, double bottom) =>
        x >= left && x < right && y >= top && y < bottom;

    // Косая черта из левого нижнего угла в правый верхний
    private static bool NearSlash(double x, double y, double stroke)
    {
        const double x1 = 0.08, y1 = 0.94, x2 = 0.92, y2 = 0.06;
        var dx = x2 - x1;
        var dy = y2 - y1;
        var t = Math.Clamp(((x - x1) * dx + (y - y1) * dy) / (dx * dx + dy * dy), 0, 1);
        var cx = x1 + t * dx - x;
        var cy = y1 + t * dy - y;
        return Math.Sqrt(cx * cx + cy * cy) <= stroke * 0.6;
    }
}
