using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.IdentityModel.Tokens.Jwt;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using ClaudeHomeServer.Controllers;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Execution;
using ClaudeHomeServer.Services.Git;
using ClaudeHomeServer.Services.Spheres;
using ClaudeHomeServer.Services.Tasks;
using ClaudeHomeServer.Services.Tasks.Projector;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
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
    private PersonaManager Personas => _factory.Services.GetRequiredService<PersonaManager>();

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

    // Эпик фичи как ПРОЕКТНАЯ задача с worktree (merge-тесты): личная задача нормализует
    // WorktreePath/WorktreeBranch в null, поэтому worktree держит только проектная. Возвращает
    // (epicId, worktreePath, branch, projectId). Worktree — РЕАЛЬНЫЙ git-worktree: корень проекта
    // `git init`'ится, а путь создаётся через `git worktree add -b` — гард мержа проверяет, что путь
    // числится в `git worktree list` проекта (тот же предикат, что AttachWorktreeAsync).
    private async Task<(string EpicId, string WorktreePath, string Branch, string ProjectId)>
        CreateFeatureEpicWithWorktreeAsync()
    {
        var projectId = CreateProject("merge");
        var projectRoot = Projects.GetById(projectId)!.RootPath;
        var wt = Path.Combine(_factory.TempDir, "merge_wt_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(wt);
        var branch = "feat/merge-" + Guid.NewGuid().ToString("N")[..6];

        // Реальный git-worktree (те же операции, что в бою: init + worktree add -b).
        var git = _factory.Services.GetRequiredService<GitService>();
        await git.InitAsync(null, projectRoot);
        await git.WorktreeAddAsync(null, projectRoot, wt, branch);

        var epic = await CreateProjectTaskAsync(projectId, new
        {
            title = "эпик merge", role = "feature",
            worktreePath = wt, worktreeBranch = branch,
        });
        return (epic.GetProperty("id").GetString()!, wt, branch, projectId);
    }

    // Принятый финал (run.Ready=true) как проектная задача в проекте эпика.
    private async Task CreateAcceptedFinalAsync(string projectId, string epicId)
    {
        await CreateProjectTaskAsync(projectId, new
        {
            title = "финал", featureId = epicId, role = "final",
            status = "done",
            verification = new { verdict = "accepted" },
        });
    }

    // Персона-исполнитель DevOps (уникальное имя, чтобы не конфликтовать с персонами других тестов).
    private ClaudeHomeServer.Models.Persona CreateDevopsPersona(string tag)
    {
        var personas = _factory.Services.GetRequiredService<PersonaManager>();
        return personas.Create(OwnerId, tag + "-" + Guid.NewGuid().ToString("N")[..6],
            null, null, null, null, null, PersonaScope.Global, null,
            null, null, true, null, null, PersonaAccess.Full, null,
            PersonaSpecialty.DevopsExecutor);
    }

    // Прямая сборка контроллера с реальными DI-сервисами (для тестов, где нужен контроль над git).
    // git — явный: реальный (из DI) / null (подсистема Git выключена → 503) / stub (сбой WorktreeListAsync).
    // HttpContext/User подставляем вручную (прямая сборка — без HTTP): claim Sub = OwnerId,
    // иначе UserId (User.FindFirstValue(Sub)) бросает ArgumentNullException.
    private TasksController BuildController(GitService? git)
    {
        var controller = new TasksController(
            tasks: _factory.Services.GetRequiredService<TaskManager>(),
            broadcaster: _factory.Services.GetRequiredService<ISessionBroadcaster>(),
            ai: _factory.Services.GetRequiredService<TaskAiService>(),
            projects: _factory.Services.GetRequiredService<ProjectManager>(),
            personas: _factory.Services.GetRequiredService<PersonaManager>(),
            executor: _factory.Services.GetRequiredService<TaskExecutionService>(),
            bindings: _factory.Services.GetRequiredService<PersonaBindingsService>(),
            sessions: _factory.Services.GetRequiredService<SessionManager>(),
            spheres: _factory.Services.GetRequiredService<ISphereDirectory>(),
            noteSync: null,
            features: _factory.Services.GetRequiredService<FeatureProjector>(),
            git: git);
        var identity = new ClaimsIdentity([new Claim(JwtRegisteredClaimNames.Sub, OwnerId)], "test");
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) },
        };
        return controller;
    }

    // Реальный GitService из DI (worktree-гард проходит штатно).
    private GitService RealGit() => _factory.Services.GetRequiredService<GitService>();

    // Статический словарь гейтов мержа (MergeGates) — для проверки, что запись убрана после мержа.
    private static ConcurrentDictionary<string, SemaphoreSlim> MergeGatesDict() =>
        (ConcurrentDictionary<string, SemaphoreSlim>)typeof(TasksController)
            .GetField("MergeGates", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null)!;

    // GitService со stub-запускателем: Start бросает → WorktreeListAsync бросает (сбой git, п.53).
    private static GitService ThrowingGit() => new(new ThrowingLauncherFactory());

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

    // ─── REST GET /api/tasks/features/{featureId}: обёртка { run, events, eventsTotal } ─────

    // Находит событие (kind, taskId) в массиве events; бросает — такого нет (в тестах событие есть).
    private static JsonElement FindEvent(JsonElement events, string kind, string taskId)
    {
        foreach (var e in events.EnumerateArray())
            if (e.GetProperty("kind").GetString() == kind && e.GetProperty("taskId").GetString() == taskId)
                return e;
        throw new InvalidOperationException($"Событие {kind}/{taskId} не найдено");
    }

    // 200: своя фича → обёртка. run — агрегат (Project), events — массив, eventsTotal — до обрезки.
    [Fact]
    public async Task GetFeature_СвояФича_200_Обёртка()
    {
        var epic = await CreateTaskAsync(new { title = "эпик", role = "feature" });
        var epicId = epic.GetProperty("id").GetString();
        await CreateTaskAsync(new { title = "шаг", featureId = epicId, role = "step", stepRef = "Ш1" });

        var response = await _client.GetAsync($"/api/tasks/features/{epicId}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        body.GetProperty("run").GetProperty("featureId").GetString().Should().Be(epicId);
        body.GetProperty("run").GetProperty("epic").GetProperty("id").GetString().Should().Be(epicId);
        body.GetProperty("events").ValueKind.Should().Be(JsonValueKind.Array);
        // 2 события (эпик + шаг, оба created) — меньше лимита 100 → обрезки нет.
        body.GetProperty("eventsTotal").GetInt32().Should().Be(2);
        body.GetProperty("events").GetArrayLength().Should().Be(2);
    }

    // 404 чужой: эпик второго юзера для первого — «нет» (не отличим от несуществующей).
    [Fact]
    public async Task GetFeature_ЧужаяФича_404()
    {
        var created = await _stranger.PostAsJsonAsync("/api/tasks", new { title = "чужой эпик", role = "feature" });
        created.StatusCode.Should().Be(HttpStatusCode.OK);
        var epicId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();

        var response = await _client.GetAsync($"/api/tasks/features/{epicId}");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // 404 удалённой: эпик владельца удалён → «нет» (тот же 404, что у чужой).
    [Fact]
    public async Task GetFeature_УдалённаяФича_404()
    {
        var epic = await CreateTaskAsync(new { title = "эпик", role = "feature" });
        var epicId = epic.GetProperty("id").GetString();
        await _client.DeleteAsync($"/api/tasks/{epicId}");

        var response = await _client.GetAsync($"/api/tasks/features/{epicId}");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // 503: подсистема Tasks выключена (FeatureProjector не зарегистрирован) — прямой сборкой контроллера.
    [Fact]
    public void GetFeature_ПодсистемаВыключена_503()
    {
        var controller = new TasksController(
            tasks: null!, broadcaster: null!, ai: null!, projects: null!,
            personas: null!, executor: null!, bindings: null!, sessions: null!, spheres: null!,
            noteSync: null, features: null);

        var result = controller.GetFeature("any", null);

        result.Should().BeOfType<Microsoft.AspNetCore.Mvc.ObjectResult>()
            .Which.StatusCode.Should().Be(503);
    }

    // eventsLimit: =2 → лента 2, eventsTotal=6 (до обрезки); all/0 → все 6.
    [Fact]
    public async Task GetFeature_EventsLimit_ОбрезаетЛенту_EventsTotalДоОбрезки()
    {
        var epic = await CreateTaskAsync(new { title = "эпик", role = "feature" });
        var epicId = epic.GetProperty("id").GetString();
        // 5 шагов → 6 событий (эпик + 5 шагов, все created).
        for (var i = 0; i < 5; i++)
            await CreateTaskAsync(new { title = "шаг", featureId = epicId, role = "step", stepRef = $"Ш{i}" });

        var limited = await (await _client.GetAsync($"/api/tasks/features/{epicId}?eventsLimit=2"))
            .Content.ReadFromJsonAsync<JsonElement>();
        limited.GetProperty("events").GetArrayLength().Should().Be(2);
        limited.GetProperty("eventsTotal").GetInt32().Should().Be(6);

        var all = await (await _client.GetAsync($"/api/tasks/features/{epicId}?eventsLimit=all"))
            .Content.ReadFromJsonAsync<JsonElement>();
        all.GetProperty("events").GetArrayLength().Should().Be(6);
        all.GetProperty("eventsTotal").GetInt32().Should().Be(6);

        var zero = await (await _client.GetAsync($"/api/tasks/features/{epicId}?eventsLimit=0"))
            .Content.ReadFromJsonAsync<JsonElement>();
        zero.GetProperty("events").GetArrayLength().Should().Be(6);
    }

    // sessionId события: чат в снимке владельца → id наружу; чат удалён → null (fail-closed).
    [Fact]
    public async Task GetFeature_SessionId_УдалённыйЧат_Null()
    {
        var sessionId = await CreatePersonalChatAsync(OwnerId);
        var epic = await CreateTaskAsync(new { title = "эпик", role = "feature" });
        var epicId = epic.GetProperty("id").GetString();
        var step = await CreateTaskAsync(new
        {
            title = "шаг", featureId = epicId, role = "step", stepRef = "Ш1",
            linkedSessionId = sessionId,
        });
        var stepId = step.GetProperty("id").GetString();

        // Чат жив (в снимке) → sessionId события наружу.
        var live = await (await _client.GetAsync($"/api/tasks/features/{epicId}"))
            .Content.ReadFromJsonAsync<JsonElement>();
        var liveEvent = FindEvent(live.GetProperty("events"), "created", stepId);
        liveEvent.GetProperty("sessionId").GetString().Should().Be(sessionId);

        // Чат удалён (не в снимке) → sessionId = null.
        await Sessions.DeleteAsync(sessionId);
        var after = await (await _client.GetAsync($"/api/tasks/features/{epicId}"))
            .Content.ReadFromJsonAsync<JsonElement>();
        var afterEvent = FindEvent(after.GetProperty("events"), "created", stepId);
        afterEvent.GetProperty("sessionId").ValueKind.Should().Be(JsonValueKind.Null);
    }

    // ─── REST POST /api/tasks/features/{featureId}/merge: мерж фичи ─────────────────────────

    // 503: подсистема Tasks выключена (FeatureProjector не зарегистрирован) — прямой сборкой контроллера.
    [Fact]
    public void MergeFeature_ПодсистемаВыключена_503()
    {
        var controller = new TasksController(
            tasks: null!, broadcaster: null!, ai: null!, projects: null!,
            personas: null!, executor: null!, bindings: null!, sessions: null!, spheres: null!,
            noteSync: null, features: null);

        var result = controller.MergeFeature("any").GetAwaiter().GetResult();

        result.Should().BeOfType<Microsoft.AspNetCore.Mvc.ObjectResult>()
            .Which.StatusCode.Should().Be(503);
    }

    // 404: чужой эпик → «нет» (тот же 404, что у GET).
    [Fact]
    public async Task MergeFeature_ЧужаяФича_404()
    {
        var created = await _stranger.PostAsJsonAsync("/api/tasks", new { title = "чужой эпик", role = "feature" });
        created.StatusCode.Should().Be(HttpStatusCode.OK);
        var epicId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();

        var response = await _client.PostAsync($"/api/tasks/features/{epicId}/merge", null);
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // 409: финал не принят (нет финала или вердикт не accepted/accepted_with_remarks).
    [Fact]
    public async Task MergeFeature_ФиналНеПринят_409()
    {
        var epic = await CreateTaskAsync(new { title = "эпик", role = "feature" });
        var epicId = epic.GetProperty("id").GetString();
        // Финал без вердикта → Ready=false.
        await CreateTaskAsync(new { title = "финал", featureId = epicId, role = "final" });

        var response = await _client.PostAsync($"/api/tasks/features/{epicId}/merge", null);
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Contain("Финал не принят");
    }

    // 409: нет исполнителя DevOps (персон с Specialty=DevopsExecutor нет).
    [Fact]
    public async Task MergeFeature_НетИсполнителяDevOps_409()
    {
        var (epicId, _, _, projectId) = await CreateFeatureEpicWithWorktreeAsync();
        // Финал принят → Ready=true.
        await CreateAcceptedFinalAsync(projectId, epicId);

        var response = await _client.PostAsync($"/api/tasks/features/{epicId}/merge", null);
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Contain("DevOps");
    }

    // 409: несколько исполнителей DevOps. Персоны создаются напрямую (уникальные имена)
    // и удаляются после теста, чтобы не влиять на другие тесты класса (общий IClassFixture).
    [Fact]
    public async Task MergeFeature_НесколькоИсполнителейDevOps_409()
    {
        var (epicId, _, _, projectId) = await CreateFeatureEpicWithWorktreeAsync();
        await CreateAcceptedFinalAsync(projectId, epicId);
        var personas = _factory.Services.GetRequiredService<PersonaManager>();
        var p1 = personas.Create(OwnerId, "DevOps-multi-" + Guid.NewGuid().ToString("N")[..6],
            null, null, null, null, null, PersonaScope.Global, null,
            null, null, true, null, null, PersonaAccess.Full, null, PersonaSpecialty.DevopsExecutor);
        var p2 = personas.Create(OwnerId, "DevOps-multi-" + Guid.NewGuid().ToString("N")[..6],
            null, null, null, null, null, PersonaScope.Global, null,
            null, null, true, null, null, PersonaAccess.Full, null, PersonaSpecialty.DevopsExecutor);

        try
        {
            var response = await _client.PostAsync($"/api/tasks/features/{epicId}/merge", null);
            response.StatusCode.Should().Be(HttpStatusCode.Conflict);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            body.GetProperty("error").GetString().Should().Contain("Несколько");
        }
        finally
        {
            personas.Delete(p1.Id, OwnerId);
            personas.Delete(p2.Id, OwnerId);
        }
    }

    // 200: создаёт задачу role=merge с персоной DevopsExecutor и запускает исполнителя.
    // Персона создаётся напрямую через PersonaManager (не через REST), чтобы не зависеть
    // от персон, созданных другими тестами класса (общий IClassFixture).
    [Fact]
    public async Task MergeFeature_СоздаётЗадачу_200()
    {
        var (epicId, _, _, projectId) = await CreateFeatureEpicWithWorktreeAsync();
        await CreateAcceptedFinalAsync(projectId, epicId);
        // Персона напрямую: уникальное имя, чтобы не конфликтовать с персонами других тестов.
        var personas = _factory.Services.GetRequiredService<PersonaManager>();
        var persona = personas.Create(OwnerId, "DevOps-merge-test-" + Guid.NewGuid().ToString("N")[..6],
            null, null, null, null, null, PersonaScope.Global, null,
            null, null, true, null, null, PersonaAccess.Full, null,
            PersonaSpecialty.DevopsExecutor);

        try
        {
            var response = await _client.PostAsync($"/api/tasks/features/{epicId}/merge", null);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                var dbg = await response.Content.ReadFromJsonAsync<JsonElement>();
                throw new Exception($"Merge failed: {(int)response.StatusCode} {dbg.GetProperty("error").GetString()}");
            }
            var task = await response.Content.ReadFromJsonAsync<JsonElement>();

            task.GetProperty("role").GetString().Should().Be("merge");
            task.GetProperty("featureId").GetString().Should().Be(epicId);
            task.GetProperty("personaId").GetString().Should().Be(persona.Id);
        }
        finally
        {
            personas.Delete(persona.Id, OwnerId);
        }
    }

    // Идемпотентность: двойной вызов → одна задача (второй вызов возвращает существующую).
    [Fact]
    public async Task MergeFeature_ДвойнойВызов_ОднаЗадача()
    {
        var (epicId, _, _, projectId) = await CreateFeatureEpicWithWorktreeAsync();
        await CreateAcceptedFinalAsync(projectId, epicId);
        // Персона напрямую: уникальное имя, чтобы не конфликтовать с персонами других тестов.
        var personas = _factory.Services.GetRequiredService<PersonaManager>();
        var persona = personas.Create(OwnerId, "DevOps-merge-test-" + Guid.NewGuid().ToString("N")[..6],
            null, null, null, null, null, PersonaScope.Global, null,
            null, null, true, null, null, PersonaAccess.Full, null,
            PersonaSpecialty.DevopsExecutor);

        try
        {
            var first = await _client.PostAsync($"/api/tasks/features/{epicId}/merge", null);
            first.StatusCode.Should().Be(HttpStatusCode.OK);
            var firstTask = await first.Content.ReadFromJsonAsync<JsonElement>();
            var firstId = firstTask.GetProperty("id").GetString();

            // Второй вызов: merge-задача уже существует (todo → запустить, inProgress → вернуть).
            var second = await _client.PostAsync($"/api/tasks/features/{epicId}/merge", null);
            second.StatusCode.Should().Be(HttpStatusCode.OK);
            var secondTask = await second.Content.ReadFromJsonAsync<JsonElement>();
            secondTask.GetProperty("id").GetString().Should().Be(firstId);
        }
        finally
        {
            personas.Delete(persona.Id, OwnerId);
        }
    }

    // M1: у merge-задачи есть самодостаточный шаблон Description: имя ветки, путь worktree,
    // «push не делать» и правило конфликта. m1: основная ветка — из git (тут git init -b main → «main»),
    // а не зашитый «master».
    [Fact]
    public async Task MergeFeature_ОписаниеСодержитШаблон()
    {
        var (epicId, worktree, branch, projectId) = await CreateFeatureEpicWithWorktreeAsync();
        await CreateAcceptedFinalAsync(projectId, epicId);
        var persona = CreateDevopsPersona("DevOps-desc");

        try
        {
            var response = await _client.PostAsync($"/api/tasks/features/{epicId}/merge", null);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var task = await response.Content.ReadFromJsonAsync<JsonElement>();
            var description = task.GetProperty("description").GetString()!;

            description.Should().Contain(branch);
            description.Should().Contain(worktree);
            description.Should().Contain("Push НЕ делать");
            description.Should().Contain("конфликт");
            // п.40: шаблон несёт абсолютный путь корня проекта (основное рабочее дерево — сюда мержим).
            var projectRoot = Projects.GetById(projectId)!.RootPath;
            description.Should().Contain(projectRoot);
            // m1: основная ветка проекта — из git (git init -b main → «main»), не зашитый «master».
            description.Should().Contain("«main»");
            description.Should().NotContain("(master)");
        }
        finally
        {
            Personas.Delete(persona.Id, OwnerId);
        }
    }

    // M2: после 200 исполнитель реально запущен — возвращённая задача несёт ClaudeStartedAt.
    // Мутация: убрать `ExecuteAsync` (не фиксировать результат) → ClaudeStartedAt null → тест краснеет.
    [Fact]
    public async Task MergeFeature_ИсполнительЗапущен()
    {
        var (epicId, _, _, projectId) = await CreateFeatureEpicWithWorktreeAsync();
        await CreateAcceptedFinalAsync(projectId, epicId);
        var persona = CreateDevopsPersona("DevOps-exec");

        try
        {
            var response = await _client.PostAsync($"/api/tasks/features/{epicId}/merge", null);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var task = await response.Content.ReadFromJsonAsync<JsonElement>();

            task.GetProperty("claudeStartedAt").ValueKind.Should().NotBe(JsonValueKind.Null,
                "исполнитель должен быть запущен (ClaudeStartedAt стоит)");
            task.GetProperty("status").GetString().Should().Be("inProgress");
        }
        finally
        {
            Personas.Delete(persona.Id, OwnerId);
        }
    }

    // M3: два конкурентных POST → одна merge-задача (создание сериализовано по featureId).
    [Fact]
    public async Task MergeFeature_КонкурентныеВызовы_ОднаЗадача()
    {
        var (epicId, _, _, projectId) = await CreateFeatureEpicWithWorktreeAsync();
        await CreateAcceptedFinalAsync(projectId, epicId);
        var persona = CreateDevopsPersona("DevOps-race");

        try
        {
            var results = await Task.WhenAll(Enumerable.Range(0, 8)
                .Select(_ => _client.PostAsync($"/api/tasks/features/{epicId}/merge", null)));

            results.Should().OnlyContain(r => r.StatusCode == HttpStatusCode.OK);
            var ids = new HashSet<string>();
            foreach (var r in results)
            {
                var t = await r.Content.ReadFromJsonAsync<JsonElement>();
                ids.Add(t.GetProperty("id").GetString()!);
            }
            ids.Should().HaveCount(1, "конкурентные POST завели ровно одну merge-задачу");
        }
        finally
        {
            Personas.Delete(persona.Id, OwnerId);
        }
    }

    // M4a: merge-задача уже закрыта (Done) → 409 «уже смержена».
    [Fact]
    public async Task MergeFeature_УжеСмержена_409()
    {
        var (epicId, _, _, projectId) = await CreateFeatureEpicWithWorktreeAsync();
        await CreateAcceptedFinalAsync(projectId, epicId);
        // Закрытая merge-задача фичи.
        await CreateProjectTaskAsync(projectId, new
        {
            title = "мерж", featureId = epicId, role = "merge", status = "done",
        });
        var persona = CreateDevopsPersona("DevOps-merged");

        try
        {
            var response = await _client.PostAsync($"/api/tasks/features/{epicId}/merge", null);
            response.StatusCode.Should().Be(HttpStatusCode.Conflict);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            body.GetProperty("error").GetString().Should().Contain("смержена");
        }
        finally
        {
            Personas.Delete(persona.Id, OwnerId);
        }
    }

    // M4b: фича закрыта эпиком (эпик Done) → 409 «закрыта».
    [Fact]
    public async Task MergeFeature_ЗакрытаЭпиком_409()
    {
        var projectId = CreateProject("merge");
        var wt = Path.Combine(_factory.TempDir, "merge_closed_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(wt);
        var branch = "feat/merge-closed-" + Guid.NewGuid().ToString("N")[..6];
        // Эпик сразу закрыт (Done).
        var epic = await CreateProjectTaskAsync(projectId, new
        {
            title = "эпик закрыт", role = "feature", status = "done",
            worktreePath = wt, worktreeBranch = branch,
        });
        var epicId = epic.GetProperty("id").GetString()!;
        await CreateAcceptedFinalAsync(projectId, epicId);
        var persona = CreateDevopsPersona("DevOps-closed");

        try
        {
            var response = await _client.PostAsync($"/api/tasks/features/{epicId}/merge", null);
            response.StatusCode.Should().Be(HttpStatusCode.Conflict);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            body.GetProperty("error").GetString().Should().Contain("закрыта");
        }
        finally
        {
            Personas.Delete(persona.Id, OwnerId);
        }
    }

    // M5: кросс-проектная DevOps-персона (проектная, с ProjectTasks-привязкой к проекту эпика)
    // не даёт ложного 409 — валидация идёт с externalScopes, как в Create.
    [Fact]
    public async Task MergeFeature_КроссПроектнаяПерсона_НеЛожный409()
    {
        var (epicId, _, _, epicProjectId) = await CreateFeatureEpicWithWorktreeAsync();
        await CreateAcceptedFinalAsync(epicProjectId, epicId);

        // Другой проект + проектная DevOps-персона с ProjectTasks-привязкой к проекту эпика.
        var otherProjectId = CreateProject("merge-other");
        var personas = _factory.Services.GetRequiredService<PersonaManager>();
        var persona = personas.Create(OwnerId, "DevOps-xproj-" + Guid.NewGuid().ToString("N")[..6],
            null, null, null, null, null, PersonaScope.Project, otherProjectId,
            null, null, true, null, null, PersonaAccess.Full, null,
            PersonaSpecialty.DevopsExecutor);
        personas.UpdateBindings(persona.Id, OwnerId,
        [
            new PersonaBinding { Type = PersonaBindingType.ProjectTasks, Target = epicProjectId, Mode = PersonaBindingMode.Auto },
        ]);

        try
        {
            var response = await _client.PostAsync($"/api/tasks/features/{epicId}/merge", null);
            response.StatusCode.Should().Be(HttpStatusCode.OK,
                "кросс-проектная привязка даёт полный доступ к проекту эпика — ложного 409 нет");
        }
        finally
        {
            personas.Delete(persona.Id, OwnerId);
        }
    }

    // M6: worktree эпика не существует на диске → 409 (вместо тихого старта в корне проекта).
    [Fact]
    public async Task MergeFeature_WorktreeНеСуществует_409()
    {
        var projectId = CreateProject("merge");
        var missing = Path.Combine(_factory.TempDir, "merge_missing_" + Guid.NewGuid().ToString("N")[..8]);
        var branch = "feat/merge-missing-" + Guid.NewGuid().ToString("N")[..6];
        var epic = await CreateProjectTaskAsync(projectId, new
        {
            title = "эпик без worktree", role = "feature",
            worktreePath = missing, worktreeBranch = branch,
        });
        var epicId = epic.GetProperty("id").GetString()!;
        await CreateAcceptedFinalAsync(projectId, epicId);
        var persona = CreateDevopsPersona("DevOps-nowt");

        try
        {
            var response = await _client.PostAsync($"/api/tasks/features/{epicId}/merge", null);
            response.StatusCode.Should().Be(HttpStatusCode.Conflict);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            body.GetProperty("error").GetString().Should().Contain("Worktree");
        }
        finally
        {
            Personas.Delete(persona.Id, OwnerId);
        }
    }

    // M6a (fail-closed): у эпика нет worktree-пути (в tasks_create необязателен) → 409,
    // иначе исполнитель тихо стартовал бы в корне проекта с описанием-заглушкой.
    [Fact]
    public async Task MergeFeature_БезWorktree_409()
    {
        var projectId = CreateProject("merge");
        var branch = "feat/merge-nowt-" + Guid.NewGuid().ToString("N")[..6];
        // Эпик с веткой, но без worktree-пути.
        var epic = await CreateProjectTaskAsync(projectId, new
        {
            title = "эпик без worktree", role = "feature",
            worktreeBranch = branch,
        });
        var epicId = epic.GetProperty("id").GetString()!;
        await CreateAcceptedFinalAsync(projectId, epicId);
        var persona = CreateDevopsPersona("DevOps-nowt");

        try
        {
            var response = await _client.PostAsync($"/api/tasks/features/{epicId}/merge", null);
            response.StatusCode.Should().Be(HttpStatusCode.Conflict);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            body.GetProperty("error").GetString().Should().Contain("Worktree");
        }
        finally
        {
            Personas.Delete(persona.Id, OwnerId);
        }
    }

    // M6b (fail-closed): у эпика есть worktree-путь, но нет ветки → 409.
    [Fact]
    public async Task MergeFeature_БезВетки_409()
    {
        var projectId = CreateProject("merge");
        var wt = Path.Combine(_factory.TempDir, "merge_nobr_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(wt);
        // Эпик с worktree-путём, но без ветки.
        var epic = await CreateProjectTaskAsync(projectId, new
        {
            title = "эпик без ветки", role = "feature",
            worktreePath = wt,
        });
        var epicId = epic.GetProperty("id").GetString()!;
        await CreateAcceptedFinalAsync(projectId, epicId);
        var persona = CreateDevopsPersona("DevOps-nobr");

        try
        {
            var response = await _client.PostAsync($"/api/tasks/features/{epicId}/merge", null);
            response.StatusCode.Should().Be(HttpStatusCode.Conflict);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            body.GetProperty("error").GetString().Should().Contain("Ветка");
        }
        finally
        {
            Personas.Delete(persona.Id, OwnerId);
        }
    }

    // M6c (fail-closed): worktree-путь есть на диске, но не числится в `git worktree list`
    // проекта (корень проекта вообще не git-репо) → 409, иначе AttachWorktreeAsync молча
    // деградирует в корень.
    [Fact]
    public async Task MergeFeature_ПутьНеWorktreeПроекта_409()
    {
        var projectId = CreateProject("merge");
        // Простая директория на диске, но не worktree проекта.
        var wt = Path.Combine(_factory.TempDir, "merge_notwt_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(wt);
        var branch = "feat/merge-notwt-" + Guid.NewGuid().ToString("N")[..6];
        var epic = await CreateProjectTaskAsync(projectId, new
        {
            title = "эпик с чужим путём", role = "feature",
            worktreePath = wt, worktreeBranch = branch,
        });
        var epicId = epic.GetProperty("id").GetString()!;
        await CreateAcceptedFinalAsync(projectId, epicId);
        var persona = CreateDevopsPersona("DevOps-notwt");

        try
        {
            var response = await _client.PostAsync($"/api/tasks/features/{epicId}/merge", null);
            response.StatusCode.Should().Be(HttpStatusCode.Conflict);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            body.GetProperty("error").GetString().Should().Contain("Worktree");
        }
        finally
        {
            Personas.Delete(persona.Id, OwnerId);
        }
    }

    // п.52/41/73: гонка — merge-задача становится Done в окне между снимком (верхний гард) и гейтом.
    // Снимок ПЕРЕД гейтом (верхний гард) видел Todo; внутри гейта (после await gate.WaitAsync) задача
    // уже Done → 409. Тест утверждает ТОЧНЫЙ текст «Фича смержена во время мержа» (отличен от верхнего
    // гарда «Фича уже смержена»): так он различает, какая ветка сработала, и не зеленеет вакуумно.
    // Воспроизведение: гейт заранее удержан (reflection), фоновая задача переворачивает merge в Done
    // и отпускает гейт; верхний гард (до гейта) уже отработал на Todo. Мутация: снять
    // `if (existing.Status == Done) return Conflict` → 200 вместо 409 → тест краснеет.
    [Fact]
    public async Task MergeFeature_Гонка_MergeDoneВнутриГейта_409()
    {
        var (epicId, wt, branch, projectId) = await CreateFeatureEpicWithWorktreeAsync();
        await CreateAcceptedFinalAsync(projectId, epicId);
        var persona = CreateDevopsPersona("DevOps-race-done");
        var tasks = _factory.Services.GetRequiredService<TaskManager>();

        // merge-задача как Todo: верхний гард (run.Merge is { Status: Done }) её не видит как Done.
        var merge = tasks.Create(projectId, OwnerId, new CreateTaskRequest(
            Title: "мерж", FeatureId: epicId, Role: "merge",
            PersonaId: persona.Id, Assignee: TaskItemAssignee.Claude,
            WorktreePath: wt, WorktreeBranch: branch));

        try
        {
            // Гейт заранее удержан (count=0): await gate.WaitAsync() внутри MergeFeature заблокируется.
            var gate = new SemaphoreSlim(0, 1);
            MergeGatesDict()[epicId] = gate;

            // Фоновая задача: после верхнего гарда (до гейта) переворачивает merge в Done и отпускает гейт.
            var flip = Task.Run(async () =>
            {
                await Task.Delay(50);
                tasks.Update(merge.Id, new UpdateTaskRequest(Status: TaskItemStatus.Done));
                gate.Release();
            });

            var result = await BuildController(RealGit()).MergeFeature(epicId);

            await flip;
            var obj = result.Should().BeAssignableTo<Microsoft.AspNetCore.Mvc.ObjectResult>().Subject;
            obj.StatusCode.Should().Be(409, "merge-задача стала Done внутри гейта → 409");
            // Точный текст: доказывает, что сработала ВНУТРЕННЯЯ проверка (гонка), а не верхний гард.
            // RelaxedJsonEscaping — чтобы кириллица не ушла в \uXXXX и сравнение по тексту шло честно.
            var body = System.Text.Json.JsonSerializer.Serialize(obj.Value,
                new System.Text.Json.JsonSerializerOptions
                {
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                });
            body.Should().Contain("Фича смержена во время мержа");
        }
        finally
        {
            Personas.Delete(persona.Id, OwnerId);
            MergeGatesDict().TryRemove(epicId, out _);
        }
    }

    // п.53: сбой git (WorktreeListAsync бросает) → 409 с честной причиной, не 500.
    // Stub-запускатель: Start бросает → RunAsync бросает → WorktreeListAsync бросает.
    // Мутация: убрать try/catch вокруг WorktreeListAsync → 500 (GitCommandException) → тест краснеет.
    [Fact]
    public async Task MergeFeature_СбойGitWorktreeList_409()
    {
        var (epicId, _, _, projectId) = await CreateFeatureEpicWithWorktreeAsync();
        await CreateAcceptedFinalAsync(projectId, epicId);
        var persona = CreateDevopsPersona("DevOps-gitfail");

        try
        {
            var result = await BuildController(ThrowingGit()).MergeFeature(epicId);

            result.Should().BeAssignableTo<Microsoft.AspNetCore.Mvc.ObjectResult>()
                .Which.StatusCode.Should().Be(409, "сбой git worktree list → 409 с честной причиной, не 500");
        }
        finally
        {
            Personas.Delete(persona.Id, OwnerId);
        }
    }

    // п.55: настоящий случай — репозиторий ЕСТЬ, путь эпика — просто папка рядом (не в `git worktree
    // list`) → 409. Отличается от M6c (там корень проекта вообще не git-репо).
    [Fact]
    public async Task MergeFeature_РепоЕсть_ПапкаРядомНеВList_409()
    {
        var projectId = CreateProject("merge");
        var projectRoot = Projects.GetById(projectId)!.RootPath;
        // Корень — реальный git-репо; worktree-путь — обычная папка, НЕ добавленная через `git worktree add`.
        var git = _factory.Services.GetRequiredService<GitService>();
        await git.InitAsync(null, projectRoot);
        var wt = Path.Combine(_factory.TempDir, "merge_plain_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(wt);
        var branch = "feat/merge-plain-" + Guid.NewGuid().ToString("N")[..6];
        var epic = await CreateProjectTaskAsync(projectId, new
        {
            title = "эпик: папка рядом, не в worktree list", role = "feature",
            worktreePath = wt, worktreeBranch = branch,
        });
        var epicId = epic.GetProperty("id").GetString()!;
        await CreateAcceptedFinalAsync(projectId, epicId);
        var persona = CreateDevopsPersona("DevOps-plain");

        try
        {
            var response = await _client.PostAsync($"/api/tasks/features/{epicId}/merge", null);
            response.StatusCode.Should().Be(HttpStatusCode.Conflict);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            body.GetProperty("error").GetString().Should().Contain("Worktree");
        }
        finally
        {
            Personas.Delete(persona.Id, OwnerId);
        }
    }

    // п.56: некорректный worktree-путь (Path.GetFullPath бросает) → 409, не 500.
    // Ноль-символ недопустим на обеих ОС (Windows/Unix) → GetFullPath бросает ArgumentException.
    [Fact]
    public async Task MergeFeature_НекорректныйПуть_409()
    {
        var projectId = CreateProject("merge");
        var badPath = "merge_bad_" + Guid.NewGuid().ToString("N")[..6] + "\0";
        var branch = "feat/merge-bad-" + Guid.NewGuid().ToString("N")[..6];
        var epic = await CreateProjectTaskAsync(projectId, new
        {
            title = "эпик с некорректным путём", role = "feature",
            worktreePath = badPath, worktreeBranch = branch,
        });
        var epicId = epic.GetProperty("id").GetString()!;
        await CreateAcceptedFinalAsync(projectId, epicId);
        var persona = CreateDevopsPersona("DevOps-badpath");

        try
        {
            var response = await _client.PostAsync($"/api/tasks/features/{epicId}/merge", null);
            response.StatusCode.Should().Be(HttpStatusCode.Conflict);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            body.GetProperty("error").GetString().Should().Contain("Worktree");
        }
        finally
        {
            Personas.Delete(persona.Id, OwnerId);
        }
    }

    // п.57: подсистема Git выключена (git == null) → честный 503 (как у Tasks), а не 409 «не найден».
    // Мутация: вернуть 409 при git==null → тест краснеет.
    [Fact]
    public async Task MergeFeature_GitВыключен_503()
    {
        var (epicId, _, _, projectId) = await CreateFeatureEpicWithWorktreeAsync();
        await CreateAcceptedFinalAsync(projectId, epicId);
        var persona = CreateDevopsPersona("DevOps-gitoff");

        try
        {
            var result = await BuildController(git: null).MergeFeature(epicId);

            result.Should().BeAssignableTo<Microsoft.AspNetCore.Mvc.ObjectResult>()
                .Which.StatusCode.Should().Be(503, "подсистема Git выключена → честный 503, не 409");
        }
        finally
        {
            Personas.Delete(persona.Id, OwnerId);
        }
    }

    // п.54: worktree фичи совпадает с корнем проекта (WorktreePath = RootPath) → 409:
    // иначе «мерж в основную ветку» происходил бы в том же дереве, что и исход.
    [Fact]
    public async Task MergeFeature_WorktreeСовпадаетСКорнем_409()
    {
        var projectId = CreateProject("merge");
        var projectRoot = Projects.GetById(projectId)!.RootPath;
        // worktree = корень проекта (реальная директория, но та же, что RootPath).
        var branch = "feat/merge-root-" + Guid.NewGuid().ToString("N")[..6];
        var epic = await CreateProjectTaskAsync(projectId, new
        {
            title = "эпик: worktree = корень", role = "feature",
            worktreePath = projectRoot, worktreeBranch = branch,
        });
        var epicId = epic.GetProperty("id").GetString()!;
        await CreateAcceptedFinalAsync(projectId, epicId);
        var persona = CreateDevopsPersona("DevOps-root");

        try
        {
            var response = await _client.PostAsync($"/api/tasks/features/{epicId}/merge", null);
            response.StatusCode.Should().Be(HttpStatusCode.Conflict);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            body.GetProperty("error").GetString().Should().Contain("совпадает с корнем");
        }
        finally
        {
            Personas.Delete(persona.Id, OwnerId);
        }
    }

    // п.42 (осознанно отменён): гейт НЕ убирается из MergeGates после мержа — один SemaphoreSlim на
    // фичу живёт до рестарта процесса (цена — десятки объектов). Снятие записи давало параллельные
    // критические секции (разные экземпляры гейта) и дубль merge-задачи; идемпотентность держит
    // свежий снимок после gate. Мутация: вернуть TryRemove в finally → запись исчезнет → тест краснеет.
    [Fact]
    public async Task MergeFeature_ГейтЖивётДоРестарта()
    {
        var (epicId, _, _, projectId) = await CreateFeatureEpicWithWorktreeAsync();
        await CreateAcceptedFinalAsync(projectId, epicId);
        var persona = CreateDevopsPersona("DevOps-gate");

        try
        {
            var response = await _client.PostAsync($"/api/tasks/features/{epicId}/merge", null);
            response.StatusCode.Should().Be(HttpStatusCode.OK);

            MergeGatesDict().ContainsKey(epicId).Should().BeTrue(
                "после мержа гейт остаётся в словаре: один SemaphoreSlim на фичу живёт до рестарта (п.42 отменён)");
        }
        finally
        {
            Personas.Delete(persona.Id, OwnerId);
            MergeGatesDict().TryRemove(epicId, out _);
        }
    }

    // M7: 404 мержа (чужой/удалённый эпик) и 404 GET features/{id} несут одинаковое тело
    // { error: "Фича не найдена" } — код и тело не различаются (фронт читает error).
    [Fact]
    public async Task MergeFeature_404ТелоРавноGet()
    {
        // Чужой эпик (второй юзер) → для владельца и POST merge, и GET дают 404.
        var created = await _stranger.PostAsJsonAsync("/api/tasks", new { title = "чужой эпик", role = "feature" });
        created.StatusCode.Should().Be(HttpStatusCode.OK);
        var epicId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();

        var merge404 = await _client.PostAsync($"/api/tasks/features/{epicId}/merge", null);
        merge404.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var mergeBody = await merge404.Content.ReadAsStringAsync();

        var get404 = await _client.GetAsync($"/api/tasks/features/{epicId}");
        get404.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var getBody = await get404.Content.ReadAsStringAsync();

        mergeBody.Should().Be(getBody, "тело 404 мержа совпадает с телом 404 GET");
        mergeBody.Should().Contain("Фича не найдена");
    }

    // m2: нестандартный id (символ вне белого списка FeatureIdPattern, «.») → 404 с тем же телом,
    // что у GET features/{id}: MergeFeature применяет тот же паттерн, что GET (одинаковый 404).
    [Fact]
    public async Task MergeFeature_НестандартныйId_404КакGet()
    {
        // «.» вне белого списка [A-Za-z0-9_-] → lookup не идёт, 404 «Фича не найдена» (как у GET).
        var merge404 = await _client.PostAsync("/api/tasks/features/abc.def/merge", null);
        merge404.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var mergeBody = await merge404.Content.ReadAsStringAsync();

        var get404 = await _client.GetAsync("/api/tasks/features/abc.def");
        get404.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var getBody = await get404.Content.ReadAsStringAsync();

        mergeBody.Should().Be(getBody, "m2: тело 404 мержа совпадает с телом 404 GET (FeatureIdPattern)");
        mergeBody.Should().Contain("Фича не найдена");
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

    // ─── Stub-запускатель: Start бросает → GitService.WorktreeListAsync бросает (сбой git, п.53) ─

    private sealed class ThrowingLauncherFactory : ILauncherFactory
    {
        public IProcessLauncher Local => new ThrowingLauncher();
        public IProcessLauncher ForOwner(string? ownerId) => new ThrowingLauncher();
        public IProcessLauncher ForProject(Project project) => new ThrowingLauncher();
    }

    private sealed class ThrowingLauncher : IProcessLauncher
    {
        public bool IsSandboxed => false;
        public bool TargetIsWindows => false;
        public IPathMapper Paths => new IdentityPathMapper();
        public string ClaudeCliCommand => "claude";
        public string HostTempDir => "";
        public string? McpApiUrlOverride => null;
        public Process Start(ProcessSpec spec) =>
            throw new InvalidOperationException("git не запустился (тест: сбой WorktreeListAsync)");
        public void Kill(Process process, string? turnId = null) { }
        public int EstimateCommandLineLength(ProcessSpec spec) => 0;
    }

    private sealed class IdentityPathMapper : IPathMapper
    {
        public string ToRuntime(string hostPath) => hostPath;
        public string ToHost(string runtimePath) => runtimePath;
    }
}
