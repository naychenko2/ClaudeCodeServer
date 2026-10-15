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
    /// <summary>Wheel clicks when scroll is called without amount.</summary>
    private const int DefaultWheelAmount = 3;

    /// <summary>
    /// Mouse actions exposed by this bridge build (D2b-2). Upstream <c>polyline</c> is not in the
    /// schema. The schema is static and does not depend on the turn.
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

        /// <summary>Press at (x, y), move, release at (toX, toY).</summary>
        [JsonStringEnumMemberName("drag")] Drag,

        /// <summary>Wheel at (x, y): up/down use the vertical wheel, left/right the horizontal one.</summary>
        [JsonStringEnumMemberName("scroll")] Scroll,

        /// <summary>Read the current cursor position; takes no point and does not lock the hands.</summary>
        [JsonStringEnumMemberName("get_position")] GetPosition,
    }

    /// <summary>
    /// Click and move the mouse by screen coordinates in windows that expose no UI Automation elements
    /// (remote desktops, canvases). Use ui_click when ui_snapshot shows elements. Take a fresh screenshot
    /// before every click. Coordinates are SCREEN pixels; a screenshot point must be converted with the frame
    /// fields of the screenshot_control response: Screen X = screenOriginX + imageX × scaleX,
    /// Screen Y = screenOriginY + imageY × scaleY.
    /// drag: press at (x,y), move, release at (toX,toY); useful to pan canvases (holding Space via keyboard is a separate step).
    /// scroll: positive amount scrolls the content toward the direction; left/right use the horizontal wheel.
    /// </summary>
    /// <param name="action">The mouse action: move, click, double_click, right_click, middle_click, drag, scroll, or get_position.</param>
    /// <param name="x">X coordinate in screen pixels (required for move, the click actions, drag start and scroll; ignored by get_position).</param>
    /// <param name="y">Y coordinate in screen pixels (required for move, the click actions, drag start and scroll; ignored by get_position).</param>
    /// <param name="toX">End X in screen pixels (required for drag).</param>
    /// <param name="toY">End Y in screen pixels (required for drag).</param>
    /// <param name="button">Mouse button held during drag: left (default) or middle.</param>
    /// <param name="direction">Scroll direction: up, down, left, or right (required for scroll).</param>
    /// <param name="amount">Wheel clicks for scroll, 1-20 (default 3). Not allowed for other actions.</param>
    /// <param name="windowHandle">Optional window handle (decimal string from window_management). When set, the root window under the point must be this one; input is also aborted if it loses the foreground.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A call result with the cursor position and, for click actions, the window that received the input. <c>IsError</c> reflects success.</returns>
    [McpServerTool(Name = "mouse_control", Title = "Mouse Control", Destructive = true, OpenWorld = false)]
    public static async partial Task<CallToolResult> ExecuteAsync(
        MouseControlAction action,
        [DefaultValue(null)] int? x,
        [DefaultValue(null)] int? y,
        [DefaultValue(null)] int? toX,
        [DefaultValue(null)] int? toY,
        [DefaultValue(null)] string? button,
        [DefaultValue(null)] string? direction,
        [DefaultValue(null)] int? amount,
        [DefaultValue(null)] string? windowHandle,
        CancellationToken cancellationToken)
    {
        var actionName = JsonNamingPolicy.SnakeCaseLower.ConvertName(action.ToString());

        // Parameters of drag/scroll are parsed before the gate, so a malformed call never lights the activity signal.
        var mouseButton = MouseButton.Left;
        if (action == MouseControlAction.Drag && !TryParseDragButton(button, out mouseButton))
        {
            return ToCallToolResult(MouseControlResult.CreateFailure(
                MouseControlErrorCode.InvalidAction,
                $"Invalid button for drag: '{button}'. Valid values: left, middle."));
        }

        ScrollDirection scrollDirection = default;
        if (action == MouseControlAction.Scroll && !TryParseScrollDirection(direction, out scrollDirection))
        {
            return ToCallToolResult(MouseControlResult.CreateFailure(
                string.IsNullOrWhiteSpace(direction)
                    ? MouseControlErrorCode.MissingRequiredParameter
                    : MouseControlErrorCode.InvalidScrollDirection,
                $"Scroll requires direction: up, down, left, or right (got '{direction}')."));
        }

        // The wheel count belongs to scroll only (default 3); the gate refuses it for every other action
        var wheelAmount = action == MouseControlAction.Scroll ? amount ?? DefaultWheelAmount : amount ?? 0;

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

        var pointList = new List<(int X, int Y)>(2);
        if (action != MouseControlAction.GetPosition)
        {
            if (x.HasValue && y.HasValue)
            {
                pointList.Add((x.Value, y.Value));
            }

            // Drag carries the end point too: the gate checks both (the drop must not land in a forbidden window)
            if (action == MouseControlAction.Drag && toX.HasValue && toY.HasValue)
            {
                pointList.Add((toX.Value, toY.Value));
            }
        }

        var gate = HandsGate.Policy.CheckPointer(actionName, pointList, expectedRoot, wheelAmount);
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
                MouseControlAction.Drag => await HandleDragAsync(x, y, toX, toY, mouseButton, target, cancellationToken),
                MouseControlAction.Scroll => await HandleScrollAsync(x, y, scrollDirection, wheelAmount, target, cancellationToken),
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
        // Гейт (CheckPointer) — главный контракт: требует ровно одну точку. Эта проверка —
        // вторичная защита на случай, если гейт когда-нибудь изменит контракт.
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

    private static Task<MouseControlResult> HandleDragAsync(
        int? x, int? y, int? toX, int? toY, MouseButton button, nint window, CancellationToken cancellationToken)
    {
        // The gate already demands exactly two points; this is the secondary guard if its contract changes
        if (!x.HasValue || !y.HasValue || !toX.HasValue || !toY.HasValue)
        {
            return Task.FromResult(MouseControlResult.CreateFailure(
                MouseControlErrorCode.MissingRequiredParameter,
                "Drag requires x and y (start) and toX and toY (end)."));
        }

        return WindowsToolsBase.MouseInputService.DragAsync(x.Value, y.Value, toX.Value, toY.Value, button, window, cancellationToken);
    }

    private static Task<MouseControlResult> HandleScrollAsync(
        int? x, int? y, ScrollDirection direction, int amount, nint window, CancellationToken cancellationToken)
    {
        if (!x.HasValue || !y.HasValue)
        {
            return Task.FromResult(MouseControlResult.CreateFailure(
                MouseControlErrorCode.MissingRequiredParameter,
                "Scroll requires x and y: the cursor is moved there before the wheel turns."));
        }

        return WindowsToolsBase.MouseInputService.ScrollAsync(direction, amount, x, y, window, cancellationToken);
    }

    /// <summary>Drag button: left (default) or middle; right is deliberately not offered.</summary>
    private static bool TryParseDragButton(string? button, out MouseButton parsed)
    {
        parsed = MouseButton.Left;
        if (string.IsNullOrWhiteSpace(button))
        {
            return true;
        }

        switch (button.Trim().ToLowerInvariant())
        {
            case "left":
                return true;
            case "middle":
                parsed = MouseButton.Middle;
                return true;
            default:
                return false;
        }
    }

    private static bool TryParseScrollDirection(string? direction, out ScrollDirection parsed)
    {
        parsed = default;
        switch (direction?.Trim().ToLowerInvariant())
        {
            case "up": parsed = ScrollDirection.Up; return true;
            case "down": parsed = ScrollDirection.Down; return true;
            case "left": parsed = ScrollDirection.Left; return true;
            case "right": parsed = ScrollDirection.Right; return true;
            default: return false;
        }
    }

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
