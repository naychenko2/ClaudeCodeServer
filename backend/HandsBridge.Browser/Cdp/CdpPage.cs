using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClaudeHomeServer.HandsBridge.Browser.Cdp;

/// <summary>Итог перехода: <see cref="ErrorText"/> — отказ сети или адреса, <see cref="Loaded"/> — дождались load.</summary>
public sealed record CdpNavigation(string FrameId, string? ErrorText, bool Loaded);

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

    /// <summary>Включить домен Page и события жизненного цикла — без них load не придёт.</summary>
    public async Task EnableAsync(CancellationToken cancellationToken = default)
    {
        await Send("Page.enable", null, cancellationToken);
        await Send("Page.setLifecycleEventsEnabled", new JsonObject { ["enabled"] = true }, cancellationToken);
    }

    /// <summary>
    /// Перейти и дождаться <c>Page.loadEventFired</c> не дольше <paramref name="loadTimeout"/>.
    /// Не дождались — не ошибка: страница может грузиться дольше, <see cref="CdpNavigation.Loaded"/> = false.
    /// Переход внутри документа (якорь) load не даёт: у него нет loaderId.
    /// </summary>
    public async Task<CdpNavigation> NavigateAsync(string url, TimeSpan loadTimeout,
        CancellationToken cancellationToken = default)
    {
        using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var load = Connection.WaitForEventAsync("Page.loadEventFired", SessionId, loadTimeout,
            cancellationToken: waitCts.Token);
        try
        {
            var r = await Send("Page.navigate", new JsonObject { ["url"] = url }, cancellationToken);
            var frameId = CdpBrowser.Str(r, "frameId");
            var error = CdpBrowser.Str(r, "errorText");
            if (error.Length > 0) return new CdpNavigation(frameId, error, false);
            if (CdpBrowser.Str(r, "loaderId").Length == 0) return new CdpNavigation(frameId, null, true);
            try
            {
                await load;
                return new CdpNavigation(frameId, null, true);
            }
            catch (CdpTimeoutException)
            {
                return new CdpNavigation(frameId, null, false);
            }
        }
        finally
        {
            waitCts.Cancel();
            try { await load; } catch { /* ожидание снято, исход уже не важен */ }
        }
    }

    /// <summary>Сырой ответ <c>Accessibility.getFullAXTree</c> (массив <c>nodes</c>) — вход сжатия снимка.</summary>
    public Task<JsonElement> GetFullAXTreeAsync(CancellationToken cancellationToken = default) =>
        Send("Accessibility.getFullAXTree", null, cancellationToken);

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
