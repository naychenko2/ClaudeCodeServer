using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Hubs;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Hubs;

public class TerminalHubTests : IClassFixture<TestWebApplicationFactory>, IAsyncLifetime
{
    private readonly TestWebApplicationFactory _factory;
    private readonly HttpClient _owner;
    private readonly string _tempDir;
    private readonly List<HubConnection> _connections = [];

    public TerminalHubTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
        _owner = factory.CreateAuthenticatedClient();
        _tempDir = Path.Combine(factory.TempDir, "term_tests");
        Directory.CreateDirectory(_tempDir);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var c in _connections)
            await c.DisposeAsync();
    }

    private async Task<HubConnection> ConnectTerminalAsync(string username, string password)
    {
        var token = _factory.GetToken(username, password);
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(_factory.Server.BaseAddress, "hubs/terminal"), options =>
            {
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
        var dir = Path.Combine(_tempDir, "term_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var response = await _owner.PostAsJsonAsync("/api/projects", new
        {
            name = "TermProject",
            rootPath = dir
        });
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        return json.GetProperty("id").GetString()!;
    }

    [Fact]
    public async Task UnauthorizedUser_CannotConnect()
    {
        // Подключение без токена должно упасть
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(_factory.Server.BaseAddress, "hubs/terminal"), options =>
            {
                options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
            })
            .Build();

        var act = () => connection.StartAsync();
        await act.Should().ThrowAsync<Exception>();
    }

    [Fact]
    public async Task StartTerminal_OnNonexistentProject_Throws()
    {
        var conn = await ConnectTerminalAsync(TestWebApplicationFactory.TestUsername, TestWebApplicationFactory.TestPassword);
        var act = () => conn.InvokeAsync("StartTerminal", "no-such-project-id", 80, 24);
        await act.Should().ThrowAsync<HubException>();
    }

    [Fact]
    public async Task TerminalInput_OnNonexistentProject_Throws()
    {
        var conn = await ConnectTerminalAsync(TestWebApplicationFactory.TestUsername, TestWebApplicationFactory.TestPassword);
        var act = () => conn.InvokeAsync("TerminalInput", "no-such-project", "ls\n");
        await act.Should().ThrowAsync<HubException>();
    }

    [Fact]
    public async Task StartTerminal_AsNonOwner_Throws()
    {
        var projectId = await CreateProjectAsync();
        // Второй пользователь не владеет проектом
        var conn2 = await ConnectTerminalAsync(TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);
        var act = () => conn2.InvokeAsync("StartTerminal", projectId, 80, 24);
        await act.Should().ThrowAsync<HubException>();
    }
}
