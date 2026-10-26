using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Mcp.Http;
using ClaudeHomeServer.Services.Tasks;
using ClaudeHomeServer.Services.Tasks.Projector;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Services;

// B4 (волна 6 «Обзор фич», разрез п.2): параметр featureNote у tasks_update и права по единому
// правилу членства FeatureChatMembership. Голос фичи пишут оркестратор (curator/orchestrator)
// или человек; исполнитель шага — нет; чат без членства — нет (fail-closed). Не-эпик — отказ
// тем же текстом, что REST PUT note. Мутации:
// • убрать гейт membership в case "tasks_update" — тесты «СессияИсполнителя»/«БезЧленства» краснеют;
// • убрать вызов SetFeatureNote — тесты «Curator»/«Оркестратор» краснеют;
// • убрать проверку role=feature — тест «НеЭпик» краснеет;
// • заменить белый список CanWriteFeatureVoice на Kind != "executor" — строка «key» в тесте
//   «БелыйСписок» краснеет (R-B4 минор 1);
// • заменить чтение Now.FirstOrDefault()?.Stage в штампе TasksToolset на "" — тест
//   «АктивнаяСтадия» краснеет (R-B4 минор 2).
[Trait("Category", "Integration")]
public class TasksToolsetFeatureNoteTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public TasksToolsetFeatureNoteTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateAuthenticatedClient();
    }

    private TaskManager Tasks => _factory.Services.GetRequiredService<TaskManager>();
    private SessionManager Sessions => _factory.Services.GetRequiredService<SessionManager>();
    private FeatureProjector Features => _factory.Services.GetRequiredService<FeatureProjector>();
    private string OwnerId => _factory.Services.GetRequiredService<UserStore>()
        .FindByUsername(TestWebApplicationFactory.TestUsername)!.Id;

    private async Task<(string ProjectId, string SessionId)> CreateProjectWithSessionAsync(string tag)
    {
        var dir = Path.Combine(_factory.TempDir, "mcp_note_" + tag + "_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var project = await _client.PostAsJsonAsync("/api/projects",
            new { name = "McpNote" + tag, rootPath = dir, createDirectory = true });
        project.EnsureSuccessStatusCode();
        var projectId = JsonSerializer.Deserialize<JsonElement>(await project.Content.ReadAsStringAsync())
            .GetProperty("id").GetString()!;
        var sessionId = await CreateSessionInProjectAsync(projectId);
        return (projectId, sessionId);
    }

    private async Task<string> CreateSessionInProjectAsync(string projectId)
    {
        var session = await _client.PostAsJsonAsync($"/api/projects/{projectId}/sessions",
            new { mode = "acceptEdits" });
        session.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<JsonElement>(await session.Content.ReadAsStringAsync())
            .GetProperty("id").GetString()!;
    }

    // Вызов MCP-инструмента через /mcp/tasks/{sessionId}: {content[0].text, isError} —
    // text несёт JSON задачи при успехе или текст Deny при отказе (паттерн TasksToolsetDefectTests).
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

    // Эпик + шаг, заведённые из чата-штаба: штаб — curator фичи (SourceSessionId эпика).
    private async Task<(string CuratorSessionId, string EpicId, string StepId)> CreateFeatureFromCuratorAsync(
        string projectId, string tag)
    {
        var curatorSessionId = await CreateSessionInProjectAsync(projectId);
        var (createText, createErr) = await CallToolAsync(curatorSessionId, "tasks_create",
            new { title = "Фича " + tag, role = "feature" });
        createErr.Should().BeFalse();
        var epicId = JsonSerializer.Deserialize<JsonElement>(createText).GetProperty("id").GetString()!;
        var (stepText, stepErr) = await CallToolAsync(curatorSessionId, "tasks_create",
            new { title = "Шаг " + tag, role = "step", featureId = epicId });
        stepErr.Should().BeFalse();
        var stepId = JsonSerializer.Deserialize<JsonElement>(stepText).GetProperty("id").GetString()!;
        return (curatorSessionId, epicId, stepId);
    }

    [Fact]
    public void ToolsList_TasksUpdate_СодержитFeatureNote()
    {
        // Параметр статичен: в tools/list всегда, в проектном и личном составе — иначе
        // сигнатура запуска мерцала бы между ходами (инвариант McpToolsetStabilityTests).
        foreach (var (label, tools) in new[] { ("project", TasksToolset.ProjectChatTools), ("personal", TasksToolset.PersonalTools) })
        {
            var props = tools.Single(t => t.Name == "tasks_update").InputSchema["properties"] is JsonObject p
                ? p : throw new InvalidOperationException($"{label}: у tasks_update нет properties");
            props.ContainsKey("featureNote").Should().BeTrue($"{label}: featureNote — аддитивный параметр tasks_update");
        }
    }

    // c2: curator (чат, создавший эпик) пишет голос; автор в сторе — чат-вызыватель, не человек;
    // At/StateAtWrite штампованы сервером; bySessionId наружу не отдаётся (FeatureNoteJsonConverter).
    [Fact]
    public async Task FeatureNote_Curator_ПишетГолос()
    {
        var (projectId, _) = await CreateProjectWithSessionAsync("curator");
        var (curatorId, epicId, _) = await CreateFeatureFromCuratorAsync(projectId, "curator");

        var (text, isError) = await CallToolAsync(curatorId, "tasks_update",
            new { id = epicId, featureNote = "фича идёт по плану, ждём ревью" });
        isError.Should().BeFalse();

        var note = JsonSerializer.Deserialize<JsonElement>(text).GetProperty("note");
        note.GetProperty("text").GetString().Should().Be("фича идёт по плану, ждём ревью");
        note.TryGetProperty("bySessionId", out _).Should().BeFalse(
            "чат автора наружу не отдаётся — тот же конвертер, что у REST/SignalR");

        var stored = Tasks.GetById(epicId)!;
        stored.Note!.Text.Should().Be("фича идёт по плану, ждём ревью");
        stored.Note.BySessionId.Should().Be(curatorId);
        stored.Note.ByUser.Should().BeFalse("MCP-путь — агентский, не человеческий");
        stored.Note.At.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1),
            "At штампует сервер, агент его не передаёт");
        // Штампы — из той же проекции, что читает фронт (разрез п.1: «из FeatureRun»).
        stored.Note.StateAtWrite.Should().Be(Features.Project(epicId, OwnerId)!.State);
        // Пустой случай (R-B4 минор 2): в этой фикстуре нет активной стадии (шаг Todo, Now пуст) —
        // штамп стадии пуст. Непустой случай покрыт тестом «АктивнаяСтадия».
        stored.Note.StageAtWrite.Should().Be("");
    }

    // Пустой текст снимает голос (та же семантика, что у REST PUT note).
    [Fact]
    public async Task FeatureNote_ПустойТекст_СнимаетГолос()
    {
        var (projectId, _) = await CreateProjectWithSessionAsync("remove");
        var (curatorId, epicId, _) = await CreateFeatureFromCuratorAsync(projectId, "remove");
        (await CallToolAsync(curatorId, "tasks_update", new { id = epicId, featureNote = "голос" }))
            .IsError.Should().BeFalse();

        var (text, isError) = await CallToolAsync(curatorId, "tasks_update", new { id = epicId, featureNote = "" });
        isError.Should().BeFalse();
        JsonSerializer.Deserialize<JsonElement>(text).GetProperty("note")
            .ValueKind.Should().Be(JsonValueKind.Null, "пустой текст снял голос");
        Tasks.GetById(epicId)!.Note.Should().BeNull();
    }

    // c2: сессия-исполнитель (chat.TaskId → шаг фичи) голос не пишет; обычный update без
    // голоса при этом остаётся разрешённым — гейт не разрастается на другие поля.
    [Fact]
    public async Task FeatureNote_СессияИсполнителя_Отказ()
    {
        var (projectId, _) = await CreateProjectWithSessionAsync("executor");
        var (_, epicId, stepId) = await CreateFeatureFromCuratorAsync(projectId, "executor");
        var execChat = await Sessions.CreateChatAsync(OwnerId, ClaudeMode.AcceptEdits, name: "exec",
            taskId: stepId);

        var (text, isError) = await CallToolAsync(execChat.Id, "tasks_update",
            new { id = epicId, featureNote = "голос из чата исполнителя" });
        isError.Should().BeTrue("executor — не право на голос, fail-closed");
        text.Should().Contain("Голос фичи пишет оркестратор или человек");
        Tasks.GetById(epicId)!.Note.Should().BeNull("отказ ничего не записал");

        var (_, plainErr) = await CallToolAsync(execChat.Id, "tasks_update",
            new { id = stepId, title = "правка шага из чата исполнителя" });
        plainErr.Should().BeFalse("обычный update без featureNote не затронут");
    }

    // c2: orchestrator (чат, запустивший исполнителя задачи фичи) пишет голос.
    [Fact]
    public async Task FeatureNote_Оркестратор_ПишетГолос()
    {
        var (projectId, _) = await CreateProjectWithSessionAsync("orch");
        var (_, epicId, stepId) = await CreateFeatureFromCuratorAsync(projectId, "orch");
        var orchestratorId = await CreateSessionInProjectAsync(projectId);
        // Знак запуска исполнителя — как его ставит tasks_run_executor: на задаче фичи.
        Tasks.Update(stepId, new UpdateTaskRequest(ExecutorLaunchedBySessionId: orchestratorId))
            .Should().NotBeNull();

        var (_, isError) = await CallToolAsync(orchestratorId, "tasks_update",
            new { id = epicId, featureNote = "голос оркестратора" });
        isError.Should().BeFalse();
        Tasks.GetById(epicId)!.Note!.Text.Should().Be("голос оркестратора");
        Tasks.GetById(epicId)!.Note!.BySessionId.Should().Be(orchestratorId);
    }

    // c2: чат без членства в фиче (тот же проект, но ни executor, ни curator, ни orchestrator)
    // — отказ тем же текстом: право только через FeatureChatMembership.
    [Fact]
    public async Task FeatureNote_БезЧленства_Отказ()
    {
        var (projectId, sessionId) = await CreateProjectWithSessionAsync("nomember");
        var epic = Tasks.Create(projectId, OwnerId, new CreateTaskRequest("Фича без члена", Role: "feature"));

        var (text, isError) = await CallToolAsync(sessionId, "tasks_update",
            new { id = epic.Id, featureNote = "чужой чат" });
        isError.Should().BeTrue();
        text.Should().Contain("Голос фичи пишет оркестратор или человек");
        Tasks.GetById(epic.Id)!.Note.Should().BeNull();
    }

    // c2: параметр у задачи не-эпика → отказ (400-семантика: текст о запросе, как у REST
    // «Голос пишется только у фичи (role=feature)»), даже если вызыватель — curator фичи.
    [Fact]
    public async Task FeatureNote_НеЭпик_Отказ()
    {
        var (projectId, _) = await CreateProjectWithSessionAsync("nonepic");
        var (curatorId, _, stepId) = await CreateFeatureFromCuratorAsync(projectId, "nonepic");

        var (text, isError) = await CallToolAsync(curatorId, "tasks_update",
            new { id = stepId, featureNote = "голос у шага" });
        isError.Should().BeTrue();
        text.Should().Contain("Голос пишется только у фичи (role=feature)");
    }

    // c2: чужой владелец — «не найдена» (первый гейт case не меняется).
    [Fact]
    public async Task FeatureNote_ЧужойВладелец_НеНайдена()
    {
        var (projectId, sessionId) = await CreateProjectWithSessionAsync("foreign");
        var foreign = Tasks.Create(projectId, "other-owner-" + Guid.NewGuid().ToString("N"),
            new CreateTaskRequest("Чужая фича", Role: "feature"));

        var (text, isError) = await CallToolAsync(sessionId, "tasks_update",
            new { id = foreign.Id, featureNote = "чужой голос" });
        isError.Should().BeTrue();
        text.Should().Contain("не найдена");
        Tasks.GetById(foreign.Id)!.Note.Should().BeNull();
    }

    // c1 (R-B4 минор 1): право голоса — белый список видов {curator, orchestrator}, а не
    // «не executor». Kind="key" BuildDetailed ещё не производит (добавит Б2, цепочка ключевого
    // чата) — тест моделирует связь: новый вид права не получает. Мутация: заменить тело
    // CanWriteFeatureVoice на link.Kind != "executor" — строка «key» краснеет.
    [Fact]
    public void FeatureNote_Право_БелыйСписок_НовыйKindНеДаётПраво()
    {
        TasksToolset.CanWriteFeatureVoice(new FeatureChatMembership.Link("curator", 1, null, null, null))
            .Should().BeTrue();
        TasksToolset.CanWriteFeatureVoice(new FeatureChatMembership.Link("orchestrator", 2, null, null, null))
            .Should().BeTrue();
        TasksToolset.CanWriteFeatureVoice(new FeatureChatMembership.Link("executor", 3, null, null, null))
            .Should().BeFalse();
        TasksToolset.CanWriteFeatureVoice(new FeatureChatMembership.Link("key", 0, null, null, null))
            .Should().BeFalse("ключевой чат (Б2) права не получает: только явный вход в список");
        TasksToolset.CanWriteFeatureVoice(new FeatureChatMembership.Link("future-kind", 3, null, null, null))
            .Should().BeFalse();
    }

    // c2 (R-B4 минор 2): у фичи ЕСТЬ активная стадия (idущий шаг) — StageAtWrite в MCP-пути
    // равен ей. Мутация: заменить чтение Now.FirstOrDefault()?.Stage в штампе TasksToolset на
    // "" — последний Should краснеет.
    [Fact]
    public async Task FeatureNote_АктивнаяСтадия_ШтампуетсяСтадия()
    {
        var (projectId, _) = await CreateProjectWithSessionAsync("stage");
        var (curatorId, epicId, stepId) = await CreateFeatureFromCuratorAsync(projectId, "stage");
        // Шаг идёт: Now непустой, активная стадия есть.
        Tasks.Update(stepId, new UpdateTaskRequest(Status: TaskItemStatus.InProgress))
            .Should().NotBeNull();

        var (_, isError) = await CallToolAsync(curatorId, "tasks_update",
            new { id = epicId, featureNote = "голос на активной стадии" });
        isError.Should().BeFalse();

        var run = Features.Project(epicId, OwnerId)!;
        run.Now.Should().NotBeEmpty("фикстура: у фичи идёт задача — активная стадия есть");
        run.Now[0].Stage.Should().Be("prod", "стадия шага (role=step) по таблице RoleToStage");

        var stored = Tasks.GetById(epicId)!;
        stored.Note!.StageAtWrite.Should().Be("prod",
            "StageAtWrite в MCP-пути — активная стадия на момент записи (FeatureBrief.StaleOf сверяет с ней)");
    }
}
