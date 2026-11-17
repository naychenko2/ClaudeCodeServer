using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.ChildMode;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Controllers;

/// <summary>
/// Статус и присутствие ребёнка (KM-B5e, ADR-041 «Контракт», ручки №1 и №2). Владелец — только
/// из JWT (контракт соседа: не из тела); ребёнок опознаётся по стору через IChildModePolicy,
/// не по claim (тот же приём, что у гейта и AdminByStore). Расчёт лимитов — только
/// ChildLimitsCalculator (KM-B5c, одна точка расчёта): контроллер валидирует запрос, засчитывает
/// минуту присутствия и превращает отказы в {code, message}.
///
/// Порядок отказов зафиксирован здесь (ADR даёт набор кодов, не последовательность):
///  1. 403 not_child — вызывающий не ребёнок: личность вызывающего раньше содержимого запроса;
///  2. 400 unknown_space — ключ неизвестен системе (в волне 1 известен только "lessons"):
///     ошибка запроса не зависит от состояния профиля;
///  3. 503 policy_unavailable — профиль есть, но не читается (fail-closed). Через HTTP достижимо
///     только как гонка между гейтом и контроллером: гейт не пускает DenyAll-ребёнка ни на какой
///     эндпоинт (ChildModeGateMiddleware.cs:37);
///  4. 403 space_disabled — ключ известен, но в профиле этого ребёнка нет такой записи;
///  5. 503 policy_unavailable — сбой записи журнала или отказа калькулятора: необработанного
///     исключения наружу нет.
///
/// Часы — DateTimeOffset.UtcNow, одно чтение на запись минуты и на статус: ответ heartbeat
/// обязан отражать только что засчитанную минуту (фронт получает статус из heartbeat, без
/// второго запроса status). Minute — та же таймзона, в которой калькулятор читает «минут
/// сегодня»: записать в другой — значит положить минуту в чужой местный день.
/// </summary>
[ApiController]
[Authorize]
[Route("api/child")]
public class ChildStatusController(
    IChildModePolicy policy,
    ChildLimitsCalculator limits,
    ChildUsageStore usage,
    ILogger<ChildStatusController> logger) : ControllerBase
{
    // Ключи пространств, известные системе в волне 1 (ADR-041 «Общие правила»): «studio»
    // заложен в формат (ChildSpaces.Studio), в поверхность ручек входит волной 2.
    private static readonly HashSet<string> KnownSpaces = new(StringComparer.Ordinal) { ChildSpaces.Lessons };

    private string UserId => User.FindFirstValue(JwtRegisteredClaimNames.Sub)!;

    [ChildAllowed]
    [HttpGet("status")]
    public IActionResult Status([FromQuery] string? space) => StatusFor(space, recordMinute: false);

    [ChildAllowed]
    [HttpPost("heartbeat")]
    public IActionResult Heartbeat([FromBody] ChildHeartbeatRequest? req) => StatusFor(req?.Space, recordMinute: true);

    private IActionResult StatusFor(string? rawSpace, bool recordMinute)
    {
        var userId = UserId;
        var snapshot = policy.For(userId);
        if (snapshot is null)
            return ChildError(403, "not_child", "Эта ручка только для детей.");

        var space = string.IsNullOrWhiteSpace(rawSpace) ? ChildSpaces.Lessons : rawSpace.Trim();
        if (!KnownSpaces.Contains(space))
            return ChildError(400, "unknown_space", "Неизвестное пространство.");

        // Нечитаемый профиль — «запрещено всё»: это отказ, а не состояние (503 у ручек, ADR И3).
        if (snapshot.DenyAll || snapshot.Profile is null)
            return ChildError(503, "policy_unavailable", "Настройки режима не читаются — попросите взрослого проверить.");

        if (!snapshot.Profile.Spaces.ContainsKey(space))
            return ChildError(403, "space_disabled", "Это пространство тебе не открыто.");

        var now = DateTimeOffset.UtcNow;
        var tz = limits.ResolveTimeZone(userId);

        if (recordMinute)
        {
            try
            {
                // Дубль той же минуты минуту не добавляет: ключ записи стора — (день, пространство,
                // минута), две вкладки одного ребёнка пишут в один файл под общим локом (KM-B5b).
                usage.RecordMinute(userId, space, now, tz);
            }
            catch (Exception e)
            {
                // Сбой журнала — 503 с причиной, а не 500: статус ребёнка не должен ронять запрос
                // и не должен молча терять минуту присутствия.
                logger.LogWarning(e, "Детский режим: минута присутствия не записана (owner {Owner}, space {Space})", userId, space);
                return ChildError(503, "policy_unavailable", "Не удалось засчитать время — попробуй ещё раз.");
            }
        }

        ChildStatus? status;
        try
        {
            status = limits.Evaluate(userId, space, now);
        }
        catch (ChildPolicyUnavailableException e)
        {
            // Гонка с админской правкой между нашим чтением политики и вызовом калькулятора.
            logger.LogWarning(e, "Детский режим: профиль перестал читаться (owner {Owner})", userId);
            return ChildError(503, "policy_unavailable", "Настройки режима не читаются — попросите взрослого проверить.");
        }

        if (status is null)
            return ChildError(403, "not_child", "Эта ручка только для детей.");

        return Ok(status);
    }

    // Тело ошибки — контракт ADR: {code, message}; message — текст для ребёнка по-русски,
    // фронт показывает его как есть.
    private static JsonResult ChildError(int status, string code, string message) =>
        new(new { code, message }) { StatusCode = status };
}

// Тело POST /api/child/heartbeat (ADR-041, ручка №2): space необязателен в теле — как и параметр
// status, по умолчанию "lessons". nullable-поле: пустое тело — законный запрос, а не 400
// авто-валидации MVC (тот же приём, что у ChildProfileDto).
public record ChildHeartbeatRequest(string? Space);
