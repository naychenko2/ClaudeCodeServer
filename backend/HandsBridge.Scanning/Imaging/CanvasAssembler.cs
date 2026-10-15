namespace ClaudeHomeServer.HandsBridge.Scanning.Imaging;

/// <summary>
/// Плитка на холсте: пиксели кадра (RGB/RGBA, row-major) и её позиция на холсте
/// (<see cref="CanvasX"/>/<see cref="CanvasY"/> = накопленные ИЗМЕРЕННЫЕ сдвиги, не номинальные),
/// плюс метаданные сдвига для детекции сомнительных швов: <see cref="MeasuredShift"/> — измеренный
/// сдвиг (из <c>ShiftEstimator</c>), <see cref="ExpectedShift"/> — ожидаемый, <see cref="Score"/> —
/// NCC, <see cref="Ambiguous"/> — признак повторяющегося узора.
/// </summary>
public readonly record struct Tile(
    byte[] Pixels,
    int Width,
    int Height,
    int CanvasX,
    int CanvasY,
    int MeasuredShift,
    int ExpectedShift,
    double Score,
    bool Ambiguous);

/// <summary>
/// Сомнительный шов между двумя соседними плитками: <see cref="Reason"/> — причина (повторяющийся
/// узор / низкая корреляция / расхождение измеренного и ожидаемого сдвига),
/// <see cref="TileA"/>/<see cref="TileB"/> — индексы плиток в сетке (row × cols + col).
/// </summary>
public readonly record struct DoubtfulSeam(string Reason, int TileA, int TileB);

/// <summary>
/// Результат сборки: <see cref="Pixels"/> — холст (RGB/RGBA, row-major), <see cref="Width"/>/
/// <see cref="Height"/> — размер холста, <see cref="Rows"/>/<see cref="Columns"/> — сетка
/// (число строк/столбцов плиток), <see cref="DoubtfulSeams"/> — список сомнительных швов.
/// </summary>
public readonly record struct CanvasResult(
    byte[] Pixels,
    int Width,
    int Height,
    int Rows,
    int Columns,
    IReadOnlyList<DoubtfulSeam> DoubtfulSeams);

/// <summary>
/// Сборка плиток в общий холст по ИЗМЕРЕННЫМ сдвигам (не номинальным): координаты плитки =
/// накопленные измеренные сдвиги (их передаёт планировщик/инструмент в <see cref="Tile.CanvasX"/>/
/// <see cref="Tile.CanvasY"/>). Перекрытия сшиваются: на стыке предпочтение — верхняя/левая
/// плитка (первая запись выигрывает), без размытия (жёсткий шов). Возвращает холст (RGB/RGBA),
/// размер, сетку (строки/столбцы) и список сомнительных швов (Ambiguous из ShiftEstimator, низкий
/// Score, расхождение измеренного и ожидаемого сдвига &gt; допуска).
/// </summary>
public static class CanvasAssembler
{
    /// <summary>Допуск расхождения измеренного и ожидаемого сдвига, px (по умолчанию 16).</summary>
    public const int DefaultShiftTolerance = 16;

    /// <summary>Порог «низкого» Score (NCC) для сомнительного шва (по умолчанию 0.5).</summary>
    public const double DefaultMinScore = 0.5;

    /// <summary>
    /// Собирает плитки в холст. <paramref name="tiles"/> — плитки с позициями (накопленные
    /// измеренные сдвиги); <paramref name="shiftTolerance"/> — допуск расхождения сдвига,
    /// <paramref name="minScore"/> — порог низкого Score. Пиксели всех плиток обязаны быть
    /// одного типа (все RGB или все RGBA).
    /// </summary>
    public static CanvasResult Assemble(
        IReadOnlyList<Tile> tiles,
        int shiftTolerance = DefaultShiftTolerance,
        double minScore = DefaultMinScore)
    {
        if (tiles is null)
            throw new ArgumentNullException(nameof(tiles));

        if (tiles.Count == 0)
            throw new ArgumentException("Нужна хотя бы одна плитка.", nameof(tiles));

        // Каналы: все плитки одного типа (3 или 4).
        var channels = tiles[0].Pixels.Length / ((long)tiles[0].Width * tiles[0].Height);
        if (channels != 3 && channels != 4)
            throw new ArgumentException(
                $"Первая плитка: {channels} каналов, ожидается 3 (RGB) или 4 (RGBA).",
                nameof(tiles));

        foreach (var t in tiles)
        {
            var ch = t.Pixels.Length / ((long)t.Width * t.Height);
            if (ch != channels)
                throw new ArgumentException(
                    $"Плитка в ({t.CanvasX}, {t.CanvasY}) имеет {ch} каналов, ожидается {channels}.",
                    nameof(tiles));
        }

        // Размер холста = обёртка всех плиток.
        var maxRight = 0;
        var maxBottom = 0;
        foreach (var t in tiles)
        {
            maxRight = Math.Max(maxRight, t.CanvasX + t.Width);
            maxBottom = Math.Max(maxBottom, t.CanvasY + t.Height);
        }

        var canvas = new byte[maxRight * maxBottom * channels];

        // «Первая запись выигрывает»: раскладываем от верхнего-левого к нижнему-правому, и каждая
        // плитка пишет только в ещё пустые пиксели. В перекрытии выигрывает более верхняя/левая.
        var order = tiles
            .Select((t, i) => (t, i))
            .OrderBy(p => p.t.CanvasY)
            .ThenBy(p => p.t.CanvasX)
            .ToArray();

        var filled = new bool[maxRight * maxBottom];
        foreach (var (t, _) in order)
        {
            for (var y = 0; y < t.Height; y++)
            {
                var cy = t.CanvasY + y;
                for (var x = 0; x < t.Width; x++)
                {
                    var cx = t.CanvasX + x;
                    var pixelIdx = cy * maxRight + cx;
                    if (filled[pixelIdx])
                        continue; // уже заполнено более верхней/левой плиткой

                    var dst = pixelIdx * channels;
                    var src = (y * t.Width + x) * channels;
                    for (var c = 0; c < channels; c++)
                        canvas[dst + c] = t.Pixels[src + c];

                    filled[pixelIdx] = true;
                }
            }
        }

        // Сетка: число строк/столбцов по уникальным координатам.
        var rows = tiles.Select(t => t.CanvasY).Distinct().Count();
        var cols = tiles.Select(t => t.CanvasX).Distinct().Count();

        // Сомнительные швы: для каждой пары соседних плиток (по позиции) проверяем метаданные.
        var seams = FindDoubtfulSeams(tiles, shiftTolerance, minScore);

        return new CanvasResult(canvas, maxRight, maxBottom, rows, cols, seams);
    }

    /// <summary>
    /// Ищет сомнительные швы между соседними плитками. Соседство — по позиции: плитки, чьи
    /// прямоугольники на холсте перекрываются или граничат. Для каждой такой пары проверяем:
    /// Ambiguous, низкий Score, расхождение |измеренный − ожидаемый| &gt; допуска.
    /// </summary>
    private static IReadOnlyList<DoubtfulSeam> FindDoubtfulSeams(
        IReadOnlyList<Tile> tiles,
        int shiftTolerance,
        double minScore)
    {
        var seams = new List<DoubtfulSeam>();
        var n = tiles.Count;
        for (var i = 0; i < n; i++)
        {
            for (var j = i + 1; j < n; j++)
            {
                if (!AreAdjacent(tiles[i], tiles[j]))
                    continue;

                var reason = SeamReason(tiles[i], tiles[j], shiftTolerance, minScore)
                    ?? SeamReason(tiles[j], tiles[i], shiftTolerance, minScore);
                if (reason is not null)
                    seams.Add(new DoubtfulSeam(reason, i, j));
            }
        }

        return seams;
    }

    /// <summary>
    /// Причина сомнительности шва (null — шов надёжен). Проверяем плитку <paramref name="a"/>:
    /// Ambiguous, низкий Score, расхождение сдвига.
    /// </summary>
    private static string? SeamReason(Tile a, Tile b, int shiftTolerance, double minScore)
    {
        if (a.Ambiguous)
            return "Повторяющийся узор (Ambiguous)";
        if (a.Score < minScore)
            return $"Низкая корреляция (Score {a.Score:0.00} < {minScore:0.00})";
        if (Math.Abs(a.MeasuredShift - a.ExpectedShift) > shiftTolerance)
            return $"Расхождение сдвига: измерен {a.MeasuredShift}, ожидаем {a.ExpectedShift}";

        return null;
    }

    /// <summary>
    /// Соседние ли плитки (для шва): на одной строке и с перекрывающимися по X диапазонами,
    /// ИЛИ на одном столбце и с перекрывающимися по Y диапазонами. Диагональные плитки (разные
    /// строка и столбец) швом не считаются, даже если их углы перекрываются.
    /// </summary>
    private static bool AreAdjacent(Tile a, Tile b)
    {
        // Одна строка (y совпадает) и диапазоны X пересекаются.
        if (a.CanvasY == b.CanvasY)
        {
            var xOverlap = Math.Min(a.CanvasX + a.Width, b.CanvasX + b.Width)
                            > Math.Max(a.CanvasX, b.CanvasX);
            if (xOverlap)
                return true;
        }

        // Один столбец (x совпадает) и диапазоны Y пересекаются.
        if (a.CanvasX == b.CanvasX)
        {
            var yOverlap = Math.Min(a.CanvasY + a.Height, b.CanvasY + b.Height)
                            > Math.Max(a.CanvasY, b.CanvasY);
            if (yOverlap)
                return true;
        }

        return false;
    }
}
