using System.Text.Json;
using ClaudeHomeServer.Controllers;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Devices;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services.Devices;

/// <summary>
/// Мост рук едет в архиве агента (ADR-016 §7): отдельного компонента в раздаче больше нет.
/// Каталог старой выкатки с секцией <c>hands</c> по-прежнему читается, но архив моста не
/// отдаётся и в указатель не попадает.
/// </summary>
public class HandsComponentReleaseTests : IDisposable
{
    private readonly AgentReleaseFixture _rel = new();
    private readonly RecordingReleaseFileSystem _fs = new();

    public HandsComponentReleaseTests() => _rel.PublishWithLegacyHands();

    public void Dispose() => _rel.Dispose();

    private AgentReleaseCatalog Catalog() => new(_rel.Config(), fileSystem: _fs);

    private static AgentDownloadsController Controller(AgentReleaseCatalog catalog) =>
        new(NullLogger<AgentDownloadsController>.Instance, catalog)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

    [Fact]
    public void Старая_секция_hands_не_ломает_раздачу_агента()
    {
        var latest = Catalog().Current().Latest;

        latest.Should().NotBeNull();
        latest!.Archives.Keys.Should().BeEquivalentTo(DeviceAgentRids.WinX64, DeviceAgentRids.LinuxX64);
    }

    [Fact]
    public void Архив_рук_старой_выкатки_не_отдаётся_и_до_диска_не_доходит()
    {
        var catalog = Catalog();
        catalog.Current();
        _fs.Reset();

        var result = Controller(catalog).Archive(AgentReleaseFixture.Version, "win-x64", AgentReleaseFixture.HandsFile);

        result.Should().BeOfType<NotFoundObjectResult>();
        _fs.Count("open").Should().Be(0);
    }

    [Fact]
    public void Указатель_компонента_рук_не_несёт()
    {
        var ok = Controller(Catalog()).Manifest().Should().BeOfType<OkObjectResult>().Subject;

        JsonSerializer.SerializeToElement(ok.Value).TryGetProperty("hands", out _).Should().BeFalse();
    }
}

/// <summary>Донесение агента о руках: только по ходу, который сервер отправил ЭТОМУ устройству.</summary>
public class DeviceHubHandsReportTests : IDisposable
{
    private readonly string _owner = "owner-" + Guid.NewGuid().ToString("N")[..6];
    private readonly string _temp = Directory.CreateTempSubdirectory("ccs-hands-hub-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_temp, recursive: true); } catch (IOException) { }
    }

    private DeviceHub NewHub(ILocalHandsNotifier notifier, string deviceId)
    {
        var context = new Moq.Mock<Microsoft.AspNetCore.SignalR.HubCallerContext>();
        context.SetupGet(c => c.ConnectionId).Returns("conn");
        context.SetupGet(c => c.User).Returns(new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
            [new System.Security.Claims.Claim(DesktopProtocol.OwnerIdClaim, _owner),
             new System.Security.Claims.Claim(DesktopProtocol.DeviceIdClaim, deviceId)], "device-token")));
        var router = new DeviceConnectionRegistry([], NullLogger<DeviceConnectionRegistry>.Instance);
        var exec = new DeviceExecChannel(new DeviceRegistry(_temp), router,
            new DeviceHarnessPolicy(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build()),
            Moq.Mock.Of<IDeviceExecOpenSender>(), NullLogger<DeviceExecChannel>.Instance);
        return new DeviceHub(router, exec, NullLogger<DeviceHub>.Instance, handsNotifier: notifier) { Context = context.Object };
    }

    [Fact]
    public async Task Донесение_по_своему_ходу_уходит_в_чат_чужое_отвергается()
    {
        ClaudeHomeServer.Services.Execution.DeviceHandsTurns.Register(_owner, "dev-1", "turn-1", "chat-1");
        var notifier = new Moq.Mock<ILocalHandsNotifier>();

        await NewHub(notifier.Object, "dev-1").ReportHandsStatus(
            new DeviceHandsReport("turn-1", HandsChatStates.Stopped, HandsEndReason.StoppedFromTray));
        var foreignDevice = () => NewHub(notifier.Object, "dev-2").ReportHandsStatus(new DeviceHandsReport("turn-1", HandsChatStates.Active));
        var unknownTurn = () => NewHub(notifier.Object, "dev-1").ReportHandsStatus(new DeviceHandsReport("turn-x", HandsChatStates.Active));
        var unknownState = () => NewHub(notifier.Object, "dev-1").ReportHandsStatus(new DeviceHandsReport("turn-1", "hacked"));

        await foreignDevice.Should().ThrowAsync<Microsoft.AspNetCore.SignalR.HubException>();
        await unknownTurn.Should().ThrowAsync<Microsoft.AspNetCore.SignalR.HubException>();
        await unknownState.Should().ThrowAsync<Microsoft.AspNetCore.SignalR.HubException>();
        notifier.Verify(n => n.HandsStatusAsync(_owner, "dev-1", "chat-1", HandsChatStates.Stopped, HandsEndReason.StoppedFromTray,
            Moq.It.IsAny<CancellationToken>()), Moq.Times.Once);
        notifier.VerifyNoOtherCalls();
        ClaudeHomeServer.Services.Execution.DeviceHandsTurns.ChatStateOf(_owner, "dev-1", "chat-1").State.Should().Be(HandsChatStates.Stopped);
    }

    // Дефект 3d761fc8: итог агент шлёт уже после кадра Exit — к этому времени сервер закрыл исполнение
    [Fact]
    public async Task Итог_после_конца_хода_принят_и_дошёл_до_чата_чужой_ход_отказ()
    {
        ClaudeHomeServer.Services.Execution.DeviceHandsTurns.Register(_owner, "dev-1", "turn-3", "chat-3");
        ClaudeHomeServer.Services.Execution.DeviceHandsTurns.End(_owner, "dev-1", "turn-3");
        var notifier = new Moq.Mock<ILocalHandsNotifier>();

        await NewHub(notifier.Object, "dev-1").ReportHandsStatus(
            new DeviceHandsReport("turn-3", HandsChatStates.Stopped, HandsEndReason.StoppedFromTray));
        var forged = () => NewHub(notifier.Object, "dev-1").ReportHandsStatus(new DeviceHandsReport("turn-4", HandsChatStates.Stopped));
        var afterFinal = () => NewHub(notifier.Object, "dev-1").ReportHandsStatus(new DeviceHandsReport("turn-3", HandsChatStates.Active));

        await forged.Should().ThrowAsync<Microsoft.AspNetCore.SignalR.HubException>();
        await afterFinal.Should().ThrowAsync<Microsoft.AspNetCore.SignalR.HubException>("итог закрыл окно донесений хода");
        notifier.Verify(n => n.HandsStatusAsync(_owner, "dev-1", "chat-3", HandsChatStates.Stopped, HandsEndReason.StoppedFromTray,
            Moq.It.IsAny<CancellationToken>()), Moq.Times.Once);
        notifier.VerifyNoOtherCalls();
        ClaudeHomeServer.Services.Execution.DeviceHandsTurns.ChatStateOf(_owner, "dev-1", "chat-3")
            .Should().Be((HandsChatStates.Stopped, HandsEndReason.StoppedFromTray));
    }

    [Fact]
    public async Task Причина_с_устройства_только_из_известного_набора()
    {
        ClaudeHomeServer.Services.Execution.DeviceHandsTurns.Register(_owner, "dev-1", "turn-2", "chat-2");
        var notifier = new Moq.Mock<ILocalHandsNotifier>();

        await NewHub(notifier.Object, "dev-1").ReportHandsStatus(
            new DeviceHandsReport("turn-2", HandsChatStates.Stopped, "<script>любой текст</script>"));

        notifier.Verify(n => n.HandsStatusAsync(_owner, "dev-1", "chat-2", HandsChatStates.Stopped, null,
            Moq.It.IsAny<CancellationToken>()), Moq.Times.Once);
    }
}
