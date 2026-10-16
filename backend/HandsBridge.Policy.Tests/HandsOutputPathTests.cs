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

    // Каждый служебный путь — с РАЗРЕШЁННЫМ для данного инструмента расширением: при снятом
    // запрете .claude/ файл прошёл бы, а не упёрся в чужое расширение (тест краснеет по причине,
    // а не по совпадению).
    [Theory]
    [InlineData(HandsTools.ScreenshotControl, ".git/x.png")]
    [InlineData(HandsTools.ScreenshotControl, ".claude/agents/x.png")]
    [InlineData(HandsTools.ScanCanvas, ".claude/agents/x.png")]
    [InlineData(HandsTools.ScanDocument, ".claude/agents/x.md")]
    [InlineData(HandsTools.ScanDocument, ".git/x.md")]
    [InlineData(HandsTools.ScanDocument, ".mcp.json")]
    [InlineData(HandsTools.ScreenshotControl, ".cc-cache/x.png")]
    public void Service_paths_are_denied(string tool, string path)
    {
        var decision = Check(tool, path);
        Assert.False(decision.Allowed);
        Assert.Contains("service path", decision.Reason);
    }

    [Theory]
    [InlineData(HandsTools.ScreenshotControl, ".GIT/x.png")]
    [InlineData(HandsTools.ScreenshotControl, ".Claude/agents/x.png")]
    [InlineData(HandsTools.ScreenshotControl, ".CC-cache/x.png")]
    public void Service_paths_are_case_insensitive(string tool, string path)
    {
        var decision = Check(tool, path);
        Assert.False(decision.Allowed);
        Assert.Contains("service path", decision.Reason);
    }

    // Порядок проверок: служебный каталог с ЧУЖИМ расширением всё равно отказывает «по служебному
    // пути», а не «по расширению». Иначе снятый запрет .claude/ с чужим расширением маскировался бы
    // отказом по расширению, и тест Service_paths_are_denied его не словил бы.
    [Fact]
    public void Service_path_reason_wins_over_extension_check()
    {
        // .md не в whitelist ScreenshotControl, но путь в .claude/ — причина «service path».
        var decision = Check(HandsTools.ScreenshotControl, ".claude/agents/x.md");
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

    // ---------- содержимое сегмента (NormalizeRelativePath) ----------

    // У каждого вида отказа — своя причина, не общий «не относительный путь».
    [Theory]
    [InlineData("\u202E.png", "invisible Unicode")]
    [InlineData("\u200B.png", "invisible Unicode")]
    [InlineData("\uFEFF.png", "invisible Unicode")]
    [InlineData("~/x.png", "'~'")]
    [InlineData("~user/x.png", "'~'")]
    [InlineData("x.png:stream", "':'")]
    [InlineData("C:x.png", "':'")]
    [InlineData("a<b.png", "< > \" | ? *")]
    [InlineData("a>b.png", "< > \" | ? *")]
    [InlineData("a\"b.png", "< > \" | ? *")]
    [InlineData("a|b.png", "< > \" | ? *")]
    [InlineData("a?b.png", "< > \" | ? *")]
    [InlineData("a*b.png", "< > \" | ? *")]
    [InlineData("NUL.png", "reserved Windows device name")]
    [InlineData("con.png", "reserved Windows device name")]
    [InlineData("COM1.png", "reserved Windows device name")]
    [InlineData("lpt9.png", "reserved Windows device name")]
    [InlineData(".git./x.png", "dot or a space")]
    [InlineData("docs /x.png", "dot or a space")]
    public void Segment_content_rejections_have_a_clear_reason(string path, string reasonFragment)
    {
        var decision = Check(HandsTools.ScreenshotControl, path);
        Assert.False(decision.Allowed);
        Assert.Contains(reasonFragment, decision.Reason);
    }

    // ---------- короткие имена 8.3: '~' в каждом сегменте ----------

    // '~' — короткие имена 8.3 (CLAUDE~1 → .claude) и home (~/x): знак запрещён в каждом
    // сегменте, а не только в первом. Мутация «~ только в первом сегменте» краснеет: CLAUDE~1
    // (не в начале) пройдёт старый запрет и уйдёт в .claude/.
    [Theory]
    [InlineData("CLAUDE~1/x.json")]
    [InlineData("GIT~1/x.md")]
    [InlineData("a/CLAUDE~1/x.json")]
    [InlineData("docs/~tmp/x.md")]
    [InlineData("a~b.md")]
    public void Short_83_name_with_tilde_in_any_segment_is_denied(string path)
    {
        var decision = Check(HandsTools.ScreenshotControl, path);
        Assert.False(decision.Allowed);
        Assert.Contains("'~'", decision.Reason);
    }

    // «Нормальные имена проходят»: кириллица, иероглифы, пробел в середине, зарезерв. имена
    // Windows С расширением (console/com10/lpt — не CON/COM1..9/LPT1..9), .github (не .git),
    // имя с точкой в середине (my.cc.md) и валидные эмодзи — суррогатная пара (😀) и BMP-эмодзи
    // (⭐, ⚓). Ни одно из этих имён гейт не должен отказывать.
    [Theory]
    [InlineData(HandsTools.ScanDocument, "docs/конфигурация.md")]
    [InlineData(HandsTools.ScreenshotControl, "docs/a b.png")]
    [InlineData(HandsTools.ScanDocument, "docs/console.md")]
    [InlineData(HandsTools.ScanDocument, "docs/com10.md")]
    [InlineData(HandsTools.ScreenshotControl, "docs/lpt.png")]
    [InlineData(HandsTools.ScanDocument, ".github/x.md")]
    [InlineData(HandsTools.ScanDocument, "docs/my.cc.md")]
    [InlineData(HandsTools.ScreenshotControl, "docs/😀.png")]
    [InlineData(HandsTools.ScreenshotControl, "docs/⭐.png")]
    [InlineData(HandsTools.ScreenshotControl, "docs/⚓.png")]
    [InlineData(HandsTools.ScanDocument, "docs/日本語.md")]
    [InlineData(HandsTools.ScanDocument, "docs/Ёё.md")]
    public void Normal_names_pass(string tool, string path)
    {
        var decision = Check(tool, path);
        Assert.True(decision.Allowed);
    }

    // Одиночные суррогаты (битый UTF-16): high без low и low без high — отказ со своей причиной,
    // в отличие от валидной пары (😀), которая проходит (см. Normal_names_pass). [Fact], а не
    // Theory: два суррогата дают xUnit'у одинаковый display-name и дедуплицируются в один кейс.
    [Fact]
    public void Lone_surrogates_are_denied()
    {
        var high = Check(HandsTools.ScreenshotControl, "docs/\uD83D.png");
        Assert.False(high.Allowed);
        Assert.Contains("lone surrogate", high.Reason);

        var low = Check(HandsTools.ScreenshotControl, "docs/\uDE00.png");
        Assert.False(low.Allowed);
        Assert.Contains("lone surrogate", low.Reason);
    }

    // ---------- дописывание: ownFiles в нормализованной, регистронезависимой форме ----------

    [Fact]
    public void Append_own_matches_normalized_case_insensitively()
    {
        // docs\x.png в списке (обратные слеша) совпадает с docs/x.png в запросе.
        var backslashOwn = new[] { "docs\\x.png" };
        Assert.True(Check(HandsTools.ScreenshotControl, "docs/x.png", appendOwn: true, own: backslashOwn).Allowed);

        // Разный регистр — тоже совпадает.
        var upperOwn = new[] { "DOCS/X.PNG" };
        Assert.True(Check(HandsTools.ScreenshotControl, "docs/x.png", appendOwn: true, own: upperOwn).Allowed);

        // Чужой файл — отказ.
        var otherOwn = new[] { "docs/y.png" };
        Assert.False(Check(HandsTools.ScreenshotControl, "docs/x.png", appendOwn: true, own: otherOwn).Allowed);
    }

    // ---------- коллизия имени: без расширения, только расширение, несколько точек ----------

    [Fact]
    public void Next_free_name_without_an_extension_bumps_to_v2()
    {
        var taken = new HashSet<string> { "a" };
        Assert.Equal("a.v2", HandsPolicy.NextFreeName("a", taken.Contains));
    }

    [Fact]
    public void Next_free_name_with_multiple_dots_keeps_the_last_extension()
    {
        var taken = new HashSet<string> { "a.b.png" };
        Assert.Equal("a.b.v2.png", HandsPolicy.NextFreeName("a.b.png", taken.Contains));
    }

    [Fact]
    public void Name_that_is_only_an_extension_is_denied_in_the_path()
    {
        // «.png» — имя без стема: расширение есть, имени нет.
        var decision = Check(HandsTools.ScreenshotControl, ".png");
        Assert.False(decision.Allowed);
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
