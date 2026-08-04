using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace ClaudeHomeServer.Tests.Controllers;

// /api/usage: LoginCommand в per-аккаунт блоке подписок — готовая команда входа для
// плашки «нужен claude login» на экране использования. Логика вычисления пути покрыта
// юнит-тестами SubscriptionOAuthUsageServiceTests.LoginCommandFor_*; здесь проверяем,
// что контроллер реально прокидывает поле в JSON-ответ для аккаунта пула.
public class UsageControllerTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public UsageControllerTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    // Приёмка-2 (задача 977c3ee5): «вторая подписка не отображается». Пул из двух
    // подписок обязан отдать обе в блоке subscriptions — экран рисует ровно то,
    // что пришло, и терять ключи не должен ни контроллер, ни пул.
    [Fact]
    public async Task GetUsage_ПулИзДвухПодписок_ОтдаётОбеВБлокеSubscriptions()
    {
        using var withPool = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [$"{ClaudeSubscriptionPool.Section}:claude:OAuthToken"] = "token-one",
                    [$"{ClaudeSubscriptionPool.Section}:claude:DisplayName"] = "Claude 1 (Max)",
                    [$"{ClaudeSubscriptionPool.Section}:claude-2:OAuthToken"] = "token-two",
                    [$"{ClaudeSubscriptionPool.Section}:claude-2:DisplayName"] = "Claude 2 (Max)",
                });
            });
        });
        var client = await AuthenticateAsync(withPool);

        var usage = await client.GetFromJsonAsync<JsonElement>("/api/usage");

        var subs = usage.GetProperty("subscriptions");
        subs.GetProperty("claude").GetProperty("name").GetString().Should().Be("Claude 1 (Max)");
        subs.GetProperty("claude-2").GetProperty("name").GetString().Should().Be("Claude 2 (Max)");
    }

    [Fact]
    public async Task GetUsage_АккаунтПула_ОтдаётLoginCommandСПутёмПрофиля()
    {
        using var withPool = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [$"{ClaudeSubscriptionPool.Section}:second:OAuthToken"] = "token-second",
                });
            });
        });
        var client = await AuthenticateAsync(withPool);

        var usage = await client.GetFromJsonAsync<JsonElement>("/api/usage");

        var login = usage.GetProperty("subscriptions").GetProperty("second")
            .GetProperty("loginCommand").GetString();
        login.Should().NotBeNull();
        login.Should().Contain("CLAUDE_CONFIG_DIR")
            .And.Contain($"sub-second")
            .And.Contain("claude login");
    }

    // WithWebHostBuilder возвращает базовый WebApplicationFactory<Program> — расширение
    // CreateAuthenticatedClient из TestWebApplicationFactory ему недоступно, логинимся вручную.
    private static async Task<HttpClient> AuthenticateAsync(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new
        {
            username = TestWebApplicationFactory.TestUsername,
            password = TestWebApplicationFactory.TestPassword,
        });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", body.GetProperty("token").GetString());
        return client;
    }
}
