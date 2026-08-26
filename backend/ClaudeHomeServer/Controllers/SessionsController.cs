using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ClaudeHomeServer.Filters;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Controllers;

[ApiController]
[Authorize]
[Route("api/projects/{projectId}/sessions")]
public class SessionsController(SessionManager sessions, ProjectManager projects,
    FeatureFlagService flags, DefaultAssistantProvisioner provisioner,
    PersonaManager personas) : ControllerBase
{
    private string UserId => User.FindFirstValue(JwtRegisteredClaimNames.Sub)!;

    // Проект принадлежит текущему пользователю; чужой/несуществующий — как отсутствующий (404)
    private bool OwnsProject(string projectId) =>
        projects.GetById(projectId)?.OwnerId == UserId;

    [HttpGet]
    public IActionResult GetAll(string projectId)
    {
        if (!OwnsProject(projectId)) return NotFound();
        return Ok(sessions.GetByProject(projectId));
    }

    [HttpPost]
    public async Task<IActionResult> Create(string projectId, [FromBody] CreateSessionRequest req)
    {
        if (!OwnsProject(projectId)) return NotFound();
        string? personaId = req.PersonaId;
        // Последний рубеж инварианта «новый чат человека — только с персоной» (план 2.4, парная
        // связь с фронт-правкой 4.3): под флагом без personaId/resumeSessionId сначала провижним
        // ассистента и продолжим с ним. 400 — только когда провижн невозможен. Служебные пути
        // (задачи, one-shot) REST не используют, поэтому гейт их не задевает.
        if (string.IsNullOrWhiteSpace(personaId) && string.IsNullOrWhiteSpace(req.ResumeSessionId)
            && flags.IsEnabled(UserId, FeatureFlagKeys.DefaultPersonasOnboarding))
        {
            // Правило «персона контекста» — то же, что на фронте (lib/defaultPersona.ts):
            // в проекте чат ведёт руководитель, и только если его нет — личный ассистент.
            // Сервер обязан повторять это правило, иначе не-браузерный потребитель REST
            // (скрипт, будущий адаптер мессенджера) получил бы в проекте с назначенным
            // руководителем чат от личного ассистента — и вместе с ним молча потерял бы
            // командные механики (SessionManager отдаёт их только персоне-руководителю).
            // Резолв в живую персону, а не проверка поля: сирота не должна подменять правило.
            var lead = projects.GetById(projectId)?.DefaultPersonaId is { } leadId
                ? personas.Get(leadId, UserId)
                : null;
            if (lead is not null) personaId = lead.Id;
            else
            {
                var provisioned = await provisioner.EnsureAsync(UserId, HttpContext.RequestAborted);
                if (provisioned is null)
                    return BadRequest(new { error = "Новый чат создаётся только с персоной: укажите personaId" });
                personaId = provisioned.Id;
            }
        }
        try
        {
            var mode = Enum.TryParse<ClaudeMode>(req.Mode, true, out var m) ? m : ClaudeMode.AcceptEdits;
            var session = await sessions.CreateAsync(projectId, mode, req.ResumeSessionId, req.Name,
                req.Model, req.AgentName, req.Effort, personaId: personaId);
            return CreatedAtAction(nameof(GetAll), new { projectId }, session);
        }
        catch (KeyNotFoundException ex) { return NotFound(new { error = ex.Message }); }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
    }

    [HttpPut("{sessionId}")]
    public IActionResult Update(string projectId, string sessionId, [FromBody] UpdateSessionRequest req)
    {
        if (!OwnsProject(projectId)) return NotFound();
        var session = sessions.GetById(sessionId);
        if (session == null || session.ProjectId != projectId) return NotFound();
        if (req.NotificationsMuted is bool muted) sessions.SetNotificationsMuted(sessionId, muted);
        if (req.Archived is bool archived) sessions.SetArchived(sessionId, archived);
        // Хотя бы одно из двух — см. тот же блок в ChatsController.Update
        if (req.VoiceStyle is not null && !VoiceStyles.IsKnown(req.VoiceStyle))
            return BadRequest(new { error = "Неизвестный стиль озвучки" });
        if (req.VoiceMode is not null || req.VoiceStyle is not null)
            sessions.SetVoiceMode(sessionId, req.VoiceMode, req.VoiceStyle);
        if (req.ExpiresAfterMinutes is not -1)
        {
            if (req.ExpiresAfterMinutes is <= 0) return BadRequest(new { error = "Срок жизни чата должен быть положительным" });
            sessions.SetExpiry(sessionId, req.ExpiresAfterMinutes);
        }
        if (req.ExcludeFromDossiers is { } optOut)
            sessions.SetExcludeFromDossiers(sessionId, optOut);
        try
        {
            var updated = sessions.Update(sessionId, req.Name, req.Model, req.Effort, req.Tags);
            return updated == null ? NotFound() : Ok(updated);
        }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
    }

    // Назначить/сменить/снять собеседника (персону или .md-агента) у проектной сессии — в т.ч. по ходу разговора
    [HttpPost("{sessionId}/persona")]
    public IActionResult SetPersona(string projectId, string sessionId, [FromBody] SetPersonaRequest req)
    {
        if (!OwnsProject(projectId)) return NotFound();
        var session = sessions.GetById(sessionId);
        if (session == null || session.ProjectId != projectId) return NotFound();
        try
        {
            var updated = sessions.SetPersona(sessionId, UserId, req.PersonaId, req.AgentName);
            return updated == null ? NotFound() : Ok(updated);
        }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
        catch (KeyNotFoundException ex) { return BadRequest(new { error = ex.Message }); }
    }

    // GET /api/projects/{id}/sessions/{sid}/history — история чата.
    // Без параметров пагинации отдаёт ПОЛНЫЙ плоский массив (прежний контракт — старые клиенты
    // не ломаются). С limit и/или before включает постраничный режим и возвращает объект
    // { messages, hasMore, cursor }: это режет длинную историю (5+ МБ целиком) до ~100
    // сообщений хвоста, а более ранние догружаются по курсору кнопкой «Показать ранее».
    [HttpGet("{sessionId}/history")]
    public async Task<IActionResult> GetHistory(
        string projectId, string sessionId,
        [FromQuery] int? limit = null,
        [FromQuery] int? before = null)
    {
        if (!OwnsProject(projectId)) return NotFound();
        var session = sessions.GetById(sessionId);
        if (session == null || session.ProjectId != projectId) return NotFound();

        var history = await sessions.GetHistoryAsync(sessionId);

        // Ни одного параметра пагинации — прежний контракт: полный список как плоский массив
        if (limit is null && before is null)
            return Ok(history);

        // before — индекс, ДО которого (эксклюзивно) отдать сообщения. Несуществующий индекс
        // (за пределами истории или отрицательный) — 400, как требует инвариант задачи
        if (before is not null && !ChatHistoryPaginator.IsCursorValid(history.Count, before.Value))
            return BadRequest(new { error = "Курсор before указывает за пределы истории" });

        var page = ChatHistoryPaginator.Slice(history, limit, before);
        return Ok(page);
    }

    [HttpDelete("{sessionId}")]
    // Секция destructive: на делегированном ходу агент не удаляет чаты (см. FilesController.Delete)
    [DenyOnDelegatedTurn("Удаление чата")]
    public async Task<IActionResult> Delete(string projectId, string sessionId)
    {
        if (!OwnsProject(projectId)) return NotFound();
        var session = sessions.GetById(sessionId);
        if (session == null) return NoContent(); // идемпотентное удаление
        if (session.ProjectId != projectId) return NotFound(); // чужая сессия — не удаляем
        await sessions.DeleteAsync(sessionId);
        return NoContent();
    }

    // Подобрать значки-иконки чатам проекта без них (действие AI-палитры «Проставить значки
    // тем» в разделе проекта). Возвращает счётчики для тоста.
    [HttpPost("icon-batch")]
    public async Task<IActionResult> IconBatch(string projectId, CancellationToken ct)
    {
        if (!OwnsProject(projectId)) return NotFound();
        try
        {
            var result = await sessions.SetChatIconsAsync(UserId, ct, projectId);
            return Ok(result);
        }
        catch (InvalidOperationException ex) { return StatusCode(502, new { error = ex.Message }); }
    }
}

// PersonaId — собеседник нового чата; под флагом default-personas-onboarding обязателен
// (либо resumeSessionId — продолжение существующего разговора).
public record CreateSessionRequest(string Mode = "acceptEdits", string? ResumeSessionId = null, string? Name = null, string? Model = null, string? AgentName = null, string? Effort = null, string? PersonaId = null);

// ExpiresAfterMinutes: -1 (поле не прислано) — не менять; null — сделать сессию постоянной;
// N > 0 — временная, авто-удаление через N минут после последней активности
// ExcludeFromDossiers: null (поле не прислано) — не менять; иначе — признак opt-out
// «Истории решений» (ADR-004 §6, тумблер «Не сохранять решения из этого чата»)
// NotificationsMuted: null — не менять; true — заглушить уведомления чата
// Archived: null — не менять; true — убрать чат в архив; false — вернуть из архива
// VoiceMode: null — не менять; иначе — озвучка ответов включена/выключена
// VoiceStyle: null — не менять; иначе VoiceStyles.Talk | Digest (см. UpdateChatRequest)
public record UpdateSessionRequest(string? Name = null, string? Model = null, string? Effort = null, int? ExpiresAfterMinutes = -1, List<string>? Tags = null, bool? ExcludeFromDossiers = null, bool? NotificationsMuted = null, bool? VoiceMode = null, string? VoiceStyle = null, bool? Archived = null);
