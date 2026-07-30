using ClaudeHomeServer.Services;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

public class SubscriptionActivityTrackerTests
{
    [Fact]
    public void НиРазуНеТронутыйКлюч_Простаивает()
    {
        var tracker = new SubscriptionActivityTracker();
        tracker.IsIdle("acc", TimeSpan.FromMinutes(5)).Should().BeTrue();
    }

    [Fact]
    public void СразуПослеTouch_НеПростаиваетДляОбычногоПорога()
    {
        var tracker = new SubscriptionActivityTracker();
        tracker.Touch("acc");
        tracker.IsIdle("acc", TimeSpan.FromMinutes(5)).Should().BeFalse();
    }

    [Fact]
    public void СразуПослеTouch_ПростаиваетДляНулевогоПорога()
    {
        // Порог 0 — граница "прошло >= 0" верна сразу же после Touch.
        var tracker = new SubscriptionActivityTracker();
        tracker.Touch("acc");
        tracker.IsIdle("acc", TimeSpan.Zero).Should().BeTrue();
    }

    [Fact]
    public void Touch_ПустойИлиNullКлюч_Игнорируется()
    {
        var tracker = new SubscriptionActivityTracker();
        tracker.Touch(null);
        tracker.Touch("");

        tracker.IsIdle("", TimeSpan.FromMinutes(5)).Should().BeTrue();
    }

    [Fact]
    public void Touch_НеВлияетНаДругиеКлючи()
    {
        var tracker = new SubscriptionActivityTracker();
        tracker.Touch("a");

        tracker.IsIdle("b", TimeSpan.FromMinutes(5)).Should().BeTrue();
    }
}
