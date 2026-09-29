using System.ComponentModel;
using System.Runtime.Versioning;
using ClaudeHomeServer.HandsBridge.Browser;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Sbroenne.WindowsMcp.Tools;

/// <summary>
/// Браузерная рука (ADR-016 §7.1): снимок видимой части вкладки — только в ответ. Без зрения у
/// модели инструмент выключает агент (<c>HandsVision.ImageTools</c>).
/// </summary>
[McpServerToolType]
[SupportedOSPlatform("windows")]
public static partial class BrowserScreenshotTool
{
    /// <summary>
    /// Take a PNG screenshot of the visible part of the current browser tab.
    /// Keywords: browser, screenshot, see page, look, image, visual.
    /// </summary>
    /// <remarks>
    /// Use only when you need to see how the page looks (layout, images, charts); for text and element refs
    /// browser_snapshot is far cheaper. The image is returned in the response and never written to disk.
    /// </remarks>
    /// <param name="outputPath">Not supported: screenshots are never written to disk. Omit it.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The screenshot image plus the tab's URL and title.</returns>
    [McpServerTool(Name = "browser_screenshot", Title = "Browser: Screenshot", ReadOnly = true, OpenWorld = false)]
    public static async partial Task<CallToolResult> ExecuteAsync(
        [DefaultValue(null)] string? outputPath,
        CancellationToken cancellationToken)
    {
        var gate = HandsPolicy.CheckBrowserScreenshot(outputPath);
        if (!gate.Allowed)
        {
            return HandsGate.Deny(HandsTools.BrowserScreenshot, gate);
        }

        HandsGate.Acted(HandsTools.BrowserScreenshot);

        var reply = await BrowserHost.Session.ScreenshotAsync(cancellationToken);
        return BrowserToolResult.From(HandsTools.BrowserScreenshot, reply);
    }
}
