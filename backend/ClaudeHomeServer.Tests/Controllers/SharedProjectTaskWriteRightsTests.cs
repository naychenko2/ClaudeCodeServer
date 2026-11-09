using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Access;
using ClaudeHomeServer.Services.Tasks;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Controllers;

/// <summary>
/// F2-T1 (разрез docs/research/shared-projects-cut-2026-10.md §2.1): двери записи задач общего
/// проекта — «править задачи» может владелец и редактор, читатель только смотрит.
///
/// Матрица «роль × действие» на настоящем сервере: создать / изменить / перетащить (колонка и
/// порядок) / подзадачи / выполнить / перенести. Владелец — всё. Редактор — всё, кроме переноса
/// между проектами и вывода задачи в личные (право Write: это вывод данных из проекта, а не
/// правка в нём; настройки доски — Configure). Читатель — 403 с машинным кодом project_scope на
/// всех путях записи, включая подзадачи, порядок и запуск исполнителя. Посторонний — 404:
/// существование не раскрываем.
///
/// Удаление — отдельное правило (по образцу чатов, вопрос 2 макета): автор задачи (CreatedByUserId)
/// или её владелец. Редактору на чужой задаче — 403 not_task_author с причиной; читателю — прежний
/// 403 project_scope (права нет до авторства); задаче без автора — тот же fail-closed, что чату
/// без автора.
///
/// Фичевые поля и жизненный цикл фичи — отдельная линия (возврат ревью R-F2-T1): дверь
/// WriteContent открыта редактору, но она не отдаёт ему ведение фичи арендатора. Роль, фичу,
/// план, связь с ревью и статус задачи-эпика правит только владелец; вердикт и свидетельства —
/// фичевые поля только у задачи фичи (её Role или FeatureId заданы), у обычной задачи и дефекта
/// это исполнительские поля (возврат дельта-ревью c2b408f8: фронт шлёт verification при каждом
/// сохранении, и безусловный гейт закрывал редактору даже обычный дефект).
/// (FeatureOwnerGate — один предикат на REST PUT и POST создания и на MCP
/// tasks_update/tasks_complete/tasks_create, F2-M2; статус мерж-задачи — тоже жизнь фичи:
/// «merged» выводится из его закрытия, FeatureProjector.cs:83);
/// статус обычной задачи, заголовок, описание, подзадачи, колонка, порядок, исполнитель и срок
/// остаются редактору.
///
/// Ключ хранилища не меняется: задача, созданная редактором, лежит под арендатором проекта
/// (TaskItem.OwnerId — владелец проекта), автор — тот, кто поставил (CreatedByUserId). Замок
/// ProjectAccessService (право Write участнику) и матрица ProjectAccess.RoleHasRight этим шагом
/// не тронуты: переводятся двери, а не права.
///
/// Мост «чекбокс заметки → задача» (MCP notes_promote_task) — та же дверь записи задач общего
/// проекта: право мерится по актору хода тем же Write, что у REST-двери (возврат ревью R-F2-D1:
/// агент редактора не может больше человека-редактора; раньше мост мерил владельца записи —
/// арендатора — и был открыт агенту при закрытом человеке).
///
/// «Выполнить» проверяется на уже завершённой задаче: прогон исполнителя здесь не нужен (он —
/// TasksRunExecutorActorTests), а конкретный 400 «задача уже завершена» (а не «не 403») доказывает,
/// что правом не отшили и до тела ручки дошло.
/// </summary>
[Trait("Category", "Host")]
public class SharedProjectTaskWriteRightsTests : IClassFixture<TestWebApplicationFactory>
{
    private const string Owner = "owner", Editor = "editor", Reader = "reader", Outsider = "outsider";

    // 200 в ожиданиях значит «любой 2xx»
    private const int Ok2xx = 200;

    private const string NoRight = ProjectScopeAttribute.ErrorCode;
    private const string NotAuthor = TaskAuthorGate.ErrorCode;
    private const string NotFeatureOwner = FeatureOwnerGate.ErrorCode;

    private readonly TestWebApplicationFactory _factory;

    public SharedProjectTaskWriteRightsTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
        _factory.ExtraConfig[SharedProjectsGate.SwitchKey] = "true";
    }

    private HttpClient OwnerClient() => _factory.CreateAuthenticatedClient();

    private HttpClient SecondClient() => _factory.CreateAuthenticatedClient(
        TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);

    private HttpClient ClientOf(string role) => role == Owner ? OwnerClient() : SecondClient();

    private TaskManager Tasks => _factory.Services.GetRequiredService<TaskManager>();

    private string SecondId => _factory.Services
        .GetRequiredService<UserStore>().FindByUsername(TestWebApplicationFactory.SecondUsername)!.Id;

    private string TenantId => _factory.Services
        .GetRequiredService<UserStore>().FindByUsername(TestWebApplicationFactory.TestUsername)!.Id;

    private static async Task SetFlagAsync(HttpClient client, bool on) =>
        (await client.PutAsJsonAsync("/api/feature-flags/shared-projects", new { enabled = on }))
            .EnsureSuccessStatusCode();

    private static string IdOf(string json) =>
        JsonSerializer.Deserialize<JsonElement>(json).GetProperty("id").GetString()!;

    /// <summary>
    /// Проект владельца с одной задачей в нём; второй пользователь — участник роли role
    /// (owner/outsider — участника нет). closeFirst: задача закрывается (строка «выполнить»:
    /// важен отказ/проход права, не прогон).
    /// </summary>
    private async Task<(string ProjectId, string TaskId)> ArrangeAsync(string role, bool closeFirst = false)
    {
        var owner = OwnerClient();
        await SetFlagAsync(owner, true);
        if (role is Editor or Reader) await SetFlagAsync(SecondClient(), true);

        var dir = Directory.CreateDirectory(Path.Combine(_factory.TempDir, "tw_" + Guid.NewGuid().ToString("N")[..8])).FullName;
        var created = await owner.PostAsJsonAsync("/api/projects",
            new { name = "Права записи" + Guid.NewGuid().ToString("N")[..6], rootPath = dir });
        created.EnsureSuccessStatusCode();
        var projectId = IdOf(await created.Content.ReadAsStringAsync());

        if (role is Editor or Reader)
            (await owner.PostAsJsonAsync($"/api/projects/{projectId}/members",
                new { username = TestWebApplicationFactory.SecondUsername, role })).EnsureSuccessStatusCode();

        var task = await owner.PostAsJsonAsync($"/api/projects/{projectId}/tasks", new { title = "Задача проекта" });
        task.EnsureSuccessStatusCode();
        var taskId = IdOf(await task.Content.ReadAsStringAsync());
        if (closeFirst)
            (await owner.PutAsJsonAsync($"/api/tasks/{taskId}", new { status = "done" })).EnsureSuccessStatusCode();
        return (projectId, taskId);
    }

    private static async Task<(int Status, string? Code)> SendAsync(
        HttpClient client, HttpMethod method, string url, object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        if (body is not null) request.Content = JsonContent.Create(body);
        var response = await client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        string? code = null;
        if (text.Length > 0)
        {
            try
            {
                var json = JsonSerializer.Deserialize<JsonElement>(text);
                if (json.TryGetProperty("code", out var c)) code = c.GetString();
            }
            catch (JsonException) { /* ответ не JSON — кода нет */ }
        }
        return ((int)response.StatusCode, code);
    }

    /// <summary>Действие матрицы: {p} — проект, {t} — задача владельца в нём. Ожидания по ролям.</summary>
    private sealed record Act(string Name, HttpMethod Method, string Path, object? Body,
        int ForOwner, int ForEditor, int ForReader, int ForOutsider, bool CloseFirst = false)
    {
        public int For(string role) => role switch
        {
            Owner => ForOwner, Editor => ForEditor, Reader => ForReader, _ => ForOutsider,
        };
    }

    private static readonly Act[] Actions =
    [
        new("создать", HttpMethod.Post, "/api/projects/{p}/tasks", new { title = "Задача участника" }, Ok2xx, Ok2xx, 403, 404),
        new("изменить", HttpMethod.Put, "/api/tasks/{t}", new { title = "перебил" }, Ok2xx, Ok2xx, 403, 404),
        // перетаскивание доски: колонка и порядок — та же дверь правки (WriteContent)
        new("перетащить", HttpMethod.Put, "/api/tasks/{t}", new { columnId = "inProgress", order = 1500.0 }, Ok2xx, Ok2xx, 403, 404),
        new("подзадачи", HttpMethod.Put, "/api/tasks/{t}",
            new { subtasks = new[] { new { id = "", title = "шаг", isDone = false } } }, Ok2xx, Ok2xx, 403, 404),
        // статус ОБЫЧНОЙ задачи — исполнительское поле: редактор может (страховка от того, что
        // фичевая дверь R-F2-T1 не перешла на любой status)
        new("закрыть", HttpMethod.Put, "/api/tasks/{t}", new { status = "done" }, Ok2xx, Ok2xx, 403, 404),
        // запуск исполнителя — право RunTurn: у редактора оно есть с S2a-10, у читателя нет.
        // Ожидание — конкретный 400 «задача уже завершена» (TaskExecutionService), а не «не 403»:
        // «не 403» прошёл бы и на 404 «ручка не нашла задачу», и на 500 — это не доказательство,
        // что правом не отшили и до тела ручки дошло (хвост ревью R-F2-T1 п.1-2)
        new("выполнить", HttpMethod.Post, "/api/tasks/{t}/execute", null, 400, 400, 403, 404, CloseFirst: true),
        // вывод задачи из общего проекта в личные — право Write, только владельцу
        new("перенести в личные", HttpMethod.Put, "/api/tasks/{t}", new { projectId = "" }, Ok2xx, 403, 403, 404),
    ];

    public static IEnumerable<object[]> Matrix() =>
        from a in Actions
        from role in new[] { Owner, Editor, Reader, Outsider }
        select new object[] { a.Name, role };

    [Theory]
    [MemberData(nameof(Matrix))]
    public async Task РольНаДвериЗаписи_ДаётОжидаемыйОтказ(string actionName, string role)
    {
        var action = Actions.Single(a => a.Name == actionName);
        var (projectId, taskId) = await ArrangeAsync(role, action.CloseFirst);
        var titleBefore = Tasks.GetById(taskId)!.Title;

        var (status, code) = await SendAsync(ClientOf(role), action.Method,
            action.Path.Replace("{p}", projectId).Replace("{t}", taskId), action.Body);

        var expected = action.For(role);
        if (expected == Ok2xx)
        {
            (status >= 200 && status < 300).Should().BeTrue($"{role}: {actionName} → {status}");
        }
        else
        {
            status.Should().Be(expected, $"{role}: {actionName}");
            if (expected == 403)
                code.Should().Be(NoRight, $"{role}: {actionName} — отказ различается машинным кодом");
        }

        // Отказанное действие не оставило следа: ни правки, ни новой задачи
        if (expected is 400 or 403 or 404)
        {
            if (action.Method == HttpMethod.Post && action.Path.Contains("/tasks", StringComparison.Ordinal)
                && !action.Path.Contains("{t}", StringComparison.Ordinal))
                Tasks.GetByProject(projectId).Should().HaveCount(1, $"{role}: {actionName} отказано — новой задачи нет");
            else
                Tasks.GetById(taskId)!.Title.Should().Be(titleBefore, $"{role}: {actionName} отказано — запись не изменена");
        }
    }

    // ── ключ хранилища и автор: задача участника лежит под арендатором, автор — участник ──

    [Fact]
    public async Task РедакторСоздаётЗадачу_ПодКлючомАрендатора_АвторРедактор_ВладелецЕёВидит()
    {
        var (projectId, _) = await ArrangeAsync(Editor);

        var created = await SecondClient().PostAsJsonAsync($"/api/projects/{projectId}/tasks",
            new { title = "Задача редактора" });
        created.StatusCode.Should().Be(HttpStatusCode.OK);
        var taskId = IdOf(await created.Content.ReadAsStringAsync());

        var task = Tasks.GetById(taskId)!;
        task.ProjectId.Should().Be(projectId);
        task.OwnerId.Should().Be(TenantId,
            "ключ записи проектной задачи — арендатор: иначе задача участника уедет из календаря и выдачи владельца");
        task.CreatedByUserId.Should().Be(SecondId, "автор — тот, кто поставил задачу: по нему решается удаление");

        // Задача остаётся в выдаче и календаре арендатора
        (await OwnerClient().GetAsync($"/api/tasks/{taskId}")).EnsureSuccessStatusCode();
        (await ProjectTaskIdsAsync(OwnerClient(), projectId)).Should().Contain(taskId);
    }

    private async Task<List<string>> ProjectTaskIdsAsync(HttpClient client, string projectId)
    {
        var r = await client.GetAsync($"/api/projects/{projectId}/tasks");
        r.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<JsonElement>(await r.Content.ReadAsStringAsync())
            .EnumerateArray().Select(t => t.GetProperty("id").GetString()!).ToList();
    }

    // ── удаление: автор или владелец (по образцу чатов) ────────────────────────────

    [Fact]
    public async Task АвторРедакторУдаляетСвоюЗадачу_204_ВладелецУдаляетЧужую_204()
    {
        var (projectId, ownerTaskId) = await ArrangeAsync(Editor);
        var ownTaskId = IdOf(await (await SecondClient().PostAsJsonAsync($"/api/projects/{projectId}/tasks",
            new { title = "Своя задача редактора" })).Content.ReadAsStringAsync());

        (await SendAsync(SecondClient(), HttpMethod.Delete, $"/api/tasks/{ownTaskId}")).Status
            .Should().Be(204, "автор удаляет свою задачу");
        Tasks.GetById(ownTaskId).Should().BeNull();

        (await SendAsync(OwnerClient(), HttpMethod.Delete, $"/api/tasks/{ownerTaskId}")).Status
            .Should().Be(204, "владелец удаляет задачу, которую поставил не он");
        Tasks.GetById(ownerTaskId).Should().BeNull();
    }

    [Fact]
    public async Task РедакторНаЧужойЗадаче_403notTaskAuthor_ЗадачаЦела_АвторНеИзменён()
    {
        var (_, ownerTaskId) = await ArrangeAsync(Editor);

        var (status, code) = await SendAsync(SecondClient(), HttpMethod.Delete, $"/api/tasks/{ownerTaskId}");

        status.Should().Be(403);
        code.Should().Be(NotAuthor, "по коду фронт выключает кнопку с причиной");
        Tasks.GetById(ownerTaskId).Should().NotBeNull("отказ необратимого действия не трогает запись");
        Tasks.GetById(ownerTaskId)!.CreatedByUserId.Should().Be(TenantId);
    }

    [Fact]
    public async Task ЧитательНеУдаляет_403projectScope_ЗадачаЦела()
    {
        // Читатель не может поставить задачу, поэтому проверяется единственная формулировка:
        // на задаче проекта право отказывает раньше авторства
        var (_, taskId) = await ArrangeAsync(Reader);

        var (status, code) = await SendAsync(SecondClient(), HttpMethod.Delete, $"/api/tasks/{taskId}");

        status.Should().Be(403);
        code.Should().Be(NoRight, "читателю отказывает право, а не авторство");
        Tasks.GetById(taskId).Should().NotBeNull();
    }

    [Fact]
    public async Task СтараяЗадачаБезАвтора_Редактору403_Владельцу204()
    {
        // Серверный путь создания (без CreatedByHuman и без названного актора) — автор
        // неизвестен: тот же fail-closed, что у чата без автора
        var (projectId, _) = await ArrangeAsync(Editor);
        var legacy = Tasks.Create(projectId, TenantId, new CreateTaskRequest("Задача без автора"));

        var (status, code) = await SendAsync(SecondClient(), HttpMethod.Delete, $"/api/tasks/{legacy.Id}");
        status.Should().Be(403);
        code.Should().Be(NotAuthor);
        Tasks.GetById(legacy.Id).Should().NotBeNull();

        (await SendAsync(OwnerClient(), HttpMethod.Delete, $"/api/tasks/{legacy.Id}")).Status.Should().Be(204);
    }

    // ── MCP-дверь tasks_delete: то же правило, что REST (агент не больше человека) ────

    private async Task<(string ProjectId, string TaskId, string ChatId)> SharedWithChatAsync(string role = Editor)
    {
        var (projectId, taskId) = await ArrangeAsync(role);
        var chat = await OwnerClient().PostAsJsonAsync($"/api/projects/{projectId}/sessions", new { mode = "auto" });
        chat.EnsureSuccessStatusCode();
        return (projectId, taskId, IdOf(await chat.Content.ReadAsStringAsync()));
    }

    /// <summary>Ход в общем чате запускает редактор: актор идущего хода пишется в реестр
    /// SessionManager — тот же источник, что читает гейт актора и дверь удаления.</summary>
    private async Task TurnAsEditorAsync(string chatId)
    {
        (await SecondClient().PostAsJsonAsync($"/api/sessions/{chatId}/messages",
            new { text = "ход редактора", wait = "none" })).EnsureSuccessStatusCode();
        _factory.Services.GetRequiredService<SessionManager>().GetTurnActor(chatId, TenantId)
            .Should().Be(SecondId, "актор идущего хода — редактор");
    }

    private static async Task<(string Text, bool IsError)> McpCallAsync(
        HttpClient client, string chatId, string tool, object args)
    {
        var resp = await client.AsCaller(chatId).PostAsJsonAsync($"/mcp/tasks/{chatId}", new
        {
            jsonrpc = "2.0", id = 1, method = "tools/call",
            @params = new { name = tool, arguments = args },
        });
        resp.EnsureSuccessStatusCode();
        var result = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync())
            .GetProperty("result");
        var isError = result.TryGetProperty("isError", out var e) && e.GetBoolean();
        return (result.GetProperty("content")[0].GetProperty("text").GetString()!, isError);
    }

    [Fact]
    public async Task АгентРедактора_tasks_delete_ЧужуюЗадачу_ОтказСТемЖеКодом_ЗадачаЦела()
    {
        var (_, taskId, chatId) = await SharedWithChatAsync();
        await TurnAsEditorAsync(chatId);

        var (text, isError) = await McpCallAsync(OwnerClient(), chatId, "tasks_delete", new { id = taskId });

        isError.Should().BeTrue(text);
        text.Should().Contain(NotAuthor,
            "MCP-дверь и REST-дверь удаляют по одному правилу: второй точки правды нет");
        Tasks.GetById(taskId).Should().NotBeNull();
    }

    [Fact]
    public async Task АгентРедактора_tasks_createЗатемdelete_СвоюЗадачу_Удаляет()
    {
        var (projectId, _, chatId) = await SharedWithChatAsync();
        await TurnAsEditorAsync(chatId);

        var (createdText, createError) = await McpCallAsync(OwnerClient(), chatId, "tasks_create",
            new { title = "Задача агента редактора" });
        createError.Should().BeFalse(createdText);
        var newId = JsonSerializer.Deserialize<JsonElement>(createdText).GetProperty("id").GetString()!;

        // Авторство по актору хода, а не по токену арендатора — по нему и удаление разрешено
        Tasks.GetById(newId)!.OwnerId.Should().Be(TenantId, "ключ записи — арендатор проекта");
        Tasks.GetById(newId)!.CreatedByUserId.Should().Be(SecondId);

        var (_, deleteError) = await McpCallAsync(OwnerClient(), chatId, "tasks_delete", new { id = newId });
        deleteError.Should().BeFalse("автор удаляет свою задачу");
        Tasks.GetById(newId).Should().BeNull();
        _ = projectId;
    }

    // ── перенос между проектами остаётся владельцу (право Write) ─────────────────────

    // Второй проект — с тем же редактором в участниках: отказ именно по праву переноса
    // (Write), а не по видимости проекта (невидимому — прежний 400 «проект недоступен»)
    [Fact]
    public async Task РедакторНеПереноситЗадачуВДругойПроект_403_ПроектЗадачиНеИзменён()
    {
        var (projectId, taskId) = await ArrangeAsync(Editor);
        var otherDir = Directory.CreateDirectory(Path.Combine(_factory.TempDir, "tw_other_" + Guid.NewGuid().ToString("N")[..6])).FullName;
        var other = IdOf(await (await OwnerClient().PostAsJsonAsync("/api/projects",
            new { name = "Другой" + Guid.NewGuid().ToString("N")[..6], rootPath = otherDir })).Content.ReadAsStringAsync());
        (await OwnerClient().PostAsJsonAsync($"/api/projects/{other}/members",
            new { username = TestWebApplicationFactory.SecondUsername, role = "editor" })).EnsureSuccessStatusCode();

        var (status, code) = await SendAsync(SecondClient(), HttpMethod.Put, $"/api/tasks/{taskId}",
            new { projectId = other });

        status.Should().Be(403);
        code.Should().Be(NoRight);
        Tasks.GetById(taskId)!.ProjectId.Should().Be(projectId, "отказ не переносит задачу");
    }

    // ── фичевые поля и жизнь фичи — только владельцу проекта (возврат ревью R-F2-T1) ────

    /// <summary>Эпик фичи арендатора в том же общем проекте (role=feature).</summary>
    private async Task<string> SharedEpicAsync(string projectId)
    {
        var epic = await OwnerClient().PostAsJsonAsync($"/api/projects/{projectId}/tasks",
            new { title = "Эпик фичи" + Guid.NewGuid().ToString("N")[..6], role = "feature" });
        epic.EnsureSuccessStatusCode();
        return IdOf(await epic.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task РедакторНеСтавитФичевыеПоля_403featureOwner_ЗаписьНеТронута_ВладелецМожет()
    {
        var (projectId, taskId) = await ArrangeAsync(Editor);
        var epicId = await SharedEpicAsync(projectId);

        var (status, code) = await SendAsync(SecondClient(), HttpMethod.Put, $"/api/tasks/{taskId}",
            new { role = "final", featureId = epicId });

        status.Should().Be(403);
        code.Should().Be(NotFeatureOwner, "по коду фронт выключает фичевые контролы с причиной");
        var untouched = Tasks.GetById(taskId)!;
        untouched.Role.Should().BeNull("отказ не пишет роль");
        untouched.FeatureId.Should().BeNull("отказ не привязывает к фиче");

        // Тот же ход владельцу открыт: отказывает роль вызывающего, а не состав полей
        (await SendAsync(OwnerClient(), HttpMethod.Put, $"/api/tasks/{taskId}",
            new { role = "final", featureId = epicId })).Status.Should().Be(200, "фичу ведёт владелец");
        Tasks.GetById(taskId)!.Role.Should().Be("final");
    }

    [Fact]
    public async Task РедакторНеЗакрываетЭпикФичи_403featureOwner_ЭпикОткрыт_ВладелецЗакрывает()
    {
        var (projectId, _) = await ArrangeAsync(Editor);
        var epicId = await SharedEpicAsync(projectId);

        var (status, code) = await SendAsync(SecondClient(), HttpMethod.Put, $"/api/tasks/{epicId}",
            new { status = "done", resultMarkdown = "фичу закрыл участник" });

        status.Should().Be(403);
        code.Should().Be(NotFeatureOwner);
        Tasks.GetById(epicId)!.Status.Should().Be(TaskItemStatus.Todo,
            "закрыть или возобновить эпик — решение владельца: из него FeatureProjector выводит состояние фичи");

        (await SendAsync(OwnerClient(), HttpMethod.Put, $"/api/tasks/{epicId}", new { status = "done" }))
            .Status.Should().Be(200);
        Tasks.GetById(epicId)!.Status.Should().Be(TaskItemStatus.Done);
    }

    [Fact]
    public async Task АгентРедактора_tasksUpdateФичевыеПоля_ТотЖеОтказ_ЗадачаЦела()
    {
        var (projectId, taskId, chatId) = await SharedWithChatAsync();
        await TurnAsEditorAsync(chatId);
        var epicId = await SharedEpicAsync(projectId);

        var (text, isError) = await McpCallAsync(OwnerClient(), chatId, "tasks_update",
            new { id = taskId, role = "final", featureId = epicId });

        isError.Should().BeTrue(text);
        text.Should().Contain(NotFeatureOwner,
            "MCP-дверь и REST-дверь отказывают одним предикатом (FeatureOwnerGate): второй точки правды нет");
        Tasks.GetById(taskId)!.Role.Should().BeNull();
        Tasks.GetById(taskId)!.FeatureId.Should().BeNull();

        // Исполнительские поля тот же агент правит свободно — отказ ровно по фичевым
        var (titleText, titleError) = await McpCallAsync(OwnerClient(), chatId, "tasks_update",
            new { id = taskId, title = "перебил агент" });
        titleError.Should().BeFalse(titleText);
        Tasks.GetById(taskId)!.Title.Should().Be("перебил агент");
    }

    [Fact]
    public async Task АгентРедактора_tasksCompleteЭпикФичи_ТотЖеОтказ_ЭпикНеЗакрыт()
    {
        var (projectId, _, chatId) = await SharedWithChatAsync();
        await TurnAsEditorAsync(chatId);
        var epicId = await SharedEpicAsync(projectId);

        var (text, isError) = await McpCallAsync(OwnerClient(), chatId, "tasks_complete",
            new { id = epicId, resultMarkdown = "фичу закрыл агент" });

        isError.Should().BeTrue(text);
        text.Should().Contain(NotFeatureOwner, "тот же предикат, что у tasks_update и REST PUT");
        Tasks.GetById(epicId)!.Status.Should().Be(TaskItemStatus.Todo, "задача цела: отказ до мутации");
    }

    [Fact]
    public async Task РедакторЗакрываетОбычныйДефектСВердиком_200_ВердикЗаписан()
    {
        // Возврат дельта-ревью c2b408f8: TaskEditForm шлёт verification при каждом сохранении
        // карточки, и безусловный гейт закрывал редактору даже обычный дефект с заметкой
        // проверки — он не мог сохранить даже заголовок. Вердикт вне фичи — исполнительское поле.
        var (projectId, _) = await ArrangeAsync(Editor);
        var defectId = IdOf(await (await OwnerClient().PostAsJsonAsync($"/api/projects/{projectId}/tasks",
            new { title = "Обычный дефект" + Guid.NewGuid().ToString("N")[..6], kind = "defect",
                  repro = new { steps = "Открыть карточку дефекта" } })).Content.ReadAsStringAsync());

        var (status, code) = await SendAsync(SecondClient(), HttpMethod.Put, $"/api/tasks/{defectId}",
            new { kind = "defect", status = "done",
                  verification = new { notes = "Проверено: дефект не воспроизводится" } });

        status.Should().Be(200, $"редактор закрывает обычный дефект с заметкой проверки (code={code})");
        var closed = Tasks.GetById(defectId)!;
        closed.Status.Should().Be(TaskItemStatus.Done);
        closed.Verification?.Notes.Should().Be("Проверено: дефект не воспроизводится",
            "вердикт записан, а не отброшен молча");
    }

    [Fact]
    public async Task РедакторНеСтавитВердикШагуФичи_403featureOwner_ЗаписьЦела_ВладелецМожет()
    {
        // Вторая половина той же правки: у задачи фичи (Role/FeatureId заданы) вердикт и
        // свидетельства остаются закрытыми — их ведёт постановщик фичи.
        var (projectId, _) = await ArrangeAsync(Editor);
        var epicId = await SharedEpicAsync(projectId);
        var stepId = IdOf(await (await OwnerClient().PostAsJsonAsync($"/api/projects/{projectId}/tasks",
            new { title = "Шаг фичи" + Guid.NewGuid().ToString("N")[..6], role = "step",
                  featureId = epicId, stepRef = "T1", lane = "back" })).Content.ReadAsStringAsync());

        var (status, code) = await SendAsync(SecondClient(), HttpMethod.Put, $"/api/tasks/{stepId}",
            new { verification = new { notes = "вердикт редактора" },
                  evidence = new[] { new { criterionId = "c1", how = "прогон", result = "зелёный" } } });

        status.Should().Be(403);
        code.Should().Be(NotFeatureOwner, "вердикт и свидетельства шага фичи — решение постановщика");
        Tasks.GetById(stepId)!.Verification.Should().BeNull("отказ до мутации");
        Tasks.GetById(stepId)!.Evidence.Should().BeEmpty();

        (await SendAsync(OwnerClient(), HttpMethod.Put, $"/api/tasks/{stepId}",
            new { verification = new { notes = "вердикт владельца" } })).Status.Should().Be(200,
            "фичу ведёт владелец");
        Tasks.GetById(stepId)!.Verification?.Notes.Should().Be("вердикт владельца");
    }

    // ── дверь создания: тот же FeatureOwnerGate (F2-M2, возврат финала db216f32) ──────────

    /// <summary>Фича «готова к мержу»: принятый финал как проектная задача (форма —
    /// TasksControllerTests.CreateAcceptedFinal; гард role=merge в TaskManager.cs:722).</summary>
    private async Task AcceptedFinalAsync(string projectId, string epicId)
    {
        var final = await OwnerClient().PostAsJsonAsync($"/api/projects/{projectId}/tasks",
            new { title = "Финал фичи", role = "final", featureId = epicId,
                  status = "done", verification = new { verdict = "accepted" } });
        final.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task РедакторНеСоздаётЗадачуФичи_403featureOwner_ЗадачиНет_ВладелецСоздаёт()
    {
        // До правки гейт стоял только на дверях правки: редактор мог завести в фиче арендатора
        // финал с вердиктом accepted и мерж-задачу — и фича доходила до «готова к мержу» без
        // владельца. Фичу заранее делаем Ready (принятый финал владельцем): отказ редактору
        // идёт по гейту владельца, а не по состоянию фичи — по состоянию его дал бы и TaskManager.
        var (projectId, _) = await ArrangeAsync(Editor);
        var epicId = await SharedEpicAsync(projectId);
        await AcceptedFinalAsync(projectId, epicId);
        var before = Tasks.GetByProject(projectId).Count;

        var (mergeStatus, mergeCode) = await SendAsync(SecondClient(), HttpMethod.Post, $"/api/projects/{projectId}/tasks",
            new { title = "Мерж фичи от редактора", role = "merge", featureId = epicId });
        mergeStatus.Should().Be(403);
        mergeCode.Should().Be(NotFeatureOwner, "по коду фронт выключает фичевые контролы создания с причиной");

        var (finalStatus, finalCode) = await SendAsync(SecondClient(), HttpMethod.Post, $"/api/projects/{projectId}/tasks",
            new { title = "Финал фичи от редактора", role = "final", featureId = epicId,
                  verification = new { verdict = "accepted" } });
        finalStatus.Should().Be(403);
        finalCode.Should().Be(NotFeatureOwner);

        Tasks.GetByProject(projectId).Count.Should().Be(before, "отказанное создание не заводит задачу");

        // Тот же ход владельцу открыт: отказывает роль вызывающего, а не состав полей
        var ok = await OwnerClient().PostAsJsonAsync($"/api/projects/{projectId}/tasks",
            new { title = "Мерж фичи", role = "merge", featureId = epicId });
        var okBody = await ok.Content.ReadAsStringAsync();
        ok.StatusCode.Should().Be(HttpStatusCode.OK, $"фичу ведёт владелец: {okBody}");
        var mergeId = IdOf(okBody);
        Tasks.GetById(mergeId)!.Role.Should().Be("merge");
        Tasks.GetById(mergeId)!.FeatureId.Should().Be(epicId);
    }

    [Fact]
    public async Task АгентРедактора_tasksCreateФичевыеПоля_ТотЖеОтказ_ЗадачиНет_ОбычнуюСоздаёт()
    {
        var (projectId, _, chatId) = await SharedWithChatAsync();
        await TurnAsEditorAsync(chatId);
        var epicId = await SharedEpicAsync(projectId);
        var before = Tasks.GetByProject(projectId).Count;

        var (text, isError) = await McpCallAsync(OwnerClient(), chatId, "tasks_create",
            new { title = "Шаг фичи от агента", role = "step", featureId = epicId, stepRef = "М1", lane = "back" });
        isError.Should().BeTrue(text);
        text.Should().Contain(NotFeatureOwner,
            "дверь создания и дверь правки отказывают одним предикатом (FeatureOwnerGate): второй точки правды нет");
        Tasks.GetByProject(projectId).Count.Should().Be(before, "отказ до первой мутации");

        // Исполнительские поля остаются агенту: обычная задача создаётся как раньше
        // (матрица Actions покрывает REST-дверь, здесь — MCP-дверь)
        var (plainText, plainError) = await McpCallAsync(OwnerClient(), chatId, "tasks_create",
            new { title = "Обычная задача агента" });
        plainError.Should().BeFalse(plainText);
        Tasks.GetByProject(projectId).Count.Should().Be(before + 1);
    }

    // ── мерж фичи: дверь по вызывающему, не по проекту (пункт 2 дельта-ревью 94ef33f0) ──

    [Fact]
    public async Task РедакторНеМержитФичуАрендатора_404_МержЗадачаНеСоздана()
    {
        // MergeFeature ищет эпик среди задач ВЫЗЫВАЮЩЕГО (TasksController.cs:1481 →
        // FeatureProjector.Project → TaskManager.GetByOwner, TaskManager.cs:338). Проектная
        // задача всегда лежит под арендатором (тот же ключ, что у задачи редактора выше),
        // поэтому редактор общего проекта не доходит ни до гарда финала, ни до записи
        // мерж-задачи (:1587): ключ «UserId» в мерже достижим только владельцу фичи.
        var (projectId, _) = await ArrangeAsync(Editor);
        var epicId = await SharedEpicAsync(projectId);

        var denied = await SecondClient().PostAsync($"/api/tasks/features/{epicId}/merge", null);
        var deniedBody = await denied.Content.ReadFromJsonAsync<JsonElement>();

        denied.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "чужая фича для редактора — «нет», тот же 404 и то же тело, что у GET features/{id}");
        deniedBody.GetProperty("error").GetString().Should().Be("Фича не найдена");
        Tasks.GetByProject(projectId).Should().NotContain(t => t.Role == TaskRoles.Merge,
            "отказанное действие не заводит мерж-задачу");

        // Тот же вызов владельцу — не 404, а 409 «финал не принят»: редактору отказывает не
        // состояние фичи и не состав гардов, а то, что фича не его (дверь по актору).
        var owner = await OwnerClient().PostAsync($"/api/tasks/features/{epicId}/merge", null);
        var ownerBody = await owner.Content.ReadFromJsonAsync<JsonElement>();
        owner.StatusCode.Should().Be(HttpStatusCode.Conflict, $"эпик свой для владельца: {ownerBody}");
        ownerBody.GetProperty("error").GetString().Should().Contain("Финал не принят");
        Tasks.GetByProject(projectId).Should().NotContain(t => t.Role == TaskRoles.Merge,
            "преждевременный мерж тоже ничего не пишет");
    }

    [Fact]
    public async Task РедакторНеЗакрываетМержЗадачуФичи_403featureOwner_МержОткрыт_ВладелецЗакрывает()
    {
        // a64356f9 п.2: состояние «merged» выводится из закрытого мержа (closedMerges →
        // TerminalState, FeatureProjector.cs:83), поэтому статус мерж-задачи — жизнь фичи, а не
        // исполнительское поле: клауза статуса FeatureOwnerGate.Touches покрывает и role=merge.
        var (projectId, _) = await ArrangeAsync(Editor);
        var epicId = await SharedEpicAsync(projectId);
        await AcceptedFinalAsync(projectId, epicId);
        var mergeCreated = await OwnerClient().PostAsJsonAsync($"/api/projects/{projectId}/tasks",
            new { title = "Мерж фичи", role = "merge", featureId = epicId });
        mergeCreated.EnsureSuccessStatusCode();
        var mergeId = IdOf(await mergeCreated.Content.ReadAsStringAsync());

        var (status, code) = await SendAsync(SecondClient(), HttpMethod.Put, $"/api/tasks/{mergeId}",
            new { status = "done", resultMarkdown = "мерж закрыл редактор" });

        status.Should().Be(403);
        code.Should().Be(NotFeatureOwner);
        Tasks.GetById(mergeId)!.Status.Should().Be(TaskItemStatus.Todo,
            "мерж-задача открыта: фича не merged — её состояние выводится только из закрытия мержа");

        (await SendAsync(OwnerClient(), HttpMethod.Put, $"/api/tasks/{mergeId}", new { status = "done" }))
            .Status.Should().Be(200, "жизнь фичи — решение владельца");
        Tasks.GetById(mergeId)!.Status.Should().Be(TaskItemStatus.Done);
    }

    // ── мост «чекбокс заметки → задача»: агентская дверь по актору хода (возврат ревью R-F2-D1) ──

    private static async Task<(string Text, bool IsError)> NotesMcpCallAsync(
        HttpClient client, string chatId, string tool, object args)
    {
        var resp = await client.AsCaller(chatId).PostAsJsonAsync($"/mcp/notes/{chatId}", new
        {
            jsonrpc = "2.0", id = 1, method = "tools/call",
            @params = new { name = tool, arguments = args },
        });
        resp.EnsureSuccessStatusCode();
        var result = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync())
            .GetProperty("result");
        var isError = result.TryGetProperty("isError", out var e) && e.GetBoolean();
        return (result.GetProperty("content")[0].GetProperty("text").GetString()!, isError);
    }

    /// <summary>Заметка с чекбоксом в общем проекте — создана агентом через notes_create в
    /// контексте своего чата (шаги 1–2 воспроизведения дефекта).</summary>
    private async Task<string> NoteWithCheckboxAsync(HttpClient client, string chatId)
    {
        var (text, isError) = await NotesMcpCallAsync(client, chatId, "notes_create",
            new { title = "Дела чата", content = "- [ ] Задача из чекбокса" });
        isError.Should().BeFalse(text);
        return JsonSerializer.Deserialize<JsonElement>(text).GetProperty("id").GetString()!;
    }

    /// <summary>notes_promote_task — из модуля «комментарии и редкие операции» (привязка
    /// notes-annotations, NotesToolset.cs:118): дефолтной персоне тест-чата модуль не выдан, и
    /// вызов отказывался на гейте модуля, не доходя до права актора (проверено прогоном).
    /// Привязка включается персоне чата, чтобы тест мерил дверь, а не модуль.</summary>
    private void EnableNotesAnnotationsForChatPersona(string chatId)
    {
        var session = _factory.Services.GetRequiredService<SessionManager>().GetOwnedVisible(chatId, TenantId)!;
        var personas = _factory.Services.GetRequiredService<PersonaManager>();
        var persona = personas.Get(session.PersonaId!, TenantId)!;
        personas.UpdateBindings(persona.Id, TenantId,
            [.. persona.Bindings ?? [], new PersonaBinding
            {
                Type = PersonaBindingType.Tool, Target = "notes-annotations", Mode = PersonaBindingMode.Always,
            }]);
    }

    /// <summary>Ещё один чат того же проекта — для второй половины теста (актор «сужается»,
    /// см. вызов ниже).</summary>
    private async Task<string> ChatInProjectAsync(string projectId)
    {
        var chat = await OwnerClient().PostAsJsonAsync($"/api/projects/{projectId}/sessions", new { mode = "auto" });
        chat.EnsureSuccessStatusCode();
        return IdOf(await chat.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task АгентРедактора_notesPromoteTaskЗаметкиПроекта_ТотЖеОтказЧтоREST_ЗадачиНет_АгентВладельцаСоздаёт()
    {
        var (projectId, _, chatId) = await SharedWithChatAsync();
        EnableNotesAnnotationsForChatPersona(chatId);
        await TurnAsEditorAsync(chatId);
        var noteId = await NoteWithCheckboxAsync(OwnerClient(), chatId);

        var (text, isError) = await NotesMcpCallAsync(OwnerClient(), chatId, "notes_promote_task",
            new { id = noteId, text = "Задача из чекбокса" });

        isError.Should().BeTrue(text);
        text.Should().Contain(ActorRightGate.DeniedText,
            "тот же отказ, что у гейта актора на прочих инструментах заметок: второй точки правды нет");
        Tasks.GetByProject(projectId).Should().HaveCount(1,
            "отказ до первой мутации: агент редактора не создаёт задачу общего проекта — как человек-редактор на REST (403)");

        // Тот же мост открыт агенту владельца — в отдельном чате того же проекта: актор хода
        // «сужается» (SessionManager.NoteTurnActor), и в чате, где уже был ход редактора, ход
        // владельца актора не расширяет — там владелец-часть мерилась бы чужому актору, а не двери
        var ownerChat = await ChatInProjectAsync(projectId);
        EnableNotesAnnotationsForChatPersona(ownerChat);
        (await OwnerClient().PostAsJsonAsync($"/api/sessions/{ownerChat}/messages",
            new { text = "ход владельца", wait = "none" })).EnsureSuccessStatusCode();
        _factory.Services.GetRequiredService<SessionManager>().GetTurnActor(ownerChat, TenantId)
            .Should().Be(TenantId, "актор идущего хода — владелец");

        var (_, ownerError) = await NotesMcpCallAsync(OwnerClient(), ownerChat, "notes_promote_task",
            new { id = noteId, text = "Задача из чекбокса" });
        ownerError.Should().BeFalse();
        var created = Tasks.GetByProject(projectId).Single(t => t.SourceNoteId == noteId);
        created.Title.Should().Be("Задача из чекбокса");
        created.ProjectId.Should().Be(projectId, "проектная заметка промоутнется в задачу своего проекта");
    }

    // ── автор задачи — вызывающий, а не поле из тела (хвост R-F2-T1 п.3) ───────────────

    [Fact]
    public async Task ТелоСCreatedByActorUserId_НеМеняетАвтора_НаОбехRESTРучках()
    {
        var (projectId, _) = await ArrangeAsync(Editor);
        // Тело называет арендатора: оба человеческих пути это значение перетирают (автора
        // ставит вызывающий — по нему решается удаление). Без теста следующая дверь могла бы
        // забыть перетереть и подменить автора чужой записью в теле.
        var created = await SecondClient().PostAsJsonAsync($"/api/projects/{projectId}/tasks",
            new { title = "Задача с чужим автором в теле", createdByActorUserId = TenantId });
        created.StatusCode.Should().Be(HttpStatusCode.OK);
        Tasks.GetById(IdOf(await created.Content.ReadAsStringAsync()))!
            .CreatedByUserId.Should().Be(SecondId, "автор — вызывающий, а не значение из тела");

        var own = await SecondClient().PostAsJsonAsync("/api/tasks",
            new { title = "Личная с чужим автором в теле", createdByActorUserId = TenantId });
        own.StatusCode.Should().Be(HttpStatusCode.OK);
        Tasks.GetById(IdOf(await own.Content.ReadAsStringAsync()))!
            .CreatedByUserId.Should().Be(SecondId, "личной задачи автор — создающий");
    }
}
