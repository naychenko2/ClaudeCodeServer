using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Access;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Controllers;

/// <summary>
/// F-1 (разрез 4bc3d04f §3, «исполнитель, порождённый агентом участника, получает права
/// арендатора»): исполнитель, запущенный через MCP tasks_run_executor из хода участника,
/// наследует АКТОРА этого хода, а не арендатора — и дальше держит его права: инструмент,
/// закрытый редактору, отказан и его исполнителю.
///
/// Источник актора — единый для всех путей: реестр хода SessionManager (NoteTurnActor пишет
/// его в точках подачи, GetTurnActor читает), тот же, что у REST-пути
/// (TasksController.Execute → sessions.GetTurnActor) и у гейта актора S2c-6
/// (ActorRightGate.Denied → ITurnActorRegistry.Actor). Сам вызов — TasksToolset
/// «tasks_run_executor»: ExecuteAsync(…, sessions.GetTurnActor(чата, владельца)).
///
/// Три случая приёмки:
/// • актор — редактор: исполнитель наследует редактора, watch_start (OwnerOnly) ему отказан;
/// • актор — владелец: как раньше, правы арендатора, гейт не при чём;
/// • актор неизвестен (ход в чате ещё не начинался): исполнитель запускается БЕЗ повышения
///   до арендатора — его ход не записывается на владельца (fail-closed).
///
/// Мутация (в отчёте задачи): вернуть в TasksToolset передачу арендатора вместо актора —
/// первый и третий тесты краснеют, второй остаётся зелёным.
/// </summary>
[Trait("Category", "Host")]
public class TasksRunExecutorActorTests : IDisposable
{
    private readonly TestWebApplicationFactory _factory = new();
    private readonly HttpClient _owner;
    private readonly HttpClient _editor;

    public TasksRunExecutorActorTests()
    {
        _factory.ExtraConfig[SharedProjectsGate.SwitchKey] = "true";
        _owner = _factory.CreateAuthenticatedClient();
        _editor = _factory.CreateAuthenticatedClient(
            TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);
    }

    public void Dispose() => _factory.Dispose();

    private SessionManager Sessions => _factory.Services.GetRequiredService<SessionManager>();

    private static string IdOf(JsonElement e) => e.GetProperty("id").GetString()!;

    private static string ToolText(JsonElement answer) =>
        answer.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;

    /// <summary>Общий проект владельца, редактор в участниках, чат проекта и задача в нём.</summary>
    private async Task<(string ChatId, string TaskId, string OwnerId, string EditorId)> ArrangeSharedAsync()
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
        var chatId = IdOf(await chat.Content.ReadFromJsonAsync<JsonElement>());

        var task = await _owner.PostAsJsonAsync($"/api/projects/{projectId}/tasks", new { title = $"Задача общего проекта {tag}" });
        task.EnsureSuccessStatusCode();
        var taskId = IdOf(await task.Content.ReadFromJsonAsync<JsonElement>());

        return (chatId, taskId, ownerUser.Id, editorUser.Id);
    }

    /// <summary>
    /// Вызов «от лица чата», как его делает конфиг хода: сессия в хвосте URL И тем же id в
    /// X-Caller-Session-Id (ClaudeSession:1701, :1733). Без заголовка транспорт не знает
    /// вызывателя (McpTransportController:83-85) и гейт актора вообще не применяется —
    /// тест без него мерил бы не то.
    /// </summary>
    private static async Task<JsonElement> CallToolAsync(HttpClient client, string server, string sessionId,
        string tool, object args)
    {
        var resp = await client.AsCaller(sessionId).PostAsJsonAsync($"/mcp/{server}/{sessionId}", new
        {
            jsonrpc = "2.0", id = 1, method = "tools/call", @params = new { name = tool, arguments = args },
        });
        resp.StatusCode.Should().Be(HttpStatusCode.OK, "отказ инструмента — content-ошибка, не протокольная");
        return JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// Запуск из чата-вызывателя (токен — арендатора, как у агента участника). Id чата исполнителя
    /// читается из хранимой задачи (карточка владельца), а не из ответа тулсета: тот проходит
    /// маску чужих чатов (S2d-4b) и при неизвестном акторе id закрыла бы — здесь проверяется
    /// актор порождённого хода, а не маска.
    /// </summary>
    private async Task<string> RunExecutorAsync(string chatId, string taskId)
    {
        var answer = await CallToolAsync(_owner, "tasks", chatId, "tasks_run_executor", new { taskId });
        var isError = answer.GetProperty("result").TryGetProperty("isError", out var flag) && flag.GetBoolean();
        isError.Should().BeFalse($"запуск исполнителя должен пройти: {ToolText(answer)}");

        var stored = await _owner.GetAsync($"/api/tasks/{taskId}");
        stored.EnsureSuccessStatusCode();
        var sessionId = (await stored.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("linkedSessionId").GetString();
        sessionId.Should().NotBeNullOrEmpty($"исполнитель обязан иметь чат: {ToolText(answer)}");
        return sessionId!;
    }

    private static async Task StartTurnAsync(HttpClient client, string chatId) =>
        (await client.PostAsJsonAsync($"/api/sessions/{chatId}/messages",
            new { text = "подними задачу на исполнение", wait = "none" }))
        .IsSuccessStatusCode.Should().BeTrue();

    /// <summary>
    /// Актор — редактор: исполнитель наследует его (не арендатора), и закрытый редактору
    /// пишущий инструмент (watch_start — OwnerOnly) отказан и его исполнителю. Иначе
    /// делегирование было бы лазейкой: агенту участника право закрыто, а порождённому им
    /// исполнителю — открыто.
    /// </summary>
    [Fact]
    public async Task RunExecutor_ИзХодаРедактора_ИсполнительНаследуетРедактора_ЕгоЗакрытыйИнструментОтказан()
    {
        var (chatId, taskId, ownerId, editorId) = await ArrangeSharedAsync();

        // Предусловие (S2a-10a): ход в общем чате принадлежит редактору
        await StartTurnAsync(_editor, chatId);
        Sessions.GetTurnActor(chatId, ownerId).Should().Be(editorId);

        var executorSessionId = await RunExecutorAsync(chatId, taskId);

        Sessions.GetTurnActor(executorSessionId, ownerId).Should().Be(editorId,
            "исполнитель порождён агентом редактора — его актор тот же, а не арендатор");

        var answer = await CallToolAsync(_owner, "watch", executorSessionId, "watch_start",
            new { name = "стор", poll_command = "exit 0" });
        answer.TryGetProperty("result", out var res).Should().BeTrue($"ответ watch_start: {answer.GetRawText()}");
        res.GetProperty("isError").GetBoolean().Should().BeTrue(ToolText(answer));
        ToolText(answer).Should().Contain(ActorRightGate.DeniedText,
            "право читается по актору порождённого хода, не по токену арендатора");
    }

    /// <summary>
    /// Актор — владелец: как раньше. Актор совпадает с арендатором → права арендатора,
    /// гейт актора к такому исполнителю не применяется.
    /// </summary>
    [Fact]
    public async Task RunExecutor_ИзХодаВладельца_АкторВладельца_КакРаньше()
    {
        var (chatId, taskId, ownerId, _) = await ArrangeSharedAsync();

        await StartTurnAsync(_owner, chatId);
        Sessions.GetTurnActor(chatId, ownerId).Should().Be(ownerId);

        var executorSessionId = await RunExecutorAsync(chatId, taskId);

        Sessions.GetTurnActor(executorSessionId, ownerId).Should().Be(ownerId);

        var answer = await CallToolAsync(_owner, "watch", executorSessionId, "watch_start",
            new { name = "стор", poll_command = "exit 0" });
        var isError = answer.GetProperty("result").TryGetProperty("isError", out var flag) && flag.GetBoolean();
        isError.Should().BeFalse($"исполнитель владельца не ограничен гейтом актора: {ToolText(answer)}");
    }

    /// <summary>
    /// Актор неизвестен (в чате ещё не было хода — вызов из фона): сам запуск легален
    /// (неизвестный актор идёт с правами арендатора), но исполнитель НЕ записывается на
    /// арендатора: его ход остаётся без актора — fail-closed, без повышения.
    /// </summary>
    [Fact]
    public async Task RunExecutor_АкторНеизвестен_ИсполнительБезПовышенияДоАрендатора()
    {
        var (chatId, taskId, ownerId, _) = await ArrangeSharedAsync();

        var executorSessionId = await RunExecutorAsync(chatId, taskId);

        Sessions.GetTurnActor(executorSessionId, ownerId).Should().BeNull(
            "актор неизвестен — исполнитель не повышается до арендатора");
    }
}
