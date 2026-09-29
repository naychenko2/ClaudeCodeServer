using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ClaudeHomeServer.HandsBridge.Browser.Cdp;
using ClaudeHomeServer.HandsBridge.Browser.Snapshot;

namespace ClaudeHomeServer.HandsBridge.Browser.Session;

/// <summary>
/// Состояние браузерной руки на процесс моста: текущая вкладка, таблица ссылок последнего снимка
/// и заметки для модели. Гейт (<c>HandsPolicy</c>) уже пропустил вызов — здесь только исполнение.
/// <list type="bullet">
/// <item>Клик и ввод — событиями CDP по <c>backendNodeId</c> из таблицы ссылок, системные мышь и
/// клавиатура не участвуют: окно Chrome не обязано быть на переднем плане.</item>
/// <item>Ссылки живут до следующего снимка, перехода главного фрейма, смены вкладки или
/// перезапуска браузера; после — отказ «сделай свежий снимок».</item>
/// <item>Диалоги страницы закрываются сами (иначе страница заблокирована и любое ожидание висит
/// до таймаута), их текст и перезапуск браузера доходят до модели заметкой в следующем ответе.</item>
/// </list>
/// JS страницы не исполняется ни одной командой: только навигация, дерево доступности и ввод.
/// </summary>
public sealed class BrowserSession(IBrowserSource source)
{
    /// <summary>Заметка модели после перезапуска: состояние снимка сброшено.</summary>
    public const string RestartedNotice =
        "The browser was restarted (its window had been closed): earlier snapshot refs are gone, take a fresh browser_snapshot.";

    /// <summary>Сколько <c>browser_navigate</c> ждёт события load.</summary>
    public static readonly TimeSpan LoadTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Ожидание текста без явного времени.</summary>
    public const int DefaultTextWaitMs = 10_000;

    private const int MaxDialogChars = 300;
    private const string FreshSnapshot = "Take a fresh browser_snapshot and use its refs.";

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    private readonly SemaphoreSlim _sync = new(1, 1);

    // Состояние ниже меняют и обработчики событий на потоке чтения трубы
    private readonly Lock _state = new();
    private readonly Dictionary<string, CdpPage> _pages = new(StringComparer.Ordinal);
    private readonly List<IDisposable> _subscriptions = [];
    private readonly List<string> _notices = [];
    private CdpBrowser? _browser;
    private string? _current;
    private Dictionary<string, int> _refs = new(StringComparer.Ordinal);

    /// <summary>backendNodeId по ссылке текущей вкладки; null — ссылки нет. Зовёт гейт до действия.</summary>
    public int? ResolveRef(string reference)
    {
        lock (_state)
            return _refs.TryGetValue(reference, out var node) ? node : null;
    }

    /// <summary>Переход по адресу, уже проверенному гейтом, с ожиданием load не дольше <see cref="LoadTimeout"/>.</summary>
    public Task<BrowserReply> NavigateAsync(string url, CancellationToken cancellationToken) =>
        RunAsync(async browser =>
        {
            var page = await CurrentPageAsync(browser, cancellationToken);
            ClearRefs(page.TargetId);
            var navigation = await page.NavigateAsync(url, LoadTimeout, cancellationToken);
            if (navigation.ErrorText is not null)
                return new BrowserReply($"Navigation to {url} failed: {navigation.ErrorText}", IsError: true);

            var text = await DescribeAsync(browser, page.TargetId, cancellationToken) +
                       "\nTake browser_snapshot to read the page and get element refs.";
            if (!navigation.Loaded)
                text += $"\nThe page did not finish loading within {LoadTimeout.TotalSeconds:0} s; it may still be loading.";
            return new BrowserReply(text);
        }, cancellationToken);

    /// <summary>
    /// Снимок текущей вкладки: вся страница или поддерево элемента по ссылке. Таблица ссылок
    /// заменяется ссылками нового снимка целиком.
    /// </summary>
    public Task<BrowserReply> SnapshotAsync(string? reference, CancellationToken cancellationToken) =>
        RunAsync(async browser =>
        {
            var page = await CurrentPageAsync(browser, cancellationToken);
            int? root = null;
            if (reference is not null)
            {
                root = ResolveRef(reference);
                if (root is null)
                    return Stale(reference);
            }

            var tree = await page.GetFullAXTreeAsync(cancellationToken);
            var snapshot = AxSnapshotFormatter.Format(tree, root);
            lock (_state)
            {
                if (_current == page.TargetId)
                    _refs = new Dictionary<string, int>(snapshot.Refs, StringComparer.Ordinal);
            }

            return new BrowserReply(await DescribeAsync(browser, page.TargetId, cancellationToken) + "\n\n" + snapshot.Text);
        }, cancellationToken);

    /// <summary>Клик по центру элемента: прокрутка к нему, его бокс, нажатие и отпускание событиями CDP.</summary>
    public Task<BrowserReply> ClickAsync(string reference, CancellationToken cancellationToken) =>
        RunAsync(async browser =>
        {
            var page = await CurrentPageAsync(browser, cancellationToken);
            if (ResolveRef(reference) is not { } node)
                return Stale(reference);

            try
            {
                await page.ScrollIntoViewAsync(node, cancellationToken);
                var (x, y) = CdpPage.QuadCenter(await page.GetContentQuadAsync(node, cancellationToken));
                await page.ClickAsync(x, y, cancellationToken);
            }
            catch (CdpProtocolException ex) when (NodeGone(ex))
            {
                return Stale(reference);
            }
            catch (CdpProtocolException ex) when (NoBox(ex))
            {
                return new BrowserReply(
                    $"Element {reference} has no visible box (hidden or zero-size) and cannot be clicked. {FreshSnapshot}",
                    IsError: true);
            }

            return new BrowserReply($"Clicked {reference}. If the page changed, take a fresh browser_snapshot before using refs again.");
        }, cancellationToken);

    /// <summary>
    /// Ввод в поле: фокус по узлу, выделение прежнего текста командой редактора, вставка текста —
    /// всё событиями CDP. <paramref name="submit"/> — Enter после ввода.
    /// </summary>
    public Task<BrowserReply> TypeAsync(string reference, string text, bool submit, CancellationToken cancellationToken) =>
        RunAsync(async browser =>
        {
            var page = await CurrentPageAsync(browser, cancellationToken);
            if (ResolveRef(reference) is not { } node)
                return Stale(reference);

            try
            {
                await page.FocusAsync(node, cancellationToken);
                await page.SelectAllAsync(cancellationToken);
                await page.InsertTextAsync(text, cancellationToken);
                if (submit)
                    await page.PressEnterAsync(cancellationToken);
            }
            catch (CdpProtocolException ex) when (NodeGone(ex))
            {
                return Stale(reference);
            }
            catch (CdpProtocolException ex) when (ex.ErrorMessage.Contains("focusable", StringComparison.OrdinalIgnoreCase))
            {
                return new BrowserReply($"Element {reference} cannot take text input: it is not focusable.", IsError: true);
            }

            return new BrowserReply(submit
                ? $"Typed into {reference} and pressed Enter. Take a fresh browser_snapshot to see the result."
                : $"Typed into {reference}.");
        }, cancellationToken);

    /// <summary>Вкладки своего браузера: <c>list</c>, <c>new</c> (адрес проверен гейтом), <c>select</c>, <c>close</c>.</summary>
    public Task<BrowserReply> TabsAsync(string action, string? url, string? tabId, CancellationToken cancellationToken) =>
        RunAsync(async browser =>
        {
            switch (action)
            {
                case "new":
                {
                    var targetId = await browser.CreateTargetAsync(url ?? "about:blank", cancellationToken);
                    await AttachAsync(browser, targetId, cancellationToken);
                    await browser.ActivateTargetAsync(targetId, cancellationToken);
                    return new BrowserReply($"Opened tab {targetId} and made it current: {url}. " +
                                            "It may still be loading; use browser_wait or browser_snapshot.");
                }
                case "select":
                {
                    if (!await TabExistsAsync(browser, tabId!, cancellationToken))
                        return UnknownTab(tabId!);
                    await AttachAsync(browser, tabId!, cancellationToken);
                    await browser.ActivateTargetAsync(tabId!, cancellationToken);
                    return new BrowserReply("Current tab:\n" + await DescribeAsync(browser, tabId!, cancellationToken) +
                                            "\nTake browser_snapshot to read it.");
                }
                case "close":
                {
                    if (!await TabExistsAsync(browser, tabId!, cancellationToken))
                        return UnknownTab(tabId!);
                    await browser.CloseTargetAsync(tabId!, cancellationToken);
                    Forget(tabId!);
                    return new BrowserReply($"Closed tab {tabId}.");
                }
                default:
                {
                    var tabs = await PageTargetsAsync(browser, cancellationToken);
                    if (tabs.Count == 0)
                        return new BrowserReply("No tabs are open. browser_navigate opens one.");
                    string? current;
                    lock (_state)
                        current = _current;
                    var sb = new StringBuilder();
                    foreach (var tab in tabs)
                    {
                        sb.Append("- tabId=").Append(tab.TargetId);
                        if (tab.TargetId == current)
                            sb.Append(" [current]");
                        sb.Append(" \"").Append(tab.Title).Append("\" ").Append(tab.Url).Append('\n');
                    }
                    return new BrowserReply(sb.ToString().TrimEnd());
                }
            }
        }, cancellationToken);

    /// <summary>
    /// Ожидание текста в дереве доступности текущей вкладки (опрос) либо пауза. Потолок времени
    /// держит гейт; без явного времени текст ждётся <see cref="DefaultTextWaitMs"/>.
    /// </summary>
    public Task<BrowserReply> WaitAsync(string? text, int? timeoutMs, CancellationToken cancellationToken) =>
        RunAsync(async browser =>
        {
            if (string.IsNullOrEmpty(text))
            {
                await Task.Delay(timeoutMs!.Value, cancellationToken);
                return new BrowserReply($"Waited {timeoutMs} ms.");
            }

            var limit = TimeSpan.FromMilliseconds(timeoutMs ?? DefaultTextWaitMs);
            var page = await CurrentPageAsync(browser, cancellationToken);
            var clock = Stopwatch.StartNew();
            while (true)
            {
                try
                {
                    if (ContainsText(await page.GetFullAXTreeAsync(cancellationToken), text))
                        return new BrowserReply($"Text \"{text}\" is on the page.");
                }
                catch (CdpProtocolException)
                {
                    // Документ сменился посреди запроса — спросим ещё раз
                }

                if (clock.Elapsed >= limit)
                    return new BrowserReply($"Text \"{text}\" did not appear within {limit.TotalMilliseconds:0} ms.", IsError: true);
                await Task.Delay(PollInterval, cancellationToken);
            }
        }, cancellationToken);

    /// <summary>Снимок видимой части текущей вкладки — только в ответ, на диск не пишется.</summary>
    public Task<BrowserReply> ScreenshotAsync(CancellationToken cancellationToken) =>
        RunAsync(async browser =>
        {
            var page = await CurrentPageAsync(browser, cancellationToken);
            var png = await page.CaptureScreenshotAsync(cancellationToken);
            return new BrowserReply(
                "Screenshot of the visible part of the current tab.\n" + await DescribeAsync(browser, page.TargetId, cancellationToken),
                Png: png);
        }, cancellationToken);

    // ---------- исполнение ----------

    private async Task<BrowserReply> RunAsync(Func<CdpBrowser, Task<BrowserReply>> action, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var meter = CdpMeter.Start();
        await _sync.WaitAsync(cancellationToken);
        var queue = Stopwatch.GetElapsedTime(started);
        try
        {
            var acquired = await source.AcquireAsync(cancellationToken);
            var acquire = Stopwatch.GetElapsedTime(started) - queue;
            BrowserReply reply;
            if (acquired.Browser is null)
            {
                reply = new BrowserReply(acquired.Refusal ?? "The browser is not available.", IsError: true);
            }
            else
            {
                if (!ReferenceEquals(acquired.Browser, _browser))
                    Reset(acquired.Browser, acquired.Restarted);

                try
                {
                    reply = await action(acquired.Browser);
                }
                catch (CdpDisconnectedException ex)
                {
                    reply = new BrowserReply($"{ex.Message}. The next browser_* call starts the browser again.", IsError: true);
                }
                catch (CdpException ex)
                {
                    reply = new BrowserReply(ex.Message, IsError: true);
                }

                reply = WithNotices(reply);
            }

            return reply with
            {
                Timing = new BrowserTiming(Stopwatch.GetElapsedTime(started), queue, acquire, meter.Calls, meter.Cdp, meter.Wait),
            };
        }
        finally
        {
            _sync.Release();
        }
    }

    /// <summary>Новый экземпляр браузера: старые вкладки, подписки и ссылки к нему не относятся.</summary>
    private void Reset(CdpBrowser browser, bool restarted)
    {
        lock (_state)
        {
            foreach (var subscription in _subscriptions)
                subscription.Dispose();
            _subscriptions.Clear();
            _pages.Clear();
            _current = null;
            _refs = new Dictionary<string, int>(StringComparer.Ordinal);
            _browser = browser;
            if (restarted)
                _notices.Add(RestartedNotice);

            // Вкладку закрыли руками или она упала: её сессия отцепляется, забываем её
            _subscriptions.Add(browser.Connection.Subscribe("Target.detachedFromTarget", null,
                e => Forget(CdpBrowser.Str(e.Params, "targetId"))));
        }
    }

    private BrowserReply WithNotices(BrowserReply reply)
    {
        string[] notices;
        lock (_state)
        {
            notices = [.. _notices];
            _notices.Clear();
        }

        if (notices.Length == 0)
            return reply;
        var prefix = string.Concat(notices.Select(n => "Note: " + n + "\n"));
        return reply with { Text = prefix + reply.Text };
    }

    // ---------- вкладки ----------

    private async Task<CdpPage> CurrentPageAsync(CdpBrowser browser, CancellationToken cancellationToken)
    {
        string? current;
        lock (_state)
        {
            current = _current;
            if (current is not null && _pages.TryGetValue(current, out var known))
                return known;
        }

        var tabs = await PageTargetsAsync(browser, cancellationToken);
        var targetId = (tabs.FirstOrDefault(t => t.TargetId == current) ?? tabs.FirstOrDefault())?.TargetId
                       ?? await browser.CreateTargetAsync("about:blank", cancellationToken);
        return await AttachAsync(browser, targetId, cancellationToken);
    }

    /// <summary>Подключиться к вкладке (или взять уже подключённую) и сделать её текущей.</summary>
    private async Task<CdpPage> AttachAsync(CdpBrowser browser, string targetId, CancellationToken cancellationToken)
    {
        lock (_state)
        {
            if (_pages.TryGetValue(targetId, out var known))
            {
                MakeCurrent(targetId);
                return known;
            }
        }

        var page = await browser.AttachAsync(targetId, cancellationToken);
        // Подписки до Page.enable: диалог, открытый к этому моменту, придёт событием сразу после него
        var dialogs = page.OnJavaScriptDialog(dialog => OnDialog(page, dialog));
        var navigations = browser.Connection.Subscribe("Page.frameNavigated", page.SessionId,
            e => OnFrameNavigated(targetId, e.Params));
        await page.EnableAsync(cancellationToken);

        lock (_state)
        {
            _subscriptions.Add(dialogs);
            _subscriptions.Add(navigations);
            _pages[targetId] = page;
            MakeCurrent(targetId);
        }
        return page;
    }

    private void MakeCurrent(string targetId)
    {
        if (_current == targetId)
            return;
        _current = targetId;
        _refs = new Dictionary<string, int>(StringComparer.Ordinal);
    }

    private void Forget(string targetId)
    {
        lock (_state)
        {
            _pages.Remove(targetId);
            if (_current != targetId)
                return;
            _current = null;
            _refs = new Dictionary<string, int>(StringComparer.Ordinal);
        }
    }

    private void ClearRefs(string targetId)
    {
        lock (_state)
        {
            if (_current == targetId)
                _refs = new Dictionary<string, int>(StringComparer.Ordinal);
        }
    }

    private static async Task<List<CdpTargetInfo>> PageTargetsAsync(CdpBrowser browser, CancellationToken cancellationToken) =>
        [.. (await browser.GetTargetsAsync(cancellationToken)).Where(t => t.Type == "page")];

    private static async Task<bool> TabExistsAsync(CdpBrowser browser, string tabId, CancellationToken cancellationToken) =>
        (await PageTargetsAsync(browser, cancellationToken)).Any(t => t.TargetId == tabId);

    private static async Task<string> DescribeAsync(CdpBrowser browser, string targetId, CancellationToken cancellationToken)
    {
        var info = (await browser.GetTargetsAsync(cancellationToken)).FirstOrDefault(t => t.TargetId == targetId);
        return info is null ? $"Tab: {targetId}" : $"Tab: {targetId}\nURL: {info.Url}\nTitle: {info.Title}";
    }

    // ---------- события страницы ----------

    /// <summary>
    /// Диалог закрывается сразу: <c>beforeunload</c> — подтверждением (модель сама просила уйти со
    /// страницы или закрыть вкладку), остальные — отказом, как «Отмена» у человека.
    /// </summary>
    private void OnDialog(CdpPage page, CdpDialog dialog)
    {
        var accept = dialog.Type == "beforeunload";
        var message = dialog.Message.Length > MaxDialogChars ? dialog.Message[..MaxDialogChars] + "…" : dialog.Message;
        lock (_state)
            _notices.Add($"The page opened a {dialog.Type} dialog \"{message}\"; it was " +
                         (accept ? "accepted (leaving the page)." : "dismissed automatically."));

        // Обработчик зовётся на потоке чтения трубы: ответ на команду ждать здесь нельзя
        _ = Task.Run(async () =>
        {
            try
            {
                await page.HandleJavaScriptDialogAsync(accept);
            }
            catch (CdpException)
            {
                // Диалог уже закрыт или вкладка ушла — страница не заблокирована
            }
        });
    }

    private void OnFrameNavigated(string targetId, JsonElement parameters)
    {
        // Ссылки держит только главный фрейм: переход во вложенном их не трогает
        if (parameters.TryGetProperty("frame", out var frame) && frame.TryGetProperty("parentId", out _))
            return;
        ClearRefs(targetId);
    }

    // ---------- разбор ----------

    private static BrowserReply Stale(string reference) =>
        new($"Ref '{reference}' is unknown or stale. {FreshSnapshot}", IsError: true);

    private static BrowserReply UnknownTab(string tabId) =>
        new($"Tab '{tabId}' does not exist. Call browser_tabs(action='list').", IsError: true);

    private static bool NodeGone(CdpProtocolException ex) =>
        ex.ErrorMessage.Contains("No node", StringComparison.OrdinalIgnoreCase) ||
        ex.ErrorMessage.Contains("does not belong to the document", StringComparison.OrdinalIgnoreCase);

    private static bool NoBox(CdpProtocolException ex) =>
        ex.ErrorMessage.Contains("box model", StringComparison.OrdinalIgnoreCase);

    /// <summary>Есть ли текст в имени или значении какого-либо узла дерева доступности.</summary>
    internal static bool ContainsText(JsonElement axTree, string text)
    {
        if (!axTree.TryGetProperty("nodes", out var nodes) || nodes.ValueKind != JsonValueKind.Array)
            return false;

        foreach (var node in nodes.EnumerateArray())
        {
            if (PropertyValue(node, "name")?.Contains(text, StringComparison.OrdinalIgnoreCase) == true ||
                PropertyValue(node, "value")?.Contains(text, StringComparison.OrdinalIgnoreCase) == true)
                return true;
        }
        return false;
    }

    private static string? PropertyValue(JsonElement node, string name) =>
        node.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Object &&
        property.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
