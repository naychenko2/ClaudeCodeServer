using ClaudeHomeServer.Services.ImageEditor;
using ClaudeHomeServer.Services.Images.Editing.Raster;

namespace ClaudeHomeServer.Services.ImageEditor;

// Маска лица на сервере (ADR-028, этап 2): человек тапает на лицо, сервер сам строит эллипс
// по bbox детектора и вычитает bbox всех остальных лиц. Чистая арифметика + один вызов растра
// (IImageRaster из Core — модуль на Images/Skia не ссылается, правило границ вертикалей).
// Ошибки — не исключения, а FaceMaskResult.Error: вызывающий (ручка) переводит их в ответ.
public sealed class FaceMaskBuilder(IImageRaster raster)
{
    // Полуоси эллипса как доли bbox лица (формула разведки spike.py: rw = (x2-x1)*0.75,
    // rh = (y2-y1)*0.8 — это именно полуоси, а не полная ширина: эллипс рисуется
    // [cx-rw, cy-rh, cx+rw, cy+rh]).
    public const double EllipseWidthShare = 0.75;
    public const double EllipseHeightShare = 0.8;
    // Запас по сторонам bbox соседа: зона шва склейки (GrowMask 8 + ImageBlur 15/σ5) —
    // дальше 30 px от края маски изменений нет.
    public const int NeighborMarginPx = 30;
    // Доля площади эллипса, оставшаяся после вычитания соседей: меньше — «лица слишком близко».
    public const double MinRemainingShare = 0.6;

    public const string FaceNotFound = "Такого лица нет на картинке.";
    public const string FacesTooClose = "Лица стоят слишком близко — закрасьте лицо кистью.";
    public const string FaceOutsideImage = "Лицо вне картинки.";

    // Маска по целевому лицу: эллипс по его bbox, вычитание bbox всех остальных лиц
    // (расширенных на NeighborMarginPx). width/height <= 0 — баг вызывающего.
    public FaceMaskResult Build(int width, int height, IReadOnlyList<LocalDetectedFace> faces, int targetN)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "Размер холста должен быть положительным");

        var target = faces.FirstOrDefault(f => f.N == targetN);
        if (target is null) return new FaceMaskResult(null, FaceNotFound, FaceMaskReason.NotFound);

        var (ellipse, excludeRects) = EllipseAndExcludes(target, faces);

        // Контроль остатка: видимая (обрезанная холстом) площадь эллипса S и та же площадь
        // после вычитания соседей S'. Цикл по пикселям описывающего прямоугольника эллипса,
        // обрезанного холстом; без декодирования PNG.
        var (s, sPrime) = VisibleArea(width, height, ellipse, excludeRects);
        if (s == 0) return new FaceMaskResult(null, FaceOutsideImage, FaceMaskReason.OutOfFrame);
        if (sPrime < MinRemainingShare * s) return new FaceMaskResult(null, FacesTooClose, FaceMaskReason.TooClose);

        return new FaceMaskResult(raster.RenderMask(width, height, [ellipse], excludeRects), null);
    }

    // Эллипс по bbox целевого лица и прямоугольники вычитания по bbox всех остальных
    // (расширенные на NeighborMarginPx; целевое лицо из себя не вычитается).
    private static (MaskEllipse Ellipse, List<MaskRect> Excludes) EllipseAndExcludes(
        LocalDetectedFace target, IReadOnlyList<LocalDetectedFace> faces)
    {
        var w = target.X2 - target.X1;
        var h = target.Y2 - target.Y1;
        var cx = (target.X1 + target.X2) / 2.0;
        var cy = (target.Y1 + target.Y2) / 2.0;
        var ellipse = new MaskEllipse(cx, cy, EllipseWidthShare * w, EllipseHeightShare * h);

        var excludes = new List<MaskRect>();
        foreach (var f in faces)
        {
            if (f.N == target.N) continue;
            excludes.Add(new MaskRect(
                f.X1 - NeighborMarginPx, f.Y1 - NeighborMarginPx,
                f.X2 + NeighborMarginPx, f.Y2 + NeighborMarginPx));
        }
        return (ellipse, excludes);
    }

    // (S, S') — площадь видимой (обрезанной холстом) части эллипса и той же части после
    // вычитания прямоугольников. Пиксель (x, y) считается «в эллипсе», если его координата
    // удовлетворяет уравнению эллипса; «вычтен», если попал в любой прямоугольник [X1, X2)×[Y1, Y2).
    // Обход — только по описывающему прямоугольнику эллипса, обрезанному холстом.
    private static (long S, long SPrime) VisibleArea(
        int width, int height, MaskEllipse e, IReadOnlyList<MaskRect> excludeRects)
    {
        if (e.Rx <= 0 || e.Ry <= 0) return (0, 0);

        var left = Math.Max(0, (int)Math.Floor(e.Cx - e.Rx));
        var top = Math.Max(0, (int)Math.Floor(e.Cy - e.Ry));
        var right = Math.Min(width, (int)Math.Ceiling(e.Cx + e.Rx));
        var bottom = Math.Min(height, (int)Math.Ceiling(e.Cy + e.Ry));

        var s = 0L;
        var sPrime = 0L;
        for (var y = top; y < bottom; y++)
        {
            for (var x = left; x < right; x++)
            {
                if (!InsideEllipse(x, y, e)) continue;
                s++;
                if (!Excluded(x, y, excludeRects)) sPrime++;
            }
        }
        return (s, sPrime);
    }

    private static bool InsideEllipse(int x, int y, MaskEllipse e)
    {
        var dx = (x - e.Cx) / e.Rx;
        var dy = (y - e.Cy) / e.Ry;
        return dx * dx + dy * dy <= 1.0;
    }

    private static bool Excluded(int x, int y, IReadOnlyList<MaskRect> excludeRects)
    {
        foreach (var r in excludeRects)
            if (r.X2 > r.X1 && r.Y2 > r.Y1 && x >= r.X1 && x < r.X2 && y >= r.Y1 && y < r.Y2)
                return true;
        return false;
    }
}

// Причина отказа маски: NotFound — такого лица нет, TooClose — лица слишком близко, OutOfFrame — вне картинки
public enum FaceMaskReason { NotFound, TooClose, OutOfFrame }

// Итог построения маски: Png — непрозрачный PNG (чёрный фон, белые эллипсы минус прямоугольники),
// Error — человекочитаемая причина, по которой маску строить не стали (Png = null),
// Reason — машинная причина (для кода ошибки в ответе); null — успех
public sealed record FaceMaskResult(byte[]? Png, string? Error, FaceMaskReason? Reason = null);
