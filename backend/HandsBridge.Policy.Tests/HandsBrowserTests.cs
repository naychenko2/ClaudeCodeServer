using ClaudeHomeServer.HandsBridge.Policy;
using Xunit;

namespace HandsBridge.Policy.Tests;

/// <summary>Гейт браузерной руки (ADR-016 §7.1, план Ш1): адреса, вкладки, ссылки, ожидание, снимок, профили.</summary>
public class HandsBrowserTests
{
    private const string ProfilesRoot = @"C:\Users\an\AppData\Local\ClaudeHome\Agent\browser-profiles";
    private const string Chrome = @"C:\Program Files\Google\Chrome\Application\chrome.exe";

    [Fact]
    public void Browser_tools_are_part_of_the_hands_surface()
    {
        string[] browser =
        [
            "browser_navigate", "browser_snapshot", "browser_click", "browser_type",
            "browser_tabs", "browser_wait", "browser_screenshot", "browser_query", "browser_evaluate",
        ];

        Assert.All(browser, t => Assert.Contains(t, HandsTools.All));
        Assert.Equal(HandsTools.All.Count, HandsTools.All.Distinct().Count());
        Assert.Equal(browser.Length, HandsTools.All.Count(t => t.StartsWith("browser_", StringComparison.Ordinal)));
    }

    [Fact]
    public void Vision_off_list_names_real_image_tools_of_the_bridge()
    {
        // Опечатка в списке агента молча оставила бы картинку провайдеру без зрения
        Assert.Equal([HandsTools.ScreenshotControl, HandsTools.BrowserScreenshot], ClaudeHomeServer.Protocol.HandsVision.ImageTools);
    }

    // ---------- адреса ----------

    [Theory]
    [InlineData("https://example.org", "https://example.org/")]
    [InlineData("http://example.org/a?q=1", "http://example.org/a?q=1")]
    [InlineData("HTTPS://Example.ORG/Path", "https://example.org/Path")]
    [InlineData("  https://example.org/  ", "https://example.org/")]
    [InlineData("https://ru.wikipedia.org/w/index.php?search=a b", "https://ru.wikipedia.org/w/index.php?search=a%20b")]
    [InlineData("about:blank", "about:blank")]
    [InlineData("ABOUT:BLANK", "about:blank")]
    public void Http_https_and_about_blank_are_opened_by_the_normalized_url(string requested, string expected)
    {
        var decision = HandsPolicy.CheckBrowserUrl(requested);

        Assert.True(decision.Allowed, decision.Reason);
        Assert.Equal(expected, decision.Url);
    }

    [Theory]
    [InlineData("file:///C:/")]
    [InlineData("file:///C:/Users/an/.ssh/id_rsa")]
    [InlineData("FILE:///C:/Windows")]
    [InlineData("file://server/share/secret.txt")]
    [InlineData("chrome://settings")]
    [InlineData("chrome://settings/passwords")]
    [InlineData("chrome-extension://abcdefghijklmnop/popup.html")]
    [InlineData("devtools://devtools/bundled/inspector.html")]
    [InlineData("javascript:alert(1)")]
    [InlineData("  JavaScript:alert(document.cookie)")]
    [InlineData("data:text/html,<script>alert(1)</script>")]
    [InlineData("data:text/html;base64,PHNjcmlwdD5hbGVydCgxKTwvc2NyaXB0Pg==")]
    [InlineData("view-source:https://example.org")]
    [InlineData("blob:https://example.org/0b1c2d3e")]
    [InlineData("about:srcdoc")]
    [InlineData("about:blank#x")]
    [InlineData("ftp://example.org/")]
    [InlineData("ws://example.org/")]
    [InlineData("C:\\Windows\\win.ini")]
    [InlineData("example.org")]
    [InlineData("//example.org")]
    [InlineData("http:/")]
    [InlineData("java\tscript:alert(1)")]
    [InlineData("https://exa\nmple.org")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Everything_else_is_denied(string? requested)
    {
        var decision = HandsPolicy.CheckBrowserUrl(requested);

        Assert.False(decision.Allowed, requested);
        Assert.Null(decision.Url);
        Assert.False(string.IsNullOrWhiteSpace(decision.Reason));
    }

    // ---------- вкладки ----------

    [Fact]
    public void New_tab_without_url_opens_about_blank()
    {
        var decision = HandsPolicy.CheckBrowserTabs("new", null, null);

        Assert.True(decision.Allowed, decision.Reason);
        Assert.Equal("about:blank", decision.Url);
    }

    [Fact]
    public void New_tab_url_goes_through_the_same_address_check()
    {
        Assert.Equal("https://example.org/", HandsPolicy.CheckBrowserTabs("new", "https://example.org", null).Url);
        Assert.False(HandsPolicy.CheckBrowserTabs("new", "file:///C:/", null).Allowed);
        Assert.False(HandsPolicy.CheckBrowserTabs("new", "data:text/html,x", null).Allowed);
    }

    [Theory]
    [InlineData("list", null)]
    [InlineData("select", "T1")]
    [InlineData("close", "T1")]
    public void Tab_actions_are_allowed(string action, string? tabId)
    {
        var decision = HandsPolicy.CheckBrowserTabs(action, null, tabId);

        Assert.True(decision.Allowed, decision.Reason);
        Assert.Null(decision.Url);
    }

    [Theory]
    [InlineData("select", null, null)]
    [InlineData("close", " ", null)]
    [InlineData("open", null, null)]
    [InlineData("LIST", null, null)]
    [InlineData(null, null, null)]
    [InlineData("list", null, "https://example.org")]
    [InlineData("select", "T1", "https://example.org")]
    public void Unknown_or_incomplete_tab_actions_are_denied(string? action, string? tabId, string? url)
    {
        Assert.False(HandsPolicy.CheckBrowserTabs(action, url, tabId).Allowed);
    }

    // ---------- ссылки снимка ----------

    private static int? Refs(string reference) => reference switch
    {
        "e1" => 101,
        "e12" => 112,
        _ => null,
    };

    [Theory]
    [InlineData("e1")]
    [InlineData("e12")]
    public void Known_ref_is_allowed(string reference)
    {
        Assert.True(HandsPolicy.CheckBrowserRef(HandsTools.BrowserClick, reference, Refs).Allowed);
    }

    [Fact]
    public void Unknown_or_stale_ref_asks_for_a_fresh_snapshot()
    {
        var decision = HandsPolicy.CheckBrowserRef(HandsTools.BrowserType, "e99", Refs);

        Assert.False(decision.Allowed);
        Assert.Contains("browser_snapshot", decision.Reason);
    }

    [Theory]
    [InlineData("12")]
    [InlineData("E12")]
    [InlineData("e")]
    [InlineData("e1x")]
    [InlineData("e-1")]
    [InlineData("ref=e12")]
    [InlineData("e12 ")]
    [InlineData("e١٢")]
    [InlineData("e12345678901")]
    [InlineData("")]
    [InlineData(null)]
    public void Malformed_ref_is_denied_without_asking_the_table(string? reference)
    {
        var asked = false;

        var decision = HandsPolicy.CheckBrowserRef(HandsTools.BrowserClick, reference, r =>
        {
            asked = true;
            return 1;
        });

        Assert.False(decision.Allowed);
        Assert.False(asked);
    }

    [Fact]
    public void Snapshot_without_ref_is_the_whole_page_and_with_ref_checks_it()
    {
        Assert.True(HandsPolicy.CheckBrowserSnapshot(null, Refs).Allowed);
        Assert.True(HandsPolicy.CheckBrowserSnapshot("e12", Refs).Allowed);
        Assert.False(HandsPolicy.CheckBrowserSnapshot("e99", Refs).Allowed);
        Assert.False(HandsPolicy.CheckBrowserSnapshot("body", Refs).Allowed);
    }

    // ---------- ожидание ----------

    [Theory]
    [InlineData("Results", null)]
    [InlineData("Results", 5000)]
    [InlineData(null, 1)]
    [InlineData(null, HandsPolicy.MaxBrowserWaitMs)]
    public void Wait_within_the_cap_is_allowed(string? text, int? ms)
    {
        Assert.True(HandsPolicy.CheckBrowserWait(text, ms).Allowed);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData(null, 0)]
    [InlineData(null, -1)]
    [InlineData(null, HandsPolicy.MaxBrowserWaitMs + 1)]
    [InlineData("Results", int.MaxValue)]
    public void Endless_or_empty_wait_is_denied(string? text, int? ms)
    {
        Assert.False(HandsPolicy.CheckBrowserWait(text, ms).Allowed);
    }

    // ---------- чтение DOM ----------

    [Theory]
    [InlineData("a.result", null, null, null)]
    [InlineData("table.prices tr", null, "text", 1)]
    [InlineData("a[href*=\"download\"]", null, "attributes", HandsPolicy.MaxBrowserQueryLimit)]
    [InlineData(null, "e12", "html", null)]
    [InlineData("li", "e1", "text", 50)]
    public void Query_by_selector_ref_or_both_is_allowed(string? selector, string? reference, string? mode, int? limit)
    {
        var decision = HandsPolicy.CheckBrowserQuery(selector, reference, mode, limit, Refs);

        Assert.True(decision.Allowed, decision.Reason);
    }

    [Fact]
    public void Query_selector_may_be_exactly_at_the_cap()
    {
        Assert.True(HandsPolicy.CheckBrowserQuery(new string('a', HandsPolicy.MaxBrowserSelectorChars), null, null, null, Refs).Allowed);
    }

    [Theory]
    [InlineData(null, null, null, null, "selector")]
    [InlineData("", null, null, null, "empty")]
    [InlineData("   ", null, null, null, "empty")]
    [InlineData("a\nb", null, null, null, "control")]
    [InlineData("a", null, "innerText", null, "mode")]
    [InlineData("a", null, "TEXT", null, "mode")]
    [InlineData("a", null, "evaluate", null, "mode")]
    [InlineData("a", null, null, 0, "limit")]
    [InlineData("a", null, null, -1, "limit")]
    [InlineData("a", null, null, HandsPolicy.MaxBrowserQueryLimit + 1, "limit")]
    [InlineData(null, "e99", null, null, "stale")]
    [InlineData(null, "body", null, null, "not a snapshot ref")]
    [InlineData("a", "e99", null, null, "stale")]
    public void Bad_query_arguments_are_denied(string? selector, string? reference, string? mode, int? limit, string reason)
    {
        var decision = HandsPolicy.CheckBrowserQuery(selector, reference, mode, limit, Refs);

        Assert.False(decision.Allowed);
        Assert.Contains(reason, decision.Reason);
    }

    [Fact]
    public void Too_long_selector_is_denied()
    {
        var decision = HandsPolicy.CheckBrowserQuery(new string('a', HandsPolicy.MaxBrowserSelectorChars + 1), null, null, null, Refs);

        Assert.False(decision.Allowed);
        Assert.Contains("longer", decision.Reason);
    }

    // ---------- JS модели ----------

    [Theory]
    [InlineData("document.title")]
    [InlineData("(() => {\n  const rows = [...document.querySelectorAll('tr')];\n  return rows.length;\n})()")]
    [InlineData("fetch('/api').then(r => r.status)")]
    public void Script_of_the_model_is_allowed(string script)
    {
        Assert.True(HandsPolicy.CheckBrowserEvaluate(script).Allowed);
    }

    [Fact]
    public void Script_may_be_exactly_at_the_cap()
    {
        Assert.True(HandsPolicy.CheckBrowserEvaluate(new string('1', HandsPolicy.MaxBrowserScriptChars)).Allowed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \n\t ")]
    public void Empty_script_is_denied(string? script)
    {
        var decision = HandsPolicy.CheckBrowserEvaluate(script);

        Assert.False(decision.Allowed);
        Assert.Contains("required", decision.Reason);
    }

    [Fact]
    public void Too_long_script_is_denied()
    {
        var decision = HandsPolicy.CheckBrowserEvaluate(new string('1', HandsPolicy.MaxBrowserScriptChars + 1));

        Assert.False(decision.Allowed);
        Assert.Contains("longer", decision.Reason);
    }

    [Fact]
    public void Javascript_and_data_urls_stay_denied_now_that_evaluate_exists()
    {
        Assert.False(HandsPolicy.CheckBrowserUrl("javascript:document.title").Allowed);
        Assert.False(HandsPolicy.CheckBrowserUrl("data:text/html,<script>1</script>").Allowed);
    }

    // ---------- снимок экрана ----------

    [Fact]
    public void Browser_screenshot_is_only_returned_inline()
    {
        Assert.True(HandsPolicy.CheckBrowserScreenshot(null).Allowed);
        Assert.True(HandsPolicy.CheckBrowserScreenshot("").Allowed);
        Assert.False(HandsPolicy.CheckBrowserScreenshot(@"C:\Users\an\Desktop\page.png").Allowed);
    }

    // ---------- профили браузерной руки в app ----------

    private static HandsPolicy WithProfiles() => new(new FakeWindows(), ProfilesRoot);

    [Theory]
    [InlineData(@"--user-data-dir=" + ProfilesRoot + @"\0123456789abcdef")]
    [InlineData(@"--user-data-dir=""" + ProfilesRoot + @"\0123456789abcdef""")]
    [InlineData(@"""--user-data-dir=" + ProfilesRoot + @"\0123456789abcdef""")]
    [InlineData(@"-user-data-dir=" + ProfilesRoot + @"\p")]
    [InlineData(@"/user-data-dir=" + ProfilesRoot + @"\p")]
    [InlineData(@"--USER-DATA-DIR=" + ProfilesRoot + @"\p")]
    [InlineData(@"--user-data-dir=c:/users/AN/appdata/local/claudehome/agent/BROWSER-PROFILES/p")]
    [InlineData(@"--user-data-dir=C:\Users\an\AppData\Local\ClaudeHome\Agent\x\..\browser-profiles\p")]
    [InlineData(@"--user-data-dir=C:\Users\an\AppData\Local\ClaudeHome\Agent\browser-profiles.\p")]
    [InlineData(@"--user-data-dir=" + ProfilesRoot)]
    [InlineData(@"https://example.org --new-window --user-data-dir=" + ProfilesRoot + @"\p")]
    [InlineData(@"--user-data-""dir=" + ProfilesRoot + @"\p""")]
    public void App_does_not_open_a_hands_browser_profile(string arguments)
    {
        var decision = WithProfiles().CheckLaunch(Chrome, arguments);

        Assert.False(decision.Allowed, arguments);
        Assert.Contains("browser_", decision.Reason);
    }

    [Theory]
    [InlineData(@"--user-data-dir=browser-profiles\p")]
    [InlineData(@"--user-data-dir=..\browser-profiles\p")]
    [InlineData(@"--user-data-dir=%LOCALAPPDATA%\ClaudeHome\Agent\browser-profiles\p")]
    [InlineData(@"--user-data-dir=\\?\C:\Users\an\AppData\Local\ClaudeHome\Agent\browser-profiles\p")]
    [InlineData(@"--user-data-dir=")]
    public void Unverifiable_user_data_dir_is_denied(string arguments)
    {
        var decision = WithProfiles().CheckLaunch(Chrome, arguments);

        Assert.False(decision.Allowed, arguments);
        Assert.Contains("full path", decision.Reason);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("https://example.org")]
    [InlineData(@"--user-data-dir=C:\Temp\chrome-profile")]
    [InlineData(@"--user-data-dir=""C:\Users\an\My Chrome""")]
    [InlineData(@"--user-data-dir=" + ProfilesRoot + @"-other\p")]
    [InlineData(@"--user-data-dir-extra=" + ProfilesRoot + @"\p")]
    [InlineData(@"user-data-dir=" + ProfilesRoot + @"\p")]
    public void Other_profiles_are_still_allowed_narrow_ban(string? arguments)
    {
        var decision = WithProfiles().CheckLaunch(Chrome, arguments);

        Assert.True(decision.Allowed, decision.Reason);
    }

    [Fact]
    public void Without_a_browser_hand_there_is_no_profile_to_protect()
    {
        var decision = new HandsPolicy(new FakeWindows()).CheckLaunch(Chrome, @"--user-data-dir=" + ProfilesRoot + @"\p");

        Assert.True(decision.Allowed, decision.Reason);
    }

    [Theory]
    [InlineData("browser-profiles")]
    [InlineData(@"\\server\share\browser-profiles")]
    [InlineData("")]
    public void Unusable_profiles_root_fails_at_construction_instead_of_disabling_the_ban(string root)
    {
        Assert.Throws<ArgumentException>(() => new HandsPolicy(new FakeWindows(), root));
    }

    // ---------- разбор аргументов ----------

    [Theory]
    [InlineData(@"a b", new[] { "a", "b" })]
    [InlineData("  a\t\tb  ", new[] { "a", "b" })]
    [InlineData(@"""a b"" c", new[] { "a b", "c" })]
    [InlineData(@"--x=""C:\My Dir"" y", new[] { @"--x=C:\My Dir", "y" })]
    [InlineData(@"a\\b", new[] { @"a\\b" })]
    [InlineData(@"a\\""b c""", new[] { @"a\b c" })]
    [InlineData(@"a\""b", new[] { @"a""b" })]
    [InlineData(@"""a""""b""", new[] { @"a""b" })]
    [InlineData(@"""""", new[] { "" })]
    [InlineData(@"C:\dir\", new[] { @"C:\dir\" })]
    public void Arguments_are_split_like_CommandLineToArgvW(string commandLine, string[] expected)
    {
        Assert.Equal(expected, HandsArguments.Split(commandLine));
    }
}
