using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using ClaudeHomeServer.Controllers;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Access;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Controllers;

/// <summary>
/// Чтение своего детского чата ребёнком (KM-B6f, ADR-041): на двух ручках чтения
/// (GET /api/chats/{id} и GET /api/chats/{id}/history, обе формы — с курсором и без)
/// ребёнку открыты только его ChildSpace-чаты вне проекта (владение, как в GetHistory). Свой
/// ChildSpace-чат — 200; чужой чат, свой без ChildSpace, чат другого ребёнка в общем проекте
/// и несуществующий — 404 chat_not_found, неотличимы (одним телом). Прочие ручки
/// ChatsController ребёнку закрыты гейтом И1 до тела ручки — 403 child_mode.
/// </summary>
[Trait("Category", "Host")]
public class ChildChatReadTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory factory;

    public ChildChatReadTests(TestWebApplicationFactory factory)
    {
        // Рубильник общих проектов нужен тесту ЧатДругогоРебёнкаВОбщемПроекте: доступ
        // участника к общему проекту без него закрыт (SharedProjectsGate). Ставится до
        // первого билда хоста — первое обращение к Services происходит в тесте.
        factory.ExtraConfig[SharedProjectsGate.SwitchKey] = "true";
        this.factory = factory;
    }

    private SessionManager Sessions => factory.Services.GetRequiredService<SessionManager>();
    private UserStore Users => factory.Services.GetRequiredService<UserStore>();
    private ProjectManager Projects => factory.Services.GetRequiredService<ProjectManager>();

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

    private static async Task<HttpResponseMessage> AssertChatNotFoundAsync(HttpClient client, string url, string what)
    {
        var resp = await client.GetAsync(url);
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound, what);
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain("chat_not_found", what);
        return resp;
    }

    // Чат другого ребёнка в общем проекте (R-KM-B6f п. 2): проект принадлежит админу —
    // по ResolveOwnerId чат принадлежит ему; ребёнок — участник с рубильником и флагом
    // общих проектов, т.е. чат ему ВИДЕН через GetAccessible и помечен ChildSpace, но не свой.
    private async Task<string> ForeignChildChatInSharedProjectAsync()
    {
        var admin = Users.FindByUsername(TestWebApplicationFactory.TestUsername)!;
        var project = Projects.Create($"Общий-{Guid.NewGuid():N}", null, AdminId, admin.Username);
        var chat = await Sessions.CreateAsync(project.Id, ClaudeMode.Auto,
            name: "Чат другого ребёнка", createdByUserId: AdminId);
        chat.ChildSpace = ChildSpaces.Lessons;
        Projects.SetMembers(project.Id,
            [new ProjectMember { UserId = ChildId, Role = ProjectRole.Reader, AddedBy = AdminId }]);
        Users.SetFeatureFlag(ChildId, FeatureFlagKeys.SharedProjects, true);
        Sessions.SaveSessions();
        return chat.Id;
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

    // ── несуществующий id: то же тело 404, что у чужого чата (R-KM-B6f п. 1) ──────

    [Fact]
    public async Task НесуществующийЧат_404_ChatNotFound_ТелоКакУЧужого()
    {
        var foreignId = await AdminChatAsync();
        SetChild();
        var child = Child();
        var missingId = Guid.NewGuid().ToString("N");

        // Карточка: 404 chat_not_found, тело побайтово то же, что у чужого — существование
        // несуществующего не раскрываем ни списком, ни телом
        var foreignBody = await (await AssertChatNotFoundAsync(child, $"/api/chats/{foreignId}", "чужой чат — карточка"))
            .Content.ReadAsStringAsync();
        var missingBody = await (await AssertChatNotFoundAsync(child, $"/api/chats/{missingId}", "несуществующий чат — карточка"))
            .Content.ReadAsStringAsync();
        missingBody.Should().Be(foreignBody, "тело несуществующего чата — то же, что у чужого");

        // История: та же неотличимость
        await AssertChatNotFoundAsync(child, $"/api/chats/{missingId}/history", "несуществующий чат — история");
    }

    // ── чат другого ребёнка в общем проекте: ребёнку не свой — 404 (R-KM-B6f п. 2) ─

    [Fact]
    public async Task ЧатДругогоРебёнкаВОбщемПроекте_404_ChatNotFound()
    {
        var id = await ForeignChildChatInSharedProjectAsync();
        SetChild();
        var child = Child();

        // Чат видим через GetAccessible (участник общего проекта) и помечен ChildSpace, но
        // принадлежит другому: чтению ребёнка остаётся только своё (OwnedChat, как в GetHistory)
        await AssertChatNotFoundAsync(child, $"/api/chats/{id}", "чат другого ребёнка в общем проекте — карточка");
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

    // ── без политики в скопе запроса — исключение, а не взрослая ветка (R-KM-B6f minor 2) ──

    // Ручной скоп запроса: claims ребёнка есть, регистрации IChildModePolicy в скопе нет —
    // ровно тот случай, который раньше молчаливо уходил во взрослую ветку. Ручка строится
    // напрямую (гейт И1 — middleware хоста, здесь его нет): проверяем только CurrentOwnerIsChild.
    private ChatsController ChatsWithoutChildPolicy() =>
        new(Sessions, Projects, null!, null!, null!, null!, null!, null!, null!, null!, null!,
            NullLogger<ChatsController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", ChildId)])),
                    RequestServices = new ServiceCollection().BuildServiceProvider(),
                },
            },
        };

    [Fact]
    public async Task СвойДетскийЧат_БезПолитикиВСкопе_Исключение_АНеВзрослаяВетка()
    {
        var id = await ChildChatAsync(ChildSpaces.Lessons);
        SetChild();

        // Взрослая ветка на СВОЁМ чате дала бы 200 (GetAccessible проходит); fail-closed даёт
        // InvalidOperationException от GetRequiredService — отсутствие политики слышно
        FluentActions.Invoking(() => ChatsWithoutChildPolicy().Get(id))
            .Should().Throw<InvalidOperationException>("политика не зарегистрирована — не ребёнок по умолчанию")
            .WithMessage("*IChildModePolicy*");
    }
}
