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
public class SessionsController(SessionManager sessions, ProjectManager projects, FeatureFlagService flags,
    DefaultAssistantProvisioner provisioner, PersonaManager personas) : ControllerBase
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
        // Инвариант десктопного чата (ADR-008): собственный транскрипт — ни из чужого,
        // ни в чужие руки. Проверка ДО провижна персоны: отказ не должен заводить сущности.
        if (req.Desktop && !flags.IsEnabled(UserId, FeatureFlagKeys.DesktopAgent))
            return BadRequest(new { error = DesktopChatGuard.FlagOff });
        if (DesktopChatGuard.Refuse(sessions, req.Desktop, req.ResumeSessionId) is string desktopRefusal)
            return BadRequest(new { error = desktopRefusal });
        string? personaId = req.PersonaId;
        // Последний рубеж инварианта «новый чат человека — только с персоной» (парная связь
        // с фронтом): без personaId/resumeSessionId сначала провижним ассистента и продолжим
        // с ним. 400 — только когда провижн невозможен. Служебные пути (задачи, one-shot)
        // REST не используют, поэтому гейт их не задевает.
        if (string.IsNullOrWhiteSpace(personaId) && string.IsNullOrWhiteSpace(req.ResumeSessionId))
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
                req.Model, req.AgentName, req.Effort, personaId: personaId, desktopChat: req.Desktop);
            return CreatedAtAction(nameof(GetAll), new { projectId }, session);
        }
        catch (KeyNotFoundException ex) { return NotFound(new { error = ex.Message }); }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
    }

    [HttpPut("{sessionId}")]
    public async Task<IActionResult> Update(string projectId, string sessionId, [FromBody] UpdateSessionRequest req)
    {
        if (!OwnsProject(projectId)) return NotFound();
        var session = sessions.GetById(sessionId);
        if (session == null || session.ProjectId != projectId) return NotFound();
        if (req.NotificationsMuted is bool muted) sessions.SetNotificationsMuted(sessionId, muted);
        if (req.VoiceMode is bool voice) sessions.SetVoiceMode(sessionId, voice);
        if (req.ExpiresAfterMinutes is not -1)
        {
            if (req.ExpiresAfterMinutes is <= 0) return BadRequest(new { error = "Срок жизни чата должен быть положительным" });
            sessions.SetExpiry(sessionId, req.ExpiresAfterMinutes);
        }
        if (req.ExcludeFromDossiers is { } optOut)
            sessions.SetExcludeFromDossiers(sessionId, optOut);
        try
        {
            var updated = await sessions.UpdateAsync(sessionId, UserId, req.Name, req.Model, req.Effort, req.Tags);
            return updated == null ? NotFound() : Ok(updated);
        }
        catch (KeyNotFoundException) { return NotFound(); }
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
// Desktop — тип чата «Десктопный» (ADR-008): только в проекте, только со своим транскриптом.
public record CreateSessionRequest(string Mode = "acceptEdits", string? ResumeSessionId = null, string? Name = null, string? Model = null, string? AgentName = null, string? Effort = null, string? PersonaId = null, bool Desktop = false);

/// <summary>
/// Инвариант десктопного чата (ADR-008, «Последствия»): собственный ClaudeSessionId —
/// десктопный чат нельзя создать из resumeSessionId и нельзя продолжить из его транскрипта.
///
/// Правило одно на оба входа создания чатов (проектный и вне проекта), поэтому живёт
/// отдельным классом, а не дублируется в двух контроллерах. Второе направление важнее
/// первого: в .jsonl десктопного чата лежат кадры чужого рабочего стола, и обычный чат,
/// продолженный из него, вынес бы их за периметр грани — вместе с фолбэком к стороннему
/// провайдеру, из которого десктопный чат намеренно выведен.
/// </summary>
internal static class DesktopChatGuard
{
    public const string FlagOff =
        "Десктопный чат недоступен: включите «Десктопный агент» в экспериментальных функциях";

    public const string ResumeIntoDesktop =
        "Десктопный чат нельзя создать из другого чата: у него собственная сессия Claude";

    public const string ResumeFromDesktop =
        "Это транскрипт десктопного чата: продолжить его в обычном чате нельзя";

    public const string OutsideProject =
        "Десктопный чат создаётся только в проекте: грань десктопного агента включается в проекте";

    /// <summary>Текст отказа, либо null — создавать можно.</summary>
    public static string? Refuse(SessionManager sessions, bool desktop, string? resumeSessionId)
    {
        if (string.IsNullOrWhiteSpace(resumeSessionId)) return null;
        if (desktop) return ResumeIntoDesktop;

        var csid = resumeSessionId.Trim();
        // Сравнение по живым чатам: транскрипт удалённого десктопного чата уносится вместе
        // с ним (инвариант удаления чата), так что «висящего» csid тут не остаётся.
        return sessions.GetAll().Any(s => s.DesktopChat && s.ClaudeSessionId == csid)
            ? ResumeFromDesktop
            : null;
    }
}

// ExpiresAfterMinutes: -1 (поле не прислано) — не менять; null — сделать сессию постоянной;
// N > 0 — временная, авто-удаление через N минут после последней активности
// ExcludeFromDossiers: null (поле не прислано) — не менять; иначе — признак opt-out
// «Истории решений» (ADR-004 §6, тумблер «Не сохранять решения из этого чата»)
// NotificationsMuted: null — не менять; true — заглушить уведомления чата
// VoiceMode: null — не менять; иначе — голосовой режим (короткий формат ответа + озвучка)
public record UpdateSessionRequest(string? Name = null, string? Model = null, string? Effort = null, int? ExpiresAfterMinutes = -1, List<string>? Tags = null, bool? ExcludeFromDossiers = null, bool? NotificationsMuted = null, bool? VoiceMode = null);
