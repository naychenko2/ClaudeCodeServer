using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.ImageEditor;
using ClaudeHomeServer.Tests.Helpers;
using ClaudeHomeServer.Tests.ImageEditor.Characters;
using FluentAssertions;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.ImageEditor.Threads;

// Доставка image_thread_changed по настоящему SignalR до соединения в группе владельца:
// запись нити через REST в личном чате и в чате проекта обязана долететь одинаково
public class ThreadChangedDeliveryTests : IAsyncLifetime
{
    private readonly TestWebApplicationFactory _factory = new();
    private readonly HttpClient _client;
    private readonly string _ownerId;
    private readonly string _projectId;
    private HubConnection? _conn;

    public ThreadChangedDeliveryTests()
    {
        _factory.ExtraServices = services =>
            services.AddSingleton<IImageEditor>(new FakeImageEditor("fal", models: FakeImageEditor.Model("m")));
        CharacterEndpointsTests.EnableFlag(_factory, TestWebApplicationFactory.TestUsername);
        (_projectId, _) = CharacterEndpointsTests.CreateProject(_factory, TestWebApplicationFactory.TestUsername);
        _ownerId = _factory.Services.GetRequiredService<UserStore>().FindByUsername(TestWebApplicationFactory.TestUsername)!.Id;
        _client = _factory.CreateAuthenticatedClient();
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_conn is not null) await _conn.DisposeAsync();
        _factory.Dispose();
    }

    private SessionManager Sessions => _factory.Services.GetRequiredService<SessionManager>();

    // Соединение как у фронта: группа владельца через JoinUser, канал «message»
    private async Task<Task<JsonElement>> ListenThreadChanged(string sessionId)
    {
        var token = _factory.GetToken(TestWebApplicationFactory.TestUsername, TestWebApplicationFactory.TestPassword);
        _conn = new HubConnectionBuilder()
            .WithUrl(new Uri(_factory.Server.BaseAddress, "hubs/session"), o =>
            {
                o.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                o.Transports = HttpTransportType.LongPolling;
                o.AccessTokenProvider = () => Task.FromResult<string?>(token);
            })
            .Build();
        _conn.HandshakeTimeout = TimeSpan.FromSeconds(60);
        var got = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _conn.On<JsonElement>("message", m =>
        {
            if (m.TryGetProperty("type", out var t) && t.GetString() == "image_thread_changed"
                && m.TryGetProperty("sessionId", out var s) && s.GetString() == sessionId)
                got.TrySetResult(m);
        });
        await _conn.StartAsync();
        await _conn.InvokeAsync("JoinUser", _ownerId);
        return got.Task;
    }

    private async Task AssertDelivered(string threadsUrl, string sessionId, string scope)
    {
        var changed = await ListenThreadChanged(sessionId);

        var resp = await _client.PostAsJsonAsync(threadsUrl, new { draftFolder = "", revision = 0 });
        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());

        (await Task.WhenAny(changed, Task.Delay(TimeSpan.FromSeconds(20)))).Should().BeSameAs(changed,
            "событие image_thread_changed должно долететь до группы владельца");
        var msg = await changed;
        msg.GetProperty("projectId").GetString().Should().Be(scope);
        msg.GetProperty("state").GetProperty("threads").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task Черновик_в_чате_проекта_долетает_событием()
    {
        var chat = await Sessions.CreateAsync(_projectId, ClaudeMode.AcceptEdits, name: "Чат проекта");
        await AssertDelivered($"/api/projects/{_projectId}/image-editor/sessions/{chat.Id}/threads", chat.Id, _projectId);
    }

    [Fact]
    public async Task Черновик_в_личном_чате_долетает_событием()
    {
        var chat = await Sessions.CreateChatAsync(_ownerId, ClaudeMode.AcceptEdits, name: "Личный чат");
        await AssertDelivered($"/api/image-editor/chats/{chat.Id}/threads", chat.Id, "personal");
    }
}
