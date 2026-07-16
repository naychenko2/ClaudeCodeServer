using ClaudeHomeServer.Hubs;
using ClaudeHomeServer.Services;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Moq;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// Тесты TerminalService без PTY (не требуют pty-bridge / ProjectManager).
/// Проверяют корректное поведение при отсутствии запущенного терминала.
/// </summary>
public class TerminalServiceTests
{
    private readonly TerminalService _svc;

    public TerminalServiceTests()
    {
        // ProjectManager не требуется для тестов пустого словаря терминалов
        _svc = new TerminalService(
            new Mock<IHubContext<TerminalHub>>().Object,
            null!,
            new Mock<ILogger<TerminalService>>().Object,
            TestLauncherFactory.Instance);
    }

    [Fact]
    public void Constructor_DoesNotThrow()
    {
        _svc.Should().NotBeNull();
    }

    [Fact]
    public void Resize_WithNoTerminal_DoesNotThrow()
    {
        var act = () => _svc.Resize("nonexistent", 100, 40);
        act.Should().NotThrow();
    }

    [Fact]
    public async Task StopAsync_WithNoTerminal_DoesNotThrow()
    {
        var act = () => _svc.StopAsync("nonexistent", "user1");
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task WriteInputAsync_WithNoTerminal_DoesNotThrow()
    {
        var act = () => _svc.WriteInputAsync("nonexistent", "ls -la\n");
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task RemoveViewerAsync_WithNoTerminal_DoesNotThrow()
    {
        var act = () => _svc.RemoveViewerAsync("nonexistent-conn");
        await act.Should().NotThrowAsync();
    }
}
