using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// planSteps на MCP-пути (TasksToolset через /mcp/tasks/{sessionId}) — «настоящий JSON»:
// • Major 2: planSteps:[null] (элемент-null) → isError, а не NRE/тихий пустой список;
// • Major 4: знаменатель не молчит — не-массив (объект), не-объект в массиве (строки),
//   смесь → явный isError, а не «задача создана без шагов, isError нет».
[Trait("Category", "Integration")]
public class TasksToolsetPlanStepsTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly HttpClient _client;
    private readonly string _projectDir;

    public TasksToolsetPlanStepsTests(TestWebApplicationFactory factory)
    {
        _client = factory.CreateAuthenticatedClient();
        _projectDir = Path.Combine(factory.TempDir, "tasks_plansteps_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_projectDir);
    }

    private async Task<(string ProjectId, string SessionId)> CreateProjectWithSessionAsync()
    {
        var project = await _client.PostAsJsonAsync("/api/projects",
            new { name = "TasksPlanSteps", rootPath = _projectDir, createDirectory = true });
        project.EnsureSuccessStatusCode();
        var projectId = JsonSerializer.Deserialize<JsonElement>(await project.Content.ReadAsStringAsync())
            .GetProperty("id").GetString()!;
        var session = await _client.PostAsJsonAsync($"/api/projects/{projectId}/sessions",
            new { mode = "acceptEdits" });
        session.EnsureSuccessStatusCode();
        var sessionId = JsonSerializer.Deserialize<JsonElement>(await session.Content.ReadAsStringAsync())
            .GetProperty("id").GetString()!;
        return (projectId, sessionId);
    }

    // Вызов MCP-инструмента (JSON-RPC). Возвращает {content[0].text, isError}: isError=true —
    // отказ гейта (Deny), text — текст отказа; isError=false — успех, text — JSON задачи.
    private async Task<(string Text, bool IsError)> CallToolAsync(string sessionId, string tool, object args)
    {
        var resp = await _client.PostAsync($"/mcp/tasks/{sessionId}",
            new StringContent(JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0", id = 1, method = "tools/call",
                @params = new { name = tool, arguments = args },
            }), System.Text.Encoding.UTF8, "application/json"));
        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            "MCP отвечает 200 даже на отказ — isError=true в теле, а не HTTP-код");
        var body = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
        var result = body.GetProperty("result");
        var isError = result.TryGetProperty("isError", out var e) && e.GetBoolean();
        var text = result.GetProperty("content").EnumerateArray()
            .First().GetProperty("text").GetString()!;
        return (text, isError);
    }

    // ─── Major 2: элемент-null в planSteps → isError (не NRE, не тихий пустой список) ─────

    [Fact]
    public async Task Create_PlanStepsЭлементNull_IsError()
    {
        var (projectId, sessionId) = await CreateProjectWithSessionAsync();
        var (text, isError) = await CallToolAsync(sessionId, "tasks_create", new
        {
            title = "план",
            projectId,
            role = "plan",
            planSteps = new object[] { null }, // [null]
        });
        isError.Should().BeTrue($"элемент-null в planSteps — отказ, а не NRE/тихий пустой список: {text}");
    }

    // ─── Major 4: знаменатель не молчит ─────────────────────────────────────────────────

    [Fact]
    public async Task Create_PlanStepsМассивСтрок_IsError()
    {
        var (projectId, sessionId) = await CreateProjectWithSessionAsync();
        var (text, isError) = await CallToolAsync(sessionId, "tasks_create", new
        {
            title = "план",
            projectId,
            role = "plan",
            planSteps = new[] { "B1", "F1" }, // строки, не объекты {stepRef, title, lane}
        });
        isError.Should().BeTrue($"массив строк в planSteps — отказ, а не пустой список без isError: {text}");
    }

    [Fact]
    public async Task Create_PlanStepsОбъектВместоМассива_IsError()
    {
        var (projectId, sessionId) = await CreateProjectWithSessionAsync();
        var (text, isError) = await CallToolAsync(sessionId, "tasks_create", new
        {
            title = "план",
            projectId,
            role = "plan",
            planSteps = new { stepRef = "B1", title = "шаг" }, // объект, не массив
        });
        isError.Should().BeTrue($"объект вместо массива в planSteps — отказ, а не «не менять»: {text}");
    }

    [Fact]
    public async Task Create_PlanStepsСмесьОбъектаИСтроки_IsError()
    {
        var (projectId, sessionId) = await CreateProjectWithSessionAsync();
        var (text, isError) = await CallToolAsync(sessionId, "tasks_create", new
        {
            title = "план",
            projectId,
            role = "plan",
            planSteps = new object[] { new { stepRef = "B1", title = "бэк" }, "F1" }, // объект + строка
        });
        isError.Should().BeTrue($"смесь объекта и строки в planSteps — отказ: {text}");
    }

    // Контроль: корректный planSteps (массив объектов) создаёт план без isError — знаменатель лёг.
    [Fact]
    public async Task Create_PlanStepsКорректныйМассив_Ок()
    {
        var (projectId, sessionId) = await CreateProjectWithSessionAsync();
        var (text, isError) = await CallToolAsync(sessionId, "tasks_create", new
        {
            title = "план",
            projectId,
            role = "plan",
            planSteps = new[]
            {
                new { stepRef = "B1", title = "бэк", lane = "бэк" },
                new { stepRef = "F1", title = "фронт", lane = "фронт" },
            },
        });
        isError.Should().BeFalse($"корректный planSteps не должен отказывать: {text}");
        var created = JsonSerializer.Deserialize<JsonElement>(text);
        created.GetProperty("planSteps").EnumerateArray().Count(_ => true).Should().Be(2);
    }

    // ─── Major 1 (регресс): смена роли плана на не-план + явный НЕПУСТОЙ planSteps ───────
    // tasks_update {role:"step", planSteps:[...]} → isError (до дельты isError=false + тихая
    // потеря списка). Пустой [] со сменой роли — допустим (снимает).
    [Fact]
    public async Task Update_ПланНаStep_СЯвнымСписком_IsError()
    {
        var (projectId, sessionId) = await CreateProjectWithSessionAsync();

        // Эпик — источник featureId (step без эпика — «висяк», fail-closed)
        var (epicText, _) = await CallToolAsync(sessionId, "tasks_create",
            new { title = "эпик", role = "feature" });
        var epicId = JsonSerializer.Deserialize<JsonElement>(epicText).GetProperty("id").GetString()!;

        // План с planSteps (привязан к эпика)
        var (planText, planIsError) = await CallToolAsync(sessionId, "tasks_create", new
        {
            title = "план",
            projectId,
            featureId = epicId,
            role = "plan",
            planSteps = new[] { new { stepRef = "B1", title = "бэк" } },
        });
        planIsError.Should().BeFalse($"план создан: {planText}");
        var planId = JsonSerializer.Deserialize<JsonElement>(planText).GetProperty("id").GetString()!;

        // план → step + явный непустой planSteps: отказ, а не тихая потеря списка
        var (text, isError) = await CallToolAsync(sessionId, "tasks_update", new
        {
            id = planId,
            role = "step",
            planSteps = new[] { new { stepRef = "B2", title = "новый шаг" } },
        });
        isError.Should().BeTrue(
            $"смена роли на не-план + непустой planSteps — отказ, а не тихая потеря: {text}");
    }
}
