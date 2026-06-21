using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeCodeServer.Tests.Helpers;
using FluentAssertions;

namespace ClaudeCodeServer.Tests.Controllers;

public class AuthControllerTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly HttpClient _client;

    public AuthControllerTests(TestWebApplicationFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Ping_ValidCredentials_Returns200()
    {
        var response = await _client.PostAsJsonAsync("/api/auth/ping", new
        {
            serverUrl = "http://localhost:5000",
            apiKey = "sk-test-key"
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("true");
    }

    [Fact]
    public async Task Ping_EmptyServerUrl_Returns400()
    {
        var response = await _client.PostAsJsonAsync("/api/auth/ping", new
        {
            serverUrl = "",
            apiKey = "sk-test"
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Ping_EmptyApiKey_Returns400()
    {
        var response = await _client.PostAsJsonAsync("/api/auth/ping", new
        {
            serverUrl = "http://localhost:5000",
            apiKey = ""
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Ping_WhitespaceValues_Returns400()
    {
        var response = await _client.PostAsJsonAsync("/api/auth/ping", new
        {
            serverUrl = "   ",
            apiKey = "   "
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
