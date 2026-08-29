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

    // Контракт для раздела «Архив»: эндпоинт НЕ фильтрует архивные на сервере — они
    // приходят в общем списке с готовым bool isArchived, а фронт сам решает, что с ними
    // делать (прятать в обычных списках, показывать в «Архиве»). Это сторож: любая
    // серверная фильтрация архива здесь сломала бы раздел «Архив» (переиспользует этот
    // эндпоинт, план «показать все архивные чаты владельца»).
    [Fact]
    public async Task GetAll_ArchivedSession_ReturnedWithIsArchivedTrue()
    {
        var projectId = await CreateProjectAsync();
        var createResponse = await _client.PostAsJsonAsync($"/api/projects/{projectId}/sessions", new { mode = "auto" });
        createResponse.EnsureSuccessStatusCode();
        var sessionId = JsonSerializer.Deserialize<JsonElement>(await createResponse.Content.ReadAsStringAsync())
            .GetProperty("id").GetString()!;

        // Архивируем чат. Эндпоинт архива работает и для проектных сессий
        // (GetOwned внутри резолвит владельца через проект).
        var archiveResponse = await _client.PutAsJsonAsync($"/api/chats/{sessionId}/archived", new { archived = true });
        archiveResponse.EnsureSuccessStatusCode();

        var response = await _client.GetAsync($"/api/projects/{projectId}/sessions");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        // Архивная сессия НЕ отфильтрована на сервере — присутствует в ответе
        body.GetArrayLength().Should().Be(1);
        var archivedSession = body.EnumerateArray().Single(e => e.GetProperty("id").GetString() == sessionId);
        // Точное имя готового bool-признака архива в ответе
        archivedSession.GetProperty("isArchived").GetBoolean().Should().BeTrue();
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

    // --- Контекст чата: GET/PUT {sessionId}/context (фича chat-context, A3) ---

    private async Task<(string ProjectId, string SessionId, string ProjectDir)> CreateSessionForContextAsync()
    {
        var projectId = await CreateProjectAsync();
        var createResponse = await _client.PostAsJsonAsync($"/api/projects/{projectId}/sessions", new
        {
            mode = "auto"
        });
        createResponse.EnsureSuccessStatusCode();
        var sessionBody = JsonSerializer.Deserialize<JsonElement>(await createResponse.Content.ReadAsStringAsync());
        var sessionId = sessionBody.GetProperty("id").GetString()!;

        // Корень проекта — по id (rootPath генерировался в CreateProjectAsync)
        using var scope = _factory.Services.CreateScope();
        var projects = scope.ServiceProvider.GetRequiredService<ClaudeHomeServer.Services.ProjectManager>();
        var projectDir = projects.GetById(projectId)!.RootPath;

        return (projectId, sessionId, projectDir);
    }

    private async Task<JsonElement> PutContextAsync(string projectId, string sessionId, object payload)
    {
        var response = await _client.PutAsJsonAsync(
            $"/api/projects/{projectId}/sessions/{sessionId}/context", payload);
        response.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Context_GetPut_Roundtrip()
    {
        var (projectId, sessionId, _) = await CreateSessionForContextAsync();

        var put = await PutContextAsync(projectId, sessionId, new object[]
        {
            new { type = "file", id = "docs/readme.md", title = "README" },
            new { type = "task", id = "08f79e36-7a45-4c9a-9fb4-6de676ab9522" }
        });
        put.GetArrayLength().Should().Be(2, "PUT возвращает новый состав");
        put[0].GetProperty("type").GetString().Should().Be("file");
        put[0].GetProperty("title").GetString().Should().Be("README");

        var get = await _client.GetAsync($"/api/projects/{projectId}/sessions/{sessionId}/context");
        get.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonSerializer.Deserialize<JsonElement>(await get.Content.ReadAsStringAsync());
        body.GetArrayLength().Should().Be(2, "GET отдаёт тот же состав");
        body[0].GetProperty("type").GetString().Should().Be("file");
        body[1].GetProperty("title").ValueKind.Should().Be(JsonValueKind.Null, "title опционален");
    }

    [Fact]
    public async Task Context_Get_NonExistentSession_Returns404()
    {
        var projectId = await CreateProjectAsync();
        var response = await _client.GetAsync($"/api/projects/{projectId}/sessions/nonexistent/context");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Context_Put_EmptyList_ClearsContext()
    {
        // Идемпотентный PUT: пустой список — законная операция «очистить контекст»
        var (projectId, sessionId, _) = await CreateSessionForContextAsync();
        await PutContextAsync(projectId, sessionId, new[]
        {
            new { type = "url", id = "https://example.com" }
        });

        await PutContextAsync(projectId, sessionId, Array.Empty<object>());

        var get = await _client.GetAsync($"/api/projects/{projectId}/sessions/{sessionId}/context");
        var body = JsonSerializer.Deserialize<JsonElement>(await get.Content.ReadAsStringAsync());
        body.GetArrayLength().Should().Be(0);
    }

    [Theory]
    [InlineData("folder")]
    [InlineData("")]
    public async Task Context_Put_UnknownType_Returns400(string badType)
    {
        var (projectId, sessionId, _) = await CreateSessionForContextAsync();

        var response = await _client.PutAsJsonAsync(
            $"/api/projects/{projectId}/sessions/{sessionId}/context",
            new[] { new { type = badType, id = "x" } });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Context_Put_EmptyId_Returns400(string? badId)
    {
        var (projectId, sessionId, _) = await CreateSessionForContextAsync();

        var response = await _client.PutAsJsonAsync(
            $"/api/projects/{projectId}/sessions/{sessionId}/context",
            new[] { new { type = "file", id = badId } });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Context_Put_MoreThan50Entries_Returns400()
    {
        var (projectId, sessionId, _) = await CreateSessionForContextAsync();

        var payload = Enumerable.Range(0, 51)
            .Select(i => new { type = "url", id = $"https://example.com/{i}" })
            .ToArray();
        var response = await _client.PutAsJsonAsync(
            $"/api/projects/{projectId}/sessions/{sessionId}/context", payload);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Context_Put_Exactly50Entries_Ok()
    {
        var (projectId, sessionId, _) = await CreateSessionForContextAsync();

        var payload = Enumerable.Range(0, 50)
            .Select(i => new { type = "url", id = $"https://example.com/{i}" })
            .ToArray();
        var put = await PutContextAsync(projectId, sessionId, payload);
        put.GetArrayLength().Should().Be(50, "потолок включительно");
    }

    [Fact]
    public async Task Context_Get_MissingFile_MarkedMissing()
    {
        var (projectId, sessionId, projectDir) = await CreateSessionForContextAsync();
        // Файл существует в момент PUT (PUT существование не проверяет — только валидацию),
        // затем удаляем: GET обязан показать missing
        var filePath = Path.Combine(projectDir, "docs", "readme.md");
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        await File.WriteAllTextAsync(filePath, "# readme");
        await PutContextAsync(projectId, sessionId, new[]
        {
            new { type = "file", id = "docs/readme.md" },
            new { type = "file", id = "docs/gone.md" }
        });

        var get = await _client.GetAsync($"/api/projects/{projectId}/sessions/{sessionId}/context");
        var body = JsonSerializer.Deserialize<JsonElement>(await get.Content.ReadAsStringAsync());
        body.GetArrayLength().Should().Be(2);
        body[0].GetProperty("missing").GetBoolean().Should().BeFalse("файл на месте");
        body[1].GetProperty("missing").GetBoolean().Should().BeTrue("файла нет — missing");
    }

    [Fact]
    public async Task Context_Get_TaskOutsideProject_MarkedMissing()
    {
        // Контекст проектного чата адресуется внутри проекта: задача из другого проекта
        // (или чужая) — missing, а не молчаливое «найдено»
        var (projectId, sessionId, _) = await CreateSessionForContextAsync();
        var otherProjectId = await CreateProjectAsync();
        var otherTaskResponse = await _client.PostAsJsonAsync($"/api/projects/{otherProjectId}/tasks", new
        {
            title = "Чужая задача"
        });
        otherTaskResponse.EnsureSuccessStatusCode();
        var otherTask = JsonSerializer.Deserialize<JsonElement>(await otherTaskResponse.Content.ReadAsStringAsync());
        var otherTaskId = otherTask.GetProperty("id").GetString()!;

        await PutContextAsync(projectId, sessionId, new[]
        {
            new { type = "task", id = otherTaskId }
        });

        var get = await _client.GetAsync($"/api/projects/{projectId}/sessions/{sessionId}/context");
        var body = JsonSerializer.Deserialize<JsonElement>(await get.Content.ReadAsStringAsync());
        body[0].GetProperty("missing").GetBoolean().Should().BeTrue("задача не из этого проекта");
    }

    [Fact]
    public async Task Context_Get_FileEscapeOutsideProject_MarkedMissing()
    {
        // Путь «наружу проекта» — SafeJoin бросает, но это missing записи, а не 500 всего GET
        var (projectId, sessionId, _) = await CreateSessionForContextAsync();
        await PutContextAsync(projectId, sessionId, new[]
        {
            new { type = "file", id = "../outside.txt" }
        });

        var get = await _client.GetAsync($"/api/projects/{projectId}/sessions/{sessionId}/context");
        get.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonSerializer.Deserialize<JsonElement>(await get.Content.ReadAsStringAsync());
        body[0].GetProperty("missing").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Context_Put_NonExistentSession_Returns404()
    {
        var projectId = await CreateProjectAsync();
        var response = await _client.PutAsJsonAsync(
            $"/api/projects/{projectId}/sessions/nonexistent/context",
            new[] { new { type = "url", id = "https://example.com" } });
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
