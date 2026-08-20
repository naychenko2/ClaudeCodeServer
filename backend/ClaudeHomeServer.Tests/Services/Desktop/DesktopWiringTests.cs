using ClaudeHomeServer.Services.Desktop;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Services.Desktop;

/// <summary>
/// Сборка грани десктопа из настоящего контейнера (ADR-008). Под-задачи первой волны
/// писались в изоляции, и разъехавшаяся склейка — не теоретический риск: не
/// зарегистрированный <see cref="IDeviceCommandSender"/> или незнакомое имя схемы
/// авторизации ломают грань ТОЛЬКО в рантайме, юнит-тесты про них ничего не знают.
/// </summary>
public class DesktopWiringTests(TestWebApplicationFactory factory) : IClassFixture<TestWebApplicationFactory>
{
    [Fact]
    public void СлужбыГрани_РезолвятсяИзКонтейнера()
    {
        using var scope = factory.Services.CreateScope();
        var sp = scope.ServiceProvider;

        sp.GetRequiredService<DeviceRegistry>().Should().NotBeNull();
        sp.GetRequiredService<DevicePairingService>().Should().NotBeNull();
        sp.GetRequiredService<IDeviceCommandSender>().Should().NotBeNull();
        sp.GetRequiredService<DesktopCallRouter>().Should().NotBeNull();
        sp.GetRequiredService<DesktopHandsSessionService>().Should().NotBeNull();
        sp.GetRequiredService<DesktopAccessGate>().Should().NotBeNull();
    }

    /// <summary>
    /// Разрыв соединения гасит сеанс рук: наблюдатель канала обязан быть ТЕМ ЖЕ экземпляром
    /// службы сеансов, а не вторым — иначе гасился бы чужой пустой реестр.
    /// </summary>
    [Fact]
    public void НаблюдательСоединений_ЭтоСлужбаСеансов()
    {
        using var scope = factory.Services.CreateScope();
        var sp = scope.ServiceProvider;

        sp.GetRequiredService<IDeviceConnectionObserver>()
            .Should().BeSameAs(sp.GetRequiredService<DesktopHandsSessionService>());
    }

    /// <summary>
    /// Обе схемы грани зарегистрированы под теми именами, которыми их называют эндпоинты:
    /// незарегистрированная схема в [Authorize] — 500 на первом же запросе.
    /// </summary>
    [Fact]
    public async Task СхемыАвторизацииГрани_Зарегистрированы()
    {
        using var scope = factory.Services.CreateScope();
        var schemes = scope.ServiceProvider.GetRequiredService<IAuthenticationSchemeProvider>();

        (await schemes.GetSchemeAsync(DesktopCapabilityAuthHandler.SchemeName)).Should().NotBeNull();
        (await schemes.GetSchemeAsync(DesktopDeviceAuthHandler.SchemeName)).Should().NotBeNull();
        (await schemes.GetSchemeAsync(ClaudeHomeServer.Protocol.DesktopProtocol.DeviceTokenScheme))
            .Should().NotBeNull("канал устройств авторизуется той же схемой, что и остальная грань");
    }
}
