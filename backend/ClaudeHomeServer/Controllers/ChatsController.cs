using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Controllers;

// Чаты вне проекта: сессии Claude без привязки к проекту, рабочая папка — {домашняя папка}/Chats (UserHomeResolver)
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

    // Получить сессию по ID независимо от типа (проектная / чат вне проекта).
    // Используется для ссылки «Связанная сессия» в карточке задачи без проекта.
    [HttpGet("{id}")]
    public IActionResult Get(string id)
    {
        var s = sessions.GetOwned(id, UserId);
        return s is null ? NotFound() : Ok(s);
    }

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

    // Групповой чат (флаг persona-group-chats): 2-8 персон, первая — ведущая.
    // Зона — по ведущей: проектная персона → сессия её проекта, глобальная → чат вне проекта.
    [HttpPost("group")]
    public async Task<IActionResult> CreateGroup([FromBody] CreateGroupChatRequest req)
    {
        var mode = Enum.TryParse<ClaudeMode>(req.Mode, true, out var m) ? m : ClaudeMode.Auto;
        try
        {
            var chat = await sessions.CreateGroupChatAsync(UserId, req.PersonaIds ?? [], mode, req.Name);
            return Ok(chat);
        }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
        catch (KeyNotFoundException ex) { return BadRequest(new { error = ex.Message }); }
    }

    // Обновить состав участников группового чата (спикер сохраняется, если остался,
    // иначе — новая ведущая). Работает и для проектной сессии группового чата.
    [HttpPut("{id}/participants")]
    public IActionResult SetParticipants(string id, [FromBody] SetParticipantsRequest req)
    {
        try
        {
            var updated = sessions.SetParticipants(id, UserId, req.PersonaIds ?? []);
            return updated is null ? NotFound() : Ok(updated);
        }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
        catch (KeyNotFoundException ex) { return BadRequest(new { error = ex.Message }); }
    }

    [HttpPut("{id}")]
    public IActionResult Update(string id, [FromBody] UpdateChatRequest req)
    {
        if (OwnedChat(id) is null) return NotFound();
        if (req.Pinned is bool pinned) sessions.SetPinned(id, pinned);
        if (req.ExpiresAfterMinutes is not -1)
        {
            if (req.ExpiresAfterMinutes is <= 0) return BadRequest(new { error = "Срок жизни чата должен быть положительным" });
            sessions.SetExpiry(id, req.ExpiresAfterMinutes);
        }
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

    // Цикл «до готово» (флаг work-loop): вкл/выкл автопродолжения хода до маркера
    // завершения. Работает и для проектной сессии (GetOwned резолвит владельца через проект).
    [HttpPut("{id}/loop")]
    public async Task<IActionResult> SetWorkLoop(string id, [FromBody] SetWorkLoopRequest req)
    {
        if (sessions.GetOwned(id, UserId) is null) return NotFound();
        var updated = await sessions.SetWorkLoopAsync(id, req.Enabled);
        return updated is null ? NotFound() : Ok(updated);
    }

    // Режим прав (permission mode) чата. Отдельный эндпоинт нужен, чтобы выбор в Composer
    // сохранялся сразу, а не только вместе со следующим сообщением: иначе уход со страницы
    // до первого хода откатывал его на прежний. Как и loop, работает для проектной сессии.
    [HttpPut("{id}/mode")]
    public IActionResult SetMode(string id, [FromBody] SetModeRequest req)
    {
        if (sessions.GetOwned(id, UserId) is null) return NotFound();
        try
        {
            var updated = sessions.SetMode(id, req.Mode);
            return updated is null ? NotFound() : Ok(updated);
        }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
    }

    // Миграция начатого чата на другого провайдера (кнопка «Продолжить на …» при
    // исчерпании лимита подписки): транскрипт CLI переносится в профиль целевого
    // провайдера, разговор продолжается через --resume с сохранением контекста.
    // Как и loop, работает и для проектной сессии (владелец резолвится через проект).
    [HttpPost("{id}/migrate-provider")]
    public async Task<IActionResult> MigrateProvider(string id, [FromBody] MigrateProviderRequest req)
    {
        try
        {
            return Ok(await sessions.MigrateProviderAsync(id, UserId, req.Model));
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
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

// ExpiresAfterMinutes: -1 (поле не прислано) — не менять; null — сделать чат постоянным;
// N > 0 — временный, авто-удаление через N минут после последней активности
public record UpdateChatRequest(string? Name = null, string? Model = null, string? Effort = null, bool? Pinned = null, int? ExpiresAfterMinutes = -1);

public record SetPersonaRequest(string? PersonaId = null, string? AgentName = null);

public record CreateGroupChatRequest(List<string>? PersonaIds, string Mode = "auto", string? Name = null);

public record SetParticipantsRequest(List<string>? PersonaIds);

public record SetWorkLoopRequest(bool Enabled);

public record MigrateProviderRequest(string Model);

public record SetModeRequest(string Mode);
