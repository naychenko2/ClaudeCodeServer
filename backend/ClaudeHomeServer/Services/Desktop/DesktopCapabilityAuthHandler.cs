using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace ClaudeHomeServer.Services.Desktop;

/// <summary>
/// Именованная схема аутентификации грани десктопа (ADR-008, «Авторизация канала»).
/// Отдельная от дефолтной JwtBearer сознательно: /api/devices/* не должны открываться
/// ни пользовательским JWT, ни сервисным токеном владельца — иначе «ось выдачи» грани
/// оказывается барьером состава инструментов, а не авторизации.
/// Ставится на эндпоинты явно: [Authorize(AuthenticationSchemes = DesktopCapabilityAuthHandler.SchemeName)].
/// </summary>
public sealed class DesktopCapabilityAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "DesktopCapability";

    private const string BearerPrefix = "Bearer ";
    private readonly JwtService _jwt;

    public DesktopCapabilityAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        JwtService jwt) : base(options, logger, encoder)
    {
        _jwt = jwt;
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // Единственный вход — Authorization: Bearer <capability-токен>. Заголовок
        // X-Caller-Session-Id здесь не читается ВООБЩЕ: чат-вызыватель выводится из токена,
        // а подделать заголовок ход может тривиально.
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(AuthenticateResult.NoResult());

        var raw = header[BearerPrefix.Length..].Trim();
        if (raw.Length == 0) return Task.FromResult(AuthenticateResult.NoResult());

        var caller = _jwt.ValidateDesktopToken(raw);
        if (caller is null)
            return Task.FromResult(AuthenticateResult.Fail("Недействительный capability-токен канала устройств"));

        var identity = new ClaimsIdentity(caller.ToClaims(), SchemeName);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.Headers.WWWAuthenticate = $"Bearer realm=\"{JwtService.DesktopAudience}\"";
        return base.HandleChallengeAsync(properties);
    }
}

public static class DesktopCapabilityAuthExtensions
{
    /// <summary>
    /// Регистрирует схему грани десктопа рядом с дефолтной JwtBearer (вызов — в Program.cs).
    /// Дефолтной не делается никогда: обычный периметр [Authorize] остаётся на JwtBearer.
    /// </summary>
    public static AuthenticationBuilder AddDesktopCapabilityAuth(this AuthenticationBuilder builder) =>
        builder.AddScheme<AuthenticationSchemeOptions, DesktopCapabilityAuthHandler>(
            DesktopCapabilityAuthHandler.SchemeName, displayName: null, configureOptions: _ => { });
}
