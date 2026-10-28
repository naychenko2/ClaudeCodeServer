using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Tasks;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Controllers;

/// <summary>
/// Поведенческие оси http-тулсетов волны 2 (блокеры приёмки 2.1). Сторожа парности
/// (TasksToolsetParityTests) сверяют ФОРМУ контракта — имена и required-наборы; здесь —
/// ПОВЕДЕНИЕ, которое форма скопировала из MemoryToolset, но потеряла при переносе:
/// гейт целевого проекта на записи, скоупы назначаемой персоны, пустое значение как
/// очистка, гейт mentions на вызове и согласованность состава с отпечатком запуска.
/// </summary>
[Trait("Category", "Host")]
public class TasksToolsetScopesTests : IDisposable
{
    private readonly TestWebApplicationFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private HttpClient Client => _factory.CreateAuthenticatedClient();

    private static async Task<string> CreateProjectAsync(HttpClient client, string prefix)
    {
        var project = await client.PostAsJsonAsync("/api/projects", new { name = $"{prefix}-{Guid.NewGuid():N}" });
        project.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<JsonElement>(
            await project.Content.ReadAsStringAsync()).GetProperty("id").GetString()!;
    }

    private static async Task<string> CreateSessionAsync(HttpClient client, string projectId)
    {
        var session = await client.PostAsJsonAsync($"/api/projects/{projectId}/sessions",
            new { mode = "acceptEdits" });
        session.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<JsonElement>(
            await session.Content.ReadAsStringAsync()).GetProperty("id").GetString()!;
    }

    private static async Task<JsonElement> CallToolAsync(
        HttpClient client, string server, string sessionId, string tool, object args)
    {
        var resp = await client.PostAsJsonAsync($"/mcp/{server}/{sessionId}", new
        {
            jsonrpc = "2.0", id = 1, method = "tools/call",
            @params = new { name = tool, arguments = args },
        });
        resp.StatusCode.Should().Be(HttpStatusCode.OK, "отказ инструмента — content-ошибка, не протокольная");
        return JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
    }

    private static string ToolText(JsonElement answer) =>
        answer.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;

    private static async Task<JsonElement> ToolJsonAsync(
        HttpClient client, string server, string sessionId, string tool, object args)
    {
        var answer = await CallToolAsync(client, server, sessionId, tool, args);
        answer.GetProperty("result").TryGetProperty("isError", out _).Should().BeFalse(
            $"вызов {tool} не должен падать: {ToolText(answer)}");
        return JsonSerializer.Deserialize<JsonElement>(ToolText(answer));
    }

    /// <summary>
    /// Оси 1 и 2: целевой проект без привязки ProjectTasks. stdio-эталон начинал
    /// assertProjectWritable с assertProjectReadable, http-копия потеряла первую проверку —
    /// пустое readonly-множество разрешало и СОЗДАНИЕ задачи на чужой доске, и ПЕРЕНОС
    /// задачи в чужой проект (ни TaskManager, ни REST про скоупы MCP не знают).
    /// </summary>
    [Fact]
    public async Task ПроектБезПривязки_СозданиеИПеренос_Отказ()
    {
        var ownProject = await CreateProjectAsync(Client, "scopes-own");
        var foreignProject = await CreateProjectAsync(Client, "scopes-foreign");
        var sessionId = await CreateSessionAsync(Client, ownProject);

        var create = await CallToolAsync(Client, "tasks", sessionId, "tasks_create",
            new { title = "Чужая доска", projectId = foreignProject });
        create.GetProperty("result").GetProperty("isError").GetBoolean().Should().BeTrue();
        ToolText(create).Should().Contain("Нет доступа к проекту").And.Contain(foreignProject);

        var created = await ToolJsonAsync(Client, "tasks", sessionId, "tasks_create",
            new { title = "Своя задача" });
        var taskId = created.GetProperty("id").GetString()!;

        var move = await CallToolAsync(Client, "tasks", sessionId, "tasks_update",
            new { id = taskId, projectId = foreignProject });
        move.GetProperty("result").GetProperty("isError").GetBoolean().Should().BeTrue();
        ToolText(move).Should().Contain("Нет доступа к проекту",
            "перенос задачи в непривязанный проект — тот же гейт записи");
    }

    /// <summary>
    /// Б-cl: перенос merge-задачи в другую фичу (tasks_update featureId), финал целевой фичи
    /// не принят ⇒ isError (400); перенос запрещён в любую фичу. C#/REST-аналоги — TaskManagerTests / TasksControllerTests.
    /// </summary>
    [Fact]
    public async Task Update_Merge_ПереносВДругуюФичу_ФиналНеПринят_IsError()
    {
        var project = await CreateProjectAsync(Client, "merge-move");
        var sessionId = await CreateSessionAsync(Client, project);

        // Фича A: эпик + финал (Done + accepted) + merge.
        var epicA = await ToolJsonAsync(Client, "tasks", sessionId, "tasks_create",
            new { title = "эпик A", role = "feature" });
        var epicAId = epicA.GetProperty("id").GetString()!;
        var finalA = await ToolJsonAsync(Client, "tasks", sessionId, "tasks_create",
            new { title = "финал A", featureId = epicAId, role = "final" });
        var finalAId = finalA.GetProperty("id").GetString()!;
        await ToolJsonAsync(Client, "tasks", sessionId, "tasks_update",
            new { id = finalAId, status = "done", verification = new { verdict = "accepted" } });
        var mergeA = await ToolJsonAsync(Client, "tasks", sessionId, "tasks_create",
            new { title = "мерж A", featureId = epicAId, role = "merge" });
        var mergeAId = mergeA.GetProperty("id").GetString()!;

        // Фича B: только эпик (финала нет → не Ready). Заводим из другого чата:
        // правило «один чат — одна фича» не даёт вести два эпика из одной сессии.
        var sessionBId = await CreateSessionAsync(Client, project);
        var epicB = await ToolJsonAsync(Client, "tasks", sessionBId, "tasks_create",
            new { title = "эпик B", role = "feature" });
        var epicBId = epicB.GetProperty("id").GetString()!;

        // Перенос merge-задачи в B ⇒ isError (финал B не принят). Зовём из чата фичи B,
        // иначе гард привязки чата к фиче откажет раньше проверяемого правила.
        var answer = await CallToolAsync(Client, "tasks", sessionBId, "tasks_update",
            new { id = mergeAId, featureId = epicBId });
        answer.GetProperty("result").GetProperty("isError").GetBoolean().Should().BeTrue();
        ToolText(answer).Should().Contain("Роль merge привязана к своей фиче");
    }

    /// <summary>
    /// Ось 3: исполнитель задачи чужого (непривязанного) проекта стартует в его рабочем
    /// каталоге — запуск обязан отказывать так же, как tasks_update по этой задаче.
    /// У stdio проверки не было вовсе; закрываем вместе с дырой записи (блокер 2.1).
    /// </summary>
    [Fact]
    public async Task RunExecutor_ЗадачаЧужогоПроекта_Отказ()
    {
        var ownProject = await CreateProjectAsync(Client, "exec-own");
        var foreignProject = await CreateProjectAsync(Client, "exec-foreign");
        var sessionId = await CreateSessionAsync(Client, ownProject);

        // Задача в чужом проекте — легально через REST (у владельца нет скоупов только у MCP-персоны)
        var task = await Client.PostAsJsonAsync($"/api/projects/{foreignProject}/tasks",
            new { title = "Задача чужого проекта" });
        task.EnsureSuccessStatusCode();
        var taskId = JsonSerializer.Deserialize<JsonElement>(
            await task.Content.ReadAsStringAsync()).GetProperty("id").GetString()!;

        var answer = await CallToolAsync(Client, "tasks", sessionId, "tasks_run_executor",
            new { taskId });
        answer.GetProperty("result").GetProperty("isError").GetBoolean().Should().BeTrue();
        ToolText(answer).Should().Contain("недоступна в этом контексте")
            .And.NotContain("не найдена", "владение задачей есть — отказывает именно скоуп проекта");
    }

    /// <summary>
    /// Ось 4: скоупы НАЗНАЧАЕМОЙ персоны, а не вызывателя (как REST Create/Update).
    /// Прямой перекос: проектная персона чужого проекта назначалась через чужую привязку
    /// вызывателя. Обратный: персона с полным ProjectTasks-доступом к целевому проекту
    /// ложно отвергалась, хотя REST разрешал.
    /// </summary>
    [Fact]
    public async Task НазначениеПерсоны_СкоупыНазначаемой_КакУREST()
    {
        var ownProject = await CreateProjectAsync(Client, "persona-own");
        var foreignProject = await CreateProjectAsync(Client, "persona-foreign");
        var sessionId = await CreateSessionAsync(Client, ownProject);

        // Проектная персона ДРУГОГО проекта
        var persona = await Client.PostAsJsonAsync("/api/personas", new
        {
            name = "Чужая проектная",
            scope = "project",
            projectId = foreignProject,
        });
        persona.EnsureSuccessStatusCode();
        var personaId = JsonSerializer.Deserialize<JsonElement>(
            await persona.Content.ReadAsStringAsync()).GetProperty("id").GetString()!;

        // Чужая персона без привязок → отказ с текстом REST-валидатора
        var denied = await CallToolAsync(Client, "tasks", sessionId, "tasks_create",
            new { title = "Задача с чужой персоной", personaId });
        denied.GetProperty("result").GetProperty("isError").GetBoolean().Should().BeTrue();
        ToolText(denied).Should().Contain("Проектная персона может выполнять только задачи своего проекта");

        // Тот же ответ даёт REST — поведение веток совпадает
        var restDenied = await Client.PostAsJsonAsync($"/api/projects/{ownProject}/tasks",
            new { title = "Задача с чужой персоной", personaId });
        restDenied.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await restDenied.Content.ReadAsStringAsync()).Should().Contain("только задачи своего проекта");

        // Обратный перекос: полная ProjectTasks-привязка НАЗНАЧАЕМОЙ персоны к нашему
        // проекту — REST разрешает, http обязан тоже (раньше ложно отказывал скоупами вызывателя)
        var binding = await Client.PutAsJsonAsync($"/api/personas/{personaId}/bindings", new
        {
            bindings = new object[] { new { type = "ProjectTasks", target = ownProject } },
        });
        binding.EnsureSuccessStatusCode();

        var allowed = await ToolJsonAsync(Client, "tasks", sessionId, "tasks_create",
            new { title = "Задача с привязанной персоной", personaId });
        allowed.GetProperty("personaId").GetString().Should().Be(personaId);
    }

    /// <summary>
    /// Ось 5: пустое значение = ОЧИСТИТЬ, а не «не менять» (семантика UpdateTaskRequest и
    /// UpdateNoteRequest, паритет со stdio). Раньше OptionalArg/LabelsArg глотали очистку
    /// молча — модель рапортовала об успехе, поле оставалось прежним.
    /// </summary>
    [Fact]
    public async Task ПустыеЗначения_ОчищаютПоля()
    {
        var project = await CreateProjectAsync(Client, "clear-fields");
        var sessionId = await CreateSessionAsync(Client, project);

        var created = await ToolJsonAsync(Client, "tasks", sessionId, "tasks_create", new
        {
            title = "Очистка полей",
            description = "Старое описание",
            labels = new[] { "метка" },
            resultMarkdown = "Старый итог",
            linkedFiles = new[] { "old.md" },
        });
        var taskId = created.GetProperty("id").GetString()!;

        // Пустая строка описания — очистить (UpdateTaskRequest пишет "" как есть,
        // поле перестаёт держать прежний текст)
        var noDescription = await ToolJsonAsync(Client, "tasks", sessionId, "tasks_update",
            new { id = taskId, description = "" });
        noDescription.GetProperty("description").GetString().Should().Be("",
            "пустая строка description обязана очистить поле, а не «не менять»");

        // Пустой массив меток — снять все метки
        var noLabels = await ToolJsonAsync(Client, "tasks", sessionId, "tasks_update",
            new { id = taskId, labels = Array.Empty<string>() });
        noLabels.GetProperty("labels").GetArrayLength().Should().Be(0,
            "пустой массив labels обязан снимать метки");

        // Пустой массив linkedFiles — очистить список итоговых файлов
        var noFiles = await ToolJsonAsync(Client, "tasks", sessionId, "tasks_update",
            new { id = taskId, linkedFiles = Array.Empty<string>() });
        noFiles.GetProperty("linkedFiles").GetArrayLength().Should().Be(0,
            "пустой массив linkedFiles обязан очищать список");

        // Заметка: пустое содержимое — стереть текст файла
        var note = await ToolJsonAsync(Client, "notes", sessionId, "notes_create", new
        {
            title = "Заметка на очистку",
            content = "# Старый текст",
        });
        var noteId = note.GetProperty("id").GetString()!;
        var clearedNote = await ToolJsonAsync(Client, "notes", sessionId, "notes_update",
            new { id = noteId, content = "" });
        clearedNote.GetProperty("content").GetString().Should().Be("",
            "пустая строка content обязана стирать содержимое заметки");
    }

    /// <summary>
    /// Разметка фичи: пустая строка stepRef/lane в tasks_update — ОЧИСТИТЬ (UpdateTaskRequest:
    /// "" = сбросить), null = не менять. featureId/role при этом не трогаем — та же семантика,
    /// что и у dueDate/worktreePath (ось 5).
    /// </summary>
    [Fact]
    public async Task ФичаПоля_ПустоеЗначение_Очищает()
    {
        var project = await CreateProjectAsync(Client, "clear-feature");
        var sessionId = await CreateSessionAsync(Client, project);

        var epic = await ToolJsonAsync(Client, "tasks", sessionId, "tasks_create",
            new { title = "Эпик", role = "feature" });
        var epicId = epic.GetProperty("id").GetString()!;
        var step = await ToolJsonAsync(Client, "tasks", sessionId, "tasks_create",
            new { title = "Шаг", featureId = epicId, role = "step", stepRef = "D1c", lane = "бэк" });
        var stepId = step.GetProperty("id").GetString()!;

        var cleared = await ToolJsonAsync(Client, "tasks", sessionId, "tasks_update",
            new { id = stepId, stepRef = "", lane = "" });
        cleared.GetProperty("stepRef").ValueKind.Should().Be(JsonValueKind.Null,
            "пустая строка stepRef обязана очистить поле, а не «не менять»");
        cleared.GetProperty("lane").ValueKind.Should().Be(JsonValueKind.Null,
            "пустая строка lane обязана очистить поле");
        cleared.GetProperty("featureId").GetString().Should().Be(epicId, "featureId не тронут");
        cleared.GetProperty("role").GetString().Should().Be("step", "role не тронут");
    }

    /// <summary>
    /// Ось 6: гейт mentions на ВЫЗОВЕ, а не только в составе. При выключенном
    /// tool:consultants инструмент пропадает из tools/list, но вызов обязан отказывать
    /// текстом — без проверки он запускал ПЛАТНЫЙ one-shot ход другой персоны
    /// (у stdio-ветки отказ был, http-копия его потеряла).
    /// </summary>
    [Fact]
    public async Task PersonaAsk_ВыключенныйConsultants_ОтказНаВызове()
    {
        var persona = await Client.PostAsJsonAsync("/api/personas", new { name = "Спрашиваемая" });
        persona.EnsureSuccessStatusCode();
        var personaId = JsonSerializer.Deserialize<JsonElement>(
            await persona.Content.ReadAsStringAsync()).GetProperty("id").GetString()!;

        var chat = await Client.PostAsJsonAsync($"/api/personas/{personaId}/chats",
            new { mode = "acceptEdits" });
        chat.EnsureSuccessStatusCode();
        var sessionId = JsonSerializer.Deserialize<JsonElement>(
            await chat.Content.ReadAsStringAsync()).GetProperty("id").GetString()!;

        // Выключаем консультации у ПЕРСОНЫ ЧАТА (решает MentionsToolsEnabled по сессии)
        var off = await Client.PutAsJsonAsync($"/api/personas/{personaId}/bindings", new
        {
            bindings = new object[] { new { type = "tool", target = "consultants", mode = "off" } },
        });
        off.EnsureSuccessStatusCode();

        var list = await Client.PostAsJsonAsync($"/mcp/personas/{sessionId}",
            new { jsonrpc = "2.0", id = 1, method = "tools/list" });
        var tools = JsonSerializer.Deserialize<JsonElement>(await list.Content.ReadAsStringAsync())
            .GetProperty("result").GetProperty("tools");
        tools.EnumerateArray().Select(t => t.GetProperty("name").GetString())
            .Should().NotContain("persona_ask", "состав уже не экспонирует инструмент");

        var answer = await CallToolAsync(Client, "personas", sessionId, "persona_ask",
            new { handle = "кто-угодно", question = "Ты тут?" });
        answer.GetProperty("result").GetProperty("isError").GetBoolean().Should().BeTrue(
            "вызов выключенного инструмента обязан отказывать, а не запускать платный ход");
        ToolText(answer).Should().Contain("tool:consultants");
    }

    /// <summary>
    /// Ось 7: согласованность состава с отпечатком запуска при ЕДИНСТВЕННОЙ персоне
    /// владельца (блокер 2.1 №4). Старая формула shape смотрела MentionsHint (гаснет —
    /// спрашивать некого), tools/list — ConsultantsEnabled (инструмент остаётся): shape
    /// говорил m0, состав отдавал persona_ask. Теперь обе стороны читают один метод
    /// SessionManager.MentionsToolsEnabled — сверяем состав с его живым значением.
    /// </summary>
    [Fact]
    public async Task ЕдинственнаяПерсона_СоставСогласованСФормулойShape()
    {
        // Свежий владелец: после провижна ровно одна персона, и чат ведёт ОНА САМА —
        // others пуст, MentionsHint null, но persona_ask в составе (сценарий блокера)
        using var client = _factory.CreateAuthenticatedClient(
            TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);
        var users = _factory.Services.GetRequiredService<UserStore>();
        var ownerId = users.FindByUsername(TestWebApplicationFactory.SecondUsername)!.Id;

        var provisioner = _factory.Services.GetRequiredService<DefaultAssistantProvisioner>();
        var persona = await provisioner.EnsureAsync(ownerId);
        persona.Should().NotBeNull("провижн дефолт-персоны обязан срабатывать");

        var all = await client.GetFromJsonAsync<JsonElement>("/api/personas");
        all.GetArrayLength().Should().Be(1, "у свежего владельца ровно одна персона");

        var chat = await client.PostAsJsonAsync($"/api/personas/{persona!.Id}/chats",
            new { mode = "acceptEdits" });
        chat.EnsureSuccessStatusCode();
        var sessionId = JsonSerializer.Deserialize<JsonElement>(
            await chat.Content.ReadAsStringAsync()).GetProperty("id").GetString()!;

        var sessions = _factory.Services.GetRequiredService<SessionManager>();
        var session = sessions.GetOwned(sessionId, ownerId);
        session.Should().NotBeNull("чат создан этим же владельцем");
        session!.PersonaId.Should().Be(persona.Id, "чат ведёт единственная персона владельца");

        // Живое значение ЕДИНОЙ формулы — его же читает построение shape
        var expected = sessions.MentionsToolsEnabled(ownerId, session, persona);

        var list = await client.PostAsJsonAsync($"/mcp/personas/{sessionId}",
            new { jsonrpc = "2.0", id = 1, method = "tools/list" });
        var names = JsonSerializer.Deserialize<JsonElement>(await list.Content.ReadAsStringAsync())
            .GetProperty("result").GetProperty("tools").EnumerateArray()
            .Select(t => t.GetProperty("name").GetString()).ToList();
        names.Contains("persona_ask").Should().Be(expected,
            "состав и отпечаток запуска читают одну формулу MentionsToolsEnabled — "
            + "расхождение перезапускает процесс CLI вхолостую");
        expected.Should().BeTrue("дефолт-персона без Off-привязок: консультации включены");
    }
}
