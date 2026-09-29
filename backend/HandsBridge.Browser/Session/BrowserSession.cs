using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ClaudeHomeServer.HandsBridge.Browser.Cdp;
using ClaudeHomeServer.HandsBridge.Browser.Dom;
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

    /// <summary>Потолок ожидания готовности DOM (DOMContentLoaded) после перехода.</summary>
    public static readonly TimeSpan LoadTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Короткое затишье после готовности DOM: ждём load или почти пустую сеть не дольше этого.
    /// Секунды хватает догрузить скрипты, рисующие содержимое, и не ждать рекламу со счётчиками,
    /// которые держали load до потолка.
    /// </summary>
    public static readonly TimeSpan SettleTimeout = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Окно, в которое после клика или Enter ловится начало перехода главного фрейма. Переход по
    /// ссылке или отправка формы просят навигацию за десятки миллисекунд; не начался — снимок
    /// берётся сразу, и заодно у страницы с перерисовкой без перехода есть время её закончить.
    /// </summary>
    public static readonly TimeSpan NavigationStartWindow = TimeSpan.FromMilliseconds(300);

    /// <summary>
    /// Бюджет снимка в ответе действия — половина полного (<see cref="AxSnapshotFormatter.DefaultBudgetChars"/>):
    /// около 1,5–2 тыс. токенов на каждый клик и ввод вместо 3–4. Верх страницы с формой, меню и
    /// первыми результатами в него влезает, а модели после действия обычно нужны именно они и
    /// свежие ссылки; дальше — <c>browser_snapshot</c> или <c>browser_query</c>.
    /// </summary>
    public const int ActionSnapshotBudgetChars = 6_000;

    /// <summary>Хвост короткого снимка, если он обрезан.</summary>
    public const string ShortSnapshotHint =
        "This is a short snapshot after the action; call browser_snapshot for the full page.";

    /// <summary>
    /// Бюджет ответа <c>browser_query</c>: запрос прицельный, модель сама попросила именно эти
    /// элементы, поэтому бюджет больше полного снимка — около 5 тыс. токенов. Этого хватает на
    /// статью или таблицу целиком, а случайный <c>body</c> в режиме <c>html</c> всё равно режется.
    /// </summary>
    public const int QueryBudgetChars = 20_000;

    /// <summary>Совпадений в ответе <c>browser_query</c> без явного limit.</summary>
    public const int DefaultQueryLimit = 10;

    private const int MaxAttributeValueChars = 500;

    /// <summary>Ожидание текста без явного времени.</summary>
    public const int DefaultTextWaitMs = 10_000;

    private const string AboutBlank = "about:blank";
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

    /// <summary>
    /// Переход по адресу, уже проверенному гейтом: готовность DOM не дольше <see cref="LoadTimeout"/>
    /// плюс затишье не дольше <see cref="SettleTimeout"/>, затем короткий снимок в ответ.
    /// </summary>
    public Task<BrowserReply> NavigateAsync(string url, CancellationToken cancellationToken) =>
        RunAsync(async browser =>
        {
            var page = await CurrentPageAsync(browser, cancellationToken);
            ClearRefs(page.TargetId);
            var navigation = await page.NavigateAsync(url, LoadTimeout, SettleTimeout, cancellationToken);
            if (navigation.ErrorText is not null)
                return new BrowserReply($"Navigation to {url} failed: {navigation.ErrorText}", IsError: true);

            var note = navigation.Loaded
                ? null
                : $"The page content was not ready within {LoadTimeout.TotalSeconds:0} s; it may still be loading.";
            return new BrowserReply(await ActionSnapshotAsync(browser, page, note, cancellationToken));
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

            var snapshot = await TakeSnapshotAsync(page, root, AxSnapshotFormatter.DefaultBudgetChars, cancellationToken);
            return new BrowserReply(await DescribeAsync(browser, page.TargetId, cancellationToken) + "\n\n" + snapshot.Text);
        }, cancellationToken);

    /// <summary>
    /// Клик по центру элемента: прокрутка к нему, его бокс, нажатие и отпускание событиями CDP.
    /// Начался переход главного фрейма — ждём его документ, как у <see cref="NavigateAsync"/>;
    /// затем короткий снимок в ответ.
    /// </summary>
    public Task<BrowserReply> ClickAsync(string reference, CancellationToken cancellationToken) =>
        RunAsync(async browser =>
        {
            var page = await CurrentPageAsync(browser, cancellationToken);
            if (ResolveRef(reference) is not { } node)
                return Stale(reference);

            using var watch = page.WatchLoad();
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

            var note = await AfterActionAsync(page, watch, cancellationToken);
            return new BrowserReply(await ActionSnapshotAsync(browser, page, $"Clicked {reference}.{note}", cancellationToken));
        }, cancellationToken);

    /// <summary>
    /// Ввод в поле: фокус по узлу, выделение прежнего текста командой редактора, вставка текста —
    /// всё событиями CDP. <paramref name="submit"/> — Enter после ввода и ожидание перехода, как
    /// после клика. В ответе — короткий снимок.
    /// </summary>
    public Task<BrowserReply> TypeAsync(string reference, string text, bool submit, CancellationToken cancellationToken) =>
        RunAsync(async browser =>
        {
            var page = await CurrentPageAsync(browser, cancellationToken);
            if (ResolveRef(reference) is not { } node)
                return Stale(reference);

            using var watch = page.WatchLoad();
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

            var note = submit
                ? $"Typed into {reference} and pressed Enter.{await AfterActionAsync(page, watch, cancellationToken)}"
                : $"Typed into {reference}.";
            return new BrowserReply(await ActionSnapshotAsync(browser, page, note, cancellationToken));
        }, cancellationToken);

    /// <summary>
    /// Вкладки своего браузера: <c>list</c>, <c>new</c> (адрес проверен гейтом), <c>select</c>,
    /// <c>close</c>. Новая и выбранная вкладка приходят в ответе коротким снимком.
    /// </summary>
    public Task<BrowserReply> TabsAsync(string action, string? url, string? tabId, CancellationToken cancellationToken) =>
        RunAsync(async browser =>
        {
            switch (action)
            {
                case "new":
                {
                    // Пустая вкладка и переход в ней: так ждём загрузку тем же путём, что browser_navigate
                    var targetId = await browser.CreateTargetAsync(AboutBlank, cancellationToken);
                    var page = await AttachAsync(browser, targetId, cancellationToken);
                    await browser.ActivateTargetAsync(targetId, cancellationToken);
                    var note = $"Opened tab {targetId} and made it current.";
                    if (url is not null && url != AboutBlank)
                    {
                        var navigation = await page.NavigateAsync(url, LoadTimeout, SettleTimeout, cancellationToken);
                        if (navigation.ErrorText is not null)
                            return new BrowserReply($"{note} Navigation to {url} failed: {navigation.ErrorText}", IsError: true);
                        if (!navigation.Loaded)
                            note += $" The page content was not ready within {LoadTimeout.TotalSeconds:0} s; it may still be loading.";
                    }
                    return new BrowserReply(await ActionSnapshotAsync(browser, page, note, cancellationToken));
                }
                case "select":
                {
                    if (!await TabExistsAsync(browser, tabId!, cancellationToken))
                        return UnknownTab(tabId!);
                    var page = await AttachAsync(browser, tabId!, cancellationToken);
                    await browser.ActivateTargetAsync(tabId!, cancellationToken);
                    return new BrowserReply(await ActionSnapshotAsync(browser, page, "Current tab changed.", cancellationToken));
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
    /// Чтение DOM текущей вкладки без JS (<c>browser_query</c>): элементы по CSS-селектору (внутри
    /// элемента по ссылке, если она есть) или сам элемент по ссылке; из каждого — текст, атрибуты
    /// или разметка. Ответ не длиннее <see cref="QueryBudgetChars"/>, обрезка честно помечена.
    /// Ссылки снимка запрос не трогает.
    /// </summary>
    /// <param name="mode"><c>text</c> (по умолчанию), <c>attributes</c> или <c>html</c> — проверено гейтом.</param>
    /// <param name="limit">Сколько совпадений показать; по умолчанию <see cref="DefaultQueryLimit"/>.</param>
    public Task<BrowserReply> QueryAsync(string? selector, string? reference, string? mode, int? limit, CancellationToken cancellationToken) =>
        RunAsync(async browser =>
        {
            var page = await CurrentPageAsync(browser, cancellationToken);
            int? backendNode = null;
            if (reference is not null && (backendNode = ResolveRef(reference)) is null)
                return Stale(reference);

            var scope = await page.GetDocumentAsync(cancellationToken);
            if (backendNode is { } node)
            {
                try
                {
                    scope = await page.PushBackendNodeAsync(node, cancellationToken);
                }
                catch (CdpProtocolException ex) when (NodeGone(ex))
                {
                    scope = 0;
                }
                if (scope == 0)
                    return Stale(reference!);
            }

            int[] matches;
            if (selector is null)
            {
                matches = [scope];
            }
            else
            {
                try
                {
                    matches = await page.QuerySelectorAllAsync(scope, selector, cancellationToken);
                }
                catch (CdpProtocolException ex)
                {
                    return new BrowserReply($"Selector '{selector}' was rejected by the page: {ex.ErrorMessage}. Use a valid CSS selector.", IsError: true);
                }
            }

            var what = selector is null ? $"element {reference}" : reference is null ? $"'{selector}'" : $"'{selector}' inside {reference}";
            var header = await DescribeAsync(browser, page.TargetId, cancellationToken) + "\n\n";
            if (matches.Length == 0)
                return new BrowserReply(header + $"No elements match {what}.");

            var shown = Math.Min(matches.Length, limit ?? DefaultQueryLimit);
            var output = new QueryOutput(QueryBudgetChars - header.Length);
            output.Line(matches.Length == 1
                ? $"1 element matches {what}:"
                : $"{matches.Length} elements match {what}" + (shown < matches.Length ? $" (showing the first {shown}):" : ":"));

            var done = 0;
            for (; done < shown && !output.Full; done++)
            {
                var item = (mode ?? "text") switch
                {
                    "html" => await page.GetOuterHtmlAsync(matches[done], cancellationToken),
                    "attributes" => FormatAttributes(await page.DescribeNodeAsync(matches[done], cancellationToken)),
                    _ => HtmlText.ToText(await page.GetOuterHtmlAsync(matches[done], cancellationToken)),
                };
                output.Item(done + 1, item.Length == 0 ? "(no text)" : item);
            }

            if (output.Full)
                output.Tail($"[output cut at {QueryBudgetChars} characters: {done} of {matches.Length} matching elements shown, " +
                            "the last one partly. Narrow the selector, lower limit or query a single element.]");
            else if (shown < matches.Length)
                output.Tail($"[{matches.Length - shown} more elements match; raise limit (up to 100) or narrow the selector.]");

            return new BrowserReply(header + output);
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

    // ---------- снимок в ответе ----------

    /// <summary>Снимок вкладки; таблица ссылок заменяется его ссылками, если вкладка всё ещё текущая.</summary>
    private async Task<AxSnapshot> TakeSnapshotAsync(CdpPage page, int? root, int budgetChars, CancellationToken cancellationToken)
    {
        var tree = await page.GetFullAXTreeAsync(cancellationToken);
        var snapshot = AxSnapshotFormatter.Format(tree, root, budgetChars);
        lock (_state)
        {
            if (_current == page.TargetId)
                _refs = new Dictionary<string, int>(snapshot.Refs, StringComparer.Ordinal);
        }
        return snapshot;
    }

    /// <summary>
    /// Ответ действия: итог действия, вкладка и короткий снимок (<see cref="ActionSnapshotBudgetChars"/>)
    /// с новыми ссылками — отдельный <c>browser_snapshot</c> после действия не нужен.
    /// </summary>
    private async Task<string> ActionSnapshotAsync(CdpBrowser browser, CdpPage page, string? note, CancellationToken cancellationToken)
    {
        var snapshot = await TakeSnapshotAsync(page, null, ActionSnapshotBudgetChars, cancellationToken);
        var sb = new StringBuilder();
        if (note is not null)
            sb.Append(note).Append('\n');
        sb.Append(await DescribeAsync(browser, page.TargetId, cancellationToken)).Append("\n\n").Append(snapshot.Text);
        if (snapshot.TruncatedNodes > 0)
            sb.Append('\n').Append(ShortSnapshotHint);
        return sb.ToString();
    }

    /// <summary>
    /// После клика или Enter: начался переход главного фрейма за <see cref="NavigationStartWindow"/> —
    /// ждём его документ так же, как <c>browser_navigate</c>. Возвращает дополнение к итогу действия.
    /// </summary>
    private static async Task<string> AfterActionAsync(CdpPage page, CdpLoadWatch watch, CancellationToken cancellationToken)
    {
        if (!await watch.WaitForNavigationStartAsync(page.MainFrameId, NavigationStartWindow, cancellationToken))
            return "";

        var state = await watch.WaitForLoadAsync(page.MainFrameId, null, LoadTimeout, SettleTimeout, cancellationToken);
        return state == CdpLoadState.NotLoaded
            ? " The page started a navigation, but no new page content became ready; it may still be loading."
            : " The page navigated.";
    }

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

    // ---------- ответ browser_query ----------

    private static string FormatAttributes((string Name, IReadOnlyList<KeyValuePair<string, string>> Attributes) node)
    {
        var sb = new StringBuilder("<").Append(node.Name);
        foreach (var (name, value) in node.Attributes)
        {
            var clipped = value.Length > MaxAttributeValueChars ? value[..MaxAttributeValueChars] + "…" : value;
            sb.Append(' ').Append(name).Append("=\"").Append(clipped.Replace("\"", "&quot;")).Append('"');
        }
        return sb.Append('>').ToString();
    }

    /// <summary>Текст ответа с потолком: последний не влезший кусок режется, дальше ничего не пишется.</summary>
    private sealed class QueryOutput(int budget)
    {
        // Запас под хвост об обрезке: он обязан влезть всегда
        private const int TailReserve = 300;

        private readonly StringBuilder _sb = new();

        public bool Full { get; private set; }

        public void Line(string text) => Append(text + "\n");

        public void Item(int index, string text) => Append($"[{index}] {text}\n");

        public void Tail(string text) => _sb.Append(text);

        private void Append(string text)
        {
            var left = budget - TailReserve - _sb.Length;
            if (text.Length <= left)
            {
                _sb.Append(text);
                return;
            }

            if (left > 1)
                _sb.Append(text, 0, left - 1).Append("…\n");
            Full = true;
        }

        public override string ToString() => _sb.ToString().TrimEnd('\n');
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
