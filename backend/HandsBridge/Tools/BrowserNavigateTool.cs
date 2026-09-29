using System.ComponentModel;
using System.Runtime.Versioning;
using ClaudeHomeServer.HandsBridge.Browser;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Sbroenne.WindowsMcp.Tools;

/// <summary>
/// Браузерная рука (ADR-016 §7.1): переход текущей вкладки своего Chrome по адресу.
/// </summary>
[McpServerToolType]
[SupportedOSPlatform("windows")]
public static partial class BrowserNavigateTool
{
    /// <summary>
    /// Open a web page in the current tab of this project's own Chrome window, wait until its content is ready and read it.
    /// Use browser_* tools for web pages instead of ui_* on a Chrome window.
    /// Keywords: browser, web, open url, go to, navigate, website, page, link.
    /// </summary>
    /// <remarks>
    /// Chrome starts on the first browser_* call in this project's own profile: logins the owner made there are available.
    /// Only http, https and about:blank addresses open: local files, browser pages, javascript: and data: URLs are refused.
    /// Downloads are blocked. Page dialogs (alert, confirm) are closed automatically and their text is reported.
    /// Waits for the page content (DOMContentLoaded) plus a short quiet, not for every ad and image.
    /// The reply already contains a short snapshot of the page with element refs (up to 6000 characters): use its refs
    /// for browser_click and browser_type directly. Call browser_snapshot only if that snapshot was cut and you need the rest.
    /// </remarks>
    /// <param name="url">Address to open: http://, https:// or about:blank.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Final URL, title and a short snapshot of the page, or the reason it could not be opened.</returns>
    [McpServerTool(Name = "browser_navigate", Title = "Browser: Open Page", Destructive = false, OpenWorld = true)]
    public static async partial Task<CallToolResult> ExecuteAsync(
        string url,
        CancellationToken cancellationToken)
    {
        var gate = HandsPolicy.CheckBrowserUrl(url);
        if (!gate.Allowed)
        {
            return HandsGate.Deny(HandsTools.BrowserNavigate, gate.Reason!);
        }

        HandsGate.Acted(HandsTools.BrowserNavigate);

        // Переходим по нормализованному адресу из решения гейта, а не по присланному
        var reply = await BrowserHost.Session.NavigateAsync(gate.Url!, cancellationToken);
        return BrowserToolResult.From(HandsTools.BrowserNavigate, reply);
    }
}
