using ClaudeHomeServer.HandsBridge.Scanning.Pixels;
using Xunit;

namespace HandsBridge.Scanning.Tests;

/// <summary>
/// Тесты определения области содержимого: разница кадра до/после сдвига — прямоугольник
/// изменившихся пикселей; рамка/линейка/панели (недвигущиеся) исключаются.
/// </summary>
public class ContentRegionTests
{
    private const int W = 200;
    private const int H = 150;

    /// <summary>
    /// Окно с рамкой: внешние 10 px по периметру — «рамка/линейка» (не двигается), внутренняя
    /// область — содержимое. «До» — содержимое в исходной позиции, «после» — содержимое
    /// сдвинуто на (dy, dx), рамка на месте.
    /// </summary>
    private static (byte[] before, byte[] after) MakeFramedWindow(int dy, int dx)
    {
        const int border = 10;
        var before = new byte[W * H];
        var after = new byte[W * H];

        for (var y = 0; y < H; y++)
        {
            for (var x = 0; x < W; x++)
            {
                var inBorder = x < border || x >= W - border || y < border || y >= H - border;
                var idx = y * W + x;

                if (inBorder)
                {
                    // Рамка: одинаковая в «до» и «после» (не двигается).
                    before[idx] = (byte)30;
                    after[idx] = (byte)30;
                    continue;
                }

                // Содержимое: градиент, «после» — сдвинуто.
                before[idx] = (byte)((x + y) % 256);
                var sy = y - dy;
                var sx = x - dx;
                after[idx] = (sy >= border && sy < H - border && sx >= border && sx < W - border)
                    ? (byte)((sx + sy) % 256)
                    : (byte)255; // вышедшее за край — фон
            }
        }

        return (before, after);
    }

    [Fact]
    public void Content_region_excludes_border_and_ruler()
    {
        var (before, after) = MakeFramedWindow(dy: 20, dx: 12);

        var result = ContentRegion.Determine(before, after, W, H, tolerance: 8.0);

        // Область = внутренняя (без рамки 10 px): x ∈ [10, W-10), y ∈ [10, H-10).
        Assert.True(result.Ok);
        Assert.Equal(10, result.Region.X);
        Assert.Equal(10, result.Region.Y);
        // Ширина/высота внутренней области (с учётом сдвига и фона на краях).
        Assert.InRange(result.Region.Width, W - 2 * 10 - 12, W - 2 * 10);
        Assert.InRange(result.Region.Height, H - 2 * 10 - 20, H - 2 * 10);
        // Достоверность высокая: содержимое меняется почти целиком.
        Assert.True(result.Confidence > 0.5);
    }

    [Fact]
    public void Identical_frames_are_a_clear_failure()
    {
        var frame = new byte[W * H];
        for (var i = 0; i < frame.Length; i++)
            frame[i] = (byte)(i % 256);

        var result = ContentRegion.Determine(frame, frame, W, H);

        Assert.False(result.Ok);
        Assert.NotNull(result.Reason);
        Assert.Equal(Rect.Empty, result.Region);
    }

    [Fact]
    public void Sparse_changes_below_min_density_are_a_failure()
    {
        // Один «мигающий» пиксель в центре: плотность далеко ниже порога → шум, не содержимое.
        var before = new byte[W * H];
        var after = new byte[W * H];
        Array.Fill(before, (byte)100);
        Array.Fill(after, (byte)100);
        var center = (H / 2) * W + W / 2;
        after[center] = (byte)200; // один пиксель изменился

        var result = ContentRegion.Determine(before, after, W, H, tolerance: 8.0, minDensity: 0.05);

        Assert.False(result.Ok);
        Assert.NotNull(result.Reason);
    }

    [Fact]
    public void Different_sizes_throw()
    {
        var a = new byte[W * H];
        var b = new byte[(W + 1) * H];

        Assert.Throws<ArgumentException>(() => ContentRegion.Determine(a, b, W, H));
    }

    [Fact]
    public void Full_frame_change_gives_full_region()
    {
        var before = new byte[W * H];
        var after = new byte[W * H];
        for (var i = 0; i < before.Length; i++)
        {
            before[i] = (byte)50;
            after[i] = (byte)200; // весь кадр изменился
        }

        var result = ContentRegion.Determine(before, after, W, H);

        Assert.True(result.Ok);
        Assert.Equal(0, result.Region.X);
        Assert.Equal(0, result.Region.Y);
        Assert.Equal(W, result.Region.Width);
        Assert.Equal(H, result.Region.Height);
        Assert.True(result.Confidence > 0.9);
    }
}
