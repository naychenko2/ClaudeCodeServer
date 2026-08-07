using System.Text.Json;
using ClaudeHomeServer.Services.Llm;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Доп. сведения (Note) и новые источники баланса провайдеров — фикстуры по живым снимкам
// запросов 07.08.2026. Сетевые фетчеры требуют ключа/живого сервера (в dev провайдер выключен),
// поэтому контракт фиксируется тестами на разборщиках (internal static) — как у GLM/Kimi.
public class ProviderBalanceExtensionsTests
{
    private static JsonElement El(string json) => JsonDocument.Parse(json).RootElement;

    // ── DeepSeek: granted_balance → Note «В том числе подарочных: $X» ────────────────

    [Fact]
    public void DeepSeek_ПодарочныйБаланс_Note()
    {
        ProviderBalanceService.DeepSeekGrantedNote(El("""{"granted_balance":"5.50"}"""))
            .Should().Be("В том числе подарочных: $5.5");
    }

    [Fact]
    public void DeepSeek_ПодарочныйНоль_NoteНет()
    {
        // granted_balance = 0 — не шумим
        ProviderBalanceService.DeepSeekGrantedNote(El("""{"granted_balance":"0.00"}"""))
            .Should().BeNull();
    }

    [Fact]
    public void DeepSeek_ПоляНет_NoteНет()
    {
        ProviderBalanceService.DeepSeekGrantedNote(El("""{"total_balance":"39.75"}"""))
            .Should().BeNull();
    }

    // ── OpenRouter: GET /key → Note расхода по периодам и лимита ключа ───────────────

    [Fact]
    public void OpenRouter_РасходПоПериодам_БезЛимита()
    {
        // Живой снимок 07.08: limit/limit_remaining null → куска про лимит нет
        var note = ProviderBalanceService.ParseOpenRouterKeyNote(El(
            """{"data":{"usage_daily":0.0003,"usage_weekly":0,"usage_monthly":0,"limit":null,"limit_remaining":null}}"""));

        note.Should().Be("Расход: $0.0003 сегодня · $0 за неделю · $0 за месяц");
    }

    [Fact]
    public void OpenRouter_СЛимитомКлюча_КусокОЛимите()
    {
        var note = ProviderBalanceService.ParseOpenRouterKeyNote(El(
            """{"data":{"usage_daily":1.2,"usage_weekly":2,"usage_monthly":3,"limit":100,"limit_remaining":45.5}}"""));

        note.Should().Be("Расход: $1.2 сегодня · $2 за неделю · $3 за месяц · лимит ключа: осталось $45.5 из $100");
    }

    [Fact]
    public void OpenRouter_ЛимитЧислом_RemainingNull_БезКускаОЛимите()
    {
        // limit задан, но limit_remaining null — сказать «осталось» нельзя, кусок опускаем
        var note = ProviderBalanceService.ParseOpenRouterKeyNote(El(
            """{"data":{"usage_daily":1,"usage_weekly":2,"usage_monthly":3,"limit":100,"limit_remaining":null}}"""));

        note.Should().Be("Расход: $1 сегодня · $2 за неделю · $3 за месяц");
    }

    [Fact]
    public void OpenRouter_ДневногоРасходаНет_NoteНет()
    {
        // Не хватает расходной части — лучше никакой Note, чем частичный
        ProviderBalanceService.ParseOpenRouterKeyNote(El(
            """{"data":{"usage_weekly":2,"usage_monthly":3}}""")).Should().BeNull();
    }

    [Fact]
    public void OpenRouter_НетData_NoteНет()
    {
        ProviderBalanceService.ParseOpenRouterKeyNote(El("""{"error":"no key"}""")).Should().BeNull();
    }

    // ── FreeLLM: provider_health → окно «Провайдеры», usage_summary → Note ───────────

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

    [Fact]
    public void FreeLlm_Использование_ПолнаяNote()
    {
        var json = """
            {"range":"24h","requests":1500,"success_rate":97.5,
             "input_tokens":120000,"output_tokens":45000,"top_models":[]}
            """;
        ProviderBalanceService.ParseFreeLlmUsage(El(json))
            .Should().Be("За 24ч: 1500 запросов, успех 97.5% · токены 120000 вх / 45000 исх");
    }

    [Fact]
    public void FreeLlm_Использование_БезSuccessRate_КусокОпущен()
    {
        var json = """{"range":"24h","requests":10,"success_rate":null,"input_tokens":1,"output_tokens":2}""";
        ProviderBalanceService.ParseFreeLlmUsage(El(json))
            .Should().Be("За 24ч: 10 запросов · токены 1 вх / 2 исх");
    }

    [Fact]
    public void FreeLlm_Использование_НетRequests_NoteНет()
    {
        ProviderBalanceService.ParseFreeLlmUsage(El("""{"success_rate":99}""")).Should().BeNull();
    }

    [Fact]
    public void FreeLlm_Использование_ТолькоЗапросы()
    {
        // Ни успеха, ни токенов — Note из одного куска (квантор «запросов» из ТЗ, без склонения)
        ProviderBalanceService.ParseFreeLlmUsage(El("""{"range":"24h","requests":7}"""))
            .Should().Be("За 24ч: 7 запросов");
    }
}
