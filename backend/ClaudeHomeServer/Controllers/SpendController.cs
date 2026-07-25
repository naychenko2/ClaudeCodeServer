using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Spend;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Controllers;

// API аналитики расхода токенов (Spend Analytics v2). Всё под [Authorize]; чужие данные —
// только роли admin (scope=all), гейт — SpendAccess. Содержимого сообщений здесь нет и быть
// не может: хранилище держит только метрики и id разрезов, имена резолвятся по реестрам.
[ApiController]
[Route("api/spend")]
[Authorize]
public class SpendController(SpendAnalyticsService analytics, SessionManager sessions) : ControllerBase
{
    private string CurrentUserId => User.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? "";
    private bool IsAdmin => User.IsInRole("admin");

    // Сводный обзор периода: тоталы, дни со стеком источников, карточки разрезов, топ ходов
    [HttpGet("overview")]
    public IActionResult Overview(string? from = null, string? to = null, string? scope = null,
        string? user = null, string? project = null, string? chat = null, string? task = null,
        string? persona = null, string? provider = null, string? model = null, string? source = null)
    {
        var res = SpendAccess.Resolve(IsAdmin, CurrentUserId, scope,
            user, project, chat, task, persona, provider, model, source);
        if (res.Error is not null) return Forbidden(res.Error);
        var (f, t) = Period(from, to);
        return Ok(analytics.Overview(f, t, res.Filter, res.AllUsers, CurrentUserId));
    }

    // Узлы pivot-дерева: агрегаты одного разреза при фильтрах. Раскрытие узла = повторный
    // вызов со следующим groupBy цепочки уровней и фильтром по значению узла.
    [HttpGet("pivot")]
    public IActionResult Pivot([FromQuery] string groupBy, string? from = null, string? to = null,
        string? scope = null, string? user = null, string? project = null, string? chat = null,
        string? task = null, string? persona = null, string? provider = null,
        string? model = null, string? source = null)
    {
        if (!SpendAnalyticsService.PivotLevels.Contains(groupBy))
            return BadRequest(new { error = $"Неизвестный разрез: {groupBy}" });
        var res = SpendAccess.Resolve(IsAdmin, CurrentUserId, scope,
            user, project, chat, task, persona, provider, model, source, groupBy);
        if (res.Error is not null) return Forbidden(res.Error);
        var (f, t) = Period(from, to);
        return Ok(new { nodes = analytics.Pivot(groupBy, f, t, res.Filter) });
    }

    // Листья-ходы среза (только детальное окно). sort: tokens (дефолт) | time
    [HttpGet("turns")]
    public IActionResult Turns(string? from = null, string? to = null, string? scope = null,
        string? user = null, string? project = null, string? chat = null, string? task = null,
        string? persona = null, string? provider = null, string? model = null, string? source = null,
        int limit = 50, int offset = 0, string? sort = null)
    {
        var res = SpendAccess.Resolve(IsAdmin, CurrentUserId, scope,
            user, project, chat, task, persona, provider, model, source);
        if (res.Error is not null) return Forbidden(res.Error);
        var (f, t) = Period(from, to);
        return Ok(analytics.Turns(f, t, res.Filter, Math.Clamp(limit, 1, 500),
            Math.Max(offset, 0), sort, CurrentUserId));
    }

    // Паспорт хода: полная запись + соседние ходы той же сессии для спарклайна.
    // Чужой ход отдаётся только админу (метрики и названия, без содержимого).
    [HttpGet("turns/{id}")]
    public IActionResult Passport(string id)
    {
        var passport = analytics.Passport(id, CurrentUserId, IsAdmin);
        return passport is null ? NotFound() : Ok(passport);
    }

    // Виджет «Домой»: сегодня/неделя текущего пользователя
    [HttpGet("widget")]
    public IActionResult Widget() => Ok(analytics.Widget(CurrentUserId));

    // Бейдж чата: суммарный расход сессии + последний ход. Владелец или admin.
    [HttpGet("sessions/{sessionId}/badge")]
    public IActionResult Badge(string sessionId)
    {
        var session = sessions.GetById(sessionId);
        if (session is null) return NotFound();
        if (!IsAdmin && sessions.ResolveOwnerId(session) != CurrentUserId)
            return Forbidden("Чужой чат доступен только администратору");
        return Ok(analytics.Badge(sessionId));
    }

    // Период запроса: дефолт — последние 30 дней по UTC
    private static (DateOnly From, DateOnly To) Period(string? from, string? to)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var t = DateOnly.TryParse(to, out var pt) ? pt : today;
        var f = DateOnly.TryParse(from, out var pf) ? pf : t.AddDays(-29);
        return f > t ? (t, t) : (f, t);
    }

    private ObjectResult Forbidden(string error) =>
        StatusCode(StatusCodes.Status403Forbidden, new { error });
}
