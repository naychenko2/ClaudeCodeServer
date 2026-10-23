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

    // ─── guid: полный, не по подстроке ─────────────────────────────────────────

    // guid другой фичи, начинающийся с нашего (общий префикс): сравнение полного guid,
    // не по подстроке. Мутация Equals→StartsWith в FeatureMatches красит этот тест.
    [Fact]
    public void Фича_GuidСОбщимПрефиксом_НеСовпадает()
    {
        var longerGuid = MyFeature + "9"; // 37 символов: наш guid + лишний
        Write($"# Чужая\n\n**Фича:** {longerGuid}\n**Файлы:** a.html\n",
            "docs", "mockups", "other.md");
        Write("<html></html>", "docs", "mockups", "a.html");

        _catalog.Find(_root, MyFeature, []).Should().BeEmpty(
            "guid другой фичи с общим префиксом не совпадает (полное сравнение)");
    }

    // ─── linkedFiles: «..» и симлинк → запись не создаётся вовсе ─────────────────────

    // docs/mockups/../../secret.html → secret.html (вне области) — запись не создаётся
    // (ни exists, ни path). Мутация «префикс по сырой строке» (без нормализации «..») красит.
    [Fact]
    public void LinkedFiles_ТочкиТочки_Нормализация_ВнеMockups_НетЗаписи()
    {
        Write("<html></html>", "secret.html");
        Write("<html></html>", "docs", "mockups", "good.html");

        var result = _catalog.Find(_root, MyFeature,
            ["docs/mockups/../../secret.html", "docs/mockups/good.html"]);

        result.Should().ContainSingle()
            .Which.Path.Should().Be("docs/mockups/good.html");
    }

    // docs/mockups/../../../etc/passwd.html → etc/passwd.html (выше корня → null) — записи нет.
    [Fact]
    public void LinkedFiles_ВышеКорня_НетЗаписи()
    {
        Write("<html></html>", "etc", "passwd.html");

        _catalog.Find(_root, MyFeature, ["docs/mockups/../../../etc/passwd.html"])
            .Should().BeEmpty("выход выше корня проекта → null, записи нет");
    }

    // Симлинк docs/mockups/link.html → файл вне проекта: exists/mtime цели не утекают,
    // записи нет. Платформонезависимый: пропускаем, если символические ссылки недоступны.
    [Fact]
    public void LinkedFiles_СимлинкВнеОбласти_НетЗаписи()
    {
        if (!SupportsSymlinks()) return;
        var outside = Path.Combine(Path.GetTempPath(), "mk_sym_" + Guid.NewGuid().ToString("N") + ".html");
        File.WriteAllText(outside, "<html>вне проекта</html>");
        try
        {
            Directory.CreateDirectory(Path.Combine(_root, "docs", "mockups"));
            File.CreateSymbolicLink(Path.Combine(_root, "docs", "mockups", "link.html"), outside);

            _catalog.Find(_root, MyFeature, ["docs/mockups/link.html"])
                .Should().BeEmpty("симлинк на цель вне docs/mockups/ → записи нет");
        }
        finally
        {
            File.Delete(outside);
        }
    }

    // ─── регистр имени файла (major 1) ─────────────────────────────────────────────

    // Файл со смешанным регистром/кириллицей: Path в ответе совпадает с путём на диске,
    // Exists=true, ModifiedAt!=null. Префикс docs/mockups/ канонизируется в нижний, но ХВОСТ
    // (имя) — в исходном регистре; иначе на Linux файл превращался бы в несуществующий нижний
    // (Exists=false, макет «уехал» в «не в master»).
    // Мутация: вернуть norm.ToLowerInvariant() (схлопнуть весь путь) → Path=mymockup_макет.html,
    // Exists=false → краснеет.
    [Fact]
    public void LinkedFiles_РегистрИмениФайла_PathСовпадаетСДиском()
    {
        Write("<html></html>", "docs", "mockups", "MyMockup_Макет.html");

        var result = _catalog.Find(_root, MyFeature, ["docs/mockups/MyMockup_Макет.html"]);

        result.Should().ContainSingle()
            .Which.Path.Should().Be("docs/mockups/MyMockup_Макет.html");
        result[0].Files.Single().Exists.Should().BeTrue("файл на диске в исходном регистре");
        result[0].ModifiedAt.Should().NotBeNull();
    }

    // Дедуп по пути на сохранённом регистре: два пути, отличающиеся только регистром имени,
    // схлопываются в одну запись (seen — OrdinalIgnoreCase). Мутация: seen → Ordinal → 2 записи.
    [Fact]
    public void LinkedFiles_ДедупПоРегиструИмени_ОднаЗапись()
    {
        Write("<html></html>", "docs", "mockups", "MyMockup.html");

        var result = _catalog.Find(_root, MyFeature,
            ["docs/mockups/MyMockup.html", "docs/mockups/MYMOCKUP.html"]);

        result.Should().HaveCount(1, "дубли, отличающиеся регистром имени, схлопнуты");
        result[0].Path.Should().Be("docs/mockups/MyMockup.html");
    }

    // ─── симлинк на промежуточный каталог (major 2) ─────────────────────────────────

    // docs/mockups/sub → внешний каталог: файл внутри ссылочного каталога не «внутри» области —
    // записи нет вовсе (exists/mtime файла вне области не утекают). Старая реализация разрешала
    // ссылку только на последнюю компоненту → файл внутри ссылочного каталога проходил.
    // Мутация: убрать проход по цепочке (только последняя компонента) → запись создаётся → краснеет.
    [Fact]
    public void LinkedFiles_СимлинкКаталогВнеОбласти_НетЗаписи()
    {
        if (!SupportsSymlinks()) return;
        var outsideDir = Path.Combine(Path.GetTempPath(), "mk_symdir_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outsideDir);
        var outsideFile = Path.Combine(outsideDir, "secret.html");
        File.WriteAllText(outsideFile, "<html>вне области</html>");
        try
        {
            Directory.CreateDirectory(Path.Combine(_root, "docs", "mockups"));
            File.CreateSymbolicLink(Path.Combine(_root, "docs", "mockups", "sub"), outsideDir);

            _catalog.Find(_root, MyFeature, ["docs/mockups/sub/secret.html"])
                .Should().BeEmpty("симлинк на промежуточный каталог вне области → записи нет");
        }
        finally
        {
            Directory.Delete(outsideDir, recursive: true);
        }
    }

    // Легитимный симлинк: docs/mockups/sub → подкаталог ВНУТРИ docs/mockups/ (_inner/). Реальный
    // путь цели (docs/mockups/_inner/legit.html) лежит в пределах области → запись есть, Exists=true.
    // Платформонезависимый: пропускаем, если ссылки недоступны.
    [Fact]
    public void LinkedFiles_СимлинкКаталогВнутриОбласти_ЗаписьЕсть()
    {
        if (!SupportsSymlinks()) return;
        var inner = Path.Combine(_root, "docs", "mockups", "_inner");
        Directory.CreateDirectory(inner);
        File.WriteAllText(Path.Combine(inner, "legit.html"), "<html>внутри mockups</html>");
        try
        {
            Directory.CreateDirectory(Path.Combine(_root, "docs", "mockups"));
            File.CreateSymbolicLink(Path.Combine(_root, "docs", "mockups", "sub"), inner);

            var result = _catalog.Find(_root, MyFeature, ["docs/mockups/sub/legit.html"]);

            result.Should().ContainSingle()
                .Which.Path.Should().Be("docs/mockups/sub/legit.html");
            result[0].Files.Single().Exists.Should().BeTrue();
        }
        finally
        {
            Directory.Delete(inner, recursive: true);
        }
    }

    // Символические ссылки доступны (File.CreateSymbolicLink не бросает)?
    private static bool SupportsSymlinks()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mk_symtest_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            var target = Path.Combine(dir, "t.txt");
            File.WriteAllText(target, "x");
            var link = Path.Combine(dir, "l.txt");
            File.CreateSymbolicLink(link, target);
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
            catch { /* уборка теста */ }
        }
    }
}
