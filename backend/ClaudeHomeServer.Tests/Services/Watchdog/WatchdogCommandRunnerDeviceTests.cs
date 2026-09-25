using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Execution;
using ClaudeHomeServer.Services.Watchdog;
using FluentAssertions;
using Moq;

namespace ClaudeHomeServer.Tests.Services.Watchdog;

// Опрос сторожа локального проекта идёт на устройстве (ForProject). Отказ канала от
// неготового устройства — пропуск, а не сбой запуска (ADR-016, вариант А плана §5);
// отозванное устройство — настоящий сбой: ждать нечего.
public class WatchdogCommandRunnerDeviceTests
{
    private static readonly Project Local = new() { Id = "p-local", OwnerId = "owner-1", DeviceId = "dev-1", RootPath = "/home/u/proj" };

    private static WatchdogCommandRunner RunnerRefusing(DeviceExecRefusal reason)
    {
        var launcher = new Mock<IProcessLauncher>();
        launcher.Setup(l => l.Start(It.IsAny<ProcessSpec>()))
            .Throws(new DeviceExecRefusedException(reason, "Устройство «Ноутбук» не в сети — сообщение не взято в работу."));
        var factory = new Mock<ILauncherFactory>();
        factory.Setup(f => f.ForProject(It.Is<Project>(p => p.Id == Local.Id))).Returns(launcher.Object);
        var projects = new Mock<IProjectManager>();
        projects.Setup(p => p.GetById(Local.Id)).Returns(Local);
        return new WatchdogCommandRunner(factory.Object, projects.Object);
    }

    [Theory]
    [InlineData(DeviceExecRefusal.Offline)]
    [InlineData(DeviceExecRefusal.NoResponse)]
    [InlineData(DeviceExecRefusal.HarnessNotReady)]
    public async Task RunAsync_НеготовоеУстройство_ПропускАНеСбойЗапуска(DeviceExecRefusal reason)
    {
        var outcome = await RunnerRefusing(reason).RunAsync("owner-1", Local.Id, Local.RootPath, "test -f x", 10, default);

        outcome.Kind.Should().Be(PollOutcomeKind.DeviceUnavailable);
        outcome.Failure.Should().Contain("не в сети");
    }

    [Fact]
    public async Task RunAsync_ОтозванноеУстройство_СбойЗапуска()
    {
        var outcome = await RunnerRefusing(DeviceExecRefusal.UnknownDevice)
            .RunAsync("owner-1", Local.Id, Local.RootPath, "test -f x", 10, default);

        outcome.Kind.Should().Be(PollOutcomeKind.LaunchFailed);
    }
}
