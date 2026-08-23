using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;

namespace ClaudeHomeServer.Services.Desktop;

/// <summary>
/// Чат-вызыватель канала устройств, выведенный ИЗ capability-токена (ADR-008,
/// «Авторизация канала»). Единственный источник правды о том, кто зовёт /api/devices/*:
/// заголовок X-Caller-Session-Id в решении об авторизации не участвует вообще — он
/// подделывается ходом и остаётся чисто диагностическим.
/// </summary>
/// <param name="OwnerId">Владелец (claim sub) — per-owner изоляция реестра устройств.</param>
/// <param name="SessionId">Чат, которому выдана грань: сверяется с чатом активного сеанса рук.</param>
/// <param name="DeviceId">
/// Устройство, если оно известно на момент выдачи. На запуске CLI сеанса рук может ещё не быть
/// (он стартует с самого устройства позже), поэтому claim необязателен; адресат всё равно
/// определяется активным сеансом, а не токеном.
/// </param>
public sealed record DesktopCaller(string OwnerId, string SessionId, string? DeviceId)
{
    /// <summary>Чат-вызыватель (registered claim "sid").</summary>
    public const string SessionClaim = "sid";

    /// <summary>Устройство (claim "did").</summary>
    public const string DeviceClaim = "did";

    /// <summary>
    /// Собирает вызывателя из принципала схемы DesktopCapability.
    /// Без sub или sid — не вызыватель: null, а не запись с пустыми полями.
    /// </summary>
    public static DesktopCaller? FromPrincipal(ClaimsPrincipal? principal)
    {
        var ownerId = principal?.FindFirstValue(JwtRegisteredClaimNames.Sub);
        var sessionId = principal?.FindFirstValue(SessionClaim);
        if (string.IsNullOrEmpty(ownerId) || string.IsNullOrEmpty(sessionId)) return null;

        var deviceId = principal!.FindFirstValue(DeviceClaim);
        return new DesktopCaller(ownerId, sessionId, string.IsNullOrEmpty(deviceId) ? null : deviceId);
    }

    /// <summary>Claims для выдачи токена — обратная сторона <see cref="FromPrincipal"/>.</summary>
    public IEnumerable<Claim> ToClaims()
    {
        yield return new Claim(JwtRegisteredClaimNames.Sub, OwnerId);
        yield return new Claim(SessionClaim, SessionId);
        if (!string.IsNullOrEmpty(DeviceId)) yield return new Claim(DeviceClaim, DeviceId);
    }
}
