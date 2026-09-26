using ClaudeHomeServer.DeviceAgent.Exec;
using ClaudeHomeServer.DeviceAgent.Hosting;
using ClaudeHomeServer.DeviceAgent.Pairing;
using ClaudeHomeServer.DeviceAgent.Sidecar;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.DeviceAgent.Tests.Pairing;

/// <summary>
/// Одно правило на все каналы агента: https, или http только на петле. Код, токен
/// устройства и команды по открытому каналу из сети не ходят.
/// </summary>
public class ServerChannelTests
{
    private sealed record Device(Uri ServerUri) : IDeviceIdentity
    {
        public string DeviceToken => "tok";
        public string Fingerprint => "fp";
    }

    [Theory]
    [InlineData("https://home.example/", true)]
    [InlineData("https://192.168.1.5:5001/", true)]
    [InlineData("http://localhost:5001/", true)]
    [InlineData("http://127.0.0.1:5000/", true)]
    [InlineData("http://[::1]:5000/", true)]
    [InlineData("http://192.168.1.5/", false)]
    [InlineData("http://home.example/", false)]
    // Не http(s) — не канал вовсе, даже на петле
    [InlineData("ftp://127.0.0.1/", false)]
    public void Https_или_петля(string server, bool expected) =>
        ServerChannel.IsSecure(new Uri(server)).Should().Be(expected);

    [Fact]
    public async Task Хаб_по_открытому_каналу_не_подключается()
    {
        await using var hub = new HubControlConnection(new Device(new Uri("http://192.168.1.5:5000/")), NullLogger.Instance);

        // Без повторов: отказ приходит сразу, а не после цикла переподключений
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var act = () => hub.ConnectAsync(timeout.Token);

        await act.Should().ThrowAsync<InsecureServerException>();
    }

    [Fact]
    public async Task Канал_исполнения_по_открытому_каналу_не_открывается()
    {
        var act = () => new ExecSocketConnector(new Device(new Uri("http://192.168.1.5:5000/")))
            .ConnectAsync("exec-1", CancellationToken.None);

        await act.Should().ThrowAsync<ExecLinkRefusedException>().WithMessage("*HTTPS*");
    }

    [Fact]
    public async Task Канал_исполнения_на_петле_пробует_подключиться()
    {
        // Порт закрыт: падение сетевое, а не отказ по правилу канала
        var act = () => new ExecSocketConnector(new Device(new Uri("http://127.0.0.1:1/")))
            .ConnectAsync("exec-1", CancellationToken.None);

        var thrown = await act.Should().ThrowAsync<Exception>();
        thrown.Which.Should().NotBeOfType<ExecLinkRefusedException>();
    }
}
