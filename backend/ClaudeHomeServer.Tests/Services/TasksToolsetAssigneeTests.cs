using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Mcp.Http;
using ClaudeHomeServer.Services.Tasks;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// S2b-2b: назначение человека-исполнителя задачи через MCP tasks_update (assignee = username
/// участника). Дверь MCP держит тот же контракт, что REST-дверь S2b-2a: резолв username→id
/// только среди участников проекта задачи (владелец + члены, через IProjectAccess), тот же тип
/// отказа TaskAssigneeDeniedException и тот же словарь кодов TaskManager — не участник →
/// assignee_not_member, читатель → reader_not_assignable, персонная задача →
/// person_task_not_assignable (её отказывает сам TaskManager, здесь только текст с кодом).
/// При отказе запись не меняется: отказ до первой мутации. «me»/«claude» — прежний путь
/// enum, резолва не касается, но они сняют прежнего исполнителя-участника (M1 ревью
/// e9f308b0): «me» отдаёт в AssigneeUserId id владельца проекта задачи (TaskManager трактует
/// его как снятие), «claude» — "". Запись идёт только через TaskManager: два одновременных
/// назначения дают ровно одно из двух значений целиком.
///
/// Мутации (каждая — красный названный тест, откат — зелёный):
/// • ResolveAssigneeUsername: пропустить отказ по членству (scope is null → не отказ) —
///   «НазначитьНеУчастника» краснеет;
/// • убрать проверку роли Reader — «НазначитьЧитателя» краснеет;
/// • убрать catch TaskAssigneeDeniedException в case "tasks_update" — «ЗадачаСПерсоной»
///   краснеет: текст отказа доходит без кода;
/// • убрать AssigneeUserId из UpdateTaskRequest — «НазначитьУчастникаПоUsername» краснеет;
/// • вернуть прежнюю ветку enum (для «me»/«claude» AssigneeUserId не ставить) —
///   «УчастникЗатемMe» и «УчастникЗатемClaude» краснеют: участник остаётся в записи.
/// </summary>
[Trait("Category", "Integration")]
[Trait("Category", "Host")]
public class TasksToolsetAssigneeTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public TasksToolsetAssigneeTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
        _factory.ExtraConfig[SharedProjectsGate.SwitchKey] = "true";
    }

    private HttpClient Owner() => _factory.CreateAuthenticatedClient();
    private HttpClient As(string username, string password) => _factory.CreateAuthenticatedClient(username, password);
    private TaskManager Tasks => _factory.Services.GetRequiredService<TaskManager>();
    private UserStore Users => _factory.Services.GetRequiredService<UserStore>();
    private SessionManager Sessions => _factory.Services.GetRequiredService<SessionManager>();

    private string OwnerId => Users.FindByUsername(TestWebApplicationFactory.TestUsername)!.Id;
    private string MemberId => Users.FindByUsername(TestWebApplicationFactory.SecondUsername)!.Id;

    private static async Task SetFlagAsync(HttpClient client) =>
        (await client.PutAsJsonAsync("/api/feature-flags/shared-projects", new { enabled = true }))
            .EnsureSuccessStatusCode();

    private static string IdOf(string json) =>
        JsonSerializer.Deserialize<JsonElement>(json).GetProperty("id").GetString()!;

    // Вызов MCP-инструмента через /mcp/tasks/{sessionId}: {text, isError} — текст несёт JSON
    // задачи при успехе и текст Deny при отказе (паттерн TasksToolsetFeatureNoteTests)
    private async Task<(string Text, bool IsError)> CallToolAsync(string sessionId, string tool, object args)
    {
        var resp = await Owner().AsCaller(sessionId).PostAsync($"/mcp/tasks/{sessionId}",
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

    /// <summary>Проект владельца с чатом в нём и задачей в этом проекте; role — роль второго
    /// пользователя ("editor"/"reader"; null — участника нет). Флаг shared-projects поднят
    /// обоим: без него участник не резолвится точкой доступа — тот же отказ, что у REST-двери.</summary>
    private async Task<(string ProjectId, string SessionId, string TaskId)> SharedProjectWithTaskAsync(string? role)
    {
        var owner = Owner();
        await SetFlagAsync(owner);
        await SetFlagAsync(As(TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword));

        var dir = Path.Combine(_factory.TempDir, "ta_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var created = await owner.PostAsJsonAsync("/api/projects",
            new { name = "НазначениеMCP" + Guid.NewGuid().ToString("N")[..6], rootPath = dir, createDirectory = true });
        created.EnsureSuccessStatusCode();
        var projectId = IdOf(await created.Content.ReadAsStringAsync());

        if (role is not null)
            (await owner.PostAsJsonAsync($"/api/projects/{projectId}/members",
                new { username = TestWebApplicationFactory.SecondUsername, role })).EnsureSuccessStatusCode();

        var session = await owner.PostAsJsonAsync($"/api/projects/{projectId}/sessions", new { mode = "acceptEdits" });
        session.EnsureSuccessStatusCode();
        var sessionId = IdOf(await session.Content.ReadAsStringAsync());

        var (text, isError) = await CallToolAsync(sessionId, "tasks_create", new { title = "Задача проекта" });
        isError.Should().BeFalse(text);
        return (projectId, sessionId, IdOf(text));
    }

    // c1: участник с правом записи назначается по username; поле и «Me» поставлены,
    // чтение «Me» даёт его id (тот же итог, что у REST PUT S2b-2a)
    [Fact]
    public async Task НазначитьУчастникаПоUsername_ИсполнительОн()
    {
        var (_, sessionId, taskId) = await SharedProjectWithTaskAsync("editor");

        var (text, isError) = await CallToolAsync(sessionId, "tasks_update",
            new { id = taskId, assignee = TestWebApplicationFactory.SecondUsername });

        isError.Should().BeFalse(text);
        var body = JsonDocument.Parse(text).RootElement;
        body.GetProperty("assigneeUserId").GetString().Should().Be(MemberId);
        body.GetProperty("assignee").GetString().Should().Be("me");
        TaskManager.ResolveMeUserId(Tasks.GetById(taskId)!).Should().Be(MemberId);
    }

    // Мутация M1: пропустить отказ по членству в ResolveAssigneeUsername — этот тест красный
    [Fact]
    public async Task НазначитьНеУчастника_Отказ_assignee_not_member_ИЗаписьНеМеняется()
    {
        var (_, sessionId, taskId) = await SharedProjectWithTaskAsync(null);

        var (text, isError) = await CallToolAsync(sessionId, "tasks_update",
            new { id = taskId, assignee = TestWebApplicationFactory.SecondUsername });

        isError.Should().BeTrue("чужой username — отказ, а не тихий «не менять»");
        text.Should().Contain(TaskManager.AssigneeNotMemberCode, "код ветки — часть контракта");
        var stored = Tasks.GetById(taskId)!;
        stored.AssigneeUserId.Should().BeNull();
        stored.Assignee.Should().Be(TaskItemAssignee.Me, "отказ не трогает и Assignee (дефолт Create — Me)");
    }

    // Мутация M2: убрать проверку роли Reader — этот тест красный
    [Fact]
    public async Task НазначитьЧитателя_Отказ_reader_not_assignable_ИЗаписьНеМеняется()
    {
        var (_, sessionId, taskId) = await SharedProjectWithTaskAsync("reader");

        var (text, isError) = await CallToolAsync(sessionId, "tasks_update",
            new { id = taskId, assignee = TestWebApplicationFactory.SecondUsername });

        isError.Should().BeTrue();
        text.Should().Contain(TaskManager.ReaderNotAssignableCode);
        Tasks.GetById(taskId)!.AssigneeUserId.Should().BeNull();
    }

    // Мутация M3: убрать catch TaskAssigneeDeniedException — текст дошёл бы без кода, красный
    [Fact]
    public async Task ЗадачаСПерсоной_НазначитьУчастника_Отказ_person_task_not_assignable_ИПерсонаЦела()
    {
        var (projectId, sessionId, _) = await SharedProjectWithTaskAsync("editor");
        var persona = await Owner().PostAsJsonAsync("/api/personas",
            new { name = "Персона назначения MCP", scope = "project", projectId });
        persona.StatusCode.Should().Be(HttpStatusCode.OK);
        var personaId = IdOf(await persona.Content.ReadAsStringAsync());
        var (createdText, createdError) = await CallToolAsync(sessionId, "tasks_create",
            new { title = "Персонная задача", assignee = "claude", personaId });
        createdError.Should().BeFalse(createdText);
        var taskId = IdOf(createdText);

        var (text, isError) = await CallToolAsync(sessionId, "tasks_update",
            new { id = taskId, assignee = TestWebApplicationFactory.SecondUsername });

        isError.Should().BeTrue();
        text.Should().Contain(TaskManager.PersonTaskNotAssignableCode);
        var stored = Tasks.GetById(taskId)!;
        stored.PersonaId.Should().Be(personaId);
        stored.Assignee.Should().Be(TaskItemAssignee.Claude);
        stored.AssigneeUserId.Should().BeNull();
    }

    // «me»/«claude» — прежний путь enum: резолв username их не касается; в записи поле
    // пустое («me» — id владельца = снятие, «claude» — ""), участника не остаётся
    [Fact]
    public async Task MeIClaude_ПрежниеЗначения_РезолваНет()
    {
        var (_, sessionId, taskId) = await SharedProjectWithTaskAsync("editor");

        var (_, claudeError) = await CallToolAsync(sessionId, "tasks_update",
            new { id = taskId, assignee = "claude" });
        claudeError.Should().BeFalse();
        Tasks.GetById(taskId)!.Assignee.Should().Be(TaskItemAssignee.Claude);
        Tasks.GetById(taskId)!.AssigneeUserId.Should().BeNull();

        var (meText, meError) = await CallToolAsync(sessionId, "tasks_update",
            new { id = taskId, assignee = "me" });
        meError.Should().BeFalse(meText);
        var stored = Tasks.GetById(taskId)!;
        stored.Assignee.Should().Be(TaskItemAssignee.Me);
        stored.AssigneeUserId.Should().BeNull("«me» — это владелец, а не id участника");
    }

    // M1 ревью e9f308b0: «me» снимает прежнего участника. Последовательность: назначить
    // участника → tasks_update assignee=me → исполнитель снова владелец (id владельца в
    // TaskManager = снятие), поле пустое, Assignee=Me
    [Fact]
    public async Task УчастникЗатемMe_ИсполнительСноваВладелец()
    {
        var (_, sessionId, taskId) = await SharedProjectWithTaskAsync("editor");

        var (assignText, assignError) = await CallToolAsync(sessionId, "tasks_update",
            new { id = taskId, assignee = TestWebApplicationFactory.SecondUsername });
        assignError.Should().BeFalse(assignText);
        TaskManager.ResolveMeUserId(Tasks.GetById(taskId)!).Should().Be(MemberId,
            "до «me» исполнитель — участник: иначе тест проверяет не снятие, а дефолт");

        var (meText, meError) = await CallToolAsync(sessionId, "tasks_update", new { id = taskId, assignee = "me" });
        meError.Should().BeFalse(meText);

        var stored = Tasks.GetById(taskId)!;
        stored.AssigneeUserId.Should().BeNull("«me» = id владельца = снятие, а не «не менять»");
        stored.Assignee.Should().Be(TaskItemAssignee.Me);
        TaskManager.ResolveMeUserId(stored).Should().Be(OwnerId, "c1: после «участник → me» — id владельца");
    }

    // M1 ревью e9f308b0: «claude» снимает прежнего участника: после «участник → claude»
    // AssigneeUserId в записи пуст, человека-исполнителя нет (ResolveMeUserId → null)
    [Fact]
    public async Task УчастникЗатемClaude_ЧеловекаИсполнителяВЗаписиНет()
    {
        var (_, sessionId, taskId) = await SharedProjectWithTaskAsync("editor");

        var (assignText, assignError) = await CallToolAsync(sessionId, "tasks_update",
            new { id = taskId, assignee = TestWebApplicationFactory.SecondUsername });
        assignError.Should().BeFalse(assignText);
        Tasks.GetById(taskId)!.AssigneeUserId.Should().Be(MemberId, "до «claude» поле поставлено");

        var (claudeText, claudeError) = await CallToolAsync(sessionId, "tasks_update",
            new { id = taskId, assignee = "claude" });
        claudeError.Should().BeFalse(claudeText);

        var stored = Tasks.GetById(taskId)!;
        stored.AssigneeUserId.Should().BeNull("«claude» = человека нет: поле снято, а не оставлено");
        stored.Assignee.Should().Be(TaskItemAssignee.Claude);
        TaskManager.ResolveMeUserId(stored).Should().BeNull();
    }

    // Снятие на личной задаче (ветка «нет проекта → id вызывающего»): «me» не меняет запись —
    // исполнитель остаётся владельцем
    [Fact]
    public async Task ЛичнаяЗадачаMe_ИсполнительВладелецБезИзменений()
    {
        var chat = await Sessions.CreateChatAsync(OwnerId, ClaudeMode.AcceptEdits, name: "Личный чат me");
        var (createdText, createdError) = await CallToolAsync(chat.Id, "tasks_create", new { title = "Личная задача me" });
        createdError.Should().BeFalse(createdText);
        var taskId = IdOf(createdText);

        var (meText, meError) = await CallToolAsync(chat.Id, "tasks_update", new { id = taskId, assignee = "me" });
        meError.Should().BeFalse(meText);

        var stored = Tasks.GetById(taskId)!;
        stored.AssigneeUserId.Should().BeNull();
        stored.Assignee.Should().Be(TaskItemAssignee.Me);
        TaskManager.ResolveMeUserId(stored).Should().Be(OwnerId);
    }

    // Личная задача: чужого круга нет (ей нечего быть участником) — тот же отказ, что у REST;
    // свой username равен «снять»: поле не поставлено, исполнитель — владелец
    [Fact]
    public async Task ЛичнаяЗадача_ЧужойUsername_Отказ_СвойUsername_РавенСнять()
    {
        var chat = await Sessions.CreateChatAsync(OwnerId, ClaudeMode.AcceptEdits, name: "Личный чат");
        var (createdText, createdError) = await CallToolAsync(chat.Id, "tasks_create", new { title = "Личная задача" });
        createdError.Should().BeFalse(createdText);
        var taskId = IdOf(createdText);

        var (text, isError) = await CallToolAsync(chat.Id, "tasks_update",
            new { id = taskId, assignee = TestWebApplicationFactory.SecondUsername });
        isError.Should().BeTrue();
        text.Should().Contain(TaskManager.AssigneeNotMemberCode);
        Tasks.GetById(taskId)!.AssigneeUserId.Should().BeNull();

        var (_, ownError) = await CallToolAsync(chat.Id, "tasks_update",
            new { id = taskId, assignee = TestWebApplicationFactory.TestUsername });
        ownError.Should().BeFalse();
        var stored = Tasks.GetById(taskId)!;
        stored.AssigneeUserId.Should().BeNull();
        TaskManager.ResolveMeUserId(stored).Should().Be(OwnerId);
    }

    // Конкурентность (c3): запись только через TaskManager. Два одновременных назначения
    // разных участников дают ровно одно из двух значений целиком — запись не рвётся пополам
    // и не теряет Title (TaskCompletionSource, без Task.Delay)
    [Fact]
    public async Task ОдновременныеНазначения_РовноОдноЗначенийБезПорчиЗаписи()
    {
        var (projectId, sessionId, taskId) = await SharedProjectWithTaskAsync("editor");
        var third = Users.Add("third-assign-" + Guid.NewGuid().ToString("N")[..6], "pw-123456", "user");
        await SetFlagAsync(As(third.Username, "pw-123456"));
        (await Owner().PostAsJsonAsync($"/api/projects/{projectId}/members",
            new { username = third.Username, role = "editor" })).EnsureSuccessStatusCode();

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = Task.Run(async () =>
        {
            gate.Task.GetAwaiter().GetResult();
            return await CallToolAsync(sessionId, "tasks_update",
                new { id = taskId, assignee = TestWebApplicationFactory.SecondUsername });
        });
        var second = Task.Run(async () =>
        {
            gate.Task.GetAwaiter().GetResult();
            return await CallToolAsync(sessionId, "tasks_update",
                new { id = taskId, assignee = third.Username });
        });
        gate.SetResult();

        var (firstText, firstError) = await first;
        var (secondText, secondError) = await second;
        firstError.Should().BeFalse(firstText);
        secondError.Should().BeFalse(secondText);

        var stored = Tasks.GetById(taskId)!;
        stored.AssigneeUserId.Should().BeOneOf(MemberId, third.Id);
        stored.Assignee.Should().Be(TaskItemAssignee.Me);
        stored.Title.Should().Be("Задача проекта", "назначение исполнителя не забирает остальные поля");
    }

    // Состав и схема статичны: параметр tasks_update.assignee есть в обеих группах тулсета
    // и не несёт фиксированного enum — иначе username участника был бы закрыт на клиенте
    // (инвариант McpToolsetStabilityTests: состав не зависит от хода)
    [Fact]
    public void ToolsList_TasksUpdate_AssigneeБезФиксированногоEnum_ВОбеихГруппах()
    {
        foreach (var (label, tools) in new[]
            { ("проектный", TasksToolset.ProjectChatTools), ("личный", TasksToolset.PersonalTools) })
        {
            var props = tools.Single(t => t.Name == "tasks_update").InputSchema["properties"] is JsonObject p
                ? p : throw new InvalidOperationException($"{label}: у tasks_update нет properties");
            props.ContainsKey("assignee").Should().BeTrue($"{label}: assignee — существующий параметр, расширен значением-username");
            (props["assignee"] as JsonObject)!.ContainsKey("enum").Should().BeFalse(
                $"{label}: фиксированный enum закрыл бы username участника на клиенте");
        }
    }
}
