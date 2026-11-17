using ClaudeHomeServer.Hubs;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.ChildMode;
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

    private SessionManager Sessions => factory.Services.GetRequiredService<SessionManager>();

    private string ChildId => factory.Services.GetRequiredService<UserStore>()
        .FindByUsername(TestWebApplicationFactory.SecondUsername)!.Id;

    private async Task<string> ChatAsync(string ownerId, string? childSpace)
    {
        // Создание чата при включённом детском режиме запрещено — гасим профиль на время создания
        factory.Services.GetRequiredService<UserStore>()
            .FindByUsername(TestWebApplicationFactory.SecondUsername)!.ChildMode = null;
        var chat = await Sessions.CreateChatAsync(ownerId, ClaudeMode.Auto, name: "чат");
        chat.ChildSpace = childSpace;
        return chat.Id;
    }

    [Fact]
    public async Task Child_OwnChildChat_PassesFilter()
    {
        var chatId = await ChatAsync(ChildId, ChildSpaces.Lessons);
        var conn = await ConnectChildSessionHubAsync();
        // Фильтр пропустил: JoinSession своего детского чата отрабатывает штатно
        await conn.InvokeAsync("JoinSession", chatId);

        // SendMessage дошёл до хаба: отказ может прийти от самого хаба, но не child_mode
        var send = () => conn.InvokeAsync<string>("SendMessage", chatId, "привет", (List<string>?)null, "bypassPermissions", true);
        try { await send(); }
        catch (HubException e) { e.Message.Should().NotContain("child_mode"); }
    }

    [Theory]
    [InlineData("JoinSession")]
    [InlineData("SendMessage")]
    public async Task Child_ForeignOrNonChildOrMissingChat_ThrowsChildMode(string method)
    {
        var adminId = factory.Services.GetRequiredService<UserStore>()
            .FindByUsername(TestWebApplicationFactory.TestUsername)!.Id;
        var foreignWithSpace = await ChatAsync(adminId, ChildSpaces.Lessons);
        var ownWithoutSpace = await ChatAsync(ChildId, null);
        var conn = await ConnectChildSessionHubAsync();

        foreach (var id in new[] { foreignWithSpace, ownWithoutSpace, "no-such" })
        {
            var act = () => method == "JoinSession"
                ? conn.InvokeAsync(method, id)
                : conn.InvokeAsync<string>(method, id, "привет", (List<string>?)null, null, false);
            (await act.Should().ThrowAsync<HubException>(id)).Which.Message.Should().Contain("child_mode");
        }
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
    public void SendMessageArgs_AttachedPathsModeAndAuto_AreDropped()
    {
        var args = ChildHubFilter.SanitizeSendMessageArgs(["s", "t", new List<string> { "/etc/passwd" }, "bypassPermissions", true]);
        args.Should().Equal("s", "t", null, null, false);
    }

    private sealed class FakeCaller(System.Security.Claims.ClaimsPrincipal user) : HubCallerContext
    {
        public override string ConnectionId => "c";
        public override string? UserIdentifier => null;
        public override System.Security.Claims.ClaimsPrincipal? User => user;
        public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();
        public override Microsoft.AspNetCore.Http.Features.IFeatureCollection Features =>
            new Microsoft.AspNetCore.Http.Features.FeatureCollection();
        public override CancellationToken ConnectionAborted => CancellationToken.None;
        public override void Abort() { }
    }

    private sealed class ChildPolicy : IChildModePolicy
    {
        public ChildPolicySnapshot? For(string ownerId) => new(ownerId, new ChildProfile { Enabled = true }, DenyAll: false);
    }

    [Fact]
    public async Task SendMessage_ThroughFilter_NextGetsSanitizedArgs()
    {
        var chatId = await ChatAsync(ChildId, ChildSpaces.Lessons);
        var services = new ServiceCollection().AddSingleton<IChildModePolicy, ChildPolicy>()
            .AddSingleton(Sessions).BuildServiceProvider();
        var user = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
            [new System.Security.Claims.Claim(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub, ChildId)], "test"));
        var hub = (SessionHub)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(SessionHub));
        var method = typeof(SessionHub).GetMethod(nameof(SessionHub.SendMessage))!;
        var ctx = new HubInvocationContext(new FakeCaller(user), services, hub, method,
            new object?[] { chatId, "t", new List<string> { "/etc/passwd" }, "bypassPermissions", true });

        IReadOnlyList<object?>? seen = null;
        await new ChildHubFilter().InvokeMethodAsync(ctx, c => { seen = c.HubMethodArguments; return ValueTask.FromResult<object?>(null); });

        seen.Should().NotBeNull();
        seen!.Should().Equal(chatId, "t", null, null, false);
    }
}
