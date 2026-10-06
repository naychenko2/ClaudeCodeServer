using ClaudeHomeServer.Services.ImageEditor;

namespace ClaudeHomeServer.Services.Media;

// Якорь рамки обрезки: по какой стороне прижимается обрезаемая рамка. left/center/right — по
// горизонтали (когда обрезаются бока), top/center/bottom — по вертикали (когда верх и низ)
public static class CropAnchors
{
    public const string Left = "left";
    public const string Center = "center";
    public const string Right = "right";
    public const string Top = "top";
    public const string Bottom = "bottom";

    public static readonly IReadOnlySet<string> All =
        new HashSet<string>(StringComparer.Ordinal) { Left, Center, Right, Top, Bottom };
}

// Прямоугольник обрезки под пропорции — единственная точка расчёта для человека (ручка «Обрезать»),
// агента (image_generate op=crop) и подрезки кадров видео (ADR-025 §4–5)
public static class AspectCrop
{
    // Доли исходника (0..1) под «W:H» с якорем. Пропорция уже совпадает — вся картинка.
    // null — пропорция или якорь не читаются, либо размер не положительный
    public static ImageFractionRect? Rect(int width, int height, string? aspect, string? anchor)
    {
        if (width < 1 || height < 1 || !TryParse(aspect, out var target)) return null;
        var a = string.IsNullOrWhiteSpace(anchor) ? CropAnchors.Center : anchor.Trim();
        if (!CropAnchors.All.Contains(a)) return null;

        var current = (double)width / height;
        if (Math.Abs(current / target - 1) < 1e-6) return new ImageFractionRect(0, 0, 1, 1);

        if (current > target)
        {
            // Шире нужного — режем бока
            var w = target / current;
            var x = a switch { CropAnchors.Left => 0d, CropAnchors.Right => 1 - w, _ => (1 - w) / 2 };
            return new ImageFractionRect(x, 0, w, 1);
        }
        var h = current / target;
        var y = a switch { CropAnchors.Top => 0d, CropAnchors.Bottom => 1 - h, _ => (1 - h) / 2 };
        return new ImageFractionRect(0, y, 1, h);
    }

    // Размер результата в пикселях для того же прямоугольника
    public static FormatSize? ResultSize(int width, int height, string? aspect, string? anchor)
    {
        var r = Rect(width, height, aspect, anchor);
        return r is null
            ? null
            : new FormatSize(Math.Max(1, (int)Math.Round(width * r.Width)), Math.Max(1, (int)Math.Round(height * r.Height)));
    }

    // «W:H» → отношение; на плохую запись false
    public static bool TryParse(string? aspect, out double ratio)
    {
        ratio = 0;
        var parts = aspect?.Split(':');
        if (parts is not { Length: 2 }
            || !double.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var w)
            || !double.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var h)
            || !double.IsFinite(w) || !double.IsFinite(h) || w <= 0 || h <= 0)
            return false;
        ratio = w / h;
        return true;
    }
}
