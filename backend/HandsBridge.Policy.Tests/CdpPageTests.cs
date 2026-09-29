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

    [Fact]
    public async Task Navigate_waits_for_load_of_its_own_session_only()
    {
        var nav = _page.NavigateAsync("https://example.org/", Short);

        var sent = await Expect("Page.navigate");
        Assert.Equal("S1", sent.GetProperty("sessionId").GetString());
        Reply(sent, """{"frameId":"F1","loaderId":"L1"}""");
        var marker = _cdp.WaitForEventAsync("Marker", null, Short);
        _pipe.Frame("""{"method":"Page.loadEventFired","sessionId":"S2","params":{}}""");
        _pipe.Frame("""{"method":"Marker"}""");
        await marker;
        Assert.False(nav.IsCompleted);

        _pipe.Frame("""{"method":"Page.loadEventFired","sessionId":"S1","params":{}}""");
        var result = await nav;
        Assert.True(result.Loaded);
        Assert.Null(result.ErrorText);
        Assert.Equal("F1", result.FrameId);
    }

    [Fact]
    public async Task Navigate_returns_error_text_without_waiting_for_load()
    {
        var nav = _page.NavigateAsync("https://nowhere.invalid/", TimeSpan.FromMinutes(5));
        Reply(await Expect("Page.navigate"), """{"frameId":"F1","loaderId":"L1","errorText":"net::ERR_NAME_NOT_RESOLVED"}""");

        var result = await nav.WaitAsync(Short);
        Assert.Equal("net::ERR_NAME_NOT_RESOLVED", result.ErrorText);
        Assert.False(result.Loaded);
    }

    [Fact]
    public async Task Navigate_reports_not_loaded_when_load_does_not_come_in_time()
    {
        var nav = _page.NavigateAsync("https://slow.example/", TimeSpan.FromMilliseconds(200));
        Reply(await Expect("Page.navigate"), """{"frameId":"F1","loaderId":"L1"}""");

        Assert.False((await nav.WaitAsync(Short)).Loaded);
    }

    [Fact]
    public async Task Same_document_navigation_does_not_wait_for_load()
    {
        var nav = _page.NavigateAsync("https://example.org/#top", TimeSpan.FromMinutes(5));
        Reply(await Expect("Page.navigate"), """{"frameId":"F1"}""");

        Assert.True((await nav.WaitAsync(Short)).Loaded);
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
