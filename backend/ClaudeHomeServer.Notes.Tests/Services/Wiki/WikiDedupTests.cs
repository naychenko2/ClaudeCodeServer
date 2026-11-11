using ClaudeHomeServer.Services.Notes.Wiki;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

// S4d (ADR-035, §12г, мера 2): дедуп кодом до применения. Повтор утверждения отбрасывается
// молча; новая страница при совпадении — «возможный дубль» со ссылкой на найденную.
// Нормализация цитат и текста — QuoteMatcher (S6a), имён — NFC + регистр (как у уникальности
// WikiGate): дубль, который не увидел гейт, не должен увидеть и дедуп.
public class WikiDedupTests
{
    private const string Card = "Проекты/p1/Источники/Чат-1";

    private static WikiPage Page(
        string title,
        string[]? aliases = null,
        string[]? owns = null,
        string[]? summary = null,
        (string Card, string Quote)[]? footnotes = null,
        string[]? history = null) => new()
        {
            Title = title,
            Frontmatter = new WikiFrontmatter
            {
                Type = WikiFrontmatter.TypeTopic,
                Aliases = aliases ?? [],
                Owns = owns ?? [],
            },
            Sections =
            [
                new WikiPage.Section { Name = "Сводка", Lines = summary ?? [] },
                new WikiPage.Section { Name = "Хронология", Lines = [] },
                new WikiPage.Section { Name = "Противоречия", Lines = [] },
                new WikiPage.Section { Name = "История", Lines = history ?? [] },
            ],
            Footnotes = footnotes is null
                ? []
                : footnotes.Select(f => new WikiPage.WikiFootnote(new WikiPage.Target(true, f.Card), f.Quote)).ToList(),
        };

    private static WikiDedup.ExistingPage Existing(string path, WikiPage page) => new(path, page);

    // Снимок страницы для QuoteOwner: текст — рендер модели (round-trip Parse→Render
    // даёт ту же модель — инвариант модели страницы).
    private static WikiGate.Draft Draft(string path, WikiPage page) => new(path, page.Render());

    // ─── Утверждение: повтор отбрасывается ────────────────────────────────────

    [Fact]
    public void IsDuplicateStatement_ТотЖеИсточникИТаЖеЦитатаСДругимиПробеламиИКавычками_Отброшен()
    {
        var page = Page("Сущность", summary: ["проект ведётся на 'Rust' [^s1]"],
            footnotes: [(Card, "проект ведётся на 'Rust'")]);

        var same = WikiDedup.IsDuplicateStatement(
            page, Card, "новое оформление той же цитаты", "проект   ведётся на \"Rust\"");

        same.Should().BeTrue();
    }

    [Fact]
    public void IsDuplicateStatement_ТотЖеИсточникДругаяЦитатаИНовыйТекст_НеПовтор()
    {
        var page = Page("Сущность", summary: ["проект ведётся на 'Rust' [^s1]"],
            footnotes: [(Card, "проект ведётся на 'Rust'")]);

        var fresh = WikiDedup.IsDuplicateStatement(
            page, Card, "собрание держалось в пятницу", "собрание держалось в пятницу");

        fresh.Should().BeFalse();
    }

    [Fact]
    public void IsDuplicateStatement_ТотЖеЦитатаДругогоИсточника_НеПовтор()
    {
        var page = Page("Сущность", summary: ["проект ведётся на 'Rust' [^s1]"],
            footnotes: [(Card, "проект ведётся на 'Rust'")]);

        var otherSource = WikiDedup.IsDuplicateStatement(
            page, "Проекты/p1/Источники/Досье-9", "ещё раз про Rust", "проект ведётся на 'Rust'");

        // Мера 2 говорит о паре «тот же источник + та же цитата»: без той же карточки
        // это новое подтверждение того же факта, а не повтор.
        otherSource.Should().BeFalse();
    }

    [Fact]
    public void IsDuplicateStatement_ТекстУжеСтоитНаСтранице_Отброшен_БезСовпаденияЦитат()
    {
        var page = Page("Сущность", summary: ["собрание держалось в пятницу [^s1]"],
            footnotes: [(Card, "собрание держалось")] );

        var repeated = WikiDedup.IsDuplicateStatement(
            page, "Проекты/p1/Источники/Задача-3", "собрание  держалось в пятницу", "совсем другая цитата источника");

        repeated.Should().BeTrue();
    }

    [Fact]
    public void IsDuplicateStatement_СовпадениеСЦитатойВОпределенииСноски_НеПовтор()
    {
        // Определения сносок несут цитаты источников: совпадение текста утверждения с
        // чужой цитатой в определении не значит, что утверждение уже есть на странице.
        var page = Page("Сущность", summary: ["ничего похожего [^s1]"],
            footnotes: [(Card, "полностью совпадающий текст утверждения")]);

        var fresh = WikiDedup.IsDuplicateStatement(
            page, "Проекты/p1/Источники/Задача-3", "полностью совпадающий текст утверждения", "иная цитата");

        fresh.Should().BeFalse();
    }

    [Fact]
    public void IsDuplicateStatement_ПустыеПоляИНеРазобраннаяСтраница_НеОтбрасывают()
    {
        var page = Page("Сущность", summary: ["проект ведётся на 'Rust' [^s1]"],
            footnotes: [(Card, "проект ведётся на 'Rust'")]);

        WikiDedup.IsDuplicateStatement(null, Card, "новое утверждение", "новая цитата").Should().BeFalse();
        // Пустая цитата и пустой текст — не доказательство повтора: утверждение остаётся.
        WikiDedup.IsDuplicateStatement(page, Card, "новое утверждение", "").Should().BeFalse();
        WikiDedup.IsDuplicateStatement(page, Card, "  ", "новая цитата").Should().BeFalse();
        WikiDedup.IsDuplicateStatement(page, "", "проект ведётся на 'Rust'", "проект ведётся на 'Rust'")
            .Should().BeTrue();
    }

    // ─── Новая страница: возможный дубль со ссылкой на найденную ──────────────

    [Fact]
    public void FindPageDuplicate_АлиасНовойСтраницыРавенЗаголовкуСтраницыCommon_ВозможныйДубль()
    {
        // Новая страница — в разделе projects/P; существующая — в common, т.е. из UpChain
        // этого раздела (ADR-036, решение 4): дубль виден вверх по цепочке.
        var common = Existing("Общее/Проект на Rust.md", Page("Проект на Rust"));

        var dup = WikiDedup.FindPageDuplicate(
            "Сущность проекта", ["проект на rust"], null, [common]);

        dup.HasValue.Should().BeTrue();
        var d = dup.GetValueOrDefault();
        d.Path.Should().Be("Общее/Проект на Rust.md");
        d.Field.Should().Be("алиас");
        d.ExistingField.Should().Be("заголовок");
    }

    [Fact]
    public void FindPageDuplicate_СовпадениеЗаголовкаВСвоёмРазделеИОwnsВверхПоЦепочке_ВозможныйДубль()
    {
        var inSection = Existing("Проекты/p1/Другая.md", Page("Тот же заголовок"));
        var inChain = Existing("Общее/Сущность.md", Page("Сущность", owns: ["rust-проект"]));

        var byTitle = WikiDedup.FindPageDuplicate("тот ЖЕ заголовок", null, null, [inSection, inChain]);
        var byOwns = WikiDedup.FindPageDuplicate("новое имя", null, ["Rust-Проект"], [inSection, inChain]);

        byTitle.HasValue.Should().BeTrue();
        var byTitleValue = byTitle.GetValueOrDefault();
        byTitleValue.Path.Should().Be("Проекты/p1/Другая.md");
        byTitleValue.Field.Should().Be("заголовок");
        byOwns.HasValue.Should().BeTrue();
        var byOwnsValue = byOwns.GetValueOrDefault();
        byOwnsValue.Path.Should().Be("Общее/Сущность.md");
        byOwnsValue.Field.Should().Be("owns");
    }

    [Fact]
    public void FindPageDuplicate_НормализацияNFCИРегистрОдинКод_СоставленнаяИБукваРазные()
    {
        // «café» как U+0065 U+0301 против «café» как U+00E9» — после NFC одно имя.
        var decomposed = Existing("Общее/cafe.md", Page("café"));

        var dup = WikiDedup.FindPageDuplicate("CAFÉ", null, null, [decomposed]);

        dup.HasValue.Should().BeTrue();
        dup.GetValueOrDefault().Path.Should().Be("Общее/cafe.md");
    }

    [Fact]
    public void FindPageDuplicate_НесовпаденияПустойНаборИПустыеИмена_ДубляНет()
    {
        var other = Existing("Общее/Другое.md", Page("Другая сущность", aliases: ["иное"], owns: ["иное-1"]));

        // Несовпадающие имена.
        WikiDedup.FindPageDuplicate("Сущность проекта", ["что-то третье"], null, [other]).HasValue.Should().BeFalse();
        // Пустой набор существующих (раздел пуст) и null-набор — дубля нет.
        WikiDedup.FindPageDuplicate("Сущность проекта", null, null, []).HasValue.Should().BeFalse();
        WikiDedup.FindPageDuplicate("Сущность проекта", null, null, null).HasValue.Should().BeFalse();
        // Пустые имена новой страницы ничего не сравнивают: дубль не объявляется по пустоте.
        WikiDedup.FindPageDuplicate("  ", ["", " "], null, [other]).HasValue.Should().BeFalse();
    }

    // ─── QuoteOwner: «одна цитата — одна страница» (ADR-039, решение 2) ──────

    private static WikiPage PageWithCitation(string title, string[]? history = null) =>
        Page(title, summary: ["проект ведётся на 'Rust' [^s1]"],
            footnotes: [(Card, "проект ведётся на 'Rust'")], history: history);

    [Fact]
    public void QuoteOwner_ПараУжеСтоитНаДругойСтраницеРаздела_ВозвращаетЕёПуть()
    {
        var other = Draft("Проекты/p1/Другая.md", PageWithCitation("Другая"));

        var owner = WikiDedup.QuoteOwner([other], Card, "проект ведётся на 'Rust'", null);

        owner.Should().Be("Проекты/p1/Другая.md");
    }

    [Fact]
    public void QuoteOwner_ПараВоВторойСноскеПерваяЧужая_ВозвращаетПутьСтраницы()
    {
        // Страница с двумя сносками: первая — чужая (её ссылка только в «Истории»,
        // утверждение вытеснено), наша пара — во второй (i = 1, ссылка [^s2]).
        // Владелец определяется индексом сноски в модели: допущение «сноска с индексом i —
        // это s{i+1}» проверяется здесь — поиск «любой s1» вместо s{i+1} краснит тест.
        var page = Page("Другая",
            summary: ["проект ведётся на 'Rust' [^s2]"],
            footnotes:
            [
                ("Проекты/p1/Источники/Досье-9", "чужая цитата чужого источника"),
                (Card, "проект ведётся на 'Rust'"),
            ],
            history: ["перенесено: чужая цитата [^s1]"]);

        var other = Draft("Проекты/p1/Другая.md", page);

        WikiDedup.QuoteOwner([other], Card, "проект ведётся на 'Rust'", null)
            .Should().Be("Проекты/p1/Другая.md");
    }

    [Fact]
    public void QuoteOwner_ТаЖеЦитатаДругойКарточкиИНоваяЦитата_НикемНеЗанята()
    {
        var occupied = Draft("Проекты/p1/Другая.md", PageWithCitation("Другая"));

        // Ключ — пара (цитата, карточка): та же цитата из другой карточки — не занятие.
        WikiDedup.QuoteOwner([occupied], "Проекты/p1/Источники/Досье-9", "проект ведётся на 'Rust'", null)
            .Should().BeNull();
        // Та же карточка, другая цитата — тоже не занятие.
        WikiDedup.QuoteOwner([occupied], Card, "собрание держалось в пятницу", null).Should().BeNull();
        // Пустые цитата и карточка — нечего искать.
        WikiDedup.QuoteOwner([occupied], Card, "", null).Should().BeNull();
        WikiDedup.QuoteOwner([occupied], "", "проект ведётся на 'Rust'", null).Should().BeNull();
        // Пустой список (раздел пуст) — занятия нет.
        WikiDedup.QuoteOwner([], Card, "проект ведётся на 'Rust'", null).Should().BeNull();
    }

    [Fact]
    public void QuoteOwner_СноскаТолькоВИстории_ЦитатуНеЗанимает_ЕёВытеснили()
    {
        // Ссылка на сноску стоит только в «Истории» — перенесённое вытесненное утверждение,
        // оно больше не владеет цитатой.
        var displaced = Draft("Проекты/p1/Вытесненная.md",
            Page("Вытесненная", footnotes: [(Card, "проект ведётся на 'Rust'")],
                history: ["перенесено: проект ведётся на 'Rust' [^s1]"]));

        WikiDedup.QuoteOwner([displaced], Card, "проект ведётся на 'Rust'", null).Should().BeNull();
    }

    [Fact]
    public void QuoteOwner_СсылкаЕСТоИВДругойСекции_ЗанимаетНесмотряНаИсторию()
    {
        var both = PageWithCitation("Другая", history: ["перенесено: проект ведётся на 'Rust' [^s1]"]);
        var occupied = Draft("Проекты/p1/Другая.md", both);

        WikiDedup.QuoteOwner([occupied], Card, "проект ведётся на 'Rust'", null)
            .Should().Be("Проекты/p1/Другая.md");
    }

    [Fact]
    public void QuoteOwner_СтраницаОперацииИсключена_ПовторНаНейЛовитIsDuplicateStatement()
    {
        var own = Draft("Проекты/p1/Сама.md", PageWithCitation("Сама"));

        // exceptPage — страница самой операции: её сноски не считаются.
        WikiDedup.QuoteOwner([own], Card, "проект ведётся на 'Rust'", "Проекты/p1/Сама.md")
            .Should().BeNull();
    }

    [Fact]
    public void QuoteOwner_СтраницаБезМоделиПропускается_БезИсключенияНаружу()
    {
        var broken = new WikiGate.Draft("Проекты/p1/Сломанная.md", "это не страница: ни шапки, ни заголовка");
        var other = Draft("Проекты/p1/Другая.md", PageWithCitation("Другая"));

        // Неразобранная страница не роняет дедуп и не даёт занятия…
        WikiDedup.QuoteOwner([broken], Card, "проект ведётся на 'Rust'", null).Should().BeNull();
        // …и не мешает найти владельца дальше по списку.
        WikiDedup.QuoteOwner([broken, other], Card, "проект ведётся на 'Rust'", null)
            .Should().Be("Проекты/p1/Другая.md");
    }

    [Fact]
    public void QuoteOwner_ЦитатаСДругимиПробеламиИКавычками_ВладелецНайден_ТаЖеНормализация()
    {
        var occupied = Draft("Проекты/p1/Другая.md", PageWithCitation("Другая"));

        var owner = WikiDedup.QuoteOwner(
            [occupied], Card, "проект   ведётся на \"Rust\"", null);

        owner.Should().Be("Проекты/p1/Другая.md");
    }

    [Fact]
    public void QuoteOwner_НесколькоВладельцев_ПервыйПоПорядкуСписка()
    {
        var first = Draft("Проекты/p1/Первая.md", PageWithCitation("Первая"));
        var second = Draft("Проекты/p1/Вторая.md", PageWithCitation("Вторая"));

        WikiDedup.QuoteOwner([first, second], Card, "проект ведётся на 'Rust'", null)
            .Should().Be("Проекты/p1/Первая.md");
    }

    [Fact]
    public void NormalizeName_ТотЖеКодЧтоУУникальностиГейта_ОбрамляющиеПробелыNFCРегистр()
    {
        WikiDedup.NormalizeName("  Проект на Rust ").Should().Be("проект на rust");
        WikiDedup.NormalizeName("café").Should().Be(WikiDedup.NormalizeName("café"));
        // Пустое имя не становится ничем, с чем можно совпасть.
        WikiDedup.NormalizeName("   ").Should().BeEmpty();
    }
}
