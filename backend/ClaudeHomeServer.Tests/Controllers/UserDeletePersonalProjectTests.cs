using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Controllers;

// Каскад удаления пользователя (ADR-040 §3): запись «Личного» уходит из стора, папка остаётся.
// Домашняя папка пользователя задаётся переменной окружения ДО старта хоста фабрики
// (хост поднимается лениво, при первом обращении к фабрике).
[Trait("Category", "Host")]
public class UserDeletePersonalProjectTests : IClassFixture<TestWebApplicationFactory>, IDisposable
{
    private const string Username = "pcascade";
    private const string EnvKey = "Projects__UserHomeOverrides__" + Username;

    private readonly TestWebApplicationFactory _factory;
    private readonly string _home = Path.Combine(Path.GetTempPath(), "pcascade_" + Guid.NewGuid().ToString("N"));

    public UserDeletePersonalProjectTests(TestWebApplicationFactory factory)
    {
        Directory.CreateDirectory(_home);
        // Override действует только при заданной базе проектов — задаём её тем же путём
        Environment.SetEnvironmentVariable(EnvKey, _home);
        Environment.SetEnvironmentVariable("DefaultProjectsPath", _home);
        _factory = factory;
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(EnvKey, null);
        Environment.SetEnvironmentVariable("DefaultProjectsPath", null);
        try { Directory.Delete(_home, recursive: true); } catch { /* не критично */ }
    }

    [Fact]
    public async Task УдалениеПользователя_УбираетЗаписьЛичного_ПапкаОстаётся()
    {
        var admin = _factory.CreateAuthenticatedClient();
        var create = await admin.PostAsJsonAsync("/api/users", new
        {
            username = Username,
            password = "password12345",
            role = "user",
            executionEnvironment = "local",
        });
        create.StatusCode.Should().Be(HttpStatusCode.Created);
        var userId = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;

        var personal = _factory.Services.GetRequiredService<IPersonalProjects>();
        var project = await personal.EnsurePersonalAsync(userId);
        project.RootPath.Should().Be(Path.Combine(_home, "Personal"));

        var delete = await admin.DeleteAsync($"/api/users/{userId}");

        delete.StatusCode.Should().Be(HttpStatusCode.NoContent);
        personal.GetPersonalOf(userId).Should().BeNull();
        personal.GetByIdIncludingPersonal(project.Id).Should().BeNull();
        Directory.Exists(project.RootPath).Should().BeTrue();
    }
}
