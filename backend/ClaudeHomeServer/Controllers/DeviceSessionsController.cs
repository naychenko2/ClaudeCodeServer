using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Desktop;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Controllers;

/// <summary>Заявка устройства на старт сеанса: чат человек выбирает из очереди сам.</summary>
public sealed record DesktopHandsStartRequest(string ChatSessionId);

/// <summary>Повод остановки, названный клиентом: закрытие окна или «Стоп» человека.</summary>
public sealed record DesktopHandsStopRequest(string? Reason = null);

/// <summary>
/// Сеанс рук со стороны устройства (ADR-008, «Сеанс рук и согласие»).
///
/// Сеанс стартует ТОЛЬКО отсюда — с самой машины, под токеном устройства: у агента и у
/// веб-морды кнопки «начать» нет вовсе. Клиент показывает очередь заявок с ИМЕНЕМ чата,
/// проекта и персоны, человек выбирает чат — и это единственная дверь.
///
/// Схемы авторизации разные у разных действий, поэтому атрибута на контроллере нет:
/// устройство ходит под своей схемой, а «Стоп» из шапки чата — под обычным JWT владельца.
/// </summary>
[ApiController]
[Route("api/devices/hands")]
public sealed class DeviceSessionsController(
    DesktopHandsSessionService hands,
    IDesktopDeviceDirectory devices) : ControllerBase
{
    // Токен устройства и capability-токен канала называют claims по-разному (sub/did против
    // ownerId/deviceId) — читаем оба имени, решение от этого не зависит.
    private string? DeviceOwnerId =>
        User.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? User.FindFirstValue(DesktopProtocol.OwnerIdClaim);

    private string? DeviceId =>
        User.FindFirstValue(DesktopDeviceAuthHandler.DeviceIdClaim) ?? User.FindFirstValue(DesktopProtocol.DeviceIdClaim);

    /// <summary>Очередь заявок владельца: имя чата, проекта и персоны — по ним человек и выбирает.</summary>
    [HttpGet("requests")]
    [Authorize(AuthenticationSchemes = DesktopDeviceAuthHandler.SchemeName)]
    public IActionResult Requests()
    {
        if (DeviceOwnerId is not { } ownerId) return Unauthorized();

        return Ok(hands.RequestsFor(ownerId).Select(r => new
        {
            chatSessionId = r.ChatSessionId,
            chat = r.ChatName,
            project = r.ProjectName,
            persona = r.PersonaName,
            requestedAt = r.RequestedAt
        }));
    }

    /// <summary>Текущий сеанс ЭТОГО устройства, либо null — руки никому не отданы.</summary>
    [HttpGet]
    [Authorize(AuthenticationSchemes = DesktopDeviceAuthHandler.SchemeName)]
    public IActionResult Current()
    {
        if (DeviceOwnerId is not { } ownerId || DeviceId is not { } deviceId) return Unauthorized();
        return Ok(Describe(hands.ForDevice(ownerId, deviceId)));
    }

    /// <summary>
    /// Начать сеанс. Устройство берётся из токена, чат — из тела: подставить чужой чат
    /// нельзя, право на грань проверяется тем же гейтом, что и каждый вызов.
    /// </summary>
    [HttpPost("start")]
    [Authorize(AuthenticationSchemes = DesktopDeviceAuthHandler.SchemeName)]
    public async Task<IActionResult> Start([FromBody] DesktopHandsStartRequest req, CancellationToken ct)
    {
        if (DeviceOwnerId is not { } ownerId || DeviceId is not { } deviceId) return Unauthorized();
        if (string.IsNullOrWhiteSpace(req.ChatSessionId)) return BadRequest(new { message = "Не указан чат" });

        var device = devices.FindById(ownerId, deviceId);
        if (device is null) return Unauthorized();

        var result = await hands.StartAsync(ownerId, deviceId, device.Name, req.ChatSessionId.Trim(), ct);
        return result.Started
            ? Ok(Describe(result.Session))
            : Conflict(new { outcome = result.Outcome, message = result.Message });
    }

    /// <summary>
    /// Погасить сеанс этого устройства. Повод называет клиент: закрытие окна оболочки
    /// (жизнь в трее закрытием НЕ считается) или «Стоп» человека в трее.
    /// </summary>
    [HttpPost("stop")]
    [Authorize(AuthenticationSchemes = DesktopDeviceAuthHandler.SchemeName)]
    public async Task<IActionResult> Stop([FromBody] DesktopHandsStopRequest? req, CancellationToken ct)
    {
        if (DeviceOwnerId is not { } ownerId || DeviceId is not { } deviceId) return Unauthorized();

        var reason = req?.Reason == DesktopHandsEndReasons.ClientClosed
            ? DesktopHandsEndReasons.ClientClosed
            : DesktopHandsEndReasons.Stopped;
        var stopped = await hands.StopForDeviceAsync(ownerId, deviceId, reason, ct);
        return Ok(new { stopped });
    }

    /// <summary>
    /// «Стоп» из шапки чата — вне канала агента, под обычным JWT владельца. Разрыв делает
    /// сервер: клиента об этом уведомляет отмена вызовов и статус сеанса.
    /// </summary>
    [HttpPost("chat/{chatSessionId}/stop")]
    [Authorize]
    public async Task<IActionResult> StopFromChat(string chatSessionId, CancellationToken ct)
    {
        var userId = User.FindFirstValue(JwtRegisteredClaimNames.Sub);
        var session = hands.ForChat(chatSessionId);
        // Чужой сеанс — 404, а не 403: подтверждать его существование незачем.
        if (userId is null || session is null || session.OwnerId != userId) return NotFound();

        await hands.StopAsync(chatSessionId, DesktopHandsEndReasons.Stopped, ct);
        return Ok(new { stopped = true });
    }

    private static object? Describe(DesktopHandsSession? s) => s is null ? null : new
    {
        chatSessionId = s.ChatSessionId,
        chat = s.ChatName,
        device = s.DeviceName,
        startedAt = s.StartedAt,
        expiresAt = s.ExpiresAt,
        idleDeadlineAt = s.IdleDeadline,
        hardDeadlineAt = s.HardDeadline
    };
}
