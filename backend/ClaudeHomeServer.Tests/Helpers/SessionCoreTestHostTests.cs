using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Sessions;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Helpers;

// Помощник сборки ядра сессий (S3-8): контейнер свой у каждого Build, регистрация теста
// выигрывает у умолчаний, фасад и роли ядра делят один стор.
public sealed class SessionCoreTestHostTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "session_core_host_" + Guid.NewGuid().ToString("N"));

    public SessionCoreTestHostTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => TestFs.DeleteDirectoryResilient(_dir);

    [Fact]
    public void Build_TwoCalls_GiveSeparateManagersAndStores()
    {
        using var first = SessionCoreTestHost.Build(Path.Combine(_dir, "a"));
        using var second = SessionCoreTestHost.Build(Path.Combine(_dir, "b"));

        first.Manager.Should().NotBeSameAs(second.Manager);
        first.Services.GetRequiredService<SessionStore>()
            .Should().NotBeSameAs(second.Services.GetRequiredService<SessionStore>());
    }

    [Fact]
    public void Build_BroadcasterFromConfigure_IsTheOneInServices()
    {
        var broadcaster = new TestSessionBroadcaster();

        using var host = SessionCoreTestHost.Build(_dir, s => s.AddSingleton<ISessionBroadcaster>(broadcaster));

        host.Services.GetRequiredService<ISessionBroadcaster>().Should().BeSameAs(broadcaster);
    }

    [Fact]
    public async Task Build_ChatCreatedThroughManager_IsVisibleInStore()
    {
        using var host = SessionCoreTestHost.Build(_dir);
        var projectDir = Directory.CreateDirectory(Path.Combine(_dir, "proj")).FullName;
        var project = host.Services.GetRequiredService<ProjectManager>()
            .Create("P", projectDir, "owner-id", "owner");

        var chat = await host.Manager.CreateAsync(project.Id, ClaudeMode.Auto);

        host.Services.GetRequiredService<SessionStore>().GetById(chat.Id).Should().BeSameAs(chat);
    }
}
