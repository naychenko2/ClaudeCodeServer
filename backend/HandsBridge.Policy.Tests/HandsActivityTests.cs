using ClaudeHomeServer.HandsBridge.Policy;
using Xunit;

namespace HandsBridge.Policy.Tests;

/// <summary>
/// Сигнал «ход действует руками» (плашка трея и индикатор чата): поднимает его первое разрешённое
/// гейтом действие, которое трогает машину. Отказ, чтение и повтор — нет.
/// </summary>
public class HandsActivityTests
{
    private sealed class CountingSignal : IHandsActivitySignal
    {
        public int Raised { get; private set; }

        public void Raise() => Raised++;
    }

    public static readonly TheoryData<string, string?> Acting = new()
    {
        { HandsTools.App, null },
        { HandsTools.UiClick, null },
        { HandsTools.UiType, null },
        { HandsTools.ScreenshotControl, null },
        { HandsTools.ScreenshotControl, "capture" },
        { HandsTools.WindowManagement, "activate" },
        { HandsTools.WindowManagement, "minimize" },
        { HandsTools.WindowManagement, "maximize" },
        { HandsTools.WindowManagement, "restore" },
        { HandsTools.WindowManagement, "close" },
        { HandsTools.WindowManagement, "move" },
        { HandsTools.WindowManagement, "resize" },
        { HandsTools.WindowManagement, "set_bounds" },
        { HandsTools.WindowManagement, "move_to_monitor" },
        { HandsTools.WindowManagement, "move_and_activate" },
        { HandsTools.WindowManagement, "ensure_visible" },
        { HandsTools.BrowserNavigate, null },
        { HandsTools.BrowserSnapshot, null },
        { HandsTools.BrowserClick, null },
        { HandsTools.BrowserType, null },
        { HandsTools.BrowserTabs, "list" },
        { HandsTools.BrowserWait, null },
        { HandsTools.BrowserScreenshot, null },
        // Первый browser_* поднимает на столе владельца окно Chrome, даже если он только читает DOM
        { HandsTools.BrowserQuery, null },
    };

    public static readonly TheoryData<string, string?> ReadingOnly = new()
    {
        { HandsTools.UiSnapshot, null },
        { HandsTools.UiFind, null },
        { HandsTools.UiRead, null },
        { HandsTools.WindowManagement, "list" },
        { HandsTools.WindowManagement, "find" },
        { HandsTools.WindowManagement, "get_foreground" },
        { HandsTools.WindowManagement, "wait_for" },
        { HandsTools.WindowManagement, "get_state" },
        { HandsTools.WindowManagement, "wait_for_state" },
        { HandsTools.ScreenshotControl, "list_monitors" },
    };

    [Theory]
    [MemberData(nameof(Acting))]
    public void First_allowed_action_raises_the_signal_once(string tool, string? action)
    {
        var signal = new CountingSignal();
        var activity = new HandsActivity(signal);

        Assert.True(activity.Acted(tool, allowed: true, action));
        Assert.False(activity.Acted(tool, allowed: true, action));
        Assert.False(activity.Acted(HandsTools.App, allowed: true));

        Assert.Equal(1, signal.Raised);
        Assert.True(activity.Raised);
    }

    [Theory]
    [MemberData(nameof(Acting))]
    public void Denied_action_does_not_raise(string tool, string? action)
    {
        var signal = new CountingSignal();
        var activity = new HandsActivity(signal);

        Assert.False(activity.Acted(tool, allowed: false, action));

        Assert.Equal(0, signal.Raised);
        Assert.False(activity.Raised);
    }

    [Theory]
    [MemberData(nameof(ReadingOnly))]
    public void Reading_does_not_raise_and_does_not_spend_the_first_action(string tool, string? action)
    {
        var signal = new CountingSignal();
        var activity = new HandsActivity(signal);

        Assert.False(activity.Acted(tool, allowed: true, action));
        Assert.Equal(0, signal.Raised);

        Assert.True(activity.Acted(HandsTools.UiClick, allowed: true));
        Assert.Equal(1, signal.Raised);
    }

    [Fact]
    public void Denied_first_then_allowed_raises_on_the_allowed_one()
    {
        var signal = new CountingSignal();
        var activity = new HandsActivity(signal);

        activity.Acted(HandsTools.UiType, allowed: false);
        Assert.Equal(0, signal.Raised);

        Assert.True(activity.Acted(HandsTools.UiType, allowed: true));
        Assert.Equal(1, signal.Raised);
    }

    [Fact]
    public void Every_tool_and_window_action_is_classified()
    {
        var classified = Acting.Concat(ReadingOnly).Select(row => (string)row[0]).ToHashSet();
        Assert.Equal(HandsTools.All.Order(), classified.Order());

        // Каждое действие window_management из схемы — либо в Acting, либо в ReadingOnly
        var windowActions = Acting.Concat(ReadingOnly)
            .Where(row => (string)row[0] == HandsTools.WindowManagement)
            .Select(row => (string)row[1]!)
            .ToList();
        Assert.Equal(17, windowActions.Distinct().Count());
        Assert.All(windowActions, a => Assert.True(HandsPolicy.CheckWindowAction(a).Allowed, a));
    }
}
