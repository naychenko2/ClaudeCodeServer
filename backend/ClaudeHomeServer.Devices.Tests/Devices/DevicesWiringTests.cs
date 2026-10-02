using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Devices;
using ClaudeHomeServer.Services.Execution;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Services.Devices;

/// <summary>
/// Сборка канала устройства (ADR-016) из настоящего контейнера. Разъехавшаяся склейка — не
/// теоретический риск: не зарегистрированная служба или незнакомое имя схемы авторизации
/// ломают канал ТОЛЬКО в рантайме, юнит-тесты про них ничего не знают.
/// </summary>
public class DevicesWiringTests(TestWebApplicationFactory factory) : IClassFixture<TestWebApplicationFactory>
{
    [Fact]
    public void СлужбыКанала_РезолвятсяИзКонтейнера()
    {
        using var scope = factory.Services.CreateScope();
        var sp = scope.ServiceProvider;

        sp.GetRequiredService<DeviceRegistry>().Should().NotBeNull();
        sp.GetRequiredService<DevicePairingService>().Should().NotBeNull();
        sp.GetRequiredService<DeviceConnectionRegistry>().Should().NotBeNull();
        sp.GetRequiredService<AgentTicketService>().Should().NotBeNull();
        sp.GetRequiredService<IDeviceExecChannel>()
            .Should().BeSameAs(sp.GetRequiredService<DeviceExecChannel>(), "шов Core — форвард на тот же синглтон");
    }

    /// <summary>
    /// Выход устройства в онлайн будит ждавшую его работу: наблюдатель реестра соединений
    /// обязан быть ТЕМ ЖЕ экземпляром диспетчера, что крутит поминутный проход, а не вторым.
    /// </summary>
    [Fact]
    public void НаблюдательСоединений_ЭтоДиспетчерВыходаВОнлайн()
    {
        using var scope = factory.Services.CreateScope();
        var sp = scope.ServiceProvider;

        sp.GetServices<IDeviceConnectionObserver>().Should().ContainSingle()
            .Which.Should().BeSameAs(sp.GetRequiredService<DeviceOnlineDispatcher>());
    }

    /// <summary>
    /// Схема токена устройства зарегистрирована под тем именем, которым её называют эндпоинты:
    /// незарегистрированная схема в [Authorize] — 500 на первом же запросе.
    /// </summary>
    [Fact]
    public async Task СхемаАвторизацииУстройства_Зарегистрирована()
    {
        using var scope = factory.Services.CreateScope();
        var schemes = scope.ServiceProvider.GetRequiredService<IAuthenticationSchemeProvider>();

        (await schemes.GetSchemeAsync(DeviceAuthHandler.SchemeName)).Should().NotBeNull();
        (await schemes.GetSchemeAsync(ClaudeHomeServer.Protocol.DesktopProtocol.DeviceTokenScheme))
            .Should().NotBeNull("канал устройств авторизуется схемой токена устройства");
    }
}
