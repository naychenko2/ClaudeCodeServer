using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Filters;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Tasks;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Controllers;

// Гард против затирания снятой задачи на REST-пути (фикс-волна 4 team-blocker-honest,
// пункт 5 повторного ревью). MCP-тулсеты зовут TaskManager.Update с isAgentCall: true
// напрямую, но при выключенном Mcp:HttpTransport инструменты идут stdio-веткой отката —
// mcp/tasks-server/index.js делает обычный PUT /api/tasks/{id}, и без вывода isAgentCall
// из заголовка X-Caller-Session-Id затирание снятой задачи снова становилось молчаливым.
//
// Разведение путей — по заголовку: MCP-серверы шлют его на КАЖДЫЙ вызов, браузер не шлёт
// никогда (см. комментарий в TaskEditForm.tsx). Это не защита от подделки — подделанный
// заголовок делает правило строже, а не слабее.
//
// Мутация: убрать isAgentCall из вызова tasks.Update в TasksController — первый тест
// получит 200 вместо 400 и покраснеет.
[Trait("Category", "Integration")]
public class TasksControllerDroppedGuardTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public TasksControllerDroppedGuardTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateAuthenticatedClient();
    }

    // Снятая штабом задача: создаём обычную личную задачу и ставим тот же маркер, что
    // ставит TeamWaveService.DropSubtaskAsync (эндпоинта «снять» у задач нет — снятие
    // приходит с карточки штаба).
    private async Task<string> CreateDroppedTaskAsync()
    {
        var resp = await _client.PostAsJsonAsync("/api/tasks", new { title = "Снятая под-задача" });
        resp.EnsureSuccessStatusCode();
        var id = (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        _factory.Services.GetRequiredService<TaskManager>().MarkDroppedByHuman(id, DateTime.UtcNow)
            .Should().NotBeNull("маркер снятия обязан встать — иначе тест проверяет не тот путь");
        return id;
    }

    [Fact]
    public async Task Update_СнятаяЗадача_АгентскийПутьПоЗаголовку_400()
    {
        var taskId = await CreateDroppedTaskAsync();
        var req = new HttpRequestMessage(HttpMethod.Put, $"/api/tasks/{taskId}")
        {
            Content = JsonContent.Create(new { status = "done", resultMarkdown = "Готово" }),
        };
        req.Headers.Add(DenyOnDelegatedTurnAttribute.CallerHeader, "some-executor-session");

        var resp = await _client.SendAsync(req);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "поздний tasks_complete по stdio-ветке обязан получить внятный отказ, а не молчаливые 200 OK");
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain("снята человеком", "текст отказа объясняет, почему правка не применилась");
        var after = _factory.Services.GetRequiredService<TaskManager>().GetById(taskId)!;
        after.Status.Should().NotBe(TaskItemStatus.Done, "статус штаба не затёрт");
        after.DroppedByHumanAt.Should().NotBeNull("пометка снятия на месте");
    }

    [Fact]
    public async Task Update_СнятаяЗадача_ЧеловекБезЗаголовка_ВозвращаетВРаботу()
    {
        // Обратная сторона: человек из UI (заголовка нет) вправе вернуть снятую задачу
        // в работу — 200, статус применяется, пометка снятия снимается.
        var taskId = await CreateDroppedTaskAsync();

        var resp = await _client.PutAsJsonAsync($"/api/tasks/{taskId}", new { status = "inProgress" });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var after = _factory.Services.GetRequiredService<TaskManager>().GetById(taskId)!;
        after.Status.Should().Be(TaskItemStatus.InProgress, "человек вернул задачу в работу");
        after.DroppedByHumanAt.Should().BeNull("возврат в работу снимает пометку снятия");
    }
}
