using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Docs;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Каталог макетов фичи (MockupCatalog, Docs): шапка записки docs/mockups + запасной путь
// linkedFiles. Пути строятся от Path.GetTempPath() — набор гоняется и на Linux в CI.
public class MockupCatalogTests : IDisposable
{
    // Своя фича и чужая: сравнение полного guid без учёта регистра
    private const string MyFeature = "9919bd75-0b1c-4fda-8196-72354cbcba29";
    private const string OtherFeature = "aabb1111-2222-3333-4444-555566667777";

    private readonly string _root;
    private readonly MockupCatalog _catalog;

    public MockupCatalogTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mockup_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _catalog = new MockupCatalog(new DocsIndexService());
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    // Файл вместе с недостающими папками; путь — сегментами, а не литералом с разделителем
    private void Write(string content, params string[] segments)
    {
        var full = Path.Combine([_root, .. segments]);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    // ─── шапка: своя/чужая фича ─────────────────────────────────────────────

    [Fact]
    public void Шапка_СвояФича_Найдена_Чужая_Нет()
    {
        Write($"# Макет А\n\n**Фича:** {MyFeature}\n**Статус:** утверждён\n**Файлы:** a.html\n",
            "docs", "mockups", "mine.md");
        Write($"# Макет Б\n\n**Фича:** {OtherFeature}\n**Файлы:** b.html\n",
            "docs", "mockups", "other.md");
        Write("<html></html>", "docs", "mockups", "a.html");
        Write("<html></html>", "docs", "mockups", "b.html");

        var result = _catalog.Find(_root, MyFeature, []);

        result.Should().ContainSingle()
            .Which.Path.Should().Be("docs/mockups/mine.md");
        result[0].Source.Should().Be(MockupSource.Header);
        result[0].Title.Should().Be("Макет А");
    }

    [Fact]
    public void Шапка_БезФичи_НеМакет()
    {
        // Статус есть, Фича нет — шапка, но не макет нашей фичи
        Write($"# Без фичи\n\n**Статус:** утверждён\n**Файлы:** x.html\n",
            "docs", "mockups", "nofeature.md");
        Write("<html></html>", "docs", "mockups", "x.html");

        _catalog.Find(_root, MyFeature, []).Should().BeEmpty();
    }

    [Fact]
    public void Статус_ЧерезЕ_Утвержден_ДаётApproved()
    {
        Write($"# Макет\n\n**Фича:** {MyFeature}\n**Статус:** утвержден\n**Файлы:** a.html\n",
            "docs", "mockups", "note.md");
        Write("<html></html>", "docs", "mockups", "a.html");

        var entry = _catalog.Find(_root, MyFeature, []).Single();

        entry.Status.Should().Be(MockupStatus.Approved);
    }

    // ─── «Файлы»: .., вне области, несуществующий ───────────────────────────

    [Fact]
    public void Файлы_ТочкиИВнеОбласти_Отброшены()
    {
        // ../escape.html → docs/escape.html (вне mockups); ../../etc/passwd → etc/passwd (вне)
        Write($"# Макет\n\n**Фича:** {MyFeature}\n**Файлы:** ../escape.html, ../../etc/passwd, good.html\n",
            "docs", "mockups", "note.md");
        Write("<html></html>", "docs", "escape.html");
        Write("root", "etc", "passwd");
        Write("<html></html>", "docs", "mockups", "good.html");

        var entry = _catalog.Find(_root, MyFeature, []).Single();

        entry.Files.Should().BeEquivalentTo([new MockupFile("docs/mockups/good.html", true)]);
    }

    [Fact]
    public void Файлы_АбсолютныйПутьВнеMockups_Отброшен()
    {
        // «/docs/adr/x.md» — от корня, вне docs/mockups/
        Write($"# Макет\n\n**Фича:** {MyFeature}\n**Файлы:** /docs/adr/x.md, good.html\n",
            "docs", "mockups", "note.md");
        Write("# ADR", "docs", "adr", "x.md");
        Write("<html></html>", "docs", "mockups", "good.html");

        var entry = _catalog.Find(_root, MyFeature, []).Single();

        entry.Files.Should().BeEquivalentTo([new MockupFile("docs/mockups/good.html", true)]);
    }

    [Fact]
    public void Файлы_НесуществующийШапки_Отброшен()
    {
        Write($"# Макет\n\n**Фича:** {MyFeature}\n**Файлы:** missing.html, good.html\n",
            "docs", "mockups", "note.md");
        Write("<html></html>", "docs", "mockups", "good.html");

        var entry = _catalog.Find(_root, MyFeature, []).Single();

        entry.Files.Should().BeEquivalentTo([new MockupFile("docs/mockups/good.html", true)]);
    }

    // ─── linkedFiles ───────────────────────────────────────────────────────

    [Fact]
    public void LinkedFiles_БезФайлаНаДиске_ExistsFalse()
    {
        var result = _catalog.Find(_root, MyFeature, ["docs/mockups/branch.html"]);

        result.Should().ContainSingle()
            .Which.Source.Should().Be(MockupSource.LinkedFiles);
        result[0].Path.Should().Be("docs/mockups/branch.html");
        result[0].Files.Single().Exists.Should().BeFalse();
    }

    [Fact]
    public void LinkedFiles_ДубльСШапкой_ОднаЗаписьHeader()
    {
        // plans.html покрыт шапкой (в «Файлы») → linkedFiles его не дублирует
        Write($"# Макет\n\n**Фича:** {MyFeature}\n**Файлы:** plans.html\n",
            "docs", "mockups", "note.md");
        Write("<html></html>", "docs", "mockups", "plans.html");

        var result = _catalog.Find(_root, MyFeature, ["docs/mockups/plans.html"]);

        result.Should().ContainSingle()
            .Which.Source.Should().Be(MockupSource.Header);
        result[0].Path.Should().Be("docs/mockups/note.md");
    }

    [Fact]
    public void LinkedFiles_MdИTokens_НеСтановятсяМакетом()
    {
        // .md и -tokens.md из linkedFiles — записки, а не макеты; только .html
        Write("<html></html>", "docs", "mockups", "real.html");

        var result = _catalog.Find(_root, MyFeature,
            ["docs/mockups/note.md", "docs/mockups/tokens-tokens.md", "docs/mockups/real.html"]);

        result.Should().ContainSingle()
            .Which.Path.Should().Be("docs/mockups/real.html");
        result[0].Source.Should().Be(MockupSource.LinkedFiles);
    }

    [Fact]
    public void LinkedFiles_ПутьВнеMockups_ИГлебнойФичи_НеПопадают()
    {
        // docs/other.html (вне mockups) и docs/mockups/чужой.html (чужая фича, но linkedFiles
        // не знает фич — он просто путь) — первый отброшен, второй остаётся
        Write("<html></html>", "docs", "other.html");
        Write("<html></html>", "docs", "mockups", "x.html");

        var result = _catalog.Find(_root, MyFeature, ["docs/other.html", "docs/mockups/x.html"]);

        result.Should().ContainSingle().Which.Path.Should().Be("docs/mockups/x.html");
    }
}
