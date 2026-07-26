using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Controllers;

public class AdminUserModelTiersControllerTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public AdminUserModelTiersControllerTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Get_NonAdmin_Returns403()
    {
        var client = _factory.CreateAuthenticatedClient(
            TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);
        var response = await client.GetAsync("/api/admin/users/some-id/model-tiers");
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Get_UnknownUser_Returns404()
    {
        var client = _factory.CreateAuthenticatedClient();
        var response = await client.GetAsync("/api/admin/users/no-such-id/model-tiers");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Put_UnknownUser_Returns404()
    {
        var client = _factory.CreateAuthenticatedClient();
        var response = await client.PutAsJsonAsync("/api/admin/users/no-such-id/model-tiers", new
        {
            strong = "opus",
        });
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Admin_GetPut_GetOtherUsersTiers()
    {
        var admin = _factory.CreateAuthenticatedClient();
        var userClient = _factory.CreateAuthenticatedClient(
            TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);

        // Получаем id второго пользователя через /api/auth/me
        var me = await userClient.GetAsync("/api/auth/me");
        me.StatusCode.Should().Be(HttpStatusCode.OK);
        var meBody = JsonSerializer.Deserialize<JsonElement>(await me.Content.ReadAsStringAsync());
        var userId = meBody.GetProperty("userId").GetString()!;

        var put = await admin.PutAsJsonAsync($"/api/admin/users/{userId}/model-tiers", new
        {
            strong = "admin-opus",
            medium = "admin-sonnet",
            weak = "admin-haiku",
        });
        put.StatusCode.Should().Be(HttpStatusCode.OK);

        var get = await admin.GetAsync($"/api/admin/users/{userId}/model-tiers");
        get.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonSerializer.Deserialize<JsonElement>(await get.Content.ReadAsStringAsync());
        body.GetProperty("strong").GetString().Should().Be("admin-opus");
        body.GetProperty("medium").GetString().Should().Be("admin-sonnet");
        body.GetProperty("weak").GetString().Should().Be("admin-haiku");
    }
}
