using System.Collections.Concurrent;
using System.Text.Json;
using ClaudeHomeServer.HandsBridge.Browser.Cdp;
using ClaudeHomeServer.HandsBridge.Browser.Session;
using Xunit;

namespace HandsBridge.Policy.Tests;

/// <summary>
/// Сессия браузерной руки (план Ш6) поверх подделки трубы: браузер отвечает по сценарию, тест
/// смотрит, какие команды CDP ушли. Главное — клик и ввод идут событиями CDP по узлу из снимка,
/// ссылки гаснут при переходе и перезапуске, а перезапуск и диалог доходят до модели заметкой.
/// </summary>
public sealed class BrowserSessionTests : IAsyncLifetime
{
    const int ButtonNode = 42;

    const string Tree = """
        {"nodes":[
          {"nodeId":"1","ignored":false,"role":{"type":"role","value":"RootWebArea"},"name":{"type":"computedString","value":"Example"},"childIds":["2","3"],"backendDOMNodeId":1},
          {"nodeId":"2","ignored":false,"role":{"type":"role","value":"heading"},"name":{"type":"computedString","value":"Welcome home"},"childIds":[],"parentId":"1","backendDOMNodeId":7},
          {"nodeId":"3","ignored":false,"role":{"type":"role","value":"button"},"name":{"type":"computedString","value":"Go"},"childIds":[],"parentId":"1","backendDOMNodeId":42}
        ]}
        """;

    readonly FakeSource _source = new();
    readonly BrowserSession _session;

    public BrowserSessionTests() => _session = new BrowserSession(_source);

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _source.DisposeAsync();

    [Fact]
    public async Task Navigate_waits_for_load_and_reports_url_and_title()
    {
        var reply = await _session.NavigateAsync("https://example.org/", CancellationToken.None);

        Assert.False(reply.IsError, reply.Text);
        Assert.Contains("URL: https://example.org/", reply.Text);
        Assert.Contains("Title: Example", reply.Text);
        Assert.Equal("https://example.org/", _source.Current.Sent("Page.navigate").Single().GetProperty("url").GetString());
    }

    [Fact]
    public async Task Navigate_answers_after_dom_ready_and_a_short_quiet_not_after_load()
    {
        _source.Current.Settles = false;

        var reply = await _session.NavigateAsync("https://example.org/", CancellationToken.None);

        Assert.False(reply.IsError, reply.Text);
        Assert.DoesNotContain("not ready", reply.Text);
        Assert.True(reply.Timing!.Wait < BrowserSession.SettleTimeout + TimeSpan.FromSeconds(1), $"ожидание {reply.Timing.Wait}");
        Assert.True(reply.Timing.Wait >= BrowserSession.SettleTimeout - TimeSpan.FromMilliseconds(50), $"ожидание {reply.Timing.Wait}");
    }

    [Fact]
    public async Task Every_reply_carries_a_timing_for_hands_log()
    {
        _source.Current.LoadDelay = TimeSpan.FromMilliseconds(150);

        var reply = await _session.NavigateAsync("https://example.org/", CancellationToken.None);

        var timing = Assert.IsType<BrowserTiming>(reply.Timing);
        Assert.True(timing.CdpCalls >= 3, $"CDP-вызовов {timing.CdpCalls}");
        Assert.True(timing.Wait >= TimeSpan.FromMilliseconds(100), $"ожидание {timing.Wait}");
        Assert.True(timing.Total >= timing.Cdp + timing.Wait - TimeSpan.FromMilliseconds(5));
        var line = reply.LogLine("browser_navigate");
        Assert.StartsWith("браузер: browser_navigate ", line);
        Assert.Contains($"CDP {timing.CdpCalls} выз.", line);
        Assert.Contains($"ответ {reply.Text.Length} симв.", line);
        Assert.DoesNotContain("ошибка", line);
    }

    [Fact]
    public async Task Refusal_is_timed_too_and_its_text_goes_to_the_log()
    {
        _source.Refusal = "Google Chrome was not found.";

        var reply = await _session.SnapshotAsync(null, CancellationToken.None);

        Assert.NotNull(reply.Timing);
        Assert.Equal(0, reply.Timing!.CdpCalls);
        Assert.Contains("ошибка: Google Chrome was not found.", reply.LogLine("browser_snapshot"));
    }

    [Fact]
    public async Task Click_goes_through_cdp_input_on_the_node_of_the_ref()
    {
        await Snapshot();

        var reply = await _session.ClickAsync("e1", CancellationToken.None);

        Assert.False(reply.IsError, reply.Text);
        var browser = _source.Current;
        Assert.Equal(ButtonNode, browser.Sent("DOM.scrollIntoViewIfNeeded").Single().GetProperty("backendNodeId").GetInt32());
        Assert.Equal(ButtonNode, browser.Sent("DOM.getBoxModel").Single().GetProperty("backendNodeId").GetInt32());
        var press = browser.Sent("Input.dispatchMouseEvent").Single(p => p.GetProperty("type").GetString() == "mousePressed");
        Assert.Equal(15, press.GetProperty("x").GetDouble());
        Assert.Equal(25, press.GetProperty("y").GetDouble());
    }

    [Fact]
    public async Task Type_focuses_the_node_replaces_text_and_submits()
    {
        await Snapshot();

        var reply = await _session.TypeAsync("e1", "hello", submit: true, CancellationToken.None);

        Assert.False(reply.IsError, reply.Text);
        var browser = _source.Current;
        Assert.Equal(ButtonNode, browser.Sent("DOM.focus").Single().GetProperty("backendNodeId").GetInt32());
        Assert.Contains(browser.Sent("Input.dispatchKeyEvent"), k => k.TryGetProperty("commands", out var c) && c[0].GetString() == "selectAll");
        Assert.Equal("hello", browser.Sent("Input.insertText").Single().GetProperty("text").GetString());
        Assert.Contains(browser.Sent("Input.dispatchKeyEvent"), k => k.GetProperty("key").GetString() == "Enter");
    }

    [Fact]
    public async Task Restart_drops_refs_and_tells_the_model()
    {
        await Snapshot();
        _source.Restart();

        var reply = await _session.ClickAsync("e1", CancellationToken.None);

        Assert.True(reply.IsError);
        Assert.Contains(BrowserSession.RestartedNotice, reply.Text);
        Assert.Contains("stale", reply.Text);
        Assert.Empty(_source.Current.Sent("Input.dispatchMouseEvent"));
    }

    [Fact]
    public async Task Main_frame_navigation_makes_refs_stale()
    {
        await Snapshot();
        Assert.Equal(ButtonNode, _session.ResolveRef("e1"));

        await _source.Current.EventAsync("Page.frameNavigated", "S1", """{"frame":{"id":"F1","url":"https://example.org/next"}}""");

        Assert.Null(_session.ResolveRef("e1"));
    }

    [Fact]
    public async Task Child_frame_navigation_keeps_refs()
    {
        await Snapshot();

        await _source.Current.EventAsync("Page.frameNavigated", "S1", """{"frame":{"id":"F2","parentId":"F1","url":"https://ads.example/"}}""");

        Assert.Equal(ButtonNode, _session.ResolveRef("e1"));
    }

    [Fact]
    public async Task Dialog_is_dismissed_and_reported_in_the_next_reply()
    {
        await Snapshot();

        await _source.Current.EventAsync("Page.javascriptDialogOpening", "S1", """{"type":"alert","message":"Hi there","url":"https://example.org/"}""");
        var handled = await _source.Current.WaitSentAsync("Page.handleJavaScriptDialog");
        Assert.False(handled.GetProperty("accept").GetBoolean());

        var reply = await _session.WaitAsync("Welcome", 1000, CancellationToken.None);
        Assert.StartsWith("Note: The page opened a alert dialog \"Hi there\"", reply.Text);
        Assert.Contains("Text \"Welcome\" is on the page.", reply.Text);
    }

    [Fact]
    public async Task Wait_for_missing_text_times_out_as_error()
    {
        var reply = await _session.WaitAsync("Nowhere", 300, CancellationToken.None);

        Assert.True(reply.IsError);
        Assert.Contains("did not appear", reply.Text);
    }

    [Fact]
    public async Task Refusal_of_the_source_reaches_the_model()
    {
        _source.Refusal = "Google Chrome was not found.";

        var reply = await _session.NavigateAsync("https://example.org/", CancellationToken.None);

        Assert.True(reply.IsError);
        Assert.Equal("Google Chrome was not found.", reply.Text);
    }

    [Fact]
    public async Task New_tab_becomes_current_loads_its_page_and_answers_with_its_snapshot()
    {
        await Snapshot();

        var reply = await _session.TabsAsync("new", "https://example.org/", null, CancellationToken.None);

        Assert.False(reply.IsError, reply.Text);
        Assert.Contains("Tab: T2", reply.Text);
        Assert.Contains("[ref=e1]", reply.Text);
        var browser = _source.Current;
        Assert.Equal("T2", browser.Sent("Target.activateTarget").Single().GetProperty("targetId").GetString());
        Assert.Equal("about:blank", browser.Sent("Target.createTarget").Single().GetProperty("url").GetString());
        Assert.Equal(["S2"], browser.SessionsOf("Page.navigate"));
        Assert.Equal("S2", browser.SessionsOf("Accessibility.getFullAXTree")[^1]);
        Assert.True(Index(browser, "event:DOMContentLoaded") < Index(browser, "cmd:Accessibility.getFullAXTree", last: true));
    }

    [Fact]
    public async Task Selected_tab_answers_with_its_snapshot()
    {
        await _session.TabsAsync("new", null, null, CancellationToken.None);

        var reply = await _session.TabsAsync("select", null, "T1", CancellationToken.None);

        Assert.False(reply.IsError, reply.Text);
        Assert.Contains("Tab: T1", reply.Text);
        Assert.Contains("button \"Go\" [ref=e1]", reply.Text);
        Assert.Equal(ButtonNode, _session.ResolveRef("e1"));
        Assert.Equal("S1", _source.Current.SessionsOf("Accessibility.getFullAXTree")[^1]);
    }

    // ---------- снимок в ответе действия ----------

    [Fact]
    public async Task Navigate_answers_with_a_snapshot_and_its_refs_are_usable_at_once()
    {
        var reply = await _session.NavigateAsync("https://example.org/", CancellationToken.None);

        Assert.Contains("heading \"Welcome home\"", reply.Text);
        Assert.Contains("button \"Go\" [ref=e1]", reply.Text);
        Assert.DoesNotContain("Take browser_snapshot", reply.Text);
        Assert.Equal(ButtonNode, _session.ResolveRef("e1"));

        var click = await _session.ClickAsync("e1", CancellationToken.None);
        Assert.False(click.IsError, click.Text);
    }

    [Fact]
    public async Task Click_that_navigates_waits_for_the_new_document_before_the_snapshot()
    {
        await Snapshot();
        var browser = _source.Current;
        browser.ActionNavigates = true;
        browser.LoadDelay = TimeSpan.FromMilliseconds(400);

        var reply = await _session.ClickAsync("e1", CancellationToken.None);

        Assert.False(reply.IsError, reply.Text);
        Assert.StartsWith("Clicked e1. The page navigated.", reply.Text);
        Assert.Contains("[ref=e1]", reply.Text);
        Assert.True(Index(browser, "event:DOMContentLoaded") < Index(browser, "cmd:Accessibility.getFullAXTree", last: true),
            string.Join(" ", browser.Timeline));
        Assert.Equal(ButtonNode, _session.ResolveRef("e1"));
    }

    [Fact]
    public async Task Click_without_navigation_answers_with_a_snapshot_after_the_short_window()
    {
        await Snapshot();

        var reply = await _session.ClickAsync("e1", CancellationToken.None);

        Assert.StartsWith("Clicked e1.\n", reply.Text);
        Assert.Contains("button \"Go\" [ref=e1]", reply.Text);
        Assert.True(reply.Timing!.Wait >= BrowserSession.NavigationStartWindow - TimeSpan.FromMilliseconds(50));
        Assert.True(reply.Timing.Wait < BrowserSession.NavigationStartWindow + TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Type_without_submit_answers_with_a_snapshot_at_once()
    {
        await Snapshot();

        var reply = await _session.TypeAsync("e1", "hello", submit: false, CancellationToken.None);

        Assert.StartsWith("Typed into e1.\n", reply.Text);
        Assert.Contains("[ref=e1]", reply.Text);
        Assert.True(reply.Timing!.Wait < BrowserSession.NavigationStartWindow, $"ожидание {reply.Timing.Wait}");
    }

    [Fact]
    public async Task Type_with_submit_waits_for_the_results_page()
    {
        await Snapshot();
        var browser = _source.Current;
        browser.ActionNavigates = true;
        browser.LoadDelay = TimeSpan.FromMilliseconds(400);

        var reply = await _session.TypeAsync("e1", "cats", submit: true, CancellationToken.None);

        Assert.StartsWith("Typed into e1 and pressed Enter. The page navigated.", reply.Text);
        Assert.True(Index(browser, "event:DOMContentLoaded") < Index(browser, "cmd:Accessibility.getFullAXTree", last: true),
            string.Join(" ", browser.Timeline));
    }

    [Fact]
    public async Task Action_snapshot_is_short_and_says_where_the_full_one_is()
    {
        _source.Current.AxTree = BigTree(600);

        var action = await _session.NavigateAsync("https://example.org/", CancellationToken.None);
        var full = await _session.SnapshotAsync(null, CancellationToken.None);

        var actionSnapshot = action.Text[action.Text.IndexOf("\n\n", StringComparison.Ordinal)..];
        Assert.True(actionSnapshot.Length <= BrowserSession.ActionSnapshotBudgetChars + BrowserSession.ShortSnapshotHint.Length + 4,
            $"снимок действия {actionSnapshot.Length}");
        Assert.EndsWith(BrowserSession.ShortSnapshotHint, action.Text);
        Assert.True(full.Text.Length > BrowserSession.ActionSnapshotBudgetChars * 3 / 2, $"полный {full.Text.Length}");
        Assert.DoesNotContain(BrowserSession.ShortSnapshotHint, full.Text);
    }

    [Fact]
    public async Task Small_action_snapshot_has_no_hint()
    {
        var reply = await _session.NavigateAsync("https://example.org/", CancellationToken.None);

        Assert.DoesNotContain(BrowserSession.ShortSnapshotHint, reply.Text);
    }

    static int Index(ScriptedBrowser browser, string entry, bool last = false)
    {
        var timeline = browser.Timeline.ToList();
        var index = last ? timeline.LastIndexOf(entry) : timeline.IndexOf(entry);
        Assert.True(index >= 0, $"{entry} нет в ленте: {string.Join(" ", timeline)}");
        return index;
    }

    /// <summary>Страница из <paramref name="buttons"/> кнопок — больше любого бюджета снимка.</summary>
    static string BigTree(int buttons)
    {
        var nodes = new List<string>
        {
            $$"""{"nodeId":"1","ignored":false,"role":{"type":"role","value":"RootWebArea"},"name":{"type":"computedString","value":"Big"},"childIds":[{{string.Join(",", Enumerable.Range(2, buttons).Select(i => $"\"{i}\""))}}],"backendDOMNodeId":1}""",
        };
        for (var i = 2; i < buttons + 2; i++)
            nodes.Add($$"""{"nodeId":"{{i}}","ignored":false,"role":{"type":"role","value":"button"},"name":{"type":"computedString","value":"Button number {{i}}"},"childIds":[],"parentId":"1","backendDOMNodeId":{{1000 + i}}}""");
        return "{\"nodes\":[" + string.Join(",", nodes) + "]}";
    }

    [Fact]
    public async Task Select_of_unknown_tab_is_refused()
    {
        var reply = await _session.TabsAsync("select", null, "T404", CancellationToken.None);

        Assert.True(reply.IsError);
        Assert.Contains("does not exist", reply.Text);
    }

    [Fact]
    public async Task Screenshot_returns_png_bytes_in_the_reply()
    {
        var reply = await _session.ScreenshotAsync(CancellationToken.None);

        Assert.False(reply.IsError, reply.Text);
        Assert.Equal([1, 2, 3], reply.Png);
    }

    async Task Snapshot()
    {
        var reply = await _session.SnapshotAsync(null, CancellationToken.None);
        Assert.False(reply.IsError, reply.Text);
        Assert.Contains("[ref=e1]", reply.Text);
    }

    /// <summary>Источник браузера: <see cref="Restart"/> подменяет браузер новым, как хост после смерти Chrome.</summary>
    sealed class FakeSource : IBrowserSource, IAsyncDisposable
    {
        readonly List<ScriptedBrowser> _all = [new()];
        bool _restarted;

        public ScriptedBrowser Current => _all[^1];

        public string? Refusal { get; set; }

        public void Restart()
        {
            _all.Add(new ScriptedBrowser());
            _restarted = true;
        }

        public Task<BrowserAcquire> AcquireAsync(CancellationToken cancellationToken = default)
        {
            if (Refusal is not null)
                return Task.FromResult(new BrowserAcquire(null, false, Refusal));
            var restarted = _restarted;
            _restarted = false;
            return Task.FromResult(new BrowserAcquire(Current.Browser, restarted, null));
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var browser in _all)
                await browser.DisposeAsync();
        }
    }

    /// <summary>
    /// Браузер по сценарию: одна вкладка T1, новая — T2; отвечает на каждую команду, копит их
    /// параметры с сессией и ведёт общую ленту «команда/событие», по которой видно порядок.
    /// </summary>
    sealed class ScriptedBrowser : IAsyncDisposable
    {
        readonly FakeCdpTransport _pipe = new();
        readonly CdpConnection _connection;
        readonly ConcurrentQueue<(string Method, string? Session, JsonElement Params)> _sent = new();
        readonly ConcurrentQueue<string> _timeline = new();
        readonly CancellationTokenSource _stop = new();
        readonly List<string> _tabs = ["T1"];

        public ScriptedBrowser()
        {
            _connection = new CdpConnection(_pipe, TimeSpan.FromSeconds(5));
            Browser = new CdpBrowser(_connection);
            _ = Task.Run(RespondAsync);
        }

        public CdpBrowser Browser { get; }

        /// <summary>Сколько страница «грузится» после начала перехода.</summary>
        public TimeSpan LoadDelay { get; set; }

        /// <summary>Приходит ли load после DOMContentLoaded; нет — страница «висит» на рекламе.</summary>
        public bool Settles { get; set; } = true;

        /// <summary>Клик и Enter уводят главный фрейм на новый документ L2.</summary>
        public bool ActionNavigates { get; set; }

        /// <summary>Ответ на Accessibility.getFullAXTree.</summary>
        public string AxTree { get; set; } = Tree;

        /// <summary>Свой ответ на команду (метод, параметры) → JSON result; null — ответ по умолчанию.</summary>
        public Func<string, JsonElement, string?>? Override { get; set; }

        /// <summary>Свой ответ-ошибка на команду: метод → сообщение CDP.</summary>
        public Func<string, JsonElement, string?>? Fail { get; set; }

        /// <summary>Команды, на которые браузер не отвечает вовсе (зависшая страница).</summary>
        public HashSet<string> Silent { get; } = [];

        /// <summary>Лента «cmd:метод» и «event:имя» в порядке, в каком их видел браузер.</summary>
        public IReadOnlyList<string> Timeline => [.. _timeline];

        public IReadOnlyList<JsonElement> Sent(string method) =>
            [.. _sent.Where(s => s.Method == method).Select(s => s.Params)];

        public IReadOnlyList<string?> SessionsOf(string method) =>
            [.. _sent.Where(s => s.Method == method).Select(s => s.Session)];

        public async Task<JsonElement> WaitSentAsync(string method)
        {
            for (var i = 0; i < 100; i++)
            {
                if (Sent(method) is [var first, ..])
                    return first;
                await Task.Delay(20);
            }
            throw new TimeoutException(method + " was not sent");
        }

        /// <summary>Событие от браузера; возврат — когда соединение его уже разослало.</summary>
        public async Task EventAsync(string method, string sessionId, string parameters)
        {
            var marker = _connection.WaitForEventAsync("Test.marker", null, TimeSpan.FromSeconds(5));
            _pipe.Frame($$"""{"method":"{{method}}","sessionId":"{{sessionId}}","params":{{parameters}}}""");
            _pipe.Frame("""{"method":"Test.marker","params":{}}""");
            await marker;
        }

        async Task RespondAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                JsonElement frame;
                try
                {
                    frame = await _pipe.NextSentAsync();
                }
                catch (TimeoutException)
                {
                    continue;
                }

                var method = frame.GetProperty("method").GetString()!;
                var session = frame.TryGetProperty("sessionId", out var s) ? s.GetString() : null;
                var parameters = frame.GetProperty("params").Clone();
                _sent.Enqueue((method, session, parameters));
                _timeline.Enqueue("cmd:" + method);
                if (Silent.Contains(method))
                    continue;

                var id = frame.GetProperty("id").GetInt32();
                if (Fail?.Invoke(method, parameters) is { } error)
                    _pipe.Frame($$$"""{"id":{{{id}}},"error":{"code":-32000,"message":{{{JsonSerializer.Serialize(error)}}}}}""");
                else
                    _pipe.Frame($$"""{"id":{{id}},"result":{{Override?.Invoke(method, parameters) ?? Result(method, parameters)}}}""");

                if (method == "Page.navigate")
                {
                    await LoadAsync(session!, "L1");
                }
                else if (ActionNavigates && IsAction(method, parameters))
                {
                    Event(session!, "Page.frameRequestedNavigation", """{"frameId":"F1","reason":"anchorClick","url":"https://example.org/next"}""");
                    await LoadAsync(session!, "L2");
                }
            }
        }

        static bool IsAction(string method, JsonElement parameters) =>
            (method == "Input.dispatchMouseEvent" && parameters.GetProperty("type").GetString() == "mouseReleased") ||
            (method == "Input.dispatchKeyEvent" && parameters.GetProperty("type").GetString() == "keyDown" &&
             parameters.GetProperty("key").GetString() == "Enter");

        async Task LoadAsync(string session, string loader)
        {
            if (LoadDelay > TimeSpan.Zero)
                await Task.Delay(LoadDelay);
            Event(session, "Page.frameNavigated", """{"frame":{"id":"F1","url":"https://example.org/next"}}""");
            Lifecycle(session, loader, "DOMContentLoaded");
            if (Settles)
                Lifecycle(session, loader, "load");
        }

        void Lifecycle(string session, string loader, string name) =>
            Event(session, "Page.lifecycleEvent", $$$"""{"frameId":"F1","loaderId":"{{{loader}}}","name":"{{{name}}}"}""");

        void Event(string session, string method, string parameters)
        {
            _timeline.Enqueue("event:" + (method == "Page.lifecycleEvent"
                ? JsonDocument.Parse(parameters).RootElement.GetProperty("name").GetString()
                : method));
            _pipe.Frame($$"""{"method":"{{method}}","sessionId":"{{session}}","params":{{parameters}}}""");
        }

        string Result(string method, JsonElement parameters) => method switch
        {
            "Target.getTargets" => "{\"targetInfos\":[" + string.Join(",", _tabs.Select(t =>
                $$"""{"targetId":"{{t}}","type":"page","title":"Example","url":"https://example.org/","attached":true}""")) + "]}",
            "Target.createTarget" => AddTab(),
            "Target.attachToTarget" => $$"""{"sessionId":"{{(parameters.GetProperty("targetId").GetString() == "T1" ? "S1" : "S2")}}"}""",
            "Page.navigate" => """{"frameId":"F1","loaderId":"L1"}""",
            "Page.getFrameTree" => """{"frameTree":{"frame":{"id":"F1","loaderId":"L0","url":"about:blank"}}}""",
            "Accessibility.getFullAXTree" => AxTree,
            "DOM.getBoxModel" => """{"model":{"content":[10,20,20,20,20,30,10,30]}}""",
            "Page.captureScreenshot" => """{"data":"AQID"}""",
            _ => "{}",
        };

        string AddTab()
        {
            _tabs.Add("T2");
            return """{"targetId":"T2"}""";
        }

        public async ValueTask DisposeAsync()
        {
            // Цикл ответов выйдет сам на ближайшем таймауте чтения — ждать его незачем
            _stop.Cancel();
            await _connection.DisposeAsync();
            _pipe.EndOfStream();
        }
    }
}
