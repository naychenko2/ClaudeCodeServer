using System.Net;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Controllers;

// REST-снапшот волны «Командной реализации» (КР-наблюдаемость, этап 1): GET
// /api/chats/{id}/team-wave-snapshot — поповер бейджа «КР · волна N». Состояние волны
// готовим серверными сервисами фабрики (план и раздача — не предмет этих тестов),
// REST проверяет контракт ответа и изоляцию по владельцу.
public class TeamWaveSnapshotApiTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public TeamWaveSnapshotApiTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateAuthenticatedClient();
    }

    // Штаб с розданной волной 1 (задача через SourceSessionId + лейбл, как их создаёт
    // TeamWaveService) и состоянием WaveNumber/PlannedWaves. Возвращает id сессии и задачи.
    private async Task<(string SessionId, string TaskId)> MakeWaveAsync()
    {
        var users = _factory.Services.GetRequiredService<UserStore>();
        var ownerId = users.FindByUsername(TestWebApplicationFactory.TestUsername)!.Id;
        var projects = _factory.Services.GetRequiredService<ProjectManager>();
        var personas = _factory.Services.GetRequiredService<PersonaManager>();
        var sessions = _factory.Services.GetRequiredService<SessionManager>();
        var tasks = _factory.Services.GetRequiredService<TaskManager>();

        var dir = Path.Combine(_factory.TempDir, "proj_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var project = projects.Create("pulse-" + Guid.NewGuid().ToString("N")[..6], dir, ownerId,
            TestWebApplicationFactory.TestUsername);
        var coordinator = personas.Create(ownerId, "Алекс", "Тимлид", null, null, null, null,
            PersonaScope.Project, project.Id, null, null, memoryEnabled: false);

        var session = await sessions.CreateAsync(project.Id, ClaudeMode.Auto, personaId: coordinator.Id);
        await sessions.SetTeamImplementAsync(session.Id, enabled: true,
            coordinatorPersonaId: coordinator.Id, userId: ownerId);
        sessions.WithTeamState(session.Id, t =>
        {
            t.Stage = TeamImplementStage.Wave;
            t.WaveNumber = 1;
            t.PlannedWaves = 2;
            t.WaveStartedAt = DateTime.UtcNow;
            return true;
        });

        var task = tasks.Create(project.Id, ownerId, new CreateTaskRequest(
            Title: "Эндпоинт экспорта",
            SourceSessionId: session.Id,
            Labels: ["Командная реализация", "волна 1"]));
        return (session.Id, task.Id);
    }

    [Fact]
    public async Task Снапшот_ЖиваяВолна_ОтдаётПоляПульсаЗадачиИПороги()
    {
        var (sessionId, taskId) = await MakeWaveAsync();

        var response = await _client.GetAsync($"/api/chats/{sessionId}/team-wave-snapshot");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        body.GetProperty("sessionId").GetString().Should().Be(sessionId);
        body.GetProperty("stage").GetString().Should().Be("wave");
        body.GetProperty("waveNumber").GetInt32().Should().Be(1);
        body.GetProperty("plannedWaves").GetInt32().Should().Be(2);
        body.GetProperty("tasksTotal").GetInt32().Should().Be(1);
        body.GetProperty("tasksActive").GetInt32().Should().Be(1);
        // Задача создана только что — тишины нет
        body.GetProperty("liveness").GetString().Should().Be("alive");
        body.GetProperty("quietSeconds").GetInt64().Should().BeLessThan(60);
        body.GetProperty("lastActivityAt").ValueKind.Should().Be(JsonValueKind.String);
        var thresholds = body.GetProperty("thresholds");
        thresholds.GetProperty("quietMinutes").GetInt32().Should().Be(15);
        thresholds.GetProperty("stalledMinutes").GetInt32().Should().Be(30);
        var task = body.GetProperty("tasks").EnumerateArray().Single();
        task.GetProperty("id").GetString().Should().Be(taskId);
        task.GetProperty("title").GetString().Should().Be("Эндпоинт экспорта");
        task.GetProperty("status").GetString().Should().Be("todo");
        task.GetProperty("updatedAt").ValueKind.Should().Be(JsonValueKind.String);
        // Исполнителя задаче не назначали — поле есть, но пустое
        task.GetProperty("executorPersonaId").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Снапшот_ЧужойВладелец_404()
    {
        var (sessionId, _) = await MakeWaveAsync();
        using var stranger = _factory.CreateAuthenticatedClient(
            TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);

        var response = await stranger.GetAsync($"/api/chats/{sessionId}/team-wave-snapshot");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "чужой чат неотличим от несуществующего — данных через 404 не течёт");
    }

    [Fact]
    public async Task Снапшот_СтадияВнеВолны_404()
    {
        var (sessionId, _) = await MakeWaveAsync();
        var sessions = _factory.Services.GetRequiredService<SessionManager>();
        sessions.WithTeamState(sessionId, t => { t.Stage = TeamImplementStage.Planning; return true; });

        var response = await _client.GetAsync($"/api/chats/{sessionId}/team-wave-snapshot");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "вне Wave/Checking снапшота нет — поповер открывается только у живого бейджа волны");
    }

    [Fact]
    public async Task Снапшот_РежимВыключен_404()
    {
        var (sessionId, _) = await MakeWaveAsync();
        var sessions = _factory.Services.GetRequiredService<SessionManager>();
        var users = _factory.Services.GetRequiredService<UserStore>();
        var ownerId = users.FindByUsername(TestWebApplicationFactory.TestUsername)!.Id;
        await sessions.SetTeamImplementAsync(sessionId, enabled: false, userId: ownerId);

        var response = await _client.GetAsync($"/api/chats/{sessionId}/team-wave-snapshot");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
