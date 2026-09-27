using System.Text.Json;
using ClaudeHomeServer.Controllers;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Desktop;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services.Desktop;

/// <summary>
/// Компонент рук в раздаче агента (ADR-016 §7, план рук Ш3/Ш5): секция <c>hands</c> манифеста,
/// отдача архива строго по записи манифеста и хеш компонента в ответе на hello.
/// </summary>
public class HandsComponentReleaseTests : IDisposable
{
    private readonly AgentReleaseFixture _rel = new();
    private readonly RecordingReleaseFileSystem _fs = new();

    public HandsComponentReleaseTests() => _rel.PublishWithHands();

    public void Dispose() => _rel.Dispose();

    private AgentReleaseCatalog Catalog() => new(_rel.Config(), fileSystem: _fs);

    private static AgentDownloadsController Controller(AgentReleaseCatalog catalog) =>
        new(NullLogger<AgentDownloadsController>.Instance, catalog)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

    [Fact]
    public void Манифест_СекцияHands_РазбираетсяПодRid()
    {
        var hands = Catalog().Current().Latest!.Hands;

        hands.Should().ContainKey(DeviceAgentRids.WinX64);
        var h = hands[DeviceAgentRids.WinX64];
        h.File.Should().Be(AgentReleaseFixture.HandsFile);
        h.Sha256.Should().Be(AgentReleaseFixture.Sha(_rel.HandsBytes));
        h.RelativePath.Should().Be($"{AgentReleaseFixture.Version}/win-x64/{AgentReleaseFixture.HandsFile}");
    }

    [Fact]
    public void Архив_рук_отдаётся_байт_в_байт_тем_же_путём_что_агент()
    {
        var result = Controller(Catalog()).Archive(AgentReleaseFixture.Version, "win-x64", AgentReleaseFixture.HandsFile);

        var file = result.Should().BeOfType<FileStreamResult>().Subject;
        using var copy = new MemoryStream();
        file.FileStream.CopyTo(copy);
        copy.ToArray().Should().Equal(_rel.HandsBytes);
        file.ContentType.Should().Be("application/zip");
    }

    [Fact]
    public void Указатель_несёт_компонент_рук()
    {
        var ok = Controller(Catalog()).Manifest().Should().BeOfType<OkObjectResult>().Subject;
        var hands = JsonSerializer.SerializeToElement(ok.Value).GetProperty("hands").GetProperty("win-x64");

        hands.GetProperty("file").GetString().Should().Be(AgentReleaseFixture.HandsFile);
        hands.GetProperty("sha256").GetString().Should().Be(AgentReleaseFixture.Sha(_rel.HandsBytes));
    }

    [Theory]
    [InlineData("не из манифеста", "win-x64", "hands-1.200.0-win-x64.exe")]
    [InlineData("RID без компонента", "linux-x64", AgentReleaseFixture.HandsFile)]
    [InlineData("..", "win-x64", "..")]
    [InlineData("../ в имени", "win-x64", "../" + AgentReleaseFixture.HandsFile)]
    [InlineData("%2e%2e в имени", "win-x64", "%2e%2e%2f" + AgentReleaseFixture.HandsFile)]
    public void Запрос_рук_не_из_манифеста_404_и_до_диска_не_доходит(string why, string rid, string file)
    {
        var catalog = Catalog();
        catalog.Current();
        _fs.Reset();

        var result = Controller(catalog).Archive(AgentReleaseFixture.Version, rid, file);

        result.Should().BeOfType<NotFoundObjectResult>(why);
        _fs.Count("open").Should().Be(0, why);
    }

    [Fact]
    public void Имя_компонента_совпадает_с_архивом_агента_манифест_отвергнут()
    {
        var json = JsonSerializer.Serialize(new
        {
            version = AgentReleaseFixture.Version,
            archives = new Dictionary<string, object>
            {
                ["win-x64"] = new { file = AgentReleaseFixture.WinFile, size = 1, sha256 = new string('a', 64) },
            },
            hands = new Dictionary<string, object>
            {
                ["win-x64"] = new { file = AgentReleaseFixture.WinFile, size = 1, sha256 = new string('b', 64) },
            },
        });
        _rel.WriteManifest(AgentReleaseFixture.Version, json);
        _rel.WritePointerJson(json);

        Catalog().Current().Versions.Should().NotContainKey(AgentReleaseFixture.Version,
            "два архива с одним именем делили бы файл на диске");
    }

    [Theory]
    [InlineData("../hands.zip")]
    [InlineData("hands dir/x.zip")]
    public void Недопустимое_имя_компонента_отвергает_манифест(string file)
    {
        var json = JsonSerializer.Serialize(new
        {
            version = AgentReleaseFixture.Version,
            archives = new Dictionary<string, object>
            {
                ["win-x64"] = new { file = AgentReleaseFixture.WinFile, size = 1, sha256 = new string('a', 64) },
            },
            hands = new Dictionary<string, object> { ["win-x64"] = new { file, size = 1, sha256 = new string('b', 64) } },
        });
        _rel.WriteManifest(AgentReleaseFixture.Version, json);

        Catalog().Current().Versions.Should().NotContainKey(AgentReleaseFixture.Version);
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
        var router = new DesktopCallRouter(Moq.Mock.Of<IDeviceCommandSender>(), [], NullLogger<DesktopCallRouter>.Instance);
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
