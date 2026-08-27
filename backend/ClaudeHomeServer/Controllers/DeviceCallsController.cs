using System.Security.Claims;
using System.Text.Json;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Desktop;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Controllers;

/// <summary>
/// Результаты вызовов десктопного агента (ADR-008, «Протокол канала»).
///
/// Почему HTTP, а не хаб: сообщение SignalR ограничено 32 КБ, а в результате едут кадр и
/// снапшот. Потолок тела — 8 МБ, и это потолок ТРАНСПОРТА: бюджеты кадров живут в правилах
/// протокола, а не здесь.
///
/// Авторизация — только схемой токена устройства (сервисный JWT владельца и дефолтная
/// JwtBearer эту поверхность не открывают), плюс сверка пары владелец+устройство с записью
/// вызова. Приём одноразовый: 409 — ТОЛЬКО на дубль; поздний и частичный результат
/// принимается.
/// </summary>
[ApiController]
[Authorize(AuthenticationSchemes = DesktopProtocol.DeviceTokenScheme)]
[Route("api/devices/calls")]
public class DeviceCallsController(DesktopCallRouter router) : ControllerBase
{
    [HttpPost("{callId}/result")]
    [RequestSizeLimit(DesktopProtocol.MaxResultBytes)]
    public IActionResult PostResult(string callId, [FromBody] DeviceCallResultRequest req)
    {
        var (ownerId, deviceId) = Identity();
        if (ownerId is null || deviceId is null) return Denied();
        if (string.IsNullOrWhiteSpace(req.Outcome)) return BadRequest(new { error = "outcome обязателен" });

        var result = new DesktopCallResult(
            callId,
            req.Outcome,
            req.LastAppliedStep ?? -1,
            req.Message,
            req.Partial,
            req.Payload,
            req.AwaitMinutes);

        return router.TryAcceptResult(callId, ownerId, deviceId, result) switch
        {
            DesktopResultAcceptance.Accepted => Ok(new { accepted = true }),
            // Дубль — единственная причина 409. Опоздание причиной не является.
            DesktopResultAcceptance.Duplicate => Conflict(new { error = "duplicate", callId }),
            DesktopResultAcceptance.Forbidden => Denied(),
            _ => NotFound(new { error = "unknown_call", callId })
        };
    }

    /// <summary>
    /// Забрать результат по callId — путь реконнекта: устройство подняло связь и сверяется,
    /// доехал ли результат из локального журнала.
    /// </summary>
    [HttpGet("{callId}")]
    public IActionResult GetResult(string callId)
    {
        var (ownerId, deviceId) = Identity();
        if (ownerId is null || deviceId is null) return Denied();

        var lookup = router.TryGetPostedResult(callId, ownerId, deviceId, out var result);
        return lookup switch
        {
            DesktopResultLookup.Found => Ok(result),
            DesktopResultLookup.Pending => NoContent(),
            DesktopResultLookup.Forbidden => Denied(),
            _ => NotFound(new { error = "unknown_call", callId })
        };
    }

    // Явный 403 вместо Forbid(): у схемы устройства нет обработчика отказа, а телу ответа
    // нужен машиночитаемый повод.
    private ObjectResult Denied() => StatusCode(StatusCodes.Status403Forbidden, new { error = "forbidden" });

    // Владелец и устройство — из claims токена устройства и ниоткуда больше.
    private (string? OwnerId, string? DeviceId) Identity() =>
        (User.FindFirstValue(DesktopProtocol.OwnerIdClaim), User.FindFirstValue(DesktopProtocol.DeviceIdClaim));
}

/// <summary>
/// Тело результата. LastAppliedStep обязателен по смыслу (индекс последнего применённого
/// шага возвращается в любом исходе); не прислали — считаем «неизвестно», и сервер подставит
/// последнее донесение о прогрессе.
/// </summary>
public record DeviceCallResultRequest(
    string Outcome,
    int? LastAppliedStep,
    string? Message = null,
    bool Partial = false,
    JsonElement? Payload = null,
    int? AwaitMinutes = null);
