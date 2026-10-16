using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Дефекты на MCP-пути (TasksToolset через /mcp/tasks/{sessionId}). Закрывает четыре проверки:
// • tasks_create дефектом в done-колонку → Deny «нельзя создавать сразу в Done»
//   (парный REST-гейт EnsureNotClosedAtCreate);
// • tasks_create дефектом в review-колонку без repro → Deny «Repro.Steps»
//   (EnsureReproOnReview);
// • tasks_update/tasks_complete дефекта в done без verification/outcome → Deny
//   (EnsureVerificationOnClose, дизъюнкция закрытого дефекта);
// • outcome для обычной задачи — поле только Defect, обычное задание игнорирует
//   (паспорт 432b6de2, проверка Д-3);
// • Brief отдаёт kind и hasVerification, чтобы персоны в списке видели, что карточка
//   это дефект, а в MCP-схеме tools/list есть свойства kind/repro/verification/outcome.
[Trait("Category", "Integration")]
public class TasksToolsetDefectTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly HttpClient _client;
    private readonly string _projectDir;

    public TasksToolsetDefectTests(TestWebApplicationFactory factory)
    {
        _client = factory.CreateAuthenticatedClient();
        _projectDir = Path.Combine(factory.TempDir, "tasks_toolset_defect_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_projectDir);
    }

    private async Task<(string ProjectId, string SessionId)> CreateProjectWithSessionAsync()
    {
        var project = await _client.PostAsJsonAsync("/api/projects",
            new { name = "TasksToolsetDefect", rootPath = _projectDir, createDirectory = true });
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

    private async Task SetBoardAsync(string projectId, object columns)
    {
        var resp = await _client.PutAsJsonAsync($"/api/projects/{projectId}/board-columns", columns);
        resp.EnsureSuccessStatusCode();
    }

    // Вызов MCP-инструмента в формате JSON-RPC через /mcp/tasks/{sessionId}.
    // Возвращает {content[0].text, isError} — text содержит JSON объекта задачи при успехе
    // или текст Deny при отказе гейта.
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

    // ─── Схема: tools/list отдаёт новые свойства ────────────────────────────

    [Fact]
    public async Task ToolsList_СодержитKindReproVerificationOutcome()
    {
        var (_, sessionId) = await CreateProjectWithSessionAsync();
        var resp = await _client.PostAsync($"/mcp/tasks/{sessionId}",
            new StringContent(JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0", id = 1, method = "tools/list",
            }), System.Text.Encoding.UTF8, "application/json"));
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync())
            .GetProperty("result").GetProperty("tools");
        var byName = body.EnumerateArray()
            .ToDictionary(t => t.GetProperty("name").GetString()!);
        var create = byName["tasks_create"].GetProperty("inputSchema").GetProperty("properties");
        create.TryGetProperty("kind", out _).Should().BeTrue("kind в tasks_create");
        create.TryGetProperty("repro", out _).Should().BeTrue("repro в tasks_create");
        var update = byName["tasks_update"].GetProperty("inputSchema").GetProperty("properties");
        update.TryGetProperty("kind", out _).Should().BeTrue("kind в tasks_update");
        update.TryGetProperty("repro", out _).Should().BeTrue("repro в tasks_update");
        update.TryGetProperty("verification", out _).Should().BeTrue("verification в tasks_update");
        update.TryGetProperty("outcome", out _).Should().BeTrue("outcome в tasks_update");
        var complete = byName["tasks_complete"].GetProperty("inputSchema").GetProperty("properties");
        complete.TryGetProperty("verification", out _).Should().BeTrue("verification в tasks_complete");
        complete.TryGetProperty("outcome", out _).Should().BeTrue("outcome в tasks_complete");
    }

    // ─── Схема: tools/list отдаёт поля фичи (featureId/role/stepRef/lane/reviewOf) и verdict ──

    [Fact]
    public async Task ToolsList_СодержитПоляФичиИВердикт()
    {
        var (_, sessionId) = await CreateProjectWithSessionAsync();
        var resp = await _client.PostAsync($"/mcp/tasks/{sessionId}",
            new StringContent(JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0", id = 1, method = "tools/list",
            }), System.Text.Encoding.UTF8, "application/json"));
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync())
            .GetProperty("result").GetProperty("tools");
        var byName = body.EnumerateArray()
            .ToDictionary(t => t.GetProperty("name").GetString()!);

        var create = byName["tasks_create"].GetProperty("inputSchema").GetProperty("properties");
        foreach (var field in new[] { "featureId", "role", "stepRef", "lane", "reviewOf" })
            create.TryGetProperty(field, out _).Should().BeTrue($"{field} в tasks_create");

        var update = byName["tasks_update"].GetProperty("inputSchema").GetProperty("properties");
        foreach (var field in new[] { "featureId", "role", "stepRef", "lane", "reviewOf" })
            update.TryGetProperty(field, out _).Should().BeTrue($"{field} в tasks_update");

        // verdict — в схеме verification и у update, и у complete
        foreach (var tool in new[] { "tasks_update", "tasks_complete" })
        {
            var verification = byName[tool].GetProperty("inputSchema").GetProperty("properties")
                .GetProperty("verification").GetProperty("properties");
            verification.TryGetProperty("verdict", out _).Should().BeTrue($"verdict в {tool}.verification");
        }

        // role/verdict — enum: значения ровно из TaskRoles.All / TaskVerdicts.All
        var roleEnum = create.GetProperty("role").GetProperty("enum").EnumerateArray()
            .Select(x => x.GetString()).ToList();
        roleEnum.Should().BeEquivalentTo(ClaudeHomeServer.Models.TaskRoles.All);
        var verdictEnum = byName["tasks_complete"].GetProperty("inputSchema").GetProperty("properties")
            .GetProperty("verification").GetProperty("properties").GetProperty("verdict")
            .GetProperty("enum").EnumerateArray().Select(x => x.GetString()).ToList();
        verdictEnum.Should().BeEquivalentTo(ClaudeHomeServer.Models.TaskVerdicts.All);
    }

    // ─── Разметка фичи: tasks_create с featureId/role/stepRef/lane/reviewOf ─────

    [Fact]
    public async Task Create_ФичаПоля_Создаётся()
    {
        var (projectId, sessionId) = await CreateProjectWithSessionAsync();

        // Эпик фичи: role=feature, без personaId (иначе NormalizePersonaAssignee)
        var (epicText, _) = await CallToolAsync(sessionId, "tasks_create", new
        {
            title = "Эпик фичи",
            projectId,
            role = "feature",
        });
        var epicId = JsonSerializer.Deserialize<JsonElement>(epicText).GetProperty("id").GetString()!;

        // Два шага той же фичи: featureId + role=step + stepRef + lane
        var (step1Text, _) = await CallToolAsync(sessionId, "tasks_create", new
        {
            title = "Шаг D1c",
            projectId,
            featureId = epicId,
            role = "step",
            stepRef = "D1c",
            lane = "бэк",
        });
        var step1Id = JsonSerializer.Deserialize<JsonElement>(step1Text).GetProperty("id").GetString()!;
        var (step2Text, _) = await CallToolAsync(sessionId, "tasks_create", new
        {
            title = "Шаг D2c",
            projectId,
            featureId = epicId,
            role = "step",
            stepRef = "D2c",
            lane = "фронт",
        });
        var step2Id = JsonSerializer.Deserialize<JsonElement>(step2Text).GetProperty("id").GetString()!;

        // Ревью, оценивающее оба шага: reviewOf — список id задач той же фичи
        var (reviewText, isError) = await CallToolAsync(sessionId, "tasks_create", new
        {
            title = "Ревью шагов",
            projectId,
            featureId = epicId,
            role = "review",
            reviewOf = new[] { step1Id, step2Id },
        });
        isError.Should().BeFalse();
        var review = JsonSerializer.Deserialize<JsonElement>(reviewText);
        review.GetProperty("featureId").GetString().Should().Be(epicId);
        review.GetProperty("role").GetString().Should().Be("review");
        review.GetProperty("reviewOf").EnumerateArray().Select(x => x.GetString())
            .Should().BeEquivalentTo(step1Id, step2Id);

        // Шаг сохранил разметку: featureId/role/stepRef/lane
        var step1 = JsonSerializer.Deserialize<JsonElement>(step1Text);
        step1.GetProperty("featureId").GetString().Should().Be(epicId);
        step1.GetProperty("role").GetString().Should().Be("step");
        step1.GetProperty("stepRef").GetString().Should().Be("D1c");
        step1.GetProperty("lane").GetString().Should().Be("бэк");
    }

    [Fact]
    public async Task Update_ФичаПоля_Меняются()
    {
        var (projectId, sessionId) = await CreateProjectWithSessionAsync();
        var (epicText, _) = await CallToolAsync(sessionId, "tasks_create", new
        {
            title = "Эпик",
            projectId,
            role = "feature",
        });
        var epicId = JsonSerializer.Deserialize<JsonElement>(epicText).GetProperty("id").GetString()!;
        var (stepText, _) = await CallToolAsync(sessionId, "tasks_create", new
        {
            title = "Шаг",
            projectId,
            featureId = epicId,
            role = "step",
            stepRef = "D1c",
            lane = "бэк",
        });
        var stepId = JsonSerializer.Deserialize<JsonElement>(stepText).GetProperty("id").GetString()!;

        // Смена lane/stepRef: поля фичи меняются, featureId/role остаются
        var (text, isError) = await CallToolAsync(sessionId, "tasks_update", new
        {
            id = stepId,
            lane = "фронт",
            stepRef = "D2c",
        });
        isError.Should().BeFalse();
        var updated = JsonSerializer.Deserialize<JsonElement>(text);
        updated.GetProperty("lane").GetString().Should().Be("фронт");
        updated.GetProperty("stepRef").GetString().Should().Be("D2c");
        updated.GetProperty("featureId").GetString().Should().Be(epicId);
        updated.GetProperty("role").GetString().Should().Be("step");
    }

    // ─── Невалидный role (не из TaskRoles.All) → Deny ─────────────────────────

    [Fact]
    public async Task Create_НевалиднаяРоль_Deny()
    {
        var (projectId, sessionId) = await CreateProjectWithSessionAsync();

        var (text, isError) = await CallToolAsync(sessionId, "tasks_create", new
        {
            title = "Задача с мусорной ролью",
            projectId,
            role = "bogus",
        });

        isError.Should().BeTrue("role вне списка TaskRoles — fail-closed, агент обязан узнать");
        text.Should().Contain("Недопустимая роль фичи").And.Contain("bogus");
    }

    // ─── Невалидный verdict (не из TaskVerdicts.All) → Deny ───────────────────

    [Fact]
    public async Task Complete_НевалидныйВердикт_Deny()
    {
        var (projectId, sessionId) = await CreateProjectWithSessionAsync();
        var (createText, _) = await CallToolAsync(sessionId, "tasks_create", new
        {
            title = "Задача на вердикт",
            projectId,
        });
        var taskId = JsonSerializer.Deserialize<JsonElement>(createText).GetProperty("id").GetString()!;

        var (text, isError) = await CallToolAsync(sessionId, "tasks_complete", new
        {
            id = taskId,
            verification = new { verdict = "bogus" },
        });

        isError.Should().BeTrue("verdict вне списка TaskVerdicts — fail-closed");
        text.Should().Contain("Недопустимый вердикт").And.Contain("bogus");
    }

    // ─── Ревью фичи без вердикта → мягкое предупреждение в ответе tasks_complete ──

    [Fact]
    public async Task Complete_РевьюФичиБезВердикта_Предупреждение()
    {
        var (projectId, sessionId) = await CreateProjectWithSessionAsync();
        var (epicText, _) = await CallToolAsync(sessionId, "tasks_create", new
        {
            title = "Эпик",
            projectId,
            role = "feature",
        });
        var epicId = JsonSerializer.Deserialize<JsonElement>(epicText).GetProperty("id").GetString()!;
        var (reviewText, _) = await CallToolAsync(sessionId, "tasks_create", new
        {
            title = "Ревью фичи",
            projectId,
            featureId = epicId,
            role = "review",
        });
        var reviewId = JsonSerializer.Deserialize<JsonElement>(reviewText).GetProperty("id").GetString()!;

        // Закрытие без verdict: карточка его не покажет — мягкое предупреждение, а не отказ
        var (text, isError) = await CallToolAsync(sessionId, "tasks_complete", new { id = reviewId });
        isError.Should().BeFalse("закрытие ради UI не блокируем — только предупреждение");
        var done = JsonSerializer.Deserialize<JsonElement>(text);
        done.GetProperty("status").GetString().Should().Be("done");
        done.TryGetProperty("warning", out var warning).Should().BeTrue(
            "у ревью фичи без вердикта обязан быть отдельный warning (не notes)");
        warning.GetString().Should().Contain("вердикт");
    }

    // ─── Гейт EnsureNotClosedAtCreate (tasks_create) ─────────────────────────

    [Fact]
    public async Task Create_ДефектВDoneКолонку_Deny()
    {
        var (projectId, sessionId) = await CreateProjectWithSessionAsync();
        await SetBoardAsync(projectId, new
        {
            columns = new[] { new { id = "done-col", name = "Done", category = "done" } },
        });

        var (text, isError) = await CallToolAsync(sessionId, "tasks_create", new
        {
            title = "Дефект",
            projectId,
            kind = "defect",
            columnId = "done-col",
        });

        isError.Should().BeTrue();
        text.Should().Contain("нельзя создавать сразу в Done");
    }

    // ─── Гейт EnsureReproOnReview (tasks_create) ────────────────────────────

    [Fact]
    public async Task Create_ДефектВReviewБезRepro_Deny()
    {
        var (projectId, sessionId) = await CreateProjectWithSessionAsync();
        // Кастомная колонка с Role="review" — гейт EnsureReproOnReview смотрит именно в Role
        await SetBoardAsync(projectId, new
        {
            columns = new[]
            {
                new { id = "review-col", name = "На согласовании", category = "inProgress", role = "review" },
            },
        });

        var (text, isError) = await CallToolAsync(sessionId, "tasks_create", new
        {
            title = "Дефект в ревью без шагов",
            projectId,
            kind = "defect",
            columnId = "review-col",
        });

        isError.Should().BeTrue();
        text.Should().Contain("Repro.Steps");
    }

    [Fact]
    public async Task Create_ДефектВReviewСШагами_Создаётся()
    {
        var (projectId, sessionId) = await CreateProjectWithSessionAsync();
        await SetBoardAsync(projectId, new
        {
            columns = new[]
            {
                new { id = "review-col", name = "На согласовании", category = "inProgress", role = "review" },
            },
        });

        var (text, isError) = await CallToolAsync(sessionId, "tasks_create", new
        {
            title = "Дефект в ревью с шагами",
            projectId,
            kind = "defect",
            columnId = "review-col",
            repro = new { steps = "1. Открыть X\n2. Нажать Y" },
        });

        isError.Should().BeFalse();
        var created = JsonSerializer.Deserialize<JsonElement>(text);
        created.GetProperty("kind").GetString().Should().Be("defect");
        created.GetProperty("status").GetString().Should().Be("inProgress");
        created.GetProperty("repro").GetProperty("steps").GetString()
            .Should().Contain("Открыть X");
    }

    // ─── Гейт EnsureReproOnReview (tasks_update: перевод в review-колонку) ──

    [Fact]
    public async Task Update_ДефектПереводВReviewБезRepro_Deny()
    {
        var (projectId, sessionId) = await CreateProjectWithSessionAsync();
        await SetBoardAsync(projectId, new
        {
            columns = new[]
            {
                new { id = "review-col", name = "На согласовании", category = "inProgress", role = "review" },
            },
        });
        var (createText, _) = await CallToolAsync(sessionId, "tasks_create", new
        {
            title = "Дефект",
            projectId,
            kind = "defect",
        });
        var taskId = JsonSerializer.Deserialize<JsonElement>(createText).GetProperty("id").GetString()!;

        var (text, isError) = await CallToolAsync(sessionId, "tasks_update", new
        {
            id = taskId,
            columnId = "review-col",
        });

        isError.Should().BeTrue();
        text.Should().Contain("Repro.Steps");
    }

    // ─── Гейт EnsureVerificationOnClose (tasks_update: перевод в done) ──────

    [Fact]
    public async Task Update_ДефектВDoneБезВердикта_Deny()
    {
        var (projectId, sessionId) = await CreateProjectWithSessionAsync();
        var (createText, _) = await CallToolAsync(sessionId, "tasks_create", new
        {
            title = "Дефект",
            projectId,
            kind = "defect",
        });
        var taskId = JsonSerializer.Deserialize<JsonElement>(createText).GetProperty("id").GetString()!;

        var (text, isError) = await CallToolAsync(sessionId, "tasks_update", new
        {
            id = taskId,
            status = "done",
        });

        isError.Should().BeTrue();
        text.Should().Contain("Verification").And.Contain("ClosedWithoutCheck");
    }

    [Fact]
    public async Task Update_ДефектВDoneСOutcome_ЗакрываетсяБезВердикта()
    {
        var (projectId, sessionId) = await CreateProjectWithSessionAsync();
        var (createText, _) = await CallToolAsync(sessionId, "tasks_create", new
        {
            title = "Дефект",
            projectId,
            kind = "defect",
        });
        var taskId = JsonSerializer.Deserialize<JsonElement>(createText).GetProperty("id").GetString()!;

        var (text, isError) = await CallToolAsync(sessionId, "tasks_update", new
        {
            id = taskId,
            status = "done",
            outcome = "closedWithoutCheck",
        });

        isError.Should().BeFalse();
        var updated = JsonSerializer.Deserialize<JsonElement>(text);
        updated.GetProperty("status").GetString().Should().Be("done");
        updated.GetProperty("outcome").GetString().Should().Be("closedWithoutCheck");
    }

    [Fact]
    public async Task Update_VerificationПодставляетPersonaIdИзСессии()
    {
        // Хвост-сессия владельца токена без персоны → VerifiedAt подставлен, PersonaId == null
        // (заголовок X-Caller-Session-Id подделываем; PersonaId выводится из САМОЙ сессии)
        var (projectId, sessionId) = await CreateProjectWithSessionAsync();
        var (createText, _) = await CallToolAsync(sessionId, "tasks_create", new
        {
            title = "Дефект на проверку",
            projectId,
            kind = "defect",
        });
        var taskId = JsonSerializer.Deserialize<JsonElement>(createText).GetProperty("id").GetString()!;

        var (text, isError) = await CallToolAsync(sessionId, "tasks_update", new
        {
            id = taskId,
            status = "done",
            verification = new { notes = "Проверил" },
        });

        isError.Should().BeFalse();
        var verification = JsonSerializer.Deserialize<JsonElement>(text).GetProperty("verification");
        verification.GetProperty("notes").GetString().Should().Be("Проверил");
        // PersonaId подставлен из сессии: CreateSessionRequest без personaId спавнит
        // дефолтного ассистента (provisioner.EnsureAsync), поэтому PersonaId не null.
        verification.TryGetProperty("personaId", out var p).Should().BeTrue();
        p.ValueKind.Should().NotBe(JsonValueKind.Null);
        // VerifiedAt — реальная отметка времени, не дефолт
        verification.GetProperty("verifiedAt").GetDateTime()
            .Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
    }

    // ─── Находка 1: смена kind снимает гейты (ревью Глеба) ────────────────

    [Fact]
    public async Task Update_ДефектСоСменойKindНаTaskИDone_Deny()
    {
        // Сценарий из находки 1: персона шлёт kind=Task+status=done на дефекте — раньше
        // effective.Kind=Task снимал гейт EnsureVerificationOnClose, карточка закрывалась
        // без вердикта. Вид immutable после создания: req.Kind != task.Kind молча игнорируется,
        // вид остаётся Defect, гейт EnsureVerificationOnClose срабатывает на закрытие без
        // Verification. Та же Deny, но текст от EnsureVerificationOnClose.
        var (projectId, sessionId) = await CreateProjectWithSessionAsync();
        var (createText, _) = await CallToolAsync(sessionId, "tasks_create", new
        {
            title = "Дефект",
            projectId,
            kind = "defect",
        });
        var taskId = JsonSerializer.Deserialize<JsonElement>(createText).GetProperty("id").GetString()!;

        var (text, isError) = await CallToolAsync(sessionId, "tasks_update", new
        {
            id = taskId,
            kind = "task",
            status = "done",
        });

        isError.Should().BeTrue();
        text.Should().Contain("Verification");
        // Карточка осталась дефектом и не закрылась
        var (getText, _) = await CallToolAsync(sessionId, "tasks_get", new { id = taskId });
        var task = JsonSerializer.Deserialize<JsonElement>(getText);
        task.GetProperty("kind").GetString().Should().Be("defect");
        task.GetProperty("status").GetString().Should().Be("todo");
    }

    [Fact]
    public async Task Update_ДефектСоСменойKindНаTaskБезStatus_Проходит()
    {
        // Вид immutable после создания: kind=task игнорируется, без status изменения карточка
        // остаётся дефектом в исходном статусе. Легитимный сценарий UI-формы редактирования.
        var (projectId, sessionId) = await CreateProjectWithSessionAsync();
        var (createText, _) = await CallToolAsync(sessionId, "tasks_create", new
        {
            title = "Дефект",
            projectId,
            kind = "defect",
        });
        var taskId = JsonSerializer.Deserialize<JsonElement>(createText).GetProperty("id").GetString()!;

        var (text, isError) = await CallToolAsync(sessionId, "tasks_update", new
        {
            id = taskId,
            kind = "task",
        });

        isError.Should().BeFalse();
        var updated = JsonSerializer.Deserialize<JsonElement>(text);
        updated.GetProperty("kind").GetString().Should().Be("defect");
    }

    // ─── Находка 2: repro:{} в review-колонке (ревью Глеба) ────────────────

    [Fact]
    public async Task Update_ДефектСтираетReproБезColumnId_НоВReview_Deny()
    {
        // Сценарий из находки 2: дефект уже стоит в review-колонке, персона шлёт
        // tasks_update {repro: {}} без columnId — раньше targetIsReview не резолвился,
        // гейт EnsureReproOnReview не срабатывал, шаги стирались. Теперь effectiveColumn
        // = текущая колонка карточки, Role=review, гейт срабатывает.
        var (projectId, sessionId) = await CreateProjectWithSessionAsync();
        await SetBoardAsync(projectId, new
        {
            columns = new[]
            {
                new { id = "review-col", name = "На согласовании", category = "inProgress", role = "review" },
            },
        });
        // Создаём дефект уже в review-колонке, с шагами — гейт Create пропускает
        var (createText, _) = await CallToolAsync(sessionId, "tasks_create", new
        {
            title = "Дефект в ревью",
            projectId,
            kind = "defect",
            columnId = "review-col",
            repro = new { steps = "1. Шаг" },
        });
        var taskId = JsonSerializer.Deserialize<JsonElement>(createText).GetProperty("id").GetString()!;

        // Стираем repro без columnId — карточка остаётся в review-колонке.
        var (text, isError) = await CallToolAsync(sessionId, "tasks_update", new
        {
            id = taskId,
            repro = new { },
        });

        isError.Should().BeTrue();
        text.Should().Contain("Repro.Steps");
    }

    // ─── Находка 3: Verification без Notes (ревью Глеба, паспорт 432b6de2) ──

    [Fact]
    public async Task Update_ДефектСVerificationБезNotes_Deny()
    {
        // Сценарий из находки 3: tasks_update {verification: {}} — раньше null Notes
        // считался содержательным, карточка закрывалась без единого слова о проверке.
        // Теперь null/whitespace Notes — не вердикт (паспорт 432b6de2).
        var (projectId, sessionId) = await CreateProjectWithSessionAsync();
        var (createText, _) = await CallToolAsync(sessionId, "tasks_create", new
        {
            title = "Дефект на закрытие",
            projectId,
            kind = "defect",
        });
        var taskId = JsonSerializer.Deserialize<JsonElement>(createText).GetProperty("id").GetString()!;

        var (text, isError) = await CallToolAsync(sessionId, "tasks_update", new
        {
            id = taskId,
            status = "done",
            verification = new { },
        });

        isError.Should().BeTrue();
        text.Should().Contain("Verification");
    }

    [Fact]
    public async Task Update_ДефектСVerificationСПробельнымNotes_Deny()
    {
        // Тот же инвариант: пробельный Notes — не вердикт.
        var (projectId, sessionId) = await CreateProjectWithSessionAsync();
        var (createText, _) = await CallToolAsync(sessionId, "tasks_create", new
        {
            title = "Дефект на закрытие",
            projectId,
            kind = "defect",
        });
        var taskId = JsonSerializer.Deserialize<JsonElement>(createText).GetProperty("id").GetString()!;

        var (text, isError) = await CallToolAsync(sessionId, "tasks_update", new
        {
            id = taskId,
            status = "done",
            verification = new { notes = "   " },
        });

        isError.Should().BeTrue();
        text.Should().Contain("Verification");
    }

    // ─── Outcome только для Defect (паспорт 432b6de2, Д-3) ─────────────────

    [Fact]
    public async Task Update_OutcomeДляОбычнойЗадачи_Игнорируется()
    {
        var (projectId, sessionId) = await CreateProjectWithSessionAsync();
        var (createText, _) = await CallToolAsync(sessionId, "tasks_create", new
        {
            title = "Обычная задача",
            projectId,
        });
        var taskId = JsonSerializer.Deserialize<JsonElement>(createText).GetProperty("id").GetString()!;

        var (text, isError) = await CallToolAsync(sessionId, "tasks_update", new
        {
            id = taskId,
            status = "done",
            outcome = "closedWithoutCheck", // обычная задача — поле не имеет смысла
        });

        isError.Should().BeFalse();
        var updated = JsonSerializer.Deserialize<JsonElement>(text);
        updated.GetProperty("status").GetString().Should().Be("done");
        updated.TryGetProperty("outcome", out var o).Should().BeTrue();
        o.ValueKind.Should().Be(JsonValueKind.Null);
    }

    // ─── tasks_complete с verification/outcome ──────────────────────────────

    [Fact]
    public async Task Complete_ДефектСVerification_Закрывается()
    {
        var (projectId, sessionId) = await CreateProjectWithSessionAsync();
        var (createText, _) = await CallToolAsync(sessionId, "tasks_create", new
        {
            title = "Дефект",
            projectId,
            kind = "defect",
        });
        var taskId = JsonSerializer.Deserialize<JsonElement>(createText).GetProperty("id").GetString()!;

        var (text, isError) = await CallToolAsync(sessionId, "tasks_complete", new
        {
            id = taskId,
            verification = new { notes = "ок" },
        });

        isError.Should().BeFalse();
        var updated = JsonSerializer.Deserialize<JsonElement>(text);
        updated.GetProperty("status").GetString().Should().Be("done");
        updated.GetProperty("verification").GetProperty("notes").GetString().Should().Be("ок");
    }

    [Fact]
    public async Task Complete_ДефектБезVerificationИБезOutcome_Deny()
    {
        var (projectId, sessionId) = await CreateProjectWithSessionAsync();
        var (createText, _) = await CallToolAsync(sessionId, "tasks_create", new
        {
            title = "Дефект",
            projectId,
            kind = "defect",
        });
        var taskId = JsonSerializer.Deserialize<JsonElement>(createText).GetProperty("id").GetString()!;

        var (text, isError) = await CallToolAsync(sessionId, "tasks_complete", new
        {
            id = taskId,
        });

        isError.Should().BeTrue();
        text.Should().Contain("Verification");
    }

    [Fact]
    public async Task Complete_ДефектСOutcome_ЗакрываетсяБезВердикта()
    {
        var (projectId, sessionId) = await CreateProjectWithSessionAsync();
        var (createText, _) = await CallToolAsync(sessionId, "tasks_create", new
        {
            title = "Дефект",
            projectId,
            kind = "defect",
        });
        var taskId = JsonSerializer.Deserialize<JsonElement>(createText).GetProperty("id").GetString()!;

        var (text, isError) = await CallToolAsync(sessionId, "tasks_complete", new
        {
            id = taskId,
            outcome = "closedWithoutCheck",
        });

        isError.Should().BeFalse();
        var updated = JsonSerializer.Deserialize<JsonElement>(text);
        updated.GetProperty("status").GetString().Should().Be("done");
        updated.GetProperty("outcome").GetString().Should().Be("closedWithoutCheck");
    }

    // ─── Brief: kind и hasVerification в списке ────────────────────────────

    [Fact]
    public async Task List_BriefСодержитKindИHasVerification_ДляДефекта()
    {
        var (projectId, sessionId) = await CreateProjectWithSessionAsync();
        var (createText, _) = await CallToolAsync(sessionId, "tasks_create", new
        {
            title = "Дефект для списка",
            projectId,
            kind = "defect",
        });
        JsonNode.Parse(createText);

        var (text, isError) = await CallToolAsync(sessionId, "tasks_list", new { projectId });
        isError.Should().BeFalse();
        var items = JsonSerializer.Deserialize<JsonElement>(text);
        var defect = items.EnumerateArray().First(x => x.GetProperty("title").GetString() == "Дефект для списка");
        defect.GetProperty("kind").GetString().Should().Be("defect");
        defect.GetProperty("hasVerification").GetBoolean().Should().BeFalse();
        defect.GetProperty("status").GetString().Should().Be("todo");
    }

    [Fact]
    public async Task List_BriefСодержитKind_ДляОбычнойЗадачи()
    {
        var (projectId, sessionId) = await CreateProjectWithSessionAsync();
        var (createText, _) = await CallToolAsync(sessionId, "tasks_create", new
        {
            title = "Обычная для списка",
            projectId,
        });
        JsonNode.Parse(createText);

        var (text, isError) = await CallToolAsync(sessionId, "tasks_list", new { projectId });
        isError.Should().BeFalse();
        var items = JsonSerializer.Deserialize<JsonElement>(text);
        var task = items.EnumerateArray().First(x => x.GetProperty("title").GetString() == "Обычная для списка");
        task.GetProperty("kind").GetString().Should().Be("task");
    }
}
