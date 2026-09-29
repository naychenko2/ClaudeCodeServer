using System.Text;
using System.Text.Json;
using ClaudeHomeServer.HandsBridge.Browser.Cdp;
using Xunit;

namespace HandsBridge.Policy.Tests;

/// <summary>
/// Протокол CDP на подделке трубы (ADR-016 §7.1, план Ш3): рамки, корреляция, события, обрыв.
/// Таймауты короткие намеренно: сломанная рамка обязана дать красный тест, а не зависший прогон.
/// </summary>
public sealed class CdpConnectionTests : IAsyncLifetime
{
    static readonly TimeSpan Short = TimeSpan.FromSeconds(5);

    readonly FakeCdpTransport _pipe = new();
    readonly CdpConnection _cdp;

    public CdpConnectionTests() => _cdp = new CdpConnection(_pipe, Short);

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _cdp.DisposeAsync();

    [Fact]
    public async Task Command_is_written_as_json_frame_terminated_by_zero()
    {
        var call = _cdp.SendAsync("DOM.focus", new() { ["backendNodeId"] = 42 }, "S1");

        var sent = await _pipe.NextSentAsync();
        Assert.Equal("DOM.focus", sent.GetProperty("method").GetString());
        Assert.Equal("S1", sent.GetProperty("sessionId").GetString());
        Assert.Equal(42, sent.GetProperty("params").GetProperty("backendNodeId").GetInt32());

        _pipe.Frame(Resp(Id(sent), """{"ok":1}"""));
        Assert.Equal(1, (await call).GetProperty("ok").GetInt32());
    }

    [Fact]
    public async Task Command_without_params_sends_empty_object_and_no_session()
    {
        var call = _cdp.SendAsync("Browser.getVersion");
        var sent = await _pipe.NextSentAsync();

        Assert.Equal(JsonValueKind.Object, sent.GetProperty("params").ValueKind);
        Assert.False(sent.TryGetProperty("sessionId", out _));
        _pipe.Frame("{\"id\":" + Id(sent) + "}");
        Assert.Equal(JsonValueKind.Object, (await call).ValueKind);
    }

    [Fact]
    public async Task Responses_out_of_order_complete_their_own_commands()
    {
        var first = _cdp.SendAsync("A");
        var a = await _pipe.NextSentAsync();
        var second = _cdp.SendAsync("B");
        var b = await _pipe.NextSentAsync();

        _pipe.Frame(Resp(Id(b), """{"who":"B"}"""));
        Assert.Equal("B", (await second).GetProperty("who").GetString());
        Assert.False(first.IsCompleted);

        _pipe.Frame(Resp(Id(a), """{"who":"A"}"""));
        Assert.Equal("A", (await first).GetProperty("who").GetString());
    }

    [Fact]
    public async Task Events_interleaved_with_responses_reach_only_matching_subscribers()
    {
        var page = new List<string>();
        var browser = new List<string>();
        using var s1 = _cdp.Subscribe("Page.frameNavigated", "S1", e => page.Add(e.Params.GetProperty("n").GetString()!));
        using var s2 = _cdp.Subscribe("Page.frameNavigated", null, e => browser.Add(e.Params.GetProperty("n").GetString()!));

        // Маркер последней рамкой: цикл чтения разбирает по порядку, дошёл маркер — дошло всё
        var marker = _cdp.WaitForEventAsync("Marker", null, Short);
        var call = _cdp.SendAsync("X");
        var sent = await _pipe.NextSentAsync();
        _pipe.Frame("""{"method":"Page.frameNavigated","sessionId":"S1","params":{"n":"page-1"}}""");
        _pipe.Frame("""{"method":"Page.frameNavigated","sessionId":"S2","params":{"n":"other"}}""");
        _pipe.Frame("""{"method":"Page.frameNavigated","params":{"n":"browser"}}""");
        _pipe.Frame(Resp(Id(sent), """{"done":true}"""));
        _pipe.Frame("""{"method":"Page.frameNavigated","sessionId":"S1","params":{"n":"page-2"}}""");
        _pipe.Frame("""{"method":"Marker"}""");

        Assert.True((await call).GetProperty("done").GetBoolean());
        await marker;
        Assert.Equal(["page-1", "page-2"], page);
        Assert.Equal(["browser"], browser);
    }

    [Fact]
    public async Task Wait_for_event_is_registered_before_the_triggering_command()
    {
        var load = _cdp.WaitForEventAsync("Page.loadEventFired", "S1", Short,
            p => p.GetProperty("timestamp").GetInt32() == 2);
        _pipe.Frame("""{"method":"Page.loadEventFired","sessionId":"S1","params":{"timestamp":1}}""");
        _pipe.Frame("""{"method":"Page.loadEventFired","sessionId":"S1","params":{"timestamp":2}}""");

        Assert.Equal(2, (await load).GetProperty("timestamp").GetInt32());
    }

    [Fact]
    public async Task Error_response_throws_protocol_exception_with_code_and_message()
    {
        var call = _cdp.SendAsync("DOM.getBoxModel");
        var sent = await _pipe.NextSentAsync();
        _pipe.Frame(Err(Id(sent), """{"code":-32000,"message":"Could not compute box model."}"""));

        var ex = await Assert.ThrowsAsync<CdpProtocolException>(() => call);
        Assert.Equal(-32000, ex.Code);
        Assert.Equal("DOM.getBoxModel", ex.Method);
        Assert.Contains("Could not compute box model.", ex.Message);
    }

    [Fact]
    public async Task Pipe_eof_while_waiting_fails_everything_pending_instead_of_hanging()
    {
        var call = _cdp.SendAsync("Page.navigate");
        await _pipe.NextSentAsync();
        var evt = _cdp.WaitForEventAsync("Page.loadEventFired", "S1", TimeSpan.FromMinutes(5));

        _pipe.EndOfStream();

        await Assert.ThrowsAsync<CdpDisconnectedException>(() => call).WaitAsync(Short);
        await Assert.ThrowsAsync<CdpDisconnectedException>(() => evt).WaitAsync(Short);
        Assert.True(_cdp.IsClosed);
        // После обрыва новая команда отказывает сразу, а не ждёт таймаута
        await Assert.ThrowsAsync<CdpDisconnectedException>(() => _cdp.SendAsync("Next", timeout: TimeSpan.FromMinutes(5)))
            .WaitAsync(Short);
    }

    [Fact]
    public async Task Browser_process_exit_fails_pending_commands()
    {
        var call = _cdp.SendAsync("Browser.getVersion", timeout: TimeSpan.FromMinutes(5));
        await _pipe.NextSentAsync();

        _pipe.Exit();

        var ex = await Assert.ThrowsAsync<CdpDisconnectedException>(() => call).WaitAsync(Short);
        Assert.Contains("exited", ex.Reason);
    }

    [Fact]
    public async Task Write_failure_disconnects_instead_of_waiting_for_a_response()
    {
        _pipe.BreakWrites();

        await Assert.ThrowsAsync<CdpDisconnectedException>(
            () => _cdp.SendAsync("X", timeout: TimeSpan.FromMinutes(5))).WaitAsync(Short);
        Assert.True(_cdp.IsClosed);
    }

    [Fact]
    public async Task Malformed_frame_disconnects()
    {
        var call = _cdp.SendAsync("X", timeout: TimeSpan.FromMinutes(5));
        await _pipe.NextSentAsync();

        _pipe.Frame("{not json");

        await Assert.ThrowsAsync<CdpDisconnectedException>(() => call).WaitAsync(Short);
    }

    [Fact]
    public async Task Two_frames_in_one_read_are_both_dispatched()
    {
        var first = _cdp.SendAsync("A");
        var a = await _pipe.NextSentAsync();
        var second = _cdp.SendAsync("B");
        var b = await _pipe.NextSentAsync();

        _pipe.Push(Resp(Id(a), """{"v":1}""") + "\0" + Resp(Id(b), """{"v":2}""") + "\0");

        Assert.Equal(1, (await first).GetProperty("v").GetInt32());
        Assert.Equal(2, (await second).GetProperty("v").GetInt32());
    }

    [Fact]
    public async Task One_frame_across_three_reads_is_glued_even_inside_a_utf8_character()
    {
        var call = _cdp.SendAsync("Accessibility.getFullAXTree");
        var sent = await _pipe.NextSentAsync();
        var bytes = Encoding.UTF8.GetBytes(Resp(Id(sent), """{"name":"Войти"}""") + "\0");
        var cyrillic = Encoding.UTF8.GetByteCount("{\"id\":" + Id(sent) + ",\"result\":{\"name\":\"В");
        var cut1 = cyrillic - 1;     // посередине двухбайтовой «В»
        var cut2 = bytes.Length - 3;

        _pipe.Push(bytes[..cut1]);
        _pipe.Push(bytes[cut1..cut2]);
        Assert.False(call.IsCompleted);
        _pipe.Push(bytes[cut2..]);

        Assert.Equal("Войти", (await call).GetProperty("name").GetString());
    }

    [Fact]
    public async Task Tail_of_a_frame_and_next_frame_in_one_read()
    {
        var first = _cdp.SendAsync("A");
        var a = await _pipe.NextSentAsync();
        var second = _cdp.SendAsync("B");
        var b = await _pipe.NextSentAsync();
        var one = Resp(Id(a), """{"v":1}""") + "\0";
        var two = Resp(Id(b), """{"v":2}""") + "\0";

        _pipe.Push(one[..10]);
        _pipe.Push(one[10..] + two[..5]);
        _pipe.Push(two[5..]);

        Assert.Equal(1, (await first).GetProperty("v").GetInt32());
        Assert.Equal(2, (await second).GetProperty("v").GetInt32());
    }

    [Fact]
    public async Task Command_without_response_times_out_and_a_late_answer_is_ignored()
    {
        var call = _cdp.SendAsync("Page.navigate", timeout: TimeSpan.FromMilliseconds(200));
        var sent = await _pipe.NextSentAsync();

        var ex = await Assert.ThrowsAsync<CdpTimeoutException>(() => call);
        Assert.Equal("Page.navigate", ex.Method);

        _pipe.Frame(Resp(Id(sent), """{}"""));
        var next = _cdp.SendAsync("Next");
        var nextSent = await _pipe.NextSentAsync();
        _pipe.Frame(Resp(Id(nextSent), """{"alive":true}"""));
        Assert.True((await next).GetProperty("alive").GetBoolean());
        Assert.False(_cdp.IsClosed);
    }

    [Fact]
    public async Task Throwing_subscriber_does_not_break_the_read_loop()
    {
        using var bad = _cdp.Subscribe("E", null, _ => throw new InvalidOperationException("boom"));
        var got = _cdp.WaitForEventAsync("E", null, Short);

        _pipe.Frame("""{"method":"E","params":{"x":1}}""");

        Assert.Equal(1, (await got).GetProperty("x").GetInt32());
        Assert.False(_cdp.IsClosed);
    }

    static int Id(JsonElement sent) => sent.GetProperty("id").GetInt32();

    static string Resp(int id, string resultJson) => "{\"id\":" + id + ",\"result\":" + resultJson + "}";

    static string Err(int id, string errorJson) => "{\"id\":" + id + ",\"error\":" + errorJson + "}";
}
