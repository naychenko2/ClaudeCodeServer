using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Hubs;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Hubs;

/// <summary>
/// S0-6: хаб на точку доступа (IProjectAccess). Проверяется не текст отказа (он уже в
/// SessionHubTests), а membership групп: чужой НЕ входит в группу проекта project_{id} и в
/// группу чата {sessionId}, свой — входит. Группа — единственное, что решает «кому долетит
/// проектное событие», поэтому проверка строится на реальной рассылке в группу:
/// 1. проба в группу доходит «своему» — иначе «чужой не получил» было бы зелёным на пустоте;
/// 2. проба доходит адресно тому же чужому соединению — иначе «не получил» читалась бы как
///    мёртвое соединение, а не как отсутствие в группе.
/// Тогда единственное, чем отличается чужой от своего, — именно membership.
/// </summary>
[Trait("Category", "Host")]
public class SessionHubAccessTests : IClassFixture<TestWebApplicationFactory>, IAsyncLifetime
{
    // Событие-проба намерено вне протокола (не "message"): тест проверяет доставку в группу,
    // а не какой-то конкретный месседж, и не должен считаться по чужим реплеям при входе.
    private const string ProbeEvent = "test_group_probe";

    private readonly TestWebApplicationFactory _factory;
    private readonly HttpClient _owner;
    private readonly string _tempDir;
    private readonly List<HubConnection> _connections = [];

    public SessionHubAccessTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
        _owner = factory.CreateAuthenticatedClient();
        _tempDir = Path.Combine(factory.TempDir, "hub_access_tests");
        Directory.CreateDirectory(_tempDir);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var c in _connections)
            await c.DisposeAsync();
    }

    private async Task<HubConnection> ConnectAsync(string username, string password)
    {
        var token = _factory.GetToken(username, password);
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(_factory.Server.BaseAddress, "hubs/session"), options =>
            {
                // Как в SessionHubTests: под TestServer WebSocket'а нет, только LongPolling
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

    private async Task<string> CreateProjectAsync()
    {
        var dir = Path.Combine(_tempDir, "proj_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var response = await _owner.PostAsJsonAsync("/api/projects", new { name = "AccessProject", rootPath = dir });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    private async Task<string> CreateSessionAsync(string projectId)
    {
        var response = await _owner.PostAsJsonAsync($"/api/projects/{projectId}/sessions", new { mode = "auto" });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    // Счётчик проб данного соединения + сигнал на первую полученную
    private static (List<JsonElement> Got, TaskCompletionSource<JsonElement> First) WatchProbes(HubConnection conn)
    {
        var got = new List<JsonElement>();
        var first = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        conn.On<JsonElement>(ProbeEvent, payload =>
        {
            lock (got) got.Add(payload);
            first.TrySetResult(payload);
        });
        return (got, first);
    }

    private IHubContext<SessionHub> Hub() => _factory.Services.GetRequiredService<IHubContext<SessionHub>>();

    private Task SendToGroupAsync(string group, object probe) =>
        Hub().Clients.Group(group).SendAsync(ProbeEvent, probe);

    private Task SendToConnectionAsync(string connectionId, object probe) =>
        Hub().Clients.Client(connectionId).SendAsync(ProbeEvent, probe);

    [Fact]
    public async Task JoinProject_ЧужойНеВходитВГруппуПроекта()
    {
        var projectId = await CreateProjectAsync();

        var owner = await ConnectAsync(TestWebApplicationFactory.TestUsername, TestWebApplicationFactory.TestPassword);
        var (ownerGot, ownerFirst) = WatchProbes(owner);
        await owner.InvokeAsync("JoinProject", projectId);

        var stranger = await ConnectAsync(TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);
        var (strangerGot, strangerFirst) = WatchProbes(stranger);
        var act = () => stranger.InvokeAsync("JoinProject", projectId);
        (await act.Should().ThrowAsync<HubException>()).WithMessage("*Доступ запрещён*");

        await SendToGroupAsync("project_" + projectId, new { probe = "project" });
        (await ownerFirst.Task.WaitAsync(TimeSpan.FromSeconds(30)))
            .Should().NotBeNull("своему в группе проекта проба обязана дойти");

        // Та же проба адресно чужому соединению: дошла — значит соединение живое и слушает,
        // и его молчание ниже объясняется только отсутствием в группе
        await SendToConnectionAsync(stranger.ConnectionId!, new { probe = "direct" });
        (await strangerFirst.Task.WaitAsync(TimeSpan.FromSeconds(30))).Should().NotBeNull();
        await Task.Delay(TimeSpan.FromSeconds(1)); // запас на групповую доставку под нагрузкой прогона

        lock (ownerGot) ownerGot.Should().HaveCount(1);
        lock (strangerGot) strangerGot.Should().HaveCount(1,
            "отказанному соединению группа проекта не открывается: доходит только адресная проба");
    }

    [Fact]
    public async Task JoinSession_ЧужойНеВходитВГруппуЧата()
    {
        var projectId = await CreateProjectAsync();
        var sessionId = await CreateSessionAsync(projectId);

        var owner = await ConnectAsync(TestWebApplicationFactory.TestUsername, TestWebApplicationFactory.TestPassword);
        var (ownerGot, ownerFirst) = WatchProbes(owner);
        await owner.InvokeAsync("JoinSession", sessionId);

        var stranger = await ConnectAsync(TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);
        var (strangerGot, strangerFirst) = WatchProbes(stranger);
        var act = () => stranger.InvokeAsync("JoinSession", sessionId);
        (await act.Should().ThrowAsync<HubException>()).WithMessage("*Доступ запрещён*");

        await SendToGroupAsync(sessionId, new { probe = "session" });
        (await ownerFirst.Task.WaitAsync(TimeSpan.FromSeconds(30)))
            .Should().NotBeNull("своему в группе чата проба обязана дойти");

        await SendToConnectionAsync(stranger.ConnectionId!, new { probe = "direct" });
        (await strangerFirst.Task.WaitAsync(TimeSpan.FromSeconds(30))).Should().NotBeNull();
        await Task.Delay(TimeSpan.FromSeconds(1));

        lock (ownerGot) ownerGot.Should().HaveCount(1);
        lock (strangerGot) strangerGot.Should().HaveCount(1,
            "отказанному соединению группа чата не открывается: доходит только адресная проба");
    }
}
