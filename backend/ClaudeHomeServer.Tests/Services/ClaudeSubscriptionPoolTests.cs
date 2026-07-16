using ClaudeHomeServer.Services;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace ClaudeHomeServer.Tests.Services;

public class ClaudeSubscriptionPoolTests : IDisposable
{
    private readonly string _tempDir;

    public ClaudeSubscriptionPoolTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "subpool_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    private IConfiguration Config(params string[] subKeys)
    {
        var dict = new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(_tempDir, "projects.json")
        };
        foreach (var key in subKeys)
            dict[$"{ClaudeSubscriptionPool.Section}:{key}:OAuthToken"] = "token-" + key;
        return new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
    }

    [Fact]
    public void Pick_БезДополнительныхПодписок_ВозвращаетОсновную()
    {
        var pool = new ClaudeSubscriptionPool(Config());
        pool.Pick().Should().Be(ClaudeSubscriptionPool.PrimaryKey);
    }

    [Fact]
    public void Pick_НеВозвращаетИсчерпанную()
    {
        var pool = new ClaudeSubscriptionPool(Config("second"));
        pool.MarkExhausted("second", DateTime.UtcNow.AddHours(2));

        for (var i = 0; i < 20; i++)
            pool.Pick().Should().Be(ClaudeSubscriptionPool.PrimaryKey);
    }

    [Fact]
    public void Pick_ОсновнаяИсчерпана_ВозвращаетДополнительную()
    {
        var pool = new ClaudeSubscriptionPool(Config("second"));
        pool.MarkExhausted(ClaudeSubscriptionPool.PrimaryKey, DateTime.UtcNow.AddHours(2));

        for (var i = 0; i < 20; i++)
            pool.Pick().Should().Be("second");
    }

    [Fact]
    public void Pick_ВсеИсчерпаны_ФолбэкНаОсновную()
    {
        var pool = new ClaudeSubscriptionPool(Config("second"));
        pool.MarkExhausted(ClaudeSubscriptionPool.PrimaryKey, DateTime.UtcNow.AddHours(2));
        pool.MarkExhausted("second", DateTime.UtcNow.AddHours(2));

        pool.Pick().Should().Be(ClaudeSubscriptionPool.PrimaryKey);
    }

    [Fact]
    public void IsExhausted_ПослеВремениСброса_ПодпискаСноваДоступна()
    {
        var pool = new ClaudeSubscriptionPool(Config("second"));
        pool.MarkExhausted("second", DateTime.UtcNow.AddMilliseconds(-1));

        pool.IsExhausted("second").Should().BeFalse();
    }

    [Fact]
    public void Restore_СнапшотRejectedСоСбросомВБудущем_ПомечаетИсчерпанной()
    {
        var config = Config("second");
        var usage = new UsageService(config);
        usage.Record("five_hour", 1.0, "rejected", isUsingOverage: false,
            resetsAt: DateTime.UtcNow.AddHours(2).ToString("o"), subscriptionKey: "second");

        var pool = new ClaudeSubscriptionPool(config, usage);

        pool.IsExhausted("second").Should().BeTrue();
        pool.IsExhausted(ClaudeSubscriptionPool.PrimaryKey).Should().BeFalse();
    }

    [Fact]
    public void Restore_СнапшотRejectedСоСбросомВПрошлом_НеПомечает()
    {
        var config = Config("second");
        var usage = new UsageService(config);
        usage.Record("five_hour", 1.0, "rejected", isUsingOverage: false,
            resetsAt: DateTime.UtcNow.AddMinutes(-5).ToString("o"), subscriptionKey: "second");

        var pool = new ClaudeSubscriptionPool(config, usage);

        pool.IsExhausted("second").Should().BeFalse();
    }

    [Fact]
    public void Restore_ПолноеОкноНоOverage_НеПомечает()
    {
        var config = Config("second");
        var usage = new UsageService(config);
        usage.Record("five_hour", 1.05, "allowed", isUsingOverage: true,
            resetsAt: DateTime.UtcNow.AddHours(2).ToString("o"), subscriptionKey: "second");

        var pool = new ClaudeSubscriptionPool(config, usage);

        pool.IsExhausted("second").Should().BeFalse();
    }

    [Fact]
    public void Restore_ПоследнийСнапшотОкнаAllowed_НеПомечает()
    {
        var config = Config("second");
        var usage = new UsageService(config);
        // rejected, затем allowed того же окна (лимит подняли/сбросили) — актуален последний
        usage.Record("five_hour", 1.0, "rejected", isUsingOverage: false,
            resetsAt: DateTime.UtcNow.AddHours(2).ToString("o"), subscriptionKey: "second");
        usage.Record("five_hour", 0.2, "allowed", isUsingOverage: false,
            resetsAt: DateTime.UtcNow.AddHours(2).ToString("o"), subscriptionKey: "second");

        var pool = new ClaudeSubscriptionPool(config, usage);

        pool.IsExhausted("second").Should().BeFalse();
    }
}
