using System.Net;
using System.Net.Http.Json;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

namespace ClaudeHomeServer.Tests.Controllers;

// Фабрика с маленьким лимитом login — отдельный сервер, чтобы тест 429
// не пересекался по rate-limit с остальными тестами
public class LowLoginLimitFactory : TestWebApplicationFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Auth:LoginRateLimit"] = "3"
            });
        });
    }
}

public class RateLimitTests : IClassFixture<LowLoginLimitFactory>
{
    private readonly HttpClient _client;

    public RateLimitTests(LowLoginLimitFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Login_ExceedsLimit_Returns429()
    {
        // лимит 3/мин — на превышении приходит 429
        var statuses = new List<HttpStatusCode>();
        for (int i = 0; i < 6; i++)
        {
            var resp = await _client.PostAsJsonAsync("/api/auth/login", new
            {
                username = "any",
                password = "any"
            });
            statuses.Add(resp.StatusCode);
        }

        statuses.Should().Contain(HttpStatusCode.TooManyRequests,
            "статусы: {0}", string.Join(",", statuses.Select(s => (int)s)));
    }
}
