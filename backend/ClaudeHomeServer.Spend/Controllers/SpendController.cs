using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Access;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Services.Spend.Controllers;

// API аналитики расхода токенов (Spend Analytics v2). Всё под [Authorize]; чужие данные —
// только роли admin (scope=all), гейт — SpendAccess. Содержимого сообщений здесь нет и быть
// не может: хранилище держит только метрики и id разрезов, имена резолвятся по реестрам.
//
// Контроллер живёт в вынесенной вертикали `ClaudeHomeServer.Spend` (Этап 5, переезд из Main).
// Сборка подключается к MVC атрибутом `[assembly: ApplicationPart("...")]`
// (генерируется MSBuild благодаря Web SDK у Spend.csproj). Конструктор принимает Core-швы
// `ISessionDirectory`/`ITaskLookup` (адаптеры в Main) — Main-типы `SessionManager`/`TaskManager`
// в вертикаль не заходят.
[ApiController]
[Route("api/spend")]
[Authorize]
public class SpendController(SpendAnalyticsService analytics, ISessionDirectory sessions,
    ITaskLookup tasks, TaskPromptMetricsStore promptMetrics,
    IProjectAccess access, SpendAnswerSpeedService answerSpeed) : ControllerBase
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
            user, project, chat, task, persona, provider, model, source);
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

    // Разбивка постановки задачи по секциям (сколько символов заняли ЗАДАЧА, ПРАВИЛА,
    // ДЕЛЕГИРОВАНИЕ, КОНТЕКСТ, заметки). Раскрытие узла разреза «задача» в анализе.
    // Гейт как у Badge: своя задача либо admin. Содержимого постановки здесь нет —
    // стор хранит только размеры (инвариант TaskPromptMetricsStore).
    [HttpGet("tasks/{taskId}/prompt")]
    public IActionResult TaskPrompt(string taskId)
    {
        var task = tasks.GetById(taskId);
        if (task is null) return NotFound();
        if (!IsAdmin && task.OwnerId != CurrentUserId)
            return Forbidden("Чужая задача доступна только администратору");
        // Пусто — задача запускалась до появления стора: это не ошибка, разбивки просто нет
        return Ok(new { runs = promptMetrics.ForTask(taskId) });
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
        // Расход — только владельцу чата: решение о доступе принимает IProjectAccess (рубильник общих
        // проектов, роль), участнику общего проекта расход чужого чата не открыт (S2a-6b)
        if (!IsAdmin && access.ResolveSession(CurrentUserId, sessionId) is not { Role: ProjectRole.Owner })
            return Forbidden("Чужой чат доступен только администратору");
        return Ok(analytics.Badge(sessionId));
    }

    // Скорость ответа облачных ходов по моделям: сессионная (токены ÷ время ответов модели).
    // Только админ: агрегат по всем владельцам, сравнение скоростей разных моделей между
    // собой — служебная вещь админа (как /api/local-model/stats).
    [HttpGet("answer-speed")]
    [Authorize(Roles = "admin")]
    public IActionResult AnswerSpeed()
    {
        // Рантайм-проверка рядом с атрибутом: атрибут защищает маршрут, а проверка — сам
        // контроллер (его зовут и в обход конвейера).
        if (!IsAdmin) return Forbidden("Скорость ответа по моделям доступна только администратору");
        // Окно по времени: последние 24 часа от текущего момента (UTC), half-open [from, to).
        // Параметров окна нет намеренно: прежняя дата-пара «вчера—сегодня» из часового пояса
        // клиента давала от 24 до 48 часов (хвост ревью aa8af844, п.1). Кто считает — тот и
        // называет окно: фронт его не вычисляет и не передаёт.
        var to = DateTime.UtcNow;
        return Ok(answerSpeed.AnswerSpeed(to.AddHours(-SpendAnswerSpeedService.WindowHours), to));
    }

    // Максимальная длина периода запроса: byDay обзора строится по каждому дню, и без клампа
    // from=0001-01-01 раздул бы ответ на сотни тысяч дней (ревью Глеба, minor-1)
    internal const int MaxPeriodDays = 366;

    // Дефолтный период запроса: 29 дней назад от «to» — окно в 30 дней включительно
    internal const int DefaultPeriodDays = 29;

    private (DateOnly From, DateOnly To) Period(string? from, string? to) =>
        ClampPeriod(from, to, analytics.Store.EarliestDate);

    // Период запроса: дефолт — последние 30 дней по UTC. Кламп: раньше самой ранней записи
    // стора не ходим (earliest) и длиннее MaxPeriodDays не отдаём
    internal static (DateOnly From, DateOnly To) ClampPeriod(string? from, string? to, DateOnly? earliest)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var t = DateOnly.TryParse(to, out var pt) ? pt : today;
        var f = DateOnly.TryParse(from, out var pf) ? pf : t.AddDays(DefaultPeriodDays);
        if (f > t) return (t, t);
        if (earliest is { } e && f < e) f = e;
        if (t.DayNumber - f.DayNumber >= MaxPeriodDays) f = t.AddDays(-(MaxPeriodDays - 1));
        return f > t ? (t, t) : (f, t);
    }

    private ObjectResult Forbidden(string error) =>
        StatusCode(StatusCodes.Status403Forbidden, new { error });
}
