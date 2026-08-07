using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ClaudeHomeServer.Tests.Controllers;

// Резка кошелька от не-админа: денежные поля баланса провайдера не должны попадать в ответ API.
// Сервис подменяется стабом — реальный фетч идёт в сеть, здесь проверяем только поведение ролей.
public class ProvidersControllerTests
{
    // Полный баланс ДЕНЕЖНОГО провайдера (DeepSeek-подобный): со всеми полями, включая деньги.
    private static ProviderBalance MoneyBalance() => new(
        Available: true, Currency: "USD", TotalBalance: "39.75",
        AsOf: new DateTime(2026, 8, 7, 0, 0, 0, DateTimeKind.Utc),
        GrantedBalance: 5.5,
        KeyLimit: new ProviderKeyLimit(45.5, 100), Spend: new ProviderSpend(1.2, 2, 3));

    // Стаб сервиса баланса: отдаёт заданный баланс и один снапшот (реальный фетч идёт в сеть —
    // здесь проверяем только поведение ролей). Снапшот в «сторе» есть всегда, режет его контроллер.
    private sealed class StubBalance(ProviderBalance balance) : IProviderBalanceService
    {
        public LlmProviderConfig? GetSupported(string key) =>
            key == "deepseek"
                ? new LlmProviderConfig { Key = "deepseek", Balance = "deepseek",
                    ApiBaseUrl = "http://x", ApiKey = "k", AnthropicBaseUrl = "http://x" }
                : null;

        public Task<ProviderBalance?> GetAsync(string key, CancellationToken ct) =>
            Task.FromResult<ProviderBalance?>(balance);

        public IReadOnlyList<ProviderBalanceSnapshot> GetSnapshots(string key) =>
            [new ProviderBalanceSnapshot(DateTime.UtcNow, 39.0, balance.Currency)];
    }

    private static TestWebApplicationFactory FactoryWithStub(ProviderBalance? balance = null) =>
        new()
        {
            ExtraServices = s =>
            {
                s.RemoveAll<IProviderBalanceService>();
                s.AddSingleton<IProviderBalanceService>(new StubBalance(balance ?? MoneyBalance()));
            }
        };

    [Fact]
    public async Task АдминВидитДеньгиБаланса()
    {
        using var factory = FactoryWithStub();
        var admin = factory.CreateAuthenticatedClient();

        var body = await admin.GetFromJsonAsync<JsonElement>("/api/providers/deepseek/balance");
        body.GetProperty("totalBalance").GetString().Should().Be("39.75");
        body.GetProperty("currency").GetString().Should().Be("USD");
        body.GetProperty("grantedBalance").GetDouble().Should().Be(5.5);
        body.GetProperty("keyLimit").GetProperty("remaining").GetDouble().Should().Be(45.5);
        body.GetProperty("spend").GetProperty("daily").GetDouble().Should().Be(1.2);
    }

    [Fact]
    public async Task НеадминНеВидитНиОдногоДенежногоПоля()
    {
        using var factory = FactoryWithStub();
        var user = factory.CreateAuthenticatedClient(
            TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);

        var body = await user.GetFromJsonAsync<JsonElement>("/api/providers/deepseek/balance");

        // Ни одного денежного поля в ответе не-админа быть не должно
        foreach (var money in new[] { "totalBalance", "currency", "grantedBalance", "keyLimit", "spend" })
            body.TryGetProperty(money, out _).Should()
                .BeFalse($"поле «{money}» раскрывает кошелёк владельца");

        // Не-денежные сведения на месте — они объясняют поведение моделей и денег не раскрывают
        body.GetProperty("available").GetBoolean().Should().BeTrue();
        body.GetProperty("planLabel").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Неадмин_ИсторияДенежногоПровайдераПустая()
    {
        using var factory = FactoryWithStub();
        var user = factory.CreateAuthenticatedClient(
            TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);

        var body = await user.GetFromJsonAsync<JsonElement>("/api/providers/deepseek/usage");
        body.GetProperty("snapshots").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Админ_ИсторияДенежногоПровайдераНаМесте()
    {
        using var factory = FactoryWithStub();
        var admin = factory.CreateAuthenticatedClient();

        var body = await admin.GetFromJsonAsync<JsonElement>("/api/providers/deepseek/usage");
        body.GetProperty("balance").GetProperty("totalBalance").GetString().Should().Be("39.75");
        body.GetProperty("snapshots").GetArrayLength().Should().Be(1);
    }

    // Денежный провайдер с ПУСТОЙ валютой (DeepSeek без поля currency в ответе): раньше IsMonetary
    // на пустой валюте лгал «не деньги», и история утекала не-админу. IsQuota (whitelist) теперь режет
    // всё, что не подтверждённая квота, — пустая валюта режется. Сбой провайдера не должен раскрывать кошелёк.
    [Fact]
    public async Task Неадмин_ДенежныйПровайдерСПустойВалютой_ИсторияПустая()
    {
        using var factory = FactoryWithStub(MoneyBalance() with { Currency = "" });
        var user = factory.CreateAuthenticatedClient(
            TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);

        var body = await user.GetFromJsonAsync<JsonElement>("/api/providers/deepseek/usage");

        body.GetProperty("snapshots").GetArrayLength().Should().Be(0);
    }

    // Квотный провайдер (Currency "%"): не-админу историю видно — это не кошелёк, режется только деньги
    [Fact]
    public async Task Неадмин_КвотныйПровайдер_ИсторияНаМесте()
    {
        var quota = MoneyBalance() with
        {
            Currency = "%", TotalBalance = "69", Windows = [],
            GrantedBalance = null, KeyLimit = null, Spend = null,
        };
        using var factory = FactoryWithStub(quota);
        var user = factory.CreateAuthenticatedClient(
            TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);

        var body = await user.GetFromJsonAsync<JsonElement>("/api/providers/deepseek/usage");

        body.GetProperty("snapshots").GetArrayLength().Should().Be(1);
    }
}
