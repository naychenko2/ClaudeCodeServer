using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Controllers;

public class PushControllerTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public PushControllerTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateAuthenticatedClient();
    }

    private string StorePath => Path.Combine(_factory.TempDir, "push-subscriptions.json");

    private static object ValidSubscription(string endpoint) => new
    {
        endpoint,
        p256dh = "test-p256dh-key",
        auth = "test-auth-secret"
    };

    [Fact]
    public async Task VapidPublicKey_ВозвращаетКлюч()
    {
        var response = await _client.GetAsync("/api/push/vapid-public-key");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("publicKey").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Subscribe_ВалиднаяПодписка_204_ИПерсистится()
    {
        var endpoint = "https://push.example/" + Guid.NewGuid().ToString("N");

        var response = await _client.PostAsJsonAsync("/api/push/subscribe", ValidSubscription(endpoint));

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        // Подписка сохранена в data/push-subscriptions.json (temp-директория фабрики)
        File.Exists(StorePath).Should().BeTrue();
        File.ReadAllText(StorePath).Should().Contain(endpoint);
    }

    [Theory]
    [InlineData(null, "p", "a")]   // нет endpoint
    [InlineData("https://e", null, "a")]   // нет p256dh
    [InlineData("https://e", "p", null)]   // нет auth
    [InlineData("  ", "p", "a")]   // пустой endpoint
    public async Task Subscribe_НеполноеТело_400(string? endpoint, string? p256dh, string? auth)
    {
        var response = await _client.PostAsJsonAsync("/api/push/subscribe", new { endpoint, p256dh, auth });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Subscribe_ПовторноТотЖеEndpoint_НеДублирует()
    {
        var endpoint = "https://push.example/" + Guid.NewGuid().ToString("N");

        await _client.PostAsJsonAsync("/api/push/subscribe", ValidSubscription(endpoint));
        await _client.PostAsJsonAsync("/api/push/subscribe", ValidSubscription(endpoint));

        var json = File.ReadAllText(StorePath);
        json.Split(endpoint).Length.Should().Be(2, "endpoint должен встречаться в файле один раз");
    }

    [Fact]
    public async Task Unsubscribe_УбираетПодпискуИзХранилища()
    {
        var endpoint = "https://push.example/" + Guid.NewGuid().ToString("N");
        await _client.PostAsJsonAsync("/api/push/subscribe", ValidSubscription(endpoint));

        var response = await _client.PostAsJsonAsync("/api/push/unsubscribe", new { endpoint });

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        File.ReadAllText(StorePath).Should().NotContain(endpoint);
    }

    [Fact]
    public async Task Unsubscribe_БезEndpoint_400()
    {
        var response = await _client.PostAsJsonAsync("/api/push/unsubscribe", new { endpoint = "" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
