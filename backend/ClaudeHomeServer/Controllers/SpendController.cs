using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Llm;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Services;

[ApiController]
[Authorize]
public class SpendController(
    SpendLogService spend) : ControllerBase
{
    private static readonly string? AdminRole =
        // Проверка роли admin через policy — берём из контекста
        null;

    /// <summary>
    /// Агрегат за период (главный экран A): всего токенов, стоимость, ходы.
    /// </summary>
    [HttpGet("/api/spend/aggregate")]
    public ActionResult<object> GetAggregate(
        [FromQuery] string from, [FromQuery] string to,
        [FromQuery] string? projectId, [FromQuery] string? provider, [FromQuery] string? model)
    {
        var ownerId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(ownerId)) return Unauthorized();

        if (!DateTime.TryParse(from, out var fromDt) || !DateTime.TryParse(to, out var toDt))
            return BadRequest("Неверный формат даты. Используйте ISO 8601.");

        var agg = spend.QueryAggregate(ownerId, fromDt, toDt, projectId, provider, model);
        if (agg is null) return Ok(new {
            totalTokens = 0, inputTokens = 0, outputTokens = 0,
            cacheReadTokens = 0, cacheCreationTokens = 0,
            costUsd = (double?)null, turnCount = 0, completedCount = 0,
            cacheHitRate = (double?)null, inputOutputRatio = (double?)null
        });

        return Ok(new {
            totalTokens = agg.TotalTokens,
            inputTokens = agg.InputTokens,
            outputTokens = agg.OutputTokens,
            cacheReadTokens = agg.CacheReadTokens,
            cacheCreationTokens = agg.CacheCreationTokens,
            costUsd = agg.CostUsd.HasValue ? Math.Round(agg.CostUsd.Value, 4) : (double?)null,
            turnCount = agg.TurnCount,
            completedCount = agg.CompletedCount,
            cacheHitRate = agg.CacheHitRate.HasValue ? Math.Round(agg.CacheHitRate.Value, 3) : (double?)null,
            inputOutputRatio = agg.OutputTokens > 0
                ? Math.Round((double)agg.InputTokens / agg.OutputTokens, 2) : (double?)null
        });
    }

    /// <summary>
    /// Разбивка по дням (график).
    /// </summary>
    [HttpGet("/api/spend/daily")]
    public ActionResult GetDaily(
        [FromQuery] string from, [FromQuery] string to,
        [FromQuery] string? projectId, [FromQuery] string? provider)
    {
        var ownerId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(ownerId)) return Unauthorized();
        if (!DateTime.TryParse(from, out var fromDt) || !DateTime.TryParse(to, out var toDt))
            return BadRequest("Неверный формат даты.");

        var days = spend.QueryDaily(ownerId, fromDt, toDt, projectId, provider);
        return Ok(days.Select(d => new {
            date = d.Date,
            totalTokens = d.TotalTokens,
            inputTokens = d.InputTokens,
            outputTokens = d.OutputTokens,
            cacheReadTokens = d.CacheReadTokens,
            cacheCreationTokens = d.CacheCreationTokens,
            costUsd = d.CostUsd.HasValue ? Math.Round(d.CostUsd.Value, 4) : (double?)null,
            turnCount = d.TurnCount,
            completedCount = d.CompletedCount
        }));
    }

    /// <summary>
    /// Топ проектов (таблица).
    /// </summary>
    [HttpGet("/api/spend/by-project")]
    public ActionResult GetByProject([FromQuery] string from, [FromQuery] string to)
    {
        var ownerId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(ownerId)) return Unauthorized();
        if (!DateTime.TryParse(from, out var fromDt) || !DateTime.TryParse(to, out var toDt))
            return BadRequest("Неверный формат даты.");

        var items = spend.QueryByProject(ownerId, fromDt, toDt);

        return Ok(items.Select(p => new {
            projectId = p.ProjectId,
            totalTokens = p.TotalTokens,
            inputTokens = p.InputTokens,
            outputTokens = p.OutputTokens,
            costUsd = p.CostUsd.HasValue ? Math.Round(p.CostUsd.Value, 4) : (double?)null,
            turnCount = p.TurnCount
        }));
    }

    /// <summary>
    /// Топ моделей (таблица).
    /// </summary>
    [HttpGet("/api/spend/by-model")]
    public ActionResult GetByModel([FromQuery] string from, [FromQuery] string to)
    {
        var ownerId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(ownerId)) return Unauthorized();
        if (!DateTime.TryParse(from, out var fromDt) || !DateTime.TryParse(to, out var toDt))
            return BadRequest("Неверный формат даты.");

        var items = spend.QueryByModel(ownerId, fromDt, toDt);
        return Ok(items.Select(m => new {
            provider = m.Provider,
            model = m.Model,
            totalTokens = m.TotalTokens,
            inputTokens = m.InputTokens,
            outputTokens = m.OutputTokens,
            costUsd = m.CostUsd.HasValue ? Math.Round(m.CostUsd.Value, 4) : (double?)null,
            turnCount = m.TurnCount
        }));
    }

    /// <summary>
    /// Лента ходов (экран C — проваливание).
    /// </summary>
    [HttpGet("/api/spend/entries")]
    public ActionResult GetEntries(
        [FromQuery] string from, [FromQuery] string to,
        [FromQuery] string? projectId, [FromQuery] string? sessionId,
        [FromQuery] string? source, [FromQuery] int limit = 100, [FromQuery] int offset = 0)
    {
        var ownerId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(ownerId)) return Unauthorized();
        if (!DateTime.TryParse(from, out var fromDt) || !DateTime.TryParse(to, out var toDt))
            return BadRequest("Неверный формат даты.");

        var entries = spend.QueryEntries(ownerId, fromDt, toDt, projectId, sessionId, source, limit, offset);
        return Ok(entries.Select(e => new {
            id = e.Id,
            ts = e.Ts,
            sessionId = e.SessionId,
            projectId = e.ProjectId,
            provider = e.Provider,
            model = e.Model,
            source = e.Source,
            totalTokens = e.TotalTokens,
            inputTokens = e.InputTokens,
            outputTokens = e.OutputTokens,
            costUsd = e.CostUsd.HasValue ? Math.Round(e.CostUsd.Value, 4) : (double?)null,
            durationMs = e.DurationMs,
            completed = e.Completed,
            entityRef = e.EntityRef
        }));
    }

    /// <summary>
    /// Админский агрегат по всем пользователям.
    /// </summary>
    [HttpGet("/api/spend/admin/aggregate")]
    [Authorize(Roles = "admin")]
    public ActionResult GetAdminAggregate([FromQuery] string from, [FromQuery] string to)
    {
        if (!DateTime.TryParse(from, out var fromDt) || !DateTime.TryParse(to, out var toDt))
            return BadRequest("Неверный формат даты.");

        var items = spend.QueryAdminAggregate(fromDt, toDt);
        return Ok(items.Select(u => new {
            ownerId = u.OwnerId,
            totalTokens = u.InputTokens + u.OutputTokens + u.CacheReadTokens + u.CacheCreationTokens,
            inputTokens = u.InputTokens,
            outputTokens = u.OutputTokens,
            costUsd = u.CostUsd.HasValue ? Math.Round(u.CostUsd.Value, 4) : (double?)null,
            turnCount = u.TurnCount
        }));
    }

    /// <summary>
    /// Граница учёта: дата первой записи.
    /// </summary>
    [HttpGet("/api/spend/boundary")]
    public ActionResult GetBoundary()
    {
        var ownerId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(ownerId)) return Unauthorized();

        var boundary = spend.QueryBoundary(ownerId);
        return Ok(new { since = boundary?.ToString("O") });
    }
}
