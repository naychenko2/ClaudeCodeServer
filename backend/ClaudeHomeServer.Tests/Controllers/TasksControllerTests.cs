using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Controllers;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Tasks.Projector;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Controllers;

public class TasksControllerTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;
    private readonly HttpClient _client;      // основной юзер (владелец задач)
    private readonly HttpClient _stranger;    // второй юзер — для изоляции

    public TasksControllerTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateAuthenticatedClient();
        _stranger = factory.CreateAuthenticatedClient(
            TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);
    }

    private async Task<JsonElement> CreateTaskAsync(object body)
    {
        var response = await _client.PostAsJsonAsync("/api/tasks", body);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    // ─── Персона-исполнитель ─────────────────────────────────────────────────

    private async Task<string> CreatePersonaAsync(HttpClient client, object body)
    {
        var response = await client.PostAsJsonAsync("/api/personas", body);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var persona = await response.Content.ReadFromJsonAsync<JsonElement>();
        return persona.GetProperty("id").GetString()!;
    }

    // ─── Индекс «чат → фичи» (GET /api/tasks/features/index) ────────────────────────────────

    private SessionManager Sessions => _factory.Services.GetRequiredService<SessionManager>();
    private ProjectManager Projects => _factory.Services.GetRequiredService<ProjectManager>();
    private UserStore Users => _factory.Services.GetRequiredService<UserStore>();

    private string OwnerId => Users.FindByUsername(TestWebApplicationFactory.TestUsername)!.Id;
    private string SecondOwnerId => Users.FindByUsername(TestWebApplicationFactory.SecondUsername)!.Id;

    // Проект владельца (для проектных сессий/фич).
    private string CreateProject(string tag)
    {
        var user = Users.FindByUsername(TestWebApplicationFactory.TestUsername)!;
        var dir = Path.Combine(_factory.TempDir, "featidx_" + tag + "_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return Projects.Create("featidx-" + tag + "-" + Guid.NewGuid().ToString("N")[..6],
            dir, user.Id, user.Username).Id;
    }

    // Проектная сессия (чат) владельца; taskId — для executor-членства.
    private async Task<string> CreateProjectSessionAsync(string projectId, string? taskId = null) =>
        (await Sessions.CreateAsync(projectId, ClaudeMode.AcceptEdits, name: "chat", taskId: taskId)).Id;

    // Личный чат (вне проекта) владельца/второго юзера; taskId — для executor-членства.
    private async Task<string> CreatePersonalChatAsync(string ownerId, string? taskId = null) =>
        (await Sessions.CreateChatAsync(ownerId, ClaudeMode.AcceptEdits, name: "chat", taskId: taskId)).Id;

    // Задача в проекте (POST /api/projects/{id}/tasks) — для эпиков, привязанных к проекту.
    private async Task<JsonElement> CreateProjectTaskAsync(string projectId, object body)
    {
        var response = await _client.PostAsJsonAsync($"/api/projects/{projectId}/tasks", body);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    // Индекс: GET /api/tasks/features/index?projectId=… → JsonElement.
    private async Task<JsonElement> GetIndexAsync(string? projectId = null)
    {
        var url = projectId is null ? "/api/tasks/features/index" : $"/api/tasks/features/index?projectId={projectId}";
        var response = await _client.GetAsync(url);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    // ─── Уровень модели (modelTier) ──────────────────────────────────────────

    [Fact]
    public async Task Create_СУровнемМодели_Сохраняется()
    {
        var task = await CreateTaskAsync(new { title = "разобрать архитектуру", modelTier = "strong" });

        task.GetProperty("modelTier").GetString().Should().Be("strong");
    }

    [Fact]
    public async Task Create_СНеизвестнымУровнем_400()
    {
        var response = await _client.PostAsJsonAsync("/api/tasks",
            new { title = "задача", modelTier = "гениальная" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Update_ПустойУровень_Сбрасывает()
    {
        var task = await CreateTaskAsync(new { title = "правка", modelTier = "weak" });
        var id = task.GetProperty("id").GetString();

        var response = await _client.PutAsJsonAsync($"/api/tasks/{id}", new { modelTier = "" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var updated = await response.Content.ReadFromJsonAsync<JsonElement>();
        updated.GetProperty("modelTier").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task СозданиеПерсоны_СУровнемМодели_Сохраняется()
    {
        var response = await _client.PostAsJsonAsync("/api/personas",
            new { name = "Уровневая", modelTier = "medium" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var persona = await response.Content.ReadFromJsonAsync<JsonElement>();
        persona.GetProperty("modelTier").GetString().Should().Be("medium");

        var bad = await _client.PostAsJsonAsync("/api/personas",
            new { name = "Кривая", modelTier = "ultra" });
        bad.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Create_СЧужойПерсоной_400()
    {
        var foreignPersona = await CreatePersonaAsync(_stranger, new { name = "Чужая" });

        var response = await _client.PostAsJsonAsync("/api/tasks",
            new { title = "задача", assignee = "claude", personaId = foreignPersona });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Create_ЛичнаяЗадачаСПроектнойПерсоной_400()
    {
        // Проект и проектная персона владельца
        var projectResponse = await _client.PostAsJsonAsync("/api/projects", new
        {
            name = "ПроектПерсоны",
            rootPath = Path.Combine(_factory.TempDir, "persona-proj"),
            createDirectory = true,
        });
        projectResponse.IsSuccessStatusCode.Should().BeTrue();
        var projectId = (await projectResponse.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetString();
        var personaId = await CreatePersonaAsync(_client,
            new { name = "Проектная", scope = "project", projectId });

        // Личная задача (вне проекта) — проектная персона недопустима
        var response = await _client.PostAsJsonAsync("/api/tasks",
            new { title = "личная", assignee = "claude", personaId });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Contain("своего проекта");
    }

    [Fact]
    public async Task Update_ПустаяСтрокаPersonaId_УбираетПерсону()
    {
        var personaId = await CreatePersonaAsync(_client, new { name = "Глобальная" });
        var task = await CreateTaskAsync(new { title = "с персоной", assignee = "claude", personaId });
        var id = task.GetProperty("id").GetString();
        task.GetProperty("personaId").GetString().Should().Be(personaId);

        var response = await _client.PutAsJsonAsync($"/api/tasks/{id}", new { personaId = "" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("personaId").ValueKind.Should().Be(JsonValueKind.Null);
    }

    // ─── CRUD ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Create_ЛичнаяЗадача_ВозвращаетЗадачуБезПроекта()
    {
        var task = await CreateTaskAsync(new { title = "Личная задача", dueDate = "2026-07-10" });

        task.GetProperty("id").GetString().Should().NotBeNullOrEmpty();
        task.GetProperty("projectId").ValueKind.Should().Be(JsonValueKind.Null);
        task.GetProperty("title").GetString().Should().Be("Личная задача");
        task.GetProperty("status").GetString().Should().Be("todo");
    }

    [Fact]
    public async Task Create_ПустойЗаголовок_400()
    {
        var response = await _client.PostAsJsonAsync("/api/tasks", new { title = "  " });
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GetById_СвояЗадача_200()
    {
        var task = await CreateTaskAsync(new { title = "найди меня" });
        var id = task.GetProperty("id").GetString();

        var response = await _client.GetAsync($"/api/tasks/{id}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("title").GetString().Should().Be("найди меня");
    }

    [Fact]
    public async Task Update_МеняетПоля()
    {
        var task = await CreateTaskAsync(new { title = "до правки" });
        var id = task.GetProperty("id").GetString();

        var response = await _client.PutAsJsonAsync($"/api/tasks/{id}", new
        {
            title = "после правки",
            status = "inProgress",
            priority = "high"
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("title").GetString().Should().Be("после правки");
        body.GetProperty("status").GetString().Should().Be("inProgress");
        body.GetProperty("priority").GetString().Should().Be("high");
    }

    // ─── Дефект a7b3b85d: PUT возвращает verification.verdict ─────────────────
    // MCP tasks_complete копировал Verdict, а REST PUT пересобирая TaskVerification
    // терял его — через REST нельзя было закрыть review/qa/final фичи с вердиктом.

    [Fact]
    public async Task Update_РевьюФичи_ВердиктСохраняетсяЧерезPut()
    {
        var epic = await CreateTaskAsync(new { title = "эпик", role = "feature" });
        var epicId = epic.GetProperty("id").GetString();
        var review = await CreateTaskAsync(new { title = "ревью", featureId = epicId, role = "review" });
        var reviewId = review.GetProperty("id").GetString();

        var response = await _client.PutAsJsonAsync($"/api/tasks/{reviewId}", new
        {
            status = "done",
            verification = new { verdict = "accepted" },
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("verification").GetProperty("verdict").GetString()
            .Should().Be("accepted", "PUT обязан сохранить вердикт, как MCP tasks_complete");

        // GET по id — вердикт переживает перезапись, не только в теле PUT
        var get = await _client.GetAsync($"/api/tasks/{reviewId}");
        var got = await get.Content.ReadFromJsonAsync<JsonElement>();
        got.GetProperty("verification").GetProperty("verdict").GetString().Should().Be("accepted");
    }

    // ─── Мироринг MCP: warning в PUT, когда ревью/проверку/финал фичи закрыли без вердикта ──

    [Fact]
    public async Task Update_РевьюФичиБезВердикта_WarningВОтвете()
    {
        var epic = await CreateTaskAsync(new { title = "эпик", role = "feature" });
        var epicId = epic.GetProperty("id").GetString();
        var review = await CreateTaskAsync(new { title = "ревью", featureId = epicId, role = "review" });
        var reviewId = review.GetProperty("id").GetString();

        // Закрытие без вердикта: status=done, verification не присылаем
        var response = await _client.PutAsJsonAsync($"/api/tasks/{reviewId}", new { status = "done" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.TryGetProperty("warning", out var warning).Should().BeTrue(
            "PUT без вердикта обязан вернуть мягкое warning, как MCP tasks_complete");
        warning.GetString().Should().Be(FeatureVerdict.WarningText);
    }

    [Fact]
    public async Task Update_РевьюФичиСВердиктом_БезWarning()
    {
        var epic = await CreateTaskAsync(new { title = "эпик", role = "feature" });
        var epicId = epic.GetProperty("id").GetString();
        var review = await CreateTaskAsync(new { title = "ревью", featureId = epicId, role = "review" });
        var reviewId = review.GetProperty("id").GetString();

        // Закрытие С вердиктом: warning не должен появляться (поле отсутствует, не null)
        var response = await _client.PutAsJsonAsync($"/api/tasks/{reviewId}", new
        {
            status = "done",
            verification = new { verdict = "accepted" },
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.TryGetProperty("warning", out _).Should().BeFalse(
            "с вердиктом warning не нужен — поле отсутствует");
    }

    // ─── REST GET /api/tasks/features: агрегат фичи через FeatureProjector ─────────────────

    [Fact]
    public async Task GetFeatures_ПоСессии_ВозвращаетАгрегат()
    {
        const string sessionId = "sess-feature-test-1";
        var epic = await CreateTaskAsync(new { title = "эпик", role = "feature", sourceSessionId = sessionId });
        var epicId = epic.GetProperty("id").GetString();
        await CreateTaskAsync(new { title = "шаг", featureId = epicId, role = "step", stepRef = "Ш1" });

        var response = await _client.GetAsync($"/api/tasks/features?sessionId={sessionId}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var features = await response.Content.ReadFromJsonAsync<JsonElement>();

        // Ответ — голой массив FeatureRun; фича чата в нём, агрегат: эпик + 1 шаг, вердиктов нет
        features.GetArrayLength().Should().BeGreaterThanOrEqualTo(1);
        var run = features.EnumerateArray()
            .Single(f => f.GetProperty("featureId").GetString() == epicId);
        run.GetProperty("epic").GetProperty("id").GetString().Should().Be(epicId);
        run.GetProperty("steps").GetArrayLength().Should().Be(1);
        run.GetProperty("verdicts").GetProperty("loops").GetInt32().Should().Be(0);
        run.GetProperty("ready").GetBoolean().Should().BeFalse();
    }

    // Подсистема Tasks выключена (FeatureProjector не зарегистрирован) → честный 503, а не 500.
    // Прямая сборка контроллера: GetFeatures проверяет features==null ДО обращения к задачам.
    [Fact]
    public void GetFeatures_ПодсистемаВыключена_503()
    {
        var controller = new TasksController(
            tasks: null!, broadcaster: null!, ai: null!, projects: null!,
            personas: null!, executor: null!, bindings: null!, sessions: null!, spheres: null!,
            noteSync: null, features: null);

        var result = controller.GetFeatures(null);

        // StatusCode(503, body) — ObjectResult (несёт тело), не StatusCodeResult
        result.Should().BeOfType<Microsoft.AspNetCore.Mvc.ObjectResult>()
            .Which.StatusCode.Should().Be(503);
    }

    [Fact]
    public async Task Delete_УдаляетЗадачу()
    {
        var task = await CreateTaskAsync(new { title = "на удаление" });
        var id = task.GetProperty("id").GetString();

        var deleteResponse = await _client.DeleteAsync($"/api/tasks/{id}");
        deleteResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var getResponse = await _client.GetAsync($"/api/tasks/{id}");
        getResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetAll_ФильтрПоПоиску_НаходитСвоюЗадачу()
    {
        var marker = "уникальный_" + Guid.NewGuid().ToString("N")[..8];
        await CreateTaskAsync(new { title = marker });

        var response = await _client.GetAsync($"/api/tasks?q={marker}");

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetArrayLength().Should().Be(1);
    }

    // ─── Изоляция по владельцу ───────────────────────────────────────────────

    [Fact]
    public async Task ЧужаяЗадача_НедоступнаВторомуЮзеру()
    {
        var task = await CreateTaskAsync(new { title = "приватная" });
        var id = task.GetProperty("id").GetString();

        // GET по id → 404
        (await _stranger.GetAsync($"/api/tasks/{id}")).StatusCode
            .Should().Be(HttpStatusCode.NotFound);

        // PUT → 404
        (await _stranger.PutAsJsonAsync($"/api/tasks/{id}", new { title = "взлом" })).StatusCode
            .Should().Be(HttpStatusCode.NotFound);

        // DELETE → 404 (и задача жива)
        (await _stranger.DeleteAsync($"/api/tasks/{id}")).StatusCode
            .Should().Be(HttpStatusCode.NotFound);
        (await _client.GetAsync($"/api/tasks/{id}")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetAll_НеСодержитЧужихЗадач()
    {
        var task = await CreateTaskAsync(new { title = "только моя" });
        var id = task.GetProperty("id").GetString();

        var response = await _stranger.GetAsync("/api/tasks");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        body.EnumerateArray().Select(t => t.GetProperty("id").GetString())
            .Should().NotContain(id);
    }

    // ─── Регулярные задачи: done → следующий экземпляр ───────────────────────

    [Fact]
    public async Task Done_РегулярнаяЗадача_СпавнитСледующийЭкземпляр()
    {
        var marker = "рег_" + Guid.NewGuid().ToString("N")[..8];
        var task = await CreateTaskAsync(new
        {
            title = marker,
            dueDate = "2026-07-01",
            dueTime = "10:00",
            recurrence = new { type = "daily", interval = 1 }
        });
        var id = task.GetProperty("id").GetString();

        var response = await _client.PutAsJsonAsync($"/api/tasks/{id}", new { status = "done" });
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var listResponse = await _client.GetAsync($"/api/tasks?q={marker}");
        var list = await listResponse.Content.ReadFromJsonAsync<JsonElement>();
        var items = list.EnumerateArray().ToList();

        items.Should().HaveCount(2);
        var done = items.Single(t => t.GetProperty("status").GetString() == "done");
        var next = items.Single(t => t.GetProperty("status").GetString() == "todo");
        done.GetProperty("dueDate").GetString().Should().Be("2026-07-01");
        next.GetProperty("dueDate").GetString().Should().Be("2026-07-02");
        next.GetProperty("dueTime").GetString().Should().Be("10:00");
        // Оба экземпляра — одна серия
        next.GetProperty("seriesId").GetString().Should().Be(done.GetProperty("seriesId").GetString());
    }

    [Fact]
    public async Task Done_БезRecurrence_НеСпавнит()
    {
        // У задачи нет recurrence — следующий экземпляр не создаётся
        var marker = "без_рекуррентности_" + Guid.NewGuid().ToString("N")[..8];
        var task = await CreateTaskAsync(new
        {
            title = marker,
            dueDate = "2026-07-01"
        });
        var id = task.GetProperty("id").GetString();

        await _client.PutAsJsonAsync($"/api/tasks/{id}", new { status = "done" });

        var list = await (await _client.GetAsync($"/api/tasks?q={marker}"))
            .Content.ReadFromJsonAsync<JsonElement>();
        list.GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task Done_ПовторноеСохранениеDone_НеСпавнитВторойРаз()
    {
        var marker = "идемпотент_" + Guid.NewGuid().ToString("N")[..8];
        var task = await CreateTaskAsync(new
        {
            title = marker,
            dueDate = "2026-07-01",
            recurrence = new { type = "daily", interval = 1 }
        });
        var id = task.GetProperty("id").GetString();

        await _client.PutAsJsonAsync($"/api/tasks/{id}", new { status = "done" });
        // Второй PUT по уже завершённой задаче (wasDone == true) — спавна быть не должно
        await _client.PutAsJsonAsync($"/api/tasks/{id}", new { status = "done" });

        var list = await (await _client.GetAsync($"/api/tasks?q={marker}"))
            .Content.ReadFromJsonAsync<JsonElement>();
        list.GetArrayLength().Should().Be(2);
    }

    // ─── Индекс «чат → фичи» (GET /api/tasks/features/index) ─────────────────────────────────

    // Подсистема Tasks выключена (FeatureProjector не зарегистрирован) → честный 503, а не 500.
    // Прямая сборка контроллера: GetFeaturesIndex проверяет features==null ДО обращения к сессиям.
    [Fact]
    public void GetFeaturesIndex_ПодсистемаВыключена_503()
    {
        var controller = new TasksController(
            tasks: null!, broadcaster: null!, ai: null!, projects: null!,
            personas: null!, executor: null!, bindings: null!, sessions: null!, spheres: null!,
            noteSync: null, features: null);

        var result = controller.GetFeaturesIndex(null);

        result.Should().BeOfType<Microsoft.AspNetCore.Mvc.ObjectResult>()
            .Which.StatusCode.Should().Be(503);
    }

    // Чужой (второго юзера) чат не попадает в chats (fail-closed: снимок — только живые сессии
    // владельца) и не становится ключевым (не жив → следующего звена нет → null).
    [Fact]
    public async Task GetFeaturesIndex_ЧужойЧат_НеВChats_НеКлючевой()
    {
        var foreignChat = await CreatePersonalChatAsync(SecondOwnerId);
        var epic = await CreateTaskAsync(new { title = "эпик", role = "feature", sourceSessionId = foreignChat });
        var epicId = epic.GetProperty("id").GetString();

        var index = await GetIndexAsync();

        index.GetProperty("chats").EnumerateObject().Select(kv => kv.Name)
            .Should().NotContain(foreignChat, "чужой чат не в снимке владельца");
        var row = index.GetProperty("features").EnumerateArray()
            .Single(f => f.GetProperty("featureId").GetString() == epicId);
        row.GetProperty("keySessionId").ValueKind.Should().Be(JsonValueKind.Null,
            "чужой чат не жив → следующего звена нет → ключевой чат null");
    }

    // Ключевой чат: первое звено (SourceSessionId) «удалено» (id не в живых сессиях) →
    // берётся следующее живое звено (ExecutorLaunchedBySessionId).
    [Fact]
    public async Task GetFeaturesIndex_КлючевойУдалён_СледующееЗвено()
    {
        var project = CreateProject("key-deleted");
        var liveChat = await CreateProjectSessionAsync(project);
        var deletedChat = "deleted-chat-" + Guid.NewGuid().ToString("N")[..8];

        var epic = await CreateTaskAsync(new { title = "эпик", role = "feature", sourceSessionId = deletedChat });
        var epicId = epic.GetProperty("id").GetString();
        // CreateTaskRequest ExecutorLaunchedBySessionId не несёт — второе звено ставим через PUT.
        await _client.PutAsJsonAsync($"/api/tasks/{epicId}", new { executorLaunchedBySessionId = liveChat });

        var index = await GetIndexAsync();
        var row = index.GetProperty("features").EnumerateArray()
            .Single(f => f.GetProperty("featureId").GetString() == epicId);
        row.GetProperty("keySessionId").GetString().Should().Be(liveChat,
            "первое звено «удалено» → берётся следующее живое (ExecutorLaunchedBySessionId)");
    }

    // Ключевой чат в архиве → keyArchived=true (остаётся ключевым, помечается).
    [Fact]
    public async Task GetFeaturesIndex_КлючевойВАрхиве_KeyArchived()
    {
        var project = CreateProject("key-archived");
        var chat = await CreateProjectSessionAsync(project);
        Sessions.SetArchived(chat, archived: true, by: OwnerId);

        var epic = await CreateTaskAsync(new { title = "эпик", role = "feature", sourceSessionId = chat });
        var epicId = epic.GetProperty("id").GetString();

        var index = await GetIndexAsync();
        var row = index.GetProperty("features").EnumerateArray()
            .Single(f => f.GetProperty("featureId").GetString() == epicId);
        row.GetProperty("keySessionId").GetString().Should().Be(chat);
        row.GetProperty("keyArchived").GetBoolean().Should().BeTrue("ключевой чат в архиве → keyArchived");
    }

    // Ключевой чат в другом проекте → keyProjectId = проект этого чата.
    [Fact]
    public async Task GetFeaturesIndex_КлючевойВДругомПроекте_KeyProjectId()
    {
        var otherProject = CreateProject("key-other-project");
        var chat = await CreateProjectSessionAsync(otherProject);

        var epic = await CreateTaskAsync(new { title = "эпик", role = "feature", sourceSessionId = chat });
        var epicId = epic.GetProperty("id").GetString();

        var index = await GetIndexAsync();
        var row = index.GetProperty("features").EnumerateArray()
            .Single(f => f.GetProperty("featureId").GetString() == epicId);
        row.GetProperty("keySessionId").GetString().Should().Be(chat);
        row.GetProperty("keyProjectId").GetString().Should().Be(otherProject,
            "проект ключевого чата → keyProjectId");
    }

    // Без projectId видны и проектные, и личные (вне проектов) чаты: снимок — все сессии владельца.
    [Fact]
    public async Task GetFeaturesIndex_БезProjectId_ЛичныеЧатыВидны()
    {
        var project = CreateProject("no-project-filter");
        var personalChat = await CreatePersonalChatAsync(OwnerId);

        // Эпик: куратор — личный чат, исполнитель шага — проектный чат.
        var epic = await CreateTaskAsync(new { title = "эпик", role = "feature", sourceSessionId = personalChat });
        var epicId = epic.GetProperty("id").GetString();
        var step = await CreateTaskAsync(new { title = "шаг", featureId = epicId, role = "step", stepRef = "Ш1" });
        var stepId = step.GetProperty("id").GetString();
        var executorChat = await CreateProjectSessionAsync(project, taskId: stepId);

        var index = await GetIndexAsync();

        var chatIds = index.GetProperty("chats").EnumerateObject().Select(kv => kv.Name).ToList();
        chatIds.Should().Contain(personalChat, "личный чат (куратор) виден без projectId");
        chatIds.Should().Contain(executorChat, "проектный чат (исполнитель) виден без projectId");
    }

    // Фильтр по projectId: в ответе только сессии/фичи этого проекта; чужой проект не виден.
    [Fact]
    public async Task GetFeaturesIndex_ФильтрПоProjectId()
    {
        var projectA = CreateProject("filter-a");
        var projectB = CreateProject("filter-b");
        var chatA = await CreateProjectSessionAsync(projectA);
        var chatB = await CreateProjectSessionAsync(projectB);

        var epicA = await CreateProjectTaskAsync(projectA, new { title = "эпик A", role = "feature", sourceSessionId = chatA });
        var epicB = await CreateProjectTaskAsync(projectB, new { title = "эпик B", role = "feature", sourceSessionId = chatB });

        var index = await GetIndexAsync(projectId: projectA);

        var chatIds = index.GetProperty("chats").EnumerateObject().Select(kv => kv.Name).ToList();
        chatIds.Should().Contain(chatA, "чат проекта A в области");
        chatIds.Should().NotContain(chatB, "чат проекта B вне области projectId=A");

        var featureIds = index.GetProperty("features").EnumerateArray()
            .Select(f => f.GetProperty("featureId").GetString()).ToList();
        featureIds.Should().Contain(epicA.GetProperty("id").GetString());
        featureIds.Should().NotContain(epicB.GetProperty("id").GetString(),
            "эпик B (проект B) вне области projectId=A");
    }
}
