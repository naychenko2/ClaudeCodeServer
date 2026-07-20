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
    public void ПустойПул_ЛокальныйClaude()
    {
        // Инвариант: ни одной подписки с токеном в конфиге → пул пуст, работаем по
        // локальному входу (~/.claude/.credentials.json), Pick возвращает PrimaryKey.
        var pool = new ClaudeSubscriptionPool(Config());
        pool.HasExtra.Should().BeFalse();
        pool.All.Should().BeEmpty();
        pool.Pick().Should().Be(ClaudeSubscriptionPool.PrimaryKey);
    }

    [Fact]
    public void Claude_СТокеном_РавноправныйУчастникПула()
    {
        // Инвариант новой модели: запись "claude" с токеном — обычная подписка пула
        // наравне с остальными (входит в All, несёт свой тариф, может быть выбрана Pick).
        var config = ConfigWithTiers("max20", ("small", "pro"));
        var pool = new ClaudeSubscriptionPool(config, new UsageService(config));

        pool.All.Select(s => s.Key).Should().Contain(ClaudeSubscriptionPool.PrimaryKey);
        pool.TierLabel(ClaudeSubscriptionPool.PrimaryKey).Should().Be("Max 20×");
        // claude (Max 20×) приоритетнее small (Pro) → Pick её и возвращает
        for (var i = 0; i < 20; i++)
            pool.Pick().Should().Be(ClaudeSubscriptionPool.PrimaryKey);
    }

    [Fact]
    public void Claude_БезТокена_НеВходитВПул()
    {
        // Запись только с DisplayName/Tier (без OAuthToken/ApiKey) → Enabled=false → не в пуле,
        // хотя настроена другая подписка (пул при этом не пуст).
        var dict = new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(_tempDir, "projects.json"),
            [$"{ClaudeSubscriptionPool.Section}:{ClaudeSubscriptionPool.PrimaryKey}:DisplayName"] = "Основная",
            [$"{ClaudeSubscriptionPool.Section}:{ClaudeSubscriptionPool.PrimaryKey}:Tier"] = "max20",
            [$"{ClaudeSubscriptionPool.Section}:second:OAuthToken"] = "token-second",
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
        var pool = new ClaudeSubscriptionPool(config);

        pool.All.Select(s => s.Key).Should().NotContain(ClaudeSubscriptionPool.PrimaryKey);
        pool.All.Select(s => s.Key).Should().ContainSingle().Which.Should().Be("second");
    }

    [Fact]
    public void Pick_НеВозвращаетИсчерпанную()
    {
        // Пул из двух подписок: исчерпанная выпадает из ротации, берётся вторая
        // (при непустом пуле локальный вход не используется).
        var pool = new ClaudeSubscriptionPool(Config("second", "third"));
        pool.MarkExhausted("second", DateTime.UtcNow.AddHours(2));

        for (var i = 0; i < 20; i++)
            pool.Pick().Should().Be("third");
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
        // У "reset" высокая утилизация, но её ResetsAt в прошлом → окно сброшено → 0%,
        // поэтому она менее загружена, чем "other" (0.4), и выбирается.
        var config = Config("reset", "other");
        var usage = new UsageService(config);
        RecordUtil(usage, "reset", 0.95,
            resetsAt: DateTime.UtcNow.AddMinutes(-5).ToString("o"));
        RecordUtil(usage, "other", 0.4);

        var pool = new ClaudeSubscriptionPool(config, usage);

        for (var i = 0; i < 20; i++)
            pool.Pick().Should().Be("reset");
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
        // В пуле "pro" (без Opus, но свободнее) и "full" (умеет Opus) — Opus-пин идёт на full.
        var config = ConfigWithProPlan("pro", "full");
        var usage = new UsageService(config);
        RecordUtil(usage, "full", 0.7);
        RecordUtil(usage, "pro", 0.0); // pro свободнее, но Opus не умеет

        var pool = new ClaudeSubscriptionPool(config, usage);

        for (var i = 0; i < 20; i++)
            pool.Pick("opus").Should().Be("full");
    }

    [Fact]
    public void Pick_ПолныйIdOpus_ТожеФильтрует()
    {
        var pool = new ClaudeSubscriptionPool(ConfigWithProPlan("pro", "full"));
        for (var i = 0; i < 20; i++)
            pool.Pick("claude-opus-4-8[1m]").Should().Be("full");
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

    // --- Приоритизация по тарифу (высший тариф среди доступных выигрывает) ---

    // Конфиг с тарифами: словарь key → tier ("" = не задавать). primaryTier задаёт тариф
    // подписке "claude" — в новой модели это обычный участник пула, поэтому ей выдаётся токен
    // (запись без токена в пул не входит).
    private IConfiguration ConfigWithTiers(string? primaryTier, params (string key, string tier)[] subs)
    {
        var dict = new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(_tempDir, "projects.json"),
        };
        if (!string.IsNullOrEmpty(primaryTier))
        {
            dict[$"{ClaudeSubscriptionPool.Section}:{ClaudeSubscriptionPool.PrimaryKey}:OAuthToken"] = "token-claude";
            dict[$"{ClaudeSubscriptionPool.Section}:{ClaudeSubscriptionPool.PrimaryKey}:Tier"] = primaryTier;
        }
        foreach (var (key, tier) in subs)
        {
            dict[$"{ClaudeSubscriptionPool.Section}:{key}:OAuthToken"] = "token-" + key;
            if (!string.IsNullOrEmpty(tier))
                dict[$"{ClaudeSubscriptionPool.Section}:{key}:Tier"] = tier;
        }
        return new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
    }

    [Fact]
    public void Pick_ВышеТариф_ВыигрываетДажеПриБольшейЗагрузке()
    {
        // big (Max 20×) загружен 0.6, small (Pro) — 0.1; оба ниже порога → берём высший тариф.
        var config = ConfigWithTiers(null, ("big", "max20"), ("small", "pro"));
        var usage = new UsageService(config);
        RecordUtil(usage, "big", 0.6);
        RecordUtil(usage, "small", 0.1);

        var pool = new ClaudeSubscriptionPool(config, usage);

        for (var i = 0; i < 20; i++)
            pool.Pick().Should().Be("big");
    }

    [Fact]
    public void Pick_КрупныйВышеПорога_СпиллНаСвободныйМелкий()
    {
        // big (Max 20×) перегружен 0.9 (выше порога) → доступен только small (Pro, 0.2).
        var config = ConfigWithTiers(null, ("big", "max20"), ("small", "pro"));
        var usage = new UsageService(config);
        RecordUtil(usage, "big", 0.9);
        RecordUtil(usage, "small", 0.2);

        var pool = new ClaudeSubscriptionPool(config, usage);

        for (var i = 0; i < 20; i++)
            pool.Pick().Should().Be("small");
    }

    [Fact]
    public void Pick_КрупныйИсчерпан_СпиллНаМелкий()
    {
        var config = ConfigWithTiers(null, ("big", "max20"), ("small", "pro"));
        var pool = new ClaudeSubscriptionPool(config, new UsageService(config));
        pool.MarkExhausted("big", DateTime.UtcNow.AddHours(2));

        for (var i = 0; i < 20; i++)
            pool.Pick().Should().Be("small");
    }

    [Fact]
    public void Pick_ВсеВышеПорога_БерётВысшийТариф()
    {
        // Все выше порога 0.8 (в т.ч. основная) → нет «свободных», но приоритет высшему тарифу.
        var config = ConfigWithTiers(null, ("big", "max20"), ("small", "pro"));
        var usage = new UsageService(config);
        RecordUtil(usage, ClaudeSubscriptionPool.PrimaryKey, 0.9);
        RecordUtil(usage, "big", 0.95);
        RecordUtil(usage, "small", 0.85);

        var pool = new ClaudeSubscriptionPool(config, usage);

        for (var i = 0; i < 20; i++)
            pool.Pick().Should().Be("big");
    }

    [Fact]
    public void Pick_РавныйТариф_ВыбираетНаименееЗагруженную()
    {
        var config = ConfigWithTiers(null, ("a", "max5"), ("b", "max5"));
        var usage = new UsageService(config);
        RecordUtil(usage, "a", 0.5);
        RecordUtil(usage, "b", 0.1);

        var pool = new ClaudeSubscriptionPool(config, usage);

        for (var i = 0; i < 20; i++)
            pool.Pick().Should().Be("b");
    }

    [Fact]
    public void Pick_ТарифОсновнойИзКонфига_ПриоритетнееМелкойДополнительной()
    {
        // Основная — Max 20× (из конфига), дополнительная — Pro; обе свободны → основная.
        var config = ConfigWithTiers("max20", ("small", "pro"));
        var pool = new ClaudeSubscriptionPool(config, new UsageService(config));

        for (var i = 0; i < 20; i++)
            pool.Pick().Should().Be(ClaudeSubscriptionPool.PrimaryKey);
    }

    [Fact]
    public void TierLabel_ЧитаетсяИзКонфигаИНормализуется()
    {
        var config = ConfigWithTiers("Max 20x", ("small", "pro"));
        var pool = new ClaudeSubscriptionPool(config, new UsageService(config));

        pool.TierLabel(ClaudeSubscriptionPool.PrimaryKey).Should().Be("Max 20×");
        pool.TierLabel("small").Should().Be("Pro");
    }

    [Theory]
    [InlineData("max20", 4)]
    [InlineData("Max 20x", 4)]
    [InlineData("max_20x", 4)]
    [InlineData("max5", 3)]
    [InlineData("Max 5x", 3)]
    [InlineData("max", 2)]
    [InlineData("pro", 1)]
    [InlineData("", 0)]
    [InlineData(null, 0)]
    [InlineData("нечто", 0)]
    public void TierRank_Нормализация(string? tier, int expected)
    {
        ClaudeHomeServer.Models.ClaudeSubscriptionTier.Rank(tier).Should().Be(expected);
    }
}
