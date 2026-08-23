using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace ClaudeHomeServer.Services.Desktop;

/// <summary>
/// Схема аутентификации самого устройства (ADR-008, «Аутентификация и транспорт»):
/// канал клиента (SignalR-подключение, отдача результата вызова) авторизуется device-токеном,
/// а не пользовательским JWT и не сервисным токеном владельца. Пользовательский токен на
/// клиент не копируется вовсе — у клиента есть только его собственный device-токен.
///
/// Заголовки: <c>Authorization: Device {токен}</c> плюс обязательный
/// <c>X-Device-Fingerprint</c> — отпечаток машины СВЕРЯЕТСЯ на каждом запросе, иначе
/// утёкший токен работал бы с любой машины.
///
/// Ставится на эндпоинты явно:
/// <c>[Authorize(AuthenticationSchemes = DesktopDeviceAuthHandler.SchemeName)]</c>.
/// </summary>
public sealed class DesktopDeviceAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "DesktopDevice";

    /// <summary>Префикс схемы в Authorization — намеренно не Bearer: это другой класс токена.</summary>
    public const string TokenPrefix = "Device ";

    public const string FingerprintHeader = "X-Device-Fingerprint";

    /// <summary>Устройство в принципале (то же имя claim, что у capability-токена канала).</summary>
    public const string DeviceIdClaim = "did";

    /// <summary>Версия device-токена: по ней видно, что принципал построен не на прошлой выдаче.</summary>
    public const string DeviceTokenVersionClaim = "dtv";

    private readonly DeviceRegistry _devices;

    public DesktopDeviceAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        DeviceRegistry devices) : base(options, logger, encoder)
    {
        _devices = devices;
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith(TokenPrefix, StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(AuthenticateResult.NoResult());

        var raw = header[TokenPrefix.Length..].Trim();
        var fingerprint = Request.Headers[FingerprintHeader].ToString();

        var device = _devices.Authenticate(raw, fingerprint);
        if (device is null)
            return Task.FromResult(AuthenticateResult.Fail("Устройство не опознано"));

        var identity = new ClaimsIdentity(
            [
                new Claim(JwtRegisteredClaimNames.Sub, device.OwnerId),
                new Claim(DeviceIdClaim, device.Id),
                new Claim(ClaimTypes.Name, device.Name),
                new Claim(DeviceTokenVersionClaim, device.TokenVersion.ToString()),
            ],
            SchemeName);

        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.Headers.WWWAuthenticate = "Device";
        return base.HandleChallengeAsync(properties);
    }
}

public static class DesktopDeviceAuthExtensions
{
    /// <summary>
    /// Регистрирует схему устройства рядом с дефолтной JwtBearer (вызов — в Program.cs).
    /// Дефолтной не делается никогда: обычный периметр [Authorize] остаётся на JwtBearer.
    /// </summary>
    public static AuthenticationBuilder AddDesktopDeviceAuth(this AuthenticationBuilder builder) =>
        builder.AddScheme<AuthenticationSchemeOptions, DesktopDeviceAuthHandler>(
            DesktopDeviceAuthHandler.SchemeName, displayName: null, configureOptions: _ => { });
}
