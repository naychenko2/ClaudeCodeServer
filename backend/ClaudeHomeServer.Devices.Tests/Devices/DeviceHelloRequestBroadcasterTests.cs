using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Devices;
using ClaudeHomeServer.Services.Execution;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services.Devices;

// Смена версии CLI хоста → каждому подключённому устройству уходит RequestHello; версию push не несёт.
public class DeviceHelloRequestBroadcasterTests
{
    private sealed class FakeHostCli : IHostCliVersion
    {
        public string? Current => "2.1.311";
        public event Action? Changed;
        public void Raise() => Changed?.Invoke();
    }

    private sealed class RecordingSender(params string[] failing) : IDeviceHelloRequestSender
    {
        public readonly List<string> Sent = [];
        public Task SendRequestHelloAsync(string connectionId, CancellationToken ct = default)
        {
            if (failing.Contains(connectionId)) throw new InvalidOperationException("соединение отвалилось");
            lock (Sent) Sent.Add(connectionId);
            return Task.CompletedTask;
        }
    }

    private static IConfiguration Config(string? pin) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { [DeviceHarnessPolicy.CliVersionKey] = pin })
        .Build();

    private static async Task<DeviceConnectionRegistry> RegistryWithAsync(params string[] connIds)
    {
        var registry = new DeviceConnectionRegistry([], NullLogger<DeviceConnectionRegistry>.Instance);
        foreach (var id in connIds)
        {
            registry.RegisterConnection(id, "owner", "dev-" + id);
            await registry.HelloAsync(id, new DeviceHello(DesktopProtocol.Version, [], "1.0.0"));
        }
        return registry;
    }

    [Fact]
    public async Task СменаВерсииХоста_RequestHelloУходитКаждомуПодключённому()
    {
        var registry = await RegistryWithAsync("c1", "c2");
        registry.RegisterConnection("c3", "owner", "dev-c3"); // не представился — не получает
        var sender = new RecordingSender();
        var sut = new DeviceHelloRequestBroadcaster(new FakeHostCli(), registry, sender, Config(""),
            NullLogger<DeviceHelloRequestBroadcaster>.Instance);

        await sut.BroadcastAsync();

        sender.Sent.Should().BeEquivalentTo(["c1", "c2"]);
    }

    [Fact]
    public async Task ПрибитаяВКонфигеВерсия_НеРассылаем()
    {
        var registry = await RegistryWithAsync("c1");
        var sender = new RecordingSender();
        var sut = new DeviceHelloRequestBroadcaster(new FakeHostCli(), registry, sender, Config("2.1.280"),
            NullLogger<DeviceHelloRequestBroadcaster>.Instance);

        await sut.BroadcastAsync();

        sender.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task ОтвалившесяУстройство_НеМешаетОстальным()
    {
        var registry = await RegistryWithAsync("c1", "c2", "c3");
        var sender = new RecordingSender("c1");
        var sut = new DeviceHelloRequestBroadcaster(new FakeHostCli(), registry, sender, Config(null),
            NullLogger<DeviceHelloRequestBroadcaster>.Instance);

        await sut.BroadcastAsync();

        sender.Sent.Should().BeEquivalentTo(["c2", "c3"]);
    }

    [Fact]
    public async Task ПодпискаНаСобытие_СрабатываетОтChangedХоста()
    {
        var registry = await RegistryWithAsync("c1");
        var sender = new RecordingSender();
        var host = new FakeHostCli();
        var sut = new DeviceHelloRequestBroadcaster(host, registry, sender, Config(""),
            NullLogger<DeviceHelloRequestBroadcaster>.Instance);
        await sut.StartAsync(default);

        host.Raise();

        SpinWait.SpinUntil(() => sender.Sent.Count > 0, TimeSpan.FromSeconds(5)).Should().BeTrue();
        await sut.StopAsync(default);
    }
}
