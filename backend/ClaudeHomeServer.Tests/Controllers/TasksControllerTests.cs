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
using ClaudeHomeServer.Services.Docs;
using ClaudeHomeServer.Services.Execution;
using ClaudeHomeServer.Services.Git;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Spheres;
using ClaudeHomeServer.Services.Spend;
using ClaudeHomeServer.Services.Tasks;
using ClaudeHomeServer.Services.Tasks.Journal;
using ClaudeHomeServer.Services.Tasks.Projector;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
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
    // Б-mk-b: catalog — подменяемый каталог макетов (null — Docs выключена; заглушка — считает
    // вызовы, чтобы проверить, что для локального проекта Docs не дёргается).
    // journal — реальный FeatureJournalStore на временном каталоге (Б9c шаг 6: метод journal
    // интеграционно достижим, а не только approx). null — подсистема журнала выключена.
    private TasksController BuildController(GitService? git, MockupCatalog? catalog = null,
        ISpendFeatureReader? spend = null, FeatureJournalStore? journal = null)
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
            git: git,
            catalog: catalog,
            journal: journal,
            spend: spend);
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

    // Б-mk-b: подменяемый каталог макетов, считающий вызовы Find (G10: локальный проект
    // Docs не дёргает). Возвращает пустой список — результат не важен, важен факт вызова.
    private sealed class CountingMockupCatalog : MockupCatalog
    {
        public int FindCalls;
        public CountingMockupCatalog() : base(new DocsIndexService()) { }
        public override IReadOnlyList<MockupEntry> Find(string rootPath, string featureId,
            IReadOnlyCollection<string> linkedPaths)
        {
            FindCalls++;
            return [];
        }
    }

    // Б-mk-b: каталог, возвращающий фиксированный макет (для taskId/authorPersonaId
    // по linkedFiles).
    private sealed class FixedMockupCatalog : MockupCatalog
    {
        private readonly MockupEntry _entry;
        public FixedMockupCatalog(MockupEntry entry) : base(new DocsIndexService())
        {
            _entry = entry;
        }
        public override IReadOnlyList<MockupEntry> Find(string rootPath, string featureId,
            IReadOnlyCollection<string> linkedPaths) => [_entry];
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

    // Б-cl: перенос merge-задачи в другую фичу (PUT featureId) запрещён ⇒ 400 (и в не-Ready, и в Ready).
    // C#-аналог — TaskManagerTests.Update_Merge_ПереносВДругуюФичу_ФиналНеПринят_400.
    [Fact]
    public async Task Update_Merge_ПереносВДругуюФичу_ФиналНеПринят_400()
    {
        var epicA = await CreateTaskAsync(new { title = "эпик A", role = "feature" });
        var epicAId = epicA.GetProperty("id").GetString();
        var epicB = await CreateTaskAsync(new { title = "эпик B", role = "feature" });
        var epicBId = epicB.GetProperty("id").GetString();

        // Финал A принят → merge-задачу на A можно создать.
        await CreateTaskAsync(new { title = "финал A", featureId = epicAId, role = "final",
            status = "done", verification = new { verdict = "accepted" } });
        var merge = await CreateTaskAsync(new { title = "мерж", featureId = epicAId, role = "merge" });
        var mergeId = merge.GetProperty("id").GetString();

        // Фича B: финала нет (не Ready). Перенос merge-задачи в B ⇒ 400.
        var response = await _client.PutAsJsonAsync($"/api/tasks/{mergeId}", new { featureId = epicBId });
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Contain("Роль merge привязана к своей фиче");

        // П5: целевая фича Ready (финал принят) — перенос всё равно запрещён.
        await CreateTaskAsync(new { title = "финал B", featureId = epicBId, role = "final",
            status = "done", verification = new { verdict = "accepted" } });
        var ready = await _client.PutAsJsonAsync($"/api/tasks/{mergeId}", new { featureId = epicBId });
        ready.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ready.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString()
            .Should().Contain("Роль merge привязана к своей фиче");

        // Отвязка — по-прежнему 400.
        var unlink = await _client.PutAsJsonAsync($"/api/tasks/{mergeId}", new { featureId = "" });
        unlink.StatusCode.Should().Be(HttpStatusCode.BadRequest);
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
        // Чат реальный: с W4-CHT-B список фич чата строится по единому правилу
        // FeatureChatMembership, оно fail-closed по снимку владельца — несуществующего
        // чата в нём быть не может (прежня фикстура использовала синтетический id).
        var sessionId = await CreatePersonalChatAsync(OwnerId);
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

    // Б7: список (features?sessionId=) отдаёт чаты исполнителей (chatId/chatArchived) — те же,
    // что страница (features/{id}, ProjectFull): оба маршрута дают одинаковых исполнителей.
    // Мутация: не передать снимок в GetFeatures (ProjectForSession без SessionSnapshot) →
    // chatId = null → тест краснеет.
    [Fact]
    public async Task GetFeatures_Список_ЧатыИсполнителей_КакВСтранице()
    {
        var sessionId = await CreatePersonalChatAsync(OwnerId);
        var epic = await CreateTaskAsync(new { title = "эпик", role = "feature", sourceSessionId = sessionId });
        var epicId = epic.GetProperty("id").GetString();
        var step = await CreateTaskAsync(new
        {
            title = "шаг", featureId = epicId, role = "step", stepRef = "Ш1",
            linkedSessionId = sessionId,
        });
        var stepId = step.GetProperty("id").GetString();

        // Список: чат исполнителя (chatId) наружу, chatArchived=false.
        var list = await (await _client.GetAsync($"/api/tasks/features?sessionId={sessionId}"))
            .Content.ReadFromJsonAsync<JsonElement>();
        var listRun = list.EnumerateArray()
            .Single(f => f.GetProperty("featureId").GetString() == epicId);
        var listExec = listRun.GetProperty("stages").EnumerateArray()
            .Single(s => s.GetProperty("key").GetString() == "prod")
            .GetProperty("executors").EnumerateArray()
            .Single(e => e.GetProperty("taskId").GetString() == stepId);
        listExec.GetProperty("chatId").GetString().Should().Be(sessionId, "список: чат исполнителя наружу");
        listExec.GetProperty("chatArchived").GetBoolean().Should().BeFalse();

        // Страница: те же исполнители (оба маршрута одинаковы).
        var page = await (await _client.GetAsync($"/api/tasks/features/{epicId}"))
            .Content.ReadFromJsonAsync<JsonElement>();
        var pageExec = page.GetProperty("run").GetProperty("stages").EnumerateArray()
            .Single(s => s.GetProperty("key").GetString() == "prod")
            .GetProperty("executors").EnumerateArray()
            .Single(e => e.GetProperty("taskId").GetString() == stepId);
        pageExec.GetProperty("chatId").GetString().Should().Be(sessionId, "страница: чат исполнителя наружу");
        pageExec.GetProperty("chatId").GetString().Should().Be(
            listExec.GetProperty("chatId").GetString(), "оба маршрута дают одинаковых исполнителей");
    }

    // ─── Б4: модель чата (modelLabel/modelFull) и chats[] ─────────────────────────────────

    // «Авто» (Session.Model=null) → modelFull = резолв слота (UserModelTierResolver), не сырое «Авто».
    // Слот strong владельца → известная модель (SetModelTiers); задача modelTier=strong.
    // Мутация: резолв отдаёт сырое «Авто» в modelFull → тест краснеет.
    [Fact]
    public async Task GetFeature_МодельЧата_Авто_РезолвСлота()
    {
        var user = Users.FindByUsername(TestWebApplicationFactory.TestUsername)!;
        Users.SetModelTiers(user.Id, strong: "test-opus", medium: null, weak: null);

        var epic = await CreateTaskAsync(new { title = "эпик-авто", role = "feature" });
        var epicId = epic.GetProperty("id").GetString();
        var step = await CreateTaskAsync(new { title = "шаг-авто", featureId = epicId, role = "step", modelTier = "strong" });
        var stepId = step.GetProperty("id").GetString();
        var chatId = await CreatePersonalChatAsync(OwnerId, stepId);
        // «Авто»: CreateChatAsync сам резолвит модель из тира задачи (strong → test-opus),
        // поэтому принудительно обнуляем Session.Model — чат без закреплённой модели (слот).
        Sessions.GetById(chatId)!.Model = null;
        await _client.PutAsJsonAsync($"/api/tasks/{stepId}", new { linkedSessionId = chatId });

        var page = await (await _client.GetAsync($"/api/tasks/features/{epicId}"))
            .Content.ReadFromJsonAsync<JsonElement>();
        var exec = page.GetProperty("run").GetProperty("stages").EnumerateArray()
            .Single(s => s.GetProperty("key").GetString() == "prod")
            .GetProperty("executors").EnumerateArray()
            .Single(e => e.GetProperty("taskId").GetString() == stepId);
        exec.GetProperty("modelLabel").GetString().Should().Be("Авто");
        exec.GetProperty("modelFull").GetString().Should().Contain("test-opus", "«Авто» → резолв слота, не сырое «Авто»");
    }

    // Явная модель (Session.Model задана) → её имя (label + full), не «Авто».
    [Fact]
    public async Task GetFeature_МодельЧата_Явная_Имя()
    {
        var epic = await CreateTaskAsync(new { title = "эпик-явная", role = "feature" });
        var epicId = epic.GetProperty("id").GetString();
        var step = await CreateTaskAsync(new { title = "шаг-явная", featureId = epicId, role = "step" });
        var stepId = step.GetProperty("id").GetString();
        // Чат с явной моделью (Session.Model = "opus").
        var chatId = (await Sessions.CreateChatAsync(OwnerId, ClaudeMode.AcceptEdits, name: "chat", model: "opus", taskId: stepId)).Id;
        // Явная модель — напрямую в Session.Model (не через цепочку CreateChatAsync/CanonicalizeModel,
        // которую дёргают другие тесты общего fixture): тест не зависит от состояния реестра.
        Sessions.GetById(chatId)!.Model = "opus";
        await _client.PutAsJsonAsync($"/api/tasks/{stepId}", new { linkedSessionId = chatId });

        var page = await (await _client.GetAsync($"/api/tasks/features/{epicId}"))
            .Content.ReadFromJsonAsync<JsonElement>();
        var exec = page.GetProperty("run").GetProperty("stages").EnumerateArray()
            .Single(s => s.GetProperty("key").GetString() == "prod")
            .GetProperty("executors").EnumerateArray()
            .Single(e => e.GetProperty("taskId").GetString() == stepId);
        exec.GetProperty("modelLabel").GetString().Should().Be("Opus");
        exec.GetProperty("modelFull").GetString().Should().Be("Claude Opus");
    }

    // Список (features?sessionId=) и страница (features/{id}) дают одинаковые modelLabel/modelFull.
    [Fact]
    public async Task GetFeatures_ИСтраница_ОдинаковыеМодели()
    {
        var user = Users.FindByUsername(TestWebApplicationFactory.TestUsername)!;
        Users.SetModelTiers(user.Id, strong: "test-opus", medium: null, weak: null);

        var epic = await CreateTaskAsync(new { title = "эпик-мод", role = "feature" });
        var epicId = epic.GetProperty("id").GetString();
        var step = await CreateTaskAsync(new { title = "шаг-мод", featureId = epicId, role = "step", modelTier = "strong" });
        var stepId = step.GetProperty("id").GetString();
        var chatId = await CreatePersonalChatAsync(OwnerId, stepId);
        await _client.PutAsJsonAsync($"/api/tasks/{stepId}", new { linkedSessionId = chatId });
        // UpdateTaskRequest не имеет SourceSessionId — связываем эпик с чатом через
        // ExecutorLaunchedBySessionId (тот же признак, что читает ProjectForSession, §2).
        await _client.PutAsJsonAsync($"/api/tasks/{epicId}", new { executorLaunchedBySessionId = chatId });

        var list = await (await _client.GetAsync($"/api/tasks/features?sessionId={chatId}"))
            .Content.ReadFromJsonAsync<JsonElement>();
        var listExec = list.EnumerateArray().Single(f => f.GetProperty("featureId").GetString() == epicId)
            .GetProperty("stages").EnumerateArray()
            .Single(s => s.GetProperty("key").GetString() == "prod")
            .GetProperty("executors").EnumerateArray()
            .Single(e => e.GetProperty("taskId").GetString() == stepId);

        var page = await (await _client.GetAsync($"/api/tasks/features/{epicId}"))
            .Content.ReadFromJsonAsync<JsonElement>();
        var pageExec = page.GetProperty("run").GetProperty("stages").EnumerateArray()
            .Single(s => s.GetProperty("key").GetString() == "prod")
            .GetProperty("executors").EnumerateArray()
            .Single(e => e.GetProperty("taskId").GetString() == stepId);

        listExec.GetProperty("modelLabel").GetString().Should().Be(pageExec.GetProperty("modelLabel").GetString());
        listExec.GetProperty("modelFull").GetString().Should().Be(pageExec.GetProperty("modelFull").GetString());
        listExec.GetProperty("modelFull").GetString().Should().Contain("test-opus");
    }

    // Минор 4: «Авто» → та же модель, что боевой резолвер (ModelAssignmentResolver.ExecutorModel)
    // для того же исполнителя/персоны. Сверка: страница показывает ровно то, что даёт боевой
    // резолвер (тот же DI-синглтон, что использует контроллер). Кейс: у персоны ЯВНАЯ модель
    // ("opus"), у задачи уровня нет → боевой резолвер берёт модель персоны; старая цепочка
    // (task.ModelTier ?? persona.ModelTier ?? Medium) дала бы Medium-слот, не "opus".
    // Мутация: вернуть свою цепочку слота (не ExecutorModel) → страница даст Medium-слот → краснеет.
    [Fact]
    public async Task GetFeature_Авто_СверкаСБоевымРезолвером()
    {
        var user = Users.FindByUsername(TestWebApplicationFactory.TestUsername)!;
        // Medium-слот известен и отличен от "opus": если страница возьмёт Medium (старая цепочка),
        // а не модель персоны (боевой резолвер) — тест краснеет.
        Users.SetModelTiers(user.Id, strong: "test-opus", medium: "test-sonnet", weak: null);

        // Персона с явной моделью "opus" (боевой резолвер берёт её; старая цепочка — нет).
        var persona = Personas.Create(OwnerId, "персона-сверка", null, null, null,
            "opus", null, PersonaScope.Global, null, null, null, false);

        var epic = await CreateTaskAsync(new { title = "эпик-сверка", role = "feature" });
        var epicId = epic.GetProperty("id").GetString();
        var step = await CreateTaskAsync(new { title = "шаг-сверка", featureId = epicId, role = "step" });
        var stepId = step.GetProperty("id").GetString();
        // Задача → персона (без уровня задачи: боевой резолвер идёт через PersonaModel).
        _factory.Services.GetRequiredService<TaskManager>().GetById(stepId)!.PersonaId = persona.Id;

        var chatId = await CreatePersonalChatAsync(OwnerId, stepId);
        Sessions.GetById(chatId)!.Model = null; // «Авто»
        await _client.PutAsJsonAsync($"/api/tasks/{stepId}", new { linkedSessionId = chatId });

        // Боевой резолвер (тот же DI-синглтон, что использует контроллер): ожидаемая модель.
        var resolver = _factory.Services.GetRequiredService<ModelAssignmentResolver>();
        var task = _factory.Services.GetRequiredService<TaskManager>().GetById(stepId)!;
        var expected = resolver.ExecutorModel(task, Personas.Get(persona.Id, OwnerId), OwnerId);
        var expectedFull = expected is null ? "Авто" : $"Авто → {TasksController.ModelDisplayName(expected)}";

        var page = await (await _client.GetAsync($"/api/tasks/features/{epicId}"))
            .Content.ReadFromJsonAsync<JsonElement>();
        var exec = page.GetProperty("run").GetProperty("stages").EnumerateArray()
            .Single(s => s.GetProperty("key").GetString() == "prod")
            .GetProperty("executors").EnumerateArray()
            .Single(e => e.GetProperty("taskId").GetString() == stepId);
        exec.GetProperty("modelFull").GetString().Should().Be(expectedFull,
            "«Авто» = боевой резолвер (модель персоны), не своя цепочка слота");
        // Конкретно: явная модель персоны → "Claude Opus", не Medium-слот ("test-sonnet").
        exec.GetProperty("modelFull").GetString().Should().Be("Авто → Claude Opus");
    }

    // Минор 8: «Авто» = боевой резолвер (ExecutorModel) — сверка по кейсам «слот задачи» и
    // «дефолт места». Страница обязана показывать ровно то, что даёт боевой ModelAssignmentResolver
    // (тот же DI-синглтон, что в TaskExecutionService): (а) уровень задачи → слот тира; (б) без
    // уровня задачи → PersonaModel с дефолтом места «исполнитель» (Strong). Мутация: страница берёт
    // свою цепочку/слот (не ExecutorModel) → расхождение с боевым резолвером → краснеет.
    public static IEnumerable<object[]> АвтоСверкаCases()
    {
        // (scenario, taskTier): "strong" → «слот задачи»; null → «дефолт места» (уровня задачи нет).
        yield return new object[] { "слот задачи", "strong" };
        yield return new object[] { "дефолт места", null };
    }

    [Theory]
    [MemberData(nameof(АвтоСверкаCases))]
    public async Task GetFeature_Авто_СверкаСБоевымРезолвером_СлотЗадачиИДефолтМеста(string scenario, string? taskTier)
    {
        var user = Users.FindByUsername(TestWebApplicationFactory.TestUsername)!;
        Users.SetModelTiers(user.Id, strong: "test-opus", medium: "test-sonnet", weak: null);

        // Персона без явной модели (Model=null): боевой резолвер идёт по уровню (задачи/места).
        var persona = Personas.Create(OwnerId, "персона-" + scenario, null, null, null,
            null, null, PersonaScope.Global, null, null, null, false);

        var epic = await CreateTaskAsync(new { title = "эпик-" + scenario, role = "feature" });
        var epicId = epic.GetProperty("id").GetString();
        var step = await CreateTaskAsync(new { title = "шаг-" + scenario, featureId = epicId, role = "step" });
        var stepId = step.GetProperty("id").GetString();
        var mgr = _factory.Services.GetRequiredService<TaskManager>();
        mgr.GetById(stepId)!.PersonaId = persona.Id;
        if (taskTier is not null) mgr.GetById(stepId)!.ModelTier = Enum.Parse<ModelTier>(taskTier, ignoreCase: true);

        var chatId = await CreatePersonalChatAsync(OwnerId, stepId);
        Sessions.GetById(chatId)!.Model = null; // «Авто»
        await _client.PutAsJsonAsync($"/api/tasks/{stepId}", new { linkedSessionId = chatId });

        // Боевой резолвер (тот же DI-синглтон, что в TaskExecutionService): ожидаемая модель.
        var resolver = _factory.Services.GetRequiredService<ModelAssignmentResolver>();
        var task = mgr.GetById(stepId)!;
        var expected = resolver.ExecutorModel(task, Personas.Get(persona.Id, OwnerId), OwnerId);
        var expectedFull = expected is null ? "Авто" : $"Авто → {TasksController.ModelDisplayName(expected)}";

        var page = await (await _client.GetAsync($"/api/tasks/features/{epicId}"))
            .Content.ReadFromJsonAsync<JsonElement>();
        var exec = page.GetProperty("run").GetProperty("stages").EnumerateArray()
            .Single(s => s.GetProperty("key").GetString() == "prod")
            .GetProperty("executors").EnumerateArray()
            .Single(e => e.GetProperty("taskId").GetString() == stepId);
        exec.GetProperty("modelFull").GetString().Should().Be(expectedFull,
            $"«Авто» = боевой резолвер ({scenario}), не своя цепочка слота");
    }

    // Минор 5: owner-check задачи (fail-closed): чат, чья TaskId — чужая задача (не владельца),
    // резолвится как «без задачи» (PersonaModel(null) = null → «Авто»), а не по слоту чужой задачи.
    // Мутация «снять проверку владельца» → модель = слот strong (task.ModelTier=strong → "test-opus")
    // → full = "Авто → test-opus" ≠ "Авто" → краснеет.
    [Fact]
    public async Task ChatModelResolver_ЧужаяЗадача_OwnerCheck_БезЗадачи()
    {
        var user = Users.FindByUsername(TestWebApplicationFactory.TestUsername)!;
        Users.SetModelTiers(user.Id, strong: "test-opus", medium: null, weak: null);

        // Чужая задача (владелец = stranger) с modelTier=strong.
        var strangerResp = await _stranger.PostAsJsonAsync("/api/tasks",
            new { title = "чужая-задача", modelTier = "strong" });
        strangerResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var strangerTask = await strangerResp.Content.ReadFromJsonAsync<JsonElement>();
        var strangerTaskId = strangerTask.GetProperty("id").GetString()!;

        // Чат владельца, чья TaskId = чужая задача; Model=null («Авто»).
        var chatId = await CreatePersonalChatAsync(OwnerId, strangerTaskId);
        Sessions.GetById(chatId)!.Model = null;

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
            git: null,
            assignments: _factory.Services.GetRequiredService<ModelAssignmentResolver>(),
            catalog: null);
        var identity = new ClaimsIdentity([new Claim(JwtRegisteredClaimNames.Sub, OwnerId)], "test");
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) },
        };

        var resolver = controller.ChatModelResolver()!;
        var (label, full) = resolver(new FeatureIndexSession(chatId, strangerTaskId, null, false, null));

        // Owner-check: чужая задача (OwnerId ≠ UserId) → «без задачи» → PersonaModel(null) = null
        // → «Авто» (без модели), НЕ слот strong чужой задачи ("test-opus").
        label.Should().Be("Авто");
        full.Should().Be("Авто");
    }

    // chats[]: чат задачи фичи — executor; чужой чат (TaskId не задача фичи) — не в списке.
    // Мутация: убрать фильтр по снимку владельца в BuildFeatureChats → чужой чат попадает → краснеет.
    [Fact]
    public async Task GetFeature_Chats_ЧужойЧат_НеПопадает()
    {
        var epic = await CreateTaskAsync(new { title = "эпик-chats", role = "feature" });
        var epicId = epic.GetProperty("id").GetString();
        var step = await CreateTaskAsync(new { title = "шаг-chats", featureId = epicId, role = "step" });
        var stepId = step.GetProperty("id").GetString();
        var chatId = await CreatePersonalChatAsync(OwnerId, stepId);
        var foreignChatId = await CreatePersonalChatAsync(OwnerId, "foreign-task-not-in-feature");
        await _client.PutAsJsonAsync($"/api/tasks/{stepId}", new { linkedSessionId = chatId });

        var page = await (await _client.GetAsync($"/api/tasks/features/{epicId}"))
            .Content.ReadFromJsonAsync<JsonElement>();
        var chats = page.GetProperty("chats");
        chats.EnumerateArray().Should().Contain(c => c.GetProperty("id").GetString() == chatId, "чат задачи фичи в chats[]");
        chats.EnumerateArray().Should().NotContain(c => c.GetProperty("id").GetString() == foreignChatId, "чужой чат не в chats[]");
    }

    // chats[]: архивный чат → archived=true.
    [Fact]
    public async Task GetFeature_Chats_Архивный_Чат()
    {
        var epic = await CreateTaskAsync(new { title = "эпик-arch", role = "feature" });
        var epicId = epic.GetProperty("id").GetString();
        var step = await CreateTaskAsync(new { title = "шаг-arch", featureId = epicId, role = "step" });
        var stepId = step.GetProperty("id").GetString();
        var chatId = await CreatePersonalChatAsync(OwnerId, stepId);
        await _client.PutAsJsonAsync($"/api/tasks/{stepId}", new { linkedSessionId = chatId });
        // Архивируем чат (SetArchived: ArchivedAt=now → IsArchived=true).
        Sessions.SetArchived(chatId, true, "test");

        var page = await (await _client.GetAsync($"/api/tasks/features/{epicId}"))
            .Content.ReadFromJsonAsync<JsonElement>();
        page.GetProperty("chats").EnumerateArray()
            .Single(c => c.GetProperty("id").GetString() == chatId)
            .GetProperty("archived").GetBoolean().Should().BeTrue();
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
    public async Task GetFeature_ПодсистемаВыключена_503()
    {
        var controller = new TasksController(
            tasks: null!, broadcaster: null!, ai: null!, projects: null!,
            personas: null!, executor: null!, bindings: null!, sessions: null!, spheres: null!,
            noteSync: null, features: null);

        var result = await controller.GetFeature("any", null);

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

    // Reason вердикта в ленте: BuildEvents считается по ПОЛНОМУ run (reason = Verification.Notes),
    // а run в ответе облегчён (Slim обнуляет Notes — контракт master; verdict сохраняется).
    // Мутация «BuildEvents на облегчённом run» (features.Project вместо ProjectFull) →
    // reason = null → тест краснеет.
    [Fact]
    public async Task GetFeature_Вердикт_ReasonИзПолногоRun_RunВОтветеОблегчён()
    {
        var epic = await CreateTaskAsync(new { title = "эпик", role = "feature" });
        var epicId = epic.GetProperty("id").GetString();
        var review = await CreateTaskAsync(new
        {
            title = "ревью", featureId = epicId, role = "review",
            status = "done",
            verification = new { verdict = "accepted", notes = "принято: по контракту" },
        });
        var reviewId = review.GetProperty("id").GetString()!;

        var body = await (await _client.GetAsync($"/api/tasks/features/{epicId}"))
            .Content.ReadFromJsonAsync<JsonElement>();

        // Вердикт-событие несёт причину витка (Verification.Notes полной задачи).
        var accepted = FindEvent(body.GetProperty("events"), "accepted", reviewId);
        accepted.GetProperty("reason").GetString().Should().Be("принято: по контракту");

        // Run в ответе облегчён: Notes обнулён (контракт master), verdict на месте.
        var runReview = body.GetProperty("run").GetProperty("reviews").EnumerateArray()
            .Single(r => r.GetProperty("id").GetString() == reviewId);
        runReview.GetProperty("verification").GetProperty("notes").ValueKind
            .Should().Be(JsonValueKind.Null);
        runReview.GetProperty("verification").GetProperty("verdict").GetString()
            .Should().Be("accepted");
    }

    // Б-pl: вкладка «Планы и макеты» — plans[] в ответе GET features/{id}. plans — СПИСОК
    // документов (role/hasText/mockupPaths) БЕЗ текста: текст (ResultMarkdown) подгружается по
    // клику (GET /api/tasks/{id}). Мутация «plans по SlimRun» → hasText=false (краснеет);
    // «plans не в ответе» → поле отсутствует (краснеет).
    [Fact]
    public async Task GetFeature_Plans_СписокБезТекста()
    {
        var epic = await CreateTaskAsync(new { title = "эпик", role = "feature" });
        var epicId = epic.GetProperty("id").GetString();
        var plan = await CreateTaskAsync(new
        {
            title = "план", featureId = epicId, role = "plan",
            resultMarkdown = "См. docs/mockups/features-overview-3-plans-2.md",
        });
        var planId = plan.GetProperty("id").GetString()!;

        var body = await (await _client.GetAsync($"/api/tasks/features/{epicId}"))
            .Content.ReadFromJsonAsync<JsonElement>();

        // plans[] — массив; план на месте: role=plan, hasText=true, mockupPaths — путь.
        var plans = body.GetProperty("plans");
        plans.ValueKind.Should().Be(JsonValueKind.Array);
        var p = plans.EnumerateArray().Single(x => x.GetProperty("taskId").GetString() == planId);
        p.GetProperty("role").GetString().Should().Be("plan");
        p.GetProperty("hasText").GetBoolean().Should().BeTrue();
        p.GetProperty("mockupPaths").EnumerateArray()
            .Select(x => x.GetString()).Should().Equal("docs/mockups/features-overview-3-plans-2.md");

        // Текст (ResultMarkdown) в ответ НЕ попадает: run облегчён (Slim обнуляет ResultMarkdown).
        var runPlan = body.GetProperty("run").GetProperty("plan").EnumerateArray()
            .Single(r => r.GetProperty("id").GetString() == planId);
        runPlan.GetProperty("resultMarkdown").ValueKind.Should().Be(JsonValueKind.Null);
    }

    // Б5a/Б5b: stats (график времени) в ответе GET features/{id}: timeline с from/to/stages/
    // segments/totals. Б5b: журнал фичи пишется TaskManager'ом (created) → полный → source=journal
    // (точный режим). Индекс (features/index) и список (features?sessionId=) stats НЕ несут
    // (лёгкий путь) — поле отсутствует. Мутация «stats не в ответе» → поле отсутствует
    // (краснеет); «stats в индексе» → поле в строке индекса (краснеет).
    [Fact]
    public async Task GetFeature_Stats_Страница_ИндексИСписокБез()
    {
        // Чат реальный: список фич чата (W4-CHT-B) — по единому правилу FeatureChatMembership,
        // fail-closed по снимку владельца; синтетический id в нём не резолвится.
        var sessionId = await CreatePersonalChatAsync(OwnerId);
        var epic = await CreateTaskAsync(new { title = "эпик", role = "feature", sourceSessionId = sessionId });
        var epicId = epic.GetProperty("id").GetString();
        await CreateTaskAsync(new { title = "шаг", featureId = epicId, role = "step", stepRef = "Ш1" });

        // Страница: stats на месте, source=journal (журнал полный: created), timeline с from/to/totals.
        var page = await (await _client.GetAsync($"/api/tasks/features/{epicId}"))
            .Content.ReadFromJsonAsync<JsonElement>();
        var stats = page.GetProperty("stats");
        stats.GetProperty("source").GetString().Should().Be("journal");
        stats.GetProperty("truncated").GetBoolean().Should().BeFalse();
        var timeline = stats.GetProperty("timeline");
        timeline.TryGetProperty("from", out _).Should().BeTrue("timeline.from");
        timeline.TryGetProperty("to", out _).Should().BeTrue("timeline.to");
        timeline.TryGetProperty("stages", out _).Should().BeTrue("timeline.stages");
        timeline.TryGetProperty("segments", out _).Should().BeTrue("timeline.segments");
        timeline.TryGetProperty("totals", out _).Should().BeTrue("timeline.totals");

        // Б6: stats.breakdown (разрезы по персонам/моделям/ролям) и stats.longestNow на странице.
        var breakdown = stats.GetProperty("breakdown");
        breakdown.TryGetProperty("personas", out _).Should().BeTrue("breakdown.personas");
        breakdown.TryGetProperty("models", out _).Should().BeTrue("breakdown.models");
        breakdown.TryGetProperty("roles", out _).Should().BeTrue("breakdown.roles");
        stats.TryGetProperty("longestNow", out _).Should().BeTrue("stats.longestNow (null или объект)");

        // Индекс: строка фичи stats НЕ несёт (лёгкий путь) → breakdown тоже не несёт.
        var index = await GetIndexAsync();
        var row = index.GetProperty("features").EnumerateArray()
            .Single(f => f.GetProperty("featureId").GetString() == epicId);
        row.TryGetProperty("stats", out _).Should().BeFalse("индекс stats не несёт");

        // Список: run stats НЕ несёт.
        var list = await (await _client.GetAsync($"/api/tasks/features?sessionId={sessionId}"))
            .Content.ReadFromJsonAsync<JsonElement>();
        var listRun = list.EnumerateArray()
            .Single(f => f.GetProperty("featureId").GetString() == epicId);
        listRun.TryGetProperty("stats", out _).Should().BeFalse("список stats не несёт");
    }

    // Б5b: журнал выключен (зависимость FeatureJournalStore = null, подсистема журнала off) →
    // stats.source=approx, НЕ 500 (null-ссылка). Прямая сборка: features (FeatureProjector) на
    // месте, journal = null (BuildController не передаёт journal → дефолт null).
    [Fact]
    public async Task GetFeature_ЖурналаНет_ApproxБез500()
    {
        var epic = await CreateTaskAsync(new { title = "эпик", role = "feature" });
        var epicId = epic.GetProperty("id").GetString();

        var controller = BuildController(git: null); // journal = null (дефолт)
        var result = await controller.GetFeature(epicId, null);

        result.Should().BeOfType<Microsoft.AspNetCore.Mvc.OkObjectResult>();
        var body = (result as Microsoft.AspNetCore.Mvc.OkObjectResult)!.Value!;
        // CamelCase — как wire-формат приложения (SystemTextJson): FeatureStats.Source → source.
        var json = JsonSerializer.SerializeToElement(body,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        // journal = null → source=approx (не 500).
        json.GetProperty("stats").GetProperty("source").GetString().Should().Be("approx");
        json.GetProperty("stats").GetProperty("truncated").GetBoolean().Should().BeFalse();
    }

    // ─── Б-mk-b: макеты фичи (mockups[] + mockupsUnavailable) в GET features/{id} ───────────

    // Сериализация IActionResult.GetFeature в camelCase-JSON (как wire): для прямых сборок
    // контроллера (без HTTP). Enum-конвертер — для status/source у mockups (camelCase-строки).
    private static JsonElement FeatureJson(Microsoft.AspNetCore.Mvc.OkObjectResult result) =>
        JsonSerializer.SerializeToElement(result.Value!, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        });

    // Чужой проект: эпик (владелец A) ссылается на проект другого владельца → mockups: [].
    // Файлы чужого проекта не трогаем: GetByOwner не найдёт проект → [] (не device, не 500).
    [Fact]
    public async Task GetFeature_Mockups_ЧужойПроект_Пусто()
    {
        var epic = await CreateTaskAsync(new { title = "эпик", role = "feature" });
        var epicId = epic.GetProperty("id").GetString()!;
        // Проект второго владельца: эпик (владелец A) ссылается на него.
        var dir = Path.Combine(_factory.TempDir, "mk_foreign_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var foreign = Projects.Create("mk-foreign-" + Guid.NewGuid().ToString("N")[..6],
            dir, SecondOwnerId, "seconduser");
        _factory.Services.GetRequiredService<TaskManager>().GetById(epicId)!.ProjectId = foreign.Id;

        var body = await (await _client.GetAsync($"/api/tasks/features/{epicId}"))
            .Content.ReadFromJsonAsync<JsonElement>();

        body.GetProperty("mockups").GetArrayLength().Should().Be(0, "чужой проект → []");
        body.GetProperty("mockupsUnavailable").GetString().Should().NotBe("device", "не локальный проект");
    }

    // Локальный проект (файлы на устройстве) → mockups: [] + mockupsUnavailable: "device",
    // и каталог Docs НЕ вызывался (G10: диск сервера не читаем). Подменный каталог считает вызовы.
    [Fact]
    public async Task GetFeature_Mockups_ЛокальныйПроект_Device_КаталогНеВызывался()
    {
        var epic = await CreateTaskAsync(new { title = "эпик", role = "feature" });
        var epicId = epic.GetProperty("id").GetString()!;
        // Локальный проект (DeviceId задан) владельца.
        var dir = Path.Combine(_factory.TempDir, "mk_local_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var local = Projects.CreateLocal("mk-local-" + Guid.NewGuid().ToString("N")[..6], dir, OwnerId, "device-mk");
        _factory.Services.GetRequiredService<TaskManager>().GetById(epicId)!.ProjectId = local.Id;

        var counting = new CountingMockupCatalog();
        var controller = BuildController(git: null, catalog: counting);
        var result = (Microsoft.AspNetCore.Mvc.OkObjectResult)(await controller.GetFeature(epicId, null))!;

        var json = FeatureJson(result);
        json.GetProperty("mockups").GetArrayLength().Should().Be(0, "локальный проект → []");
        json.GetProperty("mockupsUnavailable").GetString().Should().Be("device");
        counting.FindCalls.Should().Be(0, "Docs не вызывался для локального проекта (G10)");
    }

    // Каталога нет в DI (catalog null) → 200 и mockups: [] (без флага, не 500).
    [Fact]
    public async Task GetFeature_Mockups_КаталогаНетВDI_200_Пусто()
    {
        var epic = await CreateTaskAsync(new { title = "эпик", role = "feature" });
        var epicId = epic.GetProperty("id").GetString()!;
        // Серверный проект владельца (не локальный): каталога нет в DI → catalog = null.
        var dir = Path.Combine(_factory.TempDir, "mk_docs_off_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var project = Projects.Create("mk-docs-off-" + Guid.NewGuid().ToString("N")[..6], dir, OwnerId, "testuser");
        _factory.Services.GetRequiredService<TaskManager>().GetById(epicId)!.ProjectId = project.Id;

        var controller = BuildController(git: null, catalog: null); // каталога нет в DI
        var result = (Microsoft.AspNetCore.Mvc.OkObjectResult)(await controller.GetFeature(epicId, null))!;

        var json = FeatureJson(result);
        json.GetProperty("mockups").GetArrayLength().Should().Be(0, "каталога нет в DI → []");
        json.GetProperty("mockupsUnavailable").GetString().Should().NotBe("device", "не локальный проект");
    }

    // taskId/authorPersonaId проставлены по linkedFiles: первая (по CreatedAt) задача фичи,
    // в linkedFiles которой есть path макета. Каталог — заглушка, возвращающая макет.
    [Fact]
    public async Task GetFeature_Mockups_TaskId_ПоLinkedFiles()
    {
        var epic = await CreateTaskAsync(new { title = "эпик", role = "feature" });
        var epicId = epic.GetProperty("id").GetString()!;
        // Шаг с linkedFiles, указывающим на макет (путь из каталога). PersonaId ставим
        // напрямую в сторе (мимо валидации персоны в Create — персона-фикстура не существует).
        var step = await CreateTaskAsync(new
        {
            title = "шаг", featureId = epicId, role = "step", stepRef = "Ш1",
            linkedFiles = new[] { "docs/mockups/mine.html" },
        });
        var stepId = step.GetProperty("id").GetString()!;
        _factory.Services.GetRequiredService<TaskManager>().GetById(stepId)!.PersonaId = "persona-mk";
        var dir = Path.Combine(_factory.TempDir, "mk_attr_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var project = Projects.Create("mk-attr-" + Guid.NewGuid().ToString("N")[..6], dir, OwnerId, "testuser");
        _factory.Services.GetRequiredService<TaskManager>().GetById(epicId)!.ProjectId = project.Id;
        // Шаг — в том же проекте, что эпик: OwnerTask фильтрует исполнителей по ProjectId эпика,
        // иначе макет не получил бы taskId/authorPersonaId этой задачи.
        _factory.Services.GetRequiredService<TaskManager>().GetById(stepId)!.ProjectId = project.Id;

        var catalog = new FixedMockupCatalog(new MockupEntry(
            Path: "docs/mockups/mine.html", Title: "Макет", Status: null,
            Files: [new MockupFile("docs/mockups/mine.html", true)],
            Source: MockupSource.LinkedFiles, ModifiedAt: null));
        var controller = BuildController(git: null, catalog: catalog);
        var result = (Microsoft.AspNetCore.Mvc.OkObjectResult)(await controller.GetFeature(epicId, null))!;

        var json = FeatureJson(result);
        var mockup = json.GetProperty("mockups").EnumerateArray().Single();
        mockup.GetProperty("taskId").GetString().Should().Be(stepId, "taskId — по задаче, в linkedFiles которой path");
        mockup.GetProperty("authorPersonaId").GetString().Should().Be("persona-mk");
    }

    // ─── W4-5: каталог читает проверенное worktree эпика (п.5.1) ────────────────────────────

    // Каталог, запоминающий корень каждого вызова Find и возвращающий маркерную запись с ОДНИМ
    // относительным путём для обоих деревьев (title — по корню). Существование файла-макета
    // он задаёт намеренно: в дереве эпика его нет (exists=false), в main — есть (exists=true).
    // Так проверяются и резолвер (какие корни вызывались), и слияние (побеждает запись дерева),
    // и семантика exists: запись дерева обещает файл только если он есть в дереве (W4-R1).
    private sealed class RootsMockupCatalog : MockupCatalog
    {
        private readonly string _mainRoot;
        public readonly List<string> Roots = [];
        public RootsMockupCatalog(string mainRoot) : base(new DocsIndexService())
        {
            _mainRoot = mainRoot;
        }
        public override IReadOnlyList<MockupEntry> Find(string rootPath, string featureId,
            IReadOnlyCollection<string> linkedPaths)
        {
            Roots.Add(rootPath);
            return [new MockupEntry(
                Path: "docs/mockups/x.md", Title: "mockup@" + rootPath, Status: null,
                Files: [new MockupFile("docs/mockups/x.html", rootPath == _mainRoot)],
                Source: MockupSource.Header, ModifiedAt: null)];
        }
    }

    // Проверенный worktree эпика (числится в `git worktree list` проекта): Find вызывается
    // дважды — сначала по дереву эпика, затем по корню; слияние по относительному пути:
    // запись дерева побеждает (tree="feature", title по корню дерева), а exists файла
    // проверяется по дереву фичи: в дереве файла нет (есть только в main) → exists=false,
    // иначе карточка обещала бы файл, которого читатель по ?featureId= не отдаст (W4-R1).
    [Fact]
    public async Task GetFeature_Mockups_ПроверенныйWorktree_КаталогЧитаетДеревоЭпика()
    {
        var (epicId, worktree, _, projectId) = await CreateFeatureEpicWithWorktreeAsync();
        var mainRoot = Projects.GetById(projectId)!.RootPath;

        var catalog = new RootsMockupCatalog(mainRoot);
        var controller = BuildController(RealGit(), catalog: catalog);
        var result = (Microsoft.AspNetCore.Mvc.OkObjectResult)(await controller.GetFeature(epicId, null))!;

        var json = FeatureJson(result);
        // Сначала дерево эпика, потом main — слияние по относительному пути.
        catalog.Roots.Should().Equal(worktree, mainRoot);
        var mockup = json.GetProperty("mockups").EnumerateArray().Single();
        mockup.GetProperty("tree").GetString().Should().Be("feature", "запись дерева побеждает main");
        mockup.GetProperty("title").GetString().Should().Be("mockup@" + worktree);
        mockup.GetProperty("files").EnumerateArray().Single().GetProperty("exists")
            .GetBoolean().Should().BeFalse("файл только в main — запись дерева его не обещает");
    }

    // Фолбэк: у эпика нет worktreePath — каталог вызывается один раз, по корню проекта,
    // запись помечена tree="main".
    [Fact]
    public async Task GetFeature_Mockups_БезWorktree_ФолбэкНаMainTree()
    {
        var epic = await CreateTaskAsync(new { title = "эпик", role = "feature" });
        var epicId = epic.GetProperty("id").GetString()!;
        var dir = Path.Combine(_factory.TempDir, "mk_notree_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var project = Projects.Create("mk-notree-" + Guid.NewGuid().ToString("N")[..6], dir, OwnerId, "testuser");
        _factory.Services.GetRequiredService<TaskManager>().GetById(epicId)!.ProjectId = project.Id;

        var catalog = new RootsMockupCatalog(project.RootPath);
        var controller = BuildController(RealGit(), catalog: catalog);
        var result = (Microsoft.AspNetCore.Mvc.OkObjectResult)(await controller.GetFeature(epicId, null))!;

        var json = FeatureJson(result);
        // Дерева нет — только main.
        catalog.Roots.Should().Equal(project.RootPath);
        var mockup = json.GetProperty("mockups").EnumerateArray().Single();
        mockup.GetProperty("tree").GetString().Should().Be("main");
        mockup.GetProperty("title").GetString().Should().Be("mockup@" + project.RootPath);
    }

    // Путь задачи есть на диске, но НЕ числится в `git worktree list` этого проекта (реальный
    // git-репозиторий, папка просто рядом) — отказ: каталог читает только main.
    // Мутация: в FeatureTreePathAsync заменить `list.Any(...)` на unconditional return path
    // — этот тест краснеет (чужая папка принялась бы как дерево эпика).
    [Fact]
    public async Task GetFeature_Mockups_ПутьНеВWorktreeList_ОтказФолбэкНаMain()
    {
        var (epicId, _, _, projectId) = await CreateFeatureEpicWithWorktreeAsync();
        var mainRoot = Projects.GetById(projectId)!.RootPath;
        // Эпик получает путь реальной папки, которой нет в `git worktree list` проекта.
        var alien = Path.Combine(_factory.TempDir, "mk_alien_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(alien);
        _factory.Services.GetRequiredService<TaskManager>().GetById(epicId)!.WorktreePath = alien;

        var catalog = new RootsMockupCatalog(mainRoot);
        var controller = BuildController(RealGit(), catalog: catalog);
        var result = (Microsoft.AspNetCore.Mvc.OkObjectResult)(await controller.GetFeature(epicId, null))!;

        var json = FeatureJson(result);
        // Непроверенный путь — только main.
        catalog.Roots.Should().Equal(mainRoot);
        var mockup = json.GetProperty("mockups").EnumerateArray().Single();
        mockup.GetProperty("tree").GetString().Should().Be("main");
    }

    // Сквозной сценарий W4-R1 (major 1+2) на НАСТОЯщем MockupCatalog и настоящем git-worktree:
    // записка-макет лежит в обоих деревьях и в шапке перечисляет три файла — один только в
    // дереве, другой только в main, третий — в обоих. Каталог записывает путь под tree="feature"
    // (запись дерева побеждает), Files — объединение путей: файл только из main не исчезает
    // (major 2), но его exists=false — читатель с ?featureId= читает ровно дерево и отдаёт 404
    // (major 1). Файл, есть в обоих деревьях → exists=true (минор R1B).
    // Мутация: убрать объединение Files в слиянии — этот тест краснеет (main_only пропадает).
    [Fact]
    public async Task GetFeature_Mockups_ФайлТолькоВMain_ДеревоНеОбещаетExists()
    {
        var (epicId, worktree, _, projectId) = await CreateFeatureEpicWithWorktreeAsync();
        var mainRoot = Projects.GetById(projectId)!.RootPath;
        var note = "# Макет\n\n**Фича:** " + epicId + "\n**Файлы:** tree_only.html, main_only.html, both.html\n";
        Directory.CreateDirectory(Path.Combine(mainRoot, "docs", "mockups"));
        Directory.CreateDirectory(Path.Combine(worktree, "docs", "mockups"));
        File.WriteAllText(Path.Combine(mainRoot, "docs", "mockups", "note.md"), note);
        File.WriteAllText(Path.Combine(worktree, "docs", "mockups", "note.md"), note);
        File.WriteAllText(Path.Combine(worktree, "docs", "mockups", "tree_only.html"), "tree only");
        File.WriteAllText(Path.Combine(mainRoot, "docs", "mockups", "main_only.html"), "main only");
        // Файл в ОБОИХ деревьях: есть и в дереве, и в main.
        File.WriteAllText(Path.Combine(worktree, "docs", "mockups", "both.html"), "both");
        File.WriteAllText(Path.Combine(mainRoot, "docs", "mockups", "both.html"), "both");

        var controller = BuildController(RealGit(),
            catalog: _factory.Services.GetRequiredService<MockupCatalog>());
        var result = (Microsoft.AspNetCore.Mvc.OkObjectResult)(await controller.GetFeature(epicId, null))!;

        var json = FeatureJson(result);
        var mockup = json.GetProperty("mockups").EnumerateArray().Single();
        mockup.GetProperty("tree").GetString().Should().Be("feature", "запись дерева побеждает main");
        var files = mockup.GetProperty("files").EnumerateArray()
            .ToDictionary(f => f.GetProperty("path").GetString()!, f => f.GetProperty("exists").GetBoolean());
        files.Keys.Should().Contain("docs/mockups/main_only.html",
            "файл, существующий только в main, не исчезает из каталога");
        files["docs/mockups/main_only.html"].Should().BeFalse(
            "в дереве файла нет — exists=false, обещания читателю нет");
        files["docs/mockups/tree_only.html"].Should().BeTrue("файл в дереве — обещание честное");
        files["docs/mockups/both.html"].Should().BeTrue(
            "файл существует в обоих деревьях — exists=true (минор R1B)");

        // Читатель подтверждает контракт: по ?featureId= файла из main нет, фолбэка на main нет.
        var content = await _client.GetAsync(
            $"/api/projects/{projectId}/files/content?path=docs/mockups/main_only.html&featureId={epicId}");
        content.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // Вторая половина контракта: файл макета есть только в проверенном дереве — каталог
    // говорит exists=true, и читатель с ?featureId= отдаёт 200 (exists=true ⇒ файл откроется).
    [Fact]
    public async Task GetFeature_Mockups_ФайлТолькоВДереве_ExistsTrue_ЧитательОтдаёт200()
    {
        var (epicId, worktree, _, projectId) = await CreateFeatureEpicWithWorktreeAsync();
        var relPath = "docs/mockups/only_tree.html";
        Directory.CreateDirectory(Path.Combine(worktree, "docs", "mockups"));
        File.WriteAllText(Path.Combine(worktree, "docs", "mockups", "only_tree.html"), "tree only");
        _factory.Services.GetRequiredService<TaskManager>().GetById(epicId)!.LinkedFiles = [relPath];

        var controller = BuildController(RealGit(),
            catalog: _factory.Services.GetRequiredService<MockupCatalog>());
        var result = (Microsoft.AspNetCore.Mvc.OkObjectResult)(await controller.GetFeature(epicId, null))!;

        var json = FeatureJson(result);
        var mockup = json.GetProperty("mockups").EnumerateArray().Single();
        mockup.GetProperty("tree").GetString().Should().Be("feature");
        mockup.GetProperty("files").EnumerateArray().Single().GetProperty("exists")
            .GetBoolean().Should().BeTrue("файл в дереве — обещание честное");

        var content = await _client.GetAsync(
            $"/api/projects/{projectId}/files/content?path={relPath}&featureId={epicId}");
        content.StatusCode.Should().Be(HttpStatusCode.OK, "exists=true ⇒ читатель отдаёт файл");
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

    // Б-cl (тест 8): частичный мерж — merge-задача Done, но State != merged (финал не принят,
    // эпик открыт). Старый верхний гард «Merge.Done → Фича уже смержена» давал ложный 409;
    // новый — по State == merged, поэтому фича не «уже смержена» и упирается в «финал не принят».
    // (Полный путь «создать новую» в идемпотентности — race-only: pre-gate/post-gate расходятся.)
    [Fact]
    public async Task MergeFeature_ЧастичныйМерж_НеУжеСмержена()
    {
        var (epicId, _, _, projectId) = await CreateFeatureEpicWithWorktreeAsync();
        // 1. Принятый финал — чтобы гард role=merge при создании merge-задачи прошёл.
        var final = await CreateProjectTaskAsync(projectId, new
        {
            title = "финал", featureId = epicId, role = "final", status = "done",
            verification = new { verdict = "accepted" },
        });
        // 2. Merge-задача (Done) — гард прошёл (финал принят).
        await CreateProjectTaskAsync(projectId, new
        {
            title = "мерж", featureId = epicId, role = "merge", status = "done",
        });
        // 3. «Разпринимаем» финал (returned) → частичный мерж: merge Done, финал не принят,
        //    эпик открыт → State != merged.
        await _client.PutAsJsonAsync($"/api/tasks/{final.GetProperty("id").GetString()}",
            new { verification = new { verdict = "returned" } });

        var response = await _client.PostAsync($"/api/tasks/features/{epicId}/merge", null);
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var error = body.GetProperty("error").GetString()!;
        error.Should().Contain("Финал не принят",
            "частичный мерж (merge Done, финал не принят) → «финал не принят»");
        error.Should().NotContain("уже смержена",
            "верхний гард теперь по State == merged, а не по Merge.Done — ложного «уже смержена» нет");
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
    // Воспроизведение: гейт заранее удержан (reflection); детектор ждёт, пока MergeFeature упрётся
    // в гейт (m_asyncHead non-null — детерминированно, не слепые 50 мс), флип тогда переворачивает
    // merge в Done и отпускает гейт; верхний гард (до гейта) уже отработал на Todo. Мутация: снять
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

            // Фоновая задача: ждёт, пока MergeFeature реально упрётся в гейт (поставит себя в очередь
            // ожидающих SemaphoreSlim), а не слепые 50 мс (флак e0fa7066: на загруженной машине снимок
            // line 918 мог опоздать за 50 мс, и верхний гард видел уже Done → «Фича уже смержена»).
            // Затем переворачивает merge в Done и отпускает гейт: внутри гейта re-snapshot видит Done →
            // 409 «Фича смержена во время мержа».
            // Зависание (5d098a07): раньше 5с-таймаут флипа срабатывал раньше, чем медленный git
            // MergeFeature дошёл до гейта, флип умирал не отпустив гейт, а gate.WaitAsync() (без
            // таймаута, TasksController.cs:968) висел вечно. Теперь: ожидание — через TCS с таймаутом
            // 30с, а gate.Release() — в finally (отпуск гарантирован даже по таймауту/сбою).
            var gateEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var flip = Task.Run(async () =>
            {
                try
                {
                    await gateEntered.Task.WaitAsync(TimeSpan.FromSeconds(30));
                    tasks.Update(merge.Id, new UpdateTaskRequest(Status: TaskItemStatus.Done));
                }
                finally
                {
                    gate.Release();
                }
            });
            // Детектор: MergeFeature вызвал gate.WaitAsync() → поставил себя в очередь асинхронных
            // ожидающих (m_asyncHead non-null) → сигналим флипу TCS-ом. m_asyncHead — единственный
            // надёжный сигнал: WaitAsync НЕ virtual (подклассить нельзя), а m_waitCount он НЕ трогает
            // (проверено: WaitAsync заполняет m_asyncHead, m_waitCount остаётся 0). Сам флип от поля
            // не зависит — ждёт TCS. Таймаут 30с — страховка.
            var asyncHeadField = typeof(SemaphoreSlim).GetField(
                "m_asyncHead", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var detector = Task.Run(async () =>
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    while (asyncHeadField.GetValue(gate) is null)
                        await Task.Delay(1, cts.Token);
                    gateEntered.TrySetResult(true);
                }
                catch (OperationCanceledException)
                {
                    // Таймаут детектора — страховка; флип всё равно отпустит гейт по своему таймауту.
                }
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

    // ─── Б9b: расход фичи (stats.spend) ─────────────────────────────────────────────────────

    // SpendStore в изолированной папке + шов SpendFeatureReader с заданными записями.
    private (SpendStore Store, SpendFeatureReader Reader) SpendStoreWith(params SpendRecord[] records)
    {
        var dir = Path.Combine(_factory.TempDir, "spend_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var store = new SpendStore(dir, 30);
        foreach (var r in records) store.Record(r);
        return (store, new SpendFeatureReader(store));
    }

    // Запись расхода (SpendRecord, Core): владелец, задача/чат, модель, токены, цена, источник.
    private static SpendRecord SpendRecord(string ownerId, string? taskId, string? sessionId, string model,
        long input, long output, double? cost, string source = "chat-turn") => new()
    {
        OwnerId = ownerId,
        TaskId = taskId,
        SessionId = sessionId,
        Model = model,
        InputTokens = input,
        OutputTokens = output,
        CostUsd = cost,
        Source = source,
        Timestamp = DateTime.UtcNow,
    };

    // Страница фичи через BuildController (spend — шов, journal — реальный журнал) → stats.
    private async Task<JsonElement> GetFeatureStats(ISpendFeatureReader? spend, string epicId,
        FeatureJournalStore? journal = null)
    {
        var controller = BuildController(null, null, spend, journal);
        var result = (ObjectResult)(await controller.GetFeature(epicId))!;
        // CamelCase — как wire-формат приложения (SystemTextJson): FeatureStats.Spend → spend.
        return JsonSerializer.SerializeToElement(result.Value!,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })
            .GetProperty("stats");
    }

    // Эпик + шаг + личный чат шага (linkedSessionId) — базовая фича для расхода.
    private async Task<(string EpicId, string StepId, string ChatId)> MakeFeature(string tag)
    {
        var epic = await CreateTaskAsync(new { title = "эпик-" + tag, role = "feature" });
        var epicId = epic.GetProperty("id").GetString()!;
        var step = await CreateTaskAsync(new { title = "шаг-" + tag, featureId = epicId, role = "step" });
        var stepId = step.GetProperty("id").GetString()!;
        var chatId = await CreatePersonalChatAsync(OwnerId, stepId);
        await _client.PutAsJsonAsync($"/api/tasks/{stepId}", new { linkedSessionId = chatId });
        return (epicId, stepId, chatId);
    }

    // Spend выключен (шов null) → 200 и stats.spend = null (не 500).
    [Fact]
    public async Task GetFeature_SpendВыключен_SpendNull()
    {
        var (epicId, _, _) = await MakeFeature("sp-off");

        var stats = await GetFeatureStats(null, epicId);

        var spendAbsent = !stats.TryGetProperty("spend", out var spend) || spend.ValueKind == JsonValueKind.Null;
        spendAbsent.Should().BeTrue("Spend выключен → stats.spend = null, не 500");
    }

    // Spend выключен E2E через DI (ревью Глеба, major): подсистема выключена гейтом
    // (Subsystems:spend:Enabled=false) → шов ISpendFeatureReader НЕ зарегистрирован в контейнере,
    // и контроллер строится КОНТЕЙНЕРОМ (не руками, как BuildController выше): GET feature →
    // 200 и stats.spend = null. Ручная передача null проверку не заменяет: она проверяет ветку
    // `spend is null`, но не то, что контейнер умеет построить контроллер без шова.
    // Честная мутация: убрать умолчание `= null` у параметра spend в конструкторе → контейнер
    // не строит контроллер (обязательный параметр не зарегистрирован) → падает именно этот тест.
    //
    // Гейт выключается `UseSetting` (аналог SpendDisabledTests), а НЕ процессной переменной
    // окружения: `UseSetting` едет в хост-конфигурацию → командная строка → CreateBuilder(args)
    // → успевает к ModuleLoader'у (гейт читается в LoadAll ДО builder.Build()); процессная env
    // была глобальна на процесс — при parallelizeTestCollections хосты в ДРУГИХ коллекциях,
    // поднятые в это окно, видели выключенный Spend (SpendDisabledTests ждёт включённый,
    // ImageEditSpendVisibilityTests требует SpendStore в сервисах) — окно реально, ревью Глеба.
    private sealed class SpendOffFactory : TestWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("Subsystems:spend:Enabled", "false");
        }
    }

    [Fact]
    public async Task GetFeature_SpendВыключен_DI_СпандNull()
    {
        using var offFactory = new SpendOffFactory();
        using var client = offFactory.CreateAuthenticatedClient();

        var response = await client.PostAsJsonAsync("/api/tasks", new { title = "эпик-sp-di", role = "feature" });
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var epicId = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;

        var feature = await client.GetAsync($"/api/tasks/features/{epicId}");
        feature.StatusCode.Should().Be(HttpStatusCode.OK, "Spend выключен → 200, не 500");
        var stats = (await feature.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("stats");

        var spendAbsent = !stats.TryGetProperty("spend", out var spend) || spend.ValueKind == JsonValueKind.Null;
        spendAbsent.Should().BeTrue("Spend выключен (шов не в DI) → stats.spend = null");
    }

    // Запись расхода задачи фичи (надёжная цена) → токены (вход/выход) + costUsd в stats.spend.
    [Fact]
    public async Task GetFeature_Spend_ЗаписьЗадачи_ТокеныИЦена()
    {
        var (epicId, stepId, _) = await MakeFeature("sp-cost");
        var (_, reader) = SpendStoreWith(SpendRecord(OwnerId, stepId, null, "claude-opus-4", 100, 50, 1.5));

        var spend = (await GetFeatureStats(reader, epicId)).GetProperty("spend");

        spend.ValueKind.Should().NotBe(JsonValueKind.Null);
        spend.GetProperty("inputTokens").GetInt64().Should().Be(100);
        spend.GetProperty("outputTokens").GetInt64().Should().Be(50);
        spend.GetProperty("costUsd").GetDouble().Should().Be(1.5);
        spend.GetProperty("costPartial").GetBoolean().Should().BeFalse();
        spend.GetProperty("hiddenCostModels").GetArrayLength().Should().Be(0);
    }

    // Ненадёжная модель (DeepSeek, встроенный дефолт): токены в сумму, стоимость нет,
    // costPartial=true, hiddenCostModels содержит модель.
    [Fact]
    public async Task GetFeature_Spend_НенадёжнаяМодель_ТокеныБезЦены()
    {
        var (epicId, stepId, _) = await MakeFeature("sp-deepseek");
        var (_, reader) = SpendStoreWith(SpendRecord(OwnerId, stepId, null, "deepseek-chat", 100, 50, 2.0));

        var spend = (await GetFeatureStats(reader, epicId)).GetProperty("spend");

        spend.GetProperty("inputTokens").GetInt64().Should().Be(100, "токены ненадёжной модели в сумме");
        spend.GetProperty("costUsd").GetDouble().Should().Be(0, "стоимость ненадёжной модели не в сумме");
        spend.GetProperty("costPartial").GetBoolean().Should().BeTrue();
        spend.GetProperty("hiddenCostModels").EnumerateArray()
            .Select(e => e.GetString()).Should().Contain("deepseek-chat");
    }

    // Локальная модель (Source=free) → стоимость честно 0 (не null, не «ненадёжная»).
    [Fact]
    public async Task GetFeature_Spend_FreeМодель_СтоимостьНуль()
    {
        var (epicId, stepId, _) = await MakeFeature("sp-free");
        var (_, reader) = SpendStoreWith(SpendRecord(OwnerId, stepId, null, "llama-3", 100, 50, 0, source: "free"));

        var spend = (await GetFeatureStats(reader, epicId)).GetProperty("spend");

        spend.GetProperty("inputTokens").GetInt64().Should().Be(100);
        spend.GetProperty("costUsd").GetDouble().Should().Be(0, "free → честный 0");
        spend.GetProperty("costPartial").GetBoolean().Should().BeFalse();
    }

    // Чужой владелец: запись с тем же taskId, но OwnerId другого юзера → не входит (фильтр владельца).
    [Fact]
    public async Task GetFeature_Spend_ЧужойВладелец_НеВходит()
    {
        var (epicId, stepId, _) = await MakeFeature("sp-foreign");
        // Запись с taskId фичи, но владелец = SecondOwner → фильтр OwnerId == owner отсекает.
        var (_, reader) = SpendStoreWith(SpendRecord(SecondOwnerId, stepId, null, "claude-opus-4", 100, 50, 1.5));

        var spend = (await GetFeatureStats(reader, epicId)).GetProperty("spend");

        spend.GetProperty("inputTokens").GetInt64().Should().Be(0,
            "чужая запись (OwnerId=SecondOwner) не входит в расход фичи");
        spend.GetProperty("costUsd").GetDouble().Should().Be(0);
    }

    // Ключевой чат фичи (сессия с TaskId = id эпика) входит в срез: запись расхода без taskId,
    // но с sessionId = ключевой чат → атрибутируется фиче (чат исключителен: только эта фича).
    // Мутация «ключевой чат всегда в срезе (мёртвая ветка исключительности)» → краснеет
    // GetFeature_Spend_ЧатВДвухФичах_НеВходит (100 ≠ 0).
    [Fact]
    public async Task GetFeature_Spend_КлючевойЧат_Входит()
    {
        var epic = await CreateTaskAsync(new { title = "эпик-sp-key", role = "feature" });
        var epicId = epic.GetProperty("id").GetString()!;
        // Ключевой чат = сессия с TaskId = id эпика (чат исполнения эпика); к эпику и привязан.
        var keyChat = await CreatePersonalChatAsync(OwnerId, epicId);
        await _client.PutAsJsonAsync($"/api/tasks/{epicId}", new { linkedSessionId = keyChat });

        // Запись расхода: без taskId, но sessionId = ключевой чат фичи.
        var (_, reader) = SpendStoreWith(SpendRecord(OwnerId, null, keyChat, "claude-opus-4", 100, 50, 1.5));

        var spend = (await GetFeatureStats(reader, epicId)).GetProperty("spend");

        spend.GetProperty("inputTokens").GetInt64().Should().Be(100,
            "ключевой чат (сессия с TaskId = id эпика) входит в срез → запись расхода учитывается");
    }

    // Чат в ДВУХ фичах (ключевой у A + оркестраторский у B) не входит в срез НИКОНЕГО
    // (общий чат делится в Б9c): без правила исключительности расход общего чата приписывался
    // каждой фиче (двойной счёт, critical ревью Глеба). Мутация «вернуть мёртвую ветку»
    // (ключевой чат = LinkedSessionId эпика, всегда в sessionIds) → краснеет (100 ≠ 0).
    [Fact]
    public async Task GetFeature_Spend_ЧатВДвухФичах_НеВходит()
    {
        // Фича A: эпик + ключевой чат C (сессия с TaskId = id эпика A, к эпику A привязан).
        var epicA = await CreateTaskAsync(new { title = "эпик-sp-2f-A", role = "feature" });
        var epicAId = epicA.GetProperty("id").GetString()!;
        var chatC = await CreatePersonalChatAsync(OwnerId, epicAId);
        await _client.PutAsJsonAsync($"/api/tasks/{epicAId}", new { linkedSessionId = chatC });

        // Фича B: эпик, у которого оркестраторский чат = тот же чат C.
        var epicB = await CreateTaskAsync(new { title = "эпик-sp-2f-B", role = "feature" });
        var epicBId = epicB.GetProperty("id").GetString()!;
        await _client.PutAsJsonAsync($"/api/tasks/{epicBId}", new { executorLaunchedBySessionId = chatC });

        // Запись расхода: без taskId, sessionId = общий чат C.
        var (_, reader) = SpendStoreWith(SpendRecord(OwnerId, null, chatC, "claude-opus-4", 100, 50, 1.5));

        // Срез A: ключевой чат не исключителен (входит ещё в B) → запись не входит.
        var spendA = (await GetFeatureStats(reader, epicAId)).GetProperty("spend");
        spendA.GetProperty("inputTokens").GetInt64().Should().Be(0,
            "чат в двух фичах не входит в срез A (двойного счёта нет)");

        // Срез B: чат C — не ключевой чат B (TaskId = id эпика A) и не сессия задачи B → тоже нет.
        var spendB = (await GetFeatureStats(reader, epicBId)).GetProperty("spend");
        spendB.GetProperty("inputTokens").GetInt64().Should().Be(0,
            "чат C не сессия задачи B и не её ключевой чат → не входит в срез B");
    }

    // `from` (с какой даты есть данные) отдаётся: = самая ранняя дата записи в срезе (yyyy-MM-dd).
    [Fact]
    public async Task GetFeature_Spend_From_Отдаётся()
    {
        var (epicId, stepId, _) = await MakeFeature("sp-from");
        // Запись расхода сегодня (после создания эпика, чтобы прошла фильтр from) → from = её дата.
        var rec = new SpendRecord
        {
            OwnerId = OwnerId,
            TaskId = stepId,
            Model = "claude-opus-4",
            InputTokens = 100,
            OutputTokens = 50,
            CostUsd = 1.5,
            Source = "chat-turn",
            Timestamp = DateTime.UtcNow,
        };
        var (_, reader) = SpendStoreWith(rec);

        var spend = (await GetFeatureStats(reader, epicId)).GetProperty("spend");

        spend.GetProperty("from").GetString().Should().Be(
            DateTime.UtcNow.ToString("yyyy-MM-dd"),
            "from = самая ранняя дата записи в срезе");
    }

    // Сквозной тест «итог = разрез» (ревью Глеба, major): записи расхода → один ответ
    // GET features/{id} → stats.spend (итог) и stats.breakdown (разрез) СОГЛАСНЫ по признаку
    // «цена ненадёжна». Слои связаны через шов: запись проходит SpendFeatureReader (ставит
    // UnreliableCost единой точкой правды) → Slice → FeatureBreakdown.Build. Разрез по РОЛЯМ:
    // все записи одной задачи (TaskId=stepId) маппятся в группу роли step, и признак «ненадёжно»
    // в группе (costUsd=null) обязан совпадать с итогом (costPartial=true, hiddenCostModels).
    // Мутации, краснящие именно этот тест:
    //   • «флаг UnreliableCost не выставляется» → deepseek-chat (цена 2.0) снова в сумму:
    //     итог costUsd=2.0, hidden теряет deepseek-chat → assertion costUsd=0 краснеет;
    //   • «запись без цены → 0 в итоге» (откат фикса ComputeTotals) → gpt-4o не в hiddenCostModels,
    //     а в разрезе costUsd=null → множества расходятся (BeEquivalentTo краснеет).
    [Fact]
    public async Task GetFeature_Spend_ИтогИРазрез_СогласныПоПризнакуНенадёжности()
    {
        var (epicId, stepId, _) = await MakeFeature("sp-agree");
        // Три записи одной задачи (stepId): (1) ненадёжная модель (deepseek, цена 2.0),
        // (2) обычная модель БЕЗ цены (gpt-4o, CostUsd=null), (3) free-модель (glm-local, цена 5.0).
        var (_, reader) = SpendStoreWith(
            SpendRecord(OwnerId, stepId, null, "deepseek-chat", 100, 50, 2.0),
            SpendRecord(OwnerId, stepId, null, "gpt-4o", 100, 50, null),
            SpendRecord(OwnerId, stepId, null, "glm-local:8b", 100, 50, 5.0, source: "free"));

        var stats = await GetFeatureStats(reader, epicId);
        var spend = stats.GetProperty("spend");
        var roles = stats.GetProperty("breakdown").GetProperty("roles");

        // Итог: в сумму входит только free-модель (надёжный 0); deepseek (ненадёжная) и
        // gpt-4o (без цены) — не в сумму, costPartial=true, обе в hiddenCostModels.
        spend.GetProperty("costUsd").GetDouble().Should().Be(0,
            "в сумму — только free-модель (надёжный 0); ненадёжная и без цены — нет");
        spend.GetProperty("costPartial").GetBoolean().Should().BeTrue(
            "есть запись с ненадёжной/отсутствующей ценой");
        spend.GetProperty("hiddenCostModels").EnumerateArray().Select(e => e.GetString())
            .Should().BeEquivalentTo(new[] { "deepseek-chat", "gpt-4o" },
            "модели, чья цена не в итоге: ненадёжная (deepseek) и без цены (gpt-4o)");

        // Разрез (роли): все три записи одной задачи → группа роли step. Признак «ненадёжно»
        // совпадает с итогом: есть ненадёжная/без-цены запись → costUsd=null (не 0, не 2.0).
        var stepRole = roles.EnumerateArray().Single(r => r.GetProperty("key").GetString() == "step");
        stepRole.GetProperty("costUsd").ValueKind.Should().Be(JsonValueKind.Null,
            "группа роли содержит запись без надёжной цены → costUsd=null (как итог: costPartial=true)");
        // Токены группы на месте (вход+выход трёх записей): признак «нет данных» не путать с «ненадёжно».
        stepRole.GetProperty("tokens").GetInt64().Should().Be(450, "3 записи × (100+50)");

        // Сквозное равенство: costUsd=null в разрезе ⇔ costPartial=true в итоге (по признаку).
        var breakdownHasUnreliable = roles.EnumerateArray()
            .Any(r => r.GetProperty("costUsd").ValueKind == JsonValueKind.Null);
        breakdownHasUnreliable.Should().Be(spend.GetProperty("costPartial").GetBoolean(),
            "итог (costPartial) и разрез (costUsd=null) согласны по признаку ненадёжной цены");
    }

    // ─── Б9c: доля общего (оркестраторского) чата — деление поровну между активными ─────────

    // Общий чат C (ключевой у A + оркестраторский у B) + обе фичи активны (идущие задачи) →
    // расход C делится 50/50: у каждой shared = половина, ИТОГ включает долю (срез C исключён,
    // доля добавлена сверху). Мутации: делить на ВСЕ фичи чата (не активные) / включить C в срез
    // целиком (двойной счёт) / игнорировать активность — краснеют.
    [Fact]
    public async Task GetFeature_Spend_ОбщийЧат_ДвеАктивные_Половина()
    {
        var tasks = _factory.Services.GetRequiredService<TaskManager>();
        // Фича A: эпик + ключевой чат C (сессия с TaskId = id эпика A) + идущая задача.
        var epicA = await CreateTaskAsync(new { title = "эпик-b9c-A", role = "feature" });
        var epicAId = epicA.GetProperty("id").GetString()!;
        var chatC = await CreatePersonalChatAsync(OwnerId, epicAId);
        await _client.PutAsJsonAsync($"/api/tasks/{epicAId}", new { linkedSessionId = chatC });
        var stepA = await CreateTaskAsync(new { title = "шаг-b9c-A", featureId = epicAId, role = "step" });
        var stepAId = stepA.GetProperty("id").GetString()!;
        // Идущая задача A: интервал [now-2h, now+1h] (CompletedAt=now+1h) → ход (now) строго внутри.
        // CompletedAt в будущем (нереалистично, но нужно: from=epic.CreatedAt≈now, запись при now,
        // интервал должен покрывать now — иначе запись режется фильтром from или границей half-open).
        tasks.GetById(stepAId)!.ClaudeStartedAt = DateTime.UtcNow.AddHours(-2);
        tasks.GetById(stepAId)!.CompletedAt = DateTime.UtcNow.AddHours(1);

        // Фича B: эпик, оркестраторский чат = тот же C + идущая задача.
        var epicB = await CreateTaskAsync(new { title = "эпик-b9c-B", role = "feature" });
        var epicBId = epicB.GetProperty("id").GetString()!;
        await _client.PutAsJsonAsync($"/api/tasks/{epicBId}", new { executorLaunchedBySessionId = chatC });
        var stepB = await CreateTaskAsync(new { title = "шаг-b9c-B", featureId = epicBId, role = "step" });
        var stepBId = stepB.GetProperty("id").GetString()!;
        tasks.GetById(stepBId)!.ClaudeStartedAt = DateTime.UtcNow.AddHours(-2);
        tasks.GetById(stepBId)!.CompletedAt = DateTime.UtcNow.AddHours(1);

        // Запись расхода общего чата C: 100 входных токенов, цена 2.0 (надёжная), ход в now
        // (после epic.CreatedAt, чтобы прошла фильтр from; внутри интервалов [now-2h, now+1h]).
        var rec = new SpendRecord
        {
            OwnerId = OwnerId,
            SessionId = chatC,
            Model = "claude-opus-4",
            InputTokens = 100,
            OutputTokens = 50,
            CostUsd = 2.0,
            Source = SpendSources.ChatTurn,
            Timestamp = DateTime.UtcNow,
        };
        var (_, reader) = SpendStoreWith(rec);

        // Срез A: общий чат C исключён из среза (0 токенов), доля A = 1/2 (A и B активны) = 50.
        var spendA = (await GetFeatureStats(reader, epicAId)).GetProperty("spend");
        spendA.GetProperty("shared").GetProperty("inputTokens").GetInt64().Should().Be(50,
            "общий чат делится поровну: A и B активны → 1/2");
        spendA.GetProperty("shared").GetProperty("method").GetString().Should().Be("approx",
            "метод оценки: approx (журнала нет в BuildController)");
        // Итог A включает долю: срез (0, C исключён) + доля (50) = 50.
        spendA.GetProperty("inputTokens").GetInt64().Should().Be(50, "итог включает долю общего чата");

        // Срез B: симметрично, доля B = 50.
        var spendB = (await GetFeatureStats(reader, epicBId)).GetProperty("spend");
        spendB.GetProperty("shared").GetProperty("inputTokens").GetInt64().Should().Be(50);
        spendB.GetProperty("inputTokens").GetInt64().Should().Be(50);
    }

    // Общий чат C, но только A активна (B не идёт) → A получает ЦЕЛИКОМ (1/1), не 1/2.
    // Мутация «делить на число ВСЕХ фич чата (2) вместо активных (1)» → 50 ≠ 100, краснеет.
    [Fact]
    public async Task GetFeature_Spend_ОбщийЧат_ОднаАктивная_Целиком()
    {
        var tasks = _factory.Services.GetRequiredService<TaskManager>();
        // Фича A: эпик + ключевой чат C + идущая задача (A активна).
        var epicA = await CreateTaskAsync(new { title = "эпик-b9c-1a-A", role = "feature" });
        var epicAId = epicA.GetProperty("id").GetString()!;
        var chatC = await CreatePersonalChatAsync(OwnerId, epicAId);
        await _client.PutAsJsonAsync($"/api/tasks/{epicAId}", new { linkedSessionId = chatC });
        var stepA = await CreateTaskAsync(new { title = "шаг-b9c-1a-A", featureId = epicAId, role = "step" });
        var stepAId = stepA.GetProperty("id").GetString()!;
        // Идущая задача A: интервал [now-2h, now+1h] → ход (now) внутри (A активна).
        tasks.GetById(stepAId)!.ClaudeStartedAt = DateTime.UtcNow.AddHours(-2);
        tasks.GetById(stepAId)!.CompletedAt = DateTime.UtcNow.AddHours(1);

        // Фича B: эпик, оркестраторский чат = C, НО задач нет (B не активна).
        var epicB = await CreateTaskAsync(new { title = "эпик-b9c-1a-B", role = "feature" });
        var epicBId = epicB.GetProperty("id").GetString()!;
        await _client.PutAsJsonAsync($"/api/tasks/{epicBId}", new { executorLaunchedBySessionId = chatC });

        var rec = new SpendRecord
        {
            OwnerId = OwnerId,
            SessionId = chatC,
            Model = "claude-opus-4",
            InputTokens = 100,
            CostUsd = 2.0,
            Source = SpendSources.ChatTurn,
            Timestamp = DateTime.UtcNow,
        };
        var (_, reader) = SpendStoreWith(rec);

        // A активна, B нет → знаменатель = 1 → A получает целиком (100), не 50.
        var spendA = (await GetFeatureStats(reader, epicAId)).GetProperty("spend");
        spendA.GetProperty("shared").GetProperty("inputTokens").GetInt64().Should().Be(100,
            "только A активна → доля = 1/1 = целиком (знаменатель = активные, не все фичи чата)");
        spendA.GetProperty("inputTokens").GetInt64().Should().Be(100);

        // B не активна → её доля 0 → shared: null (вклада в расход фичи нет); итог B без доли.
        var spendB = (await GetFeatureStats(reader, epicBId)).GetProperty("spend");
        spendB.GetProperty("shared").ValueKind.Should().Be(JsonValueKind.Null,
            "B не активна на момент хода → её доля 0 → shared: null");
        spendB.GetProperty("inputTokens").GetInt64().Should().Be(0, "B не активна → итог без доли");
    }

    // Фича БЕЗ общих чатов (ключевой чат исключителен: 1 фича) → shared: null (Б9b-поведение).
    [Fact]
    public async Task GetFeature_Spend_БезОбщихЧатов_SharedNull()
    {
        var (epicId, stepId, _) = await MakeFeature("b9c-noshare");
        // Запись расхода задачи фичи (надёжная цена) → расход есть, но общих чатов нет.
        var (_, reader) = SpendStoreWith(SpendRecord(OwnerId, stepId, null, "claude-opus-4", 100, 50, 1.5));

        var spend = (await GetFeatureStats(reader, epicId)).GetProperty("spend");

        spend.ValueKind.Should().NotBe(JsonValueKind.Null, "расход фичи есть");
        spend.GetProperty("shared").ValueKind.Should().Be(JsonValueKind.Null,
            "ключевой чат в 1 фиче (исключителен) → общих чатов нет → shared: null");
    }

    // Spend выключен E2E через DI (UseSetting, как в Б9b) → stats.spend = null → shared: null.
    // Честная мутация: та же, что в GetFeature_SpendВыключен_DI_СпандNull (умолчание spend=null).
    [Fact]
    public async Task GetFeature_Spend_ОбщийЧат_SpendВыключен_SharedNull_DI()
    {
        using var offFactory = new SpendOffFactory();
        using var client = offFactory.CreateAuthenticatedClient();

        var response = await client.PostAsJsonAsync("/api/tasks", new { title = "эпик-b9c-spoff", role = "feature" });
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var epicId = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;

        var feature = await client.GetAsync($"/api/tasks/features/{epicId}");
        feature.StatusCode.Should().Be(HttpStatusCode.OK);
        var stats = (await feature.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("stats");

        var spendAbsent = !stats.TryGetProperty("spend", out var spend) || spend.ValueKind == JsonValueKind.Null;
        spendAbsent.Should().BeTrue("Spend выключен (шов не в DI) → stats.spend = null → shared: null");
    }

    // ─── Б9c п.4: цена запроса GET features/{id} (10k записей, 20 фич) ─────────────────────
    //
    // Б9c проецирует ВСЕ фичи владельца: для общего чата считает активность КАЖДОЙ фичи в нём
    // (знаменатель деления) → цена запроса растёт с числом фич. Замер (кэш не вводим без него):
    //   COLD  — построение SpendStore (file I/O + десериализация 10k записей), разовое при старте;
    //   WARM  — GET features/{id} (spend в памяти), N итераций — цена на запрос в steady-state.
    // Условия: 10k записей расхода, 20 фич, общий чат, состоящий во ВСЕХ 20 (максимальный
    // знаменатель Б9c). Числа пишутся в вывод (console.log артефакта).
    [Fact]
    public async Task Benchmark_GetFeature_10kЗаписей_20Фич()
    {
        const int totalRecords = 10_000;
        const int featureCount = 20;
        const int warmIters = 10;

        // 20 фич (эпик + шаг); общий чат C: ключевой (executor) у эпика 0 + оркестраторский у
        // эпиков 1..19 → C состоит во ВСЕХ 20 (максимальный знаменатель Б9c).
        var epicIds = new List<string>();
        var stepIds = new List<string>();
        var epic0 = await CreateTaskAsync(new { title = "эпик-bench-0", role = "feature" });
        var epic0Id = epic0.GetProperty("id").GetString()!;
        epicIds.Add(epic0Id);
        var step0 = await CreateTaskAsync(new { title = "шаг-bench-0", featureId = epic0Id, role = "step" });
        stepIds.Add(step0.GetProperty("id").GetString()!);
        // Ключевой чат C: сессия с TaskId = id эпика 0 (executor-путь ChatFeatureIds).
        var chatC = await CreatePersonalChatAsync(OwnerId, epic0Id);
        await _client.PutAsJsonAsync($"/api/tasks/{epic0Id}", new { linkedSessionId = chatC });
        for (var i = 1; i < featureCount; i++)
        {
            var epic = await CreateTaskAsync(new { title = $"эпик-bench-{i}", role = "feature" });
            var epicId = epic.GetProperty("id").GetString()!;
            epicIds.Add(epicId);
            var step = await CreateTaskAsync(new { title = $"шаг-bench-{i}", featureId = epicId, role = "step" });
            stepIds.Add(step.GetProperty("id").GetString()!);
            // Оркестраторский чат = тот же C.
            await _client.PutAsJsonAsync($"/api/tasks/{epicId}", new { executorLaunchedBySessionId = chatC });
        }

        // 10k записей расхода: 500 на каждую фичу (taskId = шаг), даты равномерно по 30 дням.
        // SessionId = chatC (общий чат, состоящий во ВСЕХ 20 фичах) — записи идут в форму
        // ПРОДА (TaskId + SessionId общего чата) → путь Б9c (исключение из прямого среза +
        // деление) реально измеряется, а не только прямой срез (минор 1: раньше SessionId не
        // было → путь Б9c замером не покрывался).
        var dir = Path.Combine(_factory.TempDir, "bench_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var seedStore = new SpendStore(dir, 30);
        var swWrite = Stopwatch.StartNew();
        for (var n = 0; n < totalRecords; n++)
        {
            seedStore.Record(new SpendRecord
            {
                OwnerId = OwnerId,
                TaskId = stepIds[n % featureCount],
                SessionId = chatC,
                Model = "claude-opus-4",
                InputTokens = 1000,
                OutputTokens = 500,
                CostUsd = 0.05,
                Source = SpendSources.ChatTurn,
                Timestamp = DateTime.UtcNow.AddDays(-(n % 30)),
            });
        }
        swWrite.Stop();

        // COLD: построение НОВОГО SpendStore на той же папке (file I/O + десериализация 10k).
        var swCold = Stopwatch.StartNew();
        var coldStore = new SpendStore(dir, 30);
        swCold.Stop();

        // WARM: GET features/{id} (spend в памяти), N итераций — цена на запрос в steady-state.
        var reader = new SpendFeatureReader(coldStore);
        var swWarm = Stopwatch.StartNew();
        for (var i = 0; i < warmIters; i++)
            await GetFeatureStats(reader, epicIds[0]);
        swWarm.Stop();

        Console.WriteLine($"[B9c bench] записей={totalRecords}, фич={featureCount}, общий чат в {featureCount} фичах");
        Console.WriteLine($"[B9c bench] запись 10k: {swWrite.ElapsedMilliseconds} мс");
        Console.WriteLine($"[B9c bench] COLD (построение SpendStore, 10k записей): {swCold.ElapsedMilliseconds} мс");
        Console.WriteLine($"[B9c bench] WARM (GET features/{{id}} × {warmIters}): {swWarm.ElapsedMilliseconds} мс, " +
            $"на запрос {swWarm.Elapsed.TotalMilliseconds / warmIters:F1} мс");
    }

    // ─── Major 2: NRE вместо 400 на кривом planSteps (настоящий JSON → REST) ──────────
    // Личный эндпоинт POST /api/tasks парсит тело вручную (TaskCreateJson.Options, case-insensitive):
    // planSteps:[null] и {"title":null} раньше давали NullReferenceException → 500; теперь 400.

    [Fact]
    public async Task Create_PlanStepsЭлементNull_ЧерезJson_Дает400()
    {
        var response = await _client.PostAsJsonAsync("/api/tasks", new
        {
            title = "план",
            role = "plan",
            planSteps = new object[] { null },
        });
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "элемент-null в planSteps — 400, а не 500 (NRE)");
    }

    [Fact]
    public async Task Create_PlanStepsNullTitle_ЧерезJson_Дает400()
    {
        var response = await _client.PostAsJsonAsync("/api/tasks", new
        {
            title = "план",
            role = "plan",
            planSteps = new[] { new { stepRef = "B1", title = (string?)null } },
        });
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "null-title в planSteps — 400, а не 500 (NRE)");
    }

    // ─── Major 1 (регресс): смена роли плана на не-план + явный НЕПУСТОЙ planSteps ───────
    // PUT {role:"step", planSteps:[...]} — 400, состояние не изменилось (до дельты 200 + тихая
    // потеря списка). Пустой [] со сменой роли — допустим (снимает).
    [Fact]
    public async Task Update_ПланНаStep_СЯвнымСписком_ЧерезJson_Дает400()
    {
        var epic = await CreateTaskAsync(new { title = "эпик", role = "feature" });
        var epicId = epic.GetProperty("id").GetString();
        var plan = await CreateTaskAsync(new
        {
            title = "план",
            featureId = epicId,
            role = "plan",
            planSteps = new[] { new { stepRef = "B1", title = "бэк" } },
        });
        var planId = plan.GetProperty("id").GetString();

        // план → step + явный непустой planSteps: 400, а не 200 с тихой потерей списка
        var response = await _client.PutAsJsonAsync($"/api/tasks/{planId}", new
        {
            role = "step",
            planSteps = new[] { new { stepRef = "B2", title = "новый шаг" } },
        });
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "смена роли на не-план + непустой planSteps — 400, а не 200 с тихой потерей");

        // состояние не изменилось: роль осталась plan, список — исходный (1 шаг)
        var get = await _client.GetAsync($"/api/tasks/{planId}");
        var body = await get.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("role").GetString().Should().Be("plan");
        body.GetProperty("planSteps").EnumerateArray().Count(_ => true).Should().Be(1);
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

    // ─── Б9c, шаг 6: главный сторож «сохранение расхода» ───────────────────────────────────────

    // Сохранение расхода (решение Александра, п.1): на наборе «3 фичи, общий key-чат, общий
    // executor-чат, ход без активных, ход до создания B, дневная строка» сумма долей по фичам =
    // расходу общих чатов РОВНО (наибольший остаток, точное равенство токенов; деньги — допуск
    // 1e-9). Записи строго в форме ПРОДА (TaskId = session.TaskId, SessionId = чат).
    //
    // Членство: S = {A, B} (S — key-чат A (executor) + orchestrator-чат B), E = {B, C}
    // (E — executor-чат B + orchestrator-чат C через дочернюю задачу, проба F).
    //
    // Метод: (a) все журналы полные, НО в чате есть дневная строка → method=approx (дневная
    // строка понижает метку, п.5), (b) журнал B удалён → method=approx (неполный журнал).
    // Журнал A/B несёт pastDay-период + now-период; approx (ClaudeStartedAt) — только now →
    // дневная строка делится в journal (A+B активны на pastDay), но НЕ в approx (journal ≠ approx).
    [Fact]
    public async Task GetFeature_СохранениеРасхода_ОбщийЧат_ТочноеРавенство()
    {
        var now = DateTime.UtcNow;
        var pastDay = now.Date.AddDays(-40);
        // «now»-периоды активности — в ПРОШЛОМ (до now запроса): клип по epic.CreatedAt≈now
        // иначе обрезал бы будущие окна. A: [now-2.75h, now-1.75h), B: [now-2.25h, now-1.25h),
        // C: [now-1.8h, now-1h). Записи: r1=now-2h (A+B), r2=now-1.5h (B+C), r3=now-3h (никто),
        // r4=now-2.5h (только A).
        var aStart = now.AddHours(-2.75); var aEnd = now.AddHours(-1.75);
        var bStart = now.AddHours(-2.25); var bEnd = now.AddHours(-1.25);
        var cStart = now.AddHours(-1.8); var cEnd = now.AddHours(-1);
        var t1 = now.AddHours(-2); var t2 = now.AddHours(-1.5); var t3 = now.AddHours(-3); var t4 = now.AddHours(-2.5);

        // Фича A: эпик + шаг; key-чат S (executor: сессия S несёт TaskId=stepA).
        var (epicAId, stepAId, chatS) = await MakeFeature("cons-A");
        // Фича B: эпик + шаг; orchestrator-чат = S (stepB.ExecutorLaunchedBySessionId = S).
        var epicB = await CreateTaskAsync(new { title = "эпик-cons-B", role = "feature" });
        var epicBId = epicB.GetProperty("id").GetString()!;
        var stepB = await CreateTaskAsync(new
        {
            title = "шаг-cons-B", featureId = epicBId, role = "step",
        });
        var stepBId = stepB.GetProperty("id").GetString()!;
        // Общий executor-чат E: сессия E несёт TaskId=stepB (executor → B); привязан к stepB.
        var chatE = await CreatePersonalChatAsync(OwnerId, stepBId);
        await _client.PutAsJsonAsync($"/api/tasks/{stepBId}", new { linkedSessionId = chatE });
        // Фича C: эпик + шаг; orchestrator-чат = E (stepC.ExecutorLaunchedBySessionId = E).
        var epicC = await CreateTaskAsync(new { title = "эпик-cons-C", role = "feature" });
        var epicCId = epicC.GetProperty("id").GetString()!;
        var stepC = await CreateTaskAsync(new
        {
            title = "шаг-cons-C", featureId = epicCId, role = "step",
        });
        var stepCId = stepC.GetProperty("id").GetString()!;

        // Активность (approx): ClaudeStartedAt/CompletedAt — «now»-периоды (совпадают с
        // now-периодами журнала → r1-r4 одинаковы в обоих режимах).
        var tasks = _factory.Services.GetRequiredService<TaskManager>();
        // Оркестраторские связи задаются прямо на задачах (POST create их не принимает).
        tasks.GetById(stepBId)!.ExecutorLaunchedBySessionId = chatS;
        tasks.GetById(stepCId)!.ExecutorLaunchedBySessionId = chatE;
        tasks.GetById(stepAId)!.ClaudeStartedAt = aStart;
        tasks.GetById(stepAId)!.CompletedAt = aEnd;
        tasks.GetById(stepBId)!.ClaudeStartedAt = bStart;
        tasks.GetById(stepBId)!.CompletedAt = bEnd;
        tasks.GetById(stepCId)!.ClaudeStartedAt = cStart;
        tasks.GetById(stepCId)!.CompletedAt = cEnd;
        // Клип по epic.CreatedAt: A и B «созданы» на pastDay (иначе pastDay-периоды журнала
        // обрезались бы); C «создана» на cStart (иначе её «now»-период обрезался бы клипом).
        SetCreatedAt(tasks.GetById(epicAId)!, pastDay);
        SetCreatedAt(tasks.GetById(epicBId)!, pastDay);
        SetCreatedAt(tasks.GetById(epicCId)!, cStart);

        // Журналы (реальный FeatureJournalStore на временном каталоге): A и B — pastDay-период +
        // «now»-период; C — только «now»-период.
        var journalDir = Path.Combine(_factory.TempDir, "journal_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(journalDir);
        var journal = new FeatureJournalStore(journalDir);
        AppendJournal(journal, OwnerId, epicAId,
            ("created", pastDay.AddHours(9), epicAId),
            ("started", pastDay.AddHours(10), stepAId),
            ("stopped", pastDay.AddHours(11), stepAId),
            ("started", aStart, stepAId),
            ("stopped", aEnd, stepAId));
        AppendJournal(journal, OwnerId, epicBId,
            ("created", pastDay.AddHours(9), epicBId),
            ("started", pastDay.AddHours(12), stepBId),
            ("stopped", pastDay.AddHours(18), stepBId),
            ("started", bStart, stepBId),
            ("stopped", bEnd, stepBId));
        AppendJournal(journal, OwnerId, epicCId,
            ("created", cStart, epicCId),
            ("started", cStart, stepCId),
            ("stopped", cEnd, stepCId));

        // Записи расхода в форме ПРОДА (TaskId = session.TaskId, SessionId = чат).
        var (store, reader) = SpendStoreWith(
            // r1: на S, A+B активны (t1=now-2h) → делится A+B.
            ProdRecord(OwnerId, chatS, stepAId, "claude-opus-4", 100, 50, 1.0, t1),
            // r2: на E, B+C активны (t2=now-1.5h) → делится B+C.
            ProdRecord(OwnerId, chatE, stepBId, "claude-opus-4", 100, 50, 1.0, t2),
            // r3: на S, НИКТО не активен (t3=now-3h) → доля 0 (зачисляется в «неактивно»).
            ProdRecord(OwnerId, chatS, stepAId, "claude-opus-4", 100, 50, 1.0, t3),
            // r4: на S, ДО старта B (t4=now-2.5h: A активна, B ещё нет) → A целиком (1/1).
            ProdRecord(OwnerId, chatS, stepAId, "claude-opus-4", 100, 50, 1.0, t4),
            // r5: дневная строка на S (pastDay) → делится между фичами, активными в любой
            // момент pastDay (A и B по pastDay-периодам журнала).
            ProdRecord(OwnerId, chatS, stepAId, "claude-opus-4", 100, 50, 1.0, pastDay.AddHours(10).AddMinutes(30)));
        // pastDay старше окна (30 дней) → свёрнут в дневной агрегат.
        store.RollupOlderThan(store.WindowStart);

        // ── Сценарий (a): все журналы полные → method=journal ──
        var statsA = await GetFeatureStats(reader, epicAId, journal);
        var statsB = await GetFeatureStats(reader, epicBId, journal);
        var statsC = await GetFeatureStats(reader, epicCId, journal);

        statsA.GetProperty("spend").GetProperty("shared").GetProperty("method").GetString()
            .Should().Be("approx",
                "в наборе есть дневная строка → метод принудительно approx (п.5 решения Александра), " +
                "хотя интервалы активности берутся из полных журналов");

        // Точное равенство (токены — long):
        //   A: r1(50) + r3(0) + r4(100) + r5(50) = 200 input, 100 output, 2.0 cost.
        //   B: r1(50) + r3(0) + r4(0) + r5(50) + r2(50) = 150 input, 75 output, 1.5 cost.
        //   C: r2(50) = 50 input, 25 output, 0.5 cost.
        AssertShared(statsA, 200, 100, 2.0);
        AssertShared(statsB, 150, 75, 1.5);
        AssertShared(statsC, 50, 25, 0.5);
        // Сохранение: сумма долей (400) + неактивный ход r3 (100) = ВСЕМУ расходу (500).
        (200 + 150 + 50 + 100).Should().Be(500, "доли + неактивный ход = всему расходу");
        // Нет двойного счёта: все 5 записей лежат в ОБЩИХ чатах (S, E) → в прямом срезе фич
        // их быть не должно (исключение главнее оси taskIds, в проде запись несёт TaskId).
        // Итог spend.* ВКЛЮЧАЕТ долю → при пустом прямом срезе итог == доле.
        foreach (var st in new[] { statsA, statsB, statsC })
            st.GetProperty("spend").GetProperty("inputTokens").GetInt64()
                .Should().Be(st.GetProperty("spend").GetProperty("shared").GetProperty("inputTokens").GetInt64(),
                    "расход общих чатов идёт только в долю, не в прямой срез (итог == доле)");

        // ── Сценарий (b): журнал B (соседки) удалён → method=approx (у всех) ──
        journal.Delete(OwnerId, epicBId);
        var statsA2 = await GetFeatureStats(reader, epicAId, journal);
        var statsB2 = await GetFeatureStats(reader, epicBId, journal);
        var statsC2 = await GetFeatureStats(reader, epicCId, journal);

        statsA2.GetProperty("spend").GetProperty("shared").GetProperty("method").GetString()
            .Should().Be("approx", "журнала соседки B нет → method=approx");
        // Источник интервалов — ПО УЧАСТНИЦЕ (Б9c-4): у A журнал полный → её интервалы из
        // журнала (pastDay-период есть), у B журнала нет → approx (на pastDay не активна), у C
        // журнал полный. now-ходы (r1-r4) те же; дневная строка r5 (pastDay) — активна только A
        // → вся (100) у A, а не пополам, как при едином источнике на всех.
        //   A: r1(50) + r3(0) + r4(100) + r5(100) = 250 input, 125 output, 2.5 cost.
        //   B: r1(50) + r3(0) + r4(0) + r5(0) + r2(50) = 100 input, 50 output, 1.0 cost.
        //   C: r2(50) = 50 input, 25 output, 0.5 cost.
        // Сохранение: 250 + 100 + 50 + неактивный r3 (100) = 500.
        AssertShared(statsA2, 250, 125, 2.5);
        AssertShared(statsB2, 100, 50, 1.0);
        AssertShared(statsC2, 50, 25, 0.5);
        foreach (var st in new[] { statsA2, statsB2, statsC2 })
            st.GetProperty("spend").GetProperty("inputTokens").GetInt64()
                .Should().Be(st.GetProperty("spend").GetProperty("shared").GetProperty("inputTokens").GetInt64());
    }

    // ─── Б9c-3, сторож (i): общий чат с ТРЁМЯ участницами, ход 100, все активны (М6) ──────────

    // S = {A, B, C} (A — executor, B и C — orchestrator), ход 100 токенов, все активны.
    // Знаменатель = 3: наибольший остаток 100/3 = 34+33+33 = 100 РОВНО (input), 50/3 = 17+17+16.
    // Мутация М6 (знаменатель ≠ 3, напр. деление на 2) → сумма долей ≠ 100, тест краснеет.
    [Fact]
    public async Task GetFeature_Сторож_ТриУчастницы_100Токенов_ВсеАктивны()
    {
        var now = DateTime.UtcNow;
        var t1 = now.AddHours(-1);

        // Три фичи, все в общем чате S: A — executor (сессия S → stepA), B и C — orchestrator.
        var (epicAId, stepAId, chatS) = await MakeFeature("g3-A");
        var epicB = await CreateTaskAsync(new { title = "эпик-g3-B", role = "feature" });
        var epicBId = epicB.GetProperty("id").GetString()!;
        var stepB = await CreateTaskAsync(new { title = "шаг-g3-B", featureId = epicBId, role = "step" });
        var stepBId = stepB.GetProperty("id").GetString()!;
        var epicC = await CreateTaskAsync(new { title = "эпик-g3-C", role = "feature" });
        var epicCId = epicC.GetProperty("id").GetString()!;
        var stepC = await CreateTaskAsync(new { title = "шаг-g3-C", featureId = epicCId, role = "step" });
        var stepCId = stepC.GetProperty("id").GetString()!;

        var tasks = _factory.Services.GetRequiredService<TaskManager>();
        tasks.GetById(stepBId)!.ExecutorLaunchedBySessionId = chatS; // S = {A, B, C}
        tasks.GetById(stepCId)!.ExecutorLaunchedBySessionId = chatS;
        // Все три «созданы» до хода (иначе окно from=minCreated отрезало бы запись) и активны на t1.
        SetCreatedAt(tasks.GetById(epicAId)!, t1.AddHours(-1));
        SetCreatedAt(tasks.GetById(epicBId)!, t1.AddHours(-1));
        SetCreatedAt(tasks.GetById(epicCId)!, t1.AddHours(-1));
        tasks.GetById(stepAId)!.ClaudeStartedAt = t1.AddMinutes(-10);
        tasks.GetById(stepAId)!.CompletedAt = t1.AddMinutes(10);
        tasks.GetById(stepBId)!.ClaudeStartedAt = t1.AddMinutes(-10);
        tasks.GetById(stepBId)!.CompletedAt = t1.AddMinutes(10);
        tasks.GetById(stepCId)!.ClaudeStartedAt = t1.AddMinutes(-10);
        tasks.GetById(stepCId)!.CompletedAt = t1.AddMinutes(10);

        // Ход в S на 100 токенов (все три активны).
        var (store, reader) = SpendStoreWith(
            ProdRecord(OwnerId, chatS, stepAId, "claude-opus-4", 100, 50, 1.0, t1));

        var statsA = await GetFeatureStats(reader, epicAId);
        var statsB = await GetFeatureStats(reader, epicBId);
        var statsC = await GetFeatureStats(reader, epicCId);

        // Наибольший остаток 100/3: сумма 34+33+33=100 (input), 17+17+16=50 (output).
        // Индивидуальные значения зависят от порядка id (GUID, idx) → проверяем сумму и диапазон.
        long inA = SharedInput(statsA), inB = SharedInput(statsB), inC = SharedInput(statsC);
        (inA + inB + inC).Should().Be(100, "сумма долей input = расходу чата (100), знаменатель = 3");
        inA.Should().BeOneOf(33, 34);
        inB.Should().BeOneOf(33, 34);
        inC.Should().BeOneOf(33, 34);
        long outA = SharedOutput(statsA), outB = SharedOutput(statsB), outC = SharedOutput(statsC);
        (outA + outB + outC).Should().Be(50, "сумма долей output = расходу чата (50)");
    }

    // Доля shared: input/output токены (вспомогательное для сторожей с неопределённым idx).
    private static long SharedInput(JsonElement stats) =>
        stats.GetProperty("spend").GetProperty("shared").GetProperty("inputTokens").GetInt64();
    private static long SharedOutput(JsonElement stats) =>
        stats.GetProperty("spend").GetProperty("shared").GetProperty("outputTokens").GetInt64();

    // ─── Б9c-3, сторож (ii): два общих чата с РАЗНЫМ составом, ход при активной третьей (М10) ──

    // c1 = {F, G}, c2 = {F, H}; ход в c1 на 90 токенов, активны все три. Знаменатель = M[c1]
    // = {F, G} (НЕ объединение {F, G, H}): F и G делят 90 поровну (по 45), H (не в c1) — 0.
    // Мутация М10 (знаменатель = объединение участниц запроса = {F, G, H}) → F=30, G=30, H=30,
    // доли неверны (F должен 45, не 30) → тест краснеет.
    [Fact]
    public async Task GetFeature_Сторож_РазныйСоставЧатов_ТретьяАктивна()
    {
        var now = DateTime.UtcNow;
        var t1 = now.AddHours(-1);

        // F: executor-чат c1 (сессия c1 → stepF).
        var (epicFId, stepFId, c1) = await MakeFeature("g10-F");
        // G: orchestrator-чат = c1 → c1 = {F, G}.
        var epicG = await CreateTaskAsync(new { title = "эпик-g10-G", role = "feature" });
        var epicGId = epicG.GetProperty("id").GetString()!;
        var stepG = await CreateTaskAsync(new { title = "шаг-g10-G", featureId = epicGId, role = "step" });
        var stepGId = stepG.GetProperty("id").GetString()!;
        // H: executor-чат c2 (сессия c2 → stepH); F — orchestrator c2 → c2 = {F, H}.
        var epicH = await CreateTaskAsync(new { title = "эпик-g10-H", role = "feature" });
        var epicHId = epicH.GetProperty("id").GetString()!;
        var stepH = await CreateTaskAsync(new { title = "шаг-g10-H", featureId = epicHId, role = "step" });
        var stepHId = stepH.GetProperty("id").GetString()!;
        var c2 = await CreatePersonalChatAsync(OwnerId, stepHId);
        await _client.PutAsJsonAsync($"/api/tasks/{stepHId}", new { linkedSessionId = c2 });

        var tasks = _factory.Services.GetRequiredService<TaskManager>();
        tasks.GetById(stepGId)!.ExecutorLaunchedBySessionId = c1;   // c1 = {F, G}
        tasks.GetById(epicFId)!.ExecutorLaunchedBySessionId = c2;   // c2 = {F, H}
        // Все три «созданы» до хода (иначе окно from=minCreated отрезало бы запись) и активны на t1.
        SetCreatedAt(tasks.GetById(epicFId)!, t1.AddHours(-1));
        SetCreatedAt(tasks.GetById(epicGId)!, t1.AddHours(-1));
        SetCreatedAt(tasks.GetById(epicHId)!, t1.AddHours(-1));
        tasks.GetById(stepFId)!.ClaudeStartedAt = t1.AddMinutes(-10);
        tasks.GetById(stepFId)!.CompletedAt = t1.AddMinutes(10);
        tasks.GetById(stepGId)!.ClaudeStartedAt = t1.AddMinutes(-10);
        tasks.GetById(stepGId)!.CompletedAt = t1.AddMinutes(10);
        tasks.GetById(stepHId)!.ClaudeStartedAt = t1.AddMinutes(-10);
        tasks.GetById(stepHId)!.CompletedAt = t1.AddMinutes(10);

        // Ход в c1 на 90 токенов (F, G, H активны); output 44 — делим на 2 поровну (22/22).
        var (store, reader) = SpendStoreWith(
            ProdRecord(OwnerId, c1, stepFId, "claude-opus-4", 90, 44, 1.0, t1));

        var statsF = await GetFeatureStats(reader, epicFId);
        var statsG = await GetFeatureStats(reader, epicGId);
        var statsH = await GetFeatureStats(reader, epicHId);

        // Знаменатель = M[c1] = {F, G}: F и G по 45 (input), по 22 (output), H (не в c1) — 0.
        AssertShared(statsF, 45, 22, 0.5);
        AssertShared(statsG, 45, 22, 0.5);
        var hShared = statsH.GetProperty("spend").GetProperty("shared");
        var inH = hShared.ValueKind == JsonValueKind.Null ? 0 : hShared.GetProperty("inputTokens").GetInt64();
        inH.Should().Be(0, "H не в c1 → доля 0 (знаменатель = состав c1, не объединение)");
        (SharedInput(statsF) + SharedInput(statsG) + inH).Should()
            .Be(90, "сумма ФАКТИЧЕСКИХ долей = расходу c1 (90)");
    }

    // ─── Б9c-4, сторож: источник интервалов — ПО УЧАСТНИЦЕ (проба Глеба) ───────────────────────

    // F и H делят c2; F ещё делит c1 с G, у G журнала нет. Ход в c2 в t1: по журналу F активна
    // в t1 (approx-интервалы F начались бы только с t2). Если источник интервалов выбирается по
    // объединению составов запроса (мутация М11), запрос F видит G без журнала → всё approx →
    // F неактивна в t1 → доля F = 0, а запрос H (журналы F и H полные) даёт H = 45: сумма 45.
    // По участнице: F всегда из своего журнала → F = 45, H = 45, сумма = расходу чата (90).
    [Fact]
    public async Task GetFeature_Сторож_ИсточникИнтерваловПоУчастнице_СохранениеРасходаЧата()
    {
        var now = DateTime.UtcNow;
        var t1 = now.AddHours(-1);
        var t2 = t1.AddMinutes(30);

        var (epicFId, stepFId, c1) = await MakeFeature("g11-F");
        var epicG = await CreateTaskAsync(new { title = "эпик-g11-G", role = "feature" });
        var epicGId = epicG.GetProperty("id").GetString()!;
        var stepG = await CreateTaskAsync(new { title = "шаг-g11-G", featureId = epicGId, role = "step" });
        var stepGId = stepG.GetProperty("id").GetString()!;
        var epicH = await CreateTaskAsync(new { title = "эпик-g11-H", role = "feature" });
        var epicHId = epicH.GetProperty("id").GetString()!;
        var stepH = await CreateTaskAsync(new { title = "шаг-g11-H", featureId = epicHId, role = "step" });
        var stepHId = stepH.GetProperty("id").GetString()!;
        var c2 = await CreatePersonalChatAsync(OwnerId, stepHId);
        await _client.PutAsJsonAsync($"/api/tasks/{stepHId}", new { linkedSessionId = c2 });

        var tasks = _factory.Services.GetRequiredService<TaskManager>();
        tasks.GetById(stepGId)!.ExecutorLaunchedBySessionId = c1;   // c1 = {F, G}
        tasks.GetById(epicFId)!.ExecutorLaunchedBySessionId = c2;   // c2 = {F, H}
        SetCreatedAt(tasks.GetById(epicFId)!, t1.AddHours(-1));
        SetCreatedAt(tasks.GetById(epicGId)!, t1.AddHours(-1));
        SetCreatedAt(tasks.GetById(epicHId)!, t1.AddHours(-1));
        // approx F начинается ПОСЛЕ хода (t2); журнал F говорит, что в t1 она была активна.
        tasks.GetById(stepFId)!.ClaudeStartedAt = t2;
        tasks.GetById(stepFId)!.CompletedAt = t2.AddMinutes(20);
        tasks.GetById(stepGId)!.ClaudeStartedAt = t1.AddMinutes(-10);
        tasks.GetById(stepGId)!.CompletedAt = t1.AddMinutes(10);
        tasks.GetById(stepHId)!.ClaudeStartedAt = t1.AddMinutes(-10);
        tasks.GetById(stepHId)!.CompletedAt = t1.AddMinutes(10);

        // Полные журналы только у F и H; у G журнала нет.
        var journalDir = Path.Combine(_factory.TempDir, "journal_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(journalDir);
        var journal = new FeatureJournalStore(journalDir);
        AppendJournal(journal, OwnerId, epicFId,
            ("created", t1.AddMinutes(-20), epicFId),
            ("started", t1.AddMinutes(-10), stepFId),
            ("stopped", t1.AddMinutes(10), stepFId));
        AppendJournal(journal, OwnerId, epicHId,
            ("created", t1.AddMinutes(-20), epicHId),
            ("started", t1.AddMinutes(-10), stepHId),
            ("stopped", t1.AddMinutes(10), stepHId));

        // Ход в c2 на 90 токенов в t1.
        var (store, reader) = SpendStoreWith(
            ProdRecord(OwnerId, c2, stepHId, "claude-opus-4", 90, 44, 1.0, t1));

        var statsF = await GetFeatureStats(reader, epicFId, journal);
        var statsH = await GetFeatureStats(reader, epicHId, journal);

        // Нулевая доля приходит как shared: null — читаем её как 0, а не падаем на разборе.
        long InputOrZero(JsonElement st) =>
            st.GetProperty("spend").GetProperty("shared") is { ValueKind: JsonValueKind.Object } sh
                ? sh.GetProperty("inputTokens").GetInt64() : 0;
        var inF = InputOrZero(statsF);
        var inH = InputOrZero(statsH);
        (inF + inH).Should().Be(90L, "сохранение: F + H = расходу чата c2 (90)");
        inF.Should().Be(45L);
        inH.Should().Be(45L);
    }

    // ─── Б9c-3, сторож (iii): B создана ПОСЛЕ хода, ход целиком у A (ход до создания) ──────────

    // S = {A, B}; ход в S ДО создания B (B.CreatedAt > ход). Клип по epic.CreatedAt делает B
    // неактивной на момент хода → ход целиком у A (100, не 50), B — 0. Мутация (B активна до
    // создания) → A=50, B=50, тест краснеет.
    [Fact]
    public async Task GetFeature_Сторож_ХодДоСозданияB_ЦеликомУA()
    {
        var now = DateTime.UtcNow;
        var t1 = now.AddHours(-2);      // ход
        var bCreated = t1.AddHours(1);  // B создана ПОСЛЕ хода

        // A: executor-чат S (сессия S → stepA). B: orchestrator-чат = S → S = {A, B}.
        var (epicAId, stepAId, chatS) = await MakeFeature("g3-A");
        var epicB = await CreateTaskAsync(new { title = "эпик-g3-B", role = "feature" });
        var epicBId = epicB.GetProperty("id").GetString()!;
        var stepB = await CreateTaskAsync(new { title = "шаг-g3-B", featureId = epicBId, role = "step" });
        var stepBId = stepB.GetProperty("id").GetString()!;

        var tasks = _factory.Services.GetRequiredService<TaskManager>();
        tasks.GetById(stepBId)!.ExecutorLaunchedBySessionId = chatS;
        // A «создана» до хода и активна на t1; B «создана» на bCreated (после хода) → на t1
        // неактивна (клип по CreatedAt) → ход целиком у A.
        SetCreatedAt(tasks.GetById(epicAId)!, t1.AddHours(-1));
        tasks.GetById(stepAId)!.ClaudeStartedAt = t1.AddMinutes(-10);
        tasks.GetById(stepAId)!.CompletedAt = t1.AddMinutes(10);
        tasks.GetById(stepBId)!.ClaudeStartedAt = bCreated;
        tasks.GetById(stepBId)!.CompletedAt = bCreated.AddHours(1);
        SetCreatedAt(tasks.GetById(epicBId)!, bCreated);

        // Ход в S на 100 токенов ДО создания B.
        var (store, reader) = SpendStoreWith(
            ProdRecord(OwnerId, chatS, stepAId, "claude-opus-4", 100, 50, 1.0, t1));

        var statsA = await GetFeatureStats(reader, epicAId);
        var statsB = await GetFeatureStats(reader, epicBId);

        // Ход до создания B: A активна, B нет (клип) → ход целиком у A (100), B — 0.
        AssertShared(statsA, 100, 50, 1.0);
        var bShared = statsB.GetProperty("spend").GetProperty("shared");
        (bShared.ValueKind == JsonValueKind.Null ? 0 : bShared.GetProperty("inputTokens").GetInt64())
            .Should().Be(0, "B создана после хода → её доля 0 (клип по CreatedAt)");
    }

    // ─── Б9c-3, сторож: без дневной строки и с полными журналами → method=journal ─────────────

    // S = {A, B}; все журналы полные, дневных строк НЕТ → method=journal (не approx).
    // Мутация (дневная строка не понижает метку / журнал не полный) → method=approx, тест краснеет.
    [Fact]
    public async Task GetFeature_Сторож_БезДневнойСтроки_МетодJournal()
    {
        var now = DateTime.UtcNow;
        var t1 = now.AddHours(-1);

        var (epicAId, stepAId, chatS) = await MakeFeature("gj-A");
        var epicB = await CreateTaskAsync(new { title = "эпик-gj-B", role = "feature" });
        var epicBId = epicB.GetProperty("id").GetString()!;
        var stepB = await CreateTaskAsync(new { title = "шаг-gj-B", featureId = epicBId, role = "step" });
        var stepBId = stepB.GetProperty("id").GetString()!;

        var tasks = _factory.Services.GetRequiredService<TaskManager>();
        tasks.GetById(stepBId)!.ExecutorLaunchedBySessionId = chatS; // S = {A, B}
        // Обе «созданы» до хода (иначе окно from=minCreated отрезало бы запись) и активны на t1.
        SetCreatedAt(tasks.GetById(epicAId)!, t1.AddHours(-1));
        SetCreatedAt(tasks.GetById(epicBId)!, t1.AddHours(-1));
        tasks.GetById(stepAId)!.ClaudeStartedAt = t1.AddMinutes(-10);
        tasks.GetById(stepAId)!.CompletedAt = t1.AddMinutes(10);
        tasks.GetById(stepBId)!.ClaudeStartedAt = t1.AddMinutes(-10);
        tasks.GetById(stepBId)!.CompletedAt = t1.AddMinutes(10);

        // Полные журналы A и B (created → started → stopped), без дневных строк.
        var journalDir = Path.Combine(_factory.TempDir, "journal_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(journalDir);
        var journal = new FeatureJournalStore(journalDir);
        AppendJournal(journal, OwnerId, epicAId,
            ("created", t1.AddMinutes(-20), epicAId),
            ("started", t1.AddMinutes(-10), stepAId),
            ("stopped", t1.AddMinutes(10), stepAId));
        AppendJournal(journal, OwnerId, epicBId,
            ("created", t1.AddMinutes(-20), epicBId),
            ("started", t1.AddMinutes(-10), stepBId),
            ("stopped", t1.AddMinutes(10), stepBId));

        // Ход в S (без дневных строк).
        var (store, reader) = SpendStoreWith(
            ProdRecord(OwnerId, chatS, stepAId, "claude-opus-4", 100, 50, 1.0, t1));

        var statsA = await GetFeatureStats(reader, epicAId, journal);

        statsA.GetProperty("spend").GetProperty("shared").GetProperty("method").GetString()
            .Should().Be("journal", "все журналы полные и нет дневных строк → method=journal");
    }

    // CreatedAt — init-only: ставим через рефлексию (тест: фича «создана» в прошлом).
    private static void SetCreatedAt(TaskItem task, DateTime createdAt) =>
        typeof(TaskItem).GetProperty("CreatedAt")!.SetValue(task, createdAt);

    // Журнал фичи: записи в хронологическом порядке (created → started/stopped → …).
    private void AppendJournal(FeatureJournalStore journal, string ownerId, string featureId,
        params (string Kind, DateTime At, string TaskId)[] entries)
    {
        foreach (var e in entries)
            journal.Append(ownerId, featureId, new FeatureJournalEntry
            {
                At = e.At,
                Kind = e.Kind,
                TaskId = e.TaskId,
            });
    }

    // Доля shared: input/output токены (точное равенство) + costUsd (допуск 1e-9).
    private static void AssertShared(JsonElement stats, long expectedInput, long expectedOutput,
        double expectedCost)
    {
        var shared = stats.GetProperty("spend").GetProperty("shared");
        shared.GetProperty("inputTokens").GetInt64().Should().Be(expectedInput);
        shared.GetProperty("outputTokens").GetInt64().Should().Be(expectedOutput);
        shared.GetProperty("costUsd").GetDouble().Should().BeApproximately(expectedCost, 1e-9);
    }

    // Запись расхода в форме ПРОДА: TaskId = TaskId сессии (задача, к которой привязан чат),
    // SessionId = id сессии. Так ходы общего/оркестраторского чата выглядят в проде.
    private static SpendRecord ProdRecord(string ownerId, string sessionId, string taskId, string model,
        long input, long output, double? cost, DateTime timestamp) => new()
    {
        OwnerId = ownerId,
        TaskId = taskId,
        SessionId = sessionId,
        Model = model,
        InputTokens = input,
        OutputTokens = output,
        CostUsd = cost,
        Source = "chat-turn",
        Timestamp = timestamp,
    };

}
