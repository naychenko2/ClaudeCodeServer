using ClaudeHomeServer.Hubs;
using ClaudeHomeServer.Services;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Moq;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// Тесты DevServerService без запуска процессов.
/// </summary>
public class DevServerServiceTests
{
    private readonly DevServerService _svc;

    public DevServerServiceTests()
    {
        _svc = new DevServerService(null!, new Mock<IHubContext<SessionHub>>().Object,
            new Mock<ILogger<DevServerService>>().Object);
    }

    [Fact]
    public void Constructor_DoesNotThrow()
    {
        _svc.Should().NotBeNull();
    }

    [Fact]
    public void GetRunning_WithNoServers_ReturnsEmpty()
    {
        _svc.GetRunning("nonexistent", "user1").Should().BeEmpty();
    }

    [Fact]
    public void GetActivePreviewPort_WithNoServers_ReturnsNull()
    {
        _svc.GetActivePreviewPort("nonexistent").Should().BeNull();
    }

    [Fact]
    public void GetActiveServiceId_WithNoServers_ReturnsNull()
    {
        _svc.GetActiveServiceId("nonexistent", "user1").Should().BeNull();
    }

    [Fact]
    public void SetActivePreview_WithoutStartedServer_StillHasNoPort()
    {
        _svc.SetActivePreview("proj", "svc");
        // Активный назначен, но процесс не запущен — порта нет.
        _svc.GetActivePreviewPort("proj").Should().BeNull();
    }

    [Fact]
    public void ShutdownAll_WithNoServers_DoesNotThrow()
    {
        var act = () => _svc.ShutdownAll();
        act.Should().NotThrow();
    }
}
