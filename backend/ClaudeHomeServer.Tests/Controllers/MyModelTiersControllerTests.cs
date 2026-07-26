using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Controllers;

public class MyModelTiersControllerTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public MyModelTiersControllerTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Get_WithoutToken_Returns401()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/me/model-tiers");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Get_ReturnsOwnTiers()
    {
        var client = _factory.CreateAuthenticatedClient();
        // Гарантируем чистое состояние — тесты разделяют одну фабрику
        await client.PutAsJsonAsync("/api/me/model-tiers", new { strong = "", medium = "", weak = "" });

        var response = await client.GetAsync("/api/me/model-tiers");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        body.GetProperty("strong").GetString().Should().BeNull();
        body.GetProperty("medium").GetString().Should().BeNull();
        body.GetProperty("weak").GetString().Should().BeNull();
    }

    [Fact]
    public async Task Put_UpdatesOnlyOwnTiers()
    {
        var client = _factory.CreateAuthenticatedClient();

        var response = await client.PutAsJsonAsync("/api/me/model-tiers", new
        {
            strong = "opus",
            medium = "sonnet",
            weak = "haiku",
        });
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        body.GetProperty("strong").GetString().Should().Be("opus");
        body.GetProperty("medium").GetString().Should().Be("sonnet");
        body.GetProperty("weak").GetString().Should().Be("haiku");

        // Повторный GET отдаёт сохранённые значения
        var get = await client.GetAsync("/api/me/model-tiers");
        get.StatusCode.Should().Be(HttpStatusCode.OK);
        var getBody = JsonSerializer.Deserialize<JsonElement>(await get.Content.ReadAsStringAsync());
        getBody.GetProperty("strong").GetString().Should().Be("opus");
    }

    [Fact]
    public async Task Put_PatchWithNull_DoesNotTouchOthers()
    {
        var client = _factory.CreateAuthenticatedClient();
        await client.PutAsJsonAsync("/api/me/model-tiers", new { strong = "opus", medium = "sonnet", weak = "haiku" });

        var response = await client.PutAsJsonAsync("/api/me/model-tiers", new { medium = (string?)null });
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        body.GetProperty("strong").GetString().Should().Be("opus");
        body.GetProperty("medium").GetString().Should().Be("sonnet");
        body.GetProperty("weak").GetString().Should().Be("haiku");
    }

    [Fact]
    public async Task Put_EmptyString_ClearsSlot()
    {
        var client = _factory.CreateAuthenticatedClient();
        await client.PutAsJsonAsync("/api/me/model-tiers", new { strong = "opus" });

        var response = await client.PutAsJsonAsync("/api/me/model-tiers", new { strong = "" });
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        body.GetProperty("strong").GetString().Should().BeNull();
    }
}
