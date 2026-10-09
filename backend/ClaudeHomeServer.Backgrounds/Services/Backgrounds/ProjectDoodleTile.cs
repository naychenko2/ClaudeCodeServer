using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml;

namespace ClaudeHomeServer.Services.Backgrounds;

/// <summary>Круг фигуры дудла (второй примитив: дугой <c>A</c> модели круг рисуют кляксой).</summary>
public sealed record TileCircle(double Cx, double Cy, double R);

/// <summary>
/// Одна фигура тайла: позиция, поворот и до четырёх путей и кругов. <paramref name="Size"/> —
/// габарит фигуры от её локального нуля: вокруг его середины фигура поворачивается, по нему
/// раскладка держит зазоры и решает, нужна ли копия через стык плиток.
/// </summary>
public sealed record TileShape(
    double X, double Y, double Rotate, IReadOnlyList<string> Paths, IReadOnlyList<TileCircle> Circles,
    double Size = 0);

/// <summary>
/// Итог разбора ответа модели: либо собранный документ, либо причина отказа
/// (<c>bad-json</c> — ответ не разобрался, <c>rejected</c> — годных фигур меньше порога).
/// <paramref name="Shapes"/> — сколько фигур легло в тайл (без копий через стык).
/// </summary>
public sealed record TileBuildResult(string? Svg, string? ColorKey, string? FailReason, int Shapes = 0)
{
    public bool Ok => Svg is not null;
}

/// <summary>
/// Разметки от модели не существует: из ответа берутся только числа и строки путей,
/// документ собирает сервер из константного шаблона (ADR-008 §1, §3, §4). Валидатор
/// отвечает за косметику (границы тайла, разумность чисел) — безопасность держит сам
/// контракт «данные, а не разметка» плюс сборка через <see cref="XmlWriter"/>.
/// </summary>
public static class ProjectDoodleTile
{
    public const int TileSize = 260;
    // Порог годности тайла: полупустой дудл выглядит браком, а брак дороже стандартного фона
    public const int MinShapes = 8;
    public const int MaxShapes = 14;
    public const int MaxPathLength = 512;
    public const int MaxPathsTotalLength = 4096;
    private const int MaxPathsPerShape = 4;
    private const int MaxCirclesPerShape = 4;
    private const int MaxCommandsPerPath = 40;
    // Габарит фигуры от её локального нуля: крупнее — это уже не дудл, а клякса на полтайла
    private const double MaxShapeSize = 60;
    // Самая мелкая фигура всё равно занимает место под зазор, иначе точку зажмёт соседями
    private const double MinShapeSize = 12;
    // Поворот задаёт сервер: модель его держала в ±12°, и ряды выходили ровными
    private const double MaxRotate = 20;
    // Кандидатов на каждую фигуру (best-candidate Митчелла): больше — ровнее зазоры,
    // но и ближе к той самой сетке; 30 даёт россыпь без скучиваний
    private const int PlacementCandidates = 30;

    /// <summary>Девять ключей палитры AGENT_COLORS — единственные принимаемые значения цвета.</summary>
    public static readonly IReadOnlyList<string> ColorKeys =
        ["yellow", "orange", "blue", "green", "purple", "red", "brown", "cyan", "pink"];

    /// <summary>
    /// Разбирает ответ модели и собирает тайл; при отказе <see cref="TileBuildResult.Svg"/> = null.
    /// От модели берутся только сами фигуры: позицию и поворот задаёт <see cref="Place"/>.
    /// </summary>
    /// <param name="random">Источник случайности раскладки; тесты передают с фиксированным seed.</param>
    public static TileBuildResult Build(string? raw, Random? random = null)
    {
        var json = ExtractJsonObject(raw);
        if (json is null) return new TileBuildResult(null, null, "bad-json");

        JsonElement root;
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException) { return new TileBuildResult(null, null, "bad-json"); }
        using (doc)
        {
            root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return new TileBuildResult(null, null, "bad-json");

            var colorKey = ReadColorKey(root);
            if (!root.TryGetProperty("shapes", out var shapesEl) || shapesEl.ValueKind != JsonValueKind.Array)
                return new TileBuildResult(null, colorKey, "bad-json");

            var shapes = new List<TileShape>();
            var totalPathLength = 0;
            foreach (var el in shapesEl.EnumerateArray())
            {
                if (shapes.Count >= MaxShapes) break;   // хвост сверх 14 отбрасывается
                if (!TryReadShape(el, out var shape)) continue;
                var length = shape.Paths.Sum(p => p.Length);
                // Суммарный бюджет путей: хвостовые фигуры сверх него отбрасываются
                if (totalPathLength + length > MaxPathsTotalLength) continue;
                totalPathLength += length;
                shapes.Add(shape);
            }

            return shapes.Count < MinShapes
                ? new TileBuildResult(null, colorKey, "rejected")
                : new TileBuildResult(Render(Place(shapes, random ?? Random.Shared)), colorKey, null, shapes.Count);
        }
    }

    /// <summary>
    /// Раскладка фигур по тайлу. Модель на «рассыпь равномерно» стабильно отвечала сеткой
    /// 4×3, а repeat размножал её в решётку на весь экран — поэтому координаты ставит сервер:
    /// best-candidate Митчелла (из нескольких случайных точек берётся самая далёкая от уже
    /// поставленных) даёт россыпь без сетки и без скучиваний. Расстояния считаются на торе:
    /// тайл повторяется, и соседи через стык плиток — такие же соседи.
    /// </summary>
    public static IReadOnlyList<TileShape> Place(IReadOnlyList<TileShape> shapes, Random random)
    {
        // Крупные ставятся первыми: им труднее найти место среди уже поставленных
        var order = shapes.OrderByDescending(s => s.Size).ToList();
        var placed = new List<(double Cx, double Cy, double R, TileShape Shape)>(order.Count);
        foreach (var shape in order)
        {
            var size = Math.Max(shape.Size, MinShapeSize);
            var r = size / 2;
            double bestX = 0, bestY = 0, bestGap = double.NegativeInfinity;
            var candidates = placed.Count == 0 ? 1 : PlacementCandidates;
            for (var k = 0; k < candidates; k++)
            {
                var x = random.NextDouble() * TileSize;
                var y = random.NextDouble() * TileSize;
                var gap = double.PositiveInfinity;
                foreach (var p in placed)
                    gap = Math.Min(gap, TorusDistance(x, y, p.Cx, p.Cy) - r - p.R);
                if (gap > bestGap) (bestX, bestY, bestGap) = (x, y, gap);
            }
            var rotate = Math.Round((random.NextDouble() * 2 - 1) * MaxRotate, 1);
            // X, Y — локальный ноль фигуры: центр минус половина габарита
            var at = shape with
            {
                X = Math.Round(bestX - shape.Size / 2, 1),
                Y = Math.Round(bestY - shape.Size / 2, 1),
                Rotate = rotate,
            };
            placed.Add((bestX, bestY, r, at));
        }
        return placed.Select(p => p.Shape).ToList();
    }

    private static double TorusDistance(double x1, double y1, double x2, double y2)
    {
        var dx = Math.Abs(x1 - x2);
        var dy = Math.Abs(y1 - y2);
        dx = Math.Min(dx, TileSize - dx);
        dy = Math.Min(dy, TileSize - dy);
        return Math.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>
    /// Сборка документа из уже провалидированных фигур. Только <see cref="XmlWriter"/> —
    /// второй пояс: даже при баге валидатора значение уедет в атрибут экранированным
    /// и не породит нового тега.
    /// </summary>
    public static string Render(IReadOnlyList<TileShape> shapes)
    {
        var sb = new StringBuilder();
        var settings = new XmlWriterSettings
        {
            OmitXmlDeclaration = true,
            Indent = false,
            Encoding = new UTF8Encoding(false),
        };
        using (var w = XmlWriter.Create(sb, settings))
        {
            w.WriteStartElement("svg", "http://www.w3.org/2000/svg");
            w.WriteAttributeString("width", TileSize.ToString(CultureInfo.InvariantCulture));
            w.WriteAttributeString("height", TileSize.ToString(CultureInfo.InvariantCulture));
            w.WriteAttributeString("viewBox", $"0 0 {TileSize} {TileSize}");
            w.WriteAttributeString("fill", "none");
            w.WriteAttributeString("stroke", "#000");
            w.WriteAttributeString("stroke-width", "1.9");
            w.WriteAttributeString("stroke-linecap", "round");
            w.WriteAttributeString("stroke-linejoin", "round");

            foreach (var shape in shapes)
            {
                // Фигура на стыке рисуется ещё раз со сдвигом на тайл с другой стороны:
                // иначе repeat режет её по краю плитки. Охват — описанный круг габарита,
                // он не зависит от поворота
                var c = shape.Size / 2;
                var reach = c * Math.Sqrt(2);
                double[] dxs = Shifts(shape.X + c, reach), dys = Shifts(shape.Y + c, reach);
                foreach (var dx in dxs)
                    foreach (var dy in dys)
                        WriteShape(w, shape, shape.X + dx, shape.Y + dy, c);
            }

            w.WriteEndElement();
        }
        return sb.ToString();
    }

    private static double[] Shifts(double center, double reach) =>
        center - reach < 0 ? [0, TileSize]
        : center + reach > TileSize ? [0, -TileSize]
        : [0];

    private static void WriteShape(XmlWriter w, TileShape shape, double x, double y, double c)
    {
        w.WriteStartElement("g");
        w.WriteAttributeString("transform",
            $"translate({N(x)},{N(y)}) rotate({N(shape.Rotate)},{N(c)},{N(c)})");
        foreach (var d in shape.Paths)
        {
            w.WriteStartElement("path");
            w.WriteAttributeString("d", d);
            w.WriteEndElement();
        }
        foreach (var circle in shape.Circles)
        {
            w.WriteStartElement("circle");
            w.WriteAttributeString("cx", N(circle.Cx));
            w.WriteAttributeString("cy", N(circle.Cy));
            w.WriteAttributeString("r", N(circle.R));
            w.WriteEndElement();
        }
        w.WriteEndElement();
    }

    // Числа — всегда инвариантной культурой: запятая вместо точки в ru-RU молча ломает d
    private static string N(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    private static string? ReadColorKey(JsonElement root)
    {
        if (!root.TryGetProperty("colorKey", out var el) || el.ValueKind != JsonValueKind.String)
            return null;
        var key = el.GetString()?.Trim().ToLowerInvariant();
        // Негодный ключ просто игнорируется — тайл из-за цвета не отбрасываем
        return key is not null && ColorKeys.Contains(key) ? key : null;
    }

    private static bool TryReadShape(JsonElement el, out TileShape shape)
    {
        shape = null!;
        if (el.ValueKind != JsonValueKind.Object) return false;
        // x, y и rotate модели, если она их всё же прислала, не читаются: раскладку делает Place

        var paths = new List<string>();
        var maxLocal = 0.0;
        if (el.TryGetProperty("paths", out var pathsEl) && pathsEl.ValueKind != JsonValueKind.Null)
        {
            if (pathsEl.ValueKind != JsonValueKind.Array) return false;
            foreach (var p in pathsEl.EnumerateArray())
            {
                if (paths.Count >= MaxPathsPerShape) return false;
                if (p.ValueKind != JsonValueKind.String) return false;
                var d = p.GetString() ?? "";
                if (!TryValidatePath(d, out var pathMax)) return false;
                maxLocal = Math.Max(maxLocal, pathMax);
                paths.Add(d);
            }
        }

        var circles = new List<TileCircle>();
        if (el.TryGetProperty("circles", out var circlesEl) && circlesEl.ValueKind != JsonValueKind.Null)
        {
            if (circlesEl.ValueKind != JsonValueKind.Array) return false;
            foreach (var c in circlesEl.EnumerateArray())
            {
                if (circles.Count >= MaxCirclesPerShape) return false;
                if (c.ValueKind != JsonValueKind.Object) return false;
                if (!TryReadNumber(c, "cx", out var cx) || !InRange(cx, -2, 46)) return false;
                if (!TryReadNumber(c, "cy", out var cy) || !InRange(cy, -2, 46)) return false;
                if (!TryReadNumber(c, "r", out var r) || !InRange(r, 1, 30)) return false;
                maxLocal = Math.Max(maxLocal, Math.Max(Math.Abs(cx) + r, Math.Abs(cy) + r));
                circles.Add(new TileCircle(cx, cy, r));
            }
        }

        if (paths.Count + circles.Count == 0) return false;
        // Габарит оценивается сверху: точный bbox требовал бы прогона траектории, а смысл
        // проверки — чтобы одна фигура не съела полтайла и раскладка держала зазоры
        if (maxLocal > MaxShapeSize) return false;

        shape = new TileShape(0, 0, 0, paths, circles, maxLocal);
        return true;
    }

    private static bool TryReadNumber(JsonElement obj, string prop, out double value)
    {
        value = 0;
        return obj.TryGetProperty(prop, out var el)
            && el.ValueKind == JsonValueKind.Number
            && el.TryGetDouble(out value)
            && !double.IsNaN(value) && !double.IsInfinity(value);
    }

    private static bool InRange(double v, double min, double max) => v >= min && v <= max;

    /// <summary>
    /// Проверка строки <c>d</c>: алфавит команд, форма чисел, синтаксис и габарит
    /// (ADR-008 §3). Провал = фигура отбрасывается, не весь тайл.
    /// </summary>
    /// <param name="maxAbs">Максимум модуля чисел строки — грубая оценка габарита сверху.</param>
    public static bool TryValidatePath(string? d, out double maxAbs)
    {
        maxAbs = 0;
        if (string.IsNullOrWhiteSpace(d) || d.Length > MaxPathLength) return false;

        // (1) Алфавит. Экспоненциальная запись запрещена: e/E — единственный дешёвый способ
        // получить координату в миллион парой символов, а дудлам она не нужна
        foreach (var c in d)
            if (!IsCommand(c) && !char.IsAsciiDigit(c) && c is not ('.' or '-' or '+' or ' ' or ',' or '\t' or '\r' or '\n'))
                return false;

        var i = 0;
        while (i < d.Length && IsSeparator(d[i])) i++;
        if (i >= d.Length || (d[i] != 'M' && d[i] != 'm')) return false;

        var command = '\0';
        var args = 0;
        var commands = 0;
        while (i < d.Length)
        {
            if (IsSeparator(d[i])) { i++; continue; }

            if (IsCommand(d[i]))
            {
                if (command != '\0' && !ArityOk(command, args)) return false;
                command = d[i];
                args = 0;
                if (++commands > MaxCommandsPerPath) return false;
                i++;
                continue;
            }

            if (command == '\0' || Arity(command) == 0) return false;   // число до команды либо аргумент у Z
            if (!TryReadPathNumber(d, ref i, out var value)) return false;
            args++;
            maxAbs = Math.Max(maxAbs, Math.Abs(value));
        }
        return command != '\0' && ArityOk(command, args);
    }

    // Форма числа — [+-]?\d{1,3}(\.\d{1,3})?: три знака мантиссы убивают и «длинные хвосты»
    // вида 1.0000000001, и раздувание строки
    private static bool TryReadPathNumber(string d, ref int i, out double value)
    {
        value = 0;
        var start = i;
        if (d[i] is '+' or '-') i++;

        var intDigits = 0;
        while (i < d.Length && char.IsAsciiDigit(d[i])) { i++; intDigits++; }
        if (intDigits is 0 or > 3) return false;

        if (i < d.Length && d[i] == '.')
        {
            i++;
            var frac = 0;
            while (i < d.Length && char.IsAsciiDigit(d[i])) { i++; frac++; }
            if (frac is 0 or > 3) return false;
        }

        return double.TryParse(d.AsSpan(start, i - start), NumberStyles.Float,
            CultureInfo.InvariantCulture, out value);
    }

    private static bool IsSeparator(char c) => c is ' ' or ',' or '\t' or '\r' or '\n';

    private static bool IsCommand(char c) =>
        c is 'M' or 'm' or 'L' or 'l' or 'H' or 'h' or 'V' or 'v' or 'C' or 'c'
            or 'S' or 's' or 'Q' or 'q' or 'T' or 't' or 'A' or 'a' or 'Z' or 'z';

    private static int Arity(char c) => char.ToUpperInvariant(c) switch
    {
        'M' or 'L' or 'T' => 2,
        'H' or 'V' => 1,
        'C' => 6,
        'S' or 'Q' => 4,
        'A' => 7,
        _ => 0,   // Z/z
    };

    // Число аргументов кратно арности команды (повторы допустимы: «M0 0 5 5» = M + L)
    private static bool ArityOk(char command, int args)
    {
        var arity = Arity(command);
        return arity == 0 ? args == 0 : args > 0 && args % arity == 0;
    }

    // Ответ модели может приехать в ```-заборе или с болтовнёй вокруг: берём объект от
    // первой { до парной ей } (приём DocumentAiService.ExtractJsonObject)
    private static string? ExtractJsonObject(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var start = raw.IndexOf('{');
        if (start < 0) return null;
        int depth = 0;
        bool inStr = false, esc = false;
        for (var i = start; i < raw.Length; i++)
        {
            var c = raw[i];
            if (inStr) { if (esc) esc = false; else if (c == '\\') esc = true; else if (c == '"') inStr = false; continue; }
            if (c == '"') inStr = true;
            else if (c == '{') depth++;
            else if (c == '}' && --depth == 0) return raw[start..(i + 1)];
        }
        return null;
    }
}
