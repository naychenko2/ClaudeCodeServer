using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Чистый скоринг памяти персоны: взвешенная сумма, гейт релевантности,
// recency от последнего обращения, guard битых дат, кламп salience
public class PersonaMemoryScorerTests
{
    private static readonly DateTime Now = new(2026, 7, 11, 12, 0, 0, DateTimeKind.Utc);
    private static readonly MemoryScoringOptions Opts = MemoryScoringOptions.Default;

    private static PersonaMemoryEntry Entry(PersonaMemoryType type = PersonaMemoryType.Semantic,
        double salience = 1.0, double ageDaysCreated = 0, double ageDaysAccessed = 0) => new()
    {
        Type = type,
        Text = "запись",
        Salience = salience,
        CreatedAt = Now.AddDays(-ageDaysCreated),
        LastAccessedAt = Now.AddDays(-ageDaysAccessed),
    };

    [Fact]
    public void Сумма_СовпадаетСЭталоном()
    {
        // Свежая semantic-запись, salience 0.8, relevance 0.5:
        // 0.55·0.5 + 0.20·1 + 0.15·0.8 + 0.10·1.0 = 0.695
        var score = PersonaMemoryScorer.Score(Entry(salience: 0.8), 0.5, Now, Opts);

        score.Should().BeApproximately(0.695, 1e-9);
    }

    [Fact]
    public void ГейтMinRelevance_НерелевантнаяЗапись_Ноль()
    {
        // Даже максимально свежая и значимая запись не всплывает одной свежестью
        var score = PersonaMemoryScorer.Score(Entry(), relevance: 0.01, Now, Opts);

        score.Should().Be(0);
    }

    [Fact]
    public void Recency_ОтсчитываетсяОтПоследнегоОбращения()
    {
        // Обе созданы 60 дней назад, но к одной обращались только что —
        // reinforcement поднимает её recency до 1
        var touched = Entry(ageDaysCreated: 60, ageDaysAccessed: 0);
        var untouched = Entry(ageDaysCreated: 60, ageDaysAccessed: 60);

        var scoreTouched = PersonaMemoryScorer.Score(touched, 0.5, Now, Opts);
        var scoreUntouched = PersonaMemoryScorer.Score(untouched, 0.5, Now, Opts);

        // Разница = wRec·(1 − 2^(−60/30)) = 0.20·0.75
        (scoreTouched - scoreUntouched).Should().BeApproximately(0.20 * 0.75, 1e-9);
    }

    [Fact]
    public void GuardБитыхДат_LastAccessedРаньшеCreated_БерётсяCreated()
    {
        // Битая запись: якобы обращались до создания → якорь свежести = CreatedAt
        var broken = Entry(ageDaysCreated: 30, ageDaysAccessed: 60);
        var reference = Entry(ageDaysCreated: 30, ageDaysAccessed: 30);

        PersonaMemoryScorer.Score(broken, 0.5, Now, Opts)
            .Should().BeApproximately(PersonaMemoryScorer.Score(reference, 0.5, Now, Opts), 1e-9);
    }

    [Fact]
    public void TypeFactor_Порядок_SemanticВышеProceduralВышеEpisodic()
    {
        PersonaMemoryScorer.TypeFactor(PersonaMemoryType.Semantic)
            .Should().BeGreaterThan(PersonaMemoryScorer.TypeFactor(PersonaMemoryType.Procedural));
        PersonaMemoryScorer.TypeFactor(PersonaMemoryType.Procedural)
            .Should().BeGreaterThan(PersonaMemoryScorer.TypeFactor(PersonaMemoryType.Episodic));
    }

    [Fact]
    public void Salience_КлампитсяВНольОдин()
    {
        var inflated = Entry(salience: 5.0);
        var normal = Entry(salience: 1.0);
        var negative = Entry(salience: -1.0);
        var zero = Entry(salience: 0.0);

        PersonaMemoryScorer.Score(inflated, 0.5, Now, Opts)
            .Should().Be(PersonaMemoryScorer.Score(normal, 0.5, Now, Opts));
        PersonaMemoryScorer.Score(negative, 0.5, Now, Opts)
            .Should().Be(PersonaMemoryScorer.Score(zero, 0.5, Now, Opts));
    }

    [Fact]
    public void ДатаВБудущем_ВозрастНеОтрицательный()
    {
        // Часы уехали: обращение «в будущем» — recency не больше 1
        var future = Entry(ageDaysCreated: 0, ageDaysAccessed: -5);

        var score = PersonaMemoryScorer.Score(future, 0.5, Now, Opts);

        score.Should().BeApproximately(PersonaMemoryScorer.Score(Entry(), 0.5, Now, Opts), 1e-9);
    }
}
