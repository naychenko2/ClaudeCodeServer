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
    public async Task New_tab_becomes_current_and_drops_refs()
    {
        await Snapshot();

        var reply = await _session.TabsAsync("new", "https://example.org/", null, CancellationToken.None);

        Assert.False(reply.IsError, reply.Text);
        Assert.Contains("T2", reply.Text);
        Assert.Null(_session.ResolveRef("e1"));
        Assert.Equal("T2", _source.Current.Sent("Target.activateTarget").Single().GetProperty("targetId").GetString());
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

    /// <summary>Браузер по сценарию: одна вкладка T1, новая — T2; отвечает на каждую команду и копит их параметры.</summary>
    sealed class ScriptedBrowser : IAsyncDisposable
    {
        readonly FakeCdpTransport _pipe = new();
        readonly CdpConnection _connection;
        readonly ConcurrentQueue<(string Method, JsonElement Params)> _sent = new();
        readonly CancellationTokenSource _stop = new();
        readonly List<string> _tabs = ["T1"];

        public ScriptedBrowser()
        {
            _connection = new CdpConnection(_pipe, TimeSpan.FromSeconds(5));
            Browser = new CdpBrowser(_connection);
            _ = Task.Run(RespondAsync);
        }

        public CdpBrowser Browser { get; }

        public IReadOnlyList<JsonElement> Sent(string method) =>
            [.. _sent.Where(s => s.Method == method).Select(s => s.Params)];

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
                var parameters = frame.GetProperty("params").Clone();
                _sent.Enqueue((method, parameters));
                var id = frame.GetProperty("id").GetInt32();
                _pipe.Frame($$"""{"id":{{id}},"result":{{Result(method, parameters)}}}""");
                if (method == "Page.navigate")
                    _pipe.Frame("""{"method":"Page.loadEventFired","sessionId":"S1","params":{}}""");
            }
        }

        string Result(string method, JsonElement parameters) => method switch
        {
            "Target.getTargets" => "{\"targetInfos\":[" + string.Join(",", _tabs.Select(t =>
                $$"""{"targetId":"{{t}}","type":"page","title":"Example","url":"https://example.org/","attached":true}""")) + "]}",
            "Target.createTarget" => AddTab(),
            "Target.attachToTarget" => $$"""{"sessionId":"{{(parameters.GetProperty("targetId").GetString() == "T1" ? "S1" : "S2")}}"}""",
            "Page.navigate" => """{"frameId":"F1","loaderId":"L1"}""",
            "Accessibility.getFullAXTree" => Tree,
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
