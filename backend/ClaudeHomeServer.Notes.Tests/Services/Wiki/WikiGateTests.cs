using ClaudeHomeServer.Services.Notes.Wiki;
using static ClaudeHomeServer.Tests.Helpers.WikiGateFixtures;

namespace ClaudeHomeServer.Tests.Services;

// Строгая шапка, секции и сноски страницы + гейт правил раздела (S2a, ADR-035 §3, ADR-036 §4).
// Чистые функции — юнит без окружения. На каждое правило гейта пара «проходит / отбит».
// Каркас валидной страницы и карточки — общий с WikiPackageClaimTests, в WikiGateFixtures.
public class WikiGateTests
{
    private const string Quote = "точная цитата из источника";
    private const string Foot = "[^s1]: [[Карточка]] — «" + Quote + "»";
    private const string Good = "- Бюджет на GPU — до 15 тыс. ₽ в месяц.[^s1]";

    private static WikiGate.Draft D(string path, string content) => new(path, content);

    private static IReadOnlyList<string> Check(
        IEnumerable<WikiGate.Draft> state,
        string section = P1,
        IReadOnlyList<string>? chain = null) =>
        WikiGate.Check(state.ToList(), section, chain ?? []);

    private static void NoViolations(
        IEnumerable<WikiGate.Draft> state,
        string section = P1,
        IReadOnlyList<string>? chain = null) =>
        Check(state, section, chain).Should().BeEmpty("нарушений быть не должно");

    // —— Круговой тест: разбор → рендер → разбор даёт равные модели ——

    [Fact]
    public void Шапка_круговойРазборДаётРавнуюМодель()
    {
        var text = Page(fmExtra: "kind: person\naliases: [Вера, @vera]\nowns: [сущность-1]\nsources: [карточка-1]\nupdated: 2026-10-28\n");
        var first = WikiFrontmatter.Parse(text);
        first.Errors.Should().BeEmpty();
        var second = WikiFrontmatter.Parse(first.Model!.Render());
        second.Errors.Should().BeEmpty();
        second.Model.Should().Be(first.Model);
    }

    [Fact]
    public void Страница_круговойРазборДаётРавнуюМодель()
    {
        var text = Page(swodka: Good, footnotes: Foot, fmExtra: "aliases: [дубль]\nowns: [сущность-1]\n");
        var first = WikiPage.Parse(text);
        first.Errors.Should().BeEmpty();
        var second = WikiPage.Parse(first.Model!.Render());
        second.Errors.Should().BeEmpty();
        second.Model.Should().Be(first.Model);
    }

    [Fact]
    public void Хроника_круговойРазборДаётРавнуюМодель()
    {
        var text = """
            ---
            type: chronicle
            summary: хроника чата
            status: active
            ---

            # Чат от 28.10

            ## О чём
            Обсуждали бюджет GPU.

            ## Что решили
            - Бюджет на GPU — до 15 тыс. ₽ в месяц.[^s1]

            ## Что выяснили
            - Локальная модель быстрее облака.[^s1]

            ## Открытые вопросы
            - Когда поднимать лимит?

            [^s1]: [[Карточка]] — «Бюджет на GPU — до 15 тыс. ₽ в месяц»
            """;
        var first = WikiPage.Parse(text);
        first.Errors.Should().BeEmpty();
        first.Model!.Sections.Select(s => s.Name).Should().Equal("О чём", "Что решили", "Что выяснили", "Открытые вопросы");
        var second = WikiPage.Parse(first.Model.Render());
        second.Errors.Should().BeEmpty();
        second.Model.Should().Be(first.Model);
    }

    // —— Строгий парсер шапки ——

    [Theory]
    [InlineData("annotates: 5")]
    [InlineData("expires: 2026-12-01")]
    [InlineData("file: путь.md")]
    public void Шапка_запрещённыеПоля_отбит(string field)
    {
        var r = WikiFrontmatter.Parse(Fm("entity", field + "\n"));
        r.Model.Should().BeNull();
        r.Errors.Should().Contain(e => e.Contains(field.Split(':')[0]));
    }

    [Fact]
    public void Шапка_неизвестноеПоле_отбита()
    {
        var r = WikiFrontmatter.Parse(Fm("entity", "xyz: 1\n"));
        r.Model.Should().BeNull();
        r.Errors.Should().Contain(e => e.Contains("неизвестное поле 'xyz'"));
    }

    [Fact]
    public void Шапка_типВнеРазрешённых_отбита()
    {
        var r = WikiFrontmatter.Parse(Fm("notion"));
        r.Model.Should().BeNull();
        r.Errors.Should().Contain(e => e.Contains("не из десяти разрешённых"));
    }

    [Fact]
    public void Шапка_статусВнеЧетырёх_отбита()
    {
        var r = WikiFrontmatter.Parse(Fm("entity").Replace("status: active", "status: archived"));
        r.Model.Should().BeNull();
        r.Errors.Should().Contain(e => e.Contains("status 'archived' не из четырёх"));
    }

    [Fact]
    public void Шапка_дубльКлюча_отбита()
    {
        var r = WikiFrontmatter.Parse(Fm("entity", "kind: person\nkind: org\n"));
        r.Model.Should().BeNull();
        r.Errors.Should().Contain(e => e.Contains("'kind' дублируется"));
    }

    [Fact]
    public void Шапка_вложенноеЗначение_отбита()
    {
        var r = WikiFrontmatter.Parse(Fm("entity", " kind: person\n"));
        r.Model.Should().BeNull();
        r.Errors.Should().Contain(e => e.Contains("вложенные значения"));
    }

    [Fact]
    public void Шапка_карточныеПоляУНеКарточки_отбиты()
    {
        var r = WikiFrontmatter.Parse(Fm("entity", "origin: note\nref: note:1\nhash: h\ncaptured: 2026-10-28\n"));
        r.Model.Should().BeNull();
        r.Errors.Should().Contain(e => e.Contains("есть только у карточки источника"));
    }

    // —— Гейт: форма шапки и секций ——

    [Fact]
    public void Гейт_формаСтраницы_проходит() =>
        NoViolations([D(P1Dir + "Тет.md", Page())]);

    [Fact]
    public void Гейт_секцииНеВФиксированномПорядке_отбиты()
    {
        var bad = Fm("entity") + "\n\n# Тет\n\n## Хронология\n\n## Сводка\n\n## Противоречия\n\n## История\n";
        Check([D(P1Dir + "Тет.md", bad)]).Should().Contain(e => e.Contains("не в фиксированном порядке"));
    }

    [Fact]
    public void Гейт_отсутствующаяСекция_отбита()
    {
        var bad = Fm("entity") + "\n\n# Тет\n\n## Сводка\n\n## Хронология\n\n## Противоречия\n";
        Check([D(P1Dir + "Тет.md", bad)]).Should().Contain(e => e.Contains("нет секции 'История'"));
    }

    // —— Гейт: утверждение без сноски ——

    [Fact]
    public void Гейт_утверждениеБезСноски_отбито()
    {
        Check([D(P1Dir + "Тет.md", Page(swodka: "- Просто утверждение."))])
            .Should().Contain(e => e.Contains("без сноски"));
    }

    [Fact]
    public void Гейт_утверждениеСносной_проходит() =>
        NoViolations([D(P1Dir + "Тет.md", Page(swodka: Good, footnotes: Foot)), CardDraft]);

    // —— Гейт: сноска не на type: source ——

    [Fact]
    public void Гейт_сноскаНеНаКарточку_отбита()
    {
        var page = Page(swodka: Good, footnotes: "[^s1]: [[ДругаяСтраница]] — «" + Quote + "»");
        var other = Page(title: "ДругаяСтраница");
        Check([D(P1Dir + "Тет.md", page), D(P1Dir + "ДругаяСтраница.md", other)])
            .Should().Contain(e => e.Contains("не карточка источника"));
    }

    [Fact]
    public void Гейт_сноскаНаНеизвестнуюКарточку_отбита()
    {
        Check([D(P1Dir + "Тет.md", Page(swodka: Good, footnotes: "[^s1]: [[НетТакой]] — «" + Quote + "»"))])
            .Should().Contain(e => e.Contains("на неизвестную карточку"));
    }

    // —— Гейт: длина цитаты ——

    [Theory]
    [InlineData("коротко")]
    [InlineData("")]
    public void Гейт_короткаяЦитата_отбита(string quote)
    {
        var foot = "[^s1]: [[Карточка]] — «" + quote + "»";
        Check([D(P1Dir + "Тет.md", Page(swodka: Good, footnotes: foot)), CardDraft])
            .Should().Contain(e => e.Contains("контракт 8–300"));
    }

    [Fact]
    public void Гейт_цитатаСверхЛимита_отбита()
    {
        var foot = "[^s1]: [[Карточка]] — «" + new string('с', 301) + "»";
        Check([D(P1Dir + "Тет.md", Page(swodka: Good, footnotes: foot)), CardDraft])
            .Should().Contain(e => e.Contains("контракт 8–300"));
    }

    // —— Гейт: дубль заголовка и алиаса в разделе ——

    [Fact]
    public void Гейт_дубльЗаголовкаВРазделе_отбит()
    {
        Check([D(P1Dir + "Тет.md", Page()), D(P1Dir + "Копия.md", Page())])
            .Should().Contain(e => e.Contains("дубль заголовок"));
    }

    [Fact]
    public void Гейт_дубльАлиасаВРазделе_отбит()
    {
        var a = Page(fmExtra: "aliases: [дубль]\n");
        var b = Page(title: "Другая", fmExtra: "aliases: [Дубль]\n");
        Check([D(P1Dir + "Тет.md", a), D(P1Dir + "Другая.md", b)])
            .Should().Contain(e => e.Contains("дубль алиас"));
    }

    // —— Гейт: дубль owns в разделе и цепочке вверх ——

    [Fact]
    public void Гейт_дубльOwnsПоЦепочкеВверх_отбит()
    {
        var page = Page(fmExtra: "owns: [сущность-1]\n");
        var common = Page(title: "Знание", dir: "Общее/", fmExtra: "owns: [сущность-1]\n");
        Check([D(P1Dir + "Тет.md", page), D("Общее/Знание.md", common)], P1, ["common"])
            .Should().Contain(e => e.Contains("дубль owns"));
    }

    [Fact]
    public void Гейт_ownsВСвоёмРазделе_проходит() =>
        NoViolations([D(P1Dir + "Тет.md", Page(fmExtra: "owns: [сущность-1]\n"))], P1, ["common"]);

    // —— Гейт: ссылки только внутри раздела и вверх (ADR-036, решение 4) ——

    [Fact]
    public void Гейт_ссылкаВбок_отбита()
    {
        var page = Page(swodka: "- См. [[Проекты/p2/Сосед]].[^s1]", footnotes: Foot);
        var neighbor = Page(title: "Сосед", dir: "Проекты/p2/");
        Check([D(P1Dir + "Тет.md", page), D("Проекты/p2/Сосед.md", neighbor), CardDraft])
            .Should().Contain(e => e.Contains("вбок или вниз"));
    }

    [Fact]
    public void Гейт_ссылкаВнизИзСферы_отбита()
    {
        var page = Page(dir: "Сферы/s1/", swodka: "- См. [[Проекты/p1/Тет]].[^s1]", footnotes: Foot);
        var target = Page();
        Check([D("Сферы/s1/Обзор.md", page), D(P1Dir + "Тет.md", target)], "spheres/s1", ["common"])
            .Should().Contain(e => e.Contains("вбок или вниз"));
    }

    [Fact]
    public void Гейт_ссылкаВверхСПрефиксом_проходит()
    {
        var page = Page(swodka: "- См. [[Общее/Знание]].[^s1]", footnotes: Foot);
        var common = Page(title: "Знание", dir: "Общее/");
        NoViolations([D(P1Dir + "Тет.md", page), D("Общее/Знание.md", common), CardDraft], P1, ["common"]);
    }

    [Fact]
    public void Гейт_голаяСсылкаВверх_проходит()
    {
        var page = Page(swodka: "- См. [[Знание]].[^s1]", footnotes: Foot);
        var common = Page(title: "Знание", dir: "Общее/");
        NoViolations([D(P1Dir + "Тет.md", page), D("Общее/Знание.md", common), CardDraft], P1, ["common"]);
    }

    [Fact]
    public void Гейт_голаяСсылкаВнутриРаздела_проходит()
    {
        var page = Page(swodka: "- См. [[Другая]].[^s1]", footnotes: Foot);
        var other = Page(title: "Другая");
        NoViolations([D(P1Dir + "Тет.md", page), D(P1Dir + "Другая.md", other), CardDraft]);
    }

    // Уточнение по ревью S2d: голая ссылка резолвится ТОЛЬКО в своём разделе и вверх;
    // имя, живущее в чужом разделе, — нарушение «вбок/вниз», а не тихая подмена.
    [Fact]
    public void Гейт_голаяСсылкаНаИмяИзЧужогоРаздела_отбита()
    {
        var page = Page(swodka: "- См. [[Чужое]].[^s1]", footnotes: Foot);
        var foreign = Page(title: "Чужое", dir: "Проекты/p2/");
        Check([D(P1Dir + "Тет.md", page), D("Проекты/p2/Чужое.md", foreign), CardDraft], P1)
            .Should().Contain(e => e.Contains("резолвится только в чужом разделе"));
    }

    [Fact]
    public void Гейт_ссылкаВКодеНеСсылка()
    {
        var page = Page(swodka: "- Пример `[[Проекты/p2/Сосед]]` в коде — не ссылка.[^s1]", footnotes: Foot);
        NoViolations([D(P1Dir + "Тет.md", page), CardDraft]);
    }

    // —— ResolveLink: резолв одной ссылки — те же правила, что у всего гейта (очередь 1, 2А:
    // им же пользуется валидатор синтеза, снимая неразрешимые ссылки в claim). Формулировку
    // нарушения даёт CheckLinks по исходу; здесь — только исход. Гейт от выноса не слабее:
    // CheckLinks проходит через ResolveLink для каждой ссылки страницы. ——

    private static List<(string Section, WikiPage Page, string Path)> Snapshot(params (string Path, string Content)[] pages) =>
        [.. pages.Select(p => (WikiSections.Parse(p.Path)!, WikiPage.Parse(p.Content).Model!, p.Path))];

    [Fact]
    public void ResolveLink_исходы_те_же_что_у_строгого_гейта()
    {
        var snap = Snapshot(
            (P1Dir + "Тет.md", Page()),
            (P1Dir + "Другая.md", Page(title: "Другая")),
            ("Общее/Знание.md", Page(title: "Знание", dir: "Общее/")),
            ("Проекты/p2/Чужое.md", Page(title: "Чужое", dir: "Проекты/p2/")));
        string[] chain = ["common"];

        WikiGate.ResolveLink("Тет", P1, chain, snap).Outcome.Should().Be(WikiGate.LinkOutcome.Resolved);
        WikiGate.ResolveLink("Другая", P1, chain, snap).Outcome.Should().Be(WikiGate.LinkOutcome.Resolved);
        WikiGate.ResolveLink("Знание", P1, chain, snap).Outcome.Should().Be(WikiGate.LinkOutcome.Resolved);
        WikiGate.ResolveLink("Общее/Знание", P1, chain, snap).Outcome.Should().Be(WikiGate.LinkOutcome.Resolved);

        var sidewaysBare = WikiGate.ResolveLink("Чужое", P1, chain, snap);
        sidewaysBare.Outcome.Should().Be(WikiGate.LinkOutcome.Sideways);
        sidewaysBare.Section.Should().Be("projects/p2");

        var sidewaysPref = WikiGate.ResolveLink("Проекты/p2/Чужое", P1, chain, snap);
        sidewaysPref.Outcome.Should().Be(WikiGate.LinkOutcome.Sideways);
        sidewaysPref.Section.Should().Be("projects/p2");

        WikiGate.ResolveLink("Призрак", P1, chain, snap).Outcome.Should().Be(WikiGate.LinkOutcome.NotFound);
        WikiGate.ResolveLink("Общее/Призрак", P1, chain, snap).Outcome.Should().Be(WikiGate.LinkOutcome.PrefixedUnknown);
        WikiGate.ResolveLink("щ/тот", P1, chain, snap).Outcome.Should().Be(WikiGate.LinkOutcome.BadPrefix);
    }

    // —— Гейт: запись в '_*', запись в неписемый раздел ——

    [Fact]
    public void Гейт_записьВПроекции_отбита() =>
        Check([D(P1Dir + "_index.md", Page(title: "Индекс"))])
            .Should().Contain(e => e.Contains("'_*'"));

    [Fact]
    public void Гейт_записьВРазделСферы_отбита() =>
        Check([D("Сферы/s1/Тет.md", Page(dir: "Сферы/s1/"))], "spheres/s1", ["common"])
            .Should().Contain(e => e.Contains("раздел сферы"));

    [Fact]
    public void Гейт_записьВПроектныйРаздел_проходит() =>
        NoViolations([D(P1Dir + "Тет.md", Page())], P1, ["spheres/s1", "common"]);

    // —— Гейт: '- [ ]' на странице ——

    [Fact]
    public void Гейт_чекбоксНаСтранице_отбит() =>
        Check([D(P1Dir + "Тет.md", Page(swodka: "- [ ] сделать потом"))])
            .Should().Contain(e => e.Contains("не несёт задач"));

    // —— Гейт: закрытый перечень origin (карточка) ——

    [Fact]
    public void Гейт_originВнеПеречня_отбит() =>
        Check([D(P1Dir + "Источники/Карточка.md", Card("Карточка", origin: "telegram"))])
            .Should().Contain(e => e.Contains("вне закрытого перечня"));

    [Fact]
    public void Гейт_originИзПеречня_проходит() =>
        NoViolations([D(P1Dir + "Источники/Карточка.md", Card("Карточка", origin: "memory"))]);

    // —— Ссылка на чат: session:{id}#m{a}-{b} живёт в ref карточки ——

    [Fact]
    public void СсылкаНаЧат_разбираетсяВКортеж()
    {
        WikiPage.TryParseSessionRef("session:abc123#m3-7", out var id, out var from, out var to).Should().BeTrue();
        id.Should().Be("abc123");
        from.Should().Be(3);
        to.Should().Be(7);
        WikiPage.TryParseSessionRef("session:abc#m-x", out _, out _, out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("session:abc#m99999999999999999999-1")]
    [InlineData("session:abc#m1-99999999999999999999")]
    [InlineData("session:abc#m0-5")]
    [InlineData("session:abc#m7-3")]
    [InlineData("session:abc#3-7")]
    [InlineData("session:../../x#m3-7")]
    [InlineData("session:a b#m3-7")]
    [InlineData("session:abc#m3-7\n")]
    public void СсылкаНаЧат_строгийРазбор_неБросаетИОтбивает(string text) =>
        WikiPage.TryParseSessionRef(text, out _, out _, out _).Should().BeFalse();

    [Fact]
    public void СсылкаНаЧат_границаОдинаковыхНомеров_проходит() =>
        WikiPage.TryParseSessionRef("session:abc#m5-5", out _, out _, out _).Should().BeTrue();

    [Fact]
    public void Гейт_сноскаПрямоНаИсточник_отбита()
    {
        var text = Page(swodka: Good, footnotes: "[^s1]: session:abc123#m3-7 — «" + Quote + "»");
        Check([D(P1Dir + "Тет.md", text)]).Should().Contain(e => e.Contains("не '[[Карточка]]'"));
    }

    [Fact]
    public void Гейт_идентификаторИсточникаСПутями_отбит()
    {
        var text = Page(swodka: Good, footnotes: "[^s1]: session:../../../data/sessions/x/history.json — «" + Quote + "»");
        Check([D(P1Dir + "Тет.md", text)]).Should().NotBeEmpty();
    }

    [Fact]
    public void Гейт_refКарточкиSessionВКорректномФормате_проходит() =>
        NoViolations([D(P1Dir + "Источники/Карточка.md", SessionCard("session:abc123#m3-7"))]);

    [Theory]
    [InlineData("session:abc#m99999999999999999999-1")]
    [InlineData("session:abc#m7-3")]
    [InlineData("session:../../x#m3-7")]
    public void Гейт_refКарточкиSessionВБитомФормате_отбит(string reference) =>
        Check([D(P1Dir + "Источники/Карточка.md", SessionCard(reference))])
            .Should().Contain(e => e.Contains("origin: session"));

    private static string SessionCard(string reference) =>
        Fm("source", $"origin: session\nref: {reference}\nhash: h1\ncaptured: 2026-10-28\n") + "\n\n# Карточка\n";

    // —— Дубли: заголовок и алиасы — одно пространство имён ——

    [Fact]
    public void Гейт_алиасСовпадаетСЗаголовкомДругойСтраницы_отбит() =>
        Check([D(P1Dir + "Отчёт.md", Page(title: "Отчёт за квартал")),
               D(P1Dir + "Другая.md", Page(title: "Другая", fmExtra: "aliases: [Отчёт за квартал]\n"))])
            .Should().Contain(e => e.Contains("дубль"));

    [Fact]
    public void Гейт_алиасСовпадаетСАлиасомДругойСтраницы_отбит() =>
        Check([D(P1Dir + "А.md", Page(title: "А", fmExtra: "aliases: [Общее имя]\n")),
               D(P1Dir + "Б.md", Page(title: "Б", fmExtra: "aliases: [общее имя]\n"))])
            .Should().Contain(e => e.Contains("дубль"));

    [Fact]
    public void Гейт_дубльЗаголовкаВНормализацииNfcNfd_отбит() =>
        Check([D(P1Dir + "А.md", Page(title: "Café")),
               D(P1Dir + "Б.md", Page(title: "Cafe\u0301"))])
            .Should().Contain(e => e.Contains("дубль"));

    // —— Цепочка вверх: только spheres/* и common ——

    [Theory]
    [InlineData("projects/p2")]
    [InlineData("personal")]
    public void Гейт_upChainШиреПоложенного_отбит(string bad)
    {
        var page = Page(swodka: "- См. [[Проекты/p2/Сосед]].[^s1]", footnotes: Foot);
        var neighbor = Page(title: "Сосед", dir: "Проекты/p2/");
        Check([D(P1Dir + "Тет.md", page), D("Проекты/p2/Сосед.md", neighbor), CardDraft], P1, [bad])
            .Should().Contain(e => e.Contains("цепочка вверх"));
    }

    [Fact]
    public void Гейт_upChainПовторРаздела_отбит() =>
        Check([D(P1Dir + "Тет.md", Page())], P1, ["common", "common"])
            .Should().Contain(e => e.Contains("повторён"));

    [Fact]
    public void Гейт_upChainСферыИОбщее_проходит() =>
        NoViolations([D(P1Dir + "Тет.md", Page())], P1, ["spheres/s1", "common"]);

    // —— Fail-closed: путь вне раздела и страница вне контракта state ——

    [Fact]
    public void Гейт_путьВнеРазделов_отбит() =>
        Check([D("Общее/../Личное/Грязь.md", Page(swodka: "- [ ] задача"))])
            .Should().Contain(e => e.Contains("не принадлежит ни одному разделу"));

    [Fact]
    public void Гейт_страницаЧужогоРазделаВнеЦепочки_отбита() =>
        Check([D(P1Dir + "Тет.md", Page()), D("Проекты/p2/Сосед.md", Page(title: "Сосед"))])
            .Should().Contain(e => e.Contains("вне раздела"));

    // —— Сноска обязательна в «Хронологии» и «Истории» ——

    [Theory]
    [InlineData("Хронология")]
    [InlineData("История")]
    public void Гейт_утверждениеБезСноскиВХронологииИИстории_отбито(string section)
    {
        var text = Page(swodka: Good, footnotes: Foot).Replace($"## {section}\n", $"## {section}\n- Бюджет GPU вырос до 20 тыс. ₽.\n");
        Check([D(P1Dir + "Тет.md", text), CardDraft])
            .Should().Contain(e => e.Contains($"утверждение в '{section}' без сноски"));
    }

    // —— Номера сносок перенумеровывает код ——

    [Fact]
    public void НомераСносок_ПеренумеровываетКод()
    {
        var text = Page(
            swodka: "- Утв1.[^zz1] Утв2.[^aa1]",
            footnotes: "[^zz1]: [[Карточка]] — «первая достаточная цитата»\n[^aa1]: [[Карточка]] — «вторая достаточная цитата»");
        var r = WikiPage.Parse(text);
        r.Errors.Should().BeEmpty();
        r.Model!.Sections[0].Lines[0].Should().Be("- Утв1.[^s1] Утв2.[^s2]");
        r.Model.Footnotes.Should().HaveCount(2);
    }

    [Fact]
    public void СсылкаНаНесуществующуюСноску_отбита()
    {
        var r = WikiPage.Parse(Page(swodka: "- Утв.[^s9]"));
        r.Model.Should().BeNull();
        r.Errors.Should().Contain(e => e.Contains("несуществующую сноску"));
    }

    // —— Карточка источника: данные, не рассказ ——

    [Fact]
    public void КарточкаИсточника_безСекцийРазбирается()
    {
        var r = WikiPage.Parse(Card("Карточка"));
        r.Errors.Should().BeEmpty();
        r.Model!.Frontmatter.Type.Should().Be("source");
    }

    [Fact]
    public void КарточкаИсточника_сСекциями_отбита()
    {
        var r = WikiPage.Parse(Card("Карточка") + "\n## Сводка\n");
        r.Model.Should().BeNull();
        r.Errors.Should().Contain(e => e.Contains("не может быть секций"));
    }

    // —— Область правила дословности (S6b-2, M2): GivenOnly ——

    private static readonly List<WikiGate.Draft> FootnoteState =
        [CardDraft, D(P1Dir + "Тет.md", Page(swodka: Good, footnotes: Foot))];

    [Fact]
    public void Гейт_дословностьGivenOnlyСноскаБезТекстаКарточки_проходит()
    {
        // Вызывающий дал тексты только своих источников: этой карточки в словаре нет —
        // её сноска (принятая раньше, по своему источнику) не может отвергнуть запись.
        WikiGate.Check(FootnoteState, P1, [], new Dictionary<string, string>(), WikiGate.VerbatimScope.GivenOnly)
            .Should().BeEmpty();

        // Тот же вход в режиме по умолчанию — по-прежнему нарушение (fail-closed).
        WikiGate.Check(FootnoteState, P1, [], new Dictionary<string, string>())
            .Should().Contain(e => e.Contains("нет текста источника карточки"));
    }

    [Fact]
    public void Гейт_дословностьGivenOnlyЦитатаВнеТекстаСвоейКарточки_отбита()
    {
        // Текст карточки у вызывающего ЕСТЬ — GivenOnly не отменяет дословность своей
        // карточки: цитата вне её текста — нарушение, как в All.
        var texts = new Dictionary<string, string> { [CardDraft.Path] = "совсем другой текст источника" };
        WikiGate.Check(FootnoteState, P1, [], texts, WikiGate.VerbatimScope.GivenOnly)
            .Should().Contain(e => e.Contains("отсутствует в тексте источника"));

        var own = new Dictionary<string, string> { [CardDraft.Path] = "вступление. " + Quote + " окончание" };
        WikiGate.Check(FootnoteState, P1, [], own, WikiGate.VerbatimScope.GivenOnly).Should().BeEmpty();
    }

    // —— B1 (ADR-038): автор пакета, карточка origin: human, секция «Заметки» ——

    private static string PageWithNotes(string notes, string footnotes = "", string swodka = "") =>
        Page(swodka: swodka, footnotes: footnotes).Replace("## История\n", "## История\n\n## Заметки\n" + notes + "\n");

    private static readonly WikiGate.Draft HumanCard =
        new(P1Dir + "Источники/Моя.md", Card("Моя", origin: "human"));

    [Fact]
    public void Гейт_карточкаHumanОтПакетаAi_отбита()
    {
        var violations = WikiGate.Check([HumanCard], P1, [], null, WikiGate.VerbatimScope.All,
            WikiPackage.AuthorAi, baseState: []);
        violations.Should().Contain(e => e.Contains("origin: human") && e.Contains("автора human"));
    }

    [Fact]
    public void Гейт_карточкаHumanПоУмолчаниюАвторAiБезБазы_отбита()
    {
        // Fail-closed: не названный автор — ai, базы нет — карточка считается новой.
        Check([HumanCard]).Should().Contain(e => e.Contains("origin: human"));
    }

    [Fact]
    public void Гейт_карточкаHumanОтПакетаHuman_проходит()
    {
        WikiGate.Check([HumanCard], P1, [], null, WikiGate.VerbatimScope.All,
            WikiPackage.AuthorHuman, baseState: []).Should().BeEmpty();
    }

    [Fact]
    public void Гейт_пакетAiСсылаетсяНаСуществующуюКарточкуHuman_проходит()
    {
        var state = new List<WikiGate.Draft>
        {
            HumanCard,
            D(P1Dir + "Тет.md", Page(swodka: Good, footnotes: Foot.Replace("Карточка", "Моя"))),
        };
        // Карточка уже была до пакета: ИИ цитирует человека, создавать нечего.
        WikiGate.Check(state, P1, [], null, WikiGate.VerbatimScope.All, WikiPackage.AuthorAi, baseState: [HumanCard])
            .Should().BeEmpty();
    }

    [Fact]
    public void Гейт_пакетAiПодменилСуществующуюКарточкуHuman_отбит()
    {
        var changed = new WikiGate.Draft(HumanCard.Path, Card("Моя", origin: "human").Replace("hash: h1", "hash: h2"));
        WikiGate.Check([changed], P1, [], null, WikiGate.VerbatimScope.All, WikiPackage.AuthorAi, baseState: [HumanCard])
            .Should().Contain(e => e.Contains("origin: human"));
    }

    [Fact]
    public void Гейт_заметкиОтПакетаAi_новаяСекцияОтбита()
    {
        var page = D(P1Dir + "Тет.md", PageWithNotes("Моя мысль."));
        var plain = D(P1Dir + "Тет.md", Page());
        WikiGate.Check([page], P1, [], null, WikiGate.VerbatimScope.All, WikiPackage.AuthorAi, baseState: [plain])
            .Should().Contain(e => e.Contains("«Заметки»") && e.Contains("автора human"));
    }

    [Fact]
    public void Гейт_заметкиОтПакетаAi_правкаИУдалениеОтбиты_БезИзмененийПроходит()
    {
        var before = D(P1Dir + "Тет.md", PageWithNotes("Моя мысль."));
        var edited = D(P1Dir + "Тет.md", PageWithNotes("Подправлено ИИ."));
        var removed = D(P1Dir + "Тет.md", Page());
        WikiGate.Check([edited], P1, [], null, WikiGate.VerbatimScope.All, WikiPackage.AuthorAi, baseState: [before])
            .Should().Contain(e => e.Contains("«Заметки»"));
        WikiGate.Check([removed], P1, [], null, WikiGate.VerbatimScope.All, WikiPackage.AuthorAi, baseState: [before])
            .Should().Contain(e => e.Contains("«Заметки»"));
        WikiGate.Check([before], P1, [], null, WikiGate.VerbatimScope.All, WikiPackage.AuthorAi, baseState: [before])
            .Should().BeEmpty("пакет ai не задевает существующие «Заметки» — это не изменение");
    }

    [Fact]
    public void Гейт_заметкиОтПакетаHuman_проходят_ИбезСносок()
    {
        var page = D(P1Dir + "Тет.md", PageWithNotes("| a | b |\n|---|---|\n| 1 | 2 |"));
        WikiGate.Check([page], P1, [], null, WikiGate.VerbatimScope.All, WikiPackage.AuthorHuman, baseState: [])
            .Should().BeEmpty();

        var withRef = D(P1Dir + "Тет.md", PageWithNotes("Моя мысль.[^s1]", footnotes: Foot));
        WikiGate.Check([withRef, CardDraft], P1, [], null, WikiGate.VerbatimScope.All, WikiPackage.AuthorHuman, baseState: [])
            .Should().Contain(e => e.Contains("нет сносок"));
    }

    [Fact]
    public void Гейт_неизвестныйАвтор_отбит()
    {
        WikiGate.Check([], P1, [], null, WikiGate.VerbatimScope.All, "robot")
            .Should().Contain(e => e.Contains("автор пакета"));
    }

    [Fact]
    public void Страница_заметкиТолькоПоследней_ПорядокСоблюдён()
    {
        var ok = WikiPage.Parse(PageWithNotes("Текст."));
        ok.Errors.Should().BeEmpty();
        ok.Model!.Notes!.Lines.Should().Equal("Текст.");
        WikiPage.Parse(ok.Model.Render()).Model.Should().Be(ok.Model);

        // «Заметки» не на своём месте (до «История») — нарушение порядка.
        var early = Page().Replace("## Противоречия\n", "## Заметки\nх\n\n## Противоречия\n");
        WikiPage.Parse(early).Model.Should().BeNull();
        // Страница без «Заметки» остаётся валидной.
        WikiPage.Parse(Page()).Model!.Notes.Should().BeNull();
    }

    [Fact]
    public void Origin_human_вПеречне_НоАиЗаявкаЕгоНеМожет()
    {
        WikiGate.OriginValues.Should().Contain(WikiGate.OriginHuman);
        WikiGate.AiOriginValues.Should().NotContain(WikiGate.OriginHuman);
        WikiGate.AiOriginValues.Should().HaveCount(WikiGate.OriginValues.Count - 1);
    }
}
