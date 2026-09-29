using System.ComponentModel;
using System.Runtime.Versioning;
using ClaudeHomeServer.HandsBridge.Browser;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Sbroenne.WindowsMcp.Tools;

/// <summary>
/// Браузерная рука (ADR-016 §7.1): компактный снимок дерева доступности текущей вкладки со ссылками.
/// </summary>
[McpServerToolType]
[SupportedOSPlatform("windows")]
public static partial class BrowserSnapshotTool
{
    // Генератор описаний из XML не понимает параметр @ref, поэтому описания — атрибутами
    [Description(
        "Read the current browser tab as a compact accessibility tree: one line per element like " +
        "'- button \"Sign in\" [ref=e12]'. Interactive elements get refs for browser_click and browser_type. " +
        "browser_navigate, browser_click, browser_type and browser_tabs(new/select) already return a short snapshot " +
        "with fresh refs: call this only when that one was cut, when the page changed on its own, or for a subtree. " +
        "Refs live until the next snapshot, navigation, tab switch or browser restart; a new snapshot replaces all earlier refs. " +
        "Large pages are cut to a size budget; pass ref of a container element to read just its subtree. " +
        "Prefer this over browser_screenshot whenever text is enough.")]
    [McpServerTool(Name = "browser_snapshot", Title = "Browser: Read Page", ReadOnly = true, OpenWorld = false)]
    public static async Task<CallToolResult> ExecuteAsync(
        [Description("Optional ref of an element from the previous snapshot: read only its subtree.")] [DefaultValue(null)] string? @ref,
        CancellationToken cancellationToken)
    {
        var gate = HandsPolicy.CheckBrowserSnapshot(@ref, BrowserHost.Session.ResolveRef);
        if (!gate.Allowed)
        {
            return HandsGate.Deny(HandsTools.BrowserSnapshot, gate);
        }

        HandsGate.Acted(HandsTools.BrowserSnapshot);

        var reply = await BrowserHost.Session.SnapshotAsync(@ref, cancellationToken);
        return BrowserToolResult.From(HandsTools.BrowserSnapshot, reply);
    }
}
