using ClaudeHomeServer.HandsBridge.Policy;
using Xunit;

namespace HandsBridge.Policy.Tests;

/// <summary>Своё окно, <c>ui_*</c>, <c>window_management</c> и <c>screenshot_control</c>.</summary>
public class HandsWindowTests
{
    public static readonly TheoryData<string> UiTools =
        [HandsTools.UiSnapshot, HandsTools.UiFind, HandsTools.UiClick, HandsTools.UiType, HandsTools.UiRead];

    public static readonly TheoryData<string> HandleActions =
    [
        "activate", "minimize", "maximize", "restore", "close", "move", "resize", "set_bounds",
        "move_to_monitor", "get_state", "wait_for_state", "move_and_activate", "ensure_visible",
    ];

    // ---------- своё окно ----------

    [Fact]
    public void Window_of_listed_program_in_the_job_is_own_even_if_path_differs_in_case_and_slashes()
    {
        Assert.True(Machine.Policy().IsOwnWindow(Machine.Own));
    }

    [Fact]
    public void Child_process_outside_the_list_in_the_same_job_is_foreign()
    {
        Assert.False(Machine.Policy().IsOwnWindow(Machine.ChildOutsideList));
    }

    [Fact]
    public void Listed_program_started_by_the_user_outside_the_job_is_foreign()
    {
        Assert.False(Machine.Policy().IsOwnWindow(Machine.UserNotepad));
    }

    [Fact]
    public void Interpreter_in_the_job_is_foreign_even_if_listed()
    {
        Assert.False(Machine.Policy().IsOwnWindow(Machine.ForbiddenInJob));
    }

    [Fact]
    public void Program_removed_from_the_list_loses_its_windows_immediately()
    {
        var apps = Machine.Apps(Machine.Notepad);
        var policy = new HandsPolicy(() => apps, Machine.Windows());
        Assert.True(policy.IsOwnWindow(Machine.Own));

        apps = Machine.Apps(Machine.Paint);

        Assert.False(policy.IsOwnWindow(Machine.Own));
    }

    [Theory]
    [InlineData("0x64")]
    [InlineData(" 100")]
    [InlineData("100 ")]
    [InlineData("+100")]
    [InlineData("-100")]
    [InlineData("100.0")]
    [InlineData("0")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("99999999999999999999999")]
    public void Handle_is_accepted_only_as_plain_decimal(string? handle)
    {
        Assert.False(Machine.Policy().IsOwnWindow(handle));
    }

    // ---------- ui_* ----------

    [Theory]
    [MemberData(nameof(UiTools))]
    public void Ui_tool_on_own_window_is_allowed(string tool)
    {
        Assert.True(Machine.Policy().CheckUi(tool, Machine.Own, Machine.ResolveElement).Allowed);
    }

    [Theory]
    [MemberData(nameof(UiTools))]
    public void Ui_tool_on_foreign_window_is_denied(string tool)
    {
        var policy = Machine.Policy();

        foreach (var foreign in new[] { Machine.ChildOutsideList, Machine.UserNotepad, Machine.ForbiddenInJob, Machine.Missing })
        {
            var decision = policy.CheckUi(tool, foreign, Machine.ResolveElement);
            Assert.False(decision.Allowed, $"{tool} on {foreign}");
            Assert.Contains("not yours", decision.Reason);
        }
    }

    [Theory]
    [MemberData(nameof(UiTools))]
    public void Ui_tool_without_window_is_denied_there_is_no_foreground_fallback(string tool)
    {
        Assert.False(Machine.Policy().CheckUi(tool, null, Machine.ResolveElement).Allowed);
        Assert.False(Machine.Policy().CheckUi(tool, " ", Machine.ResolveElement).Allowed);
    }

    [Theory]
    [MemberData(nameof(UiTools))]
    public void Element_from_foreign_or_unknown_window_is_denied_even_through_own_window(string tool)
    {
        var policy = Machine.Policy();

        Assert.True(policy.CheckUi(tool, Machine.Own, Machine.ResolveElement, "own", null, "").Allowed);
        Assert.False(policy.CheckUi(tool, Machine.Own, Machine.ResolveElement, "foreign").Allowed);
        Assert.False(policy.CheckUi(tool, Machine.Own, Machine.ResolveElement, "own", "unknown").Allowed);
    }

    // ---------- window_management ----------

    [Theory]
    [MemberData(nameof(HandleActions))]
    public void Handle_action_needs_own_window(string action)
    {
        var policy = Machine.Policy();

        Assert.True(policy.CheckWindowAction(action, Machine.Own).Allowed);
        Assert.False(policy.CheckWindowAction(action, Machine.ChildOutsideList).Allowed);
        Assert.False(policy.CheckWindowAction(action, Machine.UserNotepad).Allowed);
        Assert.False(policy.CheckWindowAction(action, null).Allowed);
    }

    [Fact]
    public void Title_search_across_the_desktop_is_denied()
    {
        var decision = Machine.Policy().CheckWindowAction("wait_for", null);

        Assert.False(decision.Allowed);
        Assert.Contains("list", decision.Reason);
    }

    [Theory]
    [InlineData("list")]
    [InlineData("find")]
    [InlineData("get_foreground")]
    public void Listing_actions_pass_and_are_filtered_by_result(string action)
    {
        Assert.True(Machine.Policy().CheckWindowAction(action, null).Allowed);
    }

    [Fact]
    public void Unknown_action_is_denied()
    {
        Assert.False(Machine.Policy().CheckWindowAction("send_keys", Machine.Own).Allowed);
    }

    [Fact]
    public void List_and_find_return_only_own_windows()
    {
        var all = new[] { Machine.Own, Machine.ChildOutsideList, Machine.UserNotepad, Machine.ForbiddenInJob, Machine.Missing, "garbage" };

        var own = Machine.Policy().FilterOwn(all, h => h);

        Assert.Equal([Machine.Own], own);
    }

    [Fact]
    public void Foreground_details_are_shown_only_for_own_window()
    {
        var policy = Machine.Policy();

        Assert.True(policy.CheckForegroundResult(Machine.Own).Allowed);
        Assert.False(policy.CheckForegroundResult(Machine.UserNotepad).Allowed);
        Assert.False(policy.CheckForegroundResult(null).Allowed);
    }

    // ---------- screenshot_control ----------

    [Theory]
    [InlineData(null)]
    [InlineData("primary_screen")]
    [InlineData("secondary_screen")]
    [InlineData("monitor")]
    [InlineData("region")]
    [InlineData("all_monitors")]
    [InlineData("primary")]
    [InlineData("allmonitors")]
    public void Screen_targets_are_denied_even_with_own_handle(string? target)
    {
        var decision = Machine.Policy().CheckScreenshot("capture", target, Machine.Own, null);

        Assert.False(decision.Allowed);
        Assert.Contains("target='window'", decision.Reason);
    }

    [Theory]
    [InlineData("window")]
    [InlineData("WINDOW")]
    public void Own_window_capture_is_allowed(string target)
    {
        Assert.True(Machine.Policy().CheckScreenshot(null, target, Machine.Own, null).Allowed);
        Assert.True(Machine.Policy().CheckScreenshot("capture", target, Machine.Own, "inline").Allowed);
    }

    [Fact]
    public void Foreign_or_missing_window_capture_is_denied()
    {
        var policy = Machine.Policy();

        Assert.False(policy.CheckScreenshot("capture", "window", Machine.UserNotepad, null).Allowed);
        Assert.False(policy.CheckScreenshot("capture", "window", Machine.ChildOutsideList, null).Allowed);
        Assert.False(policy.CheckScreenshot("capture", "window", null, null).Allowed);
    }

    [Fact]
    public void Capture_to_file_is_denied()
    {
        Assert.False(Machine.Policy().CheckScreenshot("capture", "window", Machine.Own, "file").Allowed);
    }

    [Fact]
    public void Monitor_list_carries_no_pixels_and_is_allowed()
    {
        Assert.True(Machine.Policy().CheckScreenshot("list_monitors", null, null, null).Allowed);
        Assert.False(Machine.Policy().CheckScreenshot("record", "window", Machine.Own, null).Allowed);
    }
}
