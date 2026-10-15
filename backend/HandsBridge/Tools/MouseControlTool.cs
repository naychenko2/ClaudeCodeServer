using System.ComponentModel;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Sbroenne.WindowsMcp.Native;

namespace Sbroenne.WindowsMcp.Tools;

/// <summary>
/// MCP tool for controlling the mouse cursor on Windows.
/// </summary>
[McpServerToolType]
[SupportedOSPlatform("windows")]
public static partial class MouseControlTool
{
    /// <summary>
    /// Mouse actions exposed by this bridge build (D2b-1). The full upstream set
    /// (<c>drag</c>, <c>polyline</c>, <c>scroll</c>) is not in the schema yet; it is widened in
    /// D2b-2. The schema is static and does not depend on the turn.
    /// </summary>
    public enum MouseControlAction
    {
        /// <summary>Move the cursor to (x, y).</summary>
        [JsonStringEnumMemberName("move")] Move,

        /// <summary>Left mouse button click at (x, y).</summary>
        [JsonStringEnumMemberName("click")] Click,

        /// <summary>Double left-click at (x, y).</summary>
        [JsonStringEnumMemberName("double_click")] DoubleClick,

        /// <summary>Right mouse button click at (x, y).</summary>
        [JsonStringEnumMemberName("right_click")] RightClick,

        /// <summary>Middle mouse button click at (x, y).</summary>
        [JsonStringEnumMemberName("middle_click")] MiddleClick,

        /// <summary>Read the current cursor position; takes no point and does not lock the hands.</summary>
        [JsonStringEnumMemberName("get_position")] GetPosition,
    }

    /// <summary>
    /// Click and move the mouse by screen coordinates in windows that expose no UI Automation elements
    /// (remote desktops, canvases). Use ui_click when ui_snapshot shows elements. Take a fresh screenshot
    /// before every click; coordinates in the screenshot are not screen coordinates if it was scaled.
    /// </summary>
    /// <param name="action">The mouse action: move, click, double_click, right_click, middle_click, or get_position.</param>
    /// <param name="x">X coordinate in screen pixels (required for move and the click actions; ignored by get_position).</param>
    /// <param name="y">Y coordinate in screen pixels (required for move and the click actions; ignored by get_position).</param>
    /// <param name="windowHandle">Optional window handle (decimal string from window_management). When set, the root window under the point must be this one; input is also aborted if it loses the foreground.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A call result with the cursor position and, for click actions, the window that received the input. <c>IsError</c> reflects success.</returns>
    [McpServerTool(Name = "mouse_control", Title = "Mouse Control", Destructive = true, OpenWorld = false)]
    public static async partial Task<CallToolResult> ExecuteAsync(
        MouseControlAction action,
        [DefaultValue(null)] int? x,
        [DefaultValue(null)] int? y,
        [DefaultValue(null)] string? windowHandle,
        CancellationToken cancellationToken)
    {
        var actionName = JsonNamingPolicy.SnakeCaseLower.ConvertName(action.ToString());

        // Gate: the point must be inside the virtual desktop and over a window that accepts input
        // (and, when windowHandle is set, that window must be the one under the point).
        long? expectedRoot = null;
        if (!string.IsNullOrWhiteSpace(windowHandle))
        {
            if (!WindowHandleParser.TryParse(windowHandle, out var parsedHandle) || parsedHandle == IntPtr.Zero)
            {
                return ToCallToolResult(MouseControlResult.CreateFailure(
                    MouseControlErrorCode.InvalidCoordinates,
                    $"Invalid windowHandle: '{windowHandle}'. Expected a decimal string from window_management."));
            }

            expectedRoot = parsedHandle.ToInt64();
        }

        var points = action == MouseControlAction.GetPosition
            ? Array.Empty<(int X, int Y)>()
            : (x.HasValue && y.HasValue)
                ? new[] { (x.Value, y.Value) }
                : Array.Empty<(int X, int Y)>();

        var gate = HandsGate.Policy.CheckPointer(actionName, points, expectedRoot, amount: 0);
        if (!gate.Allowed)
        {
            return HandsGate.Deny(HandsTools.MouseControl, gate);
        }

        // First action of the turn that passes the gate raises the activity signal; get_position only
        // reads the cursor, so TouchesMachine keeps the indicator off for it (see HandsActivity).
        if (HandsGate.Acted(HandsTools.MouseControl, actionName) is { } busy)
        {
            return busy;
        }

        try
        {
            // The internal overloads take a non-nullable handle; without windowHandle we fall back to the
            // foreground window so the input is still guarded against a window that loses focus mid-action.
            var target = ResolveTargetWindow(windowHandle);

            var result = action switch
            {
                MouseControlAction.Move => await HandleMoveAsync(x, y, target, cancellationToken),
                MouseControlAction.Click => await HandleClickAsync(x, y, target, cancellationToken),
                MouseControlAction.DoubleClick => await HandleDoubleClickAsync(x, y, target, cancellationToken),
                MouseControlAction.RightClick => await HandleRightClickAsync(x, y, target, cancellationToken),
                MouseControlAction.MiddleClick => await HandleMiddleClickAsync(x, y, target, cancellationToken),
                MouseControlAction.GetPosition => GetCurrentPosition(),
                _ => MouseControlResult.CreateFailure(
                    MouseControlErrorCode.InvalidAction,
                    $"Unknown action: '{actionName}'"),
            };

            return ToCallToolResult(result);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ErrorResult(WindowsToolsBase.SerializeToolError("mouse_control", ex));
        }
    }

    /// <summary>
    /// Resolves the window the input is guarded against: the explicit <paramref name="windowHandle"/> when
    /// given, otherwise the current foreground window (so a click is still aborted if that window loses focus).
    /// </summary>
    private static nint ResolveTargetWindow(string? windowHandle)
    {
        if (!string.IsNullOrWhiteSpace(windowHandle) && WindowHandleParser.TryParse(windowHandle, out var handle)
            && handle != IntPtr.Zero)
        {
            return handle;
        }

        return NativeMethods.GetForegroundWindow();
    }

    private static Task<MouseControlResult> HandleMoveAsync(int? x, int? y, nint window, CancellationToken cancellationToken)
    {
        if (!x.HasValue || !y.HasValue)
        {
            return Task.FromResult(MouseControlResult.CreateFailure(
                MouseControlErrorCode.MissingRequiredParameter,
                "Move action requires both x and y coordinates."));
        }

        return WindowsToolsBase.MouseInputService.MoveAsync(x.Value, y.Value, window, cancellationToken);
    }

    private static Task<MouseControlResult> HandleClickAsync(int? x, int? y, nint window, CancellationToken cancellationToken) =>
        WindowsToolsBase.MouseInputService.ClickAsync(x, y, ModifierKey.None, window, cancellationToken);

    private static Task<MouseControlResult> HandleDoubleClickAsync(int? x, int? y, nint window, CancellationToken cancellationToken) =>
        WindowsToolsBase.MouseInputService.DoubleClickAsync(x, y, ModifierKey.None, window, cancellationToken);

    private static Task<MouseControlResult> HandleRightClickAsync(int? x, int? y, nint window, CancellationToken cancellationToken) =>
        WindowsToolsBase.MouseInputService.RightClickAsync(x, y, ModifierKey.None, window, cancellationToken);

    private static Task<MouseControlResult> HandleMiddleClickAsync(int? x, int? y, nint window, CancellationToken cancellationToken) =>
        WindowsToolsBase.MouseInputService.MiddleClickAsync(x, y, window, cancellationToken);

    private static MouseControlResult GetCurrentPosition()
    {
        var position = Coordinates.FromCurrent();
        return MouseControlResult.CreateSuccess(position);
    }

    /// <summary>
    /// Converts a mouse control result into an MCP call result. <see cref="CallToolResult.IsError"/>
    /// mirrors <see cref="MouseControlResult.Success"/>.
    /// </summary>
    private static CallToolResult ToCallToolResult(MouseControlResult result) =>
        new()
        {
            Content = [new TextContentBlock { Text = JsonSerializer.Serialize(result, WindowsToolsBase.JsonOptions) }],
            IsError = !result.Success
        };

    /// <summary>
    /// Wraps a pre-serialized JSON error payload in a failed call result.
    /// </summary>
    private static CallToolResult ErrorResult(string json) =>
        new()
        {
            Content = [new TextContentBlock { Text = json }],
            IsError = true
        };
}
