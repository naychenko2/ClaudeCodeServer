using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Controllers;

// Сводка для дашборда «Домой»: кросс-проектный срез сессий пользователя.
// Read-only агрегация поверх SessionManager + ProjectManager — отдельный сервис не нужен.
[ApiController]
[Authorize]
[Route("api/home")]
public class HomeController(SessionManager sessions, ProjectManager projects) : ControllerBase
{
    private string UserId => User.FindFirstValue(JwtRegisteredClaimNames.Sub)!;

    /// <summary>
    /// GET /api/home/summary?recent=10 — активные и недавние сессии по всем проектам + чаты.
    /// active — живые (Starting/Working/Waiting, конвенция BoardService); Active (простаивающий
    /// процесс) и завершённые уходят в recent. Orphaned не показываем — это мусор после рестартов.
    /// </summary>
    [HttpGet("summary")]
    public IActionResult GetSummary([FromQuery] int recent = 10)
    {
        recent = Math.Clamp(recent, 1, 50);
        // Имена проектов владельца: id → Name (чужие проекты сюда не попадают по построению)
        var projectNames = projects.GetByOwner(UserId).ToDictionary(p => p.Id, p => p.Name);

        // Архивные чаты в сводку не попадают вовсе: «убрал с глаз» распространяется и на
        // главную, и на точку активности проекта, которая считается по этому же ответу
        var all = sessions.GetAllOwnedBy(UserId)
            .Where(s => s.ArchivedAt is null)
            .OrderByDescending(s => s.UpdatedAt)
            .ToList();

        // «Живая» сессия — общее определение SessionLiveness.IsLive: его же считает
        // гейдж ccs.sessions.active, чтобы сводка и метрика не разъезжались.
        var active = all
            .Where(SessionLiveness.IsLive)
            .Select(s => ToDto(s, projectNames))
            .ToList();
        var recentItems = all
            .Where(s => !s.IsLive() && s.Status is not SessionStatus.Orphaned)
            .Take(recent)
            .Select(s => ToDto(s, projectNames))
            .ToList();

        return Ok(new { active, recent = recentItems });
    }

    private static HomeSessionDto ToDto(Session s, IReadOnlyDictionary<string, string> projectNames) => new(
        s.Id,
        s.ProjectId,
        s.ProjectId is not null ? projectNames.GetValueOrDefault(s.ProjectId) : null,
        s.Name,
        s.Status,
        s.LastMessage,
        s.PersonaId,
        s.TaskId,
        s.TaskDone,
        s.MessageCount,
        s.UpdatedAt,
        s.Origin,
        s.IsPinned,
        s.Tags,
        s.Participants,
        s.ExpiresAfterMinutes,
        s.LastReadAt);
}

// Строка сводки: сессия + имя проекта (чтобы фронт не тянул список проектов отдельно).
// Поля фильтрации (Origin/IsPinned/Tags/Participants/ExpiresAfterMinutes) нужны, чтобы
// клиентский фильтр чатов (chatFilters.ts → matchChatFilter) можно было применить к
// сводке — без них точка активности проекта неверно горела бы по чатам, скрытым фильтром
public record HomeSessionDto(
    string Id,
    string? ProjectId,
    string? ProjectName,
    string? Name,
    SessionStatus Status,
    string? LastMessage,
    string? PersonaId,
    string? TaskId,
    bool TaskDone,
    int MessageCount,
    DateTime UpdatedAt,
    ChatOrigin Origin,
    bool IsPinned,
    List<string> Tags,
    List<string>? Participants,
    int? ExpiresAfterMinutes,
    // Серверная отметка прочтения (синк между устройствами); дефолт — чтобы не ломать
    // конструирование именованными аргументами в тестах
    DateTime? LastReadAt = null);
