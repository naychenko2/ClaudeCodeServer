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

        var all = sessions.GetAllOwnedBy(UserId)
            .OrderByDescending(s => s.UpdatedAt)
            .ToList();

        // «Живая» сессия: работает/ждет, либо реально запускается (уже есть сообщение).
        // Пустой новорожденный чат тоже имеет статус Starting (процесс claude стартует
        // лениво при первом сообщении) — это не активность, ему место в «недавних».
        static bool IsLive(Session s) =>
            s.Status is SessionStatus.Working or SessionStatus.Waiting
            || (s.Status is SessionStatus.Starting && s.MessageCount > 0);

        var active = all
            .Where(IsLive)
            .Select(s => ToDto(s, projectNames))
            .ToList();
        var recentItems = all
            .Where(s => !IsLive(s) && s.Status is not SessionStatus.Orphaned)
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
        s.MessageCount,
        s.UpdatedAt);
}

// Строка сводки: сессия + имя проекта (чтобы фронт не тянул список проектов отдельно)
public record HomeSessionDto(
    string Id,
    string? ProjectId,
    string? ProjectName,
    string? Name,
    SessionStatus Status,
    string? LastMessage,
    string? PersonaId,
    string? TaskId,
    int MessageCount,
    DateTime UpdatedAt);
