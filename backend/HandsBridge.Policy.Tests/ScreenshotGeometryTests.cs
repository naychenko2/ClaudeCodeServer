using ClaudeHomeServer.HandsBridge.Policy;
using Xunit;

namespace HandsBridge.Policy.Tests;

/// <summary>
/// Пересчёт пикселя картинки снимка в экранную точку (D2b-2): «Screen X = screenOriginX + imageX × scaleX».
/// Чистая функция без WinAPI — идёт на Linux.
/// </summary>
public class ScreenshotGeometryTests
{
    [Fact]
    public void Unscaled_capture_at_zero_origin_maps_one_to_one()
    {
        var frame = ScreenshotGeometry.Create(0, 0, 1920, 1080, 1920, 1080);

        Assert.Equal(1.0, frame.ScaleX);
        Assert.Equal(1.0, frame.ScaleY);
        Assert.Equal((450, 300), ScreenshotGeometry.ToScreen(frame, 450, 300));
    }

    [Fact]
    public void Image_downscaled_twice_doubles_the_coordinates()
    {
        var frame = ScreenshotGeometry.Create(0, 0, 1920, 1080, 960, 540);

        Assert.Equal(2.0, frame.ScaleX);
        Assert.Equal(2.0, frame.ScaleY);
        Assert.Equal(960, frame.ImageWidth);
        Assert.Equal(540, frame.ImageHeight);
        Assert.Equal((900, 600), ScreenshotGeometry.ToScreen(frame, 450, 300));
    }

    [Fact]
    public void Window_capture_adds_the_window_origin()
    {
        var frame = ScreenshotGeometry.Create(200, 150, 800, 600, 800, 600);

        Assert.Equal((200, 150), ScreenshotGeometry.ToScreen(frame, 0, 0));
        Assert.Equal((250, 180), ScreenshotGeometry.ToScreen(frame, 50, 30));
    }

    [Fact]
    public void Scaled_window_capture_combines_origin_and_scale()
    {
        var frame = ScreenshotGeometry.Create(200, 150, 1600, 1200, 800, 600);

        Assert.Equal((200 + 100, 150 + 60), ScreenshotGeometry.ToScreen(frame, 50, 30));
    }

    [Fact]
    public void Second_monitor_to_the_left_has_negative_origin()
    {
        // Второй монитор слева от основного: левый верхний угол кадра — отрицательный X
        var frame = ScreenshotGeometry.Create(-1920, -200, 1920, 1080, 960, 540);

        Assert.Equal(-1920, frame.OriginX);
        Assert.Equal((-1920, -200), ScreenshotGeometry.ToScreen(frame, 0, 0));
        Assert.Equal((-1920 + 1000, -200 + 600), ScreenshotGeometry.ToScreen(frame, 500, 300));
    }

    [Fact]
    public void Non_integer_scale_rounds_to_the_nearest_pixel()
    {
        var frame = ScreenshotGeometry.Create(0, 0, 1000, 1000, 300, 300); // scale = 3.333…

        Assert.Equal((1000, 1000), ScreenshotGeometry.ToScreen(frame, 300, 300));
        Assert.Equal((333, 333), ScreenshotGeometry.ToScreen(frame, 100, 100));
    }

    [Theory]
    [InlineData(0, 100, 100, 100)]
    [InlineData(100, 0, 100, 100)]
    [InlineData(100, 100, 0, 100)]
    [InlineData(100, 100, 100, -1)]
    public void Empty_sizes_are_rejected(int sourceWidth, int sourceHeight, int imageWidth, int imageHeight) =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ScreenshotGeometry.Create(0, 0, sourceWidth, sourceHeight, imageWidth, imageHeight));
}
