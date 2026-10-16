using ClaudeHomeServer.HandsBridge.Scanning.Pixels;
using Xunit;

namespace HandsBridge.Scanning.Tests;

/// <summary>Тесты стабилизации кадра: «кадр не изменился» (допуск) и «конец прокрутки» (повторный хеш).</summary>
public class FrameStabilityTests
{
    [Fact]
    public void Identical_frames_are_stable()
    {
        var frame = new byte[] { 10, 20, 30, 40, 50 };

        Assert.True(FrameStability.AreStable(frame, frame, tolerance: 0.0));
    }

    [Fact]
    public void Frames_within_tolerance_are_stable()
    {
        var a = new byte[] { 100, 100, 100, 100 };
        // Средняя абсолютная разница = 1.0 < 2.0 → стабильно.
        var b = new byte[] { 101, 100, 100, 99 };

        Assert.True(FrameStability.AreStable(a, b, tolerance: 2.0));
    }

    [Fact]
    public void Frames_beyond_tolerance_are_not_stable()
    {
        var a = new byte[] { 100, 100, 100, 100 };
        // Средняя абсолютная разница = 10.0 > 5.0 → нестабильно.
        var b = new byte[] { 120, 100, 100, 80 };

        Assert.False(FrameStability.AreStable(a, b, tolerance: 5.0));
    }

    [Fact]
    public void Different_sizes_throw_argument_exception()
    {
        var a = new byte[] { 1, 2, 3 };
        var b = new byte[] { 1, 2 };

        Assert.Throws<ArgumentException>(() => FrameStability.AreStable(a, b, tolerance: 1.0));
    }

    [Fact]
    public void Empty_frames_are_stable()
    {
        Assert.True(FrameStability.AreStable(ReadOnlySpan<byte>.Empty, ReadOnlySpan<byte>.Empty, tolerance: 0.0));
    }

    [Fact]
    public void Two_consecutive_equal_hashes_mean_end_of_scroll()
    {
        Assert.True(FrameStability.IsEndOfScroll(new List<ulong> { 1, 2, 7, 7 }));
    }

    [Fact]
    public void Changing_frames_are_not_end_of_scroll()
    {
        Assert.False(FrameStability.IsEndOfScroll(new List<ulong> { 1, 2, 3, 4 }));
    }

    [Fact]
    public void Fewer_than_two_hashes_is_not_end_of_scroll()
    {
        Assert.False(FrameStability.IsEndOfScroll(new List<ulong>()));
        Assert.False(FrameStability.IsEndOfScroll(new List<ulong> { 5 }));
    }

    [Fact]
    public void Null_hashes_throw()
    {
        Assert.Throws<ArgumentNullException>(() => FrameStability.IsEndOfScroll(null));
    }
}
