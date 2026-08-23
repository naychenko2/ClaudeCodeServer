using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Controllers;

// КР-наблюдаемость, этап 3: REST-поверхность перезапуска — POST /api/chats/{id}/team-wave/
// tasks/{taskId}/restart, /restart, /restart-turn. Состояние готовим серверными сервисами
// фабрики (как TeamWaveSnapshotApiTests), REST проверяет контракты ответов, гейты и
// изоляцию по владельцу. Механику (перевыдача, kill+resume) гоняют юнит-тесты сервисов.
public class TeamWaveRestartApiTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public TeamWaveRestartApiTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateAuthenticatedClient();
        _factory.LlmAdapters.Reset();
    }

    // Штаб в стадии «волна» с розданной задачей и (опционально) занятым ходом штаба.
    // busy=true — чат-штаб завис в Working без живого прогона (главный сценарий этапа 3).
    // План публикуем карточкой в историю напрямую (планировщик — LLM, в API-тестах не ходит):
    // перевыдача читает план именно оттуда (GetTeamPlanAsync).
    private async Task<(string SessionId, string TaskId)> MakeWaveAsync(bool busy = false)
    {
        var users = _factory.Services.GetRequiredService<UserStore>();
        var ownerId = users.FindByUsername(TestWebApplicationFactory.TestUsername)!.Id;
        var projects = _factory.Services.GetRequiredService<ProjectManager>();
        var personas = _factory.Services.GetRequiredService<PersonaManager>();
        var sessions = _factory.Services.GetRequiredService<SessionManager>();
        var tasks = _factory.Services.GetRequiredService<TaskManager>();
        var history = _factory.Services.GetRequiredService<ChatHistoryService>();

        var dir = Path.Combine(_factory.TempDir, "proj_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var project = projects.Create("restart-" + Guid.NewGuid().ToString("N")[..6], dir, ownerId,
            TestWebApplicationFactory.TestUsername);
        var coordinator = personas.Create(ownerId, "Алекс", "Тимлид", null, null, null, null,
            PersonaScope.Project, project.Id, null, null, memoryEnabled: false);

        var session = await sessions.CreateAsync(project.Id, ClaudeMode.Auto, personaId: coordinator.Id);
        await sessions.SetTeamImplementAsync(session.Id, enabled: true,
            coordinatorPersonaId: coordinator.Id, userId: ownerId);
        var info = sessions.GetById(session.Id)!;
        info.ClaudeSessionId = "apicsid" + Guid.NewGuid().ToString("N")[..12];
        if (busy) info.Status = SessionStatus.Working;

        var task = tasks.Create(project.Id, ownerId, new CreateTaskRequest(
            Title: "Эндпоинт экспорта",
            SourceSessionId: session.Id,
            Labels: ["Командная реализация", "волна 1"]));
        var plan = new TeamImplementPlan
        {
            Request = "Экспорт",
            Summary = "Экспорт",
            Approved = true,
            Subtasks =
            [
                new TeamImplementSubtask
                {
                    Title = "Эндпоинт экспорта",
                    Goal = "GET /api/tasks/export",
                    ExecutorPersonaId = coordinator.Id,
                    Wave = 1,
                    DoneCriteria = "отдаёт CSV",
                    TaskId = task.Id,
                    Attempts = 1,
                },
            ],
        };
        await history.SaveAsync(info.ClaudeSessionId,
        [
            new ClaudeHomeServer.Protocol.StoredTeamPlanMessage
            {
                PlanId = plan.Id, Plan = plan, Resolved = true, Approved = true,
                PersonaId = coordinator.Id,
            },
        ]);
        // Форма «после рестарта сервера»: план живёт на диске, аккумулятора нет — перевыдача
        // обязана работать и по этому пути (GetTeamPlanAsync читает историю с диска).
        // Аккумулятор обнуляем рефлексией: живой (пустой в памяти) не видит прямой записи.
        var entries = (System.Collections.IDictionary)typeof(SessionManager)
            .GetField("_sessions", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(sessions)!;
        entries[session.Id]!.GetType().GetField("Accumulator")!
            .SetValue(entries[session.Id], null);
        sessions.WithTeamState(session.Id, t =>
        {
            t.Stage = TeamImplementStage.Wave;
            t.WaveNumber = 1;
            t.PlannedWaves = 2;
            t.PlanCardId = plan.Id;
            t.PlanVersion = 1;
            t.ApprovedPlanVersion = 1;
            t.WaveStartedAt = DateTime.UtcNow.AddMinutes(-45);
            t.WaveActivityAt = DateTime.UtcNow.AddMinutes(-45);
            return true;
        });
        // Волна молчит дольше StalledMinutes — форма зависшей, кнопка перезапуска активна
        tasks.GetById(task.Id)!.UpdatedAt = DateTime.UtcNow.AddMinutes(-45);
        return (session.Id, task.Id);
    }

    private static HttpContent Body(object payload) =>
        JsonContent.Create(payload);

    // --- Перезапуск задачи ---

    [Fact]
    public async Task ПерезапускЗадачи_ЧужойВладелец_404()
    {
        var (sessionId, taskId) = await MakeWaveAsync();
        using var stranger = _factory.CreateAuthenticatedClient(
            TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);

        var response = await stranger.PostAsync(
            $"/api/chats/{sessionId}/team-wave/tasks/{taskId}/restart", Body(new { }));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "чужой чат неотличим от несуществующего — данных через 404 не течёт");
    }

    [Fact]
    public async Task ПерезапускЗадачи_Завершённая_409СТекстом()
    {
        var (sessionId, taskId) = await MakeWaveAsync();
        var tasks = _factory.Services.GetRequiredService<TaskManager>();
        tasks.Update(taskId, new UpdateTaskRequest(Status: TaskItemStatus.Done));

        var response = await _client.PostAsync(
            $"/api/chats/{sessionId}/team-wave/tasks/{taskId}/restart", Body(new { }));

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        body.GetProperty("error").GetString().Should().Contain("уже завершена",
            "отказ — человеческим текстом, а не пустой кнопкой");
    }

    [Fact]
    public async Task ПерезапускЗадачи_Зависшая_ПеревыдачаОтвечаетКонтрактом()
    {
        var (sessionId, taskId) = await MakeWaveAsync();

        var response = await _client.PostAsync(
            $"/api/chats/{sessionId}/team-wave/tasks/{taskId}/restart", Body(new { }));

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            await response.Content.ReadAsStringAsync());
        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        body.GetProperty("outcome").GetString().Should().Be("reissued");
        body.GetProperty("message").GetString().Should().Contain("перевыдана");
        var tasks = _factory.Services.GetRequiredService<TaskManager>();
        tasks.GetById(taskId)!.Description.Should().Contain("Повторная попытка");
    }

    // --- Перезапуск волны ---

    [Fact]
    public async Task ПерезапускВолны_ЖиваяВолнаБезПодтверждения_ТребуетПодтверждения()
    {
        var (sessionId, taskId) = await MakeWaveAsync();
        var tasks = _factory.Services.GetRequiredService<TaskManager>();
        tasks.GetById(taskId)!.UpdatedAt = DateTime.UtcNow; // волна жива

        var response = await _client.PostAsync(
            $"/api/chats/{sessionId}/team-wave/restart", Body(new { confirm = false }));

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            await response.Content.ReadAsStringAsync());
        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        body.GetProperty("requiresConfirm").GetBoolean().Should().BeTrue();
        tasks.GetById(taskId)!.Description.Should().NotContain("Повторная попытка",
            "без подтверждения ничего не раздаётся");
    }

    [Fact]
    public async Task ПерезапускВолны_Зависшая_ПеревыдаётНезакрытое()
    {
        var (sessionId, taskId) = await MakeWaveAsync();

        var response = await _client.PostAsync(
            $"/api/chats/{sessionId}/team-wave/restart", Body(new { confirm = false }));

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            await response.Content.ReadAsStringAsync());
        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        body.GetProperty("requiresConfirm").GetBoolean().Should().BeFalse();
        body.GetProperty("reissued").GetInt32().Should().Be(1);
    }

    // --- Перезапуск хода штаба ---

    [Fact]
    public async Task ПерезапускХода_ЗависшийШтаб_ВозвращаетЧатВРаботуИДоставляетОчередь()
    {
        var (sessionId, _) = await MakeWaveAsync(busy: true);
        var sessions = _factory.Services.GetRequiredService<SessionManager>();
        // Сообщение застряло в очереди зависшего хода: написать в чат нельзя
        await sessions.SendOrEnqueueAsync(sessionId, "продолжай волну");

        var response = await _client.PostAsync(
            $"/api/chats/{sessionId}/team-wave/restart-turn", Body(new { }));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        body.GetProperty("outcome").GetString().Should().Be("restarted");
        body.GetProperty("resumed").GetBoolean().Should().BeTrue();
        body.GetProperty("message").GetString().Should().Contain("с того же места",
            "в очереди стоит сообщение — продолжение действительно будет");

        // Отложенное сообщение уходит первым ходом нового процесса (фабрика адаптеров —
        // фейк: SentMessages фиксирует доставку без запуска claude)
        var adapter = _factory.LlmAdapters.Adapters[sessionId];
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        string? sent = null;
        while (DateTime.UtcNow < deadline)
        {
            lock (adapter.SentMessages)
                if (adapter.SentMessages.Count > 0) { sent = adapter.SentMessages[0]; break; }
            await Task.Delay(50);
        }
        sent.Should().Contain("продолжай волну",
            "новый ход с --resume доставил застрявшее сообщение — это и есть «работа не потеряна»");
        sessions.GetById(sessionId)!.Status.Should().BeOneOf(
            SessionStatus.Working, SessionStatus.Active, SessionStatus.Finished);
        sessions.GetById(sessionId)!.TeamImplement!.Stage.Should().Be(TeamImplementStage.Wave,
            "режим и стадия пережили перезапуск");
    }

    [Fact]
    public async Task ПерезапускХода_СвободныйЧат_409СТекстом()
    {
        var (sessionId, _) = await MakeWaveAsync(busy: false);

        var response = await _client.PostAsync(
            $"/api/chats/{sessionId}/team-wave/restart-turn", Body(new { }));

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        body.GetProperty("error").GetString().Should().Contain("не занят");
    }

    // Minor 4 ревью: кнопка и тексты — про штаб, вне «Командной реализации»
    // перезапуск хода не работает (обобщение на все чаты — отдельное решение)
    [Fact]
    public async Task ПерезапускХода_ВнеРежимаКР_409СТекстом()
    {
        var users = _factory.Services.GetRequiredService<UserStore>();
        var ownerId = users.FindByUsername(TestWebApplicationFactory.TestUsername)!.Id;
        var projects = _factory.Services.GetRequiredService<ProjectManager>();
        var sessions = _factory.Services.GetRequiredService<SessionManager>();
        var dir = Path.Combine(_factory.TempDir, "proj_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var project = projects.Create("plain-" + Guid.NewGuid().ToString("N")[..6], dir, ownerId,
            TestWebApplicationFactory.TestUsername);
        var session = await sessions.CreateAsync(project.Id, ClaudeMode.Auto);
        sessions.GetById(session.Id)!.Status = SessionStatus.Working;

        var response = await _client.PostAsync(
            $"/api/chats/{session.Id}/team-wave/restart-turn", Body(new { }));

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        body.GetProperty("error").GetString().Should().Contain("штаба");
    }

    // Minor 3 ревью: очередь пуста — нового хода не будет, текст не обещает продолжения
    [Fact]
    public async Task ПерезапускХода_ПустаяОчередь_ТекстГоворитПоФакту()
    {
        var (sessionId, _) = await MakeWaveAsync(busy: true);

        var response = await _client.PostAsync(
            $"/api/chats/{sessionId}/team-wave/restart-turn", Body(new { }));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        var message = body.GetProperty("message").GetString()!;
        message.Should().Contain("напишите сообщение",
            "очередь пуста — ход не начнётся сам, человек должен знать об этом");
        message.Should().NotContain("с того же места",
            "обещание продолжения без хода — враньё");
    }

    [Fact]
    public async Task ПерезапускХода_ЧужойВладелец_404()
    {
        var (sessionId, _) = await MakeWaveAsync(busy: true);
        using var stranger = _factory.CreateAuthenticatedClient(
            TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);

        var response = await stranger.PostAsync(
            $"/api/chats/{sessionId}/team-wave/restart-turn", Body(new { }));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ПерезапускХода_ПовреждённыйТранскрипт_409СКодомИНачалоЗаново()
    {
        var (sessionId, _) = await MakeWaveAsync(busy: true);
        var sessions = _factory.Services.GetRequiredService<SessionManager>();
        const string csid = "apidmg0123456789";
        sessions.GetById(sessionId)!.ClaudeSessionId = csid;
        // Транскрипт в регистрируемом корне проб: последняя строка оборвана
        var projDir = Path.Combine(_factory.TempDir, "tr-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(projDir);
        await File.WriteAllTextAsync(Path.Combine(projDir, csid + ".jsonl"),
            "{\"type\":\"user\"}\n{\"type\":\"assistant\",\"mess");
        ClaudeHomeServer.Services.WorkflowAgentParser.AddAllowedRoot(_factory.TempDir);

        var refused = await _client.PostAsync(
            $"/api/chats/{sessionId}/team-wave/restart-turn", Body(new { }));

        refused.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var refusal = JsonSerializer.Deserialize<JsonElement>(await refused.Content.ReadAsStringAsync());
        refusal.GetProperty("code").GetString().Should().Be("transcript_damaged",
            "фронт по коду показывает «начать ход заново» вместо пустой ошибки");
        sessions.GetById(sessionId)!.Status.Should().Be(SessionStatus.Working);

        // Major ревью: пока 409 идёт к человеку, финализация убитого прогона переводит
        // чат в Active — повторный startFresh обязан проходить и из этого состояния
        sessions.GetById(sessionId)!.Status = SessionStatus.Active;
        var fresh = await _client.PostAsync(
            $"/api/chats/{sessionId}/team-wave/restart-turn", Body(new { startFresh = true }));

        fresh.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonSerializer.Deserialize<JsonElement>(await fresh.Content.ReadAsStringAsync());
        body.GetProperty("outcome").GetString().Should().Be("fresh");
        sessions.GetById(sessionId)!.ClaudeSessionId.Should().BeNull(
            "ход начнётся заново — без --resume по повреждённому файлу");
    }
}
