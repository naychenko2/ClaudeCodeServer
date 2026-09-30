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
    public async Task Pause_without_text_is_capped_and_the_model_is_told_to_wait_for_text()
    {
        var session = new BrowserSession(_source, pauseCap: TimeSpan.FromMilliseconds(50));

        var reply = await session.WaitAsync(null, 30_000, CancellationToken.None);

        Assert.False(reply.IsError, reply.Text);
        Assert.StartsWith("Waited 50 ms instead of 30000 ms: a pause without text is capped at 50 ms.", reply.Text);
        Assert.Contains("already wait for the page to load", reply.Text);
        Assert.Contains("browser_wait with text='...'", reply.Text);
        Assert.True(reply.Timing!.Total < TimeSpan.FromSeconds(10), $"пауза {reply.Timing.Total}");
    }

    [Fact]
    public async Task Pause_under_the_cap_is_kept_as_asked()
    {
        var session = new BrowserSession(_source, pauseCap: TimeSpan.FromMilliseconds(50));

        var reply = await session.WaitAsync(null, 20, CancellationToken.None);

        Assert.Equal("Waited 20 ms.", reply.Text);
    }

    [Fact]
    public void Default_pause_cap_is_two_seconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(2), BrowserSession.MaxPause);
    }

    // ---------- этапы замера ----------

    [Fact]
    public async Task Navigate_log_line_splits_navigation_load_quiet_and_snapshot()
    {
        _source.Current.LoadDelay = TimeSpan.FromMilliseconds(150);

        var reply = await _session.NavigateAsync("https://example.org/", CancellationToken.None);

        var stages = reply.Timing!.Stages!;
        Assert.Equal(["Page.navigate", "DOMContentLoaded", "затишье", "снимок"], stages.Select(s => s.Name));
        Assert.True(stages[1].Elapsed >= TimeSpan.FromMilliseconds(100), $"DOMContentLoaded {stages[1].Elapsed}");
        Assert.Equal("load", stages[2].Detail);
        Assert.Matches(@"^AX-дерево \d+ мс, 3 узлов, сжатие \d+ мс$", stages[3].Detail!);

        var line = reply.LogLine("browser_navigate");
        Assert.Matches(@"\[Page\.navigate \d+ мс, DOMContentLoaded \d+ мс, затишье \d+ мс \(load\), " +
                       @"снимок \d+ мс \(AX-дерево \d+ мс, 3 узлов, сжатие \d+ мс\)\]; ответ \d+ симв\.", line);
    }

    [Fact]
    public async Task Quiet_that_ran_out_is_marked_as_the_ceiling()
    {
        _source.Current.Settles = false;

        var reply = await _session.NavigateAsync("https://example.org/", CancellationToken.None);

        var quiet = reply.Timing!.Stages!.Single(s => s.Name == "затишье");
        Assert.Equal("потолок", quiet.Detail);
    }

    [Fact]
    public void Log_line_prints_stages_in_order_with_details()
    {
        var timing = new BrowserTiming(TimeSpan.FromMilliseconds(3_000), TimeSpan.Zero, TimeSpan.Zero, 3,
            TimeSpan.FromMilliseconds(1_200), TimeSpan.FromMilliseconds(1_800),
            [
                new("Page.navigate", TimeSpan.FromMilliseconds(1_000)),
                new("DOMContentLoaded", TimeSpan.FromMilliseconds(800)),
                new("затишье", TimeSpan.FromMilliseconds(1_000), "потолок"),
                new("снимок", TimeSpan.FromMilliseconds(200), "AX-дерево 150 мс, 4000 узлов, сжатие 50 мс"),
            ]);

        var line = new BrowserReply("ok", Timing: timing).LogLine("browser_navigate");

        Assert.Equal("браузер: browser_navigate 3000 мс: очередь 0, браузер 0, CDP 3 выз. 1200 мс, ожидание страницы 1800 мс " +
                     "[Page.navigate 1000 мс, DOMContentLoaded 800 мс, затишье 1000 мс (потолок), " +
                     "снимок 200 мс (AX-дерево 150 мс, 4000 узлов, сжатие 50 мс)]; ответ 2 симв.", line);
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

    // ---------- browser_query ----------

    /// <summary>
    /// DOM по сценарию: документ 1, ссылки 11-13 по селектору <c>a</c>, кнопка снимка (backend 42)
    /// — узел 50, внутри неё <c>span</c> — узел 51.
    /// </summary>
    void UseDom(string? bigHtml = null)
    {
        var browser = _source.Current;
        browser.Override = (method, p) => method switch
        {
            "DOM.getDocument" => """{"root":{"nodeId":1,"backendNodeId":1,"nodeName":"#document"}}""",
            "DOM.pushNodesByBackendIdsToFrontend" => p.GetProperty("backendNodeIds")[0].GetInt32() == ButtonNode
                ? """{"nodeIds":[50]}"""
                : """{"nodeIds":[0]}""",
            "DOM.querySelectorAll" => (p.GetProperty("nodeId").GetInt32(), p.GetProperty("selector").GetString()) switch
            {
                (1, "a") => """{"nodeIds":[11,12,13]}""",
                (1, "article") => """{"nodeIds":[20]}""",
                (50, "span") => """{"nodeIds":[51]}""",
                _ => """{"nodeIds":[]}""",
            },
            "DOM.getOuterHTML" => JsonSerializer.Serialize(new
            {
                outerHTML = p.GetProperty("nodeId").GetInt32() switch
                {
                    11 => "<a href=\"/one\" class=\"r\">First <b>result</b></a>",
                    12 => "<a href=\"/two\">Second &amp; more</a>",
                    13 => "<a href=\"/three\">Third</a>",
                    20 => bigHtml ?? "<article><h1>Title</h1><script>evil()</script><p>Body&nbsp;text</p></article>",
                    50 => "<button>Go <span>now</span></button>",
                    51 => "<span>now</span>",
                    _ => "",
                },
            }),
            "DOM.describeNode" => p.GetProperty("nodeId").GetInt32() switch
            {
                11 => """{"node":{"nodeId":11,"localName":"a","nodeName":"A","attributes":["href","/one","class","r"]}}""",
                12 => """{"node":{"nodeId":12,"localName":"a","nodeName":"A","attributes":["href","/two"]}}""",
                _ => """{"node":{"nodeId":13,"localName":"a","nodeName":"A","attributes":["href","/three","title","say \"hi\""]}}""",
            },
            _ => null,
        };
        browser.Fail = (method, p) => method == "DOM.querySelectorAll" && p.GetProperty("selector").GetString() == "a[" ? "DOM Error while querying" : null;
    }

    [Fact]
    public async Task Query_text_reads_every_match_as_plain_text()
    {
        UseDom();

        var reply = await _session.QueryAsync("a", null, "text", null, CancellationToken.None);

        Assert.False(reply.IsError, reply.Text);
        Assert.Contains("URL: https://example.org/", reply.Text);
        Assert.Contains("3 elements match 'a':", reply.Text);
        Assert.Contains("[1] First result\n[2] Second & more\n[3] Third", reply.Text);
        Assert.Equal("S1", _source.Current.SessionsOf("DOM.querySelectorAll").Single());
        Assert.Empty(_source.Current.Sent("Runtime.evaluate"));
    }

    [Fact]
    public async Task Query_defaults_to_text_and_drops_scripts()
    {
        UseDom();

        var reply = await _session.QueryAsync("article", null, null, null, CancellationToken.None);

        Assert.Contains("1 element matches 'article':", reply.Text);
        Assert.Contains("[1] Title\nBody text", reply.Text);
        Assert.DoesNotContain("evil", reply.Text);
    }

    [Fact]
    public async Task Query_attributes_gives_tag_and_attributes()
    {
        UseDom();

        var reply = await _session.QueryAsync("a", null, "attributes", null, CancellationToken.None);

        Assert.Contains("[1] <a href=\"/one\" class=\"r\">", reply.Text);
        Assert.Contains("[3] <a href=\"/three\" title=\"say &quot;hi&quot;\">", reply.Text);
        Assert.Empty(_source.Current.Sent("DOM.getOuterHTML"));
    }

    [Fact]
    public async Task Query_html_gives_outer_html()
    {
        UseDom();

        var reply = await _session.QueryAsync("a", null, "html", 2, CancellationToken.None);

        Assert.Contains("3 elements match 'a' (showing the first 2):", reply.Text);
        Assert.Contains("[1] <a href=\"/one\" class=\"r\">First <b>result</b></a>", reply.Text);
        Assert.DoesNotContain("/three", reply.Text);
        Assert.Contains("[1 more elements match; raise limit", reply.Text);
        Assert.Equal(2, _source.Current.Sent("DOM.getOuterHTML").Count);
    }

    [Fact]
    public async Task Query_by_ref_reads_the_element_and_selector_searches_inside_it()
    {
        await Snapshot();
        UseDom();

        var element = await _session.QueryAsync(null, "e1", "html", null, CancellationToken.None);
        var inside = await _session.QueryAsync("span", "e1", "text", null, CancellationToken.None);

        Assert.Contains("1 element matches element e1:", element.Text);
        Assert.Contains("[1] <button>Go <span>now</span></button>", element.Text);
        Assert.Contains("1 element matches 'span' inside e1:", inside.Text);
        Assert.Contains("[1] now", inside.Text);
        Assert.Equal(ButtonNode, _source.Current.Sent("DOM.pushNodesByBackendIdsToFrontend")[0].GetProperty("backendNodeIds")[0].GetInt32());
        Assert.Equal(ButtonNode, _session.ResolveRef("e1"));
    }

    [Fact]
    public async Task Query_of_a_ref_gone_from_the_document_asks_for_a_fresh_snapshot()
    {
        await Snapshot();
        UseDom();
        _source.Current.Override = (method, _) => method == "DOM.pushNodesByBackendIdsToFrontend" ? """{"nodeIds":[0]}""" : null;

        var reply = await _session.QueryAsync(null, "e1", null, null, CancellationToken.None);

        Assert.True(reply.IsError);
        Assert.Contains("stale", reply.Text);
    }

    [Fact]
    public async Task Invalid_selector_and_no_matches_are_told_plainly()
    {
        UseDom();

        var bad = await _session.QueryAsync("a[", null, null, null, CancellationToken.None);
        var none = await _session.QueryAsync("table", null, null, null, CancellationToken.None);

        Assert.True(bad.IsError);
        Assert.Contains("Selector 'a[' was rejected by the page: DOM Error while querying", bad.Text);
        Assert.False(none.IsError);
        Assert.EndsWith("No elements match 'table'.", none.Text);
    }

    [Fact]
    public async Task Query_output_is_capped_and_the_cut_is_said_honestly()
    {
        UseDom("<article>" + string.Concat(Enumerable.Range(0, 3000).Select(i => $"<p>Paragraph number {i}</p>")) + "</article>");

        var reply = await _session.QueryAsync("article", null, "text", null, CancellationToken.None);

        Assert.True(reply.Text.Length <= BrowserSession.QueryBudgetChars, $"ответ {reply.Text.Length}");
        Assert.Contains("Paragraph number 0\n", reply.Text);
        Assert.DoesNotContain("Paragraph number 2999", reply.Text);
        Assert.EndsWith("[output cut at 20000 characters: 1 of 1 matching elements shown, the last one partly. " +
                        "Narrow the selector, lower limit or query a single element.]", reply.Text);
    }

    // ---------- browser_evaluate ----------

    void EvaluateReturns(string json) =>
        _source.Current.Override = (method, _) => method == "Runtime.evaluate" ? json : null;

    [Fact]
    public async Task Evaluate_runs_in_the_session_of_the_current_tab_and_returns_the_value()
    {
        EvaluateReturns("""{"result":{"type":"object","value":{"rows":3,"names":["a","b"]}}}""");

        var reply = await _session.EvaluateAsync("({rows: 3, names: ['a', 'b']})", CancellationToken.None);

        Assert.False(reply.IsError, reply.Text);
        Assert.Equal("Result (object):\n{\"rows\":3,\"names\":[\"a\",\"b\"]}", reply.Text);
        var browser = _source.Current;
        Assert.Equal(["S1"], browser.SessionsOf("Runtime.evaluate"));
        var sent = browser.Sent("Runtime.evaluate").Single();
        Assert.Equal("({rows: 3, names: ['a', 'b']})", sent.GetProperty("expression").GetString());
        Assert.True(sent.GetProperty("returnByValue").GetBoolean());
        Assert.True(sent.GetProperty("awaitPromise").GetBoolean());
        Assert.Equal((int)BrowserSession.EvaluateTimeout.TotalMilliseconds, sent.GetProperty("timeout").GetInt32());
    }

    [Fact]
    public async Task Evaluate_follows_the_current_tab_and_never_goes_to_the_browser_target()
    {
        EvaluateReturns("""{"result":{"type":"number","value":1}}""");
        await _session.EvaluateAsync("1", CancellationToken.None);
        await _session.TabsAsync("new", null, null, CancellationToken.None);

        await _session.EvaluateAsync("1", CancellationToken.None);

        Assert.Equal(["S1", "S2"], _source.Current.SessionsOf("Runtime.evaluate"));
    }

    [Theory]
    [InlineData("""{"result":{"type":"string","value":"Example Domain"}}""", "Result (string):\nExample Domain")]
    [InlineData("""{"result":{"type":"undefined"}}""", "Result (undefined):\nundefined")]
    [InlineData("""{"result":{"type":"object","subtype":"null","value":null}}""", "Result (null):\nnull")]
    [InlineData("""{"result":{"type":"number","unserializableValue":"NaN","description":"NaN"}}""", "Result (number):\nNaN")]
    [InlineData("""{"result":{"type":"boolean","value":true}}""", "Result (boolean):\ntrue")]
    public async Task Evaluate_formats_every_kind_of_value(string cdp, string expected)
    {
        EvaluateReturns(cdp);

        var reply = await _session.EvaluateAsync("x", CancellationToken.None);

        Assert.False(reply.IsError, reply.Text);
        Assert.Equal(expected, reply.Text);
    }

    [Fact]
    public async Task Page_exception_reaches_the_model_as_text_and_goes_to_the_log()
    {
        EvaluateReturns("""
            {"result":{"type":"object","subtype":"error"},
             "exceptionDetails":{"text":"Uncaught","lineNumber":0,"columnNumber":9,
               "exception":{"type":"object","subtype":"error","description":"TypeError: foo is not a function\n    at <anonymous>:1:10"}}}
            """);

        var reply = await _session.EvaluateAsync("undefined.foo()", CancellationToken.None);

        Assert.True(reply.IsError);
        Assert.Contains("The script threw an exception in the page:\nTypeError: foo is not a function", reply.Text);
        Assert.Contains("(at line 1, column 10 of the script)", reply.Text);
        var log = reply.LogLine("browser_evaluate");
        Assert.Contains("скрипт 15 симв.: undefined.foo() → исключение: The script threw", log);
    }

    [Fact]
    public async Task Hanging_script_is_abandoned_after_the_ceiling()
    {
        var session = new BrowserSession(_source, TimeSpan.FromMilliseconds(300));
        _source.Current.Silent.Add("Runtime.evaluate");

        var reply = await session.EvaluateAsync("new Promise(() => {})", CancellationToken.None);

        Assert.True(reply.IsError);
        Assert.Contains("did not finish within 0.3 s", reply.Text);
        Assert.Equal(300, _source.Current.Sent("Runtime.evaluate").Single().GetProperty("timeout").GetInt32());
        Assert.Contains("→ таймаут", reply.LogLine("browser_evaluate"));
    }

    [Fact]
    public async Task Value_that_cannot_be_returned_is_explained()
    {
        _source.Current.Fail = (method, _) => method == "Runtime.evaluate" ? "Object reference chain is too long" : null;

        var reply = await _session.EvaluateAsync("window", CancellationToken.None);

        Assert.True(reply.IsError);
        Assert.Contains("Object reference chain is too long", reply.Text);
        Assert.Contains("JSON.stringify", reply.Text);
    }

    [Fact]
    public async Task Big_result_is_cut_with_a_note()
    {
        var big = new string('x', BrowserSession.EvaluateResultBudgetChars + 5_000);
        EvaluateReturns(JsonSerializer.Serialize(new { result = new { type = "string", value = big } }));

        var reply = await _session.EvaluateAsync("'x'.repeat(25000)", CancellationToken.None);

        Assert.False(reply.IsError);
        Assert.True(reply.Text.Length < BrowserSession.EvaluateResultBudgetChars + 300, $"ответ {reply.Text.Length}");
        Assert.EndsWith($"[result cut at {BrowserSession.EvaluateResultBudgetChars} of {big.Length} characters: " +
                        "return less, for example a slice, a count or only the fields you need]", reply.Text);
    }

    [Fact]
    public async Task Long_script_is_clipped_in_the_log_line()
    {
        EvaluateReturns("""{"result":{"type":"number","value":1}}""");
        var script = "1;" + new string(' ', 5_000) + "\n2";

        var reply = await _session.EvaluateAsync(script, CancellationToken.None);

        var log = reply.LogLine("browser_evaluate");
        Assert.Contains($"скрипт {script.Length} симв.: 1;", log);
        Assert.True(log.Length < 3_000, $"строка лога {log.Length}");
        Assert.DoesNotContain('\n', log);
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
