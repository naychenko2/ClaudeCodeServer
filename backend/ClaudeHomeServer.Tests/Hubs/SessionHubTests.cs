using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;

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
