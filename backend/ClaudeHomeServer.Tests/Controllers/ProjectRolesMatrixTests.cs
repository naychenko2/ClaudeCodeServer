using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Controllers;

// Матрица «роль × проектное действие» (ревью S1-2, M1/M2): настройки и удаление проекта — право
// Configure, только владелец; участник (редактор и читатель) получает 403 и ничего не меняет.
// Рубильник и флаг встроены в точку доступа: выключены — участнику 404 на проектных эндпоинтах.
public class ProjectRolesMatrixTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public ProjectRolesMatrixTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
        _factory.ExtraConfig[SharedProjectsGate.SwitchKey] = "true";
    }

    private HttpClient Owner() => _factory.CreateAuthenticatedClient();

    private HttpClient Second() => _factory.CreateAuthenticatedClient(
        TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);

    private static async Task SetFlagAsync(HttpClient client, bool on) =>
        (await client.PutAsJsonAsync("/api/feature-flags/shared-projects", new { enabled = on }))
            .EnsureSuccessStatusCode();

    private async Task<string> SharedProjectAsync(string role)
    {
        var owner = Owner();
        await SetFlagAsync(owner, true);
        await SetFlagAsync(Second(), true);
        var dir = Path.Combine(_factory.TempDir, "rm_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var created = await owner.PostAsJsonAsync("/api/projects",
            new { name = "Роли" + Guid.NewGuid().ToString("N")[..6], rootPath = dir });
        created.EnsureSuccessStatusCode();
        var id = JsonSerializer.Deserialize<JsonElement>(await created.Content.ReadAsStringAsync())
            .GetProperty("id").GetString()!;
        (await owner.PostAsJsonAsync($"/api/projects/{id}/members",
            new { username = TestWebApplicationFactory.SecondUsername, role })).EnsureSuccessStatusCode();
        return id;
    }

    // Проектно-админские действия: метод, путь после /api/projects/{id}, тело
    public static IEnumerable<object[]> ConfigureActions()
    {
        foreach (var role in new[] { "editor", "reader" })
        {
            yield return [role, "DELETE", "", null!];
            yield return [role, "PUT", "", new { name = "Чужое имя" }];
            yield return [role, "PUT", "/device", new { deviceId = (string?)null }];
            yield return [role, "PUT", "/hands", new { enabled = false }];
            yield return [role, "PUT", "/archive-days", new { days = 5 }];
            yield return [role, "POST", "/archive-run", new { }];
            yield return [role, "PUT", "/board-columns", new { columns = new[] { new { id = "c1", name = "Новое", category = "todo" } } }];
            yield return [role, "PUT", "/tags", new { tags = new[] { "x" } }];
            yield return [role, "POST", "/icon/mode", new { kind = "initials" }];
            yield return [role, "POST", "/git/remote", new { url = "https://example.invalid/r.git" }];
            yield return [role, "POST", "/git/init", new { }];
            yield return [role, "PUT", "/git/auto-commit", new { enabled = true }];
        }
    }

    [Theory]
    [MemberData(nameof(ConfigureActions))]
    public async Task Участник_НастройкиИУдаление_ОтказСПрава403_ПроектЖив(
        string role, string method, string path, object? body)
    {
        var id = await SharedProjectAsync(role);
        var req = new HttpRequestMessage(new HttpMethod(method), $"/api/projects/{id}{path}");
        if (body is not null) req.Content = JsonContent.Create(body);

        var resp = await Second().SendAsync(req);

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden, $"{role} {method} {path}");
        var project = _factory.Services.GetRequiredService<ProjectManager>().GetById(id);
        project.Should().NotBeNull();
        project!.Name.Should().NotBe("Чужое имя");
    }

    [Fact]
    public async Task Владелец_УдаляетПроект_204()
    {
        var id = await SharedProjectAsync("editor");

        (await Owner().DeleteAsync($"/api/projects/{id}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        _factory.Services.GetRequiredService<ProjectManager>().GetById(id).Should().BeNull();
    }

    [Theory]
    [InlineData("editor")]
    [InlineData("reader")]
    public async Task Рубильник_Выключен_УчастникуПамятьКоманды404_ЗаписьОСтаётсяИВладелецВидит(string role)
    {
        var id = await SharedProjectAsync(role);
        var second = Second();
        (await second.GetAsync($"/api/projects/{id}/team-memory")).StatusCode.Should().Be(HttpStatusCode.OK);

        var config = _factory.Services.GetRequiredService<IConfiguration>();
        config[SharedProjectsGate.SwitchKey] = "false";
        try
        {
            (await second.GetAsync($"/api/projects/{id}/team-memory")).StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await second.GetAsync($"/api/projects/{id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await Owner().GetAsync($"/api/projects/{id}/team-memory")).StatusCode.Should().Be(HttpStatusCode.OK);
            _factory.Services.GetRequiredService<ProjectManager>().GetById(id)!.Members
                .Should().Contain(m => m.Role.ToString().Equals(role, StringComparison.OrdinalIgnoreCase));
        }
        finally { config[SharedProjectsGate.SwitchKey] = "true"; }
    }

    [Fact]
    public async Task ФлагУчастникаВыключен_УчастникуПамятьКоманды404_ВладельцуНет()
    {
        var id = await SharedProjectAsync("editor");
        await SetFlagAsync(Second(), false);
        try
        {
            (await Second().GetAsync($"/api/projects/{id}/team-memory")).StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await Owner().GetAsync($"/api/projects/{id}/team-memory")).StatusCode.Should().Be(HttpStatusCode.OK);
        }
        finally { await SetFlagAsync(Second(), true); }
    }
}
