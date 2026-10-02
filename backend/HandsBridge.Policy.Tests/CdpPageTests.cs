using System.Text.Json;
using ClaudeHomeServer.HandsBridge.Browser.Cdp;
using Xunit;

namespace HandsBridge.Policy.Tests;

/// <summary>Обёртки команд браузера и вкладки поверх подделки трубы (план Ш3).</summary>
public sealed class CdpPageTests : IAsyncLifetime
{
    static readonly TimeSpan Short = TimeSpan.FromSeconds(5);

    readonly FakeCdpTransport _pipe = new();
    readonly CdpConnection _cdp;
    readonly CdpPage _page;

    public CdpPageTests()
    {
        _cdp = new CdpConnection(_pipe, Short);
        _page = new CdpPage(_cdp, "T1", "S1");
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _cdp.DisposeAsync();

    [Fact]
    public async Task Attach_uses_flatten_and_returns_page_bound_to_session()
    {
        var browser = new CdpBrowser(_cdp);
        var attach = browser.AttachAsync("T9");

        var sent = await Expect("Target.attachToTarget");
        Assert.Equal("T9", sent.GetProperty("params").GetProperty("targetId").GetString());
        Assert.True(sent.GetProperty("params").GetProperty("flatten").GetBoolean());
        Reply(sent, """{"sessionId":"S9"}""");

        var page = await attach;
        Assert.Equal("S9", page.SessionId);
        Assert.Equal("T9", page.TargetId);
    }

    [Fact]
    public async Task Deny_downloads_sends_behavior_deny()
    {
        var call = new CdpBrowser(_cdp).DenyDownloadsAsync();
        var sent = await Expect("Browser.setDownloadBehavior");
        Assert.Equal("deny", sent.GetProperty("params").GetProperty("behavior").GetString());
        Reply(sent, "{}");
        await call;
    }

    static readonly TimeSpan Forever = TimeSpan.FromMinutes(5);

    [Fact]
    public async Task Navigate_waits_for_dom_of_its_own_session_frame_and_document_only()
    {
        var nav = _page.NavigateAsync("https://example.org/", Forever, Forever);

        var sent = await Expect("Page.navigate");
        Assert.Equal("S1", sent.GetProperty("sessionId").GetString());
        Reply(sent, """{"frameId":"F1","loaderId":"L1"}""");
        Lifecycle("S2", "F1", "L1", "DOMContentLoaded");
        Lifecycle("S1", "F2", "L1", "DOMContentLoaded");
        Lifecycle("S1", "F1", "L0", "DOMContentLoaded");
        Lifecycle("S1", "F1", "L0", "load");
        await Drain();
        Assert.False(nav.IsCompleted);

        Lifecycle("S1", "F1", "L1", "DOMContentLoaded");
        await Drain();
        Assert.False(nav.IsCompleted);

        Lifecycle("S1", "F1", "L1", "load");
        var result = await nav.WaitAsync(Short);
        Assert.True(result.Loaded);
        Assert.True(result.Settled);
        Assert.Null(result.ErrorText);
        Assert.Equal("F1", result.FrameId);
    }

    [Fact]
    public async Task Dom_ready_arriving_before_the_navigate_reply_is_not_lost()
    {
        var nav = _page.NavigateAsync("https://example.org/", Forever, Forever);

        var sent = await Expect("Page.navigate");
        Lifecycle("S1", "F1", "L1", "DOMContentLoaded");
        Lifecycle("S1", "F1", "L1", "networkAlmostIdle");
        Reply(sent, """{"frameId":"F1","loaderId":"L1"}""");

        var result = await nav.WaitAsync(Short);
        Assert.True(result.Loaded);
        Assert.True(result.Settled);
    }

    [Fact]
    public async Task Navigate_does_not_wait_for_load_beyond_a_short_quiet_after_dom_ready()
    {
        var nav = _page.NavigateAsync("https://ads.example/", Forever, TimeSpan.FromMilliseconds(200));
        Reply(await Expect("Page.navigate"), """{"frameId":"F1","loaderId":"L1"}""");
        Lifecycle("S1", "F1", "L1", "DOMContentLoaded");

        // load так и не придёт: реклама держит страницу, а ответ нужен сейчас
        var result = await nav.WaitAsync(Short);
        Assert.True(result.Loaded);
        Assert.False(result.Settled);
    }

    [Fact]
    public async Task Navigate_returns_error_text_without_waiting_for_load()
    {
        var nav = _page.NavigateAsync("https://nowhere.invalid/", Forever, Forever);
        Reply(await Expect("Page.navigate"), """{"frameId":"F1","loaderId":"L1","errorText":"net::ERR_NAME_NOT_RESOLVED"}""");

        var result = await nav.WaitAsync(Short);
        Assert.Equal("net::ERR_NAME_NOT_RESOLVED", result.ErrorText);
        Assert.False(result.Loaded);
    }

    [Fact]
    public async Task Navigate_reports_not_loaded_when_dom_is_not_ready_in_time()
    {
        var nav = _page.NavigateAsync("https://slow.example/", TimeSpan.FromMilliseconds(200), Forever);
        Reply(await Expect("Page.navigate"), """{"frameId":"F1","loaderId":"L1"}""");

        Assert.False((await nav.WaitAsync(Short)).Loaded);
    }

    [Fact]
    public async Task Same_document_navigation_does_not_wait_for_load()
    {
        var nav = _page.NavigateAsync("https://example.org/#top", Forever, Forever);
        Reply(await Expect("Page.navigate"), """{"frameId":"F1"}""");

        Assert.True((await nav.WaitAsync(Short)).Loaded);
    }

    [Fact]
    public async Task Broken_pipe_ends_the_load_wait_at_once()
    {
        var nav = _page.NavigateAsync("https://example.org/", Forever, Forever);
        Reply(await Expect("Page.navigate"), """{"frameId":"F1","loaderId":"L1"}""");

        _pipe.EndOfStream();

        await Assert.ThrowsAsync<CdpDisconnectedException>(() => nav.WaitAsync(Short));
    }

    [Fact]
    public async Task Navigation_start_after_a_click_is_seen_and_its_document_awaited()
    {
        using var watch = _page.WatchLoad();
        _pipe.Frame("""{"method":"Page.frameRequestedNavigation","sessionId":"S1","params":{"frameId":"F2","reason":"scriptInitiated","url":"https://ads/"}}""");
        _pipe.Frame("""{"method":"Page.frameRequestedNavigation","sessionId":"S1","params":{"frameId":"F1","reason":"anchorClick","url":"https://example.org/next"}}""");

        Assert.True(await watch.WaitForNavigationStartAsync("F1", Short).WaitAsync(Short));

        Lifecycle("S1", "F1", "L7", "init");
        Lifecycle("S1", "F1", "L7", "DOMContentLoaded");
        Lifecycle("S1", "F1", "L7", "load");
        Assert.Equal(CdpLoadState.Settled, await watch.WaitForLoadAsync("F1", null, Forever, Forever).WaitAsync(Short));
    }

    [Fact]
    public async Task Click_without_navigation_waits_only_the_short_window()
    {
        using var watch = _page.WatchLoad();

        var started = DateTime.UtcNow;
        Assert.False(await watch.WaitForNavigationStartAsync("F1", TimeSpan.FromMilliseconds(150)).WaitAsync(Short));
        Assert.True(DateTime.UtcNow - started >= TimeSpan.FromMilliseconds(100));
    }

    [Fact]
    public async Task Same_document_navigation_after_a_click_is_not_a_load()
    {
        using var watch = _page.WatchLoad();
        _pipe.Frame("""{"method":"Page.navigatedWithinDocument","sessionId":"S1","params":{"frameId":"F1","url":"https://example.org/#b"}}""");

        Assert.False(await watch.WaitForNavigationStartAsync("F1", Forever).WaitAsync(Short));
    }

    [Fact]
    public async Task Stopped_loading_without_a_new_document_ends_the_wait_of_an_unknown_loader()
    {
        using var watch = _page.WatchLoad();
        _pipe.Frame("""{"method":"Page.frameStoppedLoading","sessionId":"S1","params":{"frameId":"F1"}}""");

        Assert.Equal(CdpLoadState.NotLoaded, await watch.WaitForLoadAsync("F1", null, Forever, Forever).WaitAsync(Short));
    }

    // ---------- чем кончилось затишье (этапы hands.log) ----------

    [Theory]
    [InlineData("load", "load")]
    [InlineData("networkAlmostIdle", "networkAlmostIdle")]
    [InlineData("stopped", "конец загрузки")]
    public async Task Quiet_that_settled_is_marked_by_its_event(string settle, string outcome)
    {
        var meter = CdpMeter.Start();
        using var watch = _page.WatchLoad();
        Lifecycle("S1", "F1", "L1", "DOMContentLoaded");
        if (settle == "stopped")
            _pipe.Frame("""{"method":"Page.frameStoppedLoading","sessionId":"S1","params":{"frameId":"F1"}}""");
        else
            Lifecycle("S1", "F1", "L1", settle);

        Assert.Equal(CdpLoadState.Settled, await watch.WaitForLoadAsync("F1", "L1", Forever, Forever).WaitAsync(Short));
        Assert.Equal(outcome, Quiet(meter));
    }

    [Fact]
    public async Task Quiet_window_that_ran_out_is_not_the_load_ceiling()
    {
        var meter = CdpMeter.Start();
        using var watch = _page.WatchLoad();
        Lifecycle("S1", "F1", "L1", "DOMContentLoaded");

        var state = await watch.WaitForLoadAsync("F1", "L1", Forever, TimeSpan.FromMilliseconds(100)).WaitAsync(Short);

        Assert.Equal(CdpLoadState.DomReady, state);
        Assert.Equal("затишье истекло", Quiet(meter));
    }

    [Fact]
    public async Task Quiet_cut_by_the_load_ceiling_is_marked_as_the_ceiling()
    {
        var meter = CdpMeter.Start();
        using var watch = _page.WatchLoad();
        Lifecycle("S1", "F1", "L1", "DOMContentLoaded");

        var state = await watch.WaitForLoadAsync("F1", "L1", TimeSpan.FromMilliseconds(100), Forever).WaitAsync(Short);

        Assert.Equal(CdpLoadState.DomReady, state);
        Assert.Equal("потолок загрузки", Quiet(meter));
    }

    [Fact]
    public async Task Cancelled_quiet_is_marked_as_cancel()
    {
        var meter = CdpMeter.Start();
        using var watch = _page.WatchLoad();
        using var cts = new CancellationTokenSource();
        Lifecycle("S1", "F1", "L1", "DOMContentLoaded");

        var wait = watch.WaitForLoadAsync("F1", "L1", Forever, Forever, cts.Token);
        await Drain();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait.WaitAsync(Short));
        Assert.Equal("отмена", Quiet(meter));
    }

    [Fact]
    public async Task Dom_that_never_came_is_marked_as_not_arrived()
    {
        var meter = CdpMeter.Start();
        using var watch = _page.WatchLoad();

        Assert.Equal(CdpLoadState.NotLoaded,
            await watch.WaitForLoadAsync("F1", "L1", TimeSpan.FromMilliseconds(100), Forever).WaitAsync(Short));
        var dom = Assert.Single(meter.Stages);
        Assert.Equal(("DOMContentLoaded", "не пришёл"), (dom.Name, dom.Detail));
    }

    static string? Quiet(CdpMeter meter) => meter.Stages.Single(s => s.Name == "затишье").Detail;

    [Fact]
    public async Task Enable_learns_the_main_frame()
    {
        var enable = _page.EnableAsync();
        Reply(await Expect("Page.enable"), "{}");
        Reply(await Expect("Page.setLifecycleEventsEnabled"), "{}");
        Reply(await Expect("Page.getFrameTree"), """{"frameTree":{"frame":{"id":"MAIN","loaderId":"L0"},"childFrames":[]}}""");
        await enable;

        Assert.Equal("MAIN", _page.MainFrameId);
    }

    void Lifecycle(string session, string frame, string loader, string name) =>
        _pipe.Frame($$$"""{"method":"Page.lifecycleEvent","sessionId":"{{{session}}}","params":{"frameId":"{{{frame}}}","loaderId":"{{{loader}}}","name":"{{{name}}}"}}""");

    /// <summary>Все события до этой точки соединение уже разослало.</summary>
    async Task Drain()
    {
        var marker = _cdp.WaitForEventAsync("Marker", null, Short);
        _pipe.Frame("""{"method":"Marker"}""");
        await marker;
        await Task.Delay(50);
    }

    [Fact]
    public async Task Click_presses_and_releases_at_the_quad_center()
    {
        var click = _page.ClickAsync(15, 25);
        var types = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            var sent = await Expect("Input.dispatchMouseEvent");
            var p = sent.GetProperty("params");
            types.Add(p.GetProperty("type").GetString()!);
            Assert.Equal(15, p.GetProperty("x").GetDouble());
            Assert.Equal(25, p.GetProperty("y").GetDouble());
            Reply(sent, "{}");
        }
        await click;
        Assert.Equal(["mouseMoved", "mousePressed", "mouseReleased"], types);
        Assert.Equal((15, 25), CdpPage.QuadCenter([10, 20, 20, 20, 20, 30, 10, 30]));
    }

    [Fact]
    public async Task Box_model_content_quad_is_parsed()
    {
        var quad = _page.GetContentQuadAsync(7);
        var sent = await Expect("DOM.getBoxModel");
        Assert.Equal(7, sent.GetProperty("params").GetProperty("backendNodeId").GetInt32());
        Reply(sent, """{"model":{"content":[1,2,3,2,3,4,1,4],"width":2,"height":2}}""");

        var parsed = await quad;
        Assert.Equal(new double[] { 1, 2, 3, 2, 3, 4, 1, 4 }, parsed);
    }

    [Fact]
    public async Task Screenshot_is_decoded_from_base64()
    {
        var shot = _page.CaptureScreenshotAsync();
        var sent = await Expect("Page.captureScreenshot");
        Assert.Equal("png", sent.GetProperty("params").GetProperty("format").GetString());
        Reply(sent, "{\"data\":\"" + Convert.ToBase64String([0x89, 0x50, 0x4E, 0x47]) + "\"}");

        var png = await shot;
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, png);
    }

    [Fact]
    public async Task Dialog_event_of_the_page_reaches_handler()
    {
        var dialog = new TaskCompletionSource<CdpDialog>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var _ = _page.OnJavaScriptDialog(d => dialog.TrySetResult(d));

        _pipe.Frame("""{"method":"Page.javascriptDialogOpening","sessionId":"S1","params":{"type":"confirm","message":"Leave?","url":"https://a/"}}""");

        var d = await dialog.Task.WaitAsync(Short);
        Assert.Equal(("confirm", "Leave?"), (d.Type, d.Message));

        var handle = _page.HandleJavaScriptDialogAsync(accept: false);
        var sent = await Expect("Page.handleJavaScriptDialog");
        Assert.False(sent.GetProperty("params").GetProperty("accept").GetBoolean());
        Reply(sent, "{}");
        await handle;
    }

    async Task<JsonElement> Expect(string method)
    {
        var sent = await _pipe.NextSentAsync();
        Assert.Equal(method, sent.GetProperty("method").GetString());
        return sent;
    }

    void Reply(JsonElement sent, string resultJson) =>
        _pipe.Frame(Resp(sent.GetProperty("id").GetInt32(), resultJson));

    static string Resp(int id, string resultJson) => "{\"id\":" + id + ",\"result\":" + resultJson + "}";

    static string Err(int id, string errorJson) => "{\"id\":" + id + ",\"error\":" + errorJson + "}";
}
