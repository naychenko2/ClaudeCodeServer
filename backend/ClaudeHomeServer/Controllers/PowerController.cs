using ClaudeHomeServer.Services.Power;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Controllers;

// Питание машины, на которой крутится продукт: выключить, перезагрузить, усыпить из веб-морды.
//
// Зачем: продукт держит машину дома, а хозяин ходит в него снаружи. Без этой кнопки погасить
// компьютер удалённо нечем — до RDP надо сначала дойти, а чат для такой команды лишний
// посредник (кончились лимиты провайдера — и выключить нечем).
//
// Замков три, и они независимы, как у выкатки: admin-роль, выключенная по умолчанию
// конфигурация (PowerControlOptions) и платформа (не Windows — команду отдавать нечем). Прятать
// пункт в UI без серверной проверки нельзя: веб-морда торчит наружу.
//
// Отсрочка перед выполнением — не украшение, а единственное окно, в которое можно передумать:
// пункт меню задевается промахом, а разбудить погашенную машину из соседней комнаты уже
// невозможно.
[ApiController]
[Route("api/admin/power")]
[Authorize(Roles = "admin")]
public class PowerController(PowerControlService power, ILogger<PowerController> log) : ControllerBase
{
    public sealed record ScheduleRequest(string Action);

    /// <summary>
    /// Состояние: доступна ли фича и не запланировано ли уже что-то. Отвечает 200 ВСЕГДА, даже
    /// когда фича выключена — эндпоинт зовёт шапка при каждом монтировании, чтобы решить,
    /// рисовать ли пункт меню, и 404 шумел бы ошибкой в консоли (тот же приём, что у выкатки).
    /// </summary>
    [HttpGet("status")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public IActionResult GetStatus()
    {
        var pending = power.Pending;
        return Ok(new
        {
            enabled = power.Enabled,
            available = power.Available,
            delaySeconds = power.DelaySeconds,
            pending = pending is null ? null : new
            {
                action = Name(pending.Action),
                runAt = pending.RunAt,
                secondsLeft = Math.Max(0, (int)Math.Ceiling((pending.RunAt - DateTimeOffset.Now).TotalSeconds)),
                requestedBy = pending.RequestedBy,
            },
        });
    }

    /// <summary>
    /// Планирует действие. 202 — команда принята и сработает через отсрочку; до этого момента
    /// её можно снять через cancel.
    /// </summary>
    [HttpPost]
    public IActionResult Schedule([FromBody] ScheduleRequest request)
    {
        // Выключенная фича — 404: «здесь ничего нет» честнее, чем «вам сюда нельзя», когда
        // дело не в правах.
        if (!power.Enabled) return NotFound();

        if (!TryParseAction(request?.Action, out var action))
            return BadRequest(new { error = "Неизвестное действие. Ожидается shutdown, restart или sleep." });

        var result = power.Schedule(action, User.Identity?.Name ?? "неизвестный");
        if (!result.Ok)
        {
            log.LogInformation("Команда питания отклонена: {Reason}", result.Reason);
            return Conflict(new { error = result.Reason });
        }

        return Accepted(new
        {
            action = Name(action),
            runAt = result.Pending!.RunAt,
            secondsLeft = power.DelaySeconds,
        });
    }

    /// <summary>Снимает запланированное действие. 404 — снимать было нечего.</summary>
    [HttpPost("cancel")]
    public IActionResult Cancel()
    {
        if (!power.Enabled) return NotFound();
        return power.Cancel(User.Identity?.Name ?? "неизвестный")
            ? Ok(new { cancelled = true })
            : NotFound(new { error = "Нечего отменять." });
    }

    private static bool TryParseAction(string? value, out PowerAction action)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "shutdown": action = PowerAction.Shutdown; return true;
            case "restart": action = PowerAction.Restart; return true;
            case "sleep": action = PowerAction.Sleep; return true;
            default: action = default; return false;
        }
    }

    private static string Name(PowerAction action) => action.ToString().ToLowerInvariant();
}
