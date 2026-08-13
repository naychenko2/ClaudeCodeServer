using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Controllers;

public class SessionsControllerTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly HttpClient _client;
    private readonly TestWebApplicationFactory _factory;
    private readonly string _tempDir;

    public SessionsControllerTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateAuthenticatedClient();
        _tempDir = Path.Combine(factory.TempDir, "session_tests");
        Directory.CreateDirectory(_tempDir);
    }

    private async Task<string> CreateProjectAsync()
    {
        var dir = Path.Combine(_tempDir, "proj_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var response = await _client.PostAsJsonAsync("/api/projects", new
        {
            name = "SessionProject",
            rootPath = dir
        });
        response.EnsureSuccessStatusCode();
        var json = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        return json.GetProperty("id").GetString()!;
    }

    [Fact]
    public async Task GetAll_ExistingProject_Returns200EmptyArray()
    {
        var projectId = await CreateProjectAsync();
        var response = await _client.GetAsync($"/api/projects/{projectId}/sessions");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        body.GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Create_NonExistentProject_Returns404()
    {
        var response = await _client.PostAsJsonAsync("/api/projects/nonexistent/sessions", new
        {
            mode = "auto"
        });
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetHistory_NonExistentSession_Returns404()
    {
        var projectId = await CreateProjectAsync();
        var response = await _client.GetAsync($"/api/projects/{projectId}/sessions/nonexistent/history");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetHistory_SessionBelongsToDifferentProject_Returns404()
    {
        // Тест проверяет что GetHistory валидирует projectId
        var projectId1 = await CreateProjectAsync();
        var projectId2 = await CreateProjectAsync();

        // Получаем историю несуществующей сессии из другого проекта
        var response = await _client.GetAsync($"/api/projects/{projectId1}/sessions/fake-session/history");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Delete_NonExistentSession_Returns204()
    {
        // DELETE всегда возвращает 204, даже если сессия не найдена
        var projectId = await CreateProjectAsync();
        var response = await _client.DeleteAsync($"/api/projects/{projectId}/sessions/nonexistent");
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Create_ValidProject_Returns201WithSession()
    {
        var projectId = await CreateProjectAsync();

        var response = await _client.PostAsJsonAsync($"/api/projects/{projectId}/sessions", new
        {
            mode = "auto"
        });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        body.GetProperty("id").GetString().Should().NotBeNullOrEmpty();
        body.GetProperty("projectId").GetString().Should().Be(projectId);
        body.GetProperty("mode").GetString().Should().Be("auto");
    }

    [Fact]
    public async Task GetAll_AfterCreatingSession_ReturnsSession()
    {
        var projectId = await CreateProjectAsync();
        await _client.PostAsJsonAsync($"/api/projects/{projectId}/sessions", new { mode = "auto" });

        var response = await _client.GetAsync($"/api/projects/{projectId}/sessions");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        body.GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task GetAll_MultipleSessionsCreated_ReturnsAllSessions()
    {
        var projectId = await CreateProjectAsync();
        await _client.PostAsJsonAsync($"/api/projects/{projectId}/sessions", new { mode = "auto" });
        await _client.PostAsJsonAsync($"/api/projects/{projectId}/sessions", new { mode = "plan" });
        await _client.PostAsJsonAsync($"/api/projects/{projectId}/sessions", new { mode = "ask" });

        var response = await _client.GetAsync($"/api/projects/{projectId}/sessions");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        body.GetArrayLength().Should().Be(3);
    }

    [Fact]
    public async Task GetHistory_ExistingSession_Returns200WithEmptyHistory()
    {
        var projectId = await CreateProjectAsync();
        var createResponse = await _client.PostAsJsonAsync($"/api/projects/{projectId}/sessions", new { mode = "auto" });
        var sessionBody = JsonSerializer.Deserialize<JsonElement>(await createResponse.Content.ReadAsStringAsync());
        var sessionId = sessionBody.GetProperty("id").GetString()!;

        var response = await _client.GetAsync($"/api/projects/{projectId}/sessions/{sessionId}/history");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        body.GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Delete_ExistingSession_Returns204AndRemovedFromGetAll()
    {
        var projectId = await CreateProjectAsync();
        var createResponse = await _client.PostAsJsonAsync($"/api/projects/{projectId}/sessions", new { mode = "auto" });
        var sessionBody = JsonSerializer.Deserialize<JsonElement>(await createResponse.Content.ReadAsStringAsync());
        var sessionId = sessionBody.GetProperty("id").GetString()!;

        var deleteResponse = await _client.DeleteAsync($"/api/projects/{projectId}/sessions/{sessionId}");
        deleteResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var listResponse = await _client.GetAsync($"/api/projects/{projectId}/sessions");
        var list = JsonSerializer.Deserialize<JsonElement>(await listResponse.Content.ReadAsStringAsync());
        list.GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Create_WithName_ReturnsSessionWithName()
    {
        var projectId = await CreateProjectAsync();

        var response = await _client.PostAsJsonAsync($"/api/projects/{projectId}/sessions", new
        {
            mode = "auto",
            name = "Тестовый чат"
        });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        body.GetProperty("name").GetString().Should().Be("Тестовый чат");
    }

    // Посев истории напрямую через ChatHistoryService ДО создания сессии: StartNewSessionAsync
    // при resumeSessionId грузит существующий history.json в аккумулятор, и GetHistoryAsync
    // отдаёт его — без реального запуска хода и claude.exe. csid валиден по IsSafeSessionId.
    private async Task<string> SeedHistoryAndCreateSessionAsync(string projectId, int messageCount)
    {
        var csid = "history-" + Guid.NewGuid().ToString("N")[..16];
        using var scope = _factory.Services.CreateScope();
        var historySvc = scope.ServiceProvider.GetRequiredService<ChatHistoryService>();
        var messages = Enumerable.Range(0, messageCount)
            .Select(i => (StoredMessage)new StoredTextMessage($"msg-{i}"))
            .ToList();
        await historySvc.SaveAsync(csid, messages);

        var createResponse = await _client.PostAsJsonAsync($"/api/projects/{projectId}/sessions", new
        {
            mode = "auto",
            resumeSessionId = csid
        });
        createResponse.EnsureSuccessStatusCode();
        var body = JsonSerializer.Deserialize<JsonElement>(await createResponse.Content.ReadAsStringAsync());
        return body.GetProperty("id").GetString()!;
    }

    [Fact]
    public async Task GetHistory_WithoutParams_ReturnsFlatArray_BackwardCompat()
    {
        var projectId = await CreateProjectAsync();
        var sessionId = await SeedHistoryAndCreateSessionAsync(projectId, messageCount: 150);

        // Старый контракт: без параметров — полный плоский массив (не объект с messages/hasMore)
        var response = await _client.GetAsync($"/api/projects/{projectId}/sessions/{sessionId}/history");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        body.ValueKind.Should().Be(JsonValueKind.Array, "без параметров пагинации — прежний плоский контракт");
        body.GetArrayLength().Should().Be(150);
    }

    [Fact]
    public async Task GetHistory_WithLimit_ReturnsTailPageWithCursor()
    {
        var projectId = await CreateProjectAsync();
        var sessionId = await SeedHistoryAndCreateSessionAsync(projectId, messageCount: 150);

        var response = await _client.GetAsync($"/api/projects/{projectId}/sessions/{sessionId}/history?limit=100");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        body.ValueKind.Should().Be(JsonValueKind.Object, "с limit — постраничный объект");
        body.GetProperty("messages").GetArrayLength().Should().Be(100);
        body.GetProperty("hasMore").GetBoolean().Should().BeTrue();
        body.GetProperty("cursor").GetInt32().Should().Be(50);
    }

    [Fact]
    public async Task GetHistory_WithBefore_LoadsEarlierBatchToStart()
    {
        var projectId = await CreateProjectAsync();
        var sessionId = await SeedHistoryAndCreateSessionAsync(projectId, messageCount: 150);

        // Догрузка по курсору 50 из хвоста → последние перед ним 100 сообщений [0..49] здесь,
        // т.к. до курсора всего 50. Это финальная пачка: hasMore=false, cursor=null.
        var response = await _client.GetAsync(
            $"/api/projects/{projectId}/sessions/{sessionId}/history?limit=100&before=50");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        body.GetProperty("messages").GetArrayLength().Should().Be(50);
        body.GetProperty("hasMore").GetBoolean().Should().BeFalse();
        body.GetProperty("cursor").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task GetHistory_InvalidBefore_Returns400()
    {
        var projectId = await CreateProjectAsync();
        var sessionId = await SeedHistoryAndCreateSessionAsync(projectId, messageCount: 10);

        // before за пределами истории — 400 (несуществующий индекс)
        var response = await _client.GetAsync(
            $"/api/projects/{projectId}/sessions/{sessionId}/history?before=999");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // --- POST /api/sessions/{sid}/pending/preempt (кнопка «Прервать и отправить») ---

    [Fact]
    public async Task PreemptForPending_НесуществующаяСессия_Returns404()
    {
        var response = await _client.PostAsync("/api/sessions/nonexistent/pending/preempt", null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task PreemptForPending_СвободныйЧатБезОчереди_Returns409()
    {
        // Ход не идёт и доставлять нечего — прерывать нечего. Отказ должен быть явным:
        // молчаливое 204 обмануло бы клиент, и тот нарисовал бы «Прервано» на пустом месте.
        var projectId = await CreateProjectAsync();
        var sessionId = await SeedHistoryAndCreateSessionAsync(projectId, messageCount: 1);

        var response = await _client.PostAsync($"/api/sessions/{sessionId}/pending/preempt", null);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }
}
