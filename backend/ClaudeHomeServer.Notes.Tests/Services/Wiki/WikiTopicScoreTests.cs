using ClaudeHomeServer.Services.Notes.Wiki;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// «По теме» (выкат 1): чистый скорер по редким общим основам. Снимок — 24 страницы, так что
// основа с df = 2 весит ln(12), а порог доли 0.25 режет основы, которые есть больше чем у 6.
public class WikiTopicScoreTests
{
    private static WikiTopicScore.Index Snapshot(params (string Path, string Title, string? Summary)[] special)
    {
        var entries = special.Select(s => new WikiTopicScore.Entry(s.Path, s.Title, s.Summary)).ToList();
        for (var i = entries.Count; i < 24; i++)
            entries.Add(new WikiTopicScore.Entry($"фон{i}", $"Фоновая{i}", null));
        return WikiTopicScore.BuildIndex(entries);
    }

    [Fact]
    public void ПятьОбщихРедкихОсновы_СуммаВесовЛогарифма()
    {
        var idx = Snapshot(("a", "Страница А", "аметист бирюза гранат хризолит яшма"),
            ("b", "Страница Б", "аметист бирюза гранат хризолит яшма"));

        WikiTopicScore.Score(idx, "a", "b").Should().BeApproximately(5 * Math.Log(24.0 / 2), 1e-9);
    }

    [Fact]
    public void ОднаОбщаяОснова_ПараНедопустимаДажеБезПорога()
    {
        var idx = Snapshot(("a", "Страница А", "аметист"), ("b", "Страница Б", "аметист"));

        WikiTopicScore.Score(idx, "a", "b", minScore: 0).Should().BeNull();
    }

    [Fact]
    public void ДвеОсновыНижеПорога_ОтсечённыПоMinScore_ИзОтданыБезПорога()
    {
        var idx = Snapshot(("a", "Страница А", "аметист бирюза"), ("b", "Страница Б", "аметист бирюза"));

        WikiTopicScore.Score(idx, "a", "b").Should().BeNull("2·ln(12) ≈ 5.0 < 11");
        WikiTopicScore.Score(idx, "a", "b", minScore: 0).Should().BeApproximately(2 * Math.Log(12), 1e-9);
    }

    [Fact]
    public void ПорогMinScore_Равен11_СторожЗначения()
    {
        // Значение порога откалибровано замером T0 (нижняя граница корзины E, 11+;
        // WikiTopicScore.cs:13-17). Отдельный сторож: смена значения падает здесь, а не
        // в поведенческом тесте ниже, где она тонула среди проверок пары (хвост R-T1b, п. 1).
        WikiTopicScore.MinScore.Should().Be(11);
    }

    [Fact]
    public void ТриОсновы_СуммаМеждуСтарымИНовымПорогом_ОтсечённаПоMinScore()
    {
        var idx = Snapshot(("a", "Страница А", "аметист бирюза гранат"), ("b", "Страница Б", "аметист бирюза гранат"));

        WikiTopicScore.Score(idx, "a", "b").Should().BeNull("3·ln(12) ≈ 7.5 < 11 (при прежнем 6 пара проходила)");
        WikiTopicScore.Score(idx, "a", "b", minScore: 0).Should().BeApproximately(3 * Math.Log(12), 1e-9);
    }

    [Fact]
    public void ЧастаяОснова_БольшеДоли_НеСчитается()
    {
        // «аметист» есть у 7 страниц из 24 (> 25 %) — не считается; общих остаётся две.
        var idx = Snapshot(("a", "Страница А", "аметист бирюза гранат"),
            ("b", "Страница Б", "аметист бирюза гранат"),
            ("c", "Страница В", "аметист"), ("d", "Страница Г", "аметист"), ("e", "Страница Д", "аметист"),
            ("f", "Страница Е", "аметист"), ("g", "Страница Ж", "аметист"));

        WikiTopicScore.Score(idx, "a", "b", minScore: 0).Should().BeApproximately(2 * Math.Log(12), 1e-9);
    }

    [Fact]
    public void ОсноваИзОбоихЗаголовков_ВеситВдвое()
    {
        var idx = Snapshot(("a", "Аметист и бирюза", null), ("b", "Аметист бирюза", null));

        WikiTopicScore.Score(idx, "a", "b", minScore: 0).Should().BeApproximately(2 * 2 * Math.Log(12), 1e-9);
    }

    [Fact]
    public void ОсноваИзОдногоЗаголовка_ВесНеУдваивается()
    {
        var idx = Snapshot(("a", "Аметист бирюза", null), ("b", "Страница Б", "аметист бирюза"));

        WikiTopicScore.Score(idx, "a", "b", minScore: 0).Should().BeApproximately(2 * Math.Log(12), 1e-9);
    }

    // ─── пины снимка (хвост R-T1, п. 3): N=1, df=N, пустая сводка ───

    [Fact]
    public void Пин_N1_ЕдинственнаяСтраница_КандидатовНет()
    {
        // Снимок из одной страницы (n=1): оценка пары не строится — Rank исключает саму
        // цель, пары вне индекса нет; и при minScore=0: единственная страница даёт df каждой
        // основы = n (1 > 0,25) — редких основ нет вовсе.
        var idx = WikiTopicScore.BuildIndex([new WikiTopicScore.Entry("a", "Замок вишнёвый", "замок вишнёвый")]);

        idx.PageCount.Should().Be(1);
        WikiTopicScore.Rank(idx, "a", ["a"]).Should().BeEmpty("цель не кандидат сама себе");
        WikiTopicScore.Score(idx, "a", "нет-такой").Should().BeNull("пары вне индекса нет");
        WikiTopicScore.Score(idx, "a", "a", minScore: 0).Should().BeNull("при n=1 каждая основа частая");
    }

    [Fact]
    public void Пин_DfРавнаN_ВсеОсновыЧастые_ПарыНет_ДажеБезПорога()
    {
        // df каждой общей основы = N (2 из 2 — доля 1 > MaxDfShare): основа не называет
        // предмет, общих редких меньше MinCommon — пары нет ни при каком minScore.
        var idx = WikiTopicScore.BuildIndex([
            new WikiTopicScore.Entry("a", "Замок вишнёвый", "замок вишнёвый"),
            new WikiTopicScore.Entry("b", "Замок вишнёвый", "замок вишнёвый")]);

        idx.PageCount.Should().Be(2);
        WikiTopicScore.Score(idx, "a", "b", minScore: 0).Should().BeNull();
    }

    [Fact]
    public void Пин_ПустаяСводка_РавнаЕёОтсутствию()
    {
        // Пустая сводка к тексту страницы ничего не добавляет: BuildIndex берёт один
        // заголовок (тот же путь, что для null) — оценки пустой и отсутствующей сводки равны.
        var empty = Snapshot(("a", "Аметист бирюза", ""), ("b", "Аметист бирюза", ""));
        var none = Snapshot(("a", "Аметист бирюза", null), ("b", "Аметист бирюза", null));

        WikiTopicScore.Score(empty, "a", "b", minScore: 0).Should().BeApproximately(2 * 2 * Math.Log(12), 1e-9);
        WikiTopicScore.Score(empty, "a", "b", minScore: 0)
            .Should().Be(WikiTopicScore.Score(none, "a", "b", minScore: 0),
                "пустая сводка равна её отсутствию");
    }

    [Fact]
    public void Rank_УбываниеОценки_РавенствоПоOrdinalПути_Потолок()
    {
        var s = "аметист бирюза гранат хризолит яшма малахит нефрит";
        var idx = Snapshot(("t", "Цель", s), ("z", "Я", s), ("b", "Б", s), ("y", "Ы", "аметист бирюза гранат"), ("m", "М", "аметист топаз"));

        var ranked = WikiTopicScore.Rank(idx, "t", ["z", "y", "b", "m", "t"], minScore: 0, take: 3);

        ranked.Select(r => r.Path).Should().Equal(["b", "z", "y"], "по убыванию, при равенстве — по Ordinal; m недопустима");
        WikiTopicScore.Rank(idx, "t", ["z", "y", "b"], take: 2).Select(r => r.Path).Should().Equal("b", "z");
        WikiTopicScore.Rank(idx, "t", ["z", "b"], take: 0).Should().BeEmpty();
    }

    [Fact]
    public void Rank_ПорядокВходаНеВлияет_РавныеОценкиПоOrdinal()
    {
        var s = "аметист бирюза гранат хризолит яшма малахит нефрит";
        var idx = Snapshot(("t", "Цель", s), ("c", "Ц", s), ("a", "А", s), ("b", "Б", s));

        var one = WikiTopicScore.Rank(idx, "t", ["c", "a", "b"]).Select(r => r.Path);
        var two = WikiTopicScore.Rank(idx, "t", ["b", "c", "a"]).Select(r => r.Path);

        one.Should().Equal("a", "b", "c");
        two.Should().Equal(one);
    }

    [Fact]
    public void BuildIndex_ВызововСловНеБольшеЧемСтраницПлюсЗаголовкиДопустимыхПар()
    {
        var calls = 0;
        HashSet<string> Counted(string t) { calls++; return WikiCandidates.SignificantWords(t); }
        var entries = Enumerable.Range(0, 50).Select(i => new WikiTopicScore.Entry($"p{i}", $"З{i}", $"уникальное{i}слово{i}"));

        WikiTopicScore.BuildIndex(entries, Counted);

        calls.Should().Be(50, "один вызов на страницу, не на пару");
    }

    // ─── TitleSubset (C1): вложение основ заголовков без порога ───

    [Theory]
    [InlineData("", "Каталог исходных")]
    [InlineData("Каталог исходных", "")]
    [InlineData("", "")]
    public void TitleSubset_ПустойЗаголовок_False(string a, string b) =>
        WikiTopicScore.TitleSubset(a, b).Should().BeFalse();

    [Fact]
    public void TitleSubset_РегистрИЁ_НеВлияют()
    {
        WikiTopicScore.TitleSubset("ЗАМОК ВИШНЁВЫЙ", "Большой замок вишневый").Should().BeTrue();
    }

    [Fact]
    public void TitleSubset_ПовторяющиесяОсновы_СхлопываютсяВНабор()
    {
        // «замок замок» — одна основа: короче MinCommon, вложение не считается.
        WikiTopicScore.TitleSubset("замок замок", "замок вишневый").Should().BeFalse();
        // а повтор в большем заголовке вложению не мешает.
        WikiTopicScore.TitleSubset("замок вишневый", "замок замок вишневый").Should().BeTrue();
    }

    [Fact]
    public void TitleSubset_Симметрия_ПорядокАргументовНеВлияет()
    {
        WikiTopicScore.TitleSubset("Каталог исходных", "Содержание каталога исходных")
            .Should().Be(WikiTopicScore.TitleSubset("Содержание каталога исходных", "Каталог исходных"))
            .And.Be(true);
        WikiTopicScore.TitleSubset("замок вишневый", "река мутная")
            .Should().Be(WikiTopicScore.TitleSubset("река мутная", "замок вишневый"))
            .And.Be(false);
    }

    [Fact]
    public void TitleSubset_ДвеОсновы_ГраницаMinCommon()
    {
        // ровно две общие основы — вложение есть; одна — нет (одна общая основа не предмет).
        WikiTopicScore.TitleSubset("замок вишневый", "старый замок вишневый").Should().BeTrue();
        WikiTopicScore.TitleSubset("замок", "старый замок вишневый").Should().BeFalse();
        // две основы, но не вложены (одна чужая) — нет.
        WikiTopicScore.TitleSubset("замок вишневый", "старый замок мутный").Should().BeFalse();
    }
}
