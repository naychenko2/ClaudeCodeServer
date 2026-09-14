using System.Security.Claims;
using ClaudeHomeServer.Hubs;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Desktop;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ClaudeHomeServer.Tests.Hubs;

// Хаб устройств десктопного агента (ADR-008): владелец и устройство берутся ТОЛЬКО из claims
// токена, регистрация соединения на подключении, объявление версии протокола в Hello,
// донесения по чужому вызову отвергаются. Хаб зовём напрямую — маппинг и схема авторизации
// живут в проводке, здесь проверяется поведение.
public class DeviceHubTests
{
    private const string Owner = "owner-1";
    private const string Device = "device-1";
    private const string Conn = "conn-1";

    private sealed class SilentSender : IDeviceCommandSender
    {
        public readonly TaskCompletionSource<DesktopCallCommand> Sent =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task SendCallAsync(string connectionId, DesktopCallCommand command, CancellationToken ct = default)
        {
            Sent.TrySetResult(command);
            return Task.CompletedTask;
        }

        public Task SendGoAsync(string connectionId, DesktopGoCommand go, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendCancelAsync(string connectionId, DesktopCancelCommand cancel, CancellationToken ct = default) => Task.CompletedTask;
    }

    // Часы, которые не идут: фазовые дедлайны в этих тестах не проверяются, а на медленном
    // раннере реальные 2 с ack'а сделали бы тест флаки.
    private sealed class FrozenTime : TimeProvider
    {
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
            => new NeverTimer();

        private sealed class NeverTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private static DesktopCallRouter NewRouter(IDeviceCommandSender sender) =>
        new(sender, [], NullLogger<DesktopCallRouter>.Instance, new FrozenTime());

    private static (DeviceHub Hub, Mock<HubCallerContext> Context) NewHub(
        DesktopCallRouter router, string? ownerId = Owner, string? deviceId = Device, string connectionId = Conn)
    {
        var claims = new List<Claim>();
        if (ownerId is not null) claims.Add(new Claim(DesktopProtocol.OwnerIdClaim, ownerId));
        if (deviceId is not null) claims.Add(new Claim(DesktopProtocol.DeviceIdClaim, deviceId));

        var context = new Mock<HubCallerContext>();
        context.SetupGet(c => c.ConnectionId).Returns(connectionId);
        context.SetupGet(c => c.User).Returns(new ClaimsPrincipal(new ClaimsIdentity(claims, "device-token")));
        context.SetupGet(c => c.ConnectionAborted).Returns(CancellationToken.None);

        var hub = new DeviceHub(router, NullLogger<DeviceHub>.Instance) { Context = context.Object };
        return (hub, context);
    }

    [Fact]
    public async Task ТокенБезПарыВладелецУстройство_СоединениеРвётся()
    {
        var router = NewRouter(new SilentSender());
        var (hub, context) = NewHub(router, ownerId: Owner, deviceId: null);

        await hub.OnConnectedAsync();

        context.Verify(c => c.Abort(), Times.Once);
        router.Online(Owner).Should().BeEmpty();
    }

    [Fact]
    public async Task Подключение_РегистрируетСоединениеАОнлайнДаётТолькоHello()
    {
        var router = NewRouter(new SilentSender());
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
        var router = NewRouter(new SilentSender());
        var (hub, _) = NewHub(router);
        await hub.OnConnectedAsync();

        var act = () => hub.Hello(new DeviceHello(DesktopProtocol.Version + 1, [], "9.9.9"));

        (await act.Should().ThrowAsync<HubException>()).Which.Message.Should().ContainEquivalentOf("версия протокола");
        router.IsOnline(Owner, Device).Should().BeFalse();
    }

    [Fact]
    public async Task Отключение_УводитУстройствоВОфлайн()
    {
        var router = NewRouter(new SilentSender());
        var (hub, _) = NewHub(router);
        await hub.OnConnectedAsync();
        await hub.Hello(new DeviceHello(DesktopProtocol.Version, [], "1.0.0"));

        await hub.OnDisconnectedAsync(null);

        router.IsOnline(Owner, Device).Should().BeFalse();
    }

    [Fact]
    public async Task ДонесенияПоНеизвестномуВызову_HubException()
    {
        var router = NewRouter(new SilentSender());
        var (hub, _) = NewHub(router);
        await hub.OnConnectedAsync();
        await hub.Hello(new DeviceHello(DesktopProtocol.Version, [], "1.0.0"));

        await ((Func<Task>)(() => hub.Ack("нет-такого"))).Should().ThrowAsync<HubException>();
        await ((Func<Task>)(() => hub.Confirm("нет-такого"))).Should().ThrowAsync<HubException>();
        await ((Func<Task>)(() => hub.Decline("нет-такого"))).Should().ThrowAsync<HubException>();
        await ((Func<Task>)(() => hub.Awaiting("нет-такого", 5))).Should().ThrowAsync<HubException>();
        await ((Func<Task>)(() => hub.Progress("нет-такого", 1))).Should().ThrowAsync<HubException>();
    }

    [Fact]
    public async Task ДонесенияСвоегоВызова_ПроводятсяЧерезМаршрутизатор()
    {
        var sender = new SilentSender();
        var router = NewRouter(sender);
        var (hub, _) = NewHub(router);
        await hub.OnConnectedAsync();
        await hub.Hello(new DeviceHello(DesktopProtocol.Version, ["click"], "1.0.0"));

        using var cts = new CancellationTokenSource();
        var invoke = router.InvokeAsync(
            new DesktopCallRequest(Owner, Device, "chat-1", DesktopCallKinds.Act, DeviceName: "home"), cts.Token);
        var command = await sender.Sent.Task;

        await hub.Ack(command.CallId);
        await hub.Awaiting(command.CallId, 5);
        await hub.Progress(command.CallId, 2);
        await hub.Decline(command.CallId);

        var result = await invoke;
        result.Outcome.Should().Be(DesktopOutcomes.Denied);
    }
}
