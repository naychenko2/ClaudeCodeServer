using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Access;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Tasks;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ClaudeHomeServer.Tests.Controllers;

/// <summary>
/// F-9 (ревью R-F-1, решение по дефекту 0071ef75, вариант «а»): автозапуск исполнителя
/// по сроку наследует актора-СОЗДАТЕЛЯ задачи, а не «неизвестного», которому ActorRightGate
/// отдаёт права арендатора. Источник создателя — TaskItem.CreatedByUserId: у MCP-пути его
/// пишет TasksToolset (актор хода-создателя, GetTurnActor), у REST — UserId из JWT.
/// Прогон идёт через настоящий TaskSchedulerService.TickAsync и настоящий исполнитель.
///
/// • (а) задача создана агентом редактора общего проекта → ход исполнителя идёт с актором-редактором,
///   закрытый ему watch_start отказан;
/// • (б) создана владельцем → актором называется владелец (после О2 финального ревью безымянный
///   актор общего проекта не имел бы ни одного права и не мог бы выполнить свою задачу);
///   поведение то же, что раньше: права арендатора, watch_start открыт;
/// • (в) старая запись без создателя → считается созданной владельцем буквально: его имя;
/// • (г) личный проект/задача без проекта → поведение прежнее (там безымянный актор был бы
///   разрешён), но имя тоже называется явно.
///
/// Мутации (в отчёте задачи): вернуть в TaskSchedulerService передачу «неизвестного» актора
/// (null вместо TaskLaunchActor.For) — тест (а) краснеет; вернуть в TaskLaunchActor null для
/// создателя-владельца (алиаса до О2) — в (б) и (в) краснеют оба утверждения: и про актора,
/// и про watch_start (безымянный ход общего проекта лишён всех прав).
/// </summary>
[Trait("Category", "Host")]
public class TaskAutoLaunchActorTests : IDisposable
{
    private readonly TestWebApplicationFactory _factory = new();
    private readonly HttpClient _owner;
    private readonly HttpClient _editor;

    // Срок задачи далёкий по календарю; «сейчас» планировщика — через час после срока (окно автозапуска 24 ч)
    private const string DueDate = "2026-01-01";
    private const string DueTime = "10:00";
    private static readonly DateTime Now = new(2026, 1, 1, 11, 0, 0, DateTimeKind.Utc);

    public TaskAutoLaunchActorTests()
    {
        _factory.ExtraConfig[SharedProjectsGate.SwitchKey] = "true";
        _owner = _factory.CreateAuthenticatedClient();
        _editor = _factory.CreateAuthenticatedClient(
            TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);
    }

    public void Dispose() => _factory.Dispose();

    private SessionManager Sessions => _factory.Services.GetRequiredService<SessionManager>();
    private TaskManager Tasks => _factory.Services.GetRequiredService<TaskManager>();

    private static string IdOf(JsonElement e) => e.GetProperty("id").GetString()!;

    private static string ToolText(JsonElement answer) =>
        answer.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;

    private static async Task<JsonElement> CallToolAsync(HttpClient client, string server, string sessionId,
        string tool, object args)
    {
        var resp = await client.AsCaller(sessionId).PostAsJsonAsync($"/mcp/{server}/{sessionId}", new
        {
            jsonrpc = "2.0", id = 1, method = "tools/call", @params = new { name = tool, arguments = args },
        });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        return JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
    }

    private async Task<(string ProjectId, string ChatId, string OwnerId, string EditorId)> ArrangeSharedAsync()
    {
        foreach (var c in new[] { _owner, _editor })
            (await c.PutAsJsonAsync("/api/feature-flags/shared-projects", new { enabled = true })).EnsureSuccessStatusCode();
        var tag = Guid.NewGuid().ToString("N")[..8];

        var project = await _owner.PostAsJsonAsync("/api/projects", new { name = $"Общий-{tag}" });
        project.EnsureSuccessStatusCode();
        var projectId = IdOf(await project.Content.ReadFromJsonAsync<JsonElement>());

        var ownerUser = _factory.Services.GetRequiredService<UserStore>().FindByUsername(TestWebApplicationFactory.TestUsername)!;
        var editorUser = _factory.Services.GetRequiredService<UserStore>().FindByUsername(TestWebApplicationFactory.SecondUsername)!;
        _factory.Services.GetRequiredService<ProjectManager>().SetMembers(projectId,
            [new ProjectMember { UserId = editorUser.Id, Role = ProjectRole.Editor, AddedBy = ownerUser.Id }]);

        var chat = await _owner.PostAsJsonAsync($"/api/projects/{projectId}/sessions", new { mode = "acceptEdits" });
        chat.EnsureSuccessStatusCode();
        return (projectId, IdOf(await chat.Content.ReadFromJsonAsync<JsonElement>()), ownerUser.Id, editorUser.Id);
    }

    private static async Task StartTurnAsync(HttpClient client, string chatId) =>
        (await client.PostAsJsonAsync($"/api/sessions/{chatId}/messages",
            new { text = "поставь задачу на срок", wait = "none" }))
        .IsSuccessStatusCode.Should().BeTrue();

    /// <summary>Задача Claude со сроком, поставленная агентом чата через MCP tasks_create (актор хода — создатель).</summary>
    private async Task<string> CreateDueTaskViaMcpAsync(string chatId, string? projectId)
    {
        var answer = await CallToolAsync(_owner, "tasks", chatId, "tasks_create", new
        {
            title = "Автозапуск " + Guid.NewGuid().ToString("N")[..6],
            assignee = "claude",
            dueDate = DueDate,
            dueTime = DueTime,
            projectId,
        });
        var isError = answer.GetProperty("result").TryGetProperty("isError", out var flag) && flag.GetBoolean();
        isError.Should().BeFalse($"tasks_create должен пройти: {ToolText(answer)}");
        return IdOf(JsonSerializer.Deserialize<JsonElement>(ToolText(answer)));
    }

    /// <summary>Тик настоящего планировщика на настоящем исполнителе; id чата исполнителя — из записи задачи.</summary>
    private async Task<string> AutoLaunchAsync(string taskId)
    {
        var sp = _factory.Services;
        var scheduler = new TaskSchedulerService(
            Tasks, sp.GetRequiredService<IUserStore>(), sp.GetRequiredService<ISessionBroadcaster>(),
            sp.GetRequiredService<ITaskExecutor>(), new Mock<IDailyBriefingRunner>().Object,
            new Mock<IPersonaAutomationRunner>().Object, new Mock<ITaskNotificationDispatcher>().Object,
            NullLogger<TaskSchedulerService>.Instance);

        await scheduler.TickAsync(Now);

        var session = Tasks.GetById(taskId)!.LinkedSessionId;
        session.Should().NotBeNullOrEmpty("автозапуск по сроку обязан поднять чат исполнителя");
        return session!;
    }

    private async Task<bool> WatchStartDeniedAsync(string executorSessionId)
    {
        var answer = await CallToolAsync(_owner, "watch", executorSessionId, "watch_start",
            new { name = "стор", poll_command = "exit 0" });
        var result = answer.GetProperty("result");
        var denied = result.TryGetProperty("isError", out var flag) && flag.GetBoolean();
        if (denied) ToolText(answer).Should().Contain(ActorRightGate.DeniedText);
        return denied;
    }

    [Fact]
    public async Task Автозапуск_ЗадачаРедактора_ХодИдётСАкторомРедактора_ЗакрытыйИнструментОтказан()
    {
        var (projectId, chatId, ownerId, editorId) = await ArrangeSharedAsync();
        await StartTurnAsync(_editor, chatId);
        Sessions.GetTurnActor(chatId, ownerId).Should().Be(editorId);

        var taskId = await CreateDueTaskViaMcpAsync(chatId, projectId);
        Tasks.GetById(taskId)!.CreatedByUserId.Should().Be(editorId, "MCP-создатель — актор хода редактора");

        var executorSession = await AutoLaunchAsync(taskId);

        Sessions.GetTurnActor(executorSession, ownerId).Should().Be(editorId,
            "автозапуск наследует создателя, а не «неизвестного» с правами арендатора");
        (await WatchStartDeniedAsync(executorSession)).Should().BeTrue("watch_start закрыт редактору");
    }

    [Fact]
    public async Task Автозапуск_ЗадачаВладельца_КакРаньше_ПраваАрендатора()
    {
        var (projectId, chatId, ownerId, _) = await ArrangeSharedAsync();
        await StartTurnAsync(_owner, chatId);

        var taskId = await CreateDueTaskViaMcpAsync(chatId, projectId);
        Tasks.GetById(taskId)!.CreatedByUserId.Should().Be(ownerId);

        var executorSession = await AutoLaunchAsync(taskId);

        Sessions.GetTurnActor(executorSession, ownerId).Should().Be(ownerId,
            "создатель — владелец: его имя называется явно (безымянный ход общего проекта после О2 не имеет прав)");
        (await WatchStartDeniedAsync(executorSession)).Should().BeFalse("права арендатора — как раньше");
    }

    [Fact]
    public async Task Автозапуск_СтараяЗадачаБезСоздателя_СчитаетсяВладельцем()
    {
        var (projectId, chatId, ownerId, _) = await ArrangeSharedAsync();
        await StartTurnAsync(_owner, chatId);

        var taskId = await CreateDueTaskViaMcpAsync(chatId, projectId);
        Tasks.GetById(taskId)!.CreatedByUserId = null; // запись до F-9

        var executorSession = await AutoLaunchAsync(taskId);

        Sessions.GetTurnActor(executorSession, ownerId).Should().Be(ownerId,
            "старая запись считается созданной владельцем буквально: её актор — владелец записи");
        (await WatchStartDeniedAsync(executorSession)).Should().BeFalse("legacy — права владельца, как раньше");
    }

    [Fact]
    public async Task Автозапуск_ЛичнаяЗадача_БезИзменений()
    {
        var (_, chatId, ownerId, _) = await ArrangeSharedAsync();
        await StartTurnAsync(_owner, chatId);

        var created = await _owner.PostAsJsonAsync("/api/tasks", new
        {
            title = "Личная " + Guid.NewGuid().ToString("N")[..6], assignee = "claude", dueDate = DueDate, dueTime = DueTime,
        });
        created.EnsureSuccessStatusCode();
        var taskId = IdOf(await created.Content.ReadFromJsonAsync<JsonElement>());
        Tasks.GetById(taskId)!.CreatedByUserId.Should().Be(ownerId, "REST: создатель — UserId из JWT");

        var executorSession = await AutoLaunchAsync(taskId);

        // Поведение личного проекта не изменилось (там безымянный актор был бы разрешён), но
        // имя называется тем же порядком: актор исполнителя задачи владельца — владелец.
        Sessions.GetTurnActor(executorSession, ownerId).Should().Be(ownerId);
        (await WatchStartDeniedAsync(executorSession)).Should().BeFalse("личный проект — как раньше");
    }
}
