using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Controllers;

/// <summary>
/// Задачи и модель архитектуры общего проекта для участника (shared-projects, S1-3c).
/// В S1 участник ЧИТАЕТ: список задач проекта и модель — 200 (от арендатора проекта,
/// <c>RequestScope.TenantOwnerId</c>), любая запись задачи и модели — 403 «нет права на
/// запись» (право записи участнику откроет S2b). Видно это только при рубильнике И флаге:
/// посторонний и участник с выключенным флагом получают 404 — существование не раскрываем.
/// Полная карточка задачи (<c>GET /api/tasks/{id}</c>) участнику ещё не открыта: её перевод — S2b.
/// </summary>
public class SharedProjectReadTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public SharedProjectReadTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
        _factory.ExtraConfig[SharedProjectsGate.SwitchKey] = "true";
    }

    private HttpClient Owner() => _factory.CreateAuthenticatedClient();

    private HttpClient Member() => _factory.CreateAuthenticatedClient(
        TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);

    private static async Task SetFlagAsync(HttpClient client, bool on) =>
        (await client.PutAsJsonAsync("/api/feature-flags/shared-projects", new { enabled = on }))
            .EnsureSuccessStatusCode();

    /// <summary>Проект владельца с задачей в нём и вторым пользователем в заданной роли
    /// (role = null — участника нет, второй пользователь остаётся посторонним).</summary>
    private async Task<(string ProjectId, string TaskId)> SharedProjectAsync(string? role)
    {
        var owner = Owner();
        await SetFlagAsync(owner, true);
        await SetFlagAsync(Member(), true);
        var dir = Path.Combine(_factory.TempDir, "sr_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);

        var created = await owner.PostAsJsonAsync("/api/projects",
            new { name = "Чтение" + Guid.NewGuid().ToString("N")[..6], rootPath = dir });
        created.EnsureSuccessStatusCode();
        var projectId = JsonSerializer.Deserialize<JsonElement>(await created.Content.ReadAsStringAsync())
            .GetProperty("id").GetString()!;

        if (role is not null)
            (await owner.PostAsJsonAsync($"/api/projects/{projectId}/members",
                new { username = TestWebApplicationFactory.SecondUsername, role })).EnsureSuccessStatusCode();

        var task = await owner.PostAsJsonAsync($"/api/projects/{projectId}/tasks", new { title = "Задача владельца" });
        task.EnsureSuccessStatusCode();
        return (projectId, IdOf(await task.Content.ReadAsStringAsync()));
    }

    private static string IdOf(string json) =>
        JsonSerializer.Deserialize<JsonElement>(json).GetProperty("id").GetString()!;

    /// <summary>Задачи проекта, которые видит вызывающий: id и заголовок.</summary>
    private static async Task<List<(string Id, string Title)>> ProjectTasksAsync(HttpClient client, string projectId)
    {
        var r = await client.GetAsync($"/api/projects/{projectId}/tasks");
        r.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<JsonElement>(await r.Content.ReadAsStringAsync())
            .EnumerateArray()
            .Select(t => (t.GetProperty("id").GetString()!, t.GetProperty("title").GetString()!))
            .ToList();
    }

    /// <summary>Отказ «нет права на запись» — та же форма, что у [ProjectScope]: 403 с этим кодом.</summary>
    private static async Task AssertDeniedAsync(HttpResponseMessage r)
    {
        r.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var body = JsonSerializer.Deserialize<JsonElement>(await r.Content.ReadAsStringAsync());
        body.GetProperty("code").GetString().Should().Be("project_scope");
    }

    /// <summary>Содержимое модели (та же форма, что в тестах вертикали). Тело PUT — объект
    /// {content, baseVersion}: сама модель как строка. PUT без такой обёртки — 400 от
    /// связывателя, и проверка отказа доступа до тела действия не дошла бы.</summary>
    private const string ModelBody = """{"state":{"model":{"systems":[],"viewLevel":"system"}},"version":0}""";

    private static async Task<HttpResponseMessage> PutModelAsync(HttpClient client, string url) =>
        await client.PutAsJsonAsync(url, new { content = ModelBody, baseVersion = (string?)null });

    // ── чтение участника, отказ записи ────────────────────────────────────────────

    [Theory]
    [InlineData("editor")]
    [InlineData("reader")]
    public async Task Участник_ВидитЗадачиПроекта_ОтАрендатора(string role)
    {
        var (projectId, taskId) = await SharedProjectAsync(role);

        var seen = await ProjectTasksAsync(Member(), projectId);

        seen.Select(t => t.Id).Should().Contain(taskId, "задачи проекта — его задачи, ключ — TenantOwnerId");
        seen.Single(t => t.Id == taskId).Title.Should().Be("Задача владельца");
    }

    [Theory]
    [InlineData("editor")]
    [InlineData("reader")]
    public async Task Участник_ПишетЗадачу_403_И_ЗадачаНеМеняется(string role)
    {
        var (projectId, taskId) = await SharedProjectAsync(role);
        var member = Member();

        await AssertDeniedAsync(await member.PostAsJsonAsync($"/api/projects/{projectId}/tasks", new { title = "Задача участника" }));
        await AssertDeniedAsync(await member.PutAsJsonAsync($"/api/tasks/{taskId}", new { title = "перебил" }));
        await AssertDeniedAsync(await member.SendAsync(new HttpRequestMessage(HttpMethod.Delete, $"/api/tasks/{taskId}")));
        await AssertDeniedAsync(await member.PostAsync($"/api/tasks/{taskId}/execute", null));

        var after = await ProjectTasksAsync(Owner(), projectId);
        after.Should().ContainSingle();
        after.Single().Title.Should().Be("Задача владельца", "отказ записи не оставил следа: ни новой задачи, ни правки, ни удаления");
    }

    [Theory]
    [InlineData("editor")]
    [InlineData("reader")]
    public async Task Участник_ЧитаетМодельАрхитектуры_Пишет_403(string role)
    {
        var (projectId, _) = await SharedProjectAsync(role);
        var member = Member();
        var url = $"/api/projects/{projectId}/architecture/model";

        var got = await member.GetAsync(url);
        got.EnsureSuccessStatusCode();
        JsonSerializer.Deserialize<JsonElement>(await got.Content.ReadAsStringAsync())
            .GetProperty("exists").GetBoolean().Should().BeFalse("модели ещё нет — её отсутствие участнику тоже видно");

        await AssertDeniedAsync(await PutModelAsync(member, url));
        await AssertDeniedAsync(await member.PostAsync($"/api/projects/{projectId}/architecture/generate", null));

        // Отказ не оставил файла: владельцу модель всё так же нечего показать
        var owner = await Owner().GetAsync(url);
        owner.EnsureSuccessStatusCode();
        JsonSerializer.Deserialize<JsonElement>(await owner.Content.ReadAsStringAsync())
            .GetProperty("exists").GetBoolean().Should().BeFalse();
    }

    // ── невидимому — 404 на все четыре вызова ─────────────────────────────────────

    private static async Task AllFourAreNotFoundAsync(HttpClient client, string projectId, string taskId)
    {
        var modelUrl = $"/api/projects/{projectId}/architecture/model";

        (await client.GetAsync($"/api/projects/{projectId}/tasks")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.PutAsJsonAsync($"/api/tasks/{taskId}", new { title = "перебил" })).StatusCode
            .Should().Be(HttpStatusCode.NotFound);
        (await client.GetAsync(modelUrl)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await PutModelAsync(client, modelUrl)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Посторонний_НеВидитНиЗадачНиМодели_404()
    {
        var (projectId, taskId) = await SharedProjectAsync(role: null);
        await AllFourAreNotFoundAsync(Member(), projectId, taskId);
    }

    [Fact]
    public async Task ФлагУчастникаВыключен_404()
    {
        var (projectId, taskId) = await SharedProjectAsync("editor");
        await SetFlagAsync(Member(), false);
        try
        {
            await AllFourAreNotFoundAsync(Member(), projectId, taskId);
        }
        finally { await SetFlagAsync(Member(), true); }
    }

    [Fact]
    public async Task РубильникВыключен_УчастникНеВидит_404()
    {
        var (projectId, taskId) = await SharedProjectAsync("editor");
        var config = _factory.Services.GetRequiredService<IConfiguration>();
        config[SharedProjectsGate.SwitchKey] = "false";
        try
        {
            await AllFourAreNotFoundAsync(Member(), projectId, taskId);
        }
        finally { config[SharedProjectsGate.SwitchKey] = "true"; }
    }

    // ── владелец: прежний путь не изменился ───────────────────────────────────────

    [Fact]
    public async Task Владелец_ЧитаетИПишетЗадачиКакРаньше()
    {
        var (projectId, taskId) = await SharedProjectAsync("editor");
        var owner = Owner();

        (await owner.PutAsJsonAsync($"/api/tasks/{taskId}", new { title = "перебил владелец" })).EnsureSuccessStatusCode();

        var after = await ProjectTasksAsync(owner, projectId);
        after.Single(t => t.Id == taskId).Title.Should().Be("перебил владелец");
        // Участник видит чужую правку своей задачи — чтение осталось открытым
        (await ProjectTasksAsync(Member(), projectId)).Single(t => t.Id == taskId).Title
            .Should().Be("перебил владелец");
    }

    [Fact]
    public async Task Владелец_ЧитаетМодельАрхитектуры_И_ПишетЕёКакРаньше()
    {
        var (projectId, _) = await SharedProjectAsync("editor");
        var url = $"/api/projects/{projectId}/architecture/model";

        (await Owner().GetAsync(url)).EnsureSuccessStatusCode();

        var put = await PutModelAsync(Owner(), url);
        put.StatusCode.Should().Be(HttpStatusCode.OK, "право записи владельца точка доступа не трогает");
        JsonSerializer.Deserialize<JsonElement>(await put.Content.ReadAsStringAsync())
            .GetProperty("version").GetString().Should().NotBeNullOrEmpty();
    }
}
