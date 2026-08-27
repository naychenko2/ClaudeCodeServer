using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Desktop;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Controllers;

/// <summary>
/// Реестр устройств десктопного агента и сопряжение (ADR-008). Управление устройствами —
/// работа человека в вебе, поэтому обычный [Authorize]; обмен кода на токен анонимен —
/// у клиента на этот момент нет вообще никаких учётных данных.
///
/// Сервисный токен владельца сюда не пускается ни на одну ручку: он лежит в env КАЖДОГО
/// хода (включая ночной tasks-executor), и с ним ход завёл бы себе устройство или снял
/// чужое. Вызовы канала с capability-токеном чата живут отдельно от этого контроллера.
/// </summary>
[ApiController]
[Authorize]
[Route("api/devices")]
public class DevicesController(
    DeviceRegistry registry, DevicePairingService pairing, UserStore users) : ControllerBase
{
    private string UserId => User.FindFirstValue(JwtRegisteredClaimNames.Sub)!;

    [HttpGet]
    public IActionResult List()
    {
        if (ServiceTokenRefusal() is { } refusal) return refusal;
        return Ok(registry.GetByOwner(UserId).Select(ToDto));
    }

    /// <summary>Код сопряжения: 8 символов, живёт 5 минут, привязан к этой веб-сессии.</summary>
    [HttpPost("pairing")]
    public IActionResult StartPairing()
    {
        if (ServiceTokenRefusal() is { } refusal) return refusal;
        if (InsecureChannelRefusal() is { } insecure) return insecure;

        var code = pairing.Start(UserId, WebSessionKey(), WebTokenVersion());
        return Ok(new
        {
            code = code.Code,
            expiresAt = code.ExpiresAt,
            attemptsLeft = code.AttemptsLeft,
            hostFingerprint = MachineFingerprint.OfHost(),
        });
    }

    /// <summary>Заявка видна только выпустившей её веб-сессии — 404 в остальных случаях.</summary>
    [HttpGet("pairing")]
    public IActionResult PairingStatus()
    {
        if (ServiceTokenRefusal() is { } refusal) return refusal;
        if (InsecureChannelRefusal() is { } insecure) return insecure;

        var code = pairing.GetPending(UserId, WebSessionKey());
        if (code is null) return NotFound(new { error = "Активной заявки на сопряжение нет" });
        return Ok(new { code = code.Code, expiresAt = code.ExpiresAt, attemptsLeft = code.AttemptsLeft });
    }

    [HttpDelete("pairing")]
    public IActionResult CancelPairing()
    {
        if (ServiceTokenRefusal() is { } refusal) return refusal;
        return pairing.Cancel(UserId, WebSessionKey())
            ? NoContent()
            : NotFound(new { error = "Активной заявки на сопряжение нет" });
    }

    public record RenameRequest(string Name);

    [HttpPatch("{id}")]
    public IActionResult Rename(string id, [FromBody] RenameRequest request)
    {
        if (ServiceTokenRefusal() is { } refusal) return refusal;
        try
        {
            var device = registry.Rename(UserId, id, request.Name);
            return device is null ? NotFound(new { error = "Устройство не найдено" }) : Ok(ToDto(device));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>Отзыв устройства: запись остаётся надгробием, токен умирает немедленно.</summary>
    [HttpDelete("{id}")]
    public IActionResult Revoke(string id)
    {
        if (ServiceTokenRefusal() is { } refusal) return refusal;
        return registry.Revoke(UserId, id)
            ? NoContent()
            : NotFound(new { error = "Устройство не найдено" });
    }

    public record PairRequest(string Code, string Name, string Fingerprint, string? ClientVersion);

    /// <summary>
    /// Обмен кода сопряжения на device-токен. Ответ содержит ТОЛЬКО учётные данные самого
    /// устройства: пользовательский JWT и API-ключ владельца на клиент не уезжают никогда
    /// (сторож — DevicesControllerTests).
    /// </summary>
    [AllowAnonymous]
    [HttpPost("pair")]
    public IActionResult Pair([FromBody] PairRequest request)
    {
        if (InsecureChannelRefusal() is { } insecure) return insecure;

        var result = pairing.Redeem(
            DevicePairingService.PairEndpoint, request.Code, request.Name,
            request.Fingerprint, request.ClientVersion);

        return result.Status switch
        {
            DevicePairingStatus.Ok => Ok(new
            {
                deviceId = result.Device!.Id,
                name = result.Device.Name,
                deviceToken = result.Token,
                tokenVersion = result.Device.TokenVersion,
            }),
            // Слишком много попыток — 429: это ровно тот случай, ради которого счётчик
            // и заведён, и клиенту незачем гадать по тексту
            DevicePairingStatus.TooManyAttempts => StatusCode(
                StatusCodes.Status429TooManyRequests, new { error = result.Error }),
            DevicePairingStatus.SameHost or DevicePairingStatus.SessionGone => StatusCode(
                StatusCodes.Status403Forbidden, new { error = result.Error }),
            _ => BadRequest(new { error = result.Error }),
        };
    }

    private static object ToDto(DesktopDevice device) => new
    {
        id = device.Id,
        name = device.Name,
        // Отпечаток машины наружу отдаём урезанным: полный нужен только клиенту, а в
        // списке он служит человеку приметой «это та самая машина»
        fingerprint = device.MachineFingerprint.Length >= 12
            ? device.MachineFingerprint[..12]
            : device.MachineFingerprint,
        clientVersion = device.ClientVersion,
        createdAt = device.CreatedAt,
        lastSeenAt = device.LastSeenAt,
        revoked = device.Revoked,
        revokedAt = device.RevokedAt,
        tokenVersion = device.TokenVersion,
    };

    // Сервисный токен владельца (typ=svc) — не человек за клавиатурой: устройствами
    // распоряжается только веб-сессия
    private IActionResult? ServiceTokenRefusal() =>
        User.FindFirstValue(JwtService.TokenKindClaim) == JwtService.ServiceTokenKind
            ? StatusCode(StatusCodes.Status403Forbidden,
                new { error = "Устройствами распоряжается только веб-сессия владельца" })
            : null;

    private IActionResult? InsecureChannelRefusal() =>
        DeviceChannelGuard.IsSecure(Request)
            ? null
            : StatusCode(StatusCodes.Status403Forbidden, new
            {
                error = "Сопряжение устройства доступно только по HTTPS: " +
                        "по открытому каналу код и токен не выдаются",
            });

    // Отпечаток веб-сессии: сам токен наружу не отдаём и не храним — только его хеш.
    // Ровно этой сессии принадлежит выпущенный код (ADR-008: «код привязан к
    // инициировавшей веб-сессии»).
    private string WebSessionKey()
    {
        var raw = Request.Headers.Authorization.ToString();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();
    }

    // Версия сессий пользователя из токена: смена пароля бампает её в сторе, и код,
    // выпущенный прежней сессией, перестаёт обмениваться
    private int WebTokenVersion() =>
        int.TryParse(User.FindFirstValue(JwtService.TokenVersionClaim), out var version)
            ? version
            : users.GetById(UserId)?.TokenVersion ?? 0;
}
