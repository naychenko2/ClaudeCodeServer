using ClaudeHomeServer.HandsBridge.Policy;
using Xunit;

namespace HandsBridge.Policy.Tests;

/// <summary>
/// Путь записи (D1c): только НОВЫЙ файл под корнем проекта. Нормализует относительный путь,
/// держит путь в пределах корня (не диск, не UNC, не «..» выше корня), не пускает в служебные
/// каталоги, проверяет расширение по инструменту, потолок размера и число файлов за ход.
/// </summary>
public class HandsOutputPathTests
{
    private static HandsPolicy Policy() => new(new FakeWindows());

    private static HandsOutputPathDecision Check(
        string tool, string path, long bytes = 100, int written = 0,
        bool appendOwn = false, IReadOnlyCollection<string>? own = null)
        => Policy().CheckOutputPath(tool, null, path, bytes, written, appendOwn, own);

    // ---------- нормализация и пределы корня ----------

    [Theory]
    [InlineData("..")]
    [InlineData("a/../../b")]
    [InlineData("C:\\x")]
    [InlineData("/x")]
    [InlineData("\\\\srv\\share\\x")]
    public void Path_above_root_or_absolute_is_denied(string path)
    {
        var decision = Check(HandsTools.ScreenshotControl, path);
        Assert.False(decision.Allowed);
        Assert.Contains("relative to the project root", decision.Reason);
    }

    [Fact]
    public void Relative_path_under_root_is_allowed()
    {
        var decision = Check(HandsTools.ScreenshotControl, "docs/x.png");
        Assert.True(decision.Allowed);
        Assert.Equal("docs/x.png", decision.Path);
    }

    [Fact]
    public void Mixed_separators_are_normalized_to_forward_slash()
    {
        var decision = Check(HandsTools.ScreenshotControl, "docs\\x.png");
        Assert.True(decision.Allowed);
        Assert.Equal("docs/x.png", decision.Path);
    }

    // ---------- служебные каталоги ----------

    [Theory]
    [InlineData(".git/config")]
    [InlineData(".claude/agents/x.md")]
    [InlineData(".claude/commands/y.md")]
    [InlineData(".mcp.json")]
    [InlineData(".cc-cache/x.png")]
    [InlineData(".cc-foo")]
    public void Service_paths_are_denied(string path)
    {
        var decision = Check(HandsTools.ScreenshotControl, path);
        Assert.False(decision.Allowed);
        Assert.Contains("service path", decision.Reason);
    }

    [Theory]
    [InlineData(".GIT/x.png")]
    [InlineData(".Claude/agents/x.md")]
    public void Service_paths_are_case_insensitive(string path)
    {
        var decision = Check(HandsTools.ScreenshotControl, path);
        Assert.False(decision.Allowed);
        Assert.Contains("service path", decision.Reason);
    }

    [Fact]
    public void Cc_attachments_is_the_only_allowed_cc_folder()
    {
        Assert.True(Check(HandsTools.ScreenshotControl, ".cc-attachments/x.png").Allowed);
        Assert.True(Check(HandsTools.ScanCanvas, ".cc-attachments/x.png").Allowed);
    }

    // ---------- расширения по инструменту ----------

    [Theory]
    [InlineData(HandsTools.ScanDocument, "docs/x.png")]
    [InlineData(HandsTools.ScanCanvas, "docs/x.md")]
    [InlineData(HandsTools.ScreenshotControl, "docs/x.exe")]
    [InlineData(HandsTools.ScanDocument, "docs/x.exe")]
    [InlineData(HandsTools.ScanCanvas, "docs/x.exe")]
    public void Extension_outside_the_tool_whitelist_is_denied(string tool, string path)
    {
        var decision = Check(tool, path);
        Assert.False(decision.Allowed);
        Assert.Contains("not one of them", decision.Reason);
    }

    [Theory]
    [InlineData(HandsTools.ScreenshotControl, "docs/x.png")]
    [InlineData(HandsTools.ScreenshotControl, "docs/x.jpg")]
    [InlineData(HandsTools.ScanDocument, "docs/x.md")]
    [InlineData(HandsTools.ScanDocument, "docs/x.txt")]
    [InlineData(HandsTools.ScanDocument, "docs/x.json")]
    [InlineData(HandsTools.ScanCanvas, "docs/x.png")]
    public void Extension_in_the_tool_whitelist_is_allowed(string tool, string path)
    {
        Assert.True(Check(tool, path).Allowed);
    }

    // ---------- форма пути ----------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("x\0y.png")]
    public void Empty_whitespace_or_control_path_is_denied(string path)
    {
        Assert.False(Check(HandsTools.ScreenshotControl, path).Allowed);
    }

    // ---------- потолки ----------

    [Fact]
    public void File_size_is_capped_at_20mb()
    {
        var twentyMb = 20L * 1024 * 1024;
        Assert.True(Check(HandsTools.ScreenshotControl, "docs/x.png", bytes: twentyMb).Allowed);
        Assert.False(Check(HandsTools.ScreenshotControl, "docs/x.png", bytes: twentyMb + 1).Allowed);
    }

    [Fact]
    public void Scan_canvas_is_capped_at_50mb_and_scan_document_at_20mb()
    {
        var fiftyMb = 50L * 1024 * 1024;
        Assert.True(Check(HandsTools.ScanCanvas, "docs/x.png", bytes: fiftyMb).Allowed);
        Assert.False(Check(HandsTools.ScanCanvas, "docs/x.png", bytes: fiftyMb + 1).Allowed);
        Assert.False(Check(HandsTools.ScanDocument, "docs/x.md", bytes: fiftyMb).Allowed);
    }

    [Fact]
    public void The_101st_file_of_a_turn_is_denied()
    {
        Assert.True(Check(HandsTools.ScreenshotControl, "docs/x.png", written: 99).Allowed);
        var decision = Check(HandsTools.ScreenshotControl, "docs/x.png", written: 100);
        Assert.False(decision.Allowed);
        Assert.Contains("100 files", decision.Reason);
    }

    // ---------- дописывание своего файла ----------

    [Fact]
    public void Append_own_requires_a_file_created_this_turn()
    {
        var own = new[] { "docs/x.png" };
        Assert.True(Check(HandsTools.ScreenshotControl, "docs/x.png", appendOwn: true, own: own).Allowed);
        var decision = Check(HandsTools.ScreenshotControl, "docs/y.png", appendOwn: true, own: own);
        Assert.False(decision.Allowed);
        Assert.Contains("created itself", decision.Reason);
    }

    // ---------- коллизия имени ----------

    [Fact]
    public void Next_free_name_bumps_to_v2_then_v3()
    {
        var onlyOriginal = new HashSet<string> { "a.png" };
        Assert.Equal("a.v2.png", HandsPolicy.NextFreeName("a.png", onlyOriginal.Contains));

        var originalAndV2 = new HashSet<string> { "a.png", "a.v2.png" };
        Assert.Equal("a.v3.png", HandsPolicy.NextFreeName("a.png", originalAndV2.Contains));

        Assert.Equal("a.png", HandsPolicy.NextFreeName("a.png", _ => false));
    }

    [Fact]
    public void Next_free_name_keeps_the_folder()
    {
        var taken = new HashSet<string> { "docs/a.png" };
        Assert.Equal("docs/a.v2.png", HandsPolicy.NextFreeName("docs/a.png", taken.Contains));
    }
}
