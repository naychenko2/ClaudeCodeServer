namespace ClaudeHomeServer.HandsBridge.Scanning.Pixels;

/// <summary>Ось прокрутки: вертикаль (листание вниз) или горизонталь (сдвиг вправо).</summary>
public enum Axis
{
    Vertical,
    Horizontal,
}

/// <summary>
/// Результат оценки сдвига между двумя кадрами. <see cref="Shift"/> — найденный сдвиг в
/// пикселях (знак: положительный — содержимое <c>before</c> сдвинуто вниз/вправо в <c>after</c>),
/// <see cref="Score"/> — нормализованная корреляция (NCC, 0..1) на этом сдвиге,
/// <see cref="SecondBestScore"/> — лучший NCC на любом ДРУГОМ сдвиге.
/// </summary>
public readonly record struct ShiftResult(int Shift, double Score, double SecondBestScore, bool Ambiguous);

/// <summary>
/// Оценка сдвига полотна по двум кадрам. Чистая функция без Windows-зависимостей: работает с
/// плоскими <see cref="ReadOnlySpan{T}"/>-ами серого (0..255, row-major), поэтому её можно
/// проверять прогоном на Linux, не поднимая System.Drawing/Win32.
///
/// Метод — «корреляция по строкам/столбцам»: изображение схлопывается в 1D-сигнал (среднее по
/// строке для вертикали, среднее по столбцу для горизонтали), и по этому сигналу ищется сдвиг,
/// при котором два сигнала совпадают (нормализованная перекрёстная корреляция). Окно поиска —
/// <c>searchFraction</c> от размера (по умолчанию ±50 %).
///
/// Повторяющиеся узоры (сетка Visio) дают несколько равных пиков корреляции: второй пик близок к
/// лучшему (отношение &gt; 0.9) ИЛИ лучший сдвиг далёк от <paramref name="expectedShift"/> —
/// тогда шов помечается <see cref="ShiftResult.Ambiguous"/>, а не молча выбирается. Среди
/// равноценных пиков <see cref="ShiftResult.Shift"/> берется ближайший к <paramref name="expectedShift"/>.
/// </summary>
public static class ShiftEstimator
{
    /// <summary>Порог «второй пик близок к лучшему»: отношение второго к лучшему NCC.</summary>
    private const double SecondPeakRatio = 0.9;

    /// <summary>
    /// Порог «лучший далёк от ожидаемого сдвига», px: найденный сдвиг дальше этого от
    /// <c>expectedShift</c> — сомнительно (подсказка прокрутки не подтверждается).
    /// </summary>
    private const int FarFromExpected = 16;

    /// <summary>
    /// Оценка сдвига. <paramref name="grayBefore"/>/<paramref name="grayAfter"/> — серые кадры
    /// (row-major, 0..255) одного размера <c>width × height</c>; <paramref name="expectedShift"/> —
    /// ожидаемый сдвиг (подсказка от прокрутки), <paramref name="searchFraction"/> — доля окна
    /// поиска (0..0.5, по умолчанию 0.5 = ±50 %).
    /// </summary>
    public static ShiftResult Estimate(
        ReadOnlySpan<byte> grayBefore,
        ReadOnlySpan<byte> grayAfter,
        int width,
        int height,
        Axis axis,
        int expectedShift,
        double searchFraction = 0.5)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentException("Ширина и высота должны быть положительными.", nameof(width));

        var expected = (long)width * height;
        if (grayBefore.Length != expected || grayAfter.Length != expected)
            throw new ArgumentException(
                $"Кадров {grayBefore.Length} и {grayAfter.Length}, а ожидается {expected} (width × height).",
                nameof(grayBefore));

        // Схлопываем в 1D-сигнал: вертикаль — среднее по строке, горизонталь — по столбцу.
        var a = axis == Axis.Vertical ? RowProfile(grayBefore, width, height) : ColProfile(grayBefore, width, height);
        var b = axis == Axis.Vertical ? RowProfile(grayAfter, width, height) : ColProfile(grayAfter, width, height);

        var n = Math.Min(a.Length, b.Length);
        if (n == 0)
            return new ShiftResult(0, 0.0, 0.0, true);

        var fraction = Math.Clamp(searchFraction, 0.0, 0.5);
        var maxShift = Math.Max(0, Math.Min((int)(fraction * n), n - 1));

        var bestShift = 0;
        var bestScore = double.NegativeInfinity;
        var secondBestScore = double.NegativeInfinity;

        for (var s = -maxShift; s <= maxShift; s++)
        {
            var score = Correlation(a, b, s, n);

            if (score > bestScore)
            {
                // Новый лучший: прежний лучший становится вторым.
                secondBestScore = Math.Max(secondBestScore, bestScore);
                bestScore = score;
                bestShift = s;
            }
            else if (score > secondBestScore)
            {
                secondBestScore = score;
            }
        }

        // Повторяющийся узор: несколько пиков с равным NCC. Среди равноценных (в пределах порога)
        // берём ближайший к ожидаемому сдвигу — это «верный результат по expectedShift».
        if (bestScore > 0)
        {
            var threshold = SecondPeakRatio * bestScore;
            var bestDistance = int.MaxValue;
            for (var s = -maxShift; s <= maxShift; s++)
            {
                if (Correlation(a, b, s, n) < threshold)
                    continue;
                var distance = Math.Abs(s - expectedShift);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    bestShift = s;
                }
            }
        }
        else
        {
            // Плоский сигнал (однотонный кадр): все сдвиги дают NCC=0, сдвиг не определён —
            // возвращаем 0 (без деления на ноль: второй пик тоже 0).
            bestShift = 0;
            secondBestScore = 0;
        }

        var flat = bestScore <= 0;

        // Сомнительный шов: плоский сигнал (сдвиг не определён), второй пик близок к лучшему,
        // либо лучший далёк от ожидаемого.
        var ambiguous =
            flat
            || (bestScore > 0 && secondBestScore > 0 && secondBestScore / bestScore > SecondPeakRatio)
            || Math.Abs(bestShift - expectedShift) > FarFromExpected;

        return new ShiftResult(bestShift, bestScore, secondBestScore, ambiguous);
    }

    /// <summary>
    /// Нормализованная перекрёстная корреляция (NCC, 0..1) сигнала <paramref name="a"/> со
    /// сдвинутым на <paramref name="shift"/> сигналом <paramref name="b"/> на перекрывающейся
    /// области. Знак: <c>b[i + shift] ≈ a[i]</c>, т.е. положительный <c>shift</c> — содержимое
    /// <paramref name="a"/> сдвинуто вниз/вправо на <c>shift</c> в <paramref name="b"/>.
    /// </summary>
    private static double Correlation(ReadOnlySpan<double> a, ReadOnlySpan<double> b, int shift, int n)
    {
        // Индексы i в a, при которых b[i + shift] существует: i ∈ [0, n) и i+shift ∈ [0, n).
        var iStart = Math.Max(0, -shift);
        var iEnd = Math.Min(n, n - shift);
        if (iEnd <= iStart)
            return 0.0;

        var sumA = 0.0;
        var sumB = 0.0;
        for (var i = iStart; i < iEnd; i++)
        {
            sumA += a[i];
            sumB += b[i + shift];
        }

        var count = iEnd - iStart;
        var meanA = sumA / count;
        var meanB = sumB / count;

        var cov = 0.0;
        var varA = 0.0;
        var varB = 0.0;
        for (var i = iStart; i < iEnd; i++)
        {
            var da = a[i] - meanA;
            var db = b[i + shift] - meanB;
            cov += da * db;
            varA += da * da;
            varB += db * db;
        }

        // varA/varB = 0 — плоский сигнал (однотонный кадр), сдвиг неопределён: без деления на ноль.
        var denom = Math.Sqrt(varA * varB);
        return denom <= 1e-9 ? 0.0 : cov / denom;
    }

    /// <summary>1D-сигнал по вертикали: среднее серого по каждой строке (длина = height).</summary>
    private static double[] RowProfile(ReadOnlySpan<byte> gray, int width, int height)
    {
        var profile = new double[height];
        for (var y = 0; y < height; y++)
        {
            var sum = 0.0;
            var row = gray.Slice(y * width, width);
            foreach (var v in row)
                sum += v;
            profile[y] = sum / width;
        }

        return profile;
    }

    /// <summary>1D-сигнал по горизонтали: среднее серого по каждому столбцу (длина = width).</summary>
    private static double[] ColProfile(ReadOnlySpan<byte> gray, int width, int height)
    {
        var profile = new double[width];
        for (var x = 0; x < width; x++)
        {
            var sum = 0.0;
            for (var y = 0; y < height; y++)
                sum += gray[y * width + x];
            profile[x] = sum / height;
        }

        return profile;
    }
}
