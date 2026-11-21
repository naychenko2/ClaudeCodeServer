using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.ChildMode;
using ClaudeHomeServer.Services.Llm;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace ClaudeHomeServer.Tests.Services;

// И2″ (ADR-041 ред. 4 решения 25–26, 27): ChildRoute.Evaluate/Resolve/ChildChain — отбор маршрута
// ребёнка. Детских ключей нет: ребёнку доступен любой маршрут реестра взрослых — любая включённая
// запись пула (setup-token, API-ключ, смешанная) и любой включённый провайдер, — и только с одним
// согласием профиля: без него не годен ни один маршрут, прежние согласия предмета его не открывают.
// Сторож на каждый путь: форма записи пула, маркеры tier:*/preset:*, цена 0 у облачной, способность
// плана (Opus/1M), модель чужого провайдера, первичный ключ без записи, облачный шаг без цены
// в цепочке.
public class ChildRouteEvaluateTests
{
    private const string Pool1 = "kids-api";

    private static (ClaudeSubscriptionPool Pool, LlmProviderRegistry Providers) Build(
        Action<Dictionary<string, string?>>? tweak = null)
    {
        var d = new Dictionary<string, string?>
        {
            [$"{ClaudeSubscriptionPool.Section}:{Pool1}:ApiKey"] = "sk-ant-kids",
            [$"{ClaudeSubscriptionPool.Section}:oauth-only:OAuthToken"] = "oauth-x",
            [$"{ClaudeSubscriptionPool.Section}:both:ApiKey"] = "sk-ant-x",
            [$"{ClaudeSubscriptionPool.Section}:both:OAuthToken"] = "oauth-x",
            // Планы без Opus и без 1M-окна: способность плана — свойство тарифа (SupportsModel),
            // не временная пометка пары.
            [$"{ClaudeSubscriptionPool.Section}:no-opus:ApiKey"] = "sk-ant-noopus",
            [$"{ClaudeSubscriptionPool.Section}:no-opus:SupportsOpus"] = "false",
            [$"{ClaudeSubscriptionPool.Section}:no-1m:ApiKey"] = "sk-ant-no1m",
            [$"{ClaudeSubscriptionPool.Section}:no-1m:Supports1M"] = "false",
            // Облачный провайдер с ценой
            ["LlmProviders:deepseek:AnthropicBaseUrl"] = "https://api.deepseek.test/anthropic",
            ["LlmProviders:deepseek:ApiKey"] = "sk-ds",
            ["LlmProviders:deepseek:Models:0:Id"] = "deepseek-v4-pro",
            ["LlmProviders:deepseek:Models:0:PriceInMissPer1M"] = "0.5",
            ["LlmProviders:deepseek:Models:0:PriceOutPer1M"] = "1.0",
            // Облачный провайдер без цены (обе стороны 0)
            ["LlmProviders:glm:AnthropicBaseUrl"] = "https://api.glm.test/anthropic",
            ["LlmProviders:glm:ApiKey"] = "sk-glm",
            ["LlmProviders:glm:Models:0:Id"] = "glm-5.2",
            // Локальный провайдер
            ["LlmProviders:loc:AnthropicBaseUrl"] = "http://127.0.0.1:18020",
            ["LlmProviders:loc:IsLocal"] = "true",
            ["LlmProviders:loc:Models:0:Id"] = "loc-27b",
            // Выключенный (нет ключа)
            ["LlmProviders:off:AnthropicBaseUrl"] = "https://api.off.test/anthropic",
            ["LlmProviders:off:Models:0:Id"] = "off-1",
            ["LlmProviders:off:Models:0:PriceInMissPer1M"] = "1",
        };
        tweak?.Invoke(d);
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(d).Build();
        return (new ClaudeSubscriptionPool(cfg), new LlmProviderRegistry(cfg));
    }

    // Профиль без поля согласия: согласие — параметр Evaluate/Resolve (решение 26), поле профиля
    // здесь не ставится и не читается — его добавит Б2a, читать будет вызывающий (Б2b).
    private static ChildProfile Profile(string providerKey = Pool1, string model = "sonnet") =>
        new()
        {
            Enabled = true,
            ProviderKey = providerKey,
            Model = model,
        };

    // --- путь 1: любая включённая запись пула (setup-token, API-ключ, оба) ---

    [Theory]
    [InlineData(Pool1)]
    [InlineData("oauth-only")]   // claude setup-token: токен без ключа — тоже маршрут ребёнка
    [InlineData("both")]         // оба способа сразу
    public void Пул_ЛюбаяЗапись_СоСогласием_МаршрутПул(string key)
    {
        var (pool, providers) = Build();
        var d = ChildRoute.Evaluate(key, "sonnet", true, pool, providers);
        d.Kind.Should().Be(ChildRouteKind.Pool);
        d.Pool!.Key.Should().Be(key);
    }

    [Theory]
    [InlineData(Pool1)]
    [InlineData("oauth-only")]
    [InlineData("both")]
    public void Пул_БезСогласияПрофиля_Отказ(string key)
    {
        var (pool, providers) = Build();
        ChildRoute.Evaluate(key, "sonnet", false, pool, providers).Refusal.Should()
            .Be(ChildRouteRefusal.RiskUnacknowledged,
                "режима «безопасный маршрут без согласия» нет: детских ключей нет, отличать не от чего");
    }

    [Fact]
    public void ПервичныйКлючБезЗаписиПула_NoRoute()
    {
        var (pool, providers) = Build();
        // Учётка хостового ~/.claude: без записи в секции пула она недоступна ни одному ходу,
        // а на проде «claude» — обычная запись пула (тот же путь, что выше).
        ChildRoute.Evaluate(ClaudeSubscriptionPool.PrimaryKey, "sonnet", true, pool, providers).Refusal
            .Should().Be(ChildRouteRefusal.NoRoute);
    }

    [Fact]
    public void БезРеестраПровайдеров_Отказ_FailClosed()
    {
        var (pool, _) = Build();
        ChildRoute.Evaluate(Pool1, "sonnet", true, pool, null).Accepted.Should().BeFalse();
    }

    // --- путь 2: маркеры в модели ---

    [Theory]
    [InlineData("tier:strong")]
    [InlineData("TIER:weak")]
    [InlineData("preset:chain-1")]
    [InlineData("default")]
    [InlineData("claude")]
    [InlineData("")]
    [InlineData(null)]
    public void Маркерная_Модель_ModelNotConcrete(string? model)
    {
        var (pool, providers) = Build();
        ChildRoute.Evaluate(Pool1, model, true, pool, providers).Refusal.Should().Be(ChildRouteRefusal.ModelNotConcrete);
        ChildRoute.Evaluate("deepseek", model, true, pool, providers).Refusal.Should().Be(ChildRouteRefusal.ModelNotConcrete);
    }

    // --- путь 3: способность ПЛАНА аккаунта (Opus/1M на плане без них — не маршрут) ---

    [Theory]
    [InlineData("opus")]
    [InlineData("claude-opus-4-5")]
    public void ПланБезOpus_Opus_ModelNotOnPlan(string model)
    {
        var (pool, providers) = Build();
        ChildRoute.Evaluate("no-opus", model, true, pool, providers).Refusal
            .Should().Be(ChildRouteRefusal.ModelNotOnPlan,
                "на неспособном плану аккаунте ход падает «issue with the selected model», а не тратит бюджет");
        ChildRoute.Evaluate("no-opus", "sonnet", true, pool, providers).Kind.Should().Be(ChildRouteKind.Pool,
            "срезается неспособная плану модель, а не весь аккаунт");
    }

    [Fact]
    public void ПланБез1M_Окно1M_ModelNotOnPlan()
    {
        var (pool, providers) = Build();
        ChildRoute.Evaluate("no-1m", "opus[1m]", true, pool, providers).Refusal
            .Should().Be(ChildRouteRefusal.ModelNotOnPlan);
        ChildRoute.Evaluate("no-1m", "opus", true, pool, providers).Kind.Should().Be(ChildRouteKind.Pool);
    }

    // --- путь 4: цена 0 у облачной ---

    [Fact]
    public void Облако_БезЦены_ModelUnpriced()
    {
        var (pool, providers) = Build();
        ChildRoute.Evaluate("glm", "glm-5.2", true, pool, providers).Refusal.Should().Be(ChildRouteRefusal.ModelUnpriced);
    }

    [Fact]
    public void Облако_СЦеной_ИСогласием_МаршрутПровайдер()
    {
        var (pool, providers) = Build();
        var d = ChildRoute.Evaluate("deepseek", "deepseek-v4-pro", true, pool, providers);
        d.Kind.Should().Be(ChildRouteKind.Provider);
        d.Provider!.Key.Should().Be("deepseek");
    }

    // --- путь 5: согласие профиля — одно на любой маршрут ---

    [Theory]
    [InlineData("deepseek", "deepseek-v4-pro")]
    [InlineData("loc", "loc-27b")]
    public void НеПул_БезСогласия_RiskUnacknowledged(string key, string model)
    {
        var (pool, providers) = Build();
        ChildRoute.Evaluate(key, model, false, pool, providers).Refusal.Should().Be(ChildRouteRefusal.RiskUnacknowledged);
        ChildRoute.Evaluate(key, model, true, pool, providers).Accepted.Should().BeTrue();
    }

    [Fact]
    public void Локальная_ЦенаНеНужна_СоСогласием_Принята()
    {
        var (pool, providers) = Build();
        ChildRoute.Evaluate("loc", "loc-27b", true, pool, providers).Kind.Should().Be(ChildRouteKind.Provider);
    }

    // --- путь 6: модель чужого провайдера ---

    [Fact]
    public void Провайдер_МодельДругогоПровайдера_ModelNotOfProvider()
    {
        var (pool, providers) = Build();
        ChildRoute.Evaluate("deepseek", "glm-5.2", true, pool, providers).Refusal
            .Should().Be(ChildRouteRefusal.ModelNotOfProvider);
    }

    [Fact]
    public void ПулКлюч_СоСторонней_Моделью_ModelNotOfProvider()
    {
        var (pool, providers) = Build();
        ChildRoute.Evaluate(Pool1, "deepseek-v4-pro", true, pool, providers).Refusal
            .Should().Be(ChildRouteRefusal.ModelNotOfProvider);
    }

    // --- прочее: отказ ---

    [Theory]
    [InlineData("off", "off-1")]       // провайдер выключен
    [InlineData("missing", "sonnet")]  // ключа нет нигде
    [InlineData("", "sonnet")]
    public void ПрочиеКлючи_Отказ(string key, string model)
    {
        var (pool, providers) = Build();
        ChildRoute.Evaluate(key, model, true, pool, providers).Refusal.Should().Be(ChildRouteRefusal.NoRoute);
    }

    // --- Resolve / ResolveLesson / SubjectFor: согласие — параметр вызывающего, не поле профиля (решение 26) ---

    [Fact]
    public void ResolveLesson_СогласиеПрофиля_ОткрываетМаршрутУрока_ПоПареЧата()
    {
        // Б17c: маршрут урока — пара самого чата (поставщик и модель, закреплённые запуском), а не поля предмета.
        var (pool, providers) = Build();
        var snap = new ChildPolicySnapshot("kid", Profile(), false);
        ChildRoute.ResolveLesson(snap, "deepseek", "deepseek-v4-pro", true, pool, providers).Kind
            .Should().Be(ChildRouteKind.Provider);
    }

    [Fact]
    public void ResolveLesson_БезСогласияПрофиля_Отказ_ИПустаяПараОтказывает()
    {
        var (pool, providers) = Build();
        var snap = new ChildPolicySnapshot("kid", Profile(), false);
        FluentActions.Invoking(() => ChildRoute.ResolveLesson(snap, "deepseek", "deepseek-v4-pro", false, pool, providers))
            .Should().Throw<ChildRouteRefusedException>("согласие — одно на профиль (решение 26)");
        FluentActions.Invoking(() => ChildRoute.ResolveLesson(snap, null, null, true, pool, providers))
            .Should().Throw<ChildRouteRefusedException>("чат без закреплённой пары маршрута не имеет");
    }

    [Fact]
    public void Resolve_ЧатПравил_СогласованныйМаршрутПрофиля_МаршрутПул()
    {
        var (pool, providers) = Build();
        var snap = new ChildPolicySnapshot("kid", Profile(), false);
        ChildRoute.Resolve(snap, true, pool, providers).Kind.Should().Be(ChildRouteKind.Pool);
    }

    [Fact]
    public void Resolve_ЧатПравил_БезСогласияПрофиля_Отказ()
    {
        var (pool, providers) = Build();
        var snap = new ChildPolicySnapshot("kid", Profile(), false);
        FluentActions.Invoking(() => ChildRoute.Resolve(snap, false, pool, providers))
            .Should().Throw<ChildRouteRefusedException>("без согласия не годен ни один маршрут");
        // Согласие не открывает маршрут, которого нет: первичный ключ без записи пула — тот же NoRoute.
        var noRoute = new ChildPolicySnapshot("kid", Profile(providerKey: "oauth-missing"), false);
        FluentActions.Invoking(() => ChildRoute.Resolve(noRoute, true, pool, providers))
            .Should().Throw<ChildRouteRefusedException>();
    }

    [Fact]
    public void Resolve_DenyAll_Отказ()
    {
        var (pool, providers) = Build();
        // С согласием тоже: DenyAll и отсутствие профиля отказывают раньше, чем вердикт согласия.
        FluentActions.Invoking(() => ChildRoute.Resolve(new ChildPolicySnapshot("kid", null, true), true, pool, providers))
            .Should().Throw<ChildRouteRefusedException>();
    }

    [Fact]
    public void SubjectFor_ПредметЗакрытИлиНет_Отказ_ЧатПравил_Null()
    {
        var snap = new ChildPolicySnapshot("kid", new ChildProfile
        {
            Enabled = true,
            Subjects = [new ChildSubject { Id = "math", Enabled = true }, new ChildSubject { Id = "off", Enabled = false }],
        }, false);
        ChildRoute.SubjectFor(snap, new Session { ChildSpace = "lessons", ChildSubject = "math" })!.Id.Should().Be("math");
        FluentActions.Invoking(() => ChildRoute.SubjectFor(snap, new Session { ChildSpace = "lessons", ChildSubject = "off" }))
            .Should().Throw<ChildRouteRefusedException>();
        FluentActions.Invoking(() => ChildRoute.SubjectFor(snap, new Session { ChildSpace = "lessons", ChildSubject = "none" }))
            .Should().Throw<ChildRouteRefusedException>();
        ChildRoute.SubjectFor(snap, new Session { ChildSpace = "lessons", OnboardingKind = OnboardingKinds.ChildRules })
            .Should().BeNull();
    }

    // --- цепочка фолбэка ребёнка: облачные шаги без цены выпадают (решение 27) ---

    [Fact]
    public void ЦепочкаРебёнка_ОблакоБезЦеныВыпадает_ПодпискаИЛокальнаяОстаются()
    {
        var (_, providers) = Build();
        // Хвост слотов, каким он приходит из ResolveChain: подписка, облако без цены, локальная.
        ChildRoute.ChildChain(["sonnet", "glm-5.2", "loc-27b"], providers).Should()
            .Equal(["sonnet", "loc-27b"]);
        // Облако с ценой остаётся: бюджет на нём считает расход.
        ChildRoute.ChildChain(["glm-5.2", "deepseek-v4-pro"], providers).Should().Equal(["deepseek-v4-pro"]);
        // Пустая цепочка — пустая: без хвоста отказывает вызывающий, здесь магии нет.
        ChildRoute.ChildChain([], providers).Should().BeEmpty();
    }
}
