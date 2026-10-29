using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Hubs;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Hubs;

/// <summary>
/// S1-4: отзыв доступа немедленно выкидывает живые соединения участника из групп хаба
/// (разрез §2.2): после «убрать»/«покинуть» он не получает событий project_{id} и {sessionId},
/// владелец продолжает; выкидываются все вкладки; повторный JoinProject — отказ (S0-6);
/// при смене роли группа остаётся и приходит событие обновления прав (К3 плана v2).
///
/// Отрицательная проверка без Task.Delay. Проба в группу и адресная проба тому же соединению
/// пишутся в один исходящий канал FIFO: если бы соединение осталось в группе, групповая проба
/// пришла бы раньше адресной. Ждём адресную через TaskCompletionSource и смотрим, что групповых
/// до неё не было. Завершённость самого выкидывания доказывает личное событие
/// project_access_revoked: сервер отправляет его после снятия всех групп того же человека.
/// </summary>
public class RevokedMemberHubTests : IClassFixture<TestWebApplicationFactory>, IAsyncLifetime
{
    // Событие-проба вне протокола (как в SessionHubAccessTests): проверяется доставка в группу,
    // а не конкретный месседж, и не считаются чужие реплеи при входе
    private const string ProbeEvent = "test_group_probe";

    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    private readonly TestWebApplicationFactory _factory;
    private readonly List<HubConnection> _connections = [];

    public RevokedMemberHubTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
        // Рубильник инстанса включаем до первого запроса (хост собирается лениво)
        _factory.ExtraConfig[SharedProjectsGate.SwitchKey] = "true";
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

    private string SecondId => _factory.Services
        .GetRequiredService<UserStore>().FindByUsername(TestWebApplicationFactory.SecondUsername)!.Id;

    private IHubContext<SessionHub> Hub() => _factory.Services.GetRequiredService<IHubContext<SessionHub>>();

    private static async Task SetFlagAsync(HttpClient client, bool on) =>
        (await client.PutAsJsonAsync("/api/feature-flags/shared-projects", new { enabled = on }))
            .EnsureSuccessStatusCode();

    private async Task<string> CreateProjectAsync(HttpClient owner)
    {
        var dir = Path.Combine(_factory.TempDir, "revoked_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var response = await owner.PostAsJsonAsync("/api/projects", new { name = "Общий", rootPath = dir });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    private async Task<string> CreateSessionAsync(HttpClient owner, string projectId)
    {
        var response = await owner.PostAsJsonAsync($"/api/projects/{projectId}/sessions", new { mode = "auto" });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    /// <summary>Общий проект с чатом и участником-editor: базовая обстановка всех четырёх тестов.</summary>
    private async Task<(string ProjectId, string SessionId)> SharedProjectWithMemberAsync()
    {
        var owner = Owner();
        await SetFlagAsync(owner, true);
        await SetFlagAsync(Member(), true);
        var projectId = await CreateProjectAsync(owner);
        var sessionId = await CreateSessionAsync(owner, projectId);
        (await owner.PostAsJsonAsync($"/api/projects/{projectId}/members",
            new { username = TestWebApplicationFactory.SecondUsername, role = "editor" })).EnsureSuccessStatusCode();
        return (projectId, sessionId);
    }

    private async Task<HubConnection> ConnectAsync(string username, string password)
    {
        var token = _factory.GetToken(username, password);
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(_factory.Server.BaseAddress, "hubs/session"), options =>
            {
                // Как в SessionHubAccessTests: под TestServer WebSocket'а нет, только LongPolling
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

    /// <summary>
    /// Приёмник событий соединения: метки полученных payload'ов (поле "probe") и сигналы
    /// на первую полученную каждой метки. Порядок меток — порядок исходящего канала,
    /// на нём держится отрицательная проверка.
    /// </summary>
    private sealed class Sink
    {
        private readonly object _gate = new();
        private readonly List<string> _got = [];
        private readonly Dictionary<string, TaskCompletionSource<string>> _firsts = [];

        public void Attach(HubConnection conn, string eventName) => conn.On<JsonElement>(eventName, payload =>
        {
            var label = payload.TryGetProperty("probe", out var p) ? p.GetString() ?? "?" : "?";
            lock (_gate)
            {
                _got.Add(label);
                if (!_firsts.TryGetValue(label, out var tcs))
                {
                    tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _firsts[label] = tcs;
                }
                tcs.TrySetResult(label);
            }
        });

        public Task<string> FirstOf(string label)
        {
            lock (_gate)
            {
                if (!_firsts.TryGetValue(label, out var tcs))
                {
                    tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _firsts[label] = tcs;
                }
                return tcs.Task;
            }
        }

        public string[] Got()
        {
            lock (_gate) return [.. _got];
        }
    }

    private static TaskCompletionSource<JsonElement> NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Fact]
    public async Task Remove_ОтозванныйНеПолучаетСобытияПроектаИЧата_ВладелецПолучает()
    {
        var (projectId, sessionId) = await SharedProjectWithMemberAsync();

        var owner = await ConnectAsync(TestWebApplicationFactory.TestUsername, TestWebApplicationFactory.TestPassword);
        await owner.InvokeAsync("JoinProject", projectId);
        await owner.InvokeAsync("JoinSession", sessionId);
        var ownerSink = new Sink();
        ownerSink.Attach(owner, ProbeEvent);

        var revoked = await ConnectAsync(TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);
        await revoked.InvokeAsync("JoinProject", projectId);
        await revoked.InvokeAsync("JoinSession", sessionId);
        await revoked.InvokeAsync("JoinUser", SecondId);
        var revokedSink = new Sink();
        revokedSink.Attach(revoked, ProbeEvent);
        var revokedSignal = NewSignal();
        revoked.On<JsonElement>("project_access_revoked", p => revokedSignal.TrySetResult(p));

        // Действие: владелец убирает участника
        (await Owner().DeleteAsync($"/api/projects/{projectId}/members/{SecondId}")).EnsureSuccessStatusCode();

        // Личное событие К3 (план v2): пришло в группу user_{id} и несёт проект и имя владельца.
        // Отправлено сервером после снятия всех групп того же человека — его получение и есть
        // доказательство завершённого выкидывания
        var payload = await revokedSignal.Task.WaitAsync(Wait);
        payload.GetProperty("projectId").GetString().Should().Be(projectId);
        payload.GetProperty("ownerDisplayName").ValueKind.Should().NotBe(JsonValueKind.Undefined);

        // Проба в оба типа групп, затем адресная — тому же соединению. Исходящий канал FIFO:
        // если бы соединение осталось в группе, групповые пробы пришли бы раньше адресной
        await Hub().Clients.Group("project_" + projectId).SendAsync(ProbeEvent, new { probe = "project" });
        await Hub().Clients.Group(sessionId).SendAsync(ProbeEvent, new { probe = "session" });
        await Hub().Clients.Client(revoked.ConnectionId!).SendAsync(ProbeEvent, new { probe = "direct" });

        (await revokedSink.FirstOf("direct").WaitAsync(Wait)).Should().Be("direct",
            "живое соединение обязано слушать: его молчание объясняется только выходом из групп");
        // Equal(params[]) съел бы строку-причину как второй ожидаемый элемент — ожидаемое
        // передаём массивом, причину отдельным параметром
        revokedSink.Got().Should().Equal(new[] { "direct" },
            "после отзыва живое соединение не получает ни событий проекта, ни событий чата");

        (await ownerSink.FirstOf("project").WaitAsync(Wait)).Should().Be("project");
        (await ownerSink.FirstOf("session").WaitAsync(Wait)).Should().Be("session");

        // Повторный вход отозванного — отказ точки доступа (S0-6): группа закрыта не только
        // выкидыванием, но и правом
        var act = () => revoked.InvokeAsync("JoinProject", projectId);
        (await act.Should().ThrowAsync<HubException>()).WithMessage("*Доступ запрещён*");
    }

    [Fact]
    public async Task Remove_ВыкидываютсяВсеВкладкиОдногоУчастника()
    {
        var (projectId, sessionId) = await SharedProjectWithMemberAsync();

        var owner = await ConnectAsync(TestWebApplicationFactory.TestUsername, TestWebApplicationFactory.TestPassword);
        await owner.InvokeAsync("JoinProject", projectId);
        var ownerSink = new Sink();
        ownerSink.Attach(owner, ProbeEvent);

        // Две вкладки одного человека — обе в группе проекта и чата
        var tab1 = await ConnectAsync(TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);
        var tab2 = await ConnectAsync(TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);
        foreach (var tab in new[] { tab1, tab2 })
        {
            await tab.InvokeAsync("JoinProject", projectId);
            await tab.InvokeAsync("JoinSession", sessionId);
            await tab.InvokeAsync("JoinUser", SecondId);
        }
        var sink1 = new Sink();
        sink1.Attach(tab1, ProbeEvent);
        var sink2 = new Sink();
        sink2.Attach(tab2, ProbeEvent);
        var revokedSignal = NewSignal();
        tab2.On<JsonElement>("project_access_revoked", p => revokedSignal.TrySetResult(p));

        (await Owner().DeleteAsync($"/api/projects/{projectId}/members/{SecondId}")).EnsureSuccessStatusCode();
        // Событие шлётся после выкидывания всех соединений — приход на любой вкладке
        // значит, что сняты все
        await revokedSignal.Task.WaitAsync(Wait);

        await Hub().Clients.Group("project_" + projectId).SendAsync(ProbeEvent, new { probe = "project" });
        await Hub().Clients.Client(tab1.ConnectionId!).SendAsync(ProbeEvent, new { probe = "direct1" });
        await Hub().Clients.Client(tab2.ConnectionId!).SendAsync(ProbeEvent, new { probe = "direct2" });

        (await sink1.FirstOf("direct1").WaitAsync(Wait)).Should().Be("direct1");
        sink1.Got().Should().Equal(new[] { "direct1" }, "первая вкладка выкинута из группы проекта");
        (await sink2.FirstOf("direct2").WaitAsync(Wait)).Should().Be("direct2");
        sink2.Got().Should().Equal(new[] { "direct2" }, "вторая вкладка того же человека выкинута тоже");

        (await ownerSink.FirstOf("project").WaitAsync(Wait)).Should().Be("project",
            "выкидываются соединения отозванного, а не все соединения группы");
    }

    [Fact]
    public async Task Leave_ПокинувшийНеПолучаетСобытия_ВладелецПродолжает()
    {
        var (projectId, sessionId) = await SharedProjectWithMemberAsync();

        var owner = await ConnectAsync(TestWebApplicationFactory.TestUsername, TestWebApplicationFactory.TestPassword);
        await owner.InvokeAsync("JoinProject", projectId);
        await owner.InvokeAsync("JoinSession", sessionId);
        var ownerSink = new Sink();
        ownerSink.Attach(owner, ProbeEvent);

        var left = await ConnectAsync(TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);
        await left.InvokeAsync("JoinProject", projectId);
        await left.InvokeAsync("JoinSession", sessionId);
        await left.InvokeAsync("JoinUser", SecondId);
        var leftSink = new Sink();
        leftSink.Attach(left, ProbeEvent);
        var leftSignal = NewSignal();
        left.On<JsonElement>("project_access_revoked", p => leftSignal.TrySetResult(p));

        // Действие: участник сам покидает проект
        (await Member().DeleteAsync($"/api/projects/{projectId}/members/me")).EnsureSuccessStatusCode();
        await leftSignal.Task.WaitAsync(Wait);

        await Hub().Clients.Group("project_" + projectId).SendAsync(ProbeEvent, new { probe = "project" });
        await Hub().Clients.Group(sessionId).SendAsync(ProbeEvent, new { probe = "session" });
        await Hub().Clients.Client(left.ConnectionId!).SendAsync(ProbeEvent, new { probe = "direct" });

        (await leftSink.FirstOf("direct").WaitAsync(Wait)).Should().Be("direct");
        leftSink.Got().Should().Equal(new[] { "direct" }, "покинувший проект не остаётся ни в группе проекта, ни в группе чата");
        (await ownerSink.FirstOf("project").WaitAsync(Wait)).Should().Be("project");
        (await ownerSink.FirstOf("session").WaitAsync(Wait)).Should().Be("session");
    }

    [Fact]
    public async Task СменаРоли_ГруппаОстаётся_ПриходитСобытиеОбновленияПрав()
    {
        var (projectId, _) = await SharedProjectWithMemberAsync();

        var member = await ConnectAsync(TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);
        await member.InvokeAsync("JoinProject", projectId);
        var memberSink = new Sink();
        memberSink.Attach(member, ProbeEvent);
        var roleSignal = NewSignal();
        member.On<JsonElement>("project_members_changed", p => roleSignal.TrySetResult(p));

        // Действие: редактор понижен до читателя
        (await Owner().PutAsJsonAsync($"/api/projects/{projectId}/members/{SecondId}", new { role = "reader" }))
            .EnsureSuccessStatusCode();

        // К3: событие обновления прав приходит участнику (он остаётся в группе проекта),
        // и после него проба группы ему по-прежнему доходит — доступ не отозван
        var payload = await roleSignal.Task.WaitAsync(Wait);
        payload.GetProperty("projectId").GetString().Should().Be(projectId);

        await Hub().Clients.Group("project_" + projectId).SendAsync(ProbeEvent, new { probe = "after-role" });
        (await memberSink.FirstOf("after-role").WaitAsync(Wait)).Should().Be("after-role",
            "при смене роли группа остаётся: участник продолжает получать события проекта");
    }
}
