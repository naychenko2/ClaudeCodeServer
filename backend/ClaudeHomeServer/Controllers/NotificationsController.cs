using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ClaudeHomeServer.Hubs;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;

namespace ClaudeHomeServer.Controllers;

[ApiController, Authorize, Route("api/notifications")]
public class NotificationsController(
    NotificationStore store,
    NotificationService notif,
    ILogger<NotificationsController> log) : ControllerBase
{
    // Текущий пользователь из JWT sub claim
    private string UserId => User.FindFirstValue(JwtRegisteredClaimNames.Sub)!;

    /// <summary>GET /api/notifications — список уведомлений с фильтрацией и пагинацией</summary>
    [HttpGet]
    public async Task<ActionResult<NotificationListResponse>> GetList(
        [FromQuery] int limit = 50,
        [FromQuery] int offset = 0,
        [FromQuery] string? kind = null,
        [FromQuery] bool? unreadOnly = null)
    {
        var result = await store.GetListWithCountsAsync(UserId, kind, unreadOnly, limit, offset);
        return Ok(result);
    }

    /// <summary>GET /api/notifications/unread-count — количество непрочитанных (для бейджа)</summary>
    [HttpGet("unread-count")]
    public async Task<ActionResult<object>> GetUnreadCount()
    {
        var count = await store.GetUnreadCountAsync(UserId);
        return Ok(new { count });
    }

    /// <summary>GET /api/notifications/{id} — одно уведомление</summary>
    [HttpGet("{id}")]
    public async Task<ActionResult<NotificationListItem>> GetById(string id)
    {
        var item = await store.GetByIdAsync(UserId, id);
        if (item is null) return NotFound();
        return Ok(item);
    }

    /// <summary>POST /api/notifications — создать уведомление (из MCP или вручную)</summary>
    [HttpPost]
    public async Task<ActionResult<NotificationListItem>> Create([FromBody] CreateNotificationRequest req)
    {
        // Через NotificationService — денормализация персоны/проекта (Enrich) + SignalR + push
        // единой точкой. Push для важных: напоминания, ответы персон/агентов, завершение задач.
        var sendPush = req.Kind is "reminder" or "claude" or "success";
        var id = await notif.SendAsync(UserId, req, sendPush);
        var item = await store.GetByIdAsync(UserId, id);

        log.LogInformation("Уведомление создано: {Id} «{Title}» ({Kind})", id, req.Title, req.Kind);
        return CreatedAtAction(nameof(GetById), new { id }, item);
    }

    /// <summary>PUT /api/notifications/{id}/read — отметить прочитанным</summary>
    [HttpPut("{id}/read")]
    public async Task<ActionResult> MarkRead(string id)
    {
        var ok = await store.MarkReadAsync(UserId, id);
        if (!ok) return NotFound();
        return NoContent();
    }

    /// <summary>PUT /api/notifications/read-all — прочитать все</summary>
    [HttpPut("read-all")]
    public async Task<ActionResult<object>> MarkAllRead()
    {
        var count = await store.MarkAllReadAsync(UserId);
        return Ok(new { marked = count });
    }

    /// <summary>PUT /api/notifications/read-batch — прочитать выбранные</summary>
    [HttpPut("read-batch")]
    public async Task<ActionResult<object>> MarkReadBatch([FromBody] NotificationBatchRequest req)
    {
        var count = await store.MarkReadBatchAsync(UserId, req.Ids);
        return Ok(new { marked = count });
    }

    /// <summary>DELETE /api/notifications/{id} — удалить одно</summary>
    [HttpDelete("{id}")]
    public async Task<ActionResult> Delete(string id)
    {
        var ok = await store.DeleteAsync(UserId, id);
        if (!ok) return NotFound();
        return NoContent();
    }

    /// <summary>DELETE /api/notifications/batch — удалить выбранные</summary>
    [HttpDelete("batch")]
    public async Task<ActionResult<object>> DeleteBatch([FromBody] NotificationBatchRequest req)
    {
        var count = await store.DeleteBatchAsync(UserId, req.Ids);
        return Ok(new { deleted = count });
    }

    /// <summary>DELETE /api/notifications/read-all — удалить все прочитанные</summary>
    [HttpDelete("read-all")]
    public async Task<ActionResult<object>> DeleteReadAll()
    {
        var count = await store.DeleteReadAsync(UserId);
        return Ok(new { deleted = count });
    }
}
