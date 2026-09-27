using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Execution;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Controllers;

// ADR-016, задача 3.2: SessionManager отдаёт ходу среду ПРОЕКТА. Боевая фабрика раннеров
// (LauncherFactory из DI), перехвачен только адаптер: какой Launcher пришёл в контексте.
public class SessionManagerProjectLauncherTests : IDisposable
{
    private sealed class CapturingFactory : ILlmSessionAdapterFactory
    {
        public ConcurrentDictionary<string, LlmSessionContext> Contexts { get; } = new();
        private readonly FakeLlmSessionAdapterFactory _inner = new();

        public ILlmSessionAdapter Create(Session session, LlmSessionContext context)
        {
            Contexts[session.Id] = context;
            return _inner.Create(session, context);
        }
    }

    private readonly CapturingFactory _adapters = new();
    private readonly TestWebApplicationFactory _factory;

    public SessionManagerProjectLauncherTests()
    {
        _factory = new TestWebApplicationFactory
        {
            ExtraServices = services => services.AddSingleton<ILlmSessionAdapterFactory>(_adapters),
        };
    }

    public void Dispose() => _factory.Dispose();

    private async Task<(string ProjectId, string SessionId)> CreateSessionAsync(string? deviceId)
    {
        var client = _factory.CreateAuthenticatedClient();
        var created = await client.PostAsJsonAsync("/api/projects", new { name = $"lp-{Guid.NewGuid():N}" });
        created.EnsureSuccessStatusCode();
        var projectId = JsonSerializer.Deserialize<JsonElement>(
            await created.Content.ReadAsStringAsync()).GetProperty("id").GetString()!;

        // Привязку к устройству ставим напрямую: создание через API требует живого агента
        // устройства — здесь проверяется выбор раннера, а не сопряжение
        if (deviceId is not null)
            _factory.Services.GetRequiredService<ProjectManager>().GetById(projectId)!.DeviceId = deviceId;

        var session = await client.PostAsJsonAsync($"/api/projects/{projectId}/sessions", new { mode = "acceptEdits" });
        session.EnsureSuccessStatusCode();
        var sessionId = JsonSerializer.Deserialize<JsonElement>(
            await session.Content.ReadAsStringAsync()).GetProperty("id").GetString()!;
        return (projectId, sessionId);
    }

    [Fact]
    public async Task ЧатПроектаНаУстройстве_ХодЧерезRemoteProcessRunner()
    {
        var (_, sessionId) = await CreateSessionAsync("dev-42");

        _adapters.Contexts[sessionId].Launcher.Should().BeOfType<RemoteProcessRunner>()
            .Which.DeviceId.Should().Be("dev-42");
    }

    [Fact]
    public async Task ЧатСерверногоПроекта_СредаВладельцаКакРаньше()
    {
        var (projectId, sessionId) = await CreateSessionAsync(deviceId: null);

        var project = _factory.Services.GetRequiredService<ProjectManager>().GetById(projectId)!;
        _adapters.Contexts[sessionId].Launcher.Should().BeSameAs(
            _factory.Services.GetRequiredService<ILauncherFactory>().ForOwner(project.OwnerId));
    }
}
