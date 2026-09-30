using System.Security.Claims;
using ClaudeHomeServer.Hubs;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Desktop;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ClaudeHomeServer.Tests.Hubs;

// Хаб устройств (канал управления ADR-016): владелец и устройство берутся ТОЛЬКО из claims
// токена, регистрация соединения на подключении, объявление версии протокола в Hello.
// Хаб зовём напрямую — маппинг и схема авторизации живут в проводке, здесь проверяется
// поведение.
public class DeviceHubTests
{
    private const string Owner = "owner-1";
    private const string Device = "device-1";
    private const string Conn = "conn-1";

    private static DeviceConnectionRegistry NewRouter() =>
        new([], NullLogger<DeviceConnectionRegistry>.Instance);

    private static (DeviceHub Hub, Mock<HubCallerContext> Context) NewHub(
        DeviceConnectionRegistry router, string? ownerId = Owner, string? deviceId = Device, string connectionId = Conn)
    {
        var claims = new List<Claim>();
        if (ownerId is not null) claims.Add(new Claim(DesktopProtocol.OwnerIdClaim, ownerId));
        if (deviceId is not null) claims.Add(new Claim(DesktopProtocol.DeviceIdClaim, deviceId));

        var context = new Mock<HubCallerContext>();
        context.SetupGet(c => c.ConnectionId).Returns(connectionId);
        context.SetupGet(c => c.User).Returns(new ClaimsPrincipal(new ClaimsIdentity(claims, "device-token")));
        context.SetupGet(c => c.ConnectionAborted).Returns(CancellationToken.None);

        // Устройств в реестре нет: Hello этих тестов без полей агента, сведения агента не пишутся
        var exec = new DeviceExecChannel(
            new DeviceRegistry(Path.Combine(Path.GetTempPath(), "ccs_devhub_" + Guid.NewGuid().ToString("N"))),
            router,
            new DeviceHarnessPolicy(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build()),
            Mock.Of<IDeviceExecOpenSender>(),
            NullLogger<DeviceExecChannel>.Instance);
        var hub = new DeviceHub(router, exec, NullLogger<DeviceHub>.Instance) { Context = context.Object };
        return (hub, context);
    }

    [Fact]
    public async Task ТокенБезПарыВладелецУстройство_СоединениеРвётся()
    {
        var router = NewRouter();
        var (hub, context) = NewHub(router, ownerId: Owner, deviceId: null);

        await hub.OnConnectedAsync();

        context.Verify(c => c.Abort(), Times.Once);
        router.IsOnline(Owner, Device).Should().BeFalse();
    }

    [Fact]
    public async Task Подключение_РегистрируетСоединениеАОнлайнДаётТолькоHello()
    {
        var router = NewRouter();
        var (hub, _) = NewHub(router);

        await hub.OnConnectedAsync();
        router.IsOnline(Owner, Device).Should().BeFalse();

        var ack = await hub.Hello(new DeviceHello(DesktopProtocol.Version, ["click", "type"], "1.0.0"));

        ack.ProtocolVersion.Should().Be(DesktopProtocol.Version);
        ack.AckTimeoutSeconds.Should().Be((int)DesktopProtocol.AckTimeout.TotalSeconds);
        ack.MaxBatchSteps.Should().Be(DesktopProtocol.MaxBatchSteps);
        router.IsOnline(Owner, Device).Should().BeTrue();
        router.Find(Owner, Device)!.SupportedSteps.Should().BeEquivalentTo(["click", "type"]);
    }

    [Fact]
    public async Task НесовместимаяВерсияПротокола_ЧестныйОтказ()
    {
        var router = NewRouter();
        var (hub, _) = NewHub(router);
        await hub.OnConnectedAsync();

        var act = () => hub.Hello(new DeviceHello(DesktopProtocol.Version + 1, [], "9.9.9"));

        (await act.Should().ThrowAsync<HubException>()).Which.Message.Should().ContainEquivalentOf("версия протокола");
        router.IsOnline(Owner, Device).Should().BeFalse();
    }

    [Fact]
    public async Task Отключение_УводитУстройствоВОфлайн()
    {
        var router = NewRouter();
        var (hub, _) = NewHub(router);
        await hub.OnConnectedAsync();
        await hub.Hello(new DeviceHello(DesktopProtocol.Version, [], "1.0.0"));

        await hub.OnDisconnectedAsync(null);

        router.IsOnline(Owner, Device).Should().BeFalse();
    }
}
