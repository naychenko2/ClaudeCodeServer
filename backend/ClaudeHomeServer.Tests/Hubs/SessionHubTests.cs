using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Hubs;

// Интеграционные тесты SignalR-хаба: подключение через TestServer,
// проверка ownership-ограничений JoinSession / JoinProject / JoinUser
public class SessionHubTests : IClassFixture<TestWebApplicationFactory>, IAsyncLifetime
{
    private readonly TestWebApplicationFactory _factory;
    private readonly HttpClient _owner;
    private readonly string _tempDir;
    private readonly List<HubConnection> _connections = [];

    public SessionHubTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
        _owner = factory.CreateAuthenticatedClient();
        _tempDir = Path.Combine(factory.TempDir, "hub_tests");
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
                // Гоним трафик через in-memory TestServer; WebSocket там нет — LongPolling
                options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
                options.AccessTokenProvider = () => Task.FromResult<string?>(token);
            })
            .Build();
        // Под параллельной нагрузкой всего набора тестов TestServer обслуживает negotiate
        // с очередью — дефолтных 15с HandshakeTimeout не хватало, тесты flakали на StartAsync.
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
        var response = await _owner.PostAsJsonAsync("/api/projects", new
        {
            name = "HubProject",
            rootPath = dir
        });
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        return json.GetProperty("id").GetString()!;
    }

    private async Task<string> CreateSessionAsync(string projectId)
    {
        var response = await _owner.PostAsJsonAsync($"/api/projects/{projectId}/sessions", new
        {
            mode = "auto"
        });
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        return json.GetProperty("id").GetString()!;
    }

    // ─── JoinSession ─────────────────────────────────────────────────────────

    [Fact]
    public async Task JoinSession_СвояСессия_Ок()
    {
        var projectId = await CreateProjectAsync();
        var sessionId = await CreateSessionAsync(projectId);
        var conn = await ConnectAsync(TestWebApplicationFactory.TestUsername, TestWebApplicationFactory.TestPassword);

        var act = () => conn.InvokeAsync("JoinSession", sessionId);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task JoinSession_ЧужаяСессия_Отказ()
    {
        var projectId = await CreateProjectAsync();
        var sessionId = await CreateSessionAsync(projectId);
        var stranger = await ConnectAsync(TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);

        var act = () => stranger.InvokeAsync("JoinSession", sessionId);

        (await act.Should().ThrowAsync<HubException>())
            .WithMessage("*Доступ запрещён*");
    }

    [Fact]
    public async Task JoinSession_НесуществующаяСессия_Отказ()
    {
        var conn = await ConnectAsync(TestWebApplicationFactory.TestUsername, TestWebApplicationFactory.TestPassword);

        var act = () => conn.InvokeAsync("JoinSession", "ghost-session");

        await act.Should().ThrowAsync<HubException>();
    }

    // ─── JoinSession: реплей статуса ─────────────────────────────────────────

    // Собирает status_changed, приходящие соединению, чтобы проверять реплей при входе в группу
    private static (List<JsonElement> Received, SemaphoreSlim Signal) CollectStatuses(HubConnection conn)
    {
        List<JsonElement> received = [];
        SemaphoreSlim signal = new(0);
        conn.On<JsonElement>("message", msg =>
        {
            if (msg.TryGetProperty("type", out var type) && type.GetString() == "status_changed")
            {
                lock (received) received.Add(msg);
                signal.Release();
            }
        });
        return (received, signal);
    }

    private void SetStatus(string sessionId, SessionStatus status) =>
        _factory.Services.GetRequiredService<SessionManager>().GetById(sessionId)!.Status = status;

    [Theory]
    [InlineData(SessionStatus.Active)]
    [InlineData(SessionStatus.Error)]
    [InlineData(SessionStatus.Finished)]
    public async Task JoinSession_ЗавершённаяСессия_РеплеитСтатус(SessionStatus status)
    {
        // Клиент, пропустивший конец хода вне группы, узнаёт о нём только из этого реплея —
        // иначе у него навсегда остаётся «Claude печатает…»
        var projectId = await CreateProjectAsync();
        var sessionId = await CreateSessionAsync(projectId);
        SetStatus(sessionId, status);
        var conn = await ConnectAsync(TestWebApplicationFactory.TestUsername, TestWebApplicationFactory.TestPassword);
        var (received, signal) = CollectStatuses(conn);

        await conn.InvokeAsync("JoinSession", sessionId);

        (await signal.WaitAsync(TimeSpan.FromSeconds(10))).Should().BeTrue("статус должен прийти при входе в группу");
        lock (received)
        {
            received.Should().HaveCount(1);
            received[0].GetProperty("status").GetString().Should().Be(status.ToString().ToLowerInvariant());
            received[0].GetProperty("sessionId").GetString().Should().Be(sessionId);
        }
    }

    [Fact]
    public async Task JoinSession_РабочаяСессия_РеплеитСтатус()
    {
        // Прежнее поведение (working/waiting/starting) не сломано расширением реплея
        var projectId = await CreateProjectAsync();
        var sessionId = await CreateSessionAsync(projectId);
        SetStatus(sessionId, SessionStatus.Working);
        var conn = await ConnectAsync(TestWebApplicationFactory.TestUsername, TestWebApplicationFactory.TestPassword);
        var (received, signal) = CollectStatuses(conn);

        await conn.InvokeAsync("JoinSession", sessionId);

        (await signal.WaitAsync(TimeSpan.FromSeconds(10))).Should().BeTrue();
        lock (received) received[0].GetProperty("status").GetString().Should().Be("working");
    }

    [Fact]
    public async Task JoinSession_РеплейСтатуса_ТолькоCaller()
    {
        // Реплей адресный: вход второго клиента не должен слать статус всей группе,
        // иначе чужие вкладки получали бы ложный «конец хода»
        var projectId = await CreateProjectAsync();
        var sessionId = await CreateSessionAsync(projectId);
        SetStatus(sessionId, SessionStatus.Active);

        var first = await ConnectAsync(TestWebApplicationFactory.TestUsername, TestWebApplicationFactory.TestPassword);
        var (firstReceived, firstSignal) = CollectStatuses(first);
        await first.InvokeAsync("JoinSession", sessionId);
        (await firstSignal.WaitAsync(TimeSpan.FromSeconds(10))).Should().BeTrue();

        var second = await ConnectAsync(TestWebApplicationFactory.TestUsername, TestWebApplicationFactory.TestPassword);
        var (secondReceived, secondSignal) = CollectStatuses(second);
        await second.InvokeAsync("JoinSession", sessionId);
        (await secondSignal.WaitAsync(TimeSpan.FromSeconds(10))).Should().BeTrue();

        // Запас на доставку по LongPolling: если бы реплей шёл в группу, он бы уже дошёл
        await Task.Delay(TimeSpan.FromMilliseconds(500));
        lock (secondReceived) secondReceived.Should().HaveCount(1);
        lock (firstReceived) firstReceived.Should().HaveCount(1, "первому соединению чужой join статус не шлёт");
    }

    // ─── JoinProject ─────────────────────────────────────────────────────────

    [Fact]
    public async Task JoinProject_СвойПроект_Ок()
    {
        var projectId = await CreateProjectAsync();
        var conn = await ConnectAsync(TestWebApplicationFactory.TestUsername, TestWebApplicationFactory.TestPassword);

        var act = () => conn.InvokeAsync("JoinProject", projectId);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task JoinProject_ЧужойПроект_Отказ()
    {
        var projectId = await CreateProjectAsync();
        var stranger = await ConnectAsync(TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);

        var act = () => stranger.InvokeAsync("JoinProject", projectId);

        (await act.Should().ThrowAsync<HubException>())
            .WithMessage("*Доступ запрещён*");
    }

    // ─── JoinUser ────────────────────────────────────────────────────────────

    [Fact]
    public async Task JoinUser_ЧужойUserId_Отказ()
    {
        // Свой userId узнаём из /api/auth/me второго юзера
        var secondClient = _factory.CreateAuthenticatedClient(
            TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);
        var me = await secondClient.GetFromJsonAsync<JsonElement>("/api/auth/me");
        var secondUserId = me.GetProperty("userId").GetString()!;

        var conn = await ConnectAsync(TestWebApplicationFactory.TestUsername, TestWebApplicationFactory.TestPassword);

        // Первый юзер подписывается на группу второго — отказ
        var act = () => conn.InvokeAsync("JoinUser", secondUserId);
        await act.Should().ThrowAsync<HubException>();

        // Второй на себя — ок
        var second = await ConnectAsync(TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);
        var own = () => second.InvokeAsync("JoinUser", secondUserId);
        await own.Should().NotThrowAsync();
    }

    // ─── Аутентификация ──────────────────────────────────────────────────────

    [Fact]
    public async Task Подключение_БезТокена_НеПроходит()
    {
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(_factory.Server.BaseAddress, "hubs/session"), options =>
            {
                options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
            })
            .Build();

        var act = () => connection.StartAsync();

        await act.Should().ThrowAsync<Exception>(); // 401 на negotiate
        await connection.DisposeAsync();
    }
}
