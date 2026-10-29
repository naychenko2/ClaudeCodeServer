using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Controllers;

// Членство в общем проекте (shared-projects, S1-2): шесть отказов с кодами и нетронутым
// projects.json, права владельца/участника/постороннего, git init при первом приглашении.
public class ProjectMembersControllerTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public ProjectMembersControllerTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
        // Рубильник инстанса включаем до первого запроса (хост собирается лениво)
        _factory.ExtraConfig[SharedProjectsGate.SwitchKey] = "true";
    }

    private HttpClient Owner() => _factory.CreateAuthenticatedClient();

    private HttpClient Second() => _factory.CreateAuthenticatedClient(
        TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);

    private string IdOf(string username) =>
        _factory.Services.GetRequiredService<UserStore>().FindByUsername(username)!.Id;

    private static async Task SetFlagAsync(HttpClient client, bool on) =>
        (await client.PutAsJsonAsync("/api/feature-flags/shared-projects", new { enabled = on }))
            .EnsureSuccessStatusCode();

    private async Task<string> CreateProjectAsync(HttpClient client, string? dir = null)
    {
        dir ??= Path.Combine(_factory.TempDir, "members_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var response = await client.PostAsJsonAsync("/api/projects", new { name = "Общий", rootPath = dir });
        response.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync())
            .GetProperty("id").GetString()!;
    }

    private string StoreHash() =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(_factory.TempDir, "projects.json"))));

    private static async Task<string?> CodeOf(HttpResponseMessage r) =>
        JsonSerializer.Deserialize<JsonElement>(await r.Content.ReadAsStringAsync()) is { ValueKind: JsonValueKind.Object } j
        && j.TryGetProperty("code", out var c) ? c.GetString() : null;

    private static Task<HttpResponseMessage> AddAsync(HttpClient c, string projectId, string username, string role = "editor") =>
        c.PostAsJsonAsync($"/api/projects/{projectId}/members", new { username, role });

    // Отказ: свой статус и код, projects.json не изменился
    private async Task AssertRefusalAsync(Func<Task<HttpResponseMessage>> call, HttpStatusCode status, string code)
    {
        var before = StoreHash();
        var r = await call();
        r.StatusCode.Should().Be(status);
        (await CodeOf(r)).Should().Be(code);
        StoreHash().Should().Be(before);
    }

    [Fact]
    public async Task Добавление_РедакторПоявляетсяВСписке()
    {
        var owner = Owner(); await SetFlagAsync(owner, true); await SetFlagAsync(Second(), true);
        var id = await CreateProjectAsync(owner);

        var r = await AddAsync(owner, id, TestWebApplicationFactory.SecondUsername);

        r.StatusCode.Should().Be(HttpStatusCode.OK);
        var project = _factory.Services.GetRequiredService<ProjectManager>().GetById(id)!;
        project.Members.Should().ContainSingle(m => m.UserId == IdOf(TestWebApplicationFactory.SecondUsername));
    }

    [Fact]
    public async Task Отказ_UserNotFound()
    {
        var owner = Owner(); await SetFlagAsync(owner, true);
        var id = await CreateProjectAsync(owner);
        await AssertRefusalAsync(() => AddAsync(owner, id, "nobody_such"), HttpStatusCode.NotFound, "user_not_found");
    }

    [Fact]
    public async Task Отказ_AlreadyMember()
    {
        var owner = Owner(); await SetFlagAsync(owner, true); await SetFlagAsync(Second(), true);
        var id = await CreateProjectAsync(owner);
        (await AddAsync(owner, id, TestWebApplicationFactory.SecondUsername)).EnsureSuccessStatusCode();
        await AssertRefusalAsync(() => AddAsync(owner, id, TestWebApplicationFactory.SecondUsername),
            HttpStatusCode.Conflict, "already_member");
    }

    [Fact]
    public async Task Отказ_Self()
    {
        var owner = Owner(); await SetFlagAsync(owner, true);
        var id = await CreateProjectAsync(owner);
        await AssertRefusalAsync(() => AddAsync(owner, id, TestWebApplicationFactory.TestUsername),
            HttpStatusCode.BadRequest, "self");
    }

    [Fact]
    public async Task Отказ_FlagDisabled_УПолучателя()
    {
        var owner = Owner(); await SetFlagAsync(owner, true); await SetFlagAsync(Second(), false);
        var id = await CreateProjectAsync(owner);
        await AssertRefusalAsync(() => AddAsync(owner, id, TestWebApplicationFactory.SecondUsername),
            HttpStatusCode.Conflict, "flag_disabled");
    }

    [Fact]
    public async Task Отказ_LocalProject()
    {
        var owner = Owner(); await SetFlagAsync(owner, true); await SetFlagAsync(Second(), true);
        var dir = Path.Combine(_factory.TempDir, "local_" + Guid.NewGuid().ToString("N")[..8]);
        var local = _factory.Services.GetRequiredService<ProjectManager>()
            .CreateLocal("Локальный", dir, IdOf(TestWebApplicationFactory.TestUsername), "device-1");
        await AssertRefusalAsync(() => AddAsync(owner, local.Id, TestWebApplicationFactory.SecondUsername),
            HttpStatusCode.Conflict, "local_project");
    }

    [Fact]
    public async Task Отказ_РубильникВыключен_503()
    {
        using var off = new TestWebApplicationFactory();
        off.ExtraConfig[SharedProjectsGate.SwitchKey] = "false";
        var owner = off.CreateAuthenticatedClient();
        await SetFlagAsync(owner, true);
        var dir = Path.Combine(off.TempDir, "off_proj"); Directory.CreateDirectory(dir);
        var created = await owner.PostAsJsonAsync("/api/projects", new { name = "Закрытый", rootPath = dir });
        var id = JsonSerializer.Deserialize<JsonElement>(await created.Content.ReadAsStringAsync()).GetProperty("id").GetString()!;
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(off.TempDir, "projects.json"))));

        var r = await AddAsync(owner, id, TestWebApplicationFactory.SecondUsername);

        r.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await CodeOf(r)).Should().Be("shared_projects_disabled");
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(off.TempDir, "projects.json")))).Should().Be(hash);
    }

    [Fact]
    public async Task УчастникНаУправлении_403_Посторонний_404()
    {
        var owner = Owner(); await SetFlagAsync(owner, true); await SetFlagAsync(Second(), true);
        var id = await CreateProjectAsync(owner);
        var secondId = IdOf(TestWebApplicationFactory.SecondUsername);
        var strangerProject = await CreateProjectAsync(owner); // второй в нём не участник
        (await AddAsync(owner, id, TestWebApplicationFactory.SecondUsername)).EnsureSuccessStatusCode();
        var member = Second();

        // участник: добавить, сменить роль, убрать — 403
        (await AddAsync(member, id, TestWebApplicationFactory.TestUsername)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await member.PutAsJsonAsync($"/api/projects/{id}/members/{secondId}", new { role = "reader" }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await member.DeleteAsync($"/api/projects/{id}/members/{secondId}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // посторонний — 404 на всё, включая «покинуть»
        (await AddAsync(member, strangerProject, "x")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await member.PutAsJsonAsync($"/api/projects/{strangerProject}/members/{secondId}", new { role = "reader" }))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await member.DeleteAsync($"/api/projects/{strangerProject}/members/me")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task СменаРоли_Убрать_Покинуть()
    {
        var owner = Owner(); await SetFlagAsync(owner, true); await SetFlagAsync(Second(), true);
        var id = await CreateProjectAsync(owner);
        var secondId = IdOf(TestWebApplicationFactory.SecondUsername);
        var projects = _factory.Services.GetRequiredService<ProjectManager>();
        (await AddAsync(owner, id, TestWebApplicationFactory.SecondUsername)).EnsureSuccessStatusCode();

        (await owner.PutAsJsonAsync($"/api/projects/{id}/members/{secondId}", new { role = "reader" })).EnsureSuccessStatusCode();
        projects.GetById(id)!.Members!.Single().Role.Should().Be(ClaudeHomeServer.Services.Access.ProjectRole.Reader);

        (await Second().DeleteAsync($"/api/projects/{id}/members/me")).EnsureSuccessStatusCode();
        projects.GetById(id)!.Members.Should().BeNull();

        (await AddAsync(owner, id, TestWebApplicationFactory.SecondUsername, "reader")).EnsureSuccessStatusCode();
        (await owner.DeleteAsync($"/api/projects/{id}/members/{secondId}")).EnsureSuccessStatusCode();
        projects.GetById(id)!.Members.Should().BeNull();
    }

    [Fact]
    public async Task ПервоеДобавление_СоздаётГитРепозиторий()
    {
        var owner = Owner(); await SetFlagAsync(owner, true); await SetFlagAsync(Second(), true);
        var dir = Path.Combine(Path.GetTempPath(), "members_git_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var id = await CreateProjectAsync(owner, dir);
            Directory.Exists(Path.Combine(dir, ".git")).Should().BeFalse();

            (await AddAsync(owner, id, TestWebApplicationFactory.SecondUsername)).EnsureSuccessStatusCode();

            Directory.Exists(Path.Combine(dir, ".git")).Should().BeTrue();
        }
        finally { try { Directory.Delete(dir, true); } catch { /* не критично */ } }
    }

    [Fact]
    public async Task УдалениеВладельца_ПриЖивыхУчастниках_Отказ()
    {
        var second = Second(); var admin = Owner();
        await SetFlagAsync(second, true); await SetFlagAsync(admin, true);
        var id = await CreateProjectAsync(second);
        (await AddAsync(second, id, TestWebApplicationFactory.TestUsername)).EnsureSuccessStatusCode();

        var r = await admin.DeleteAsync($"/api/users/{IdOf(TestWebApplicationFactory.SecondUsername)}");

        r.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await CodeOf(r)).Should().Be("owner_has_members");
    }

    [Fact]
    public async Task НетПеречисленияУчёток()
    {
        var owner = Owner(); await SetFlagAsync(owner, true);
        var id = await CreateProjectAsync(owner);
        // GET на коллекцию и по имени/части имени не существует
        (await owner.GetAsync($"/api/projects/{id}/members")).IsSuccessStatusCode.Should().BeFalse();
        (await owner.GetAsync($"/api/projects/{id}/members/sec")).IsSuccessStatusCode.Should().BeFalse();
    }
}
