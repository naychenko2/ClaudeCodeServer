using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Desktop;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services.Desktop;

// Реестр соединений хаба устройств (ADR-016): до Hello устройство командам недоступно,
// Hello делает его онлайн и будит наблюдателей, разрыв гасит.
public class DeviceConnectionRegistryTests
{
    private const string Owner = "owner-1";
    private const string Device = "device-1";
    private const string Conn = "conn-1";

    private sealed class RecordingObserver : IDeviceConnectionObserver
    {
        public readonly List<DeviceConnection> Online = [];
        public readonly List<DeviceConnection> Offline = [];

        public Task OnDeviceOnlineAsync(DeviceConnection connection, CancellationToken ct = default)
        {
            Online.Add(connection);
            return Task.CompletedTask;
        }

        public Task OnDeviceOfflineAsync(DeviceConnection connection, CancellationToken ct = default)
        {
            Offline.Add(connection);
            return Task.CompletedTask;
        }
    }

    private static (DeviceConnectionRegistry Registry, RecordingObserver Observer) Build()
    {
        var observer = new RecordingObserver();
        return (new DeviceConnectionRegistry([observer], NullLogger<DeviceConnectionRegistry>.Instance), observer);
    }

    [Fact]
    public async Task Hello_ДелаетУстройствоОнлайнИЗоветНаблюдателя()
    {
        var (registry, observer) = Build();
        registry.RegisterConnection(Conn, Owner, Device);

        registry.IsOnline(Owner, Device).Should().BeFalse("до Hello устройство командам недоступно");

        var ack = await registry.HelloAsync(Conn, new DeviceHello(DesktopProtocol.Version, [], "1.0.0"));

        ack.ProtocolVersion.Should().Be(DesktopProtocol.Version);
        registry.IsOnline(Owner, Device).Should().BeTrue();
        registry.Find(Owner, Device)!.ConnectionId.Should().Be(Conn);
        observer.Online.Should().ContainSingle().Which.DeviceId.Should().Be(Device);
    }

    [Fact]
    public async Task Разрыв_ГаситУстройствоИЗоветНаблюдателя()
    {
        var (registry, observer) = Build();
        registry.RegisterConnection(Conn, Owner, Device);
        await registry.HelloAsync(Conn, new DeviceHello(DesktopProtocol.Version, [], "1.0.0"));

        await registry.RemoveConnectionAsync(Conn);

        registry.IsOnline(Owner, Device).Should().BeFalse();
        observer.Offline.Should().ContainSingle().Which.DeviceId.Should().Be(Device);
    }

    [Fact]
    public async Task ПовторноеПодключение_ВытесняетПрежнееСоединениеУстройства()
    {
        var (registry, observer) = Build();
        registry.RegisterConnection("old", Owner, Device);
        await registry.HelloAsync("old", new DeviceHello(DesktopProtocol.Version, [], "1.0.0"));

        registry.RegisterConnection("new", Owner, Device);
        await registry.HelloAsync("new", new DeviceHello(DesktopProtocol.Version, [], "1.0.0"));

        registry.Find(Owner, Device)!.ConnectionId.Should().Be("new",
            "одно устройство — одно соединение: push не должен уйти в зависшее прежнее");
        observer.Online.Should().HaveCount(2);
    }
}
