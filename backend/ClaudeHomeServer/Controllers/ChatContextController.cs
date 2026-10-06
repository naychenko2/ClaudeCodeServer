using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.Json.Nodes;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.ChatContext;
using ClaudeHomeServer.Services.Composition;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Controllers;

// Контекст чата (ADR-023 §2.1): основной объект и референсы. Владелец — из sub, сессия — GetOwned
// (чужая или несуществующая — 404). Область (проект или личный чат) вертикаль выводит сама, поэтому
// путь один. Проверку Ref и подписи делают провайдеры видов; выключенная вертикаль провайдера не
// регистрирует — её элементы приходят с missing, а запись такого вида отказывает 400 kind_unknown.
// Локальный проект (ADR-016): сервер его файлов не видит, поэтому чтение отдаёт пустой контекст, а
// запись — 400 project_local_unsupported до любого обращения к диску.
[ApiController]
[Authorize]
public class ChatContextController(
    SessionManager sessions,
    IProjectManager projects,
    ContextKindRegistry registry,
    IChatContextStore store,
    IEnumerable<IChatSavedFiles> savedFiles) : ControllerBase
{
    private string UserId => User.FindFirstValue(JwtRegisteredClaimNames.Sub)!;

    [HttpGet(ChatContextRoutes.Base)]
    public IActionResult Get(string sessionId)
    {
        if (Scope(sessionId) is not { } scope) return NotFound();
        if (FilesUnavailable(scope)) return Ok(new ChatContextDto(0, null, []));
        return Ok(Dto(scope, store.Get(UserId, sessionId)));
    }

    [HttpPut(ChatContextRoutes.Primary)]
    public IActionResult PutPrimary(string sessionId, [FromBody] ChatContextPrimaryRequest req)
    {
        if (Scope(sessionId) is not { } scope) return NotFound();
        if (Refuse(scope) is { } refused) return refused;
        return Write(scope, () =>
        {
            if (req.Kind is null) return store.SetPrimary(UserId, sessionId, null, req.Revision);
            var item = Check(scope, req.Kind, req.Ref, null);
            return store.SetPrimary(UserId, sessionId, item, req.Revision);
        });
    }

    [HttpPost(ChatContextRoutes.Refs)]
    public IActionResult PostRef(string sessionId, [FromBody] ChatContextRefRequest req)
    {
        if (Scope(sessionId) is not { } scope) return NotFound();
        if (Refuse(scope) is { } refused) return refused;
        return Write(scope, () =>
        {
            var role = string.IsNullOrWhiteSpace(req.Role) ? null : req.Role.Trim();
            var item = Check(scope, req.Kind, req.Ref, role);
            if (role is not null) RequireRole(scope, store.Get(UserId, sessionId).Primary, item);
            return store.AddRef(UserId, sessionId, item, req.Revision);
        });
    }

    [HttpDelete(ChatContextRoutes.Ref)]
    public IActionResult DeleteRef(string sessionId, string itemId, [FromQuery] long? revision)
    {
        if (Scope(sessionId) is not { } scope) return NotFound();
        if (Refuse(scope) is { } refused) return refused;
        return Write(scope, () => store.RemoveRef(UserId, sessionId, itemId, revision));
    }

    [HttpDelete(ChatContextRoutes.Base)]
    public IActionResult Clear(string sessionId, [FromQuery] long? revision)
    {
        if (Scope(sessionId) is not { } scope) return NotFound();
        if (Refuse(scope) is { } refused) return refused;
        return Write(scope, () => store.Clear(UserId, sessionId, revision));
    }

    [HttpGet(ChatContextRoutes.SavedFiles)]
    public IActionResult SavedFiles(string sessionId)
    {
        if (Scope(sessionId) is not { } scope) return NotFound();
        if (FilesUnavailable(scope)) return Ok(Array.Empty<ChatSavedFile>());
        return Ok(savedFiles.SelectMany(s => s.List(scope)).OrderBy(f => f.SavedAt).ToList());
    }

    private ContextScope? Scope(string sessionId)
    {
        if (sessions.GetOwned(sessionId, UserId) is not { } session) return null;
        var project = session.ProjectId is { } pid ? projects.GetById(pid) : null;
        return new ContextScope(UserId, session, project);
    }

    private static bool FilesUnavailable(ContextScope scope) =>
        scope.Project is { } p && !ProjectCapabilityGuard.Allows(p, ProjectCapabilityArea.FileBound);

    private IActionResult? Refuse(ContextScope scope) =>
        FilesUnavailable(scope)
            ? BadRequest(new { error = ChatContextErrors.ProjectLocalUnsupported, message = ProjectCapabilityGuard.FilesOnDeviceReason })
            : null;

    private ChatContextDto Dto(ContextScope scope, ChatContextState state) =>
        ChatContextDtoBuilder.Build(registry, scope, state);

    // Вид известен, Ref — объект и проходит Validate провайдера владельца; иначе ChatContextException → 400
    private ContextItem Check(ContextScope scope, string? kind, JsonObject? reference, string? role)
    {
        if (string.IsNullOrWhiteSpace(kind) || !registry.IsRegistered(kind))
            throw new ChatContextException(ChatContextErrors.KindUnknown, $"Вид контекста «{kind}» не зарегистрирован");
        if (reference is null)
            throw new ChatContextException(ChatContextErrors.RefInvalid, "Не указан ref");
        if (registry.Validate(scope, kind, reference) is { } refusal)
            throw new ChatContextException(ChatContextErrors.RefInvalid, refusal);
        return new ContextItem("ci_" + Guid.NewGuid().ToString("N")[..12], kind, reference, role,
            ContextActor.Human, DateTime.UtcNow);
    }

    // Роль референса принадлежит принимающей операции: основного нет или его таблица роли не знает — отказ
    private void RequireRole(ContextScope scope, ContextItem? primary, ContextItem item)
    {
        var accepted = primary is not null && registry.Find(primary.Kind) is { } owner
            ? owner.AcceptedRefs(scope, primary, null)
            : [];
        if (!accepted.Any(a => a.Role == item.Role && a.Kinds.Contains(item.Kind)))
            throw new ChatContextException(ChatContextErrors.RoleNotAccepted,
                $"Основной объект не принимает референс «{item.Kind}» с ролью «{item.Role}»");
    }

    private IActionResult Write(ContextScope scope, Func<ChatContextState> write)
    {
        try
        {
            return Ok(Dto(scope, write()));
        }
        catch (ChatContextConflictException ex)
        {
            return StatusCode(StatusCodes.Status409Conflict,
                new ChatContextConflictDto(ex.Code, Dto(scope, ex.Current)));
        }
        catch (ChatContextException ex)
        {
            return BadRequest(new { error = ex.Code, message = ex.Message });
        }
    }
}

// Тела ручек: revision — та, от которой считал клиент; null — без сверки
public sealed record ChatContextPrimaryRequest(string? Kind, JsonObject? Ref, long? Revision);

public sealed record ChatContextRefRequest(string? Kind, JsonObject? Ref, string? Role, long? Revision);
