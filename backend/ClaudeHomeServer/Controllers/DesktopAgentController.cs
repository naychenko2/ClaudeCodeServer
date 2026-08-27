using System.Text.Json;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Desktop;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Controllers;

/// <summary>Тело вызова устройства от mcp/desktop-server.</summary>
/// <param name="Device">Человеческое имя устройства («home»); опущено — устройство сеанса.</param>
/// <param name="Kind">Вид вызова: screen | ui | act | open | run.</param>
/// <param name="Args">Аргументы инструмента — сервер их не разбирает, это дело клиента.</param>
/// <param name="ConfirmationWaitMinutes">Сколько минут ждать человека (потолок — у протокола).</param>
public sealed record DesktopAgentCallRequest(
    string? Device,
    string Kind,
    JsonElement? Args = null,
    int? ConfirmationWaitMinutes = null);

/// <summary>
/// Харнес-контракт грани десктопа: сюда ходит mcp/desktop-server из песочницы (ADR-008).
///
/// Авторизация — ТОЛЬКО capability-токеном чата: ни пользовательский JWT, ни сервисный
/// токен владельца этой поверхности не открывают. Чат-вызыватель берётся из токена, а
/// заголовок X-Caller-Session-Id не читается вообще — подделать его ход может тривиально.
///
/// Отказ гейта — 409 { outcome, message }: инструменты desktop_* остаются в составе
/// tools/list при любом отказе, потому что состав входит в сигнатуру запуска CLI, и его
/// изменение перезапустило бы процесс со всеми MCP-серверами.
/// </summary>
[ApiController]
[Authorize(AuthenticationSchemes = DesktopCapabilityAuthHandler.SchemeName)]
[Route("api/devices/agent")]
public sealed class DesktopAgentController(
    DesktopAccessGate gate,
    DesktopHandsSessionService hands,
    IDesktopDeviceDirectory devices,
    DesktopCallRouter router) : ControllerBase
{
    private DesktopCaller? Caller => DesktopCaller.FromPrincipal(User);

    /// <summary>
    /// desktop_devices: устройства владельца — имя, связь, статус сеанса рук. Сеанса не
    /// требует: инструмент как раз и рассказывает, что сеанса нет и на чём его начать.
    /// </summary>
    [HttpGet("list")]
    public IActionResult List()
    {
        if (Caller is not { } caller) return Unauthorized();

        var decision = gate.EvaluateFacet(caller);
        if (!decision.Allowed) return Refused(decision);

        var session = hands.ForChat(caller.SessionId);
        var items = devices.List(caller.OwnerId).Select(d => new
        {
            name = d.Name,
            online = d.Online,
            // «Руки этого чата на этом устройстве» — вопрос чата, а не владельца:
            // скрытого «активного устройства» у владельца не существует.
            handsHere = session?.DeviceId == d.Id,
            // Устройство занято сеансом другого чата — честно говорим об этом.
            busyWith = hands.ForDevice(caller.OwnerId, d.Id) is { } other && other.ChatSessionId != caller.SessionId
                ? other.ChatName ?? other.ChatSessionId
                : null
        }).ToList();

        return Ok(new
        {
            devices = items,
            hands = session is null ? null : new
            {
                device = session.DeviceName,
                startedAt = session.StartedAt,
                expiresAt = session.ExpiresAt
            }
        });
    }

    /// <summary>
    /// desktop_screen | desktop_ui | desktop_act | desktop_open | desktop_run — один вход.
    /// Гейт проверяет право на КАЖДЫЙ вызов; дальше вызов ведёт маршрутизатор канала.
    /// </summary>
    [HttpPost("call")]
    public async Task<IActionResult> Call([FromBody] DesktopAgentCallRequest req, CancellationToken ct)
    {
        if (Caller is not { } caller) return Unauthorized();
        if (!DesktopCallKinds.IsKnown(req.Kind))
            return BadRequest(new { outcome = DesktopOutcomes.ProtocolError, message = $"Неизвестный вид вызова: {req.Kind}" });

        var decision = gate.EvaluateCall(caller, req.Device);
        if (!decision.Allowed) return Refused(decision);

        var result = await router.InvokeAsync(new DesktopCallRequest(
            caller.OwnerId,
            decision.Device!.Id,
            caller.SessionId,
            req.Kind,
            req.Args,
            RequiresConfirmation(req.Kind),
            req.ConfirmationWaitMinutes,
            decision.Device.Name,
            decision.Chat?.ChatName), ct);

        return Ok(result);
    }

    // Кадр и снапшот внутри сеанса уходят без отдельного нажатия — иначе «посмотри, что за
    // ошибка» неработоспособно (ADR). Всё, что меняет состояние машины, подтверждается
    // человеком всегда: решение принимает сервер, а не аргумент вызова.
    private static bool RequiresConfirmation(string kind) =>
        kind is not (DesktopCallKinds.Screen or DesktopCallKinds.Ui);

    // Отказ гейта — 409 с исходом и текстом: модель обязана понять, что произошло.
    private ObjectResult Refused(DesktopGateDecision decision) =>
        Conflict(new { outcome = decision.Outcome, message = decision.Message });
}
