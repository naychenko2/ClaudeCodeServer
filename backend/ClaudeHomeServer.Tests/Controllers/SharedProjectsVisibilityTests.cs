using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Controllers;

// Чтение участником общего проекта (shared-projects, S1-3a): список, «Домой», файлы (чтение),
// WebDAV, роли на запись. Участник видит проект только при рубильнике И флаге; в S1 пишет
// только владелец (читатель и редактор получают 403).
public class SharedProjectsVisibilityTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public SharedProjectsVisibilityTests(TestWebApplicationFactory factory)
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

    // Проект владельца с файлом a.txt и вторым пользователем в заданной роли
    private async Task<(string Id, string Name)> SharedProjectAsync(string role)
    {
        var owner = Owner();
        await SetFlagAsync(owner, true);
        await SetFlagAsync(Second(), true);
        var name = "Общий" + Guid.NewGuid().ToString("N")[..6];
        var dir = Path.Combine(_factory.TempDir, "sv_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "a.txt"), "привет");
        var created = await owner.PostAsJsonAsync("/api/projects", new { name, rootPath = dir });
        created.EnsureSuccessStatusCode();
        var id = JsonSerializer.Deserialize<JsonElement>(await created.Content.ReadAsStringAsync())
            .GetProperty("id").GetString()!;
        (await owner.PostAsJsonAsync($"/api/projects/{id}/members",
            new { username = TestWebApplicationFactory.SecondUsername, role })).EnsureSuccessStatusCode();
        return (id, name);
    }

    private static async Task<List<string>> ProjectIdsAsync(HttpClient client)
    {
        var r = await client.GetAsync("/api/projects");
        r.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<JsonElement>(await r.Content.ReadAsStringAsync())
            .EnumerateArray().Select(e => e.GetProperty("id").GetString()!).ToList();
    }

    [Theory]
    [InlineData("editor")]
    [InlineData("reader")]
    public async Task Участник_ВидитОбщийПроектВСписке(string role)
    {
        var (id, _) = await SharedProjectAsync(role);
        (await ProjectIdsAsync(Second())).Should().Contain(id);
    }

    [Fact]
    public async Task ФлагУчастникаВыключен_ПроектНеВиден_ПоIdНе404()
    {
        var (id, _) = await SharedProjectAsync("editor");
        await SetFlagAsync(Second(), false);
        try
        {
            (await ProjectIdsAsync(Second())).Should().NotContain(id);
            (await Second().GetAsync($"/api/projects/{id}/files")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        }
        finally { await SetFlagAsync(Second(), true); }
    }

    [Fact]
    public async Task РубильникВыключен_ПроектНеВиден()
    {
        var (id, _) = await SharedProjectAsync("editor");
        var config = _factory.Services.GetRequiredService<IConfiguration>();
        config[SharedProjectsGate.SwitchKey] = "false";
        try { (await ProjectIdsAsync(Second())).Should().NotContain(id); }
        finally { config[SharedProjectsGate.SwitchKey] = "true"; }
    }

    [Fact]
    public async Task Посторонний_ПроектНеВиден()
    {
        var owner = Owner();
        var dir = Path.Combine(_factory.TempDir, "sv_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var created = await owner.PostAsJsonAsync("/api/projects", new { name = "Личный" + Guid.NewGuid().ToString("N")[..6], rootPath = dir });
        var id = JsonSerializer.Deserialize<JsonElement>(await created.Content.ReadAsStringAsync())
            .GetProperty("id").GetString()!;
        await SetFlagAsync(Second(), true);

        (await ProjectIdsAsync(Second())).Should().NotContain(id);
    }

    [Fact]
    public async Task Читатель_ЧитаетФайлы_ПишетСОтказом403()
    {
        var (id, _) = await SharedProjectAsync("reader");
        var reader = Second();

        (await reader.GetAsync($"/api/projects/{id}/files")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await reader.PutAsJsonAsync($"/api/projects/{id}/files/content?path=a.txt", new { content = "x" }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Редактор_ВS1_ПишетСОтказом403()
    {
        var (id, _) = await SharedProjectAsync("editor");

        (await Second().PutAsJsonAsync($"/api/projects/{id}/files/content?path=a.txt", new { content = "x" }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Владелец_ПишетФайлыКакРаньше()
    {
        var (id, _) = await SharedProjectAsync("editor");

        (await Owner().PutAsJsonAsync($"/api/projects/{id}/files/content?path=a.txt", new { content = "владелец" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task WebDav_УчастникЧитает_ПишетСОтказом403()
    {
        var (_, name) = await SharedProjectAsync("reader");
        var reader = Second();
        var url = $"/projects/{Uri.EscapeDataString(name)}/a.txt";

        (await reader.GetAsync(url)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await reader.PutAsync(url, new StringContent("x", Encoding.UTF8))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await reader.SendAsync(new HttpRequestMessage(HttpMethod.Delete, url))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task WebDav_Владелец_ПишетКакРаньше()
    {
        var (_, name) = await SharedProjectAsync("editor");

        (await Owner().PutAsync($"/projects/{Uri.EscapeDataString(name)}/a.txt", new StringContent("владелец", Encoding.UTF8)))
            .StatusCode.Should().BeOneOf(HttpStatusCode.NoContent, HttpStatusCode.Created, HttpStatusCode.OK);
    }
}
