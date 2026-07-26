using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Controllers;

public class ModelsControllerTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public ModelsControllerTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Get_WithoutToken_Returns401()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/models");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Get_AssignmentsRespectUserTierOverrides()
    {
        // Основной тестовый пользователь — admin (TestUsername), второй — user
        var admin = _factory.CreateAuthenticatedClient();
        var userClient = _factory.CreateAuthenticatedClient(
            TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);

        // Гарантируем известное состояние: у admin — личный strong, у user — наследование
        await admin.PutAsJsonAsync("/api/me/model-tiers", new { strong = "admin-opus", medium = "", weak = "" });
        await userClient.PutAsJsonAsync("/api/me/model-tiers", new { strong = "", medium = "", weak = "" });

        // GET /api/models отдаёт resolved assignments с учётом caller-id
        var response = await admin.GetAsync("/api/models");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        var assignments = body.GetProperty("assignments");
        assignments.GetProperty("chat-new").GetString().Should().Be("admin-opus");

        // У второго пользователя личного слота нет — назначение chat-new должно совпадать
        // с глобальным (null, если глобальный тоже пуст)
        var userResponse = await userClient.GetAsync("/api/models");
        userResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var userBody = JsonSerializer.Deserialize<JsonElement>(await userResponse.Content.ReadAsStringAsync());
        var userAssignments = userBody.GetProperty("assignments");
        userAssignments.GetProperty("chat-new").GetString().Should().BeNull();
    }
}
