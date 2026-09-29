using System.ComponentModel;
using System.Runtime.Versioning;
using ClaudeHomeServer.HandsBridge.Browser;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Sbroenne.WindowsMcp.Tools;

/// <summary>
/// Браузерная рука (ADR-016 §7.1): чтение DOM текущей вкладки по CSS-селектору или ссылке
/// снимка — командами домена DOM, без JS страницы.
/// </summary>
[McpServerToolType]
[SupportedOSPlatform("windows")]
public static partial class BrowserQueryTool
{
    // Генератор описаний из XML не понимает параметр @ref, поэтому описания — атрибутами
    [Description(
        "Read exact content of elements of the current browser tab without running page scripts: by CSS selector, " +
        "by ref from a snapshot, or a selector inside the element of a ref. mode='text' gives the text of each element " +
        "(article body, table rows, prices; hidden text is included), mode='attributes' gives tag and attributes " +
        "(href of links, src of images, values of inputs), mode='html' gives outerHTML. " +
        "Use it instead of paging through browser_snapshot when you know which elements you need, " +
        "for example selector='a.result-title' with mode='attributes' for all result links. " +
        "Up to limit elements (default 10, max 100); the reply is capped at 20000 characters and says so when cut. " +
        "Refs of the snapshot stay valid.")]
    [McpServerTool(Name = "browser_query", Title = "Browser: Query Page", ReadOnly = true, OpenWorld = false)]
    public static async Task<CallToolResult> ExecuteAsync(
        [Description("CSS selector, like 'article', 'table.prices tr' or 'a[href*=\"download\"]'.")] [DefaultValue(null)] string? selector,
        [Description("Ref of an element from the latest snapshot, like e12: read that element, or search inside it with selector.")] [DefaultValue(null)] string? @ref,
        [Description("What to read from each element: 'text' (default), 'attributes' or 'html'.")] [DefaultValue(null)] string? mode,
        [Description("How many matching elements to return, 1-100. Default: 10.")] [DefaultValue(null)] int? limit,
        CancellationToken cancellationToken)
    {
        var gate = HandsPolicy.CheckBrowserQuery(selector, @ref, mode, limit, BrowserHost.Session.ResolveRef);
        if (!gate.Allowed)
        {
            return HandsGate.Deny(HandsTools.BrowserQuery, gate);
        }

        HandsGate.Acted(HandsTools.BrowserQuery);

        var reply = await BrowserHost.Session.QueryAsync(selector, @ref, mode, limit, cancellationToken);
        return BrowserToolResult.From(HandsTools.BrowserQuery, reply);
    }
}
