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
    IHubContext<SessionHub> hub,
    ILogger<NotificationsController> log) : ControllerBase
{
    // Текущий пользователь из JWT (задаётся JwtAuthFilter / middleware)
    private string UserId => HttpContext.Items["UserId"] as string ?? "";

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
        var item = await store.AddAsync(UserId, req);

        // SignalR + web push (как старый dual-channel)
        var msg = new NotificationMessage(
            Title: item.Title,
            Body: item.Body,
            Url: item.Url,
            Kind: item.Kind,
            NotificationId: item.Id,
            Type: item.Type,
            ProjectId: item.ProjectId,
            SessionId: item.SessionId,
            TaskId: item.TaskId,
            Source: item.Source,
            Tag: item.Tag);

        await hub.Clients.Group("user_" + UserId).SendAsync("message", msg);
        // Push — опционально, только для важных
        // await _push.SendToUserAsync(UserId, msg);

        log.LogInformation("Уведомление создано: {Id} «{Title}» ({Kind})", item.Id, item.Title, item.Kind);
        return CreatedAtAction(nameof(GetById), new { id = item.Id }, item);
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
}
