using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Controllers;

// POST /api/chats/{id}/window-1m/drop — «продолжить в стандартном окне 200 тысяч токенов»
// под карточкой отказа Window1MUnavailable. Единственный путь, которым суффикс [1m] снимается
// с чата: автоматического среза в ходе больше нет (тихая деградация длинного разговора в 200K
// — мина), поэтому решение принимает человек, а сервер только исполняет.
public class ChatsWindow1MDropTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    private readonly HttpClient _client = factory.CreateAuthenticatedClient();

    private async Task<string> CreateChatAsync(string? model)
    {
        var resp = await _client.PostAsJsonAsync("/api/chats", new { mode = "auto", model });
        resp.EnsureSuccessStatusCode();
        var chat = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
        return chat.GetProperty("id").GetString()!;
    }

    private static SessionManager SessionsOf(TestWebApplicationFactory f) =>
        f.Services.GetRequiredService<SessionManager>();

    [Fact]
    public async Task ЧатНаОкне1M_СуффиксСнят_ОстаётсяБазовыйАлиас()
    {
        var id = await CreateChatAsync("opus[1m]");

        var resp = await _client.PostAsync($"/api/chats/{id}/window-1m/drop", null);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        SessionsOf(factory).GetById(id)!.Model.Should().Be("opus",
            "базовый алиас закрепляется в чате явно — иначе следующий ход вернул бы окно 1M");
    }

    [Fact]
    public async Task ЧатБезОкна1M_400_СниматьНечего()
    {
        var id = await CreateChatAsync("sonnet");

        var resp = await _client.PostAsync($"/api/chats/{id}/window-1m/drop", null);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        SessionsOf(factory).GetById(id)!.Model.Should().Be("sonnet");
    }

    [Fact]
    public async Task ЧужойЧат_404()
    {
        var resp = await _client.PostAsync("/api/chats/нет-такого/window-1m/drop", null);

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // Дополнение покрытия проектными чатами: фронт рисует кнопку «Продолжить в стандартном окне»
    // и в проектных чатах (тот же ChatPanel без ветвления по проекту), а исходная проверка через
    // OwnedChat жёстко отбивала любой ProjectId != null — для владельца это всегда был 404.
    // Резолв через GetOwned (как у MigrateProvider/SetArchived/SetWorkLoop) умеет проектные чаты
    // через ResolveOwnerId, поэтому эндпоинт обязан их обслуживать. Без этих двух кейсов
    // пробел в покрытии был и остался бы невидим.

    // Кнопка живёт и в проектных чатах (ChatPanel без ветвления): «opus[1m]» → «opus» снимается.
    [Fact]
    public async Task ПроектныйЧатНаОкне1M_СуффиксСнят_ОстаётсяБазовыйАлиас()
    {
        var projectId = await CreateProjectAsync();
        var chatId = await CreateProjectSessionAsync(projectId, "opus[1m]");

        var resp = await _client.PostAsync($"/api/chats/{chatId}/window-1m/drop", null);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        SessionsOf(factory).GetById(chatId)!.Model.Should().Be("opus",
            "базовый алиас закрепляется в чате явно — иначе следующий ход вернул бы окно 1M");
    }

    // Чужой проектный чат — тот же гейт 404, что у чата вне проекта: GetOwned резолвит владельца
    // через владельца проекта и даёт null на чужой сессии.
    [Fact]
    public async Task ЧужойПроектныйЧат_404()
    {
        using var otherClient = factory.CreateAuthenticatedClient(
            TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);
        var otherProjectId = await otherClient.CreateProjectAsync();
        var otherChatId = await otherClient.CreateProjectSessionAsync(otherProjectId, "opus[1m]");

        var resp = await _client.PostAsync($"/api/chats/{otherChatId}/window-1m/drop", null);

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "чужой проектный чат резолвится в null через GetOwned — 404, как и чужой чат вне проекта");
    }

    // Создание проекта в дефолтной папке (без rootPath). Сам путь неважен — фронт рисует кнопку
    // независимо от него; для теста достаточно, что проект живёт и чат создаётся в нём.
    private async Task<string> CreateProjectAsync()
    {
        var resp = await _client.PostAsJsonAsync("/api/projects", new { name = "Window1MDrop" });
        resp.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync())
            .GetProperty("id").GetString()!;
    }

    // Проектная сессия (владелец — клиент). Model пробрасывается на этапе создания: иначе
    // пришлось бы править через отдельный PUT, а тест проверяет кнопку «снять окно 1M», не смену модели.
    private async Task<string> CreateProjectSessionAsync(string projectId, string? model)
    {
        var resp = await _client.PostAsJsonAsync($"/api/projects/{projectId}/sessions",
            new { mode = "auto", model });
        resp.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync())
            .GetProperty("id").GetString()!;
    }
}

// Расширение HttpClient тестовыми хелперами — чтобы второй владелец мог сам создать проект и
// проектный чат для проверки изоляции. Не дублируем логику WorkspaceHttpOwnerIsolationTests,
// потому что там своя фабрика per-test, а здесь — общий IClassFixture.
internal static class ChatsWindow1MDropHttpClientExtensions
{
    public static async Task<string> CreateProjectAsync(this HttpClient client)
    {
        var resp = await client.PostAsJsonAsync("/api/projects", new { name = "Window1MDrop" });
        resp.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync())
            .GetProperty("id").GetString()!;
    }

    public static async Task<string> CreateProjectSessionAsync(this HttpClient client, string projectId, string? model)
    {
        var resp = await client.PostAsJsonAsync($"/api/projects/{projectId}/sessions",
            new { mode = "auto", model });
        resp.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync())
            .GetProperty("id").GetString()!;
    }
}
