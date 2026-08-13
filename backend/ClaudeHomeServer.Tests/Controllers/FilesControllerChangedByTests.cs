using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Controllers;

// POST /api/projects/{id}/files/changed-by — панель «Изменения»: для присланных путей
// отдаёт, какие ещё чаты проекта их меняли.
public class FilesControllerChangedByTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly HttpClient _client;
    private readonly TestWebApplicationFactory _factory;
    private readonly string _tempDir;

    public FilesControllerChangedByTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateAuthenticatedClient();
        _tempDir = Path.Combine(factory.TempDir, "changed_by_tests");
        Directory.CreateDirectory(_tempDir);
    }

    private async Task<string> CreateProjectAsync(string name = "ChangedByProject")
    {
        var dir = Path.Combine(_tempDir, "proj_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var response = await _client.PostAsJsonAsync("/api/projects", new { name, rootPath = dir });
        response.EnsureSuccessStatusCode();
        var json = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        return json.GetProperty("id").GetString()!;
    }

    // Посев истории напрямую через ChatHistoryService ДО создания сессии (см.
    // SessionsControllerTests.SeedHistoryAndCreateSessionAsync — тот же приём): без
    // реального запуска хода и claude.exe. csid валиден по IsSafeSessionId.
    private async Task<string> CreateSessionWithHistoryAsync(string projectId, string csidSuffix, string? name, params StoredMessage[] messages)
    {
        var csid = "cby-" + csidSuffix + "-" + Guid.NewGuid().ToString("N")[..8];
        using var scope = _factory.Services.CreateScope();
        var historySvc = scope.ServiceProvider.GetRequiredService<ChatHistoryService>();
        await historySvc.SaveAsync(csid, messages.ToList());

        var response = await _client.PostAsJsonAsync($"/api/projects/{projectId}/sessions", new
        {
            mode = "auto",
            resumeSessionId = csid,
            name,
        });
        response.EnsureSuccessStatusCode();
        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        return body.GetProperty("id").GetString()!;
    }

    private async Task<JsonElement> ChangedByAsync(string projectId, params string[] paths)
    {
        var response = await _client.PostAsJsonAsync($"/api/projects/{projectId}/files/changed-by", new { paths });
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ChangedBy_NonExistentProject_Returns404()
    {
        var response = await _client.PostAsJsonAsync("/api/projects/nonexistent/files/changed-by",
            new { paths = new[] { "a.ts" } });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // Поверхность раскрывает id и имена чужих чатов — ownership обязателен явным тестом,
    // как у соседних экшенов (GetProject: чужой владелец → KeyNotFoundException → 404)
    [Fact]
    public async Task ChangedBy_ЧужойВладелец_Returns404()
    {
        var projectId = await CreateProjectAsync();
        var other = _factory.CreateAuthenticatedClient(
            TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);

        var response = await other.PostAsJsonAsync($"/api/projects/{projectId}/files/changed-by",
            new { paths = new[] { "a.ts" } });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ChangedBy_ЧатМенялФайл_ВозвращаетСессиюИИмя()
    {
        var projectId = await CreateProjectAsync();
        var sessionId = await CreateSessionWithHistoryAsync(projectId, "own", "Правки конфига",
            new StoredFileChangedMessage("src/app.ts", 3, 1));

        var body = await ChangedByAsync(projectId, "src/app.ts");

        var files = body.GetProperty("files");
        files.TryGetProperty("src/app.ts", out var entries).Should().BeTrue();
        entries.GetArrayLength().Should().Be(1);
        entries[0].GetProperty("sessionId").GetString().Should().Be(sessionId);
        entries[0].GetProperty("name").GetString().Should().Be("Правки конфига");
    }

    [Fact]
    public async Task ChangedBy_ПутьНеЗапрошен_НеВозвращается()
    {
        var projectId = await CreateProjectAsync();
        await CreateSessionWithHistoryAsync(projectId, "extra", "Чат",
            new StoredFileChangedMessage("src/a.ts", 1, 0),
            new StoredFileChangedMessage("src/b.ts", 1, 0));

        var body = await ChangedByAsync(projectId, "src/a.ts");

        var files = body.GetProperty("files");
        files.TryGetProperty("src/a.ts", out _).Should().BeTrue();
        files.TryGetProperty("src/b.ts", out _).Should().BeFalse();
    }

    [Fact]
    public async Task ChangedBy_НетСовпадений_ReturnsEmptyFiles()
    {
        var projectId = await CreateProjectAsync();
        await CreateSessionWithHistoryAsync(projectId, "none", "Чат",
            new StoredFileChangedMessage("src/a.ts", 1, 0));

        var body = await ChangedByAsync(projectId, "src/unrelated.ts");

        body.GetProperty("files").EnumerateObject().Should().BeEmpty();
    }

    // Ключи ответа — РОВНО присланные строки (не lowercase-нормализованные): сравнение
    // с индексом идёт по lowercase, но наружу возвращается исходный регистр запроса
    [Fact]
    public async Task ChangedBy_КлючиОтвета_ВИсходномРегистреЗапроса()
    {
        var projectId = await CreateProjectAsync();
        await CreateSessionWithHistoryAsync(projectId, "case", "Чат",
            new StoredFileChangedMessage("src/app.ts", 1, 0));

        var body = await ChangedByAsync(projectId, "SRC/App.TS");

        var files = body.GetProperty("files");
        files.TryGetProperty("SRC/App.TS", out var entries).Should().BeTrue("ключ — как в запросе, не lowercase");
        entries.GetArrayLength().Should().Be(1);
    }

    // Чужой (второй) чат тоже менял тот же файл — оба попадают в ответ
    [Fact]
    public async Task ChangedBy_НесколькоЧатов_ВозвращаетВсех()
    {
        var projectId = await CreateProjectAsync();
        var s1 = await CreateSessionWithHistoryAsync(projectId, "m1", "Первый",
            new StoredFileChangedMessage("src/shared.ts", 1, 0));
        var s2 = await CreateSessionWithHistoryAsync(projectId, "m2", "Второй",
            new StoredFileChangedMessage("src/shared.ts", 2, 0));

        var body = await ChangedByAsync(projectId, "src/shared.ts");

        var entries = body.GetProperty("files").GetProperty("src/shared.ts");
        entries.GetArrayLength().Should().Be(2);
        var ids = entries.EnumerateArray().Select(e => e.GetProperty("sessionId").GetString()).ToList();
        ids.Should().BeEquivalentTo([s1, s2]);
    }
}
