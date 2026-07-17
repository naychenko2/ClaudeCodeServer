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

    private IConfiguration Config(params string[] subKeys) => Config(null, subKeys);

    private IConfiguration Config(double? softThreshold, params string[] subKeys)
    {
        var dict = new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(_tempDir, "projects.json")
        };
        if (softThreshold is not null)
            dict[$"{ClaudeSubscriptionPool.Section}:SoftThreshold"] =
                softThreshold.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        foreach (var key in subKeys)
            dict[$"{ClaudeSubscriptionPool.Section}:{key}:OAuthToken"] = "token-" + key;
        return new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
    }

    // Свежий снимок утилизации 5h-окна для подписки (ResetsAt по умолчанию в будущем).
    private static void RecordUtil(UsageService usage, string subKey, double util, string? resetsAt = null) =>
        usage.Record("five_hour", util, "allowed", isUsingOverage: false,
            resetsAt: resetsAt ?? DateTime.UtcNow.AddHours(2).ToString("o"), subscriptionKey: subKey);

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

    [Fact]
    public void Pick_ВыбираетНаименееЗагруженную()
    {
        var config = Config("second");
        var usage = new UsageService(config);
        RecordUtil(usage, ClaudeSubscriptionPool.PrimaryKey, 0.6);
        RecordUtil(usage, "second", 0.1);

        var pool = new ClaudeSubscriptionPool(config, usage);

        for (var i = 0; i < 20; i++)
            pool.Pick().Should().Be("second");
    }

    [Fact]
    public void Pick_НетДанныхУДополнительной_ВыбираетЕё_КакСвободную()
    {
        // second без снимков считается 0% (свежий аккаунт) → он менее загружен, чем основная.
        var config = Config("second");
        var usage = new UsageService(config);
        RecordUtil(usage, ClaudeSubscriptionPool.PrimaryKey, 0.5);

        var pool = new ClaudeSubscriptionPool(config, usage);

        for (var i = 0; i < 20; i++)
            pool.Pick().Should().Be("second");
    }

    [Fact]
    public void Pick_ОкноСброшено_СчитаетсяНоль()
    {
        // У основной высокая утилизация, но её ResetsAt в прошлом → окно сброшено → 0%.
        var config = Config("second");
        var usage = new UsageService(config);
        RecordUtil(usage, ClaudeSubscriptionPool.PrimaryKey, 0.95,
            resetsAt: DateTime.UtcNow.AddMinutes(-5).ToString("o"));
        RecordUtil(usage, "second", 0.4);

        var pool = new ClaudeSubscriptionPool(config, usage);

        for (var i = 0; i < 20; i++)
            pool.Pick().Should().Be(ClaudeSubscriptionPool.PrimaryKey);
    }

    [Fact]
    public void Pick_ВсеВышеПорога_ВыбираетНаименееЗагруженную()
    {
        var config = Config("second");
        var usage = new UsageService(config);
        RecordUtil(usage, ClaudeSubscriptionPool.PrimaryKey, 0.95);
        RecordUtil(usage, "second", 0.85);

        var pool = new ClaudeSubscriptionPool(config, usage);

        // Обе выше порога 0.8 — всё равно берём наименее загруженную.
        for (var i = 0; i < 20; i++)
            pool.Pick().Should().Be("second");
    }

    [Fact]
    public void Pick_ВсеИсчерпаны_БерётНаименееЗагруженнуюИзВсех()
    {
        var config = Config("second");
        var usage = new UsageService(config);
        RecordUtil(usage, ClaudeSubscriptionPool.PrimaryKey, 0.9);
        RecordUtil(usage, "second", 0.3);

        var pool = new ClaudeSubscriptionPool(config, usage);
        pool.MarkExhausted(ClaudeSubscriptionPool.PrimaryKey, DateTime.UtcNow.AddHours(2));
        pool.MarkExhausted("second", DateTime.UtcNow.AddHours(2));

        // Все помечены исчерпанными — fallback на наименее загруженную (second), не на основную.
        for (var i = 0; i < 20; i++)
            pool.Pick().Should().Be("second");
    }

    [Fact]
    public void IsInRotation_ПоПорогу()
    {
        var config = Config(softThreshold: 0.8, "second");
        var usage = new UsageService(config);
        RecordUtil(usage, ClaudeSubscriptionPool.PrimaryKey, 0.9);
        RecordUtil(usage, "second", 0.5);

        var pool = new ClaudeSubscriptionPool(config, usage);

        pool.IsInRotation(ClaudeSubscriptionPool.PrimaryKey).Should().BeFalse();
        pool.IsInRotation("second").Should().BeTrue();
    }

    [Fact]
    public void IsInRotation_Исчерпанная_НеВРотации_ДажеБезUtilization()
    {
        // Как на проде: rejected без числа utilization → EffectiveUtilization=0, но аккаунт
        // исчерпан → должен быть выведен из ротации (иначе бейдж соврёт «в ротации»).
        var config = Config(softThreshold: 0.8, "second");
        var usage = new UsageService(config);
        usage.Record("five_hour", null, "rejected", isUsingOverage: false,
            resetsAt: DateTime.UtcNow.AddHours(2).ToString("o"), subscriptionKey: "second");

        var pool = new ClaudeSubscriptionPool(config, usage);

        pool.IsExhausted("second").Should().BeTrue();
        pool.EffectiveUtilization("second").Should().Be(0);
        pool.IsInRotation("second").Should().BeFalse();
    }

    [Fact]
    public void SoftThreshold_ЧитаетсяИзКонфига()
    {
        var pool = new ClaudeSubscriptionPool(Config(softThreshold: 0.7));
        pool.SoftThreshold.Should().Be(0.7);
    }

    [Fact]
    public void SoftThreshold_ДефолтБезКонфига()
    {
        var pool = new ClaudeSubscriptionPool(Config());
        pool.SoftThreshold.Should().Be(0.8);
    }
}
