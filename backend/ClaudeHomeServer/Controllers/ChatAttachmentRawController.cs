using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ClaudeHomeServer.Filters;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Access;
using ClaudeHomeServer.Services.ChildMode;
using ClaudeHomeServer.Services.Composition;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Controllers;

/// <summary>
/// Чтение картинки-вложения чата для миниатюры в пузыре (KM-B7, ADR-041 «Контракт», ручка №12;
/// решение Андрея 17.11, п.6): ОДНА ручка на всех пользователей и одна граница доступа.
/// У чатов вне проекта ручки чтения из {дом}/Chats/.cc-attachments раньше не было вовсе.
///
/// Инвариант: отдаётся только УЧТЁННАЯ картинка ВИДИМОГО вызывающему чата. Порядок отказов:
/// чат не виден (у ребёнка — не свой или без ChildSpace) → 404 chat_not_found; всё остальное
/// — 404 file_not_found одним телом (ответ не раскрывает, что файл есть): локальный проект,
/// путь не равен записи Session.Attachments (точное сравнение, не префикс), чужой private/{автор},
/// выход за корень, не картинка по сигнатуре, файла нет. 415 не отдаётся.
/// Тип — по сигнатуре байтов, а не по расширению (мутация .exe→.png). Ресайза нет.
/// </summary>
[ProjectCapability(ProjectCapabilityArea.Platform)]
[ApiController]
[Authorize]
[Route("api/chats")]
public class ChatAttachmentRawController(
    SessionManager sessions,
    ProjectManager projects,
    IChildModePolicy childPolicy,
    ILogger<ChatAttachmentRawController> logger) : ControllerBase
{
    private string UserId => User.FindFirstValue(JwtRegisteredClaimNames.Sub)!;

    // Сигнатуре хватает начала файла: PNG — 8 байт, WEBP — 12, GIF — 6.
    private const int HeadBytes = 12;

    [ChildAllowed]
    [HttpGet("{id}/attachments/raw")]
    public IActionResult Raw(string id, [FromQuery] string? path)
    {
        // Чат виден вызывающему. Ребёнку — только свой чат вне проекта с ChildSpace (та же граница,
        // что у GET api/chats/{id}); чужой, не детский и несуществующий — одно тело 404.
        string tenantOwnerId;
        Session chat;
        if (childPolicy.For(UserId) is not null)
        {
            if (sessions.GetById(id) is not { } own || own.ProjectId is not null
                || own.OwnerId != UserId || own.ChildSpace is null)
                return Error("chat_not_found", "Чат не найден.");
            chat = own;
            tenantOwnerId = UserId;
        }
        else
        {
            if (sessions.GetAccessible(id, UserId) is not { } found)
                return Error("chat_not_found", "Чат не найден.");
            chat = found.Session;
            tenantOwnerId = found.Scope.TenantOwnerId;
        }

        // Файлы локального проекта живут на устройстве: к диску сервера не обращаемся (ADR-016 §4)
        if (chat.ProjectId is { } pid && projects.GetById(pid) is { } project
            && ProjectCapabilityGuard.Refusal(project, ProjectCapabilityArea.FileBound) is not null)
            return NoFile();

        // Нормализация формы пути — только слеши и ведущий «/»; дальше точное равенство записи учёта
        var rel = (path ?? "").Replace('\\', '/').TrimStart('/');
        if (rel.Length == 0 || !sessions.IsRecordedAttachment(id, rel)) return NoFile();
        if (PrivateAttachments.IsForeign(rel, UserId)) return NoFile();

        var root = sessions.GetChatRoot(id, tenantOwnerId);
        if (root is null) return NoFile();

        try
        {
            var full = SafePath.Join(root, rel);
            if (!System.IO.File.Exists(full)) return NoFile();

            var head = new byte[HeadBytes];
            int read;
            using (var fs = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                read = fs.Read(head, 0, head.Length);

            if (ChildAttachmentPolicy.ImageMimeBySignature(head.AsSpan(0, read)) is not { } mime) return NoFile();

            Response.Headers["X-Content-Type-Options"] = "nosniff";
            Response.Headers["Content-Security-Policy"] = "sandbox";
            Response.Headers["Cache-Control"] = "private, max-age=86400";
            return PhysicalFile(full, mime);
        }
        catch (UnauthorizedAccessException) { return NoFile(); }
        catch (IOException e)
        {
            logger.LogDebug(e, "Чтение вложения чата {Chat} не удалось", id);
            return NoFile();
        }
    }

    private static NotFoundObjectResult NoFile() =>
        new(new { code = "file_not_found", message = "Файл не найден." });

    private static NotFoundObjectResult Error(string code, string message) => new(new { code, message });
}
