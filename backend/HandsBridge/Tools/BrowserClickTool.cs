using System.ComponentModel;
using System.Runtime.Versioning;
using ClaudeHomeServer.HandsBridge.Browser;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Sbroenne.WindowsMcp.Tools;

/// <summary>
/// Браузерная рука (ADR-016 §7.1): клик по элементу из снимка событиями CDP, мышь человека не двигается.
/// </summary>
[McpServerToolType]
[SupportedOSPlatform("windows")]
public static partial class BrowserClickTool
{
    // Генератор описаний из XML не понимает параметр @ref, поэтому описания — атрибутами
    [Description(
        "Click an element of the current browser tab by its ref from browser_snapshot. " +
        "The click is delivered inside the browser: the user's mouse does not move and Chrome does not need to be in front. " +
        "The element is scrolled into view and clicked at its center. If something covers it, nothing happens: " +
        "check with a fresh browser_snapshot. After a click that changes the page, take a new snapshot before using refs.")]
    [McpServerTool(Name = "browser_click", Title = "Browser: Click", Destructive = true, OpenWorld = true)]
    public static async Task<CallToolResult> ExecuteAsync(
        [Description("Element ref from the latest browser_snapshot, like e12.")] string @ref,
        CancellationToken cancellationToken)
    {
        var gate = HandsPolicy.CheckBrowserRef(HandsTools.BrowserClick, @ref, BrowserHost.Session.ResolveRef);
        if (!gate.Allowed)
        {
            return HandsGate.Deny(HandsTools.BrowserClick, gate);
        }

        var reply = await BrowserHost.Session.ClickAsync(@ref, cancellationToken);
        return BrowserToolResult.From(HandsTools.BrowserClick, reply);
    }
}
