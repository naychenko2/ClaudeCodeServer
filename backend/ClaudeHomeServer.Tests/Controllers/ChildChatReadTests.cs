using System.Net;
using System.Net.Http.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Controllers;

/// <summary>
/// Чтение своего детского чата ребёнком (KM-B6f, ADR-041): на двух ручках чтения
/// (GET /api/chats/{id} и GET /api/chats/{id}/history, обе формы — с курсором и без)
/// ребёнку открыты только его чаты с ChildSpace. Свой ChildSpace-чат — 200; чужой чат и
/// свой без ChildSpace — 404 chat_not_found (неотличимы от несуществующего). Прочие ручки
/// ChatsController ребёнку закрыты гейтом И1 до тела ручки — 403 child_mode.
/// </summary>
[Trait("Category", "Host")]
public class ChildChatReadTests(TestWebApplicationFactory factory) : IClassFixture<TestWebApplicationFactory>
{
    private SessionManager Sessions => factory.Services.GetRequiredService<SessionManager>();
    private UserStore Users => factory.Services.GetRequiredService<UserStore>();

    private string ChildId => Users.FindByUsername(TestWebApplicationFactory.SecondUsername)!.Id;
    private string AdminId => Users.FindByUsername(TestWebApplicationFactory.TestUsername)!.Id;

    // Валидный профиль (тот же, что в ChildModeEndpointGuardTests): ребёнок проходит гейт.
    private void SetChild() =>
        Users.FindByUsername(TestWebApplicationFactory.SecondUsername)!.ChildMode = new ChildProfile
        {
            Enabled = true,
            TutorPersonaId = "tutor",
            ProviderKey = "key",
            Model = "sonnet",
        };

    // Создание чата ребёнка обходится без детской проверки хода (TurnLaunchContextFactory
    // отказывает чат ребёнка, чья персона не тьютор): чат строится, пока владелец НЕ ребёнок.
    // IClassFixture разделяет UserStore между тестами — профиль снимаем явно перед созданием.
    private void ClearChild() =>
        Users.FindByUsername(TestWebApplicationFactory.SecondUsername)!.ChildMode = null;

    private HttpClient Child() =>
        factory.CreateAuthenticatedClient(TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);

    // Чат вне проекта, принадлежащий ребёнку; ChildSpace проставляется напрямую — создание
    // ChildSpace-чата (детский онбординг) относится к другому шагу.
    private async Task<string> ChildChatAsync(string? childSpace)
    {
        ClearChild();
        var chat = await Sessions.CreateChatAsync(ChildId, ClaudeMode.Auto, name: "Чат ребёнка");
        chat.ChildSpace = childSpace;
        Sessions.SaveSessions();
        return chat.Id;
    }

    private async Task<string> AdminChatAsync() =>
        (await Sessions.CreateChatAsync(AdminId, ClaudeMode.Auto, name: "Чужой чат")).Id;

    private static async Task AssertChatNotFoundAsync(HttpClient client, string url, string what)
    {
        var resp = await client.GetAsync(url);
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound, what);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("chat_not_found", what);
    }

    private static async Task AssertChildDeniedAsync(HttpClient client, HttpMethod method, string url, object? body, string what)
    {
        using var req = new HttpRequestMessage(method, url);
        if (body is not null) req.Content = JsonContent.Create(body);
        var resp = await client.SendAsync(req);
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden, what);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("child_mode", what);
    }

    // ── свой ChildSpace-чат: 200 на обеих ручках, обе формы ──────────────────────

    [Fact]
    public async Task СвойДетскийЧат_200_НаОбеихРучках()
    {
        var id = await ChildChatAsync(ChildSpaces.Lessons);
        SetChild();
        var child = Child();

        (await child.GetAsync($"/api/chats/{id}"))
            .StatusCode.Should().Be(HttpStatusCode.OK, "карточка своего ChildSpace-чата");
        (await child.GetAsync($"/api/chats/{id}/history"))
            .StatusCode.Should().Be(HttpStatusCode.OK, "история без курсора");
        (await child.GetAsync($"/api/chats/{id}/history?after=0"))
            .StatusCode.Should().Be(HttpStatusCode.OK, "история с курсором");
    }

    // ── чужой чат и свой без ChildSpace: 404 chat_not_found, неотличимы ───────────

    [Fact]
    public async Task ЧужойЧат_404_ChatNotFound()
    {
        var id = await AdminChatAsync();
        SetChild();
        var child = Child();

        await AssertChatNotFoundAsync(child, $"/api/chats/{id}", "чужой чат — карточка");
        await AssertChatNotFoundAsync(child, $"/api/chats/{id}/history", "чужой чат — история");
    }

    [Fact]
    public async Task СвойЧатБезChildSpace_404_ChatNotFound()
    {
        var id = await ChildChatAsync(childSpace: null);
        SetChild();
        var child = Child();

        await AssertChatNotFoundAsync(child, $"/api/chats/{id}", "свой чат без ChildSpace — карточка");
        await AssertChatNotFoundAsync(child, $"/api/chats/{id}/history", "свой чат без ChildSpace — история");
    }

    // ── прочие ручки ChatsController ребёнку закрыты гейтом: 403 child_mode ───────

    [Fact]
    public async Task ПрочиеРучкиChatsController_403_ChildMode()
    {
        var id = await ChildChatAsync(ChildSpaces.Lessons);
        SetChild();
        var child = Child();

        // Ни одна из этих ручек не помечена [ChildAllowed] — гейт И1 отказывает до тела ручки
        await AssertChildDeniedAsync(child, HttpMethod.Put, $"/api/chats/{id}", new { name = "x" }, "PUT {id}");
        await AssertChildDeniedAsync(child, HttpMethod.Put, $"/api/chats/{id}/read", null, "PUT {id}/read");
        await AssertChildDeniedAsync(child, HttpMethod.Post, $"/api/chats/{id}/migrate-provider", new { }, "migrate-provider");
        await AssertChildDeniedAsync(child, HttpMethod.Post, $"/api/chats/{id}/persona", new { }, "persona");
        await AssertChildDeniedAsync(child, HttpMethod.Post, $"/api/chats/{id}/files/upload", null, "files/upload");
    }
}
