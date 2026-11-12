using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Access;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Controllers;

/// <summary>
/// Приватный чат при создании (W4-B1a, W4-B1b): POST /api/projects/{pid}/sessions и
/// POST /api/personas/{id}/chats с полем "private": true рождают чат, приватный с первого
/// мгновения — PrivateToUserId стоит в записи ДО регистрации сессии, поэтому окна, в котором
/// чат был бы общим, нет по построению. Ответ — тот же Session (privateToUserId = id
/// вызывающего, excludeFromDossiers = true). Отказ: не общий проект — 409 not_shared_project
/// (тот же текст и код, что у тумблера ChatsController.SetVisibility) и чата в проекте не
/// появилось; читатель — 403 (отказ правом, атрибут RunTurn).
/// Для ручки персон целевой проект — тот же, что у фабрики: проектная персона → её проект
/// (явного projectId в теле не нужно), иначе запросный; статус ответа — прежний контракт
/// ручки (200, SharedProjectPersonaChatTests), в отличие от 201 на sessions.
/// «Не мелькнуть» проверяется живым каналом: SignalR-клиент владельца состоит в группе проекта,
/// редактор создаёт приватный чат, затем идёт маркер, который гарантированно уходит в группу
/// проекта (создание проектной персоны, как в PersonaChangedMemberDeliveryTests). До маркера у
/// владельца не должно быть ни одного сообщения с id нового чата. Мутация «создать общим +
/// SetChatVisibility + EvictForeignViewersAsync» краснеет на ней: chat_visibility_changed уходит
/// в группу проекта.
/// </summary>
[Trait("Category", "Host")]
public class SharedProjectPrivateChatCreateTests : IClassFixture<SharedProjectPrivateChatCreateTests.Host>, IAsyncLifetime
{
    private const string MarkerEvent = "personas_changed";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    private readonly Host _factory;
    private readonly List<HubConnection> _connections = [];

    public SharedProjectPrivateChatCreateTests(Host factory)
    {
        _factory = factory;
        _factory.ExtraConfig[SharedProjectsGate.SwitchKey] = "true";
    }

    /// <summary>Хост с третьим пользователем: в общем проекте нужен второй участник,
    /// не только автор приватного чата.</summary>
    public sealed class Host : TestWebApplicationFactory
    {
        public const string ThirdUsername = "thirduser";
        public const string ThirdPassword = "thirdpassword";

        protected override IEnumerable<(string Username, string Password, string Role)> InitialUsers() =>
            [.. base.InitialUsers(), (ThirdUsername, ThirdPassword, "user")];
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var c in _connections)
            await c.DisposeAsync();
    }

    private HttpClient Owner() => _factory.CreateAuthenticatedClient();

    private HttpClient Member() => _factory.CreateAuthenticatedClient(
        TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);

    private HttpClient SecondMember() => _factory.CreateAuthenticatedClient(Host.ThirdUsername, Host.ThirdPassword);

    private string MemberId() => _factory.Services.GetRequiredService<UserStore>()
        .FindByUsername(TestWebApplicationFactory.SecondUsername)!.Id;

    private static async Task SetFlagAsync(HttpClient client) =>
        (await client.PutAsJsonAsync("/api/feature-flags/shared-projects", new { enabled = true }))
            .EnsureSuccessStatusCode();

    private static string IdOf(string json) =>
        JsonSerializer.Deserialize<JsonElement>(json).GetProperty("id").GetString()!;

    /// <summary>Тело запроса на прово: поле называется "private" — ключевое слово C#, поэтому
    /// тело собирается словарём, а не анонимным типом.</summary>
    private static StringContent Body(params (string Key, object Value)[] fields) =>
        new(JsonSerializer.Serialize(fields.ToDictionary(f => f.Key, f => f.Value)),
            Encoding.UTF8, "application/json");

    /// <summary>Общий проект владельца с двумя участниками-редакторами (второй — третий
    /// пользователь хоста).</summary>
    private async Task<string> SharedProjectAsync()
    {
        var projectId = await PersonalProjectAsync();
        (await Owner().PostAsJsonAsync($"/api/projects/{projectId}/members",
            new { username = Host.ThirdUsername, role = "editor" })).EnsureSuccessStatusCode();
        return projectId;
    }

    /// <summary>Проект владельца с одним участником-редактором (или без участников — role null):
    /// тот же путь, что в SharedProjectChatCreateTests.</summary>
    private async Task<string> PersonalProjectAsync(string? role = "editor")
    {
        var owner = Owner();
        await SetFlagAsync(owner);
        await SetFlagAsync(Member());
        await SetFlagAsync(SecondMember());
        var dir = Path.Combine(_factory.TempDir, "spc_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);

        var created = await owner.PostAsJsonAsync("/api/projects",
            new { name = "Приватное" + Guid.NewGuid().ToString("N")[..6], rootPath = dir });
        created.EnsureSuccessStatusCode();
        var projectId = IdOf(await created.Content.ReadAsStringAsync());

        if (role is not null)
            (await owner.PostAsJsonAsync($"/api/projects/{projectId}/members",
                new { username = TestWebApplicationFactory.SecondUsername, role })).EnsureSuccessStatusCode();
        return projectId;
    }

    /// <summary>Руководитель проекта: проектная персона владельца + make-default (единственная
    /// REST-точка DefaultPersonaId). Без него чат редактора в общем проекте не создаётся
    /// (400 no_project_persona: личная подмена — право владельца).</summary>
    private async Task<string> SetLeaderAsync(string projectId)
    {
        var personaId = await CreatePersonaAsync(Owner(), projectId, "Руководитель-" + Guid.NewGuid().ToString("N")[..6]);
        (await Owner().PostAsJsonAsync($"/api/personas/{personaId}/make-default", new { }))
            .EnsureSuccessStatusCode();
        return personaId;
    }

    private static async Task<string> CreatePersonaAsync(HttpClient client, string? projectId, string name)
    {
        var r = await client.PostAsJsonAsync("/api/personas",
            new { name, scope = projectId is null ? null : "project", projectId, voice = new { } });
        r.EnsureSuccessStatusCode();
        return IdOf(await r.Content.ReadAsStringAsync());
    }

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage r) =>
        JsonSerializer.Deserialize<JsonElement>(await r.Content.ReadAsStringAsync());

    private static async Task<List<string>> ChatIdsAsync(HttpClient client, string projectId) =>
        (await BodyAsync(await client.GetAsync($"/api/projects/{projectId}/sessions")))
            .EnumerateArray().Select(c => c.GetProperty("id").GetString()!).ToList();

    private static async Task<List<string>> PersonaChatIdsAsync(HttpClient client, string personaId) =>
        (await BodyAsync(await client.GetAsync($"/api/personas/{personaId}/chats")))
            .EnumerateArray().Select(c => c.GetProperty("id").GetString()!).ToList();

    /// <summary>Записанный чат — из стора, а не из ответа: проверяется состояние хранилища
    /// после полного запроса.</summary>
    private Session Stored(string sessionId) =>
        _factory.Services.GetRequiredService<SessionManager>().GetById(sessionId)!;

    private async Task<HubConnection> ConnectAsync(string username, string password)
    {
        var token = _factory.GetToken(username, password);
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(_factory.Server.BaseAddress, "hubs/session"), options =>
            {
                options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
                options.AccessTokenProvider = () => Task.FromResult<string?>(token);
            })
            .Build();
        connection.HandshakeTimeout = TimeSpan.FromSeconds(60);
        connection.ServerTimeout = TimeSpan.FromSeconds(60);
        await connection.StartAsync();
        _connections.Add(connection);
        return connection;
    }

    /// <summary>Приёмник группы проекта: собирает сообщения с id наблюдаемого чата и сигнал на
    /// маркер — personas_changed с action «created» (гарантированная рассылка в группу проекта,
    /// образец PersonaChangedMemberDeliveryTests). Руководитель создан ДО подписки соединения,
    /// поэтому «created» в этом канале бывает только от маркера.</summary>
    private sealed class Sink
    {
        private readonly object _gate = new();
        private readonly List<string> _chatIds = [];
        private readonly TaskCompletionSource _marker = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Attach(HubConnection conn)
        {
            conn.On<JsonElement>("message", m =>
            {
                var isMarker = m.TryGetProperty("type", out var t) && t.GetString() == MarkerEvent
                    && m.TryGetProperty("action", out var a) && a.GetString() == "created";
                lock (_gate)
                {
                    if (m.TryGetProperty("sessionId", out var s) && s.GetString() is { Length: > 0 } sid)
                        _chatIds.Add(sid);
                }
                if (isMarker) _marker.TrySetResult();
            });
        }

        public Task Marker => _marker.Task;

        public string[] ChatIds() { lock (_gate) return [.. _chatIds]; }
    }

    [Fact]
    public async Task РедакторСоздаётПриватныйЧат_201ПриватенИСРожденияВнеПаспортов()
    {
        var projectId = await SharedProjectAsync();
        await SetLeaderAsync(projectId);

        var r = await Member().PostAsync($"/api/projects/{projectId}/sessions",
            Body(("mode", "auto"), ("name", "Приватный чат редактора"), ("private", true)));

        r.StatusCode.Should().Be(HttpStatusCode.Created, "флаг private — действие хода, редактору открыто");
        var json = await BodyAsync(r);
        var chatId = json.GetProperty("id").GetString()!;
        json.GetProperty("privateToUserId").GetString().Should().Be(MemberId(),
            "ответ тот же Session: приватность — id вызывающего");
        json.GetProperty("excludeFromDossiers").GetBoolean().Should().BeTrue(
            "приватный чат с рождения исключён из «Истории решений» (тот же смысл, что у тумблера)");

        var stored = Stored(chatId);
        stored.PrivateToUserId.Should().Be(MemberId());
        stored.CreatedByUserId.Should().Be(MemberId(), "автор — тот же вызывающий (S2d-1c не тронут)");
        stored.ExcludeFromDossiers.Should().BeTrue();
    }

    [Fact]
    public async Task ПриватныйЧатНеВиденВладельцуИВторомуУчастнику_АвторВидит()
    {
        var projectId = await SharedProjectAsync();
        await SetLeaderAsync(projectId);

        var created = await Member().PostAsync($"/api/projects/{projectId}/sessions",
            Body(("mode", "auto"), ("private", true)));
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var chatId = IdOf(await created.Content.ReadAsStringAsync());

        (await ChatIdsAsync(Owner(), projectId)).Should().NotContain(chatId,
            "владелец проекта не видит чужой приватный чат (GetVisibleByProject, S2d-4a)");
        (await ChatIdsAsync(SecondMember(), projectId)).Should().NotContain(chatId,
            "второй участник — тоже");
        (await Owner().GetAsync($"/api/chats/{chatId}")).StatusCode.Should().Be(HttpStatusCode.NotFound,
            "и по id: существование чужого приватного не раскрываем");
        // Автор свой приватный видит — список проекта для него не пустой
        (await ChatIdsAsync(Member(), projectId)).Should().Contain(chatId);
    }

    [Fact]
    public async Task БезФлагаЧатОбыченИВиденВсеми()
    {
        var projectId = await SharedProjectAsync();
        await SetLeaderAsync(projectId);

        var created = await Member().PostAsync($"/api/projects/{projectId}/sessions",
            Body(("mode", "auto"), ("name", "Обычный чат")));
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var chatId = IdOf(await created.Content.ReadAsStringAsync());

        Stored(chatId).PrivateToUserId.Should().BeNull("поля private в запросе нет — поведение прежнее");
        (await ChatIdsAsync(Owner(), projectId)).Should().Contain(chatId,
            "обычный чат виден всем: иначе проверка приватности была бы зелёной на пустоте");
        (await ChatIdsAsync(SecondMember(), projectId)).Should().Contain(chatId);
    }

    [Fact]
    public async Task ЛичныйПроектСФлагомPrivate_409НеSharedProjectИБезНовогоЧата()
    {
        var projectId = await PersonalProjectAsync(role: null);
        var before = await ChatIdsAsync(Owner(), projectId);

        var r = await Owner().PostAsync($"/api/projects/{projectId}/sessions",
            Body(("mode", "auto"), ("private", true)));

        r.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = await BodyAsync(r);
        body.GetProperty("code").GetString().Should().Be("not_shared_project");
        body.GetProperty("error").GetString().Should().Be("Приватным можно сделать только чат общего проекта",
            "тот же текст и код, что у тумблера ChatsController.SetVisibility");
        (await ChatIdsAsync(Owner(), projectId)).Should().BeEquivalentTo(before,
            "отказанное создание не оставило в проекте чата");
    }

    [Fact]
    public async Task ЧитательСФлагомPrivate_403ИБезНовогоЧата()
    {
        var projectId = await PersonalProjectAsync(role: "reader");
        var before = await ChatIdsAsync(Owner(), projectId);

        var r = await Member().PostAsync($"/api/projects/{projectId}/sessions",
            Body(("mode", "auto"), ("private", true)));

        r.StatusCode.Should().Be(HttpStatusCode.Forbidden, "отказ различается правом, а не флагом");
        (await BodyAsync(r)).GetProperty("code").GetString().Should().Be(ProjectScopeAttribute.ErrorCode);
        (await ChatIdsAsync(Owner(), projectId)).Should().BeEquivalentTo(before);
    }

    [Fact]
    public async Task ПриватныйЧатНеМелькаетВГруппеПроекта()
    {
        var projectId = await SharedProjectAsync();
        // Руководитель создан до подписки: его «created» не шумит в приёмнике
        await SetLeaderAsync(projectId);

        var conn = await ConnectAsync(TestWebApplicationFactory.TestUsername, TestWebApplicationFactory.TestPassword);
        var sink = new Sink();
        // Приёмник и группа проекта — ДО создания чата: сообщение, ушедшее до подписки, было бы
        // просто не увидено, и проверка стала бы зелёной на пустоте
        sink.Attach(conn);
        await conn.InvokeAsync("JoinProject", projectId);

        var created = await Member().PostAsync($"/api/projects/{projectId}/sessions",
            Body(("mode", "auto"), ("private", true)));
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var chatId = IdOf(await created.Content.ReadAsStringAsync());

        // Маркер: событие, которое гарантированно уходит в группу проекта (тот же путь, что в
        // PersonaChangedMemberDeliveryTests). Он же — доказательство, что канал живой.
        await CreatePersonaAsync(Owner(), projectId, "Маркер-" + Guid.NewGuid().ToString("N")[..6]);
        await sink.Marker.WaitAsync(Wait);

        // Не «ни одного сообщения вообще» (в группе проекта легитимны чужие события), а именно
        // этого чата: ни chat_visibility_changed, ни какого-либо иного сообщения с его id
        sink.ChatIds().Should().NotContain(chatId,
            "приватный чат ни на миг не стал общим: до маркера в группе проекта нет ни одного сообщения с его id");
    }

    // === POST /api/personas/{id}/chats (W4-B1b) ===
    // Та же приватность при создании, другая дверь: чат ведётся от лица проектной персоны,
    // её проект — целевой сам (явного projectId в теле не нужно). Статус успеха — прежний
    // контракт ручки (200 OK, SharedProjectPersonaChatTests), не 201 как у sessions: шаг его
    // не меняет.

    /// <summary>Проектная персона общего проекта: чат редактора от её лица.</summary>
    private async Task<(string ProjectId, string PersonaId)> SharedProjectWithProjectPersonaAsync()
    {
        var projectId = await SharedProjectAsync();
        var personaId = await CreatePersonaAsync(Owner(), projectId, "Руководитель-" + Guid.NewGuid().ToString("N")[..6]);
        return (projectId, personaId);
    }

    [Fact]
    public async Task РедакторСоздаётПриватныйЧатСПроектнойПерсоной_ПриватенИСРождения()
    {
        var (projectId, personaId) = await SharedProjectWithProjectPersonaAsync();

        var r = await Member().PostAsync($"/api/personas/{personaId}/chats",
            Body(("mode", "auto"), ("name", "Приватный чат редактора"), ("private", true)));

        r.StatusCode.Should().Be(HttpStatusCode.OK,
            "флаг private — действие хода, редактору открыто; статус успеха ручки не менялся (200, как без флага)");
        var json = await BodyAsync(r);
        var chatId = json.GetProperty("id").GetString()!;
        json.GetProperty("privateToUserId").GetString().Should().Be(MemberId(),
            "ответ тот же Session: приватность — id вызывающего");
        json.GetProperty("excludeFromDossiers").GetBoolean().Should().BeTrue(
            "приватный чат с рождения исключён из «Истории решений» (тот же смысл, что у тумблера)");

        var stored = Stored(chatId);
        stored.ProjectId.Should().Be(projectId, "целевой проект — проект проектной персоны");
        stored.PrivateToUserId.Should().Be(MemberId());
        stored.CreatedByUserId.Should().Be(MemberId(), "автор — тот же вызывающий (S2d-1c не тронут)");
        stored.ExcludeFromDossiers.Should().BeTrue();
    }

    [Fact]
    public async Task ПриватныйЧатСПерсонойНеВиденВладельцуИВторомуУчастнику_АвторВидит()
    {
        var (projectId, personaId) = await SharedProjectWithProjectPersonaAsync();

        var created = await Member().PostAsync($"/api/personas/{personaId}/chats",
            Body(("mode", "auto"), ("private", true)));
        created.StatusCode.Should().Be(HttpStatusCode.OK);
        var chatId = IdOf(await created.Content.ReadAsStringAsync());

        (await ChatIdsAsync(Owner(), projectId)).Should().NotContain(chatId,
            "владелец проекта не видит чужой приватный чат (GetVisibleByProject, S2d-4a)");
        (await PersonaChatIdsAsync(Owner(), personaId)).Should().NotContain(chatId,
            "и в списке чатов персоны: viewer — вызывающий (И-5)");
        (await ChatIdsAsync(SecondMember(), projectId)).Should().NotContain(chatId,
            "второй участник — тоже");
        (await Owner().GetAsync($"/api/chats/{chatId}")).StatusCode.Should().Be(HttpStatusCode.NotFound,
            "и по id: существование чужого приватного не раскрываем");
        // Автор свой приватный видит — список для него не пустой
        (await PersonaChatIdsAsync(Member(), personaId)).Should().Contain(chatId);
    }

    [Fact]
    public async Task БезФлагаЧатСПерсонойОбыченИВиденВсеми()
    {
        var (projectId, personaId) = await SharedProjectWithProjectPersonaAsync();

        var created = await Member().PostAsync($"/api/personas/{personaId}/chats",
            Body(("mode", "auto"), ("name", "Обычный чат")));
        created.StatusCode.Should().Be(HttpStatusCode.OK);
        var chatId = IdOf(await created.Content.ReadAsStringAsync());

        Stored(chatId).PrivateToUserId.Should().BeNull("поля private в запросе нет — поведение прежнее");
        Stored(chatId).ExcludeFromDossiers.Should().BeFalse();
        (await ChatIdsAsync(Owner(), projectId)).Should().Contain(chatId,
            "обычный чат виден всем: иначе проверка приватности была бы зелёной на пустоте");
        (await PersonaChatIdsAsync(Owner(), personaId)).Should().Contain(chatId);
        (await ChatIdsAsync(SecondMember(), projectId)).Should().Contain(chatId);
    }

    [Fact]
    public async Task ПриватныйЧатСПерсонойНеМелькаетВГруппеПроекта()
    {
        var (projectId, personaId) = await SharedProjectWithProjectPersonaAsync();

        var conn = await ConnectAsync(TestWebApplicationFactory.TestUsername, TestWebApplicationFactory.TestPassword);
        var sink = new Sink();
        // Приёмник и группа проекта — ДО создания чата: сообщение, ушедшее до подписки, было бы
        // просто не увидено, и проверка стала бы зелёной на пустоте. Персона чата создана до
        // подписки — её «created» не шумит в приёмнике, маркером остаётся только следующая персона.
        sink.Attach(conn);
        await conn.InvokeAsync("JoinProject", projectId);

        var created = await Member().PostAsync($"/api/personas/{personaId}/chats",
            Body(("mode", "auto"), ("private", true)));
        created.StatusCode.Should().Be(HttpStatusCode.OK);
        var chatId = IdOf(await created.Content.ReadAsStringAsync());

        // Маркер: событие, которое гарантированно уходит в группу проекта (тот же путь, что в
        // PersonaChangedMemberDeliveryTests). Он же — доказательство, что канал живой.
        await CreatePersonaAsync(Owner(), projectId, "Маркер-" + Guid.NewGuid().ToString("N")[..6]);
        await sink.Marker.WaitAsync(Wait);

        sink.ChatIds().Should().NotContain(chatId,
            "приватный чат ни на миг не стал общим: до маркера в группе проекта нет ни одного сообщения с его id");
    }

    [Fact]
    public async Task ПерсонаВНеобщемПроектеСФлагомPrivate_409НеSharedProjectИБезНовогоЧата()
    {
        // Проект без участников: персона в нём — проектная, но чат её проекта не ляжет в общий
        var projectId = await PersonalProjectAsync(role: null);
        var personaId = await CreatePersonaAsync(Owner(), projectId, "Одинокая-" + Guid.NewGuid().ToString("N")[..6]);
        var before = await ChatIdsAsync(Owner(), projectId);

        var r = await Owner().PostAsync($"/api/personas/{personaId}/chats",
            Body(("mode", "auto"), ("private", true)));

        r.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = await BodyAsync(r);
        body.GetProperty("code").GetString().Should().Be("not_shared_project");
        body.GetProperty("error").GetString().Should().Be("Приватным можно сделать только чат общего проекта",
            "тот же текст и код, что у тумблера ChatsController.SetVisibility");
        (await ChatIdsAsync(Owner(), projectId)).Should().BeEquivalentTo(before,
            "отказанное создание не оставило в проекте чата: отказ до фабрики, без побочных эффектов");
    }

    [Fact]
    public async Task ПерсонаЛичногоПроектаСОбщимProjectIdВТеле_409ЦелевойПроектБерётсяИзПерсоны()
    {
        // Тело называет общий проект, но целевой проект гейта — проект проектной персоны
        // (PersonasController.cs:481-483), не req.ProjectId: персона живёт в личном проекте
        // владельца без участников (IsShared — «есть участники», ProjectAccess.cs:95), и отказ
        // идёт по проекту персоны. Резолв атрибута — тоже по персоне: вызывающий — её владелец.
        var personalProjectId = await PersonalProjectAsync(role: null);
        var personaId = await CreatePersonaAsync(Owner(), personalProjectId, "Двойная-" + Guid.NewGuid().ToString("N")[..6]);
        var sharedProjectId = await SharedProjectAsync();
        var beforePersonal = await ChatIdsAsync(Owner(), personalProjectId);
        var beforeShared = await ChatIdsAsync(Owner(), sharedProjectId);
        var beforePersonaChats = await PersonaChatIdsAsync(Owner(), personaId);

        var r = await Owner().PostAsync($"/api/personas/{personaId}/chats",
            Body(("mode", "auto"), ("private", true), ("projectId", sharedProjectId)));

        r.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = await BodyAsync(r);
        body.GetProperty("code").GetString().Should().Be("not_shared_project");
        body.GetProperty("error").GetString().Should().Be("Приватным можно сделать только чат общего проекта",
            "тот же текст и код, что у тумблера ChatsController.SetVisibility");
        (await ChatIdsAsync(Owner(), personalProjectId)).Should().BeEquivalentTo(beforePersonal,
            "отказанное создание не оставило чата в проекте персоны");
        (await ChatIdsAsync(Owner(), sharedProjectId)).Should().BeEquivalentTo(beforeShared,
            "и в общем проекте из тела: отказ до фабрики, чата там не родилось ни при каком резолве");
        (await PersonaChatIdsAsync(Owner(), personaId)).Should().BeEquivalentTo(beforePersonaChats);
    }

    [Fact]
    public async Task ГлобальнаяПерсонаСФлагомPrivate_409НеSharedProjectИБезНовогоЧата()
    {
        // Глобальной персоне не из чего брать свой проект: целевой — req.ProjectId
        // (PersonasController.cs:481-483); в теле его нет → null → IsShared(null) = false
        // (ProjectAccess.cs:95). До гейта доходит: резолв атрибута глобальной персоны — по
        // владению (ProjectAccessService.cs:70-72), вызывающий — владелец персоны.
        var personaId = await CreatePersonaAsync(Owner(), null, "Глобальная-" + Guid.NewGuid().ToString("N")[..6]);
        var before = await PersonaChatIdsAsync(Owner(), personaId);

        var r = await Owner().PostAsync($"/api/personas/{personaId}/chats",
            Body(("mode", "auto"), ("private", true)));

        r.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = await BodyAsync(r);
        body.GetProperty("code").GetString().Should().Be("not_shared_project");
        body.GetProperty("error").GetString().Should().Be("Приватным можно сделать только чат общего проекта",
            "тот же текст и код, что у тумблера ChatsController.SetVisibility");
        (await PersonaChatIdsAsync(Owner(), personaId)).Should().BeEquivalentTo(before,
            "отказ до фабрики: чата вне проекта (куда ушла бы глобальная персона без гейта) нет");
    }

    [Fact]
    public async Task ПриватныйЧатСПерсонойПоIdНеОтличимОтНесуществующего_АвторПолучает()
    {
        var (_, personaId) = await SharedProjectWithProjectPersonaAsync();

        var created = await Member().PostAsync($"/api/personas/{personaId}/chats",
            Body(("mode", "auto"), ("private", true)));
        created.StatusCode.Should().Be(HttpStatusCode.OK);
        var chatId = IdOf(await created.Content.ReadAsStringAsync());

        // Отказ второму участнику неотличим от несуществующего: тот же код, что у заведомо
        // чужого id — по одному ответу нельзя сказать, был ли такой чат (ChatsController.cs:130:
        // невидимый и несуществующий — один NotFound)
        var ghost = Guid.NewGuid().ToString("N");
        var forSecondMember = await SecondMember().GetAsync($"/api/chats/{chatId}");
        var forGhost = await SecondMember().GetAsync($"/api/chats/{ghost}");
        forSecondMember.StatusCode.Should().Be(forGhost.StatusCode,
            "отказ неотличим: существование чужого приватного не раскрываем");
        forSecondMember.StatusCode.Should().Be(HttpStatusCode.NotFound);

        // Автор свой приватный получает по id: отказ чужим — фильтр видимости, не отсутствие записи
        var forAuthor = await Member().GetAsync($"/api/chats/{chatId}");
        forAuthor.StatusCode.Should().Be(HttpStatusCode.OK);
        (await BodyAsync(forAuthor)).GetProperty("id").GetString().Should().Be(chatId);
    }
}
