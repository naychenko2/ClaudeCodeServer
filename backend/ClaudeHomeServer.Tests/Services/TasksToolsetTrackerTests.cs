using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Services.Tasks;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Services;

// TR-B2: блок «Сверка трекера» в MCP tasks_get по эпику (фича «Серверная сверка трекера»,
// разрез Александра 26.10). Проверяется поведение на вызове:
// • эпик с расхождением → к JSON дописан блок со строкой unreviewed (kind · taskId · stepRef · since);
// • эпик без расхождений → блок назван явно: «расхождений нет»;
// • обычная задача → ответ прежний байт в байт: блока нет, текст парсится JSON целиком;
// • чужой эпик → Deny, блока нет (изоляция владельцев — как у остального чтения);
// • «сервер не чинит»: tasks_get ничего не меняет в карточках (снимок до и после совпадает).
// Порог FeatureTracker.TrackerGraceMinutes — константа (30 мин), дата закрытия ставится
// прямым сдвигом CompletedAt на живой задаче (отдельного «закрыть с датой» API нет).
[Trait("Category", "Integration")]
[Trait("Category", "Host")]
public class TasksToolsetTrackerTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;
    private readonly HttpClient _client;
    private readonly string _projectDir;

    public TasksToolsetTrackerTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateAuthenticatedClient();
        _projectDir = Path.Combine(factory.TempDir, "tasks_tracker_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_projectDir);
    }

    private TaskManager Tasks => _factory.Services.GetRequiredService<TaskManager>();

    private async Task<(string ProjectId, string SessionId)> CreateProjectWithSessionAsync(HttpClient? client = null)
    {
        client ??= _client;
        var project = await client.PostAsJsonAsync("/api/projects",
            new { name = "TasksTracker", rootPath = _projectDir, createDirectory = true });
        project.EnsureSuccessStatusCode();
        var projectId = JsonSerializer.Deserialize<JsonElement>(await project.Content.ReadAsStringAsync())
            .GetProperty("id").GetString()!;
        var session = await client.PostAsJsonAsync($"/api/projects/{projectId}/sessions",
            new { mode = "acceptEdits" });
        session.EnsureSuccessStatusCode();
        var sessionId = JsonSerializer.Deserialize<JsonElement>(await session.Content.ReadAsStringAsync())
            .GetProperty("id").GetString()!;
        return (projectId, sessionId);
    }

    // Вызов MCP-инструмента (JSON-RPC). Возвращает {text, isError} — как в TasksToolsetPlanStepsTests.
    private static async Task<(string Text, bool IsError)> CallToolAsync(
        HttpClient client, string sessionId, string tool, object args)
    {
        var resp = await client.PostAsync($"/mcp/tasks/{sessionId}",
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

    private static string IdOf(string taskJson) =>
        JsonDocument.Parse(taskJson).RootElement.GetProperty("id").GetString()!;

    // Эпик + сданный 2 часа назад шаг S1: цепочка сдана, покрывающего витка нет → unreviewed.
    private async Task<(string EpicId, string StepId)> CreateEpicWithLateSubmittedStepAsync(
        HttpClient client, string projectId, string sessionId)
    {
        var (epicJson, epicErr) = await CallToolAsync(client, sessionId, "tasks_create",
            new { title = "фича под сверку", projectId, role = "feature" });
        epicErr.Should().BeFalse($"эпик создаётся обычным вызовом: {epicJson}");
        var epicId = IdOf(epicJson);

        var (stepJson, stepErr) = await CallToolAsync(client, sessionId, "tasks_create",
            new { title = "шаг S1", projectId, role = "step", stepRef = "S1", featureId = epicId, lane = "back" });
        stepErr.Should().BeFalse($"шаг с featureId создаётся обычным вызовом: {stepJson}");
        var stepId = IdOf(stepJson);

        var (_, completeErr) = await CallToolAsync(client, sessionId, "tasks_complete", new { id = stepId });
        completeErr.Should().BeFalse("шаг без критериев приёмки закрывается без свидетельств");

        // Порог — константа 30 мин; «закрыть с датой» API нет, поэтому дата сдвигается на живой
        // задаче. Инвариант не нарушается: задача остаётся Done с CompletedAt.
        Tasks.GetById(stepId)!.CompletedAt = DateTime.UtcNow.AddHours(-2);
        return (epicId, stepId);
    }

    // ─── Эпик с расхождением: блок есть, строка unreviewed с id шага и stepRef ─────────────

    [Fact]
    public async Task Get_ЭпикСРасхождением_БлокСверкиСодержитUnreviewed()
    {
        var (projectId, sessionId) = await CreateProjectWithSessionAsync();
        var (epicId, stepId) = await CreateEpicWithLateSubmittedStepAsync(_client, projectId, sessionId);

        var (text, isError) = await CallToolAsync(_client, sessionId, "tasks_get", new { id = epicId });
        isError.Should().BeFalse();

        text.Should().Contain("Сверка трекера:", "у эпика блок обязан быть");
        text.Should().Contain("unreviewed", "сданный 2 часа назад шаг без покрывающего витка — unreviewed");
        text.Should().Contain(stepId, "строка расхождения называет задачу");
        text.Should().Contain("S1", "строка расхождения называет stepRef");

        // JSON карточки остаётся читаемым: блок дописан после него, а не внутрь
        var jsonPart = text.Split("\n\nСверка трекера:")[0];
        var card = JsonDocument.Parse(jsonPart);
        card.RootElement.GetProperty("id").GetString().Should().Be(epicId);
    }

    // ─── Эпик без расхождений: блок назван явно ────────────────────────────────────────────

    [Fact]
    public async Task Get_ЭпикБезРасхождений_СказаноЧтоРасхожденийНет()
    {
        var (projectId, sessionId) = await CreateProjectWithSessionAsync();
        var (epicJson, epicErr) = await CallToolAsync(_client, sessionId, "tasks_create",
            new { title = "пустая фича", projectId, role = "feature" });
        epicErr.Should().BeFalse(epicJson);

        var (text, isError) = await CallToolAsync(_client, sessionId, "tasks_get",
            new { id = IdOf(epicJson) });
        isError.Should().BeFalse();
        text.Should().Contain("Сверка трекера: расхождений нет",
            "пустой список называется явно: молчание читалось бы как «не смотрели»");
    }

    // ─── Обычная задача: ответ прежний байт в байт, парсится JSON целиком ──────────────────

    [Fact]
    public async Task Get_ОбычнаяЗадача_БлокаНет_ОтветПарситсяJSON()
    {
        var (projectId, sessionId) = await CreateProjectWithSessionAsync();
        var (taskJson, createErr) = await CallToolAsync(_client, sessionId, "tasks_create",
            new { title = "обычная задача", projectId });
        createErr.Should().BeFalse(taskJson);

        var (text, isError) = await CallToolAsync(_client, sessionId, "tasks_get",
            new { id = IdOf(taskJson) });
        isError.Should().BeFalse();
        text.Should().NotContain("Сверка трекера", "блок только у role=feature");
        var card = JsonDocument.Parse(text); // весь ответ — JSON, без дописанного хвоста
        card.RootElement.GetProperty("id").GetString().Should().Be(IdOf(taskJson));
    }

    // ─── Чужой эпик: Deny, блока нет ───────────────────────────────────────────────────────

    [Fact]
    public async Task Get_ЧужойЭпик_ОтказИБлокаНет()
    {
        using var clientB = _factory.CreateAuthenticatedClient(
            TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);
        var (projectIdB, sessionIdB) = await CreateProjectWithSessionAsync(clientB);
        var (epicJson, epicErr) = await CallToolAsync(clientB, sessionIdB, "tasks_create",
            new { title = "чужая фича", projectId = projectIdB, role = "feature" });
        epicErr.Should().BeFalse(epicJson);

        var (text, isError) = await CallToolAsync(_client, sessionIdB, "tasks_get",
            new { id = IdOf(epicJson) });
        // Сессия в хвосте — чужая: и состав, и вызов закрыты (TasksHttpDelegationGateTests).
        // Тот же итог и без смены клиента: чужая задача не видна GetForRead.
        isError.Should().BeTrue($"чужой эпик — отказ: {text}");
        text.Should().NotContain("Сверка трекера");
    }

    // ─── «Сервер не чинит»: tasks_get не меняет карточки владельца ─────────────────────────

    [Fact]
    public async Task Get_ЭпикСРасхождением_НичегоНеМеняется()
    {
        var (projectId, sessionId) = await CreateProjectWithSessionAsync();
        var (epicId, _) = await CreateEpicWithLateSubmittedStepAsync(_client, projectId, sessionId);
        var ownerId = Tasks.GetById(epicId)!.OwnerId!; // карточка всегда с владельцем

        static List<(string Id, string Status, DateTime UpdatedAt)> Snapshot(TaskManager tasks, string owner) =>
            [.. tasks.GetByOwner(owner).Select(t => (t.Id, t.Status.ToString(), t.UpdatedAt))];

        var before = Snapshot(Tasks, ownerId);
        before.Should().HaveCountGreaterThanOrEqualTo(2, "посев должен быть виден снимку");

        var (text, isError) = await CallToolAsync(_client, sessionId, "tasks_get", new { id = epicId });
        isError.Should().BeFalse();
        text.Should().Contain("Сверка трекера:", "показываем расхождения — и только");

        Snapshot(Tasks, ownerId).Should().Equal(before,
            "сервер расхождения только показывает: ни статуса, ни UpdatedAt он не трогает");
    }
}
