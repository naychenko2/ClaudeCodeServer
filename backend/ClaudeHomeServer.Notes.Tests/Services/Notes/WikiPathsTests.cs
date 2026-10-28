using System.Text;
using ClaudeHomeServer.Services.Notes;

namespace ClaudeHomeServer.Tests.Services;

// WikiPaths (ADR-035, решения 1–2): корень вики владельца, предикат «путь под data/wiki/»
// по каноническому пути (NFD, другой регистр, «..», символьная ссылка), признак заведённой
// вики по pages/_schema.md, служебные проекции «_*».
public class WikiPathsTests : IDisposable
{
    private const string Owner = "u1";
    private readonly string _dir;

    public WikiPathsTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "wiki_paths_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    // ─── Корень ───────────────────────────────────────────────────────────────

    [Fact]
    public void Root_Pages_Keeper_СтроятПутьВладелца()
    {
        WikiPaths.Root(_dir, Owner).Should().Be(Path.Combine(_dir, "wiki", Owner));
        WikiPaths.Pages(_dir, Owner).Should().Be(Path.Combine(_dir, "wiki", Owner, "pages"));
        WikiPaths.Keeper(_dir, Owner).Should().Be(Path.Combine(_dir, "wiki", Owner, "keeper"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("a/b")]
    [InlineData("..")]
    public void Root_ПлохойOwnerId_Отказ(string ownerId) =>
        Assert.Throws<ArgumentException>(() => WikiPaths.Root(_dir, ownerId));

    // ─── Предикат пути ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Страница.md")]
    [InlineData("Проекты/p1/Страница.md")]
    public void IsWikiPath_ПутьПодDataWiki_Истинен(string rel)
    {
        var full = Path.Combine(WikiPaths.Pages(_dir, Owner), rel);
        WikiPaths.IsWikiPath(full, _dir).Should().BeTrue();
    }

    [Fact]
    public void IsWikiPath_СамКореньВики_Истинен() =>
        WikiPaths.IsWikiPath(Path.Combine(_dir, "wiki"), _dir).Should().BeTrue();

    [Fact]
    public void IsWikiPath_NFD_Путь_Истинен()
    {
        // Файл на диске назван в NFC, запрос пришёл в NFD (macOS/Obsidian-формы) —
        // декомпозиция не должна давать способ обойти гейт. Кириллица в NFD не разлагается,
        // берём латиницу с акцентом: «Á» = A + U+0301.
        var nfc = "Página";
        Directory.CreateDirectory(Path.Combine(WikiPaths.Pages(_dir, Owner), nfc));
        var nfd = nfc.Normalize(NormalizationForm.FormD);
        nfd.Should().NotBe(nfc); // контрольный факт: формы действительно разные

        WikiPaths.IsWikiPath(Path.Combine(WikiPaths.Pages(_dir, Owner), nfd, "x.md"), _dir)
            .Should().BeTrue();
    }

    [Fact]
    public void IsWikiPath_ДругойРегистр_Истинен()
    {
        var full = Path.Combine(_dir, "Wiki", Owner.ToUpperInvariant(), "PAGES", "x.md");
        WikiPaths.IsWikiPath(full, _dir).Should().BeTrue();
    }

    [Fact]
    public void IsWikiPath_ЧерезТочки_Истинен()
    {
        // «data/other/../wiki/…» сворачивается GetFullPath в путь внутри вики
        var full = Path.Combine(_dir, "other", "..", "wiki", Owner, "pages", "x.md");
        WikiPaths.IsWikiPath(full, _dir).Should().BeTrue();
    }

    [Fact]
    public void IsWikiPath_ЧерезСимвольнуюСсылку_Истинен()
    {
        if (OperatingSystem.IsWindows()) return; // ссылка на каталог требует привилегий; CI и дев-машины — Linux
        var realDir = Path.Combine(WikiPaths.Pages(_dir, Owner), "внутри");
        Directory.CreateDirectory(realDir);
        var link = Path.Combine(_dir, "link");
        Directory.CreateSymbolicLink(link, realDir);

        // Путь через ссылку, ведущую в вику, — путь в вику
        WikiPaths.IsWikiPath(Path.Combine(link, "x.md"), _dir).Should().BeTrue();
    }

    [Fact]
    public void IsWikiPath_СсылкаВедётНаружу_Ложна()
    {
        if (OperatingSystem.IsWindows()) return; // см. предыдущий тест
        var outside = Path.Combine(_dir, "outside");
        Directory.CreateDirectory(outside);
        Directory.CreateDirectory(WikiPaths.Root(_dir, Owner));
        var link = Path.Combine(WikiPaths.Root(_dir, Owner), "link");
        Directory.CreateSymbolicLink(link, outside);

        // Реальный файл вне вики — не вики, даже если путь идёт через её каталог
        WikiPaths.IsWikiPath(Path.Combine(link, "x.md"), _dir).Should().BeFalse();
    }

    [Fact]
    public void IsWikiPath_WikiНаСимвольнойСсылке_Истинен()
    {
        if (OperatingSystem.IsWindows()) return; // ссылка на каталог требует привилегий; CI и дев-машины — Linux
        // Владелец выносит вики на другой диск: data/wiki — символьная ссылка. И файл под
        // ссылкой, и корень канонизируются одним кодом — иначе это разные пространства имён
        // и гейт молчит (ревью S1a, MAJOR 1).
        var target = Path.Combine(Path.GetTempPath(), "wiki_paths_target_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(target);
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(_dir, "wiki"), target);
            var full = Path.Combine(_dir, "wiki", Owner, "pages", "Хроника.md");
            WikiPaths.IsWikiPath(full, _dir).Should().BeTrue();
            // сам корень по ссылке — тоже вики
            WikiPaths.IsWikiPath(Path.Combine(_dir, "wiki"), _dir).Should().BeTrue();
        }
        finally
        {
            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
        }
    }

    [Fact]
    public void IsWikiPath_ВисячаяСсылкаНаWiki_Истинен()
    {
        if (OperatingSystem.IsWindows()) return; // см. предыдущий тест
        // data/wiki → цель, которой ещё нет. fail-closed: путь через такую ссылку — путь
        // в вики (первая же запись может создать будущую вику — не она должна пройти мимо гейта).
        Directory.CreateSymbolicLink(Path.Combine(_dir, "wiki"), Path.Combine(_dir, "нет-такого"));
        var full = Path.Combine(_dir, "wiki", Owner, "pages", "Хроника.md");
        WikiPaths.IsWikiPath(full, _dir).Should().BeTrue();
    }

    [Fact]
    public void IsWikiPath_СимвольнаяСсылкаСТочками_Истинен()
    {
        if (OperatingSystem.IsWindows()) return; // см. предыдущий тест
        // POSIX: link/.. — родитель ЦЕЛИ ссылки. Запись по data/notes/link/../Хроника.md
        // физически попадает в data/wiki/{owner}/pages/Хроника.md — предикат обязан сказать «вики»
        // (ревью S1a, MAJOR 2: GetFullPath сворачивал «..» до разыменования).
        var deep = Path.Combine(WikiPaths.Pages(_dir, Owner), "deep");
        Directory.CreateDirectory(deep);
        Directory.CreateDirectory(Path.Combine(_dir, "notes"));
        Directory.CreateSymbolicLink(Path.Combine(_dir, "notes", "link"), deep);

        var full = Path.Combine(_dir, "notes", "link", "..", "Хроника.md");
        WikiPaths.IsWikiPath(full, _dir).Should().BeTrue();
    }

    [Fact]
    public void IsWikiPath_СсылкаСТочкамиНаружу_Ложна()
    {
        if (OperatingSystem.IsWindows()) return; // см. предыдущий тест
        // обратный: та же форма, но ссылка ведёт НЕ в вику — и путь не вики
        var outside = Path.Combine(_dir, "outside", "deep");
        Directory.CreateDirectory(outside);
        Directory.CreateDirectory(Path.Combine(_dir, "notes"));
        Directory.CreateSymbolicLink(Path.Combine(_dir, "notes", "link"), outside);

        var full = Path.Combine(_dir, "notes", "link", "..", "x.md");
        WikiPaths.IsWikiPath(full, _dir).Should().BeFalse();
    }

    [Fact]
    public void IsWikiPath_ЗаметкиИСоседниеИмена_НеВики()
    {
        WikiPaths.IsWikiPath(Path.Combine(_dir, "notes", Owner, "x.md"), _dir).Should().BeFalse();
        // префикс — ровно сегмент: «wiki2» и «wikipedia» — не вики
        WikiPaths.IsWikiPath(Path.Combine(_dir, "wiki2", "x.md"), _dir).Should().BeFalse();
        WikiPaths.IsWikiPath(Path.Combine(_dir, "wikipedia", "x.md"), _dir).Should().BeFalse();
        // «data/wiki» другого dataDir — не наша вика
        WikiPaths.IsWikiPath(Path.Combine(_dir, "other-data", "wiki", Owner, "x.md"), _dir).Should().BeFalse();
    }

    // ─── Признак заведённой вики ─────────────────────────────────────────────

    [Fact]
    public void IsInitialized_ТолькоПоSchemaВPages()
    {
        WikiPaths.IsInitialized(_dir, Owner).Should().BeFalse();

        var pages = WikiPaths.Pages(_dir, Owner);
        Directory.CreateDirectory(pages);
        File.WriteAllText(Path.Combine(pages, "Проекты.md"), "не признак");
        WikiPaths.IsInitialized(_dir, Owner).Should().BeFalse();

        File.WriteAllText(Path.Combine(pages, WikiPaths.SchemaFileName), "# схема");
        WikiPaths.IsInitialized(_dir, Owner).Should().BeTrue();
    }

    // ─── Служебные проекции ──────────────────────────────────────────────────

    [Theory]
    [InlineData("_index.md", true)]
    [InlineData("Проекты/p1/_health.md", true)]
    [InlineData("_", true)]
    [InlineData("Общее/Страница.md", false)]
    [InlineData("Под_черту.md", false)]
    [InlineData("", false)]
    public void IsProjection_ПоИмениФайла(string rel, bool expected) =>
        WikiPaths.IsProjection(rel).Should().Be(expected);
}
