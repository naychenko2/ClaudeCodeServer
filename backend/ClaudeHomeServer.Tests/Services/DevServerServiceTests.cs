using ClaudeHomeServer.Core.Services;
using ClaudeHomeServer.Services.ProjectServices;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
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
        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build();
        var sandbox = new ClaudeHomeServer.Services.Execution.SandboxManager(config,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ClaudeHomeServer.Services.Execution.SandboxManager>.Instance);
        // Память портов пишет в data рядом с DataPath; в тестах конфигурация пуста,
        // поэтому файл ложится во временный каталог сборки и никому не мешает
        var portMemory = new DevServerPortMemory(config,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<DevServerPortMemory>.Instance);
        _svc = new DevServerService(null!, new TestSessionBroadcaster(),
            new Mock<ILogger<DevServerService>>().Object, TestLauncherFactory.Instance, sandbox, portMemory);
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
