using ClaudeHomeServer.Services.Llm;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace ClaudeHomeServer.Tests.Services;

// Поле Source снимка (turn/probe/oauth/null) и обратная совместимость со старым usage.json,
// записанным до появления этого поля.
public class UsageServiceTests : IDisposable
{
    private readonly string _tempDir;

    public UsageServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "usage_svc_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    private IConfiguration Config() => new ConfigurationBuilder().AddInMemoryCollection(
        new Dictionary<string, string?> { ["DataPath"] = Path.Combine(_tempDir, "projects.json") }).Build();

    [Theory]
    [InlineData("turn")]
    [InlineData("probe")]
    [InlineData("oauth")]
    [InlineData(null)]
    public void Record_ПроставляетSource(string? source)
    {
        var usage = new UsageService(Config());

        usage.Record("five_hour", 0.4, "allowed", isUsingOverage: false,
            resetsAt: DateTime.UtcNow.AddHours(2).ToString("o"), subscriptionKey: "claude", source: source);

        usage.GetAll().Should().ContainSingle().Which.Source.Should().Be(source);
    }

    [Fact]
    public void Load_СтарыйФайлБезПоляSource_ЧитаетсяSourceNull_НеПадает()
    {
        // Формат до появления Source (camelCase, как пишет сам UsageService).
        // Метка времени — свежая: Load отбрасывает снимки старше окна хранения (8 дней),
        // поэтому фиксированная дата в литерале была бы тайм-бомбой (тест зеленел до
        // определённого дня, а потом падал «коллекция пуста»).
        var ts = DateTime.UtcNow.AddHours(-1).ToString("o");
        var resets = DateTime.UtcNow.AddHours(4).ToString("o");
        var storePath = Path.Combine(_tempDir, "usage.json");
        File.WriteAllText(storePath, $$"""
        [
            {
                "timestamp": "{{ts}}",
                "limitType": "five_hour",
                "utilization": 0.42,
                "status": "allowed",
                "isUsingOverage": false,
                "resetsAt": "{{resets}}",
                "overageStatus": null,
                "overageResetsAt": null,
                "subscriptionKey": "claude"
            }
        ]
        """);

        var usage = new UsageService(Config());

        var snap = usage.GetAll().Should().ContainSingle().Subject;
        snap.Source.Should().BeNull();
        snap.LimitType.Should().Be("five_hour");
        snap.SubscriptionKey.Should().Be("claude");
    }

    // === GetLatestPerWindow: проекция «последний снимок на (подписку, окно)» для лёгкого /api/usage ===
    //
    // Запись идёт через Record (троттлинг 3 мин + UtcNow), поэтому в тестах меняю utilization
    // на >0.01, чтобы каждый снимок реально записался; «последним» считается записанным позже.

    private static void Rec(UsageService u, double? util, string resetsAt, string key = "claude")
        => u.Record("five_hour", util, "allowed", isUsingOverage: false, resetsAt, subscriptionKey: key);

    [Fact]
    public void GetLatestPerWindow_НесколькоСнимковОкна_ОтдаётТолькоСвежий()
    {
        var usage = new UsageService(Config());
        var future = DateTime.UtcNow.AddHours(2).ToString("o");
        Rec(usage, 0.1, future);
        Rec(usage, 0.2, future);
        Rec(usage, 0.3, future);

        var result = usage.GetLatestPerWindow();

        // GetAll держит все три, проекция — ровно один (самый свежий, 0.3)
        usage.GetAll().Should().HaveCount(3);
        result.Should().ContainKey("claude");
        result["claude"].Should().ContainSingle().Which.Utilization.Should().Be(0.3);
    }

    [Fact]
    public void GetLatestPerWindow_СвежийБезПроцента_ДополняетПроцентомТогоЖеОкна()
    {
        var usage = new UsageService(Config());
        var future = DateTime.UtcNow.AddHours(2).ToString("o");
        Rec(usage, 0.5, future);   // с процентом
        Rec(usage, null, future);   // свежее, без процента (живое событие «в пределах нормы»)

        var result = usage.GetLatestPerWindow();

        // И свежий (без процента), и последний с процентом того же окна — latestPerWindow на
        // фронте подставит процент, когда свежее событие его не несёт
        result["claude"].Should().HaveCount(2);
        result["claude"].Should().Contain(s => s.Utilization == null);
        result["claude"].Should().Contain(s => s.Utilization == 0.5);
    }

    [Fact]
    public void GetLatestPerWindow_СброшенноеОкно_ОтдаётПоследнееСброшенноеДляМаркераStale()
    {
        var usage = new UsageService(Config());
        var past = DateTime.UtcNow.AddHours(-1).ToString("o");    // окно уже сброшено
        var future = DateTime.UtcNow.AddHours(2).ToString("o");   // свежее окно
        Rec(usage, 0.5, past);
        Rec(usage, 0.6, future);

        var result = usage.GetLatestPerWindow();

        // Свежий (будущий сброс) + последний из УЖЕ сброшенных (маркер stale на фронте)
        result["claude"].Should().HaveCount(2);
        result["claude"].Should().Contain(s => s.ResetsAt == future);
        result["claude"].Should().Contain(s => s.ResetsAt == past);
    }

    [Fact]
    public void GetLatestPerWindow_РазныеПодписки_ГруппируютсяПоКлючу()
    {
        var usage = new UsageService(Config());
        var future = DateTime.UtcNow.AddHours(2).ToString("o");
        Rec(usage, 0.1, future, key: "claude");
        Rec(usage, 0.9, future, key: "claude-2");

        var result = usage.GetLatestPerWindow();

        result.Should().HaveCount(2);
        result["claude"].Should().ContainSingle().Which.Utilization.Should().Be(0.1);
        result["claude-2"].Should().ContainSingle().Which.Utilization.Should().Be(0.9);
    }

    [Fact]
    public void Version_РастетНаРеальнойЗаписи_АНеНаОтброшеннойТроттлингом()
    {
        var usage = new UsageService(Config());
        var future = DateTime.UtcNow.AddHours(2).ToString("o");
        var v0 = usage.Version;

        Rec(usage, 0.1, future);
        usage.Version.Should().Be(v0 + 1, "снимок записан — версия выросла");

        // Троттлинг: тот же utilization/статус <3 мин — дубль не пишется, версия не двигается
        var v1 = usage.Version;
        Rec(usage, 0.1, future);
        usage.Version.Should().Be(v1, "дубль в окне троттлинга не записан");
    }
}
