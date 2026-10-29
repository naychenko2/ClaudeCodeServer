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
/// S1-4b (хвосты №1 и №2 ревью R-S1-4): хаб-следствие операций членства полное.
/// 1. Отозванный (Remove и Leave) с открытой вкладкой «Логи» дев-сервера проекта выкинут
///    и из группы логов preview_{projectId}:{serviceId}: после отзыва живое соединение
///    не получает ни одного проектного потока, владелец — получает.
/// 2. Add/Remove/Leave шлют в группу проекта project_members_changed — список участников
///    обновляется без перезагрузки страницы; при Remove событие отозванному не долюет,
///    оно отправляется после его выкидывания из группы.
///
/// Отрицательная проверка без Task.Delay, тем же методом, что в RevokedMemberHubTests:
/// проба в группу и адресная проба тому же соединению пишутся в один исходящий канал FIFO;
/// если бы соединение осталось в группе, групповая проба пришла бы раньше адресной.
/// Завершённость выкидывания доказывает личное событие project_access_revoked.
/// Живого сервиса в тесте нет намеренно: группа логов существует от самой подписки, а
/// реальный поток логов идёт в ту же группу (DevServerService.LogGroup) — проверяется
/// именно membership, не процесс.
/// </summary>
public class ProjectMembersEventsHubTests : IClassFixture<TestWebApplicationFactory>, IAsyncLifetime
{
    // Событие-проба вне протокола (как в RevokedMemberHubTests): проверяется доставка в группу
    private const string ProbeEvent = "test_group_probe";

    // Имя сервиса произвольное: ни один реестр его не знает, группа создаётся подпиской
    private const string ServiceId = "web";

    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    private readonly TestWebApplicationFactory _factory;
    private readonly List<HubConnection> _connections = [];

    public ProjectMembersEventsHubTests(TestWebApplicationFactory factory)
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

    /// <summary>Общий проект с включённым рубильником у обоих; addMember — с участником-editor.</summary>
    private async Task<string> SharedProjectAsync(bool addMember = true)
    {
        var owner = Owner();
        await SetFlagAsync(owner, true);
        await SetFlagAsync(Member(), true);
        var dir = Path.Combine(_factory.TempDir, "members_events_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var response = await owner.PostAsJsonAsync("/api/projects", new { name = "Общий", rootPath = dir });
        response.EnsureSuccessStatusCode();
        var projectId = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        if (addMember)
            (await owner.PostAsJsonAsync($"/api/projects/{projectId}/members",
                new { username = TestWebApplicationFactory.SecondUsername, role = "editor" })).EnsureSuccessStatusCode();
        return projectId;
    }

    private async Task<HubConnection> ConnectAsync(string username, string password)
    {
        var token = _factory.GetToken(username, password);
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(_factory.Server.BaseAddress, "hubs/session"), options =>
            {
                // Как в RevokedMemberHubTests: под TestServer WebSocket'а нет, только LongPolling
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

    /// <summary>Приёмник событий соединения: метки полученных payload'ов (поле "probe") и сигналы
    /// на первую полученную каждой метки. Порядок меток — порядок исходящего канала.</summary>
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
    public async Task Remove_ОтозванныйСОткрытымиЛогамиНеПолучаетПробуГруппы_ВладелецПолучает()
    {
        var projectId = await SharedProjectAsync();

        var owner = await ConnectAsync(TestWebApplicationFactory.TestUsername, TestWebApplicationFactory.TestPassword);
        await owner.InvokeAsync("JoinProject", projectId);
        await owner.InvokeAsync("JoinPreviewLog", projectId, ServiceId);
        var ownerSink = new Sink();
        ownerSink.Attach(owner, ProbeEvent);

        var revoked = await ConnectAsync(TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);
        await revoked.InvokeAsync("JoinProject", projectId);
        await revoked.InvokeAsync("JoinPreviewLog", projectId, ServiceId);
        await revoked.InvokeAsync("JoinUser", SecondId);
        var revokedSink = new Sink();
        revokedSink.Attach(revoked, ProbeEvent);
        var revokedSignal = NewSignal();
        revoked.On<JsonElement>("project_access_revoked", p => revokedSignal.TrySetResult(p));

        // Действие: владелец убирает участника
        (await Owner().DeleteAsync($"/api/projects/{projectId}/members/{SecondId}")).EnsureSuccessStatusCode();

        // Личное событие шлётся после снятия всех групп того же человека — его получение
        // и есть доказательство завершённого выкидывания
        await revokedSignal.Task.WaitAsync(Wait);

        // Имя группы — литерал, а не серверный хелпер: тест держит контракт формата
        // между рассылкой логов и выкидыванием, а не следует за ним
        await Hub().Clients.Group($"preview_{projectId}:{ServiceId}").SendAsync(ProbeEvent, new { probe = "preview" });
        await Hub().Clients.Client(revoked.ConnectionId!).SendAsync(ProbeEvent, new { probe = "direct" });

        (await revokedSink.FirstOf("direct").WaitAsync(Wait)).Should().Be("direct",
            "живое соединение обязано слушать: его молчание объясняется только выходом из групп");
        revokedSink.Got().Should().Equal(new[] { "direct" },
            "после отзыва живое соединение не получает ни одного проектного потока — ни группы логов");

        (await ownerSink.FirstOf("preview").WaitAsync(Wait)).Should().Be("preview",
            "соединения выкидываются у отозванного, а не все соединения группы");
    }

    [Fact]
    public async Task Leave_ПокинувшийСОткрытымиЛогамиНеПолучаетПробуГруппы_ВладелецПолучает()
    {
        var projectId = await SharedProjectAsync();

        var owner = await ConnectAsync(TestWebApplicationFactory.TestUsername, TestWebApplicationFactory.TestPassword);
        await owner.InvokeAsync("JoinProject", projectId);
        await owner.InvokeAsync("JoinPreviewLog", projectId, ServiceId);
        var ownerSink = new Sink();
        ownerSink.Attach(owner, ProbeEvent);

        var left = await ConnectAsync(TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);
        await left.InvokeAsync("JoinProject", projectId);
        await left.InvokeAsync("JoinPreviewLog", projectId, ServiceId);
        await left.InvokeAsync("JoinUser", SecondId);
        var leftSink = new Sink();
        leftSink.Attach(left, ProbeEvent);
        var leftSignal = NewSignal();
        left.On<JsonElement>("project_access_revoked", p => leftSignal.TrySetResult(p));

        // Действие: участник сам покидает проект
        (await Member().DeleteAsync($"/api/projects/{projectId}/members/me")).EnsureSuccessStatusCode();
        await leftSignal.Task.WaitAsync(Wait);

        await Hub().Clients.Group($"preview_{projectId}:{ServiceId}").SendAsync(ProbeEvent, new { probe = "preview" });
        await Hub().Clients.Client(left.ConnectionId!).SendAsync(ProbeEvent, new { probe = "direct" });

        (await leftSink.FirstOf("direct").WaitAsync(Wait)).Should().Be("direct");
        leftSink.Got().Should().Equal(new[] { "direct" },
            "покинувший проект выкинут и из группы логов дев-сервера этого проекта");

        (await ownerSink.FirstOf("preview").WaitAsync(Wait)).Should().Be("preview");
    }

    [Fact]
    public async Task Add_ГруппаПроектаПолучаетProjectMembersChanged()
    {
        // Проект без участника: само действие Add и есть проверяемое
        var projectId = await SharedProjectAsync(addMember: false);

        var owner = await ConnectAsync(TestWebApplicationFactory.TestUsername, TestWebApplicationFactory.TestPassword);
        await owner.InvokeAsync("JoinProject", projectId);
        var ownerChanged = NewSignal();
        owner.On<JsonElement>("project_members_changed", p => ownerChanged.TrySetResult(p));

        (await Owner().PostAsJsonAsync($"/api/projects/{projectId}/members",
            new { username = TestWebApplicationFactory.SecondUsername, role = "editor" })).EnsureSuccessStatusCode();

        // К3: событие доходит владельцу без перезагрузки страницы и несёт проект
        var payload = await ownerChanged.Task.WaitAsync(Wait);
        payload.GetProperty("projectId").GetString().Should().Be(projectId);
    }

    [Fact]
    public async Task Remove_ГруппаПроектаПолучаетProjectMembersChanged_ОтозванномуНет()
    {
        var projectId = await SharedProjectAsync();

        var owner = await ConnectAsync(TestWebApplicationFactory.TestUsername, TestWebApplicationFactory.TestPassword);
        await owner.InvokeAsync("JoinProject", projectId);
        var ownerChanged = NewSignal();
        owner.On<JsonElement>("project_members_changed", p => ownerChanged.TrySetResult(p));

        var revoked = await ConnectAsync(TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);
        await revoked.InvokeAsync("JoinProject", projectId);
        await revoked.InvokeAsync("JoinUser", SecondId);
        var revokedChanged = NewSignal();
        revoked.On<JsonElement>("project_members_changed", p => revokedChanged.TrySetResult(p));
        var revokedSink = new Sink();
        revokedSink.Attach(revoked, ProbeEvent);

        (await Owner().DeleteAsync($"/api/projects/{projectId}/members/{SecondId}")).EnsureSuccessStatusCode();

        var payload = await ownerChanged.Task.WaitAsync(Wait);
        payload.GetProperty("projectId").GetString().Should().Be(projectId);

        // Событие отправлено после выкидывания — отозванному оно не долюет. FIFO той же
        // логики: останься он в группе, событие пришло бы до адресной пробы (сервер
        // послал его ещё во время DELETE-запроса)
        await Hub().Clients.Client(revoked.ConnectionId!).SendAsync(ProbeEvent, new { probe = "direct" });
        (await revokedSink.FirstOf("direct").WaitAsync(Wait)).Should().Be("direct");
        revokedChanged.Task.IsCompleted.Should().BeFalse(
            "список обновляется оставшимся в проекте; для отозванного — личное project_access_revoked");
    }

    [Fact]
    public async Task Leave_ГруппаПроектаПолучаетProjectMembersChanged_ПокинувшемуНет()
    {
        var projectId = await SharedProjectAsync();

        var owner = await ConnectAsync(TestWebApplicationFactory.TestUsername, TestWebApplicationFactory.TestPassword);
        await owner.InvokeAsync("JoinProject", projectId);
        var ownerChanged = NewSignal();
        owner.On<JsonElement>("project_members_changed", p => ownerChanged.TrySetResult(p));

        var left = await ConnectAsync(TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);
        await left.InvokeAsync("JoinProject", projectId);
        await left.InvokeAsync("JoinUser", SecondId);
        var leftChanged = NewSignal();
        left.On<JsonElement>("project_members_changed", p => leftChanged.TrySetResult(p));
        var leftSink = new Sink();
        leftSink.Attach(left, ProbeEvent);

        (await Member().DeleteAsync($"/api/projects/{projectId}/members/me")).EnsureSuccessStatusCode();

        var payload = await ownerChanged.Task.WaitAsync(Wait);
        payload.GetProperty("projectId").GetString().Should().Be(projectId);

        await Hub().Clients.Client(left.ConnectionId!).SendAsync(ProbeEvent, new { probe = "direct" });
        (await leftSink.FirstOf("direct").WaitAsync(Wait)).Should().Be("direct");
        leftChanged.Task.IsCompleted.Should().BeFalse(
            "покинувший выкинут из группы до события: список обновляется оставшимся в проекте");
    }
}
