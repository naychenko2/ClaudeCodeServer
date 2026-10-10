using ClaudeHomeServer.Services.ImageEditor;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.ImageEditor.Characters;

public sealed class KeyedLocksTests
{
    [Fact]
    public async Task SameKey_ExcludesEachOther_UntilReleased()
    {
        var locks = new KeyedLocks();
        var first = await locks.AcquireAsync("k");
        var second = locks.AcquireAsync("k");

        (await Task.WhenAny(second, Task.Delay(200))).Should().NotBeSameAs(second);
        first.Dispose();
        (await second.WaitAsync(TimeSpan.FromSeconds(5))).Dispose();
    }

    [Fact]
    public async Task DifferentKeys_RunInParallel()
    {
        var locks = new KeyedLocks();
        using var a = await locks.AcquireAsync("a");
        using var b = await locks.AcquireAsync("b").WaitAsync(TimeSpan.FromSeconds(5));
        locks.Count.Should().Be(2);
    }

    [Fact]
    public async Task AfterRelease_DictionaryIsEmpty_AndDoubleDisposeIsSafe()
    {
        var locks = new KeyedLocks();
        var h = await locks.AcquireAsync("k");
        h.Dispose();
        h.Dispose();

        locks.Count.Should().Be(0);
        using var again = await locks.AcquireAsync("k").WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ExceptionInsideUsing_ReleasesKey()
    {
        var locks = new KeyedLocks();
        var act = async () =>
        {
            using var _ = await locks.AcquireAsync("k");
            throw new InvalidOperationException("boom");
        };
        await act.Should().ThrowAsync<InvalidOperationException>();

        locks.Count.Should().Be(0);
        using var again = await locks.AcquireAsync("k").WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task CancelledWait_DoesNotCorruptCounter()
    {
        var locks = new KeyedLocks();
        var holder = await locks.AcquireAsync("k");
        using var cts = new CancellationTokenSource();
        var waiter = locks.AcquireAsync("k", cts.Token);
        cts.Cancel();
        await waiter.Awaiting(w => w).Should().ThrowAsync<OperationCanceledException>();

        locks.Count.Should().Be(1, "держатель всё ещё владеет ключом");
        holder.Dispose();
        locks.Count.Should().Be(0);
        using var again = await locks.AcquireAsync("k").WaitAsync(TimeSpan.FromSeconds(5));
    }
}
