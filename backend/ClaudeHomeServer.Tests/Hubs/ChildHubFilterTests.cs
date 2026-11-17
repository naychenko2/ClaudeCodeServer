using ClaudeHomeServer.Hubs;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Hubs;

/// <summary>
/// Гейт И1 на хабах (KM-B2): ребёнку — только SendMessage и подписки своего чата; terminal и
/// devices закрыты на подключении; mode/auto из SendMessage ребёнка выбрасываются.
/// </summary>
[Trait("Category", "Host")]
public class ChildHubFilterTests(TestWebApplicationFactory factory) : IClassFixture<TestWebApplicationFactory>, IAsyncLifetime
{
    private readonly List<HubConnection> _connections = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var c in _connections) await c.DisposeAsync();
    }

    private void SetChild(bool valid = true)
    {
        factory.Services.GetRequiredService<UserStore>()
            .FindByUsername(TestWebApplicationFactory.SecondUsername)!.ChildMode = new ChildProfile
        {
            Enabled = true,
            TutorPersonaId = valid ? "tutor" : "",
            ProviderKey = "key",
            Model = "sonnet",
        };
    }

    private HubConnection Build(string hub, string username, string password)
    {
        var token = factory.GetToken(username, password);
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(factory.Server.BaseAddress, hub), o =>
            {
                o.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                o.Transports = HttpTransportType.LongPolling;
                o.AccessTokenProvider = () => Task.FromResult<string?>(token);
            })
            .Build();
        _connections.Add(connection);
        return connection;
    }

    private async Task<HubConnection> ConnectChildSessionHubAsync()
    {
        SetChild();
        var c = Build("hubs/session", TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);
        await c.StartAsync();
        return c;
    }

    [Theory]
    [InlineData("SetMode", "no-such", "bypassPermissions")]
    [InlineData("CompactSession", "no-such", null)]
    [InlineData("JoinProject", "no-such", null)]
    public async Task Child_DeniedMethods_ThrowChildMode(string method, string arg1, string? arg2)
    {
        var conn = await ConnectChildSessionHubAsync();
        var act = () => arg2 is null ? conn.InvokeAsync(method, arg1) : conn.InvokeAsync(method, arg1, arg2);
        (await act.Should().ThrowAsync<HubException>()).Which.Message.Should().Contain("child_mode");
    }

    [Fact]
    public async Task Child_AllowedMethods_PassFilter()
    {
        var conn = await ConnectChildSessionHubAsync();
        // Фильтр пропустил: отказ приходит уже от самого хаба (чата нет), а не child_mode
        var join = () => conn.InvokeAsync("JoinSession", "no-such");
        (await join.Should().ThrowAsync<HubException>()).Which.Message.Should().NotContain("child_mode");

        var send = () => conn.InvokeAsync<string>("SendMessage", "no-such", "привет", (List<string>?)null, "bypassPermissions", true);
        (await send.Should().ThrowAsync<HubException>()).Which.Message.Should().NotContain("child_mode");
    }

    [Theory]
    [InlineData("hubs/terminal")]
    [InlineData("hubs/devices")]
    public async Task Child_CannotConnect_ToTerminalOrDevicesHub(string hub)
    {
        SetChild();
        var c = Build(hub, TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);
        var act = () => c.StartAsync();
        await act.Should().ThrowAsync<Exception>();
    }

    [Fact]
    public async Task DenyAllChild_CannotConnect_ToSessionHub()
    {
        SetChild(valid: false);
        var c = Build("hubs/session", TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);
        var act = () => c.StartAsync();
        await act.Should().ThrowAsync<Exception>();
    }

    [Fact]
    public async Task Adult_CanUseDeniedMethods()
    {
        factory.Services.GetRequiredService<UserStore>()
            .FindByUsername(TestWebApplicationFactory.SecondUsername)!.ChildMode = null;
        var c = Build("hubs/session", TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);
        await c.StartAsync();
        var act = () => c.InvokeAsync("SetMode", "no-such", "plan");
        (await act.Should().ThrowAsync<HubException>()).Which.Message.Should().NotContain("child_mode");
    }

    [Fact]
    public void SendMessageArgs_ModeAndAuto_AreDropped()
    {
        var args = ChildHubFilter.SanitizeSendMessageArgs(["s", "t", null, "bypassPermissions", true]);
        args.Should().Equal("s", "t", null, null, false);
    }
}
