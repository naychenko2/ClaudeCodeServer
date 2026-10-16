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

    /// <summary>
    /// M4: ползунок полосы прокрутки (тонкая колонка, двигается при прокрутке) отделён от
    /// содержимого зазором — min/max раздувал бы область, проекции берут только «полку» содержимого.
    /// </summary>
    [Fact]
    public void M4_scrollbar_thumb_excluded_from_region()
    {
        var before = new byte[W * H];
        var after = new byte[W * H];
        Array.Fill(before, (byte)0);
        Array.Fill(after, (byte)0);

        // Плотное содержимое x[10,180) × y[10,140), «после» — другое.
        for (var y = 10; y < 140; y++)
            for (var x = 10; x < 180; x++)
            {
                before[y * W + x] = (byte)((x + y) % 256);
                after[y * W + x] = (byte)((x + y + 50) % 256);
            }

        // Ползунок: тонкая колонка x[195,197) × y[20,130), двигается (зазор 180..194 — фон).
        for (var y = 20; y < 130; y++)
            for (var x = 195; x < 197; x++)
            {
                before[y * W + x] = (byte)100;
                after[y * W + x] = (byte)200;
            }

        var result = ContentRegion.Determine(before, after, W, H, tolerance: 8.0);

        // Область = содержимое x[10,180) × y[10,140); ползунок (x 195-196) исключён.
        Assert.True(result.Ok);
        Assert.Equal(10, result.Region.X);
        Assert.Equal(180, result.Region.Right + 1);
        Assert.Equal(10, result.Region.Y);
        Assert.Equal(140, result.Region.Bottom + 1);
    }

    /// <summary>
    /// M4: выброс в углу (один пиксель — часы/курсор) при плотном содержимом не раздувает область:
    /// его строка и столбец имеют долю изменившихся ниже порога.
    /// </summary>
    [Fact]
    public void M4_corner_outlier_excluded_with_dense_content()
    {
        var before = new byte[W * H];
        var after = new byte[W * H];
        Array.Fill(before, (byte)0);
        Array.Fill(after, (byte)0);

        // Плотное содержимое x[10,190) × y[10,140), «после» — другое.
        for (var y = 10; y < 140; y++)
            for (var x = 10; x < 190; x++)
            {
                before[y * W + x] = (byte)((x + y) % 256);
                after[y * W + x] = (byte)((x + y + 40) % 256);
            }

        // Выброс в углу: один пиксель (195, 5) изменился.
        before[5 * W + 195] = (byte)10;
        after[5 * W + 195] = (byte)200;

        var result = ContentRegion.Determine(before, after, W, H, tolerance: 8.0);

        // Область = плотное содержимое; выброс в углу (195,5) не входит.
        Assert.True(result.Ok);
        Assert.Equal(10, result.Region.X);
        Assert.Equal(190, result.Region.Right + 1);
        Assert.Equal(10, result.Region.Y);
        Assert.Equal(140, result.Region.Bottom + 1);
    }

    /// <summary>
    /// M4: пустой (неизменённый) фон у краёв кадра не входит в область — только блок содержимого.
    /// </summary>
    [Fact]
    public void M4_empty_background_at_edge_excluded()
    {
        var before = new byte[W * H];
        var after = new byte[W * H];
        Array.Fill(before, (byte)0); // фон 0
        Array.Fill(after, (byte)0);   // фон 0 (не изменился)

        // Блок содержимого x[30,120) × y[20,130), «после» — другое; фон вокруг — 0.
        for (var y = 20; y < 130; y++)
            for (var x = 30; x < 120; x++)
            {
                before[y * W + x] = (byte)((x + y) % 256);
                after[y * W + x] = (byte)((x + y + 30) % 256);
            }

        var result = ContentRegion.Determine(before, after, W, H, tolerance: 8.0);

        // Область = блок содержимого; пустой фон у краёв не входит.
        Assert.True(result.Ok);
        Assert.Equal(30, result.Region.X);
        Assert.Equal(120, result.Region.Right + 1);
        Assert.Equal(20, result.Region.Y);
        Assert.Equal(130, result.Region.Bottom + 1);
    }
}
