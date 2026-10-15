using ClaudeHomeServer.HandsBridge.Scanning.Imaging;
using Xunit;

namespace HandsBridge.Scanning.Tests;

/// <summary>
/// Тесты сборки полотна: (а) градиент 3×2 — пиксель в пиксель; (б) повторяющаяся сетка —
/// сомнительные швы помечены; (ж) схема меньше окна (одна плитка) и вырожденные размеры.
/// </summary>
public class CanvasAssemblerTests
{
    /// <summary>Оригинал — детерминированный цветной градиент (R по x, G по y, B константа).</summary>
    private static byte[] MakeOriginal(int origW, int origH)
    {
        var original = new byte[origW * origH * 3];
        for (var y = 0; y < origH; y++)
            for (var x = 0; x < origW; x++)
            {
                var idx = (y * origW + x) * 3;
                original[idx] = (byte)((x * 255) / Math.Max(1, origW - 1));
                original[idx + 1] = (byte)((y * 255) / Math.Max(1, origH - 1));
                original[idx + 2] = 128;
            }

        return original;
    }

    /// <summary>Вырезка w×h из оригинала в точке (x0, y0).</summary>
    private static byte[] Crop(byte[] original, int origW, int x0, int y0, int w, int h)
    {
        var crop = new byte[w * h * 3];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var src = ((y0 + y) * origW + (x0 + x)) * 3;
                var dst = (y * w + x) * 3;
                crop[dst] = original[src];
                crop[dst + 1] = original[src + 1];
                crop[dst + 2] = original[src + 2];
            }

        return crop;
    }

    /// <summary>
    /// (а) Схема-градиент 3×2 окна: сборка совпадает с оригиналом пиксель в пиксель. Плитки
    /// лежат на накопленных (здесь — точных) сдвигах, перекрытия сшиты «верхний/левый выигрывает».
    /// </summary>
    [Fact]
    public void Assembly_matches_original_pixel_for_pixel()
    {
        const int W = 40, H = 30;
        const int overlapX = 6, overlapY = 4;
        var stepX = W - overlapX; // 34
        var stepY = H - overlapY; // 26
        const int cols = 3, rows = 2;

        var origW = (cols - 1) * stepX + W; // 108
        var origH = (rows - 1) * stepY + H; // 56
        var original = MakeOriginal(origW, origH);

        var tiles = new List<Tile>();
        for (var r = 0; r < rows; r++)
            for (var c = 0; c < cols; c++)
            {
                var x0 = c * stepX;
                var y0 = r * stepY;
                var pixels = Crop(original, origW, x0, y0, W, H);
                tiles.Add(new Tile(pixels, W, H, x0, y0,
                    stepX, stepX, 1.0, false));
            }

        var result = CanvasAssembler.Assemble(tiles);

        Assert.Equal(origW, result.Width);
        Assert.Equal(origH, result.Height);
        Assert.Equal(rows, result.Rows);
        Assert.Equal(cols, result.Columns);

        // Пиксель в пиксель: холст = оригинал.
        Assert.Equal(original, result.Pixels);

        // Чистый градиент: сомнительных швов нет.
        Assert.Empty(result.DoubtfulSeams);
    }

    /// <summary>
    /// (б) Повторяющиеся блоки (сетка): сдвиг неоднозначен (Ambiguous) → швы между плитками
    /// помечены сомнительными, а не молча выбраны.
    /// </summary>
    [Fact]
    public void Repeating_grid_marks_doubtful_seams()
    {
        const int W = 40, H = 30;
        const int overlapX = 6, overlapY = 4;
        var stepX = W - overlapX;
        var stepY = H - overlapY;
        const int cols = 3, rows = 2;

        var origW = (cols - 1) * stepX + W;
        var origH = (rows - 1) * stepY + H;
        var original = MakeOriginal(origW, origH);

        var tiles = new List<Tile>();
        for (var r = 0; r < rows; r++)
            for (var c = 0; c < cols; c++)
            {
                var x0 = c * stepX;
                var y0 = r * stepY;
                var pixels = Crop(original, origW, x0, y0, W, H);
                // Повторяющийся узор: сдвиг неоднозначен (Ambiguous), корреляция низкая.
                tiles.Add(new Tile(pixels, W, H, x0, y0,
                    stepX, stepX, 0.4, true));
            }

        var result = CanvasAssembler.Assemble(tiles, minScore: 0.5);

        // Каждый шов между соседними плитками помечен (Ambiguous + низкий Score).
        Assert.NotEmpty(result.DoubtfulSeams);
        // Соседних пар: горизонтальные (rows × (cols-1)) + вертикальные ((rows-1) × cols).
        var expectedSeams = rows * (cols - 1) + (rows - 1) * cols;
        Assert.Equal(expectedSeams, result.DoubtfulSeams.Count);
    }

    /// <summary>
    /// (б-2) Расхождение измеренного и ожидаемого сдвига выше допуска → шов сомнительный.
    /// </summary>
    [Fact]
    public void Shift_mismatch_marks_doubtful_seam()
    {
        const int W = 40, H = 30;
        const int overlapX = 6, overlapY = 4;
        var stepX = W - overlapX;
        var stepY = H - overlapY;
        const int cols = 2, rows = 1;

        var origW = (cols - 1) * stepX + W;
        var origH = (rows - 1) * stepY + H;
        var original = MakeOriginal(origW, origH);

        var tiles = new List<Tile>();
        for (var c = 0; c < cols; c++)
        {
            var x0 = c * stepX;
            var pixels = Crop(original, origW, x0, 0, W, H);
            // Вторая плитка: измеренный сдвиг сильно отличается от ожидаемого.
            var measured = c == 0 ? stepX : stepX + 40; // расхождение 40 > допуска 16
            tiles.Add(new Tile(pixels, W, H, x0, 0,
                measured, stepX, 0.9, false));
        }

        var result = CanvasAssembler.Assemble(tiles, shiftTolerance: 16);

        // Один шов (между двумя плитками), помечен из-за расхождения сдвига.
        Assert.Single(result.DoubtfulSeams);
        Assert.Contains("сдви", result.DoubtfulSeams[0].Reason, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// (ж) Схема меньше окна: одна плитка, холст = плитка, без швов.
    /// </summary>
    [Fact]
    public void Single_tile_smaller_than_window()
    {
        const int W = 40, H = 30;
        var original = MakeOriginal(W, H);

        var tile = new Tile(original, W, H, 0, 0,
            0, 0, 1.0, false);

        var result = CanvasAssembler.Assemble(new List<Tile> { tile });

        Assert.Equal(W, result.Width);
        Assert.Equal(H, result.Height);
        Assert.Equal(1, result.Rows);
        Assert.Equal(1, result.Columns);
        Assert.Equal(original, result.Pixels);
        Assert.Empty(result.DoubtfulSeams);
    }

    /// <summary>
    /// (ж) Вырожденные размеры: 1×1 пиксель, RGBA (4 канала).
    /// </summary>
    [Fact]
    public void Degenerate_single_pixel_rgba()
    {
        var pixels = new byte[] { 10, 20, 30, 255 }; // RGBA

        var tile = new Tile(pixels, 1, 1, 0, 0,
            0, 0, 1.0, false);

        var result = CanvasAssembler.Assemble(new List<Tile> { tile });

        Assert.Equal(1, result.Width);
        Assert.Equal(1, result.Height);
        Assert.Equal(pixels, result.Pixels);
    }

    /// <summary>
    /// (ж) Пустой список плиток — понятный отказ.
    /// </summary>
    [Fact]
    public void Empty_tiles_throw()
    {
        Assert.Throws<ArgumentException>(() => CanvasAssembler.Assemble(new List<Tile>()));
    }

    /// <summary>
    /// (ж) Смешанные каналы (RGB + RGBA) — понятный отказ.
    /// </summary>
    [Fact]
    public void Mixed_channels_throw()
    {
        var rgb = new Tile(new byte[40 * 30 * 3], 40, 30, 0, 0, 0, 0, 1.0, false);
        var rgba = new Tile(new byte[40 * 30 * 4], 40, 30, 40, 0, 0, 0, 1.0, false);

        Assert.Throws<ArgumentException>(() =>
            CanvasAssembler.Assemble(new List<Tile> { rgb, rgba }));
    }

    /// <summary>
    /// (а-2) RGBA-градиент 2×2: сборка пиксель в пиксель, альфа-канал сохранён.
    /// </summary>
    [Fact]
    public void Rgba_assembly_matches_original()
    {
        const int W = 20, H = 16;
        const int overlapX = 3, overlapY = 2;
        var stepX = W - overlapX;
        var stepY = H - overlapY;
        const int cols = 2, rows = 2;

        var origW = (cols - 1) * stepX + W;
        var origH = (rows - 1) * stepY + H;

        // RGBA-оригинал: R по x, G по y, B константа, A = 255.
        var original = new byte[origW * origH * 4];
        for (var y = 0; y < origH; y++)
            for (var x = 0; x < origW; x++)
            {
                var idx = (y * origW + x) * 4;
                original[idx] = (byte)((x * 255) / Math.Max(1, origW - 1));
                original[idx + 1] = (byte)((y * 255) / Math.Max(1, origH - 1));
                original[idx + 2] = 100;
                original[idx + 3] = 255;
            }

        var tiles = new List<Tile>();
        for (var r = 0; r < rows; r++)
            for (var c = 0; c < cols; c++)
            {
                var x0 = c * stepX;
                var y0 = r * stepY;
                var crop = new byte[W * H * 4];
                for (var y = 0; y < H; y++)
                    for (var x = 0; x < W; x++)
                    {
                        var src = ((y0 + y) * origW + (x0 + x)) * 4;
                        var dst = (y * W + x) * 4;
                        for (var ch = 0; ch < 4; ch++)
                            crop[dst + ch] = original[src + ch];
                    }

                tiles.Add(new Tile(crop, W, H, x0, y0,
                    stepX, stepX, 1.0, false));
            }

        var result = CanvasAssembler.Assemble(tiles);

        Assert.Equal(original, result.Pixels);
    }
}
