using ClaudeHomeServer.Services;
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
        // Timestamp — свежий (в пределах окна retention 8 дней), чтобы снимок не
        // отфильтровывался на чтении; иначе тест хрупко зависел бы от календаря.
        var ts = DateTime.UtcNow.AddDays(-1);
        var storePath = Path.Combine(_tempDir, "usage.json");
        File.WriteAllText(storePath, $$"""
        [
            {
                "timestamp": "{{ts.ToString("o")}}",
                "limitType": "five_hour",
                "utilization": 0.42,
                "status": "allowed",
                "isUsingOverage": false,
                "resetsAt": "{{ts.AddHours(5).ToString("o")}}",
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
}
