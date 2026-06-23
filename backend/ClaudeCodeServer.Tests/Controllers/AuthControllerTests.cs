using System.Net;
using System.Net.Http.Json;
using ClaudeCodeServer.Tests.Helpers;
using FluentAssertions;

namespace ClaudeCodeServer.Tests.Controllers;

public class AuthControllerTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public AuthControllerTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient(); // без заголовка — ключ проверяется из тела
    }

    [Fact]
    public async Task Ping_ValidKey_Returns200()
    {
        var response = await _client.PostAsJsonAsync("/api/auth/ping", new
        {
            serverUrl = "http://localhost:5000",
            apiKey = TestWebApplicationFactory.ApiKey
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("true");
    }

    [Fact]
    public async Task Ping_WrongKey_Returns401()
    {
        var response = await _client.PostAsJsonAsync("/api/auth/ping", new
        {
            serverUrl = "http://localhost:5000",
            apiKey = "wrong-key"
        });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Ping_EmptyKey_Returns401()
    {
        var response = await _client.PostAsJsonAsync("/api/auth/ping", new
        {
            serverUrl = "http://localhost:5000",
            apiKey = ""
        });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Ping_KeyFromBearerHeader_Returns200()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/ping")
        {
            Content = JsonContent.Create(new { serverUrl = "http://localhost:5000" })
        };
        request.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", TestWebApplicationFactory.ApiKey);

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ProtectedEndpoint_WithoutKey_Returns401()
    {
        var response = await _client.GetAsync("/api/projects");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ProtectedEndpoint_WithKey_Returns200()
    {
        var authed = _factory.CreateAuthenticatedClient();
        var response = await authed.GetAsync("/api/projects");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
