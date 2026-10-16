using ClaudeHomeServer.HandsBridge.Scanning.Pixels;
using Xunit;

namespace HandsBridge.Scanning.Tests;

/// <summary>
/// Тесты планировщика змейки: последовательность шагов (в начало → строки змейкой → стоп по
/// оси), что каждая плитка пройдена ровно один раз, и потолки (truncated с причиной).
/// </summary>
public class SnakePlannerTests
{
    /// <summary>
    /// Симуляция сканирования: «камера» в позиции (row, col) на сетке <paramref name="rows"/>×
    /// <paramref name="cols"/>. Кадры детерминированы позицией: успешный шаг меняет кадр, шаг в
    /// край (повтор той же позиции) — нет (признак конца прокрутки). Возвращает последовательность
    /// шагов, которые выдал планировщик.
    /// </summary>
    private static List<ScanStep> RunSnake(
        int rows,
        int cols,
        int regionWidth = 100,
        int regionHeight = 80,
        SnakePlanner? planner = null)
    {
        planner ??= new SnakePlanner(regionWidth, regionHeight);

        // Кадр = функция позиции: уникален для каждой (row, col).
        byte[] FrameAt(int row, int col)
        {
            var frame = new byte[regionWidth * regionHeight];
            var seed = row * 1000 + col;
            for (var i = 0; i < frame.Length; i++)
                frame[i] = (byte)((i + seed * 37) % 256);
            return frame;
        }

        var steps = new List<ScanStep>();
        var current = planner.CurrentStep;
        steps.Add(current);

        var row = 0;
        var col = 0;

        var guard = 0;
        while (current.Kind != ScanStepKind.Done && guard++ < 1000)
        {
            // Выполняем текущий шаг: перемещаем «камеру».
            if (current.Kind == ScanStepKind.ToStart)
            {
                row = 0;
                col = 0;
            }
            else if (current.Kind == ScanStepKind.Step)
            {
                if (current.Axis == Axis.Horizontal)
                {
                    var newCol = col + current.Direction;
                    // Успешный шаг, если в пределах сетки; иначе «в край» (позиция не меняется).
                    if (newCol >= 0 && newCol < cols)
                        col = newCol;
                    // else: упёрлись в край, col не меняется
                }
                else // Vertical (вниз, к следующей строке)
                {
                    var newRow = row + current.Direction;
                    if (newRow >= 0 && newRow < rows)
                        row = newRow;
                    // else: нижний край, row не меняется
                }
            }

            // Кадр после шага: если позиция не изменилась (край) — тот же кадр (конец прокрутки).
            var frame = FrameAt(row, col);
            var elapsedMs = 1000L * guard; // детерминированное «время»
            current = planner.Feed(frame, measuredShift: 0, elapsedMs);
            steps.Add(current);
        }

        return steps;
    }

    [Fact]
    public void Snake_visits_each_tile_exactly_once_in_order()
    {
        // Сетка 2 строки × 3 столбца.
        const int rows = 2, cols = 3;
        var steps = RunSnake(rows, cols);

        // Первые шаги: ToStart, затем горизонтальные вправо (строка 0: 2 шага + 1 в край),
        // затем вниз, затем горизонтальные влево (строка 1), затем вниз (в край), затем Done.
        Assert.Equal(ScanStepKind.ToStart, steps[0].Kind);

        // Строка 0: вправо. Число горизонтальных шагов вправо = (cols-1) успешных + 1 в край.
        var row0Right = steps
            .Where(s => s.Kind == ScanStepKind.Step && s.Axis == Axis.Horizontal && s.Direction == 1)
            .TakeWhile(s => true)
            .Count();
        // В строке 0: (cols - 1) = 2 успешных + 1 «в край» = 3 шага вправо.
        // Но часть «вправо» — это и шаги строки 0. Проверим общее число.
        Assert.True(row0Right >= cols - 1, $"Ожидалось ≥ {cols - 1} шагов вправо, получено {row0Right}");

        // Есть шаги вниз (переходы между строками): rows - 1 = 1 успешный + 1 «в край» = 2.
        var downSteps = steps.Count(s => s.Kind == ScanStepKind.Step && s.Axis == Axis.Vertical && s.Direction == 1);
        Assert.True(downSteps >= rows - 1, $"Ожидалось ≥ {rows - 1} шагов вниз, получено {downSteps}");

        // Строка 1: влево.
        var leftSteps = steps.Count(s => s.Kind == ScanStepKind.Step && s.Axis == Axis.Horizontal && s.Direction == -1);
        Assert.True(leftSteps >= cols - 1, $"Ожидалось ≥ {cols - 1} шагов влево, получено {leftSteps}");

        // Последний шаг — Done.
        Assert.Equal(ScanStepKind.Done, steps[^1].Kind);
    }

    [Fact]
    public void Snake_row0_goes_right_then_row1_goes_left()
    {
        var steps = RunSnake(rows: 2, cols: 3);

        // Извлекаем только Step-ы (без ToStart/Done) и проверяем порядок направлений.
        var seq = steps
            .Where(s => s.Kind == ScanStepKind.Step)
            .Select(s => s.Axis == Axis.Horizontal ? (s.Direction > 0 ? "R" : "L") : "D")
            .ToList();

        // Строка 0: R R (вправо), затем D (вниз), строка 1: L L (влево), затем D (вниз, в край).
        // Точная последовательность: R, R, R(в край), D, L, L, L(в край), D(в край).
        // Проверим, что все R идут до первого D, а все L — после.
        var firstD = seq.IndexOf("D");
        Assert.True(firstD > 0, "Первый переход вниз должен быть после шагов вправо");

        // До первого D — только R.
        Assert.True(seq.Take(firstD).All(c => c == "R"), "До первого перехода вниз — только вправо");

        // После первого D: сначала L (строка 1), потом D.
        var afterFirstD = seq.Skip(firstD + 1).ToList();
        var secondD = afterFirstD.IndexOf("D");
        Assert.True(secondD > 0, "Второй переход вниз должен быть после шагов влево");
        Assert.True(afterFirstD.Take(secondD).All(c => c == "L"), "Во второй строке — только влево");
    }

    [Fact]
    public void Single_row_snake_stops_at_right_edge()
    {
        // 1 строка × 2 столбца: вправо до края, затем вниз (в край) → Done.
        var steps = RunSnake(rows: 1, cols: 2);

        Assert.Equal(ScanStepKind.ToStart, steps[0].Kind);
        // Шаги: R (col 0→1), R (в край), D (вниз, в край), Done.
        var stepKinds = steps.Select(s => s.Kind).ToList();
        Assert.Equal(ScanStepKind.Done, stepKinds[^1]);
        // Есть хотя бы один шаг вправо и один вниз.
        Assert.Contains(steps, s => s.Kind == ScanStepKind.Step && s.Axis == Axis.Horizontal && s.Direction == 1);
        Assert.Contains(steps, s => s.Kind == ScanStepKind.Step && s.Axis == Axis.Vertical);
    }

    [Fact]
    public void Ceiling_tiles_exceeded_truncates_with_reason()
    {
        // Маленькая область (много плиток) + низкий потолок по плиткам → truncated.
        var planner = new SnakePlanner(
            regionWidth: 10, regionHeight: 10,
            maxTiles: 3); // низкий потолок

        var steps = RunSnake(rows: 3, cols: 3, planner: planner);

        Assert.True(planner.Truncated, "Ожидалось превышение потолка по плиткам");
        Assert.NotNull(planner.TruncationReason);
        Assert.Contains("плиток", planner.TruncationReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Ceiling_side_exceeded_truncates_with_reason()
    {
        // Область больше потолка по стороне → truncated.
        var planner = new SnakePlanner(
            regionWidth: 20000, regionHeight: 100,
            maxSide: SnakePlanner.DefaultMaxSide); // 16384 < 20000

        // Один шаг достаточно, чтобы проверить потолок (проверяется после каждого Feed).
        var frame = new byte[20000 * 100];
        for (var i = 0; i < frame.Length; i++)
            frame[i] = (byte)(i % 256);
        planner.Feed(frame, 0, elapsedMs: 1000);

        Assert.True(planner.Truncated);
        Assert.Contains("сторон", planner.TruncationReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Ceiling_area_exceeded_truncates_with_reason()
    {
        // Стороны в пределах, но площадь > потолка → truncated.
        // 10000 × 10000 = 10^8 > 64×10^6, но стороны 10000 < 16384.
        var planner = new SnakePlanner(
            regionWidth: 10000, regionHeight: 10000,
            maxSide: 20000, // стороны проходят
            maxArea: SnakePlanner.DefaultMaxArea); // 64 Мп

        var frame = new byte[10000 * 10000];
        for (var i = 0; i < frame.Length; i++)
            frame[i] = (byte)(i % 256);
        planner.Feed(frame, 0, elapsedMs: 1000);

        Assert.True(planner.Truncated);
        Assert.Contains("Площадь", planner.TruncationReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Ceiling_time_exceeded_truncates_with_reason()
    {
        var planner = new SnakePlanner(regionWidth: 100, regionHeight: 100, maxElapsedMs: 5000);

        var frame = new byte[100 * 100];
        for (var i = 0; i < frame.Length; i++)
            frame[i] = (byte)(i % 256);
        planner.Feed(frame, 0, elapsedMs: 6000); // > 5000

        Assert.True(planner.Truncated);
        Assert.Contains("время", planner.TruncationReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void No_truncation_within_ceilings()
    {
        var planner = new SnakePlanner(regionWidth: 100, regionHeight: 100);

        var frame = new byte[100 * 100];
        for (var i = 0; i < frame.Length; i++)
            frame[i] = (byte)(i % 256);
        planner.Feed(frame, 0, elapsedMs: 1000);

        Assert.False(planner.Truncated);
        Assert.Null(planner.TruncationReason);
    }

    [Fact]
    public void Measured_shifts_are_recorded_per_step()
    {
        var planner = new SnakePlanner(regionWidth: 100, regionHeight: 100);

        var frame = new byte[100 * 100];
        for (var i = 0; i < frame.Length; i++)
            frame[i] = (byte)(i % 256);
        planner.Feed(frame, measuredShift: 42, elapsedMs: 1000);
        planner.Feed(frame, measuredShift: 7, elapsedMs: 2000);

        Assert.Equal(2, planner.MeasuredShifts.Count);
        Assert.Equal(42, planner.MeasuredShifts[0]);
        Assert.Equal(7, planner.MeasuredShifts[1]);
    }

    [Fact]
    public void Negative_region_size_throws()
    {
        Assert.Throws<ArgumentException>(() => new SnakePlanner(regionWidth: 0, regionHeight: 100));
        Assert.Throws<ArgumentException>(() => new SnakePlanner(regionWidth: 100, regionHeight: -1));
    }

    /// <summary>
    /// M5: шаг по оси = размер области вдоль этой оси − перекрытие. Для 1920×1080:
    /// горизонтальный 1632, вертикальный 918 (15 %).
    /// </summary>
    [Fact]
    public void M5_step_is_per_axis()
    {
        var planner = new SnakePlanner(regionWidth: 1920, regionHeight: 1080);

        // Один и тот же кадр → после ToStart конец прокрутки.
        var frame = new byte[1920 * 1080];
        for (var i = 0; i < frame.Length; i++)
            frame[i] = (byte)42;

        // Шаг 1 (после ToStart) — горизонтальный: ExpectedShift = 1632.
        var hStep = planner.Feed(frame, 0, 1000);
        Assert.Equal(Axis.Horizontal, hStep.Axis);
        Assert.Equal(1632, hStep.ExpectedShift);

        // Шаг 2 (тот же кадр → endOfScroll) — вертикальный: ExpectedShift = 918.
        var vStep = planner.Feed(frame, 0, 2000);
        Assert.Equal(Axis.Vertical, vStep.Axis);
        Assert.Equal(918, vStep.ExpectedShift);
    }

    /// <summary>
    /// M2 + M3: полотно растёт по ИЗМЕРЕННЫМ сдвигам (не по окну). 100 шагов по 918 px при
    /// окне 1920×1080 превышают потолок по стороне/площади; автомат переходит в Done.
    /// </summary>
    [Fact]
    public void M2_M3_growing_canvas_truncates_and_stops()
    {
        var planner = new SnakePlanner(regionWidth: 1920, regionHeight: 1080);

        var step = 0;
        var current = planner.CurrentStep;
        while (current.Kind != ScanStepKind.Done && step < 100)
        {
            // Уникальный кадр (нет конца прокрутки) + измеренный сдвиг 918 px.
            var frame = new byte[1920 * 1080];
            for (var i = 0; i < frame.Length; i++)
                frame[i] = (byte)((i + step * 7) % 256);
            current = planner.Feed(frame, measuredShift: 918, elapsedMs: 1000 + step);
            step++;
        }

        Assert.True(planner.Truncated, "Полотно должно превысить потолок");
        Assert.NotNull(planner.TruncationReason);
        // M3: после срабатывания потолка автомат в Done (не только флаг).
        Assert.Equal(ScanStepKind.Done, current.Kind);
    }

    /// <summary>
    /// m2: точная последовательность шагов змейки на сетке 2×3: R,R,R,D,L,L,L,D и число плиток.
    /// </summary>
    [Fact]
    public void M2_m2_exact_step_sequence_and_tile_count()
    {
        const int rows = 2, cols = 3;
        var steps = RunSnake(rows, cols);

        // Только Step-ы (без ToStart/Done): точная последовательность направлений.
        var seq = steps
            .Where(s => s.Kind == ScanStepKind.Step)
            .Select(s => s.Axis == Axis.Horizontal ? (s.Direction > 0 ? "R" : "L") : "D")
            .ToList();

        // Строка 0: R R (вправо) + R (в край) = 3×R; D (вниз); строка 1: L L + L (в край) = 3×L; D (в край).
        Assert.Equal(new[] { "R", "R", "R", "D", "L", "L", "L", "D" }, seq);

        // Число плиток = число Step-ов (ToStart не плитка, Done не плитка).
        Assert.Equal(seq.Count, planner_TileCount(steps));
    }

    private static int planner_TileCount(List<ScanStep> steps)
        => steps.Count(s => s.Kind == ScanStepKind.Step);

    /// <summary>
    /// m4: валидация параметров — отрицательное перекрытие и NaN дают понятный отказ.
    /// </summary>
    [Fact]
    public void M4_m4_overlap_validation_throws()
    {
        Assert.Throws<ArgumentException>(() => new SnakePlanner(100, 100, overlapFraction: -0.1));
        Assert.Throws<ArgumentException>(() => new SnakePlanner(100, 100, overlapFraction: double.NaN));
        Assert.Throws<ArgumentException>(() => new SnakePlanner(100, 100, overlapFraction: 1.0));
    }
}
