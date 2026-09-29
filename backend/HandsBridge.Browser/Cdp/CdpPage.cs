using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClaudeHomeServer.HandsBridge.Browser.Cdp;

/// <summary>
/// Итог перехода: <see cref="ErrorText"/> — отказ сети или адреса, <see cref="Loaded"/> — DOM
/// документа готов (DOMContentLoaded), <see cref="Settled"/> — страница ещё и затихла.
/// </summary>
public sealed record CdpNavigation(string FrameId, string? ErrorText, bool Loaded, bool Settled = false);

/// <summary>Диалог страницы (alert, confirm, prompt, beforeunload).</summary>
public sealed record CdpDialog(string Type, string Message, string Url);

/// <summary>
/// Команды одной вкладки по сессии flatten. Узлы адресуются <c>backendNodeId</c> — его отдаёт
/// дерево доступности, отдельный обход DOM не нужен.
/// </summary>
public sealed class CdpPage(CdpConnection connection, string targetId, string sessionId)
{
    public CdpConnection Connection { get; } = connection;
    public string TargetId { get; } = targetId;
    public string SessionId { get; } = sessionId;

    /// <summary>
    /// Главный фрейм вкладки — по нему ловятся переходы после клика. До <see cref="EnableAsync"/>
    /// (и если браузер дерево фреймов не отдал) — id цели: у Chrome они совпадают.
    /// </summary>
    public string MainFrameId { get; private set; } = targetId;

    /// <summary>
    /// Включить домен Page и события жизненного цикла — без них DOMContentLoaded не придёт — и
    /// узнать главный фрейм.
    /// </summary>
    public async Task EnableAsync(CancellationToken cancellationToken = default)
    {
        await Send("Page.enable", null, cancellationToken);
        await Send("Page.setLifecycleEventsEnabled", new JsonObject { ["enabled"] = true }, cancellationToken);
        var tree = await Send("Page.getFrameTree", null, cancellationToken);
        if (tree.TryGetProperty("frameTree", out var node) && node.TryGetProperty("frame", out var frame) &&
            CdpBrowser.Str(frame, "id") is { Length: > 0 } id)
            MainFrameId = id;
    }

    /// <summary>Начать наблюдение за загрузкой: зови ДО команды, которая вызывает переход.</summary>
    public CdpLoadWatch WatchLoad() => new(Connection, SessionId);

    /// <summary>
    /// Перейти и дождаться DOMContentLoaded своего документа не дольше <paramref name="loadTimeout"/>,
    /// затем затишья не дольше <paramref name="settle"/> (<see cref="CdpLoadWatch"/>).
    /// Не дождались — не ошибка: страница может грузиться дольше, <see cref="CdpNavigation.Loaded"/> = false.
    /// Переход внутри документа (якорь) загрузки не даёт: у него нет loaderId.
    /// </summary>
    public async Task<CdpNavigation> NavigateAsync(string url, TimeSpan loadTimeout, TimeSpan settle,
        CancellationToken cancellationToken = default)
    {
        using var watch = WatchLoad();
        var r = await Send("Page.navigate", new JsonObject { ["url"] = url }, cancellationToken);
        var frameId = CdpBrowser.Str(r, "frameId");
        var error = CdpBrowser.Str(r, "errorText");
        if (error.Length > 0)
            return new CdpNavigation(frameId, error, false);
        var loaderId = CdpBrowser.Str(r, "loaderId");
        if (loaderId.Length == 0)
            return new CdpNavigation(frameId, null, true, true);

        var state = await watch.WaitForLoadAsync(frameId, loaderId, loadTimeout, settle, cancellationToken);
        return new CdpNavigation(frameId, null, state != CdpLoadState.NotLoaded, state == CdpLoadState.Settled);
    }

    /// <summary>Сырой ответ <c>Accessibility.getFullAXTree</c> (массив <c>nodes</c>) — вход сжатия снимка.</summary>
    public Task<JsonElement> GetFullAXTreeAsync(CancellationToken cancellationToken = default) =>
        Send("Accessibility.getFullAXTree", null, cancellationToken);

    // ---------- DOM: чтение без JS ----------

    /// <summary>
    /// nodeId корня документа. Каждый вызов раздаёт nodeId заново, прежние гаснут — поэтому
    /// между запросами nodeId не храним, ссылки снимка держатся на <c>backendNodeId</c>.
    /// </summary>
    public async Task<int> GetDocumentAsync(CancellationToken cancellationToken = default)
    {
        var r = await Send("DOM.getDocument", null, cancellationToken);
        return r.TryGetProperty("root", out var root) && root.TryGetProperty("nodeId", out var id) ? id.GetInt32() : 0;
    }

    /// <summary>nodeId узла по <c>backendNodeId</c> (после <see cref="GetDocumentAsync"/>); 0 — узла в документе нет.</summary>
    public async Task<int> PushBackendNodeAsync(int backendNodeId, CancellationToken cancellationToken = default)
    {
        var r = await Send("DOM.pushNodesByBackendIdsToFrontend",
            new JsonObject { ["backendNodeIds"] = new JsonArray(backendNodeId) }, cancellationToken);
        return r.TryGetProperty("nodeIds", out var ids) && ids.ValueKind == JsonValueKind.Array && ids.GetArrayLength() > 0
            ? ids[0].GetInt32()
            : 0;
    }

    /// <summary>Узлы по CSS-селектору внутри <paramref name="nodeId"/>, в порядке документа.</summary>
    public async Task<int[]> QuerySelectorAllAsync(int nodeId, string selector, CancellationToken cancellationToken = default)
    {
        var r = await Send("DOM.querySelectorAll", new JsonObject { ["nodeId"] = nodeId, ["selector"] = selector }, cancellationToken);
        return r.TryGetProperty("nodeIds", out var ids) && ids.ValueKind == JsonValueKind.Array
            ? [.. ids.EnumerateArray().Select(i => i.GetInt32())]
            : [];
    }

    public async Task<string> GetOuterHtmlAsync(int nodeId, CancellationToken cancellationToken = default) =>
        CdpBrowser.Str(await Send("DOM.getOuterHTML", new JsonObject { ["nodeId"] = nodeId }, cancellationToken), "outerHTML");

    /// <summary>Имя элемента и его атрибуты парами в порядке разметки.</summary>
    public async Task<(string Name, IReadOnlyList<KeyValuePair<string, string>> Attributes)> DescribeNodeAsync(
        int nodeId, CancellationToken cancellationToken = default)
    {
        var r = await Send("DOM.describeNode", new JsonObject { ["nodeId"] = nodeId }, cancellationToken);
        if (!r.TryGetProperty("node", out var node))
            return ("", []);
        var name = CdpBrowser.Str(node, "localName") is { Length: > 0 } local ? local : CdpBrowser.Str(node, "nodeName");
        var attributes = new List<KeyValuePair<string, string>>();
        if (node.TryGetProperty("attributes", out var flat) && flat.ValueKind == JsonValueKind.Array)
        {
            var items = flat.EnumerateArray().Select(a => a.GetString() ?? "").ToArray();
            for (var i = 0; i + 1 < items.Length; i += 2)
                attributes.Add(new(items[i], items[i + 1]));
        }
        return (name, attributes);
    }

    public Task ScrollIntoViewAsync(int backendNodeId, CancellationToken cancellationToken = default) =>
        Send("DOM.scrollIntoViewIfNeeded", Node(backendNodeId), cancellationToken);

    /// <summary>Контентный четырёхугольник узла: 8 чисел x1,y1..x4,y4 в CSS-пикселях окна.</summary>
    public async Task<double[]> GetContentQuadAsync(int backendNodeId, CancellationToken cancellationToken = default)
    {
        var r = await Send("DOM.getBoxModel", Node(backendNodeId), cancellationToken);
        if (!r.TryGetProperty("model", out var model) || !model.TryGetProperty("content", out var content)
            || content.ValueKind != JsonValueKind.Array)
            throw new CdpException("DOM.getBoxModel returned no content quad");
        return [.. content.EnumerateArray().Select(v => v.GetDouble())];
    }

    public static (double X, double Y) QuadCenter(IReadOnlyList<double> quad)
    {
        if (quad.Count < 8) throw new ArgumentException("A quad has 8 coordinates", nameof(quad));
        return ((quad[0] + quad[2] + quad[4] + quad[6]) / 4, (quad[1] + quad[3] + quad[5] + quad[7]) / 4);
    }

    public Task FocusAsync(int backendNodeId, CancellationToken cancellationToken = default) =>
        Send("DOM.focus", Node(backendNodeId), cancellationToken);

    public Task DispatchMouseEventAsync(string type, double x, double y, string button = "left",
        int clickCount = 1, CancellationToken cancellationToken = default) =>
        Send("Input.dispatchMouseEvent", new JsonObject
        {
            ["type"] = type,
            ["x"] = x,
            ["y"] = y,
            ["button"] = button,
            ["clickCount"] = clickCount,
        }, cancellationToken);

    /// <summary>Клик левой кнопкой по точке окна — событиями CDP, мышь человека не двигается.</summary>
    public async Task ClickAsync(double x, double y, CancellationToken cancellationToken = default)
    {
        await DispatchMouseEventAsync("mouseMoved", x, y, "none", 0, cancellationToken);
        await DispatchMouseEventAsync("mousePressed", x, y, cancellationToken: cancellationToken);
        await DispatchMouseEventAsync("mouseReleased", x, y, cancellationToken: cancellationToken);
    }

    public Task InsertTextAsync(string text, CancellationToken cancellationToken = default) =>
        Send("Input.insertText", new JsonObject { ["text"] = text }, cancellationToken);

    public Task DispatchKeyEventAsync(string type, string key, string code, int windowsVirtualKeyCode,
        string? text = null, CancellationToken cancellationToken = default)
    {
        var p = new JsonObject
        {
            ["type"] = type,
            ["key"] = key,
            ["code"] = code,
            ["windowsVirtualKeyCode"] = windowsVirtualKeyCode,
        };
        if (text is not null) p["text"] = text;
        return Send("Input.dispatchKeyEvent", p, cancellationToken);
    }

    public async Task PressEnterAsync(CancellationToken cancellationToken = default)
    {
        await DispatchKeyEventAsync("keyDown", "Enter", "Enter", 13, "\r", cancellationToken);
        await DispatchKeyEventAsync("keyUp", "Enter", "Enter", 13, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Выделить весь текст фокусного поля командой редактора <c>selectAll</c> (как Ctrl+A) —
    /// следующая вставка заменит прежний текст, JS страницы не нужен.
    /// </summary>
    public async Task SelectAllAsync(CancellationToken cancellationToken = default)
    {
        const int ctrl = 2;
        await Send("Input.dispatchKeyEvent", new JsonObject
        {
            ["type"] = "rawKeyDown",
            ["key"] = "a",
            ["code"] = "KeyA",
            ["windowsVirtualKeyCode"] = 65,
            ["modifiers"] = ctrl,
            ["commands"] = new JsonArray("selectAll"),
        }, cancellationToken);
        await DispatchKeyEventAsync("keyUp", "a", "KeyA", 65, cancellationToken: cancellationToken);
    }

    /// <summary>Снимок видимой области в PNG — только в память, файлом не пишется.</summary>
    public async Task<byte[]> CaptureScreenshotAsync(CancellationToken cancellationToken = default)
    {
        var r = await Send("Page.captureScreenshot", new JsonObject { ["format"] = "png" }, cancellationToken);
        var data = CdpBrowser.Str(r, "data");
        if (data.Length == 0) throw new CdpException("Page.captureScreenshot returned no image");
        return Convert.FromBase64String(data);
    }

    /// <summary>Подписка на открытие диалога: пока он открыт, страница заблокирована.</summary>
    public IDisposable OnJavaScriptDialog(Action<CdpDialog> handler) =>
        Connection.Subscribe("Page.javascriptDialogOpening", SessionId, e => handler(new CdpDialog(
            CdpBrowser.Str(e.Params, "type"), CdpBrowser.Str(e.Params, "message"), CdpBrowser.Str(e.Params, "url"))));

    public Task HandleJavaScriptDialogAsync(bool accept, string? promptText = null,
        CancellationToken cancellationToken = default)
    {
        var p = new JsonObject { ["accept"] = accept };
        if (promptText is not null) p["promptText"] = promptText;
        return Send("Page.handleJavaScriptDialog", p, cancellationToken);
    }

    Task<JsonElement> Send(string method, JsonObject? parameters, CancellationToken cancellationToken) =>
        Connection.SendAsync(method, parameters, SessionId, cancellationToken: cancellationToken);

    static JsonObject Node(int backendNodeId) => new() { ["backendNodeId"] = backendNodeId };
}
