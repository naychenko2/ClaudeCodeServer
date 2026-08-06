using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Llm;
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

    private TeamMemoryService NewService(ICheapTextRunner? cheap = null)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_dir, "projects.json"),
            }).Build();
        return new TeamMemoryService(config, cheap: cheap);
    }

    // Заглушка ICheapTextRunner: у сжатия авто-записи задействован только RunFreeAsync
    // (бесплатная цепочка — платить claude за сжатие фактов не нужно).
    private sealed class FakeCheapRunner : ICheapTextRunner
    {
        public Func<string, string, string?>? OnRunFree { get; init; }
        public bool UsesLocal(string actionKey) => true;
        public string DescribeRoute(string actionKey, string? fallbackModel) => "claude";
        public Task<string> RunAsync(string actionKey, string prompt, string? fallbackModel = null,
            string? ownerId = null, object? jsonFormat = null, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<string?> RunLocalOnlyAsync(string actionKey, string prompt, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<string?> RunFreeAsync(string actionKey, string prompt, object? jsonFormat = null,
            CancellationToken ct = default) => Task.FromResult(OnRunFree?.Invoke(actionKey, prompt));
        public Task<OneShotResult> RunDetailedAsync(string actionKey, string prompt, string? fallbackModel = null,
            string? ownerId = null, TimeSpan? timeout = null, int? maxTokens = null, object? jsonFormat = null,
            CancellationToken ct = default) => throw new NotSupportedException();
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
    public void BuildRecallBlock_ДлиннаяЗапись_ОбрезаетсяПоГраницеСловаСПодсказкой()
    {
        // Запись заведомо длиннее потолка recall'а — блок обязан её укоротить, а не нести целиком
        var longText = "деплойный " + string.Join(' ', Enumerable.Repeat("наблюдение", 60));
        _svc.Add("o1", "p1", longText);

        var block = _svc.BuildRecallBlock("o1", "p1", "деплойный").Text;

        block.Should().NotBeNull();
        var line = block!.Split('\n').First(l => l.StartsWith("- ")).TrimEnd();
        line.Length.Should().BeLessThan(TeamMemoryService.RecallTextLimit + 10);
        line.Should().EndWith("…");
        line.Should().NotContain("наблюде…");   // рубим по пробелу, а не посреди слова
        block.Should().Contain("team_memory_list");
    }

    [Fact]
    public void BuildRecallBlock_КороткаяЗапись_НеТрогаетсяИБезПодсказки()
    {
        _svc.Add("o1", "p1", "Прод на naychenko.me");

        var block = _svc.BuildRecallBlock("o1", "p1", "прод").Text;

        block.Should().Contain("- Прод на naychenko.me");
        block.Should().NotContain("…");
        block.Should().NotContain("team_memory_list");
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

    // --- Диета памяти команды (①): сжатие длинной авто-записи на записи ---

    private static string LongText(string word, int count) => string.Join(' ', Enumerable.Repeat(word, count));

    [Fact]
    public async Task AddAsync_ДлиннаяАвтоЗапись_СжимаетсяЧерезCheapRunner()
    {
        var cheap = new FakeCheapRunner { OnRunFree = (_, _) => "Сжатый факт про прод" };
        var svc = NewService(cheap);
        var longText = LongText("автозаписи-предложение-транскрипта", 40);
        longText.Length.Should().BeGreaterThan(700);

        var entry = await svc.AddAsync("o1", "p1", longText, source: TeamMemorySource.AutoTurn);

        entry.Text.Should().Be("Сжатый факт про прод");
    }

    [Fact]
    public async Task AddAsync_ДлиннаяАвтоЗапись_CheapRunnerНедоступен_ОбрезаетсяПоГраницеСлова()
    {
        var svc = NewService();   // cheap = null — сразу жёсткая обрезка
        var longText = LongText("слово", 200);

        var entry = await svc.AddAsync("o1", "p1", longText, source: TeamMemorySource.AutoTurn);

        entry.Text.Length.Should().BeLessThan(longText.Length);
        entry.Text.Should().EndWith("…");
        entry.Text.Should().NotContain("сло…");   // рубим по пробелу, а не посреди слова
    }

    [Fact]
    public async Task AddAsync_ДлиннаяАвтоЗапись_CheapRunnerВернулПусто_ОбрезаетсяПоГраницеСлова()
    {
        var cheap = new FakeCheapRunner { OnRunFree = (_, _) => null };
        var svc = NewService(cheap);
        var longText = LongText("слово", 200);

        var entry = await svc.AddAsync("o1", "p1", longText, source: TeamMemorySource.AutoTurn);

        entry.Text.Length.Should().BeLessThan(longText.Length);
        entry.Text.Should().EndWith("…");
    }

    [Fact]
    public async Task AddAsync_ДлиннаяРучнаяЗапись_НеСжимается()
    {
        var cheap = new FakeCheapRunner { OnRunFree = (_, _) => "не должно быть вызвано" };
        var svc = NewService(cheap);
        var longText = LongText("ручной-текст-записи", 60);
        longText.Length.Should().BeGreaterThan(700);

        var entry = await svc.AddAsync("o1", "p1", longText, source: TeamMemorySource.Manual);

        entry.Text.Should().Be(longText.Trim());
    }

    [Fact]
    public async Task AddAsync_КороткаяАвтоЗапись_НеТрогается()
    {
        var cheap = new FakeCheapRunner { OnRunFree = (_, _) => "не должно быть вызвано" };
        var svc = NewService(cheap);

        var entry = await svc.AddAsync("o1", "p1", "Короткий факт", source: TeamMemorySource.AutoTurn);

        entry.Text.Should().Be("Короткий факт");
    }
}
