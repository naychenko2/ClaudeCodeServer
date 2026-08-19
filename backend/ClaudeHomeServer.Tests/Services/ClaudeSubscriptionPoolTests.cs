using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Llm;
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
    public void Restore_RejectedНеизвестногоОкна_НеПомечает()
    {
        // Инцидент 2026-08-02: CLI прислал по ЖИВОМУ аккаунту одиночное rejected по окну
        // seven_day_overage_included со сбросом через пять суток. Такой снимок не должен
        // выводить подписку из ротации — ни на ходу, ни при восстановлении после рестарта.
        var config = Config("second");
        var usage = new UsageService(config);
        usage.Record("seven_day_overage_included", null, "rejected", isUsingOverage: false,
            resetsAt: DateTime.UtcNow.AddDays(5).ToString("o"), subscriptionKey: "second", source: "turn");

        var pool = new ClaudeSubscriptionPool(config, usage);

        pool.IsExhausted("second").Should().BeFalse();
    }

    [Fact]
    public void Restore_RejectedНедельногоОкна_Помечает()
    {
        // Обратная сторона белого списка: базовые окна подписки маркируют как и раньше.
        var config = Config("second");
        var usage = new UsageService(config);
        usage.Record("seven_day", null, "rejected", isUsingOverage: false,
            resetsAt: DateTime.UtcNow.AddDays(2).ToString("o"), subscriptionKey: "second", source: "turn");

        var pool = new ClaudeSubscriptionPool(config, usage);

        pool.IsExhausted("second").Should().BeTrue();
    }

    [Theory]
    [InlineData("five_hour", true)]
    [InlineData("seven_day", true)]
    [InlineData("seven_day_overage_included", false)]
    [InlineData("seven_day_opus", false)]
    [InlineData("extra_usage", false)]
    [InlineData(null, false)]
    public void IsExhaustionWindow_БелыйСписокОкон(string? limitType, bool expected)
    {
        ClaudeSubscriptionPool.IsExhaustionWindow(limitType).Should().Be(expected);
    }

    [Fact]
    public void Restore_БолееПоздныйOAuthСнимок_НеМаскируетРанееЗафиксированноеИсчерпание()
    {
        // Дыра ротации: SubscriptionOAuthUsageService.RecordWindow пишет status="allowed"
        // ВСЕГДА (эндпоинт отдаёт только проценты, не вердикт accept/reject). Если такой
        // снимок хронологически последний в группе LimitType, наивная RestoreFromSnapshots
        // маскировала бы реальное исчерпание недельного окна (source="turn"/"probe",
        // status="rejected") после рестарта сервера — Pick() снова выбрал бы мёртвый аккаунт
        // с виду свежим (по 5h-окну) состоянием.
        var config = Config("second");
        var usage = new UsageService(config);
        usage.Record("seven_day", 1.0, "rejected", isUsingOverage: false,
            resetsAt: DateTime.UtcNow.AddDays(3).ToString("o"), subscriptionKey: "second", source: "turn");
        usage.Record("seven_day", 0.97, "allowed", isUsingOverage: false,
            resetsAt: DateTime.UtcNow.AddDays(3).ToString("o"), subscriptionKey: "second", source: "oauth");

        var pool = new ClaudeSubscriptionPool(config, usage);

        pool.IsExhausted("second").Should().BeTrue();
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
    public void Pick_ВсеИсчерпаны_БерётСБлижайшимСбросом()
    {
        // Инцидент 2026-08-02: оба аккаунта помечены исчерпанными, utilization 5h-окна у обоих
        // нулевая (при rejected CLI её не присылает) → tie-break по загрузке был случайным и
        // гнал новые чаты на аккаунт, лежащий до конца недели. Берём того, кто оживёт раньше.
        var config = Config(ClaudeSubscriptionPool.PrimaryKey, "claude-2");
        var pool = new ClaudeSubscriptionPool(config, new UsageService(config));
        pool.MarkExhausted(ClaudeSubscriptionPool.PrimaryKey, DateTime.UtcNow.AddDays(2));
        pool.MarkExhausted("claude-2", DateTime.UtcNow.AddHours(3));

        for (var i = 0; i < 20; i++)
            pool.Pick().Should().Be("claude-2");
        pool.PickForDisplay().Should().Be("claude-2");
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

    // Фильтр по Opus-тиру идёт по подстроке "opus", поэтому не зависит от схемы версии
    // в id (claude-opus-4-8 против claude-opus-5) — проверяем обе.
    [Theory]
    [InlineData("claude-opus-4-8[1m]")]
    [InlineData("claude-opus-5")]
    public void Pick_ПолныйIdOpus_ТожеФильтрует(string model)
    {
        var pool = new ClaudeSubscriptionPool(ConfigWithProPlan("pro", "full"));
        for (var i = 0; i < 20; i++)
            pool.Pick(model).Should().Be("full");
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

    // --- Доступность 1M-окна (opus[1m] не должен упасть на аккаунте без 1M-доступа) ---

    // Подписка limitedKey — план без 1M-окна (Supports1M=false); fullKeys — тянут 1M (default).
    private IConfiguration ConfigWith200KPlan(string limitedKey, params string[] fullKeys)
    {
        var dict = new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(_tempDir, "projects.json"),
            [$"{ClaudeSubscriptionPool.Section}:{limitedKey}:OAuthToken"] = "token-" + limitedKey,
            [$"{ClaudeSubscriptionPool.Section}:{limitedKey}:Supports1M"] = "false",
        };
        foreach (var key in fullKeys)
            dict[$"{ClaudeSubscriptionPool.Section}:{key}:OAuthToken"] = "token-" + key;
        return new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
    }

    [Fact]
    public void SupportsModel_Supports1MFalse_ОтсекаетТолькоОкно()
    {
        var pool = new ClaudeSubscriptionPool(ConfigWith200KPlan("lim"));
        // Тир-алиас с окном — отсекается; без окна — обслуживается
        pool.SupportsModel("lim", "opus[1m]").Should().BeFalse();
        pool.SupportsModel("lim", "sonnet[1m]").Should().BeFalse();
        pool.SupportsModel("lim", "opus").Should().BeTrue();
        pool.SupportsModel("lim", "haiku").Should().BeTrue();
        // Ключ вне пула (сторонний провайдер) — не наша забота
        pool.SupportsModel("glm", "glm-5.2[1m]").Should().BeTrue();
    }

    [Fact]
    public void ResolveWindowAlias_ПустойПул_ОставляетСуффикс()
    {
        // Локальный Claude (default Supports1M=true) — суффикс доезжает до --model
        var pool = new ClaudeSubscriptionPool(Config());
        pool.ResolveWindowAlias("opus[1m]").Should().Be("opus[1m]");
    }

    [Fact]
    public void ResolveWindowAlias_ВсеПодпискиТянут1M_ОставляетСуффикс()
    {
        var pool = new ClaudeSubscriptionPool(Config("a", "b"));
        pool.ResolveWindowAlias("opus[1m]").Should().Be("opus[1m]");
        pool.ResolveWindowAlias("sonnet[1m]").Should().Be("sonnet[1m]");
    }

    [Fact]
    public void ResolveWindowAlias_НиктоНеТянет1M_СрезаетВ200K()
    {
        // Обе подписки без 1M → деградация в 200K (срез суффикса), а не падение хода
        var dict = new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(_tempDir, "projects.json"),
            [$"{ClaudeSubscriptionPool.Section}:a:OAuthToken"] = "token-a",
            [$"{ClaudeSubscriptionPool.Section}:a:Supports1M"] = "false",
            [$"{ClaudeSubscriptionPool.Section}:b:OAuthToken"] = "token-b",
            [$"{ClaudeSubscriptionPool.Section}:b:Supports1M"] = "false",
        };
        var pool = new ClaudeSubscriptionPool(new ConfigurationBuilder().AddInMemoryCollection(dict).Build());

        pool.ResolveWindowAlias("opus[1m]").Should().Be("opus");
        pool.ResolveWindowAlias("sonnet[1m]").Should().Be("sonnet");
    }

    [Fact]
    public void ResolveWindowAlias_СмешанныйПул_ОставляетПокаЕстьЖивой1M()
    {
        // Есть живая 1M-подписка "full" — суффикс сохраняется
        var pool = new ClaudeSubscriptionPool(ConfigWith200KPlan("lim", "full"));
        pool.ResolveWindowAlias("opus[1m]").Should().Be("opus[1m]");
    }

    [Fact]
    public void ResolveWindowAlias_1MИсчерпаны_ДеградируетВ200K()
    {
        // Единственная 1M-подписка исчерпана, осталась живая 200K — живого 1M-кандидата нет,
        // суффикс срезается (ход в 200K), а не падает на ждущей 200K с opus[1m]
        var pool = new ClaudeSubscriptionPool(ConfigWith200KPlan("lim", "full"));
        pool.MarkExhausted("full", DateTime.UtcNow.AddHours(2));

        pool.ResolveWindowAlias("opus[1m]").Should().Be("opus");
    }

    // Окно, объявляемое CLI, считается ПОСЛЕ резолва по способности пула: срезанный алиас
    // обязан дать 200k. Объявить 1M там, где его нет, — хуже, чем не объявлять вовсе:
    // CLI не сожмёт контекст вовремя и ход упадёт «Prompt is too long» вместо компакта.
    [Fact]
    public void ОкноКонтекста_СчитаетсяПоМоделиПослеРезолваПула()
    {
        var live1M = new ClaudeSubscriptionPool(ConfigWith200KPlan("lim", "full"));
        LlmProviderRegistry.ClaudeContextWindow(live1M.ResolveWindowAlias("opus[1m]"))
            .Should().Be(1_000_000);

        live1M.MarkExhausted("full", DateTime.UtcNow.AddHours(2));
        LlmProviderRegistry.ClaudeContextWindow(live1M.ResolveWindowAlias("opus[1m]"))
            .Should().Be(200_000);
    }

    [Theory]
    // Не-тир-алиасы — суффикс не режем: полные id и сторонние провайдеры разбирает сам CLI
    [InlineData("glm-5.2[1m]", "glm-5.2[1m]")]
    [InlineData("claude-fable-5[1m]", "claude-fable-5[1m]")]
    // Базовый алиас и обычные модели — без изменений (окна в них нет)
    [InlineData("opus", "opus")]
    [InlineData("deepseek-chat", "deepseek-chat")]
    [InlineData(null, null)]
    public void ResolveWindowAlias_НеТирАлиасы_НеТрогает(string? input, string? expected)
    {
        var pool = new ClaudeSubscriptionPool(ConfigWith200KPlan("lim"));
        pool.ResolveWindowAlias(input).Should().Be(expected);
    }

    [Fact]
    public void Pick_Пин1M_ВыбираетСпособнуюПодписку()
    {
        // lim (200K, но свободнее) и full (1M) — opus[1m] идёт на full, мимо 200K-рулетки
        var config = ConfigWith200KPlan("lim", "full");
        var usage = new UsageService(config);
        RecordUtil(usage, "full", 0.7);
        RecordUtil(usage, "lim", 0.0); // lim свободнее, но 1M не умеет
        var pool = new ClaudeSubscriptionPool(config, usage);

        for (var i = 0; i < 20; i++)
            pool.Pick("opus[1m]").Should().Be("full");
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

    // --- PickForDisplay: детерминированная цель роутинга для бейджа на экране usage ---

    [Fact]
    public void PickForDisplay_РавнаяУтилизация_СтабильноОдинИТотЖеКлюч()
    {
        // У Pick при равенстве — случайный tie-break; для отображения выбор обязан быть
        // стабильным, иначе бейдж «цель роутинга» мигал бы между аккаунтами.
        var config = Config("a", "b");
        var usage = new UsageService(config);
        RecordUtil(usage, "a", 0.3);
        RecordUtil(usage, "b", 0.3);

        var pool = new ClaudeSubscriptionPool(config, usage);

        var first = pool.PickForDisplay();
        for (var i = 0; i < 20; i++)
            pool.PickForDisplay().Should().Be(first);
    }

    [Fact]
    public void PickForDisplay_ОсновнаяИсчерпана_ВтораяВышеПорога_ЦельВсёРавноВторая()
    {
        // Сценарий прода 2026-07-25: claude исчерпан (неделя 100%), claude-2 выше порога →
        // IsInRotation(claude-2)=false, но все новые чаты фактически идут на claude-2.
        var config = Config(softThreshold: 0.8, ClaudeSubscriptionPool.PrimaryKey, "claude-2");
        var usage = new UsageService(config);
        RecordUtil(usage, "claude-2", 0.91);

        var pool = new ClaudeSubscriptionPool(config, usage);
        pool.MarkExhausted(ClaudeSubscriptionPool.PrimaryKey, DateTime.UtcNow.AddHours(2));

        pool.IsInRotation("claude-2").Should().BeFalse();
        pool.PickForDisplay().Should().Be("claude-2");
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

    // P29: пометка негодного auth (протухший OAuth/ключ) — отдельная от исчерпания, без resetsAt.
    // Токен не воскреснет сам, поэтому подписка исключена из ротации до Reset/рестарта.
    [Fact]
    public void MarkAuthDead_ИсключаетПодпискуИзРотации_ДоСброса()
    {
        var pool = new ClaudeSubscriptionPool(Config("acc-a", "acc-b"));

        pool.MarkAuthDead("acc-a");
        pool.IsAuthDead("acc-a").Should().BeTrue();
        pool.IsExhausted("acc-a").Should().BeFalse("auth-dead — это не квота, resetsAt ни при чём");
        // acc-a исключена — Pick всегда возвращает живую acc-b
        for (var i = 0; i < 20; i++)
            pool.Pick().Should().Be("acc-b");
        pool.IsInRotation("acc-a").Should().BeFalse();
        pool.IsInRotation("acc-b").Should().BeTrue();
        // Reset снимает обе пометки (исчерпание и auth-dead)
        pool.Reset("acc-a");
        pool.IsAuthDead("acc-a").Should().BeFalse();
    }

    // auth-dead «хуже» исчерпания: при всех мёртвых аккаунтах PickSoonestRecovery выбирает
    // воскресающего (исчерпанного с resetsAt), а не навсегда мёртвого (auth-dead).
    [Fact]
    public void AuthDead_УступаетИсчерпанному_ВFallbackВыборе()
    {
        var pool = new ClaudeSubscriptionPool(Config("dead", "tired"));
        pool.MarkAuthDead("dead");
        pool.MarkExhausted("tired", DateTime.UtcNow.AddMinutes(10)); // воскреснет через 10 мин

        // Оба выведены из ротации → fallback: выбираем того, кто воскреснет раньше (tired)
        var pick = Enumerable.Range(0, 20).Select(_ => pool.Pick()).Distinct().ToList();
        pick.Should().NotContain("dead", "auth-dead никогда не воскреснет — он хуже исчерпанного");
    }

    // P31: ClearAuthDead снимает ТОЛЬКО пометку auth-dead, не трогая исчерпание. До фикса снять
    // auth-dead можно было лишь через Reset, гейтящийся исчерпанием — транзитный 401 выключал
    // подписку до рестарта процесса (блокер ревью P29).
    [Fact]
    public void ClearAuthDead_СнимаетТолькоAuthDead_НеТрогаяИсчерпание()
    {
        var pool = new ClaudeSubscriptionPool(Config("acc-a", "acc-b"));
        pool.MarkAuthDead("acc-a");
        pool.MarkExhausted("acc-a", DateTime.UtcNow.AddHours(2));

        pool.ClearAuthDead("acc-a");

        pool.IsAuthDead("acc-a").Should().BeFalse("auth-dead снят");
        pool.IsExhausted("acc-a").Should().BeTrue("исчерпание не тронуто — ClearAuthDead ≠ Reset");
    }

    // P31, сценарий приёмки: транзитный 401 → MarkAuthDead → аккаунт доказал работоспособность
    // (rate_limit_event пришёл) → ClearAuthDead → подписка снова в ротации БЕЗ рестарта процесса.
    [Fact]
    public void ClearAuthDead_ВозвращаетВРотацию_БезРестартаПроцесса()
    {
        var pool = new ClaudeSubscriptionPool(Config("acc-a", "acc-b"));
        pool.MarkAuthDead("acc-a");
        for (var i = 0; i < 20; i++)
            pool.Pick().Should().Be("acc-b", "acc-a помечена auth-dead и исключена из ротации");

        // Токен починили, аккаунт ответил — пометка снята
        pool.ClearAuthDead("acc-a");

        pool.IsAuthDead("acc-a").Should().BeFalse();
        pool.IsInRotation("acc-a").Should().BeTrue("подписка вернулась в ротацию");
    }
}
