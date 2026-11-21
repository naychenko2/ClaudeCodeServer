using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using ClaudeHomeServer.Controllers;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Access;
using ClaudeHomeServer.Services.Llm;
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
/// ребёнку открыты только его ChildSpace-чаты, независимо от ProjectId (Б9d: урок — чат
/// проекта-предмета, Б9b; граница — владение через GetOwnedVisible + ChildSpace). Свой
/// ChildSpace-чат — 200, живой урок в проекте-предмете и «старый урок» вне проекта одинаково;
/// чужой чат, свой без ChildSpace, чат другого ребёнка (в т.ч. участнику проекта-предмета)
/// и несуществующий — 404 chat_not_found, неотличимы (одним телом). Прочие ручки
/// ChatsController ребёнку закрыты гейтом И1 до тела ручки — 403 child_mode.
/// </summary>
[Trait("Category", "Host")]
public class ChildChatReadTests : IClassFixture<TestWebApplicationFactory>
{
    private const string ChildPassword = "kids-read-password";
    private const string KidsKey = "kids-api";
    private const string TutorModel = "sonnet";

    private readonly TestWebApplicationFactory factory;

    public ChildChatReadTests(TestWebApplicationFactory factory)
    {
        // Рубильник общих проектов нужен тесту ЧатДругогоРебёнкаВОбщемПроекте: доступ
        // участника к общему проекту без него закрыт (SharedProjectsGate). Ставится до
        // первого билда хоста — первое обращение к Services происходит в тесте.
        factory.ExtraConfig[SharedProjectsGate.SwitchKey] = "true";
        // Б9d-кейсы создают урок штатным POST (Б9b): провижн проекта-предмета требует
        // песочницы container-ребёнка (UserHomeResolver.SubjectHome), живой маршрут урока —
        // подписки предмета (И2′). Те же ключи, что в хосте ChildChatsControllerTests.
        factory.ExtraConfig["Sandbox:ProjectsRoot"] = Path.Combine(Path.GetTempPath(), "ccs-child-read-" + Guid.NewGuid().ToString("N"));
        factory.ExtraConfig[$"ClaudeSubscriptions:{KidsKey}:ApiKey"] = "sk-ant-test-kids";
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

    // ── Б9d: штатное создание урока в проекте-предмете (тот же фикстур, что в ChildChatsControllerTests) ──

    private static readonly TimeZoneInfo Moscow = TimeZoneInfo.FindSystemTimeZoneById("Europe/Moscow");

    // Окно присутствия «сейчас» (тот же фикстур, что Window(inWindow: true))
    private static (string From, string To) Window()
    {
        var local = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, Moscow).DateTime;
        return (local.AddHours(-2).ToString("HH:mm", CultureInfo.InvariantCulture),
            local.AddHours(2).ToString("HH:mm", CultureInfo.InvariantCulture));
    }

    private HttpClient Admin() => factory.CreateAuthenticatedClient();
    private HttpClient ChildClient(string name) => factory.CreateAuthenticatedClient(name, ChildPassword);

    // Ребёнок с предметом, как в проде: container-пользователь, настоящий детский профиль
    // (окно, согласие на риск маршрутов), согласие правил, фикстура предмета и назначение
    // модели репетитора (И10): без него живой маршрут не развёрнут и урок — 409 subject_not_ready.
    private async Task<(string Name, string Id)> NewSubjectChildAsync(string name)
    {
        var resp = await Admin().PostAsJsonAsync("/api/users", new
        {
            username = name, password = ChildPassword, role = "user",
            executionEnvironment = ExecutionEnvironments.Container,
        });
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var id = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync())
            .GetProperty("id").GetString()!;
        var (from, to) = Window();
        var put = await Admin().PutAsJsonAsync($"/api/users/{id}/child-profile", new
        {
            enabled = true, acknowledgeRouteRisk = true, providerKey = KidsKey, model = TutorModel,
            spaces = new Dictionary<string, object> { ["lessons"] = new { windows = new[] { new { from, to } }, dailyMinutes = 40 } },
            dailyBudgetUsd = 2, monthlyBudgetUsd = 40,
        });
        put.StatusCode.Should().Be(HttpStatusCode.OK);
        ChildTestSubjects.Seed(factory.Services, id);
        var profile = Users.GetById(id)!.ChildMode!;
        profile.RulesAgreedAt = new DateTime(2026, 11, 19, 10, 0, 0, DateTimeKind.Utc);
        profile.RulesSummary = "правила";
        (factory.Services.GetRequiredService<LocalActionOverridesStore>()
            .Set(LocalActionCatalog.ChatPersona, TutorModel)).Should().BeTrue();
        return (name, id);
    }

    // Штатный POST урока (Б9b): чат проекта-предмета от лица его руководителя
    private async Task<string> CreateLessonAsync(HttpClient client)
    {
        var resp = await ChildTestSubjects.CreateChat(client);
        resp.StatusCode.Should().Be(HttpStatusCode.Created, "штатный POST создаёт урок");
        return (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("sessionId").GetString()!;
    }

    // Проект-предмета ребёнка по маркеру (та же точка чтения, что SubjectProjectOf в ChildChatsControllerTests)
    private Project SubjectProjectOf(string childId, string subjectId) =>
        Projects.GetByOwner(childId).Single(p => ProjectCapabilities.IsChildSubject(p)
            && p.ChildSubjectId == subjectId);

    private static async Task<string> BodyOfAsync(HttpClient client, string url) =>
        await (await client.GetAsync(url)).Content.ReadAsStringAsync();

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

    // ── Б9d: живой урок в проекте-предмете: 200 на обеих ручках, обе формы ────────

    [Fact]
    public async Task УрокВПроектеПредмета_200_НаОбеихРучках()
    {
        var (name, id) = await NewSubjectChildAsync("kid-read-lesson");
        var chatId = await CreateLessonAsync(ChildClient(name));
        var project = SubjectProjectOf(id, ChildTestSubjects.Key);

        // Sanity: урок — чат проекта-предмета (Б9b), его владельца резолвит проект
        Sessions.GetById(chatId)!.ProjectId.Should().Be(project.Id);

        var child = ChildClient(name);
        (await child.GetAsync($"/api/chats/{chatId}"))
            .StatusCode.Should().Be(HttpStatusCode.OK, "карточка живого урока");
        (await child.GetAsync($"/api/chats/{chatId}/history"))
            .StatusCode.Should().Be(HttpStatusCode.OK, "история живого урока без курсора");
        (await child.GetAsync($"/api/chats/{chatId}/history?after=0"))
            .StatusCode.Should().Be(HttpStatusCode.OK, "история живого урока с курсором");
    }

    // ── «старый урок» вне проекта: 200 как раньше — GetOwnedVisible у чата без проекта
    // резолвит владельца по Session.OwnerId ──────────────────────────────────────────

    [Fact]
    public async Task СтарыйУрокВнеПроекта_200_НаОбеихРучках()
    {
        SetChild();
        // «Урок» до Б9b: детский чат вне проекта с ChildSpace и ChildSubject. Его старт ребёнку
        // отказан (Б9a), поэтому он создаётся, пока владелец НЕ ребёнок, и остаётся для чтения
        // (тот же приём, что LegacyLessonAsync в ChildChatsControllerTests).
        var user = Users.FindByUsername(TestWebApplicationFactory.SecondUsername)!;
        var profile = user.ChildMode!;
        user.ChildMode = null;
        string id;
        try
        {
            id = (await Sessions.CreateChatAsync(ChildId, ClaudeMode.Auto, name: "Старый урок",
                childSpace: ChildSpaces.Lessons, childSubject: ChildTestSubjects.Key)).Id;
        }
        finally { user.ChildMode = profile; }

        var child = Child();
        (await child.GetAsync($"/api/chats/{id}"))
            .StatusCode.Should().Be(HttpStatusCode.OK, "карточка старого урока");
        (await child.GetAsync($"/api/chats/{id}/history"))
            .StatusCode.Should().Be(HttpStatusCode.OK, "история старого урока без курсора");
        (await child.GetAsync($"/api/chats/{id}/history?after=0"))
            .StatusCode.Should().Be(HttpStatusCode.OK, "история старого урока с курсором");
    }

    // ── второй ребёнок — Reader проекта-предмета первого: урок ему не свой ─────────
    // (виден через GetAccessible, но чтению — только своё; тело, побайтово равное
    // несуществующему id) ────────────────────────────────────────────────────────────

    private async Task<(string Name, string Id)> NewPlainChildAsync(string name)
    {
        var resp = await Admin().PostAsJsonAsync("/api/users", new { username = name, password = ChildPassword, role = "user" });
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var id = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync())
            .GetProperty("id").GetString()!;
        // Профиль ребёнка напрямую в сторе — как SetChild: для чтения нужна только опознаваемость
        // ребёнком (политика), права хода и лимиты этим кейсам не нужны.
        Users.GetById(id)!.ChildMode = new ChildProfile
        {
            Enabled = true, TutorPersonaId = "tutor", ProviderKey = "key", Model = "sonnet",
        };
        return (name, id);
    }

    [Fact]
    public async Task ВторойРебёнокУчастникПроекта_404_ТелоКакУНесуществующего()
    {
        var (name, firstId) = await NewSubjectChildAsync("kid-read-owner");
        var chatId = await CreateLessonAsync(ChildClient(name));
        var project = SubjectProjectOf(firstId, ChildTestSubjects.Key);

        // Второй ребёнок — Reader проекта-предмета первого с флагом общих проектов:
        // урок ему ВИДЕН (GetAccessible), но принадлежит первому (ResolveOwnerId — по проекту)
        var (secondName, secondId) = await NewPlainChildAsync("kid-read-reader");
        Projects.SetMembers(project.Id,
            [new ProjectMember { UserId = secondId, Role = ProjectRole.Reader, AddedBy = AdminId }]);
        Users.SetFeatureFlag(secondId, FeatureFlagKeys.SharedProjects, true);

        var reader = ChildClient(secondName);
        var missingBody = await BodyOfAsync(reader, $"/api/chats/{Guid.NewGuid():N}");
        var card = await reader.GetAsync($"/api/chats/{chatId}");
        card.StatusCode.Should().Be(HttpStatusCode.NotFound, "карточка чужого урока");
        (await card.Content.ReadAsStringAsync()).Should().Be(missingBody, "чужой урок неотличим от несуществующего — карточка");
        var history = await reader.GetAsync($"/api/chats/{chatId}/history");
        history.StatusCode.Should().Be(HttpStatusCode.NotFound, "история чужого урока");
        (await history.Content.ReadAsStringAsync()).Should().Be(missingBody, "чужой урок неотличим от несуществующего — история");
    }

    // ── свой урок без ChildSpace: 404, то же тело, что у чужого (граница — ChildSpace) ─

    [Fact]
    public async Task СвойУрокБезChildSpace_404_ТелоКакУЧужогоРебёнка()
    {
        var (name, id) = await NewSubjectChildAsync("kid-read-nospace");
        var chatId = await CreateLessonAsync(ChildClient(name));
        // Снимаем метку детской области напрямую (тот же приём, что ChildChatAsync):
        // без неё урок для ребёнка неотличим от чужого — даже его собственный
        Sessions.GetById(chatId)!.ChildSpace = null;
        Sessions.SaveSessions();

        var child = ChildClient(name);
        var foreignId = await ChildChatAsync(ChildSpaces.Lessons); // чужой ребёнку: свой чат SecondUsername с ChildSpace
        var foreignBody = await BodyOfAsync(child, $"/api/chats/{foreignId}");
        (await child.GetAsync($"/api/chats/{foreignId}"))
            .StatusCode.Should().Be(HttpStatusCode.NotFound, "чужой ChildSpace-чат — карточка");

        var ownBody = await BodyOfAsync(child, $"/api/chats/{chatId}");
        ownBody.Should().Be(foreignBody, "свой урок без ChildSpace неотличим от чужого — карточка");
        // История — то же побайтное сравнение, что карточка: один статус не ловит подмену
        // тела в ветке ChildSpace GetHistory (минор ревью 9d63412e) — 404 обязан быть тем же
        // телом chat_not_found, что у чужого
        var historyResp = await child.GetAsync($"/api/chats/{chatId}/history");
        historyResp.StatusCode.Should().Be(HttpStatusCode.NotFound, "история своего урока без ChildSpace");
        (await historyResp.Content.ReadAsStringAsync())
            .Should().Be(foreignBody, "свой урок без ChildSpace неотличим от чужого — история");
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
