using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Controllers;

// Чаты вне проекта: сессии Claude без привязки к проекту, рабочая папка — {DefaultProjectsPath}/{username}/Chats
[ApiController]
[Authorize]
[Route("api/chats")]
public class ChatsController(SessionManager sessions, FileService files) : ControllerBase
{
    // DefaultMapInboundClaims = false → sub не ремапится в NameIdentifier, читаем напрямую
    private string UserId => User.FindFirstValue(JwtRegisteredClaimNames.Sub)!;

    // Чат принадлежит текущему пользователю и не привязан к проекту
    private Session? OwnedChat(string id)
    {
        var s = sessions.GetById(id);
        return s is not null && s.ProjectId is null && s.OwnerId == UserId ? s : null;
    }

    [HttpGet]
    public IActionResult GetAll() => Ok(sessions.GetProjectlessChats(UserId));

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateChatRequest req)
    {
        var mode = Enum.TryParse<ClaudeMode>(req.Mode, true, out var m) ? m : ClaudeMode.Auto;
        try
        {
            var chat = await sessions.CreateChatAsync(UserId, mode, req.ResumeSessionId, req.Name, req.Model, req.Effort);
            return CreatedAtAction(nameof(GetAll), new { }, chat);
        }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
        catch (KeyNotFoundException ex) { return BadRequest(new { error = ex.Message }); }
    }

    [HttpPut("{id}")]
    public IActionResult Update(string id, [FromBody] UpdateChatRequest req)
    {
        if (OwnedChat(id) is null) return NotFound();
        if (req.Pinned is bool pinned) sessions.SetPinned(id, pinned);
        try
        {
            var updated = sessions.Update(id, req.Name, req.Model, req.Effort);
            return updated is null ? NotFound() : Ok(updated);
        }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
    }

    // Назначить/снять собеседника у чата ДО первого хода (селектор в пустом чате):
    // персону (personaId) или .md-агента (agentName) — взаимоисключающе; оба пустые = снять.
    // Начатую сессию менять нельзя (клиент делает форк).
    [HttpPost("{id}/persona")]
    public IActionResult SetPersona(string id, [FromBody] SetPersonaRequest req)
    {
        if (OwnedChat(id) is null) return NotFound();
        try
        {
            var updated = sessions.SetPersona(id, UserId, req.PersonaId, req.AgentName);
            return updated is null ? NotFound() : Ok(updated);
        }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
        catch (KeyNotFoundException ex) { return BadRequest(new { error = ex.Message }); }
    }

    [HttpGet("{id}/history")]
    public async Task<IActionResult> GetHistory(string id)
    {
        if (OwnedChat(id) is null) return NotFound();
        return Ok(await sessions.GetHistoryAsync(id));
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id)
    {
        if (OwnedChat(id) is null) return NotFound();
        await sessions.DeleteAsync(id);
        return NoContent();
    }

    // Загрузка вложения в рабочую папку чата (в подпапку .cc-attachments) → относительный путь
    [HttpPost("{id}/files/upload")]
    [RequestSizeLimit(100 * 1024 * 1024)] // 100 МБ
    public async Task<IActionResult> Upload(string id, IFormFile? file = null)
    {
        if (OwnedChat(id) is null) return NotFound();
        if (file == null || file.Length == 0)
            return BadRequest(new { error = "Файл не выбран или пустой" });

        var root = sessions.GetChatRoot(id, UserId);
        if (root is null) return NotFound();

        // Path.GetFileName защищает от path-сегментов в имени файла (../evil)
        var safeName = Path.GetFileName(file.FileName);
        if (string.IsNullOrEmpty(safeName))
            return BadRequest(new { error = "Некорректное имя файла" });

        // Уникальность — через подпапку с GUID, чтобы сохранить оригинальное имя файла
        // (на плашке в чате показывается basename = оригинальное имя, и Claude видит его же)
        var rel = $".cc-attachments/{Guid.NewGuid():N}/{safeName}";

        using var ms = new MemoryStream();
        await file.CopyToAsync(ms);
        files.WriteFileBytes(root, rel, ms.ToArray());
        return Ok(new { path = rel });
    }
}

public record CreateChatRequest(string Mode = "auto", string? ResumeSessionId = null, string? Name = null, string? Model = null, string? Effort = null);

public record UpdateChatRequest(string? Name = null, string? Model = null, string? Effort = null, bool? Pinned = null);

public record SetPersonaRequest(string? PersonaId = null, string? AgentName = null);
