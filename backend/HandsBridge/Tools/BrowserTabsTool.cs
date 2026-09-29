using System.ComponentModel;
using System.Runtime.Versioning;
using ClaudeHomeServer.HandsBridge.Browser;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Sbroenne.WindowsMcp.Tools;

/// <summary>
/// Браузерная рука (ADR-016 §7.1): вкладки своего Chrome — чужих в профиле проекта нет.
/// </summary>
[McpServerToolType]
[SupportedOSPlatform("windows")]
public static partial class BrowserTabsTool
{
    /// <summary>
    /// List, open, select or close tabs of this project's own Chrome window.
    /// Keywords: browser, tabs, new tab, switch tab, close tab.
    /// </summary>
    /// <remarks>
    /// action='list' shows every tab with its tabId and marks the current one. action='new' opens a tab (url is optional,
    /// same address rules as browser_navigate) and makes it current. action='select' makes a tab current, action='close'
    /// closes it; both need tabId. Other browser_* tools work on the current tab; switching tabs drops snapshot refs.
    /// </remarks>
    /// <param name="action">One of: 'list', 'new', 'select', 'close'.</param>
    /// <param name="url">Address for action='new': http://, https:// or about:blank. Default: about:blank.</param>
    /// <param name="tabId">Tab id from action='list' for 'select' and 'close'.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The tab list or the result of the action.</returns>
    [McpServerTool(Name = "browser_tabs", Title = "Browser: Tabs", Destructive = true, OpenWorld = true)]
    public static async partial Task<CallToolResult> ExecuteAsync(
        string action,
        [DefaultValue(null)] string? url,
        [DefaultValue(null)] string? tabId,
        CancellationToken cancellationToken)
    {
        var gate = HandsPolicy.CheckBrowserTabs(action, url, tabId);
        if (!gate.Allowed)
        {
            return HandsGate.Deny(HandsTools.BrowserTabs, gate.Reason!);
        }

        HandsGate.Acted(HandsTools.BrowserTabs);

        var reply = await BrowserHost.Session.TabsAsync(action, gate.Url, tabId, cancellationToken);
        return BrowserToolResult.From(HandsTools.BrowserTabs, reply);
    }
}
