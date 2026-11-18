using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.ChildMode;
using ClaudeHomeServer.Services.Llm;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace ClaudeHomeServer.Tests.Services;

// И2′ (ADR-041 ред. 2, разрез §3): ChildRoute.Evaluate/Resolve — классификация маршрута «пул | провайдер | отказ».
// По сторожу на каждый путь шага Т5a: запись пула с OAuthToken, маркеры tier:*/preset:*, цена 0 у облачной,
// согласие админа, модель чужого провайдера.
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

    // --- путь 1: запись пула с OAuthToken ---

    [Theory]
    [InlineData("oauth-only")]
    [InlineData("both")]
    public void Пул_ЗаписьСТокеномПодписки_Отказ(string key)
    {
        var (pool, providers) = Build();
        ChildRoute.Evaluate(key, "sonnet", true, pool, providers).Refusal.Should().Be(ChildRouteRefusal.NoRoute);
    }

    [Fact]
    public void Пул_КлючБезТокена_АнтропикМодель_МаршрутПул()
    {
        var (pool, providers) = Build();
        var d = ChildRoute.Evaluate(Pool1, "sonnet", false, pool, providers);
        d.Kind.Should().Be(ChildRouteKind.Pool);
        d.Pool!.Key.Should().Be(Pool1);
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

    // --- путь 3: цена 0 у облачной ---

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

    // --- путь 4: согласие админа ---

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

    // --- путь 5: модель чужого провайдера ---

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

    // --- Resolve / SubjectFor ---

    [Fact]
    public void Resolve_Предмет_ИдётПоПолямПредметаИЕгоСогласию()
    {
        var (pool, providers) = Build();
        var snap = new ChildPolicySnapshot("kid",
            new ChildProfile { Enabled = true, ProviderKey = Pool1, Model = "sonnet" }, false);
        var subject = new ChildSubject { ProviderKey = "deepseek", Model = "deepseek-v4-pro" };
        FluentActions.Invoking(() => ChildRoute.Resolve(snap, subject, pool, providers))
            .Should().Throw<ChildRouteRefusedException>("согласия нет");
        subject.RouteAcknowledgedAt = DateTime.UtcNow;
        ChildRoute.Resolve(snap, subject, pool, providers).Kind.Should().Be(ChildRouteKind.Provider);
    }

    [Fact]
    public void Resolve_ЧатПравил_ПоПолямПрофиля_ТемЖеРезолвером()
    {
        var (pool, providers) = Build();
        var ok = new ChildPolicySnapshot("kid",
            new ChildProfile { Enabled = true, ProviderKey = Pool1, Model = "sonnet" }, false);
        ChildRoute.Resolve(ok, null, pool, providers).Kind.Should().Be(ChildRouteKind.Pool);
        var bad = new ChildPolicySnapshot("kid",
            new ChildProfile { Enabled = true, ProviderKey = "oauth-only", Model = "sonnet" }, false);
        FluentActions.Invoking(() => ChildRoute.Resolve(bad, null, pool, providers))
            .Should().Throw<ChildRouteRefusedException>();
    }

    [Fact]
    public void Resolve_DenyAll_Отказ()
    {
        var (pool, providers) = Build();
        FluentActions.Invoking(() => ChildRoute.Resolve(new ChildPolicySnapshot("kid", null, true), null, pool, providers))
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
}
