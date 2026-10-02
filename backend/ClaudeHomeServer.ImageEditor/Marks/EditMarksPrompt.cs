using System.Globalization;
using System.Text;
using System.Text.Json;

namespace ClaudeHomeServer.Services.ImageEditor;

// Детерминированный перевод структурных пометок marks.json в текст запроса (ADR-017,
// раздел 3). Чистая функция: одинаковый вход — одинаковый текст, поэтому фронт может держать
// свою копию для «Обсудить с Claude» с общим тест-вектором.
//
// Формат marks.json (координаты — доли 0…1 от ширины и высоты исходника):
//   { "marks": [
//       { "type": "rect",  "x": 0.62, "y": 0.10, "w": 0.26, "h": 0.25, "text": "сюда лампу" },
//       { "type": "arrow", "x1": 0.2, "y1": 0.8, "x2": 0.4, "y2": 0.5, "text": "сдвинуть" },
//       { "type": "text",  "x": 0.5, "y": 0.5, "text": "убрать провод" } ] }
// Голый массив вместо объекта тоже читается. Штрихи кисти ({ "type": "mask", "points":
// [[x, y], …], "width": w }) уходят маской, а сюда попадают только рамкой своей области:
// пересекающиеся штрихи сливаются в одну зону. Незнакомые типы и битый JSON молча
// пропускаются: пометки — подсказка модели, а не повод уронить запуск.
public static class EditMarksPrompt
{
    private const int MaxMarks = 20;
    private const int MaxTextLength = 200;

    public static string Describe(string? marksJson, MarksScope scope = MarksScope.All)
    {
        if (string.IsNullOrWhiteSpace(marksJson)) return "";

        JsonElement root;
        try
        {
            root = JsonDocument.Parse(marksJson).RootElement;
        }
        catch (JsonException)
        {
            return "";
        }

        var list = root.ValueKind switch
        {
            JsonValueKind.Array => root,
            JsonValueKind.Object when root.TryGetProperty("marks", out var m) && m.ValueKind == JsonValueKind.Array => m,
            _ => default,
        };
        if (list.ValueKind != JsonValueKind.Array) return "";

        var lines = new List<string>();
        var brush = new List<Box>();
        foreach (var mark in list.EnumerateArray())
        {
            if (mark.ValueKind != JsonValueKind.Object) continue;
            if (Str(mark, "type")?.Trim().ToLowerInvariant() is "mask" or "brush")
            {
                if (scope != MarksScope.WithoutBrush && StrokeBox(mark) is { } box) brush.Add(box);
                continue;
            }
            if (scope == MarksScope.BrushOnly || lines.Count >= MaxMarks) continue;
            var line = DescribeMark(mark);
            if (line is not null) lines.Add(line);
        }
        lines.InsertRange(0, Merge(brush).Take(MaxMarks).Select(b =>
            $"выделено кистью {Area(b.CenterX, b.CenterY)} (x {Pct(b.X1)}–{Pct(b.X2)} %, y {Pct(b.Y1)}–{Pct(b.Y2)} %)"));
        if (lines.Count == 0) return "";

        var sb = new StringBuilder("Пометки на картинке (координаты — проценты от ширины и высоты):");
        foreach (var line in lines) sb.Append("\n- ").Append(line);
        return sb.ToString();
    }

    private static string? DescribeMark(JsonElement mark)
    {
        var type = Str(mark, "type")?.Trim().ToLowerInvariant();
        var text = Label(mark);
        switch (type)
        {
            case "rect" or "box" or "frame":
            {
                if (Num(mark, "x") is not { } x || Num(mark, "y") is not { } y) return null;
                var w = Num(mark, "w") ?? Num(mark, "width") ?? 0;
                var h = Num(mark, "h") ?? Num(mark, "height") ?? 0;
                var (x1, x2) = Order(x, x + w);
                var (y1, y2) = Order(y, y + h);
                var head = $"рамка {Area((x1 + x2) / 2, (y1 + y2) / 2)} (x {Pct(x1)}–{Pct(x2)} %, y {Pct(y1)}–{Pct(y2)} %)";
                return text is null ? $"{head}: менять здесь" : $"{head}: {text}";
            }
            case "arrow":
            {
                if (Num(mark, "x1") is not { } x1 || Num(mark, "y1") is not { } y1
                    || Num(mark, "x2") is not { } x2 || Num(mark, "y2") is not { } y2)
                    return null;
                var head = $"стрелка от (x {Pct(x1)} %, y {Pct(y1)} %) к (x {Pct(x2)} %, y {Pct(y2)} %), {Area(x2, y2)}";
                return text is null ? head : $"{head}: {text}";
            }
            case "text" or "label":
            {
                if (text is null || Num(mark, "x") is not { } x || Num(mark, "y") is not { } y) return null;
                return $"подпись {Area(x, y)} (x {Pct(x)} %, y {Pct(y)} %): «{text}»";
            }
            default:
                return null;
        }
    }

    private readonly record struct Box(double X1, double Y1, double X2, double Y2)
    {
        public double CenterX => (X1 + X2) / 2;
        public double CenterY => (Y1 + Y2) / 2;
        public bool Overlaps(Box o) => X1 <= o.X2 && o.X1 <= X2 && Y1 <= o.Y2 && o.Y1 <= Y2;
        public Box Union(Box o) => new(Math.Min(X1, o.X1), Math.Min(Y1, o.Y1), Math.Max(X2, o.X2), Math.Max(Y2, o.Y2));
    }

    // Рамка штриха с учётом толщины кисти (width — доля ширины картинки)
    private static Box? StrokeBox(JsonElement mark)
    {
        if (!mark.TryGetProperty("points", out var pts) || pts.ValueKind != JsonValueKind.Array) return null;
        double x1 = 1, y1 = 1, x2 = 0, y2 = 0;
        var any = false;
        foreach (var p in pts.EnumerateArray())
        {
            if (p.ValueKind != JsonValueKind.Array || p.GetArrayLength() < 2) continue;
            if (p[0].ValueKind != JsonValueKind.Number || p[1].ValueKind != JsonValueKind.Number) continue;
            double x = Clamp(p[0].GetDouble()), y = Clamp(p[1].GetDouble());
            x1 = Math.Min(x1, x); x2 = Math.Max(x2, x);
            y1 = Math.Min(y1, y); y2 = Math.Max(y2, y);
            any = true;
        }
        if (!any) return null;
        var r = Clamp(Num(mark, "width") ?? 0) / 2;
        return new Box(Clamp(x1 - r), Clamp(y1 - r), Clamp(x2 + r), Clamp(y2 + r));
    }

    // Пересекающиеся рамки сливаются, пока есть что сливать; порядок — по первому штриху
    private static List<Box> Merge(List<Box> boxes)
    {
        var result = new List<Box>(boxes);
        for (var merged = true; merged;)
        {
            merged = false;
            for (var i = 0; i < result.Count && !merged; i++)
                for (var j = i + 1; j < result.Count; j++)
                {
                    if (!result[i].Overlaps(result[j])) continue;
                    result[i] = result[i].Union(result[j]);
                    result.RemoveAt(j);
                    merged = true;
                    break;
                }
        }
        return result;
    }

    // Часть картинки словами: сетка 3×3
    private static string Area(double x, double y)
    {
        var v = Clamp(y) switch { < 1.0 / 3 => "вверху", < 2.0 / 3 => "посередине", _ => "внизу" };
        var h = Clamp(x) switch { < 1.0 / 3 => "слева", < 2.0 / 3 => "по центру", _ => "справа" };
        return v == "посередине" && h == "по центру" ? "в центре" : $"{v} {h}";
    }

    private static string Pct(double v) =>
        Math.Round(Clamp(v) * 100).ToString(CultureInfo.InvariantCulture);

    private static double Clamp(double v) => double.IsFinite(v) ? Math.Clamp(v, 0, 1) : 0;

    private static (double, double) Order(double a, double b) => a <= b ? (a, b) : (b, a);

    private static string? Label(JsonElement mark)
    {
        var text = (Str(mark, "text") ?? Str(mark, "label"))?.Trim();
        if (string.IsNullOrEmpty(text)) return null;
        text = text.Replace('\n', ' ').Replace('\r', ' ');
        return text.Length > MaxTextLength ? text[..MaxTextLength] : text;
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double? Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d)
            ? d
            : null;
}

// Какие пометки описывать: при правке в два запроса кисть уходит в первый, остальное — во второй
public enum MarksScope { All, BrushOnly, WithoutBrush }
