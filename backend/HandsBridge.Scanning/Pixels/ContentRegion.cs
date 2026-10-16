namespace ClaudeHomeServer.HandsBridge.Scanning.Pixels;

/// <summary>
/// Результат определения области содержимого: <see cref="Ok"/> — область определена,
/// <see cref="Region"/> — прямоугольник изменившихся пикселей (то, что реально прокручивается),
/// <see cref="Confidence"/> — достоверность 0..1 (плотность изменившихся пикселей внутри
/// прямоугольника: чем плотнее, тем увереннее, что это содержимое, а не шум),
/// <see cref="Reason"/> — причина отказа (null при успехе).
/// </summary>
public readonly record struct ContentRegionResult(bool Ok, Rect Region, double Confidence, string? Reason);

/// <summary>
/// Определение области содержимого окна, которая реально прокручивается: разница кадра
/// до/после известного сдвига — прямоугольник изменившихся пикселей, а рамка окна, линейки,
/// панели и полосы прокрутки (недвигущиеся) исключаются. Чистая функция над серыми кадрами
/// (0..255, row-major) — без Windows-зависимостей, чтобы тесты шли на Linux.
///
/// Достоверность — плотность изменившихся пикселей внутри прямоугольника: настоящая область
/// содержимого после прокрутки меняется почти целиком (плотность близка к 1), а часы,
/// мигающий курсор или малый движущийся элемент дают низкую плотность. Если изменившихся
/// пикселей нет вовсе или плотность ниже порога — понятный отказ с причиной, а не «успех»
/// с мусорным прямоугольником.
/// </summary>
public static class ContentRegion
{
    /// <summary>
    /// Определяет область содержимого по паре кадров до/после известного сдвига.
    /// <paramref name="grayBefore"/>/<paramref name="grayAfter"/> — серые кадры (row-major,
    /// 0..255) одного размера <c>width × height</c>; <paramref name="tolerance"/> — порог
    /// разницы серого (0..255): пиксель с разницей больше порога считается изменившимся
    /// (по умолчанию 8 — выше шума захвата, ниже реального изменения содержимого);
    /// <paramref name="minDensity"/> — минимальная плотность изменившихся пикселей внутри
    /// прямоугольника (0..1, по умолчанию 0.05): ниже неё «область» считается шумом;
    /// <paramref name="minRegionFraction"/> — минимальная доля кадра, которую должна покрывать
    /// область (0..1, по умолчанию 0.01): крошечное пятно (один-два пикселя) — шум, а не
    /// содержимое, даже если внутри него плотность 100 %.
    /// </summary>
    public static ContentRegionResult Determine(
        ReadOnlySpan<byte> grayBefore,
        ReadOnlySpan<byte> grayAfter,
        int width,
        int height,
        double tolerance = 8.0,
        double minDensity = 0.05,
        double minRegionFraction = 0.01)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentException("Ширина и высота должны быть положительными.", nameof(width));

        var expected = (long)width * height;
        if (grayBefore.Length != expected || grayAfter.Length != expected)
            throw new ArgumentException(
                $"Кадры {grayBefore.Length} и {grayAfter.Length} пикселей, а ожидается {expected} (width × height).",
                nameof(grayBefore));

        // M4: не min/max по любым изменившимся пикселям (раздувается выбросами — ползунок полосы
        // прокрутки, часы, курсор в углу), а проекции: доля изменившихся пикселей в каждой строке
        // и каждом столбце. «Полка» — самый длинный непрерывный участок строк/столбцов, где доля
        // выше порога; область = пересечение полок по X и Y.
        var rowChanged = new int[height];
        var colChanged = new int[width];
        var changed = 0;

        for (var i = 0; i < grayBefore.Length; i++)
        {
            var diff = Math.Abs((int)grayBefore[i] - grayAfter[i]);
            if (diff <= tolerance)
                continue;

            changed++;
            var x = i % width;
            var y = i / width;
            rowChanged[y]++;
            colChanged[x]++;
        }

        if (changed == 0)
            return new ContentRegionResult(false, Rect.Empty, 0.0,
                "Кадры совпадают в пределах допуска: содержимое не изменилось, область не определена.");

        // Порог «полки»: строка/столбец считается частью содержимого, если изменилась не меньше
        // этой доли его пикселей. Выброс (ползунок, курсор, часы) даёт долю ~0.005 в своей
        // строке/столбце; плотное содержимое — от ~0.45 (узкий блок) до 1.0. Порог 0.1 разделяет.
        const double plateauThreshold = 0.1;

        // Самая длинная непрерывная «полка» строк (по Y) и столбцов (по X).
        var (rowStart, rowEnd) = LongestActiveRun(rowChanged, height, width, plateauThreshold);
        var (colStart, colEnd) = LongestActiveRun(colChanged, width, height, plateauThreshold);

        if (rowStart < 0 || colStart < 0)
            return new ContentRegionResult(false, Rect.Empty, 0.0,
                "Не найдена непрерывная «полка» изменившихся строк/столбцов: " +
                "изменения разрознены (шум/выбросы), а не сплошное содержимое.");

        var region = new Rect(colStart, rowStart, colEnd - colStart + 1, rowEnd - rowStart + 1);

        // Плотность внутри найденной области (вторая защита от выбросов, прошедших полку).
        var changedInRegion = 0;
        for (var y = rowStart; y <= rowEnd; y++)
            for (var x = colStart; x <= colEnd; x++)
            {
                var diff = Math.Abs((int)grayBefore[y * width + x] - grayAfter[y * width + x]);
                if (diff > tolerance)
                    changedInRegion++;
            }

        var density = (double)changedInRegion / region.Area;
        var regionFraction = (double)region.Area / ((long)width * height);

        // Крошечная область (доля кадра ниже порога) — шум, даже если внутри плотность 100 %.
        if (regionFraction < minRegionFraction)
            return new ContentRegionResult(false, region, density,
                $"Область покрывает {regionFraction:P2} кадра (порог {minRegionFraction:P2}): " +
                "слишком мала, вероятно, шум, а не содержимое.");

        if (density < minDensity)
            return new ContentRegionResult(false, region, density,
                $"Плотность изменившихся пикселей {density:P2} ниже порога {minDensity:P2}: " +
                "область, вероятно, шум, а не содержимое.");

        return new ContentRegionResult(true, region, density, null);
    }

    /// <summary>
    /// Самая длинная непрерывная «полка»: участок индексов, где доля изменившихся пикселей
    /// (<paramref name="changedAt"/> / <paramref name="lineLength"/>) не меньше
    /// <paramref name="threshold"/>. Возвращает (start, end) включительно, или (-1, -1), если
    /// подходящей строки/столбца нет.
    /// </summary>
    private static (int start, int end) LongestActiveRun(
        int[] changedAt,
        int lineCount,
        int lineLength,
        double threshold)
    {
        var bestStart = -1;
        var bestEnd = -1;
        var bestLen = 0;
        var runStart = -1;

        for (var i = 0; i < lineCount; i++)
        {
            var active = (double)changedAt[i] / lineLength >= threshold;
            if (active)
            {
                if (runStart < 0)
                    runStart = i;
            }
            else
            {
                if (runStart >= 0)
                {
                    var len = i - runStart;
                    if (len > bestLen)
                    {
                        bestLen = len;
                        bestStart = runStart;
                        bestEnd = i - 1;
                    }

                    runStart = -1;
                }
            }
        }

        // Хвостовой run (доходит до конца).
        if (runStart >= 0)
        {
            var len = lineCount - runStart;
            if (len > bestLen)
            {
                bestLen = len;
                bestStart = runStart;
                bestEnd = lineCount - 1;
            }
        }

        return (bestStart, bestEnd);
    }
}
