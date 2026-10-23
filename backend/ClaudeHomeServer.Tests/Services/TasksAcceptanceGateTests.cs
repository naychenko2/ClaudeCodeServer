using System.Net;
using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Services;

// Критерии приёмки: один гейт в TaskManager, одинаковый отказ через MCP (Deny) и REST (400).
[Trait("Category", "Integration")]
public class TasksAcceptanceGateTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;
    private readonly HttpClient _client;
    private readonly string _projectDir;

    public TasksAcceptanceGateTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateAuthenticatedClient();
        _projectDir = Path.Combine(factory.TempDir, "tasks_acceptance_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_projectDir);
    }

    // Одна папка — один проект на владельца: проект заводим один раз, сессий в нём можно несколько
    private string? _projectId;

    private async Task<string> CreateSessionAsync()
    {
        if (_projectId is null)
        {
            var project = await _client.PostAsJsonAsync("/api/projects",
                new { name = "TasksAcceptance", rootPath = _projectDir, createDirectory = true });
            project.EnsureSuccessStatusCode();
            _projectId = JsonSerializer.Deserialize<JsonElement>(await project.Content.ReadAsStringAsync())
                .GetProperty("id").GetString()!;
        }
        var projectId = _projectId;
        var session = await _client.PostAsJsonAsync($"/api/projects/{projectId}/sessions", new { mode = "acceptEdits" });
        session.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<JsonElement>(await session.Content.ReadAsStringAsync())
            .GetProperty("id").GetString()!;
    }

    private async Task<(string Text, bool IsError)> CallToolAsync(string sessionId, string tool, object args)
    {
        var resp = await _client.PostAsync($"/mcp/tasks/{sessionId}",
            new StringContent(JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0", id = 1, method = "tools/call",
                @params = new { name = tool, arguments = args },
            }), System.Text.Encoding.UTF8, "application/json"));
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync()).GetProperty("result");
        var isError = result.TryGetProperty("isError", out var e) && e.GetBoolean();
        return (result.GetProperty("content").EnumerateArray().First().GetProperty("text").GetString()!, isError);
    }

    private async Task<string> CreateTaskWithThreeCriteriaAsync(string sessionId)
    {
        var (text, isError) = await CallToolAsync(sessionId, "tasks_create", new
        {
            title = "С критериями",
            acceptance = new[] { new { text = "первый" }, new { text = "второй" }, new { text = "третий" } },
        });
        isError.Should().BeFalse(text);
        return JsonSerializer.Deserialize<JsonElement>(text).GetProperty("id").GetString()!;
    }

    [Fact]
    public async Task Complete_БезСвидетельств_DenyСПеречнем_ИЗаТемЗакрываетсяСТремя()
    {
        var sessionId = await CreateSessionAsync();
        var id = await CreateTaskWithThreeCriteriaAsync(sessionId);

        var (denyText, isError) = await CallToolAsync(sessionId, "tasks_complete", new { id });
        isError.Should().BeTrue();
        denyText.Should().Contain("c1").And.Contain("c2").And.Contain("c3");

        var (okText, okErr) = await CallToolAsync(sessionId, "tasks_complete", new
        {
            id,
            evidence = new[]
            {
                new { criterionId = "c1", how = "тест", result = "зелёный" },
                new { criterionId = "c2", how = "тест", result = "зелёный" },
                new { criterionId = "c3", how = "тест", result = "зелёный" },
            },
        });
        okErr.Should().BeFalse(okText);
        JsonSerializer.Deserialize<JsonElement>(okText).GetProperty("evidence").GetArrayLength().Should().Be(3);
    }

    [Fact]
    public async Task Rest_ЗакрытиеБезСвидетельств_400СТемЖеПеречнем()
    {
        var sessionId = await CreateSessionAsync();
        var id = await CreateTaskWithThreeCriteriaAsync(sessionId);

        var resp = await _client.PutAsJsonAsync($"/api/tasks/{id}", new { status = "done" });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain("c1").And.Contain("c2").And.Contain("c3");

        var ok = await _client.PutAsJsonAsync($"/api/tasks/{id}", new
        {
            status = "done",
            evidence = new[]
            {
                new { criterionId = "c1", how = "h", result = "r" },
                new { criterionId = "c2", how = "h", result = "r" },
                new { criterionId = "c3", how = "h", result = "r" },
            },
        });
        ok.StatusCode.Should().Be(HttpStatusCode.OK, await ok.Content.ReadAsStringAsync());
    }

    // Сессия, РЕАЛЬНО привязанная к задаче (Session.TaskId) — чат-исполнитель
    private void BindToTask(string sessionId, string taskId) =>
        _factory.Services.GetRequiredService<SessionManager>().GetById(sessionId)!.TaskId = taskId;

    // Клиент с сервисным JWT того же владельца (как у окружения исполнителя, CCS_HOOK_TOKEN)
    private HttpClient ServiceClient(string? callerSessionId)
    {
        var userId = new JwtSecurityTokenHandler()
            .ReadJwtToken(_client.DefaultRequestHeaders.Authorization!.Parameter)
            .Subject;
        var token = _factory.Services.GetRequiredService<JwtService>().IssueServiceToken(userId);
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (callerSessionId is not null) client.DefaultRequestHeaders.Add("X-Caller-Session-Id", callerSessionId);
        return client;
    }

    private static readonly object[] RewrittenCriteria =
        [new { id = "c1", text = "первый" }, new { id = "c2", text = "второй" }];

    [Fact]
    public async Task Mcp_Исполнитель_НеМеняетКритерии_ПостановщикМеняет()
    {
        var setter = await CreateSessionAsync();
        var executor = await CreateSessionAsync();
        var id = await CreateTaskWithThreeCriteriaAsync(setter);
        BindToTask(executor, id);

        var (denyText, denied) = await CallToolAsync(executor, "tasks_update", new { id, acceptance = RewrittenCriteria });
        denied.Should().BeTrue(denyText);
        denyText.Should().Contain("acceptance");
        var (clearText, clearDenied) = await CallToolAsync(executor, "tasks_update", new { id, acceptance = Array.Empty<object>() });
        clearDenied.Should().BeTrue(clearText);

        var (okText, isError) = await CallToolAsync(setter, "tasks_update", new { id, acceptance = RewrittenCriteria });
        isError.Should().BeFalse(okText);
        JsonSerializer.Deserialize<JsonElement>(okText).GetProperty("acceptance").GetArrayLength().Should().Be(2);
    }

    [Fact]
    public async Task Rest_СервисныйТокен_ИсполнительИБезСессии_400_ПостановщикИЧеловек_200()
    {
        var setter = await CreateSessionAsync();
        var executor = await CreateSessionAsync();
        var id = await CreateTaskWithThreeCriteriaAsync(setter);
        BindToTask(executor, id);
        var body = new { acceptance = RewrittenCriteria };

        // исполнитель: сервисный токен + сессия, привязанная к задаче
        (await ServiceClient(executor).PutAsJsonAsync($"/api/tasks/{id}", body))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        // fail-closed: сервисный токен без заголовка и с несуществующей сессией
        (await ServiceClient(null).PutAsJsonAsync($"/api/tasks/{id}", body))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ServiceClient("нет-такой").PutAsJsonAsync($"/api/tasks/{id}", body))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // постановщик: сервисный токен + сессия без привязки к задаче
        var ok = await ServiceClient(setter).PutAsJsonAsync($"/api/tasks/{id}", body);
        ok.StatusCode.Should().Be(HttpStatusCode.OK, await ok.Content.ReadAsStringAsync());
        // человек: пользовательский JWT, заголовка нет
        var human = await _client.PutAsJsonAsync($"/api/tasks/{id}", new { acceptance = new[] { new { id = "c1", text = "первый" } } });
        human.StatusCode.Should().Be(HttpStatusCode.OK, await human.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task СменаТекстаКритерия_СбрасываетЕгоСвидетельство()
    {
        var sessionId = await CreateSessionAsync();
        var id = await CreateTaskWithThreeCriteriaAsync(sessionId);
        var evidence = new[]
        {
            new { criterionId = "c1", how = "тест", result = "зелёный" },
            new { criterionId = "c2", how = "тест", result = "зелёный" },
            new { criterionId = "c3", how = "тест", result = "зелёный" },
        };
        var (e1, err1) = await CallToolAsync(sessionId, "tasks_update", new { id, evidence });
        err1.Should().BeFalse(e1);

        var (u, err2) = await CallToolAsync(sessionId, "tasks_update", new
        {
            id,
            acceptance = new[]
            {
                new { id = "c1", text = "первый" }, new { id = "c2", text = "второй, но другой смысл" },
                new { id = "c3", text = "третий" },
            },
        });
        err2.Should().BeFalse(u);
        JsonSerializer.Deserialize<JsonElement>(u).GetProperty("evidence").GetArrayLength().Should().Be(2);

        var (denyText, isError) = await CallToolAsync(sessionId, "tasks_complete", new { id });
        isError.Should().BeTrue();
        denyText.Should().Contain("c2").And.NotContain("c1:").And.NotContain("c3:");
    }

    [Fact]
    public async Task Rest_СервисныйТокен_СессияЧужогоВладельца_Исполнитель_400()
    {
        var setter = await CreateSessionAsync();
        var foreign = await CreateSessionAsync();
        var id = await CreateTaskWithThreeCriteriaAsync(setter);
        // Сессия чужого владельца (вне проекта), к задаче не привязана: по ней нельзя «стать постановщиком»
        var info = _factory.Services.GetRequiredService<SessionManager>().GetById(foreign)!;
        info.ProjectId = null;
        info.OwnerId = "чужой-владелец";

        var resp = await ServiceClient(foreign).PutAsJsonAsync($"/api/tasks/{id}", new { acceptance = RewrittenCriteria });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest, "fail-closed: чужая сессия не смягчает правило");
    }

    // Причина возврата: одинаковые правила на MCP (Deny) и REST (400), валидная причина сохраняется
    [Fact]
    public async Task ПричинаВозврата_McpИRest_ОдинаковыеПравила()
    {
        var sessionId = await CreateSessionAsync();
        async Task<string> NewTaskId()
        {
            var (t, err) = await CallToolAsync(sessionId, "tasks_create", new { title = "ревью" });
            err.Should().BeFalse(t);
            return JsonSerializer.Deserialize<JsonElement>(t).GetProperty("id").GetString()!;
        }

        // MCP: причина при accepted и вне списка — отказ; returned + size — сохраняется
        var id = await NewTaskId();
        var (t1, e1) = await CallToolAsync(sessionId, "tasks_complete", new { id, verification = new { verdict = "accepted", returnReason = "size" } });
        e1.Should().BeTrue(t1);
        var (t2, e2) = await CallToolAsync(sessionId, "tasks_complete", new { id, verification = new { verdict = "returned", returnReason = "придумал" } });
        e2.Should().BeTrue(t2);
        var (t3, e3) = await CallToolAsync(sessionId, "tasks_complete", new { id, verification = new { verdict = "returned", returnReason = "contract", notes = "n" } });
        e3.Should().BeFalse(t3);
        JsonSerializer.Deserialize<JsonElement>(t3).GetProperty("verification").GetProperty("returnReason").GetString().Should().Be("contract");

        // REST: те же отказы и то же сохранение
        var rid = await NewTaskId();
        (await _client.PutAsJsonAsync($"/api/tasks/{rid}", new { status = "done", verification = new { verdict = "accepted", returnReason = "size" } }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await _client.PutAsJsonAsync($"/api/tasks/{rid}", new { status = "done", verification = new { verdict = "returned", returnReason = "other" } }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest, "other требует notes");
        var ok = await _client.PutAsJsonAsync($"/api/tasks/{rid}", new { status = "done", verification = new { verdict = "returned", returnReason = "quality" } });
        ok.StatusCode.Should().Be(HttpStatusCode.OK, await ok.Content.ReadAsStringAsync());
        JsonSerializer.Deserialize<JsonElement>(await ok.Content.ReadAsStringAsync())
            .GetProperty("verification").GetProperty("returnReason").GetString().Should().Be("quality");
    }
}
