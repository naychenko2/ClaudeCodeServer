using System.ComponentModel;
using System.Runtime.Versioning;
using ClaudeHomeServer.HandsBridge.Browser;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Sbroenne.WindowsMcp.Tools;

/// <summary>
/// Браузерная рука (ADR-016 §7.1): ожидание текста на странице или пауза, с потолком из гейта.
/// </summary>
[McpServerToolType]
[SupportedOSPlatform("windows")]
public static partial class BrowserWaitTool
{
    /// <summary>
    /// Wait until a text appears on the current browser tab, or pause for a number of milliseconds.
    /// Keywords: browser, wait, loading, appear, delay, pause.
    /// </summary>
    /// <remarks>
    /// Do not call it after browser_navigate, browser_click or browser_type: they already wait for the page to load
    /// and return a fresh snapshot. To wait for content that appears later, pass text: the page's accessibility tree is
    /// checked repeatedly until an element name or value contains the text (case-insensitive), up to ms milliseconds
    /// (default 10000, at most 30000). Without text, ms is a plain pause capped at 2000.
    /// </remarks>
    /// <param name="text">Text to wait for on the page.</param>
    /// <param name="ms">Timeout for text (1-30000), or pause length without text (capped at 2000), in milliseconds.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Whether the text appeared, or confirmation of the pause.</returns>
    [McpServerTool(Name = "browser_wait", Title = "Browser: Wait", ReadOnly = true, OpenWorld = false)]
    public static async partial Task<CallToolResult> ExecuteAsync(
        [DefaultValue(null)] string? text,
        [DefaultValue(null)] int? ms,
        CancellationToken cancellationToken)
    {
        var gate = HandsPolicy.CheckBrowserWait(text, ms);
        if (!gate.Allowed)
        {
            return HandsGate.Deny(HandsTools.BrowserWait, gate);
        }

        HandsGate.Acted(HandsTools.BrowserWait);

        var reply = await BrowserHost.Session.WaitAsync(text, ms, cancellationToken);
        return BrowserToolResult.From(HandsTools.BrowserWait, reply);
    }
}
