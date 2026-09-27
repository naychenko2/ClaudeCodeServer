using ClaudeHomeServer.HandsBridge.Policy;
using Xunit;

namespace HandsBridge.Policy.Tests;

/// <summary>
/// <c>ui_*</c>, <c>window_management</c> и <c>screenshot_control</c>. Границы «только свои окна»
/// нет — снята решением владельца 2026-09-27 (ADR-016 §7): тесты, требовавшие отказа на чужом
/// окне и на снимке экрана, переписаны на «проходит». Остались запрет ввода в окна
/// интерпретаторов, терминалов и Проводника и запрет записи снимка на диск.
/// </summary>
public class HandsWindowTests
{
    public static readonly TheoryData<string> UiTools =
        [HandsTools.UiSnapshot, HandsTools.UiFind, HandsTools.UiClick, HandsTools.UiType, HandsTools.UiRead];

    public static readonly TheoryData<string> InputTools = [HandsTools.UiClick, HandsTools.UiType];

    public static readonly TheoryData<string> ReadTools = [HandsTools.UiSnapshot, HandsTools.UiFind, HandsTools.UiRead];

    public static readonly TheoryData<string> WindowActions =
    [
        "list", "find", "get_foreground", "wait_for",
        "activate", "minimize", "maximize", "restore", "close", "move", "resize", "set_bounds",
        "move_to_monitor", "get_state", "wait_for_state", "move_and_activate", "ensure_visible",
    ];

    /// <summary>Окна, в которые руки не вводят: терминалы, интерпретаторы, Проводник, неизвестное.</summary>
    private static readonly string[] NoInputWindows =
        [Machine.UserCmd, Machine.UserTerminal, Machine.UserPwsh, Machine.ExplorerWindow, Machine.UnknownImage, Machine.Missing];

    // ---------- ui_* ----------

    [Theory]
    [MemberData(nameof(UiTools))]
    public void Ui_tool_on_foreign_window_is_allowed(string tool)
    {
        var policy = Machine.Policy();

        foreach (var window in new[] { Machine.TurnNotepad, Machine.UserNotepad, Machine.UserMusic })
            Assert.True(policy.CheckUi(tool, window, Machine.ResolveElement).Allowed, $"{tool} on {window}");
    }

    [Theory]
    [MemberData(nameof(UiTools))]
    public void Element_of_foreign_window_is_allowed(string tool)
    {
        Assert.True(Machine.Policy().CheckUi(tool, Machine.UserNotepad, Machine.ResolveElement, "user", "turn", null, "").Allowed);
    }

    [Theory]
    [MemberData(nameof(UiTools))]
    public void Ui_tool_without_window_is_denied_there_is_no_foreground_fallback(string tool)
    {
        Assert.False(Machine.Policy().CheckUi(tool, null, Machine.ResolveElement).Allowed);
        Assert.False(Machine.Policy().CheckUi(tool, " ", Machine.ResolveElement).Allowed);
    }

    [Theory]
    [MemberData(nameof(InputTools))]
    public void Input_into_interpreter_terminal_or_explorer_window_is_denied(string tool)
    {
        var policy = Machine.Policy();

        foreach (var window in NoInputWindows)
        {
            var decision = policy.CheckUi(tool, window, Machine.ResolveElement);
            Assert.False(decision.Allowed, $"{tool} on {window}");
            Assert.Contains("never click or type", decision.Reason);
        }
    }

    [Theory]
    [MemberData(nameof(InputTools))]
    public void Input_into_terminal_element_is_denied_even_through_another_window(string tool)
    {
        var policy = Machine.Policy();

        Assert.False(policy.CheckUi(tool, Machine.UserNotepad, Machine.ResolveElement, "terminal").Allowed);
        Assert.False(policy.CheckUi(tool, Machine.UserNotepad, Machine.ResolveElement, "user", "unknown").Allowed);
    }

    [Theory]
    [MemberData(nameof(InputTools))]
    public void Input_needs_a_plain_decimal_handle(string tool)
    {
        foreach (var handle in new[] { "0x64", "+300", "300 ", "0" })
            Assert.False(Machine.Policy().CheckUi(tool, handle, Machine.ResolveElement).Allowed, handle);
    }

    [Theory]
    [MemberData(nameof(ReadTools))]
    public void Reading_interpreter_or_terminal_window_is_allowed(string tool)
    {
        var policy = Machine.Policy();

        foreach (var window in new[] { Machine.UserCmd, Machine.UserTerminal, Machine.ExplorerWindow })
            Assert.True(policy.CheckUi(tool, window, Machine.ResolveElement, "terminal").Allowed, $"{tool} on {window}");
    }

    [Fact]
    public void Accepts_input_by_program_image_case_and_slashes_do_not_matter()
    {
        var policy = Machine.Policy();

        Assert.True(policy.AcceptsInput(100));
        Assert.True(policy.AcceptsInput(310));
        Assert.False(policy.AcceptsInput(400));
        Assert.False(policy.AcceptsInput(0));
    }

    // ---------- window_management ----------

    [Theory]
    [MemberData(nameof(WindowActions))]
    public void Every_window_action_is_allowed_on_any_window(string action)
    {
        Assert.True(HandsPolicy.CheckWindowAction(action).Allowed);
    }

    [Fact]
    public void Unknown_action_is_denied()
    {
        Assert.False(HandsPolicy.CheckWindowAction("send_keys").Allowed);
    }

    // ---------- screenshot_control ----------

    /// <summary>Цель и hwnd гейт больше не принимает вовсе; что цели доходят до снимка — рантайм-сторож.</summary>
    [Fact]
    public void Capture_is_allowed_inline()
    {
        Assert.True(HandsPolicy.CheckScreenshot(null, null, null).Allowed);
        Assert.True(HandsPolicy.CheckScreenshot("capture", "inline", null).Allowed);
    }

    [Fact]
    public void Capture_to_file_is_denied()
    {
        Assert.False(HandsPolicy.CheckScreenshot("capture", "file", null).Allowed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("inline")]
    [InlineData("file")]
    public void Output_path_is_denied_in_any_mode(string? outputMode)
    {
        var decision = HandsPolicy.CheckScreenshot("capture", outputMode, @"C:\Users\an\evil.jpg");

        Assert.False(decision.Allowed);
    }

    [Fact]
    public void Monitor_list_is_allowed_unknown_action_is_denied()
    {
        Assert.True(HandsPolicy.CheckScreenshot("list_monitors", null, null).Allowed);
        Assert.False(HandsPolicy.CheckScreenshot("record", null, null).Allowed);
    }
}
