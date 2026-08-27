using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
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
    IDesktopDeviceDirectory devices,
    IDesktopChatDirectory chats) : ControllerBase
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
    /// Статус сеанса для шапки чата — под обычным JWT владельца. Отдельная ручка нужна
    /// потому, что событие desktop_session эфемерное: перезагрузив страницу, веб-морда о
    /// живом сеансе больше ниоткуда не узнает и бейдж «руки на home» погас бы на глазах у
    /// работающих рук. Чужой чат — 404: подтверждать его существование незачем.
    /// </summary>
    [HttpGet("chat/{chatSessionId}")]
    [Authorize]
    public IActionResult ChatStatus(string chatSessionId)
    {
        if (ServiceTokenRefusal() is { } refusal) return refusal;
        if (OwnedChat(chatSessionId) is not { } chat) return NotFound();

        var session = hands.ForChat(chatSessionId);
        var request = hands.RequestsFor(chat.OwnerId).FirstOrDefault(r => r.ChatSessionId == chatSessionId);

        return Ok(new
        {
            active = session is not null,
            session = Describe(session),
            // Заявка в очереди — то, что человек видит в окне клиента. Бейдж этим объясняет
            // ожидание («примите заявку на устройстве»), а не молчит.
            requestedAt = request?.RequestedAt,
            // Почему грань не выдана — та же формулировка, что получает модель; null — выдана
            facetRefusal = chat.FacetRefusal()
        });
    }

    /// <summary>
    /// Заявка на сеанс из веб-морды. Веб может ТОЛЬКО попросить: сеанс стартует лишь с
    /// самого устройства (ADR-008), поэтому ветки, создающей сеанс, здесь нет и быть не
    /// может. Ровно ту же заявку ставит гейт, когда вызов пришёл в чат без рук.
    /// </summary>
    [HttpPost("chat/{chatSessionId}/request")]
    [Authorize]
    public IActionResult RequestFromChat(string chatSessionId)
    {
        if (ServiceTokenRefusal() is { } refusal) return refusal;
        if (OwnedChat(chatSessionId) is not { } chat) return NotFound();
        if (chat.FacetRefusal() is string facetOff)
            return Conflict(new { outcome = DesktopGateOutcomes.FacetOff, message = facetOff });

        // Сеанс уже идёт — просить нечего, отвечаем текущим состоянием
        if (hands.ForChat(chatSessionId) is { } active)
            return Ok(new { requested = false, active = true, session = Describe(active) });

        var request = hands.Enqueue(chat);
        return Ok(new { requested = true, active = false, requestedAt = request.RequestedAt });
    }

    /// <summary>
    /// «Стоп» из шапки чата — вне канала агента, под обычным JWT владельца. Разрыв делает
    /// сервер: клиента об этом уведомляет отмена вызовов и статус сеанса.
    /// </summary>
    [HttpPost("chat/{chatSessionId}/stop")]
    [Authorize]
    public async Task<IActionResult> StopFromChat(string chatSessionId, CancellationToken ct)
    {
        if (ServiceTokenRefusal() is { } refusal) return refusal;

        var userId = User.FindFirstValue(JwtRegisteredClaimNames.Sub);
        var session = hands.ForChat(chatSessionId);
        // Чужой сеанс — 404, а не 403: подтверждать его существование незачем.
        if (userId is null || session is null || session.OwnerId != userId) return NotFound();

        await hands.StopAsync(chatSessionId, DesktopHandsEndReasons.Stopped, ct);
        return Ok(new { stopped = true });
    }

    // Сервисный JWT владельца лежит в env КАЖДОГО хода, включая ночной tasks-executor.
    // Веб-половина грани — статус, заявка и «Стоп» — работа человека в браузере: ход не
    // должен уметь ни просить руки, ни гасить чужой сеанс.
    private IActionResult? ServiceTokenRefusal() =>
        User.FindFirstValue(JwtService.TokenKindClaim) == JwtService.ServiceTokenKind
            ? StatusCode(StatusCodes.Status403Forbidden,
                new { error = "Сеансом рук распоряжается только веб-сессия владельца" })
            : null;

    // Чат владельца, либо null: чужой и исчезнувший чат снаружи неразличимы — оба дают 404.
    private DesktopChatInfo? OwnedChat(string chatSessionId)
    {
        var userId = User.FindFirstValue(JwtRegisteredClaimNames.Sub);
        if (string.IsNullOrEmpty(userId)) return null;
        var chat = chats.Find(chatSessionId);
        return chat is not null && chat.OwnerId == userId ? chat : null;
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
