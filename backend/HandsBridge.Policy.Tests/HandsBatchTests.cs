using ClaudeHomeServer.HandsBridge.Policy;
using Xunit;

namespace HandsBridge.Policy.Tests;

/// <summary>
/// Пакет шагов <c>hands_batch</c> (D1c): до 20 шагов, только ввод и пауза; <c>wait</c> до 5 с,
/// межшаговая пауза до 2000 мс, сумма ожиданий до 60 с. В пакет не входят <c>app</c>,
/// <c>window_management</c> с <c>close</c>, любой <c>browser_*</c>, снимки, вложенный пакет и
/// неизвестный инструмент.
/// </summary>
public class HandsBatchTests
{
    private static HandsPolicy Policy() => new(new FakeWindows());

    /// <summary>Аргументы <c>window_management</c>: гейт читает <c>action</c> через рефлексию.</summary>
    private sealed class WindowArgs
    {
        public string? Action { get; init; }
    }

    private static HandsBatchStep Step(string tool, int? delay = null, object? args = null)
        => new HandsBatchStep(tool, delay, args);

    // ---------- потолок и форма ----------

    [Fact]
    public void Batch_is_capped_at_20_steps()
    {
        var policy = Policy();
        var twenty = Enumerable.Range(0, 20).Select(_ => Step(HandsTools.KeyboardControl)).ToList();
        Assert.True(policy.CheckBatch(twenty, 0, false).Allowed);

        var twentyOne = Enumerable.Range(0, 21).Select(_ => Step(HandsTools.KeyboardControl)).ToList();
        var decision = policy.CheckBatch(twentyOne, 0, false);
        Assert.False(decision.Allowed);
        Assert.Contains("21 steps", decision.Reason);
    }

    [Fact]
    public void Empty_batch_is_denied()
    {
        var decision = Policy().CheckBatch([], 0, false);
        Assert.False(decision.Allowed);
        Assert.Contains("at least one step", decision.Reason);
    }

    // ---------- запрещённые инструменты ----------

    [Fact]
    public void Nested_batch_is_denied()
    {
        var decision = Policy().CheckBatch([Step(HandsTools.HandsBatch)], 0, false);
        Assert.False(decision.Allowed);
        Assert.Contains("does not nest", decision.Reason);
    }

    [Fact]
    public void App_in_batch_is_denied()
    {
        var decision = Policy().CheckBatch([Step(HandsTools.App)], 0, false);
        Assert.False(decision.Allowed);
        Assert.Contains("does not start programs", decision.Reason);
    }

    [Fact]
    public void Window_management_close_in_batch_is_denied()
    {
        var decision = Policy().CheckBatch([Step(HandsTools.WindowManagement, args: new WindowArgs { Action = "close" })], 0, false);
        Assert.False(decision.Allowed);
        Assert.Contains("does not close windows", decision.Reason);
    }

    [Fact]
    public void Window_management_activate_in_batch_is_allowed()
    {
        Assert.True(Policy().CheckBatch([Step(HandsTools.WindowManagement, args: new WindowArgs { Action = "activate" })], 0, false).Allowed);
    }

    [Theory]
    [InlineData(HandsTools.BrowserNavigate)]
    [InlineData(HandsTools.BrowserSnapshot)]
    [InlineData(HandsTools.BrowserQuery)]
    public void Browser_in_batch_is_denied(string tool)
    {
        var decision = Policy().CheckBatch([Step(tool)], 0, false);
        Assert.False(decision.Allowed);
        Assert.Contains("does not browse", decision.Reason);
    }

    [Fact]
    public void Screenshot_in_batch_is_denied()
    {
        var decision = Policy().CheckBatch([Step(HandsTools.ScreenshotControl)], 0, false);
        Assert.False(decision.Allowed);
        Assert.Contains("does not take screenshots", decision.Reason);
    }

    [Fact]
    public void Unknown_tool_in_batch_is_denied()
    {
        var decision = Policy().CheckBatch([Step("teleport")], 0, false);
        Assert.False(decision.Allowed);
        Assert.Contains("Unknown hands_batch tool", decision.Reason);
    }

    // ---------- регистр имён инструментов и action ----------

    // Модель может прислать «Close», «CLOSE», « close » — сравнение регистронезависимо и с обрезкой.
    [Theory]
    [InlineData("Close")]
    [InlineData("CLOSE")]
    [InlineData(" close ")]
    public void Window_management_close_is_case_and_whitespace_insensitive(string action)
    {
        var decision = Policy().CheckBatch([Step(HandsTools.WindowManagement, args: new WindowArgs { Action = action })], 0, false);
        Assert.False(decision.Allowed);
        Assert.Contains("does not close windows", decision.Reason);
    }

    [Fact]
    public void App_in_batch_is_case_insensitive()
    {
        var decision = Policy().CheckBatch([Step("APP")], 0, false);
        Assert.False(decision.Allowed);
        Assert.Contains("does not start programs", decision.Reason);
    }

    [Fact]
    public void Browser_in_batch_is_case_insensitive()
    {
        var decision = Policy().CheckBatch([Step("Browser_Click")], 0, false);
        Assert.False(decision.Allowed);
        Assert.Contains("does not browse", decision.Reason);
    }

    // ---------- задержки ----------

    [Fact]
    public void Wait_step_is_capped_at_5s()
    {
        var policy = Policy();
        Assert.True(policy.CheckBatch([Step("wait", 5000)], 0, false).Allowed);
        var decision = policy.CheckBatch([Step("wait", 5001)], 0, false);
        Assert.False(decision.Allowed);
        Assert.Contains("at most 5000 ms", decision.Reason);
    }

    [Fact]
    public void Step_delay_is_capped_at_2000ms()
    {
        var policy = Policy();
        Assert.True(policy.CheckBatch([Step(HandsTools.KeyboardControl, 2000)], 0, false).Allowed);
        var decision = policy.CheckBatch([Step(HandsTools.KeyboardControl, 2001)], 0, false);
        Assert.False(decision.Allowed);
        Assert.Contains("at most 2000 ms", decision.Reason);
    }

    [Fact]
    public void Total_waits_are_capped_at_60s()
    {
        var policy = Policy();
        Assert.True(policy.CheckBatch([Step("wait", 5000)], 60_000, false).Allowed);
        var decision = policy.CheckBatch([Step("wait", 5000)], 60_001, false);
        Assert.False(decision.Allowed);
        Assert.Contains("at most 60000 ms", decision.Reason);
    }

    // Мост считает totalWaitMs сам и может занизить; гейт пересчитывает сумму wait-шагов сам и
    // берёт большее: 13 ожиданий по 5 с = 65 с > 60 с, хотя мост передал 5000.
    [Fact]
    public void Wait_sum_is_recomputed_even_when_the_bridge_underreports()
    {
        var steps = Enumerable.Range(0, 13).Select(_ => Step("wait", 5000)).ToList();
        var decision = Policy().CheckBatch(steps, 5000, false);
        Assert.False(decision.Allowed);
        Assert.Contains("at most 60000 ms", decision.Reason);
    }
}
