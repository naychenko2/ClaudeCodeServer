using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using ClaudeHomeServer.HandsBridge.Browser.Snapshot;
using ClaudeHomeServer.HandsBridge.Policy;
using Xunit;
using Xunit.Abstractions;

namespace HandsBridge.Policy.Tests;

/// <summary>
/// Сжатие AX-дерева в снимок браузерной руки (ADR-016 §7.1, план Ш2). Фикстуры — ответы
/// <c>Accessibility.getFullAXTree</c> реальных страниц, снятые Chrome 153 при окне 1280×800
/// (из узлов убраны только поля, которые форматтер не читает: sources, chromeRole,
/// ignoredReasons). Рядом с каждым деревом — backendDOMNodeId интерактивных элементов,
/// видимых на первом экране, по <c>DOM.getBoxModel</c>.
/// </summary>
public partial class AxSnapshotFormatterTests(ITestOutputHelper output)
{
    // Страница, узлов на входе, потолок строк на выходе — регрессия бюджета
    public static TheoryData<string, int, int> Fixtures => new()
    {
        { "simple-page", 1569, 10 },       // example.com
        { "login-form", 136, 40 },         // github.com/login
        { "long-article", 20936, 320 },    // en.wikipedia.org/wiki/Alan_Turing
        { "link-list", 1624, 400 },        // news.ycombinator.com
    };

    public static TheoryData<string> Pages => ["simple-page", "login-form", "long-article", "link-list"];

    private static readonly HashSet<string> Interactive =
    [
        "link", "button", "textbox", "searchbox", "combobox", "checkbox", "radio", "menuitem",
        "menuitemcheckbox", "menuitemradio", "tab", "option", "switch", "slider", "spinbutton", "treeitem",
    ];

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Snapshot_of_real_page_fits_the_budget(string page, int inputNodes, int maxLines)
    {
        var snapshot = AxSnapshotFormatter.Format(LoadTree(page));

        output.WriteLine($"{page}: узлов на входе {snapshot.InputNodes}, строк на выходе {snapshot.Lines}, " +
            $"символов {snapshot.Text.Length}, ссылок {snapshot.Refs.Count}, обрезано {snapshot.TruncatedNodes}");
        Assert.Equal(inputNodes, snapshot.InputNodes);
        Assert.True(snapshot.Text.Length <= AxSnapshotFormatter.DefaultBudgetChars, $"{snapshot.Text.Length} символов");
        Assert.True(snapshot.Lines <= maxLines, $"{snapshot.Lines} строк");
        Assert.Equal(snapshot.Lines, snapshot.Text.Split('\n').Length);
    }

    [Theory]
    [MemberData(nameof(Pages))]
    public void Every_line_follows_the_snapshot_format(string page)
    {
        var snapshot = AxSnapshotFormatter.Format(LoadTree(page));

        Assert.All(snapshot.Text.Split('\n'), line => Assert.Matches(LineFormat(), line));
    }

    [Theory]
    [MemberData(nameof(Pages))]
    public void Refs_are_accepted_by_the_gate_and_point_only_to_interactive_nodes(string page)
    {
        var tree = LoadTree(page);
        var snapshot = AxSnapshotFormatter.Format(tree);
        var roleByBackendId = InteractiveBackendIds(tree);
        var printed = RefInText().Matches(snapshot.Text).Select(m => m.Groups["ref"].Value).ToList();

        Assert.NotEmpty(printed);
        Assert.Equal(printed.Count, printed.Distinct().Count());
        Assert.Equal(printed.Order(), snapshot.Refs.Keys.Order());
        int? Resolve(string r) => snapshot.Refs.TryGetValue(r, out var id) ? id : null;
        Assert.All(printed, r => Assert.True(HandsPolicy.CheckBrowserRef(HandsTools.BrowserClick, r, Resolve).Allowed, r));
        Assert.All(snapshot.Refs.Values, id => Assert.Contains(id, roleByBackendId));

        // Ссылка стоит только на строке интерактивной роли
        Assert.All(snapshot.Text.Split('\n').Where(l => l.Contains("[ref=", StringComparison.Ordinal)),
            line => Assert.Contains(RoleOf(line), Interactive));
    }

    [Theory]
    [MemberData(nameof(Pages))]
    public void Same_tree_gives_the_same_snapshot_and_refs(string page)
    {
        var first = AxSnapshotFormatter.Format(LoadTree(page));
        var second = AxSnapshotFormatter.Format(LoadTree(page));

        Assert.Equal(first.Text, second.Text);
        Assert.Equal(first.Refs, second.Refs);
    }

    [Theory]
    [InlineData("long-article")]
    [InlineData("login-form")]
    [InlineData("simple-page")]
    [InlineData("link-list")]
    public void First_screen_links_and_buttons_keep_their_refs(string page)
    {
        var snapshot = AxSnapshotFormatter.Format(LoadTree(page));
        var firstScreen = JsonSerializer.Deserialize<int[]>(File.ReadAllText(FixturePath($"{page}.first-screen.json")))!;

        var missing = firstScreen.Except(snapshot.Refs.Values).ToList();
        Assert.True(missing.Count == 0, $"без ссылки {missing.Count} из {firstScreen.Length}: {string.Join(", ", missing)}");
    }

    [Fact]
    public void Long_article_is_cut_with_a_tail_that_says_how_to_narrow()
    {
        var snapshot = AxSnapshotFormatter.Format(LoadTree("long-article"));

        Assert.True(snapshot.TruncatedNodes > 0);
        var tail = snapshot.Text.Split('\n')[^1];
        Assert.Contains($"{snapshot.TruncatedNodes} more elements not shown", tail);
        Assert.Contains("browser_snapshot with the ref", tail);
    }

    [Fact]
    public void Login_form_keeps_fields_and_submit_with_refs()
    {
        var text = AxSnapshotFormatter.Format(LoadTree("login-form")).Text;

        Assert.Matches(@"- textbox ""Username or email address"" \[ref=e\d+\]", text);
        Assert.Matches(@"- textbox ""Password"" \[ref=e\d+\]", text);
        Assert.Matches(@"- button ""Sign in"" \[ref=e\d+\]", text);
    }

    [Fact]
    public void Letter_by_letter_text_is_glued_into_one_line()
    {
        // example.com отдаёт абзац сотней StaticText по одной букве
        var text = AxSnapshotFormatter.Format(LoadTree("simple-page")).Text;

        Assert.Contains("This domain is for", text);
    }

    // ---------- синтетика: правила по одному ----------

    [Fact]
    public void Ignored_and_unnamed_generic_nodes_dissolve_into_their_parent()
    {
        var tree = Tree(
            N("1", "RootWebArea", "Page", children: ["2"]),
            N("2", "none", ignored: true, children: ["3"]),
            N("3", "generic", children: ["4", "5"]),
            N("4", "heading", "Title", props: [("level", 1)], children: ["6"]),
            N("6", "StaticText", "Title"),
            N("5", "button", "Go", backend: 50));

        var snapshot = AxSnapshotFormatter.Format(tree);

        Assert.Equal("- heading \"Title\" [level=1]\n- button \"Go\" [ref=e1]", snapshot.Text);
        Assert.Equal(50, snapshot.Refs["e1"]);
    }

    [Fact]
    public void Chain_of_single_children_collapses_to_the_meaningful_node()
    {
        var tree = Tree(
            N("1", "RootWebArea", "Page", children: ["2", "9"]),
            N("2", "list", children: ["3"]),
            N("3", "listitem", children: ["4"]),
            N("4", "link", "Home", backend: 40, children: ["5"]),
            N("5", "StaticText", "Home"),
            N("9", "paragraph", children: ["10"]),
            N("10", "StaticText", "Hello   world"));

        var snapshot = AxSnapshotFormatter.Format(tree);

        Assert.Equal("- link \"Home\" [ref=e1]\n- paragraph: Hello world", snapshot.Text);
    }

    [Fact]
    public void Paragraph_with_link_keeps_text_around_it()
    {
        var tree = Tree(
            N("1", "RootWebArea", "Page", children: ["2"]),
            N("2", "paragraph", children: ["3", "4", "5"]),
            N("3", "StaticText", "Read "),
            N("4", "link", "the docs", backend: 7, children: ["6"]),
            N("6", "StaticText", "the docs"),
            N("5", "StaticText", " first"));

        var snapshot = AxSnapshotFormatter.Format(tree);

        Assert.Equal("- paragraph\n  - text: Read\n  - link \"the docs\" [ref=e1]\n  - text: first", snapshot.Text);
    }

    [Fact]
    public void Field_state_and_value_are_printed()
    {
        var tree = Tree(
            N("1", "RootWebArea", "Page", children: ["2", "3", "4"]),
            N("2", "textbox", "Email", backend: 2, value: "a@b.c"),
            N("3", "checkbox", "Remember", backend: 3, props: [("checked", "true")]),
            N("4", "button", "More \"x\"", backend: 4, props: [("expanded", false), ("disabled", true)]));

        var text = AxSnapshotFormatter.Format(tree).Text;

        Assert.Equal(
            "- textbox \"Email\" [ref=e1]: a@b.c\n" +
            "- checkbox \"Remember\" [checked] [ref=e2]\n" +
            "- button \"More \\\"x\\\"\" [collapsed] [disabled] [ref=e3]",
            text);
    }

    [Fact]
    public void Interactive_node_without_backend_id_gets_no_ref()
    {
        var tree = Tree(
            N("1", "RootWebArea", "Page", children: ["2"]),
            N("2", "button", "Ghost"));

        var snapshot = AxSnapshotFormatter.Format(tree);

        Assert.Equal("- button \"Ghost\"", snapshot.Text);
        Assert.Empty(snapshot.Refs);
    }

    [Fact]
    public void Over_budget_tree_is_cut_and_refs_of_cut_nodes_are_not_issued()
    {
        var ids = Enumerable.Range(2, 200).Select(i => i.ToString()).ToArray();
        var nodes = new List<JsonElement> { N("1", "RootWebArea", "Page", children: ids) };
        nodes.AddRange(ids.Select(i => N(i, "link", $"Link number {i}", backend: int.Parse(i))));

        var snapshot = AxSnapshotFormatter.Format(Tree([.. nodes]), budgetChars: 1_000);

        Assert.True(snapshot.Text.Length <= 1_000, $"{snapshot.Text.Length}");
        Assert.Equal(200, snapshot.Refs.Count + snapshot.TruncatedNodes);
        Assert.Equal(snapshot.Lines - 1, snapshot.Refs.Count);
        Assert.StartsWith($"- ... {snapshot.TruncatedNodes} more elements not shown", snapshot.Text.Split('\n')[^1]);
    }

    [Fact]
    public void Subtree_snapshot_starts_at_the_given_element()
    {
        var tree = Tree(
            N("1", "RootWebArea", "Page", children: ["2", "3"]),
            N("2", "button", "Outside", backend: 2),
            N("3", "navigation", "Menu", backend: 3, children: ["4"]),
            N("4", "link", "Inside", backend: 4));

        var snapshot = AxSnapshotFormatter.Format(tree, rootBackendNodeId: 3);

        Assert.Equal("- navigation \"Menu\"\n  - link \"Inside\" [ref=e1]", snapshot.Text);
        Assert.Equal(4, snapshot.Refs["e1"]);
    }

    [Fact]
    public void Unknown_subtree_root_asks_for_a_fresh_snapshot()
    {
        var tree = Tree(N("1", "RootWebArea", "Page"));

        var snapshot = AxSnapshotFormatter.Format(tree, rootBackendNodeId: 999);

        Assert.Contains("fresh browser_snapshot", snapshot.Text);
        Assert.Empty(snapshot.Refs);
    }

    // ---------- помощники ----------

    [GeneratedRegex(@"^(  )*- (text: .+|[A-Za-z]+( ""([^""\\]|\\.)*"")?( \[[a-z]+(=[a-z0-9]+)?\])*( \[ref=e[1-9][0-9]*\])?(: .+)?|\.\.\. .+)$")]
    private static partial Regex LineFormat();

    [GeneratedRegex(@"\[ref=(?<ref>e\d+)\]")]
    private static partial Regex RefInText();

    private static string RoleOf(string line) => line.TrimStart()[2..].Split(' ', ':')[0];

    private static string FixturePath(string file) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "AxTrees", file);

    private static JsonElement LoadTree(string page)
    {
        using var file = File.OpenRead(FixturePath($"{page}.json.gz"));
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        return JsonDocument.Parse(gzip).RootElement.Clone();
    }

    private static HashSet<int> InteractiveBackendIds(JsonElement tree) =>
        tree.GetProperty("nodes").EnumerateArray()
            .Where(n => n.TryGetProperty("backendDOMNodeId", out _)
                && n.TryGetProperty("role", out var r) && Interactive.Contains(r.GetProperty("value").GetString()!))
            .Select(n => n.GetProperty("backendDOMNodeId").GetInt32())
            .ToHashSet();

    private static JsonElement Tree(params JsonElement[] nodes) =>
        JsonSerializer.SerializeToElement(new { nodes });

    private static JsonElement N(
        string id, string role, string? name = null, bool ignored = false, int? backend = null,
        string? value = null, string[]? children = null, (string Name, object Value)[]? props = null)
    {
        var node = new Dictionary<string, object?>
        {
            ["nodeId"] = id,
            ["ignored"] = ignored,
            ["role"] = new { type = "role", value = role },
            ["childIds"] = children ?? [],
        };
        if (name is not null)
            node["name"] = new { type = "computedString", value = name };
        if (value is not null)
            node["value"] = new { type = "string", value };
        if (backend is not null)
            node["backendDOMNodeId"] = backend;
        if (props is not null)
            node["properties"] = props.Select(p => new { name = p.Name, value = new { type = "tristate", value = p.Value } }).ToArray();
        return JsonSerializer.SerializeToElement(node);
    }
}
