using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace ClaudeHomeServer.Tests.Services;

// Стор командной памяти: дефолты, кламп важности, дедуп-on-write, обратная совместимость старого стора
public class TeamMemoryServiceTests : IDisposable
{
    private readonly string _dir;
    private readonly TeamMemoryService _svc;

    public TeamMemoryServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "team-mem-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _svc = NewService();
    }

    private TeamMemoryService NewService()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_dir, "projects.json"),
            }).Build();
        return new TeamMemoryService(config);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* тест-мусор */ }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Add_Дефолты_FactManual_ИTrim()
    {
        var e = _svc.Add("o1", "p1", "  Прод на naychenko.me  ");

        e.Text.Should().Be("Прод на naychenko.me");
        e.Type.Should().Be(TeamMemoryType.Fact);
        e.Source.Should().Be(TeamMemorySource.Manual);
        e.Salience.Should().Be(1.0);
        e.SourceSessionId.Should().BeNull();
    }

    [Fact]
    public void Add_ТипИсточникСессияSalience_Проставляются()
    {
        var e = _svc.Add("o1", "p1", "Выбрали PG",
            TeamMemoryType.Decision, TeamMemorySource.AutoTurn, "s1", 0.8);

        e.Type.Should().Be(TeamMemoryType.Decision);
        e.Source.Should().Be(TeamMemorySource.AutoTurn);
        e.SourceSessionId.Should().Be("s1");
        e.Salience.Should().Be(0.8);
    }

    [Fact]
    public void Add_Salience_КлампВДиапазон()
    {
        _svc.Add("o1", "p1", "a", salience: 9).Salience.Should().Be(1.0);
        _svc.Add("o1", "p1", "b", salience: 0.0001).Salience.Should().Be(0.05);
    }

    [Fact]
    public void Add_Дедуп_ТотЖеТекстИТип_УсиливаетНеПлодит()
    {
        var first = _svc.Add("o1", "p1", "Прод на naychenko.me", TeamMemoryType.Fact, salience: 0.5);
        var second = _svc.Add("o1", "p1", "прод на naychenko.me", TeamMemoryType.Fact, salience: 0.5); // ci

        second.Id.Should().Be(first.Id);                       // та же запись, не дубль
        _svc.List("o1", "p1").Should().ContainSingle();
        second.Salience.Should().BeApproximately(0.6, 1e-9);   // 0.5 + boost 0.1
    }

    [Fact]
    public void Add_РазныйТип_НеСчитаетсяДублем()
    {
        _svc.Add("o1", "p1", "X", TeamMemoryType.Fact);
        _svc.Add("o1", "p1", "X", TeamMemoryType.Decision);

        _svc.List("o1", "p1").Should().HaveCount(2);
    }

    [Fact]
    public void Add_ИзоляцияПоOwnerИProject()
    {
        _svc.Add("o1", "p1", "A");
        _svc.Add("o1", "p2", "B");
        _svc.Add("o2", "p1", "C");

        _svc.List("o1", "p1").Should().ContainSingle();
        _svc.List("o1", "p2").Should().ContainSingle();
        _svc.List("o2", "p1").Should().ContainSingle();
    }

    [Fact]
    public void Load_СтарыйСторБезНовыхПолей_ПолучаетДефолты()
    {
        // Старый формат: запись только с Id/OwnerId/ProjectId/Text/CreatedAt (PascalCase, как писал прежний код)
        var legacy = """
            {"o1:p1":[{"Id":"e1","OwnerId":"o1","ProjectId":"p1","Text":"Старый факт","CreatedAt":"2025-01-01T00:00:00Z"}]}
            """;
        File.WriteAllText(Path.Combine(_dir, "team-memory.json"), legacy);

        var svc = NewService();
        var e = svc.List("o1", "p1").Should().ContainSingle().Subject;

        e.Text.Should().Be("Старый факт");
        e.Type.Should().Be(TeamMemoryType.Fact);        // отсутствует → enum default 0
        e.Source.Should().Be(TeamMemorySource.Manual);  // отсутствует → enum default 0
        e.Salience.Should().Be(1.0);                    // отсутствует → инициализатор
    }

    [Fact]
    public void Add_Персистится_МеждуЭкземплярами()
    {
        _svc.Add("o1", "p1", "Запомни", TeamMemoryType.Decision, TeamMemorySource.AutoMeeting, "s9", 0.7);

        var reopened = NewService();
        var e = reopened.List("o1", "p1").Should().ContainSingle().Subject;
        e.Text.Should().Be("Запомни");
        e.Type.Should().Be(TeamMemoryType.Decision);
        e.Source.Should().Be(TeamMemorySource.AutoMeeting);
        e.Salience.Should().Be(0.7);
    }
}
