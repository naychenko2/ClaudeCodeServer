using System.ComponentModel;
using System.Runtime.Versioning;
using ClaudeHomeServer.HandsBridge.Browser;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Sbroenne.WindowsMcp.Tools;

/// <summary>
/// Браузерная рука (ADR-016 §7.1): ввод текста в поле из снимка событиями CDP, без системной клавиатуры.
/// </summary>
[McpServerToolType]
[SupportedOSPlatform("windows")]
public static partial class BrowserTypeTool
{
    // Генератор описаний из XML не понимает параметр @ref, поэтому описания — атрибутами
    [Description(
        "Type text into a field of the current browser tab by its ref from browser_snapshot, replacing the field's text. " +
        "Input is delivered inside the browser: the user's keyboard focus is not touched. Set submit=true to press Enter " +
        "after typing (for example to run a search), then take a fresh browser_snapshot to see the result.")]
    [McpServerTool(Name = "browser_type", Title = "Browser: Type", Destructive = true, OpenWorld = true)]
    public static async Task<CallToolResult> ExecuteAsync(
        [Description("Ref of a text field from the latest browser_snapshot, like e7.")] string @ref,
        [Description("Text to put into the field; it replaces what the field contained.")] string text,
        [Description("Press Enter after typing. Default: false.")] [DefaultValue(false)] bool submit,
        CancellationToken cancellationToken)
    {
        var gate = HandsPolicy.CheckBrowserRef(HandsTools.BrowserType, @ref, BrowserHost.Session.ResolveRef);
        if (!gate.Allowed)
        {
            return HandsGate.Deny(HandsTools.BrowserType, gate);
        }

        var reply = await BrowserHost.Session.TypeAsync(@ref, text ?? "", submit, cancellationToken);
        return BrowserToolResult.From(HandsTools.BrowserType, reply);
    }
}
