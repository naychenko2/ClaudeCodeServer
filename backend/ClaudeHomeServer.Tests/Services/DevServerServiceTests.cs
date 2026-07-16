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
    public void GetStatus_WithNoServers_ReturnsStopped()
    {
        var (status, port, error) = _svc.GetStatus("nonexistent", "user1");
        status.Should().Be("stopped");
        port.Should().BeNull();
    }

    [Fact]
    public void GetPortNoAuth_WithNoServers_ReturnsNull()
    {
        var port = _svc.GetPortNoAuth("nonexistent");
        port.Should().BeNull();
    }

    [Fact]
    public void ShutdownAll_WithNoServers_DoesNotThrow()
    {
        var act = () => _svc.ShutdownAll();
        act.Should().NotThrow();
    }
}
