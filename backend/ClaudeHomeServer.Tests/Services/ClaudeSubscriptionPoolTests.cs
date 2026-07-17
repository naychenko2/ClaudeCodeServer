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

    // --- Доступность модели (пин Opus у персоны не должен попасть на план без Opus) ---

    private IConfiguration ConfigWithProPlan(string proKey, params string[] fullKeys)
    {
        var dict = new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(_tempDir, "projects.json"),
            [$"{ClaudeSubscriptionPool.Section}:{proKey}:OAuthToken"] = "token-" + proKey,
            [$"{ClaudeSubscriptionPool.Section}:{proKey}:SupportsOpus"] = "false",
        };
        foreach (var key in fullKeys)
            dict[$"{ClaudeSubscriptionPool.Section}:{key}:OAuthToken"] = "token-" + key;
        return new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
    }

    [Fact]
    public void Pick_ПинOpus_НеПопадаетНаПланБезOpus_ДажеСвободный()
    {
        var config = ConfigWithProPlan("pro");
        var usage = new UsageService(config);
        RecordUtil(usage, ClaudeSubscriptionPool.PrimaryKey, 0.7);
        RecordUtil(usage, "pro", 0.0); // pro свободнее, но Opus не умеет

        var pool = new ClaudeSubscriptionPool(config, usage);

        for (var i = 0; i < 20; i++)
            pool.Pick("opus").Should().Be(ClaudeSubscriptionPool.PrimaryKey);
    }

    [Fact]
    public void Pick_ПолныйIdOpus_ТожеФильтрует()
    {
        var pool = new ClaudeSubscriptionPool(ConfigWithProPlan("pro"));
        for (var i = 0; i < 20; i++)
            pool.Pick("claude-opus-4-8[1m]").Should().Be(ClaudeSubscriptionPool.PrimaryKey);
    }

    [Fact]
    public void Pick_БезПинаМодели_ПланБезOpus_УчаствуетВРотации()
    {
        var config = ConfigWithProPlan("pro");
        var usage = new UsageService(config);
        RecordUtil(usage, ClaudeSubscriptionPool.PrimaryKey, 0.7);
        RecordUtil(usage, "pro", 0.0);

        var pool = new ClaudeSubscriptionPool(config, usage);

        for (var i = 0; i < 20; i++)
            pool.Pick().Should().Be("pro");
        for (var i = 0; i < 20; i++)
            pool.Pick("sonnet").Should().Be("pro");
    }

    [Fact]
    public void Pick_ПинOpus_СпособныеИсчерпаны_ВсёРавноВыбираетСпособную()
    {
        // Лучше упереться в лимит на правильном аккаунте, чем гарантированно упасть на Pro
        var config = ConfigWithProPlan("pro", "full2");
        var pool = new ClaudeSubscriptionPool(config, new UsageService(config));
        pool.MarkExhausted(ClaudeSubscriptionPool.PrimaryKey, DateTime.UtcNow.AddHours(2));
        pool.MarkExhausted("full2", DateTime.UtcNow.AddHours(2));

        for (var i = 0; i < 20; i++)
            pool.Pick("opus").Should().BeOneOf(ClaudeSubscriptionPool.PrimaryKey, "full2");
    }

    [Fact]
    public void SupportsModel_НеClaudeКлюч_ВсегдаTrue()
    {
        var pool = new ClaudeSubscriptionPool(ConfigWithProPlan("pro"));
        pool.SupportsModel("deepseek", "deepseek-v4-pro").Should().BeTrue();
        pool.SupportsModel("pro", "opus").Should().BeFalse();
        pool.SupportsModel("pro", "sonnet").Should().BeTrue();
        pool.SupportsModel(ClaudeSubscriptionPool.PrimaryKey, "opus").Should().BeTrue();
    }
}
