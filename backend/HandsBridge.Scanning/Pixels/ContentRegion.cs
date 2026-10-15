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

        var minX = int.MaxValue;
        var minY = int.MaxValue;
        var maxX = -1;
        var maxY = -1;
        var changed = 0;

        for (var i = 0; i < grayBefore.Length; i++)
        {
            var diff = Math.Abs((int)grayBefore[i] - grayAfter[i]);
            if (diff <= tolerance)
                continue;

            changed++;
            var x = i % width;
            var y = i / width;
            if (x < minX) minX = x;
            if (x > maxX) maxX = x;
            if (y < minY) minY = y;
            if (y > maxY) maxY = y;
        }

        if (changed == 0)
            return new ContentRegionResult(false, Rect.Empty, 0.0,
                "Кадры совпадают в пределах допуска: содержимое не изменилось, область не определена.");

        var region = new Rect(minX, minY, maxX - minX + 1, maxY - minY + 1);
        var density = (double)changed / region.Area;
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
}
