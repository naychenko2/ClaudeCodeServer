using System.Text.Json;
using ClaudeHomeServer.Services.Llm;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Типизированные поля и новые источники баланса провайдеров — фикстуры по живым снимкам
// запросов 07.08.2026. Сетевые фетчеры требуют ключа/живого сервера (в dev провайдер выключен),
// поэтому контракт фиксируется тестами на разборщиках (internal static) — как у GLM/Kimi.
public class ProviderBalanceExtensionsTests
{
    private static JsonElement El(string json) => JsonDocument.Parse(json).RootElement;

    // ── FreeLLM: provider_health → окно «Провайдеры», usage_summary → Health ─────────

    [Fact]
    public void FreeLlm_Здоровье_СчитаетЖивыхИВсех()
    {
        // Две платформы с healthy-ключами из трёх → 2/3
        var json = """
            {"openai":{"keys":{"healthy":3,"rate_limited":1},"active_cooldowns":1,"available_models":12},
             "anthropic":{"keys":{"healthy":2},"active_cooldowns":0,"available_models":5},
             "google":{"keys":{"rate_limited":2},"active_cooldowns":2,"available_models":0}}
            """;
        ProviderBalanceService.ParseFreeLlmHealth(El(json)).Should().Be((2, 3));
    }

    [Fact]
    public void FreeLlm_ВсеПлатформыНеЖивы_НольЖивых()
    {
        var json = """
            {"openai":{"keys":{"rate_limited":2}},
             "anthropic":{"keys":{"invalid":1}}}
            """;
        ProviderBalanceService.ParseFreeLlmHealth(El(json)).Should().Be((0, 2));
    }

    [Fact]
    public void FreeLlm_ПлатформаБезKeys_НеЖива()
    {
        // healthy нет вовсе → платформа не жива, но в «всего» входит
        ProviderBalanceService.ParseFreeLlmHealth(El("""{"openai":{"available_models":3}}"""))
            .Should().Be((0, 1));
    }

    [Fact]
    public void FreeLlm_ПустойИлиМусор_ЗдоровьяNull()
    {
        ProviderBalanceService.ParseFreeLlmHealth(El("""{}""")).Should().BeNull();
        ProviderBalanceService.ParseFreeLlmHealth(El("""[]""")).Should().BeNull();
    }

    // ── Новые типизированные поля (значения) ────────────────────────────────────────

    // DeepSeek: GrantedBalance — число подарочного остатка (granted_balance > 0, иначе null)
    [Fact]
    public void DeepSeek_ПодарочныйБаланс_Значение()
    {
        ProviderBalanceService.DeepSeekGrantedBalance(El("""{"granted_balance":"5.50"}"""))
            .Should().Be(5.5);
    }

    [Fact]
    public void DeepSeek_ПодарочныйНоль_ЗначенияНет()
    {
        ProviderBalanceService.DeepSeekGrantedBalance(El("""{"granted_balance":"0.00"}"""))
            .Should().BeNull();
    }

    // OpenRouter: ParseOpenRouterKey — Spend и KeyLimit как независимые поля
    [Fact]
    public void OpenRouter_Ключ_SpendИKeyLimit()
    {
        var data = ProviderBalanceService.ParseOpenRouterKey(El(
            """{"data":{"usage_daily":1.2,"usage_weekly":2,"usage_monthly":3,"limit":100,"limit_remaining":45.5}}"""));
        data!.Spend.Should().Be(new ProviderSpend(1.2, 2, 3));
        data.KeyLimit.Should().Be(new ProviderKeyLimit(45.5, 100));
    }

    [Fact]
    public void OpenRouter_Ключ_ЛимитNull_KeyLimitНет_SpendЕсть()
    {
        var data = ProviderBalanceService.ParseOpenRouterKey(El(
            """{"data":{"usage_daily":0.1,"usage_weekly":0.2,"usage_monthly":0.3,"limit":null}}"""));
        data!.Spend.Should().Be(new ProviderSpend(0.1, 0.2, 0.3));
        data.KeyLimit.Should().BeNull();
    }

    [Fact]
    public void OpenRouter_Ключ_ДневногоРасходаНет_SpendНет_KeyLimitЕсть()
    {
        // Независимые поля: расхода нет, но лимит ключа разобрался
        var data = ProviderBalanceService.ParseOpenRouterKey(El(
            """{"data":{"usage_weekly":2,"limit":10,"limit_remaining":8}}"""));
        data!.Spend.Should().BeNull();
        data.KeyLimit.Should().Be(new ProviderKeyLimit(8, 10));
    }

    [Fact]
    public void OpenRouter_Ключ_RemainingNull_KeyLimitНет()
    {
        var data = ProviderBalanceService.ParseOpenRouterKey(El(
            """{"data":{"usage_daily":1,"usage_weekly":2,"usage_monthly":3,"limit":100,"limit_remaining":null}}"""));
        data!.Spend.Should().NotBeNull();
        data.KeyLimit.Should().BeNull();
    }

    [Fact]
    public void OpenRouter_Ключ_НичегоНет_Null()
    {
        ProviderBalanceService.ParseOpenRouterKey(El("""{"error":"no key"}""")).Should().BeNull();
    }

    // FreeLLM: ParseFreeLlmUsageData — значения трафика; ComposeFreeLlmHealth — сборка Health
    [Fact]
    public void FreeLlm_Использование_Значения()
    {
        var data = ProviderBalanceService.ParseFreeLlmUsageData(El(
            """{"range":"24h","requests":1500,"success_rate":97.5,"input_tokens":120000,"output_tokens":45000}"""));
        data!.Requests24h.Should().Be(1500);
        data.SuccessRate.Should().Be(97.5);
    }

    [Fact]
    public void FreeLlm_Использование_БезSuccessRate_Значение()
    {
        var data = ProviderBalanceService.ParseFreeLlmUsageData(El(
            """{"range":"24h","requests":10,"success_rate":null}"""));
        data!.Requests24h.Should().Be(10);
        data.SuccessRate.Should().BeNull();
    }

    [Fact]
    public void FreeLlm_Использование_НетRequests_Null()
    {
        ProviderBalanceService.ParseFreeLlmUsageData(El("""{"success_rate":99}""")).Should().BeNull();
    }

    [Fact]
    public void FreeLlm_Здоровье_СборкаИзПлатформИТрафика()
    {
        var health = ProviderBalanceService.ComposeFreeLlmHealth((2, 3),
            new ProviderBalanceService.FreeLlmUsageData(1500, 97.5));
        health!.Requests24h.Should().Be(1500);
        health.SuccessRate.Should().Be(97.5);
        health.PlatformsAlive.Should().Be(2);
        health.PlatformsTotal.Should().Be(3);
    }

    [Fact]
    public void FreeLlm_Здоровье_ТолькоПлатформы()
    {
        // usage не пришёл — Health есть на одних платформах, без трафика
        var health = ProviderBalanceService.ComposeFreeLlmHealth((1, 2), null);
        health!.Requests24h.Should().BeNull();
        health.SuccessRate.Should().BeNull();
        health.PlatformsAlive.Should().Be(1);
        health.PlatformsTotal.Should().Be(2);
    }

    [Fact]
    public void FreeLlm_Здоровье_ТолькоТрафик()
    {
        var health = ProviderBalanceService.ComposeFreeLlmHealth(null,
            new ProviderBalanceService.FreeLlmUsageData(7, null));
        health!.Requests24h.Should().Be(7);
        health.PlatformsAlive.Should().BeNull();
        health.PlatformsTotal.Should().BeNull();
    }

    [Fact]
    public void FreeLlm_Здоровье_Ничего_Null()
    {
        ProviderBalanceService.ComposeFreeLlmHealth(null, null).Should().BeNull();
    }
}
