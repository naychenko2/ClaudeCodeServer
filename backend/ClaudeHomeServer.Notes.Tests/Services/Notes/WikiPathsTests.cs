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
    public void IsWikiPath_СсылкаСТочкамиВПути_POSIXЭталонFailClosed()
    {
        if (OperatingSystem.IsWindows()) return; // см. предыдущий тест
        // Эталон модели (не поведение .NET-писателей, ревью S1a, минор 1): ядро для сырого
        // пути «link/../Хроника.md» применит «..» к разыменованной ссылке и запишет в вику —
        // предикат обязан сказать «вики». Но .NET-писатель (SafePath/GetFullPath) сворачивает
        // «..» в строке до syscall и такого пути к ФС не несёт: отказ лишний, не дыра.
        // Настоящий вектор .NET — «..» внутри ЦЕЛИ ссылки — покрыт физическим тестом
        // IsWikiPath_ЦельСсылкиСТочками_РазыменовываетсяФизически ниже.
        var deep = Path.Combine(WikiPaths.Pages(_dir, Owner), "deep");
        Directory.CreateDirectory(deep);
        Directory.CreateDirectory(Path.Combine(_dir, "notes"));
        Directory.CreateSymbolicLink(Path.Combine(_dir, "notes", "link"), deep);

        var full = Path.Combine(_dir, "notes", "link", "..", "Хроника.md");
        WikiPaths.IsWikiPath(full, _dir).Should().BeTrue();
    }

    [Fact]
    public void IsWikiPath_ЦельСсылкиСТочками_РазыменовываетсяФизически()
    {
        if (OperatingSystem.IsWindows()) return; // см. предыдущий тест
        // Проба A2 (ревью S1a, MAJOR 1): sub → pages вики, link → «sub/../evil» — «..» в
        // ЦЕЛИ ссылки. Ядро применяет «..» к физически разыменованному sub (родитель цели),
        // а не к строке цели: GetFullPath давал data/notes/evil (гейт false), а запись
        // шла в data/wiki/{owner}/evil/. Инвариант: каноническая форма гейта == реальный путь ФС.
        var pages = WikiPaths.Pages(_dir, Owner);
        Directory.CreateDirectory(pages);
        Directory.CreateDirectory(Path.Combine(_dir, "wiki", Owner, "evil")); // место реальной записи
        Directory.CreateDirectory(Path.Combine(_dir, "notes"));
        Directory.CreateSymbolicLink(Path.Combine(_dir, "notes", "sub"), pages);
        Directory.CreateSymbolicLink(Path.Combine(_dir, "notes", "link"), "sub/../evil"); // относительная цель

        var full = Path.Combine(_dir, "notes", "link", "x.md");
        WikiPaths.IsWikiPath(full, _dir).Should().BeTrue();

        // и физическая запись того же пути идёт в вику, а не в «data/notes/evil»
        File.WriteAllText(full, "запись", new UTF8Encoding(false));
        File.Exists(Path.Combine(_dir, "wiki", Owner, "evil", "x.md")).Should().BeTrue();
        File.Exists(Path.Combine(_dir, "notes", "evil", "x.md")).Should().BeFalse();
    }

    [Fact]
    public void IsWikiPath_СсылкаНаWikiСозданаПослеПервогоВызова_УчитываетсяСразу()
    {
        if (OperatingSystem.IsWindows()) return; // см. предыдущий тест
        // Проба D (ревью S1a, MAJOR 2): кэш корня на 60 с давал окно fail-open — первый
        // вызов при ещё несуществующем data/wiki, затем data/wiki становится ссылкой на
        // настоящую вику, и следующие 60 с гейт молчал. Между вызовами не кэшируется
        // ничего: корень и путь считаются одним свежим обходом (MAJOR 3).
        var full = Path.Combine(_dir, "wiki", Owner, "pages", "Хроника.md");
        WikiPaths.IsWikiPath(full, _dir).Should().BeTrue(); // до ссылки — путь под data/wiki

        var target = Path.Combine(_dir, "real-wiki");
        Directory.CreateDirectory(target);
        Directory.CreateSymbolicLink(Path.Combine(_dir, "wiki"), target);

        // сразу после создания ссылки — без окна: обе стороны в одном пространстве имён
        WikiPaths.IsWikiPath(full, _dir).Should().BeTrue();
        WikiPaths.IsWikiPath(Path.Combine(target, Owner, "pages", "Хроника.md"), _dir).Should().BeTrue();
    }

    [Fact]
    public void IsWikiPath_DataDirСталСсылкойПослеПервогоВызова_УчитываетсяСразу()
    {
        if (OperatingSystem.IsWindows()) return; // см. предыдущий тест
        // Проба ревью S1a, MAJOR 3: бессрочный кэш канонического dataDir — тот же класс,
        // что MAJOR 2, на уровень выше: корень считался из закешированного Canonical(dataDir),
        // а путь канонизировался заново. После переезда dataDir (каталог стал ссылкой на
        // своё же содержимое) стороны описывали разные деревья, и гейт отвечал false на путь,
        // который физически лежит в вики. Инвариант: ответ гейта зависит только от состояния
        // ФС, не от истории вызовов.
        var data = Path.Combine(_dir, "dd-swap");
        var pages = Path.Combine(data, "wiki", Owner, "pages");
        Directory.CreateDirectory(pages);

        var full = Path.Combine(pages, "x.md");
        WikiPaths.IsWikiPath(full, data).Should().BeTrue(); // первый вызов — тот, что заполнил бы кэш

        // вики переезжает на «другой диск»: dataDir становится ссылкой на прежнее содержимое
        var moved = Path.Combine(_dir, "dd-swap-disk2");
        Directory.Move(data, moved);
        Directory.CreateSymbolicLink(data, moved);

        // сразу после переезда — тот же путь остаётся в вики
        WikiPaths.IsWikiPath(full, data).Should().BeTrue();

        // и физическая запись того же пути идёт в переехавшую вику (ср. с пробой A2)
        File.WriteAllText(full, "запись", new UTF8Encoding(false));
        File.Exists(Path.Combine(moved, "wiki", Owner, "pages", "x.md")).Should().BeTrue();
    }

    [Fact]
    public async Task IsWikiPath_ЦиклСсылок_ОтвечаетБыстроIFailClosed()
    {
        if (OperatingSystem.IsWindows()) return; // см. предыдущий тест
        // Минор ревью S1a (мутация M4): без границы MaxLinkHops цикл a→b→a гонял бесконечно.
        // С границей разбор обрывается, и ответ — fail-closed: путь считается принадлежащим
        // вики независимо от места цикла. Решение по задаче S1a: записать такой путь ФС всё
        // равно не даст (ELOOP), поэтому отказ (true) безопаснее «false по несуществующему
        // месту» — ни один вызывающий при true не пишет.
        Directory.CreateDirectory(Path.Combine(_dir, "notes"));
        Directory.CreateSymbolicLink(Path.Combine(_dir, "notes", "a"), "b");
        Directory.CreateSymbolicLink(Path.Combine(_dir, "notes", "b"), "a");

        var call = Task.Run(() => WikiPaths.IsWikiPath(Path.Combine(_dir, "notes", "a", "x.md"), _dir));
        (await Task.WhenAny(call, Task.Delay(5000))).Should().Be(call, "предикат на цикле ссылок не должен зависать");
        (await call).Should().BeTrue();
    }

    [Theory]
    [InlineData(40, false)] // цепочка ровно в бюджет: разобралась — ответ по её цели (вне вики)
    [InlineData(41, true)]  // за бюджетом: разбор не удался — fail-closed
    public void IsWikiPath_ЦепочкаСсылок_ГраницаMaxLinkHops(int links, bool expected)
    {
        if (OperatingSystem.IsWindows()) return; // см. предыдущий тест
        // Граница глубины (минор ревью S1a): MaxLinkHops=40 — цепочка из 40 ссылок ещё
        // разбирается (цель вне вики → false); 41-я — за пределами общего бюджета разбора:
        // цель не разыменована, ответ fail-closed (true), а не зависший или молчащий.
        var outside = Path.Combine(_dir, "outside");
        Directory.CreateDirectory(outside);
        var chain = Path.Combine(_dir, "chain");
        Directory.CreateDirectory(chain);
        for (var i = 0; i < links; i++)
            Directory.CreateSymbolicLink(Path.Combine(chain, $"l{i}"), i == links - 1 ? outside : $"l{i + 1}");

        WikiPaths.IsWikiPath(Path.Combine(chain, "l0", "x.md"), _dir).Should().Be(expected);
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
