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
    public void ЧетыреОбщихРедкихОсновы_СуммаВесовЛогарифма()
    {
        var idx = Snapshot(("a", "Страница А", "аметист бирюза гранат хризолит"),
            ("b", "Страница Б", "аметист бирюза гранат хризолит"));

        WikiTopicScore.Score(idx, "a", "b").Should().BeApproximately(4 * Math.Log(24.0 / 2), 1e-9);
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

        WikiTopicScore.Score(idx, "a", "b").Should().BeNull("2·ln(12) ≈ 5.0 < 6");
        WikiTopicScore.Score(idx, "a", "b", minScore: 0).Should().BeApproximately(2 * Math.Log(12), 1e-9);
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

    [Fact]
    public void Rank_УбываниеОценки_РавенствоПоOrdinalПути_Потолок()
    {
        var s = "аметист бирюза гранат хризолит";
        var idx = Snapshot(("t", "Цель", s), ("z", "Я", s), ("b", "Б", s), ("y", "Ы", "аметист бирюза гранат"), ("m", "М", "аметист топаз"));

        var ranked = WikiTopicScore.Rank(idx, "t", ["z", "y", "b", "m", "t"], minScore: 0, take: 3);

        ranked.Select(r => r.Path).Should().Equal(["b", "z", "y"], "по убыванию, при равенстве — по Ordinal; m недопустима");
        WikiTopicScore.Rank(idx, "t", ["z", "y", "b"], take: 2).Select(r => r.Path).Should().Equal("b", "z");
        WikiTopicScore.Rank(idx, "t", ["z", "b"], take: 0).Should().BeEmpty();
    }
}
