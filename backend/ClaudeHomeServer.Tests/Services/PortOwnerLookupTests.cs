using System.Net;
using System.Net.Sockets;
using ClaudeHomeServer.Services;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// Поиск процесса, слушающего порт. Нужен, чтобы предложить «Стоп» сервису, поднятому вне
/// продукта или пережившему его перезапуск: своего объекта процесса у нас в этот момент нет.
///
/// Путь реализован для Windows (продукт по архитектуре живёт на хосте Windows); на прочих
/// платформах тесты честно скипаются, а не притворяются пройденными.
/// </summary>
public class PortOwnerLookupTests
{
    [SkippableFact]
    public void Находит_владельца_по_IPv4()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "поиск владельца порта реализован для Windows");

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;

            var owner = PortOwnerLookup.Find(port);

            owner.Should().NotBeNull();
            owner!.Pid.Should().Be(Environment.ProcessId, "слушает этот самый тестовый процесс");
        }
        finally { listener.Stop(); }
    }

    /// <summary>
    /// Ради этого случая и смотрим обе семьи: dev-серверы на Node по умолчанию слушают ::1,
    /// и владелец такого порта в IPv4-таблице не значится вовсе.
    /// </summary>
    [SkippableFact]
    public void Находит_владельца_по_IPv6()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "поиск владельца порта реализован для Windows");
        Skip.IfNot(Socket.OSSupportsIPv6, "IPv6 недоступен на этой машине");

        TcpListener? listener = null;
        try
        {
            listener = new TcpListener(IPAddress.IPv6Loopback, 0);
            listener.Start();
        }
        catch (SocketException)
        {
            Skip.If(true, "не удалось занять IPv6-порт");
        }

        try
        {
            var port = ((IPEndPoint)listener!.LocalEndpoint).Port;

            var owner = PortOwnerLookup.Find(port);

            owner.Should().NotBeNull();
            owner!.Pid.Should().Be(Environment.ProcessId);
        }
        finally { listener!.Stop(); }
    }

    [SkippableFact]
    public void Свободный_порт_владельца_не_имеет()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "поиск владельца порта реализован для Windows");

        // Заняли и сразу отпустили — номер точно свободен
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        PortOwnerLookup.Find(port).Should().BeNull();
    }
}
