using ClaudeHomeServer.HandsBridge.Scanning.Pixels;
using Xunit;

namespace HandsBridge.Scanning.Tests;

/// <summary>
/// Тесты оценки сдвига полотна. Синтетическая «сетка» (клетки 16 px + детерминированный шум)
/// повторяет самопроверку спайка: шум ломает идеальную периодичность, поэтому истинный сдвиг
/// (не кратный периоду) находится как единственный пик, а кратный периоду — даёт равные пики.
/// </summary>
public class ShiftEstimatorTests
{
    private const int W = 320;
    private const int H = 240;

    /// <summary>
    /// Синтетическое «полотно»: периодическая сетка (клетки 16 px, границы темнее) плюс
    /// детерминированный шум, заданный ПО КЛЕТКАМ (16×16), а не по пикселям. Клеточный шум
    /// не усредняется при схлопывании в профиль строки/столбца, поэтому ломает идеальную
    /// периодичность: истинный сдвиг (не кратный 16) становится единственным пиком, а кратный
    /// 16 даёт равные пики (повторяющийся узор).
    /// </summary>
    private static byte[] MakeGrid(int width, int height, int seed = 42)
    {
        var rng = new Random(seed);
        var canvas = new byte[width * height];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var onGrid = (y % 16 == 0) || (x % 16 == 0);
                var baseVal = onGrid ? 40 : 200;
                // Шум по клетке: одна величина на всю клетку 16×16, детерминирована координатами.
                var cellNoise = ((y / 16 * 31 + x / 16) % 17) - 8; // -8..+8
                canvas[y * width + x] = (byte)Math.Clamp(baseVal + cellNoise, 0, 255);
            }

        return canvas;
    }

    /// <summary>«Кадр после»: содержимое сдвинуто на (dy, dx); вышедшие за край пиксели — фон 255.</summary>
    private static byte[] ShiftFrame(byte[] canvas, int width, int height, int dy, int dx)
    {
        var after = new byte[width * height];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var sy = y - dy;
                var sx = x - dx;
                after[y * width + x] = (sy >= 0 && sy < height && sx >= 0 && sx < width)
                    ? canvas[sy * width + sx]
                    : (byte)255;
            }

        return after;
    }

    [Fact]
    public void Vertical_shift_not_multiple_of_period_is_found_exactly()
    {
        var canvas = MakeGrid(W, H);
        var after = ShiftFrame(canvas, W, H, dy: 12, dx: 0);

        var result = ShiftEstimator.Estimate(canvas, after, W, H, Axis.Vertical, expectedShift: 12);

        // «Находится точно»: аргмакс — единственный пик на истинном сдвиге (не кратном периоду).
        Assert.Equal(12, result.Shift);
        Assert.True(result.Score > 0.9);
    }

    [Fact]
    public void Horizontal_shift_not_multiple_of_period_is_found_exactly()
    {
        var canvas = MakeGrid(W, H);
        var after = ShiftFrame(canvas, W, H, dy: 0, dx: 7);

        var result = ShiftEstimator.Estimate(canvas, after, W, H, Axis.Horizontal, expectedShift: 7);

        Assert.Equal(7, result.Shift);
        Assert.True(result.Score > 0.9);
    }

    [Fact]
    public void Vertical_shift_multiple_of_period_is_ambiguous_and_resolved_by_expected()
    {
        var canvas = MakeGrid(W, H);
        var after = ShiftFrame(canvas, W, H, dy: 16, dx: 0);

        var result = ShiftEstimator.Estimate(canvas, after, W, H, Axis.Vertical, expectedShift: 16);

        // Кратный периоду: пики корреляции равны (повторяющийся узор) → шов неоднозначен,
        // а сам сдвиг восстановлен по expectedShift.
        Assert.True(result.Ambiguous);
        Assert.Equal(16, result.Shift);
    }

    [Fact]
    public void Vertical_shift_twice_period_is_ambiguous_and_resolved_by_expected()
    {
        var canvas = MakeGrid(W, H);
        var after = ShiftFrame(canvas, W, H, dy: 32, dx: 0);

        var result = ShiftEstimator.Estimate(canvas, after, W, H, Axis.Vertical, expectedShift: 32);

        Assert.True(result.Ambiguous);
        Assert.Equal(32, result.Shift);
    }

    [Fact]
    public void Monochrome_frame_is_ambiguous_without_division_by_zero()
    {
        var flat = new byte[W * H];
        Array.Fill(flat, (byte)128);

        var result = ShiftEstimator.Estimate(flat, flat, W, H, Axis.Vertical, expectedShift: 5);

        Assert.True(result.Ambiguous);
        Assert.Equal(0, result.Shift);
    }

    [Fact]
    public void Different_sizes_throw_argument_exception()
    {
        var a = MakeGrid(W, H);
        var b = MakeGrid(W + 1, H);

        Assert.Throws<ArgumentException>(() =>
            ShiftEstimator.Estimate(a, b, W, H, Axis.Vertical, expectedShift: 0));
    }

    [Fact]
    public void Zero_shift_is_found()
    {
        var canvas = MakeGrid(W, H);

        var result = ShiftEstimator.Estimate(canvas, canvas, W, H, Axis.Vertical, expectedShift: 0);

        Assert.Equal(0, result.Shift);
    }

    [Fact]
    public void Negative_shift_has_correct_sign()
    {
        // after[y] = canvas[y + 9] (содержимое ушло ВВЕРХ на 9) => сдвиг -9.
        var canvas = MakeGrid(W, H);
        var after = new byte[W * H];
        for (var y = 0; y < H; y++)
            for (var x = 0; x < W; x++)
            {
                var sy = y + 9;
                after[y * W + x] = (sy >= 0 && sy < H) ? canvas[sy * W + x] : (byte)255;
            }

        var result = ShiftEstimator.Estimate(canvas, after, W, H, Axis.Vertical, expectedShift: -9);

        Assert.Equal(-9, result.Shift);
    }

    // ---------- EstimateAround (K1): окно вокруг ожидаемого сдвига, а не от нуля ----------

    /// <summary>
    /// K1: сдвиг больше 50 % размера (перекрытие &lt; 50 %) — симметричное окно от нуля
    /// (<see cref="ShiftEstimator.Estimate"/>) его не покрывает, а <see cref="ShiftEstimator.EstimateAround"/>
    /// (окно вокруг ожидаемого) находит. Сдвиг 150 при H=240 — 62 %, вне окна ±50 % от нуля.
    /// </summary>
    [Fact]
    public void EstimateAround_finds_large_shift_that_symmetric_estimate_misses()
    {
        var canvas = MakeGrid(W, H);
        var after = ShiftFrame(canvas, W, H, dy: 150, dx: 0);

        // Симметричное окно от нуля (±50 % = ±120) не покрывает 150 → находит край, не истинный.
        var symmetric = ShiftEstimator.Estimate(canvas, after, W, H, Axis.Vertical, expectedShift: 150);
        // Окно вокруг ожидаемого (150 ± 35 %) накрывает 150 → находит истинный сдвиг.
        var around = ShiftEstimator.EstimateAround(canvas, after, W, H, Axis.Vertical, expectedShift: 150);

        Assert.NotEqual(150, symmetric.Shift); // старое окно промахивается
        Assert.Equal(150, around.Shift);        // новое окно находит
        Assert.True(around.Score > 0.9);
    }

    /// <summary>
    /// K1: <see cref="ShiftEstimator.EstimateAround"/> с повторяющимся узором (сдвиг кратен периоду)
    /// помечает шов <see cref="ShiftResult.Ambiguous"/> и среди равных пиков берёт ближайший к ожидаемому.
    /// </summary>
    [Fact]
    public void EstimateAround_repeating_pattern_is_ambiguous_and_resolved_by_expected()
    {
        var canvas = MakeGrid(W, H);
        var after = ShiftFrame(canvas, W, H, dy: 32, dx: 0); // 32 = 2 периода (16)

        var result = ShiftEstimator.EstimateAround(canvas, after, W, H, Axis.Vertical, expectedShift: 32);

        Assert.True(result.Ambiguous);
        Assert.Equal(32, result.Shift);
    }

    /// <summary>
    /// K1: окно <see cref="ShiftEstimator.EstimateAround"/> не шире допустимого перекрытия:
    /// при minOverlapFraction=0.10 сдвиг больше 90 % размера отбрасывается (кадры почти не перекрываются).
    /// </summary>
    [Fact]
    public void EstimateAround_respects_min_overlap_window()
    {
        var canvas = MakeGrid(W, H);
        // Сдвиг 230 при H=240 — 95.8 %, кадра почти не перекрываются (перекрытие 4.2 % < 10 %).
        var after = ShiftFrame(canvas, W, H, dy: 230, dx: 0);

        var result = ShiftEstimator.EstimateAround(canvas, after, W, H, Axis.Vertical, expectedShift: 230, minOverlapFraction: 0.10);

        // Окно обрезано до |s| ≤ 90 % (216): истинный 230 вне окна → аргмакс не дальше 216,
        // а не на истинном 230. Без обрезки (minOverlap=0) окно шире и 230 достижимо.
        Assert.NotEqual(230, result.Shift);
        Assert.True(Math.Abs(result.Shift) <= 216, $"сдвиг {result.Shift} вышел за допустимое перекрытие (≤216)");
    }
}
