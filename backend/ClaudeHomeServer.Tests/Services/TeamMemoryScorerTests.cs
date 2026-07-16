using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Чистый скоринг памяти команды: взвешенная сумма, гейт релевантности, порядок typeFactor,
// recency от создания, кламп salience, вытеснение по retention-скорингу
public class TeamMemoryScorerTests
{
    private static readonly DateTime Now = new(2026, 7, 16, 12, 0, 0, DateTimeKind.Utc);
    private static readonly MemoryScoringOptions Opts = MemoryScoringOptions.Default;

    private static TeamMemoryEntry Entry(TeamMemoryType type = TeamMemoryType.Fact,
        double salience = 1.0, double ageDays = 0) => new()
    {
        OwnerId = "o1",
        ProjectId = "p1",
        Type = type,
        Text = "запись",
        Salience = salience,
        CreatedAt = Now.AddDays(-ageDays),
    };

    [Fact]
    public void Сумма_СовпадаетСЭталоном()
    {
        // Свежая fact-запись, salience 0.8, relevance 0.5:
        // 0.55·0.5 + 0.20·1 + 0.15·0.8 + 0.10·0.8 = 0.275 + 0.20 + 0.12 + 0.08 = 0.675
        var score = TeamMemoryScorer.Score(Entry(salience: 0.8), 0.5, Now, Opts);

        score.Should().BeApproximately(0.675, 1e-9);
    }

    [Fact]
    public void ГейтMinRelevance_НерелевантнаяЗапись_Ноль()
    {
        var score = TeamMemoryScorer.Score(Entry(), relevance: 0.01, Now, Opts);

        score.Should().Be(0);
    }

    [Fact]
    public void TypeFactor_Порядок_DecisionВышеConventionВышеFactВышеGlossary()
    {
        TeamMemoryScorer.TypeFactor(TeamMemoryType.Decision)
            .Should().BeGreaterThan(TeamMemoryScorer.TypeFactor(TeamMemoryType.Convention));
        TeamMemoryScorer.TypeFactor(TeamMemoryType.Convention)
            .Should().BeGreaterThan(TeamMemoryScorer.TypeFactor(TeamMemoryType.Fact));
        TeamMemoryScorer.TypeFactor(TeamMemoryType.Fact)
            .Should().BeGreaterThan(TeamMemoryScorer.TypeFactor(TeamMemoryType.Glossary));
    }

    [Fact]
    public void Recency_ОтсчитываетсяОтСоздания()
    {
        var fresh = Entry(ageDays: 0);
        var old = Entry(ageDays: 60);

        var scoreFresh = TeamMemoryScorer.Score(fresh, 0.5, Now, Opts);
        var scoreOld = TeamMemoryScorer.Score(old, 0.5, Now, Opts);

        // Разница = wRec·(1 − 2^(−60/30)) = 0.20·0.75
        (scoreFresh - scoreOld).Should().BeApproximately(0.20 * 0.75, 1e-9);
    }

    [Fact]
    public void ДатаВБудущем_ВозрастНеОтрицательный()
    {
        // Часы уехали: создание «в будущем» — recency не больше 1
        var future = Entry(ageDays: -5);

        TeamMemoryScorer.Score(future, 0.5, Now, Opts)
            .Should().BeApproximately(TeamMemoryScorer.Score(Entry(), 0.5, Now, Opts), 1e-9);
    }

    [Fact]
    public void Salience_КлампитсяВНольОдин()
    {
        TeamMemoryScorer.Score(Entry(salience: 5.0), 0.5, Now, Opts)
            .Should().Be(TeamMemoryScorer.Score(Entry(salience: 1.0), 0.5, Now, Opts));
        TeamMemoryScorer.Score(Entry(salience: -1.0), 0.5, Now, Opts)
            .Should().Be(TeamMemoryScorer.Score(Entry(salience: 0.0), 0.5, Now, Opts));
    }

    // --- SelectEvictionIds: порядок и объём вытеснения ---

    [Fact]
    public void SelectEvictionIds_НетПереполнения_Пусто()
    {
        var entries = Enumerable.Range(1, 5).Select(_ => Entry()).ToList();

        TeamMemoryScorer.SelectEvictionIds(entries, 5, Opts, Now).Should().BeEmpty();
        TeamMemoryScorer.SelectEvictionIds(entries, 0, Opts, Now).Should().BeEmpty();   // потолок выключен
    }

    [Fact]
    public void SelectEvictionIds_ВытесняетНаименееЦенные()
    {
        var valuable = Entry(TeamMemoryType.Decision, salience: 1.0, ageDays: 0);
        var stale = Entry(TeamMemoryType.Glossary, salience: 0.1, ageDays: 120);   // старая мелочь — первая на выход
        var medium = Entry(TeamMemoryType.Fact, salience: 0.5, ageDays: 30);
        var entries = new List<TeamMemoryEntry> { valuable, stale, medium };

        var evicted = TeamMemoryScorer.SelectEvictionIds(entries, 2, Opts, Now);

        evicted.Should().ContainSingle().Which.Should().Be(stale.Id);
    }

    [Fact]
    public void SelectEvictionIds_ВытесняетНужноеКоличество()
    {
        var entries = Enumerable.Range(1, 10)
            .Select(i => Entry(salience: i / 10.0, ageDays: i))
            .ToList();

        TeamMemoryScorer.SelectEvictionIds(entries, 7, Opts, Now).Should().HaveCount(3);
    }
}
