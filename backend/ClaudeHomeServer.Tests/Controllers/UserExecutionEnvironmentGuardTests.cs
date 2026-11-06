using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Controllers;

// Характеризационный тест ДО выноса SessionStore (S3-1): смена среды исполнения при
// существующих чатах запрещена (UsersController.Update → HasSessionsOwnedBy → 409).
[Trait("Category", "Host")]
public class UserExecutionEnvironmentGuardTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public UserExecutionEnvironmentGuardTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private async Task<string> CreateUserAsync(HttpClient admin)
    {
        var username = "envguard_" + Guid.NewGuid().ToString("N")[..8];
        var create = await admin.PostAsJsonAsync("/api/users", new
        {
            username,
            password = "password12345",
            role = "user",
            executionEnvironment = "local",
        });
        create.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await create.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("id").GetString()!;
    }

    private static Task<HttpResponseMessage> SetEnvAsync(HttpClient admin, string userId, string env) =>
        admin.PutAsJsonAsync($"/api/users/{userId}", new { executionEnvironment = env });

    [Fact]
    public async Task СменаСреды_БезЧатов_Проходит()
    {
        var admin = _factory.CreateAuthenticatedClient();
        var userId = await CreateUserAsync(admin);

        var response = await SetEnvAsync(admin, userId, "container");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task СменаСреды_ПриСуществующемЧате_409_ИСредаНеМеняется()
    {
        var admin = _factory.CreateAuthenticatedClient();
        var userId = await CreateUserAsync(admin);
        var sessions = _factory.Services.GetRequiredService<SessionManager>();
        await sessions.CreateChatAsync(userId, ClaudeMode.Auto);

        var response = await SetEnvAsync(admin, userId, "container");

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        _factory.Services.GetRequiredService<UserStore>().GetById(userId)!
            .ExecutionEnvironment.Should().Be("local");
    }

    [Fact]
    public async Task СменаСреды_ПриЧатеНоТоЖеЗначение_НеСчитаетсяСменой()
    {
        var admin = _factory.CreateAuthenticatedClient();
        var userId = await CreateUserAsync(admin);
        var sessions = _factory.Services.GetRequiredService<SessionManager>();
        await sessions.CreateChatAsync(userId, ClaudeMode.Auto);

        var response = await SetEnvAsync(admin, userId, "local");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
