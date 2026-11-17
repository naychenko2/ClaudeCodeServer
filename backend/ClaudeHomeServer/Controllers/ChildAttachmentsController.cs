using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.ChildMode;
using ClaudeHomeServer.Services.Docs;
using ClaudeHomeServer.Services.Files;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Controllers;

/// <summary>
/// Загрузка вложения в детский чат (KM-B6e, ADR-041 «Контракт», ручка №8; решение Андрея №5,
/// вариант «б»): allow-list типов по сигнатуре байтов (ChildAttachmentPolicy), PDF превращается
/// в текст на сервере (тот же путь, что files_document_read: MarkitdownService), инструмент Read
/// ребёнку не открывается. Картинка доходит до модели image-блоком, текст — инлайном
/// (AttachmentInliner, как у обычных вложений); производный .txt текста учитывается в
/// Session.Attachments рядом с PDF (SaveDerivedChatAttachment) — фильтр хаба пропускает его
/// в attachedPaths, иначе текст до модели не дошёл бы. Расширение сохраняемого имени нормализуется
/// по определённому типу (NormalizeExtension): kind в ответе и расширение path не расходятся —
/// их читает по расширению downstream.
///
/// Владелец — только из JWT; ребёнок опознаётся по стору через IChildModePolicy (как у
/// ChildChatsController). Чат — свой, вне проекта и с ChildSpace: чужой, не детский и
/// несуществующий — одно тело 404 (существование не раскрываем). Лимит размера — единственный
/// лимит калькулятора, нужный здесь: загрузка не ход, поэтому состояния И3 (окно, бюджет,
/// правила) НЕ проверяются — их точка — гейт запуска хода (KM-B5d).
///
/// Порядок отказов: 403 not_child → 404 chat_not_found → 422 empty_file → 413 too_large →
/// 415 unsupported_type → 422 pdf_no_text → 503 policy_unavailable. Неизвестного исключения
/// наружу нет: сбой записи или чтения — 503.
/// </summary>
[ApiController]
[Authorize]
[Route("api/child")]
public class ChildAttachmentsController(
    IChildModePolicy policy,
    ChildLimitsCalculator limits,
    SessionManager sessions,
    FileService files,
    MarkitdownService markitdown,
    ILogger<ChildAttachmentsController> logger) : ControllerBase
{
    private string UserId => User.FindFirstValue(JwtRegisteredClaimNames.Sub)!;

    // Транспортный потолок тела — дефолт maxAttachmentBytes ADR (10 МБ): контрактная проверка
    // — по file.Length ниже (значение ChildMode:MaxAttachmentBytes может быть снижено без правки
    // здесь; поднять выше этого потолка — правка обеих величин одним решением).
    [ChildAllowed]
    [HttpPost("chats/{id}/attachments")]
    [RequestSizeLimit(10 * 1024 * 1024)]
    public async Task<IActionResult> Upload(string id, IFormFile? file, CancellationToken ct)
    {
        var snapshot = policy.For(UserId);
        if (snapshot is null) return ChildError(403, "not_child", "Эта ручка только для детей.");
        if (snapshot.DenyAll || snapshot.Profile is null) return Unavailable();

        // Свой, не детский и несуществующий — неотличимы (тот же приём, что у voice и чтения истории)
        var chat = sessions.GetOwnedVisible(id, UserId);
        if (chat is null || chat.ProjectId is not null || chat.ChildSpace is not string space)
            return ChildError(404, "chat_not_found", "Чат не найден.");

        // Лимит — из той же точки расчёта, что статус и запуск хода (И3); состояние не смотрим.
        long maxBytes;
        try
        {
            maxBytes = limits.Evaluate(UserId, space, DateTimeOffset.UtcNow)!.MaxAttachmentBytes;
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Детский режим: лимиты не посчитались при загрузке вложения (owner {Owner}, chat {Chat})", UserId, id);
            return Unavailable();
        }

        if (file is null || file.Length == 0) return ChildError(422, "empty_file", "Файл пустой.");
        if (file.Length > maxBytes) return ChildError(413, "too_large", "Файл больше разрешённого.");

        using var ms = new MemoryStream();
        await file.CopyToAsync(ms, ct);
        var bytes = ms.ToArray();
        if (bytes.Length == 0) return ChildError(422, "empty_file", "Файл пустой.");

        var kind = ChildAttachmentPolicy.Detect(bytes);
        if (kind is ChildAttachmentPolicy.Kind.Unsupported)
            return ChildError(415, "unsupported_type", "Такой файл нельзя приложить.");

        var root = sessions.GetChatRoot(id, UserId);
        if (root is null) return ChildError(404, "chat_not_found", "Чат не найден.");

        try
        {
            // Расширение сохраняемого имени — по определённому типу (возврат ревью R-KM-B6e):
            // downstream читает тип снова по расширению (AttachmentInliner.SplitImagePaths,
            // MarkitdownService) — оно не должно расходиться с kind. Имя остаётся читаемым,
            // меняется только расширение.
            var saveName = ChildAttachmentPolicy.NormalizeExtension(file.FileName, kind, bytes);

            // Раскладка и учёт — те же, что у обычной загрузки (ChatsController.Upload):
            // {AttachmentsDir}/{guid}/{имя}; своя подпапка на каждый файл — два файла с одним
            // именем не затирают друг друга.
            var rel = sessions.SaveChatAttachment(id, UserId, saveName, r => files.WriteFileBytes(root, r, bytes));
            if (rel is null) return ChildError(422, "empty_file", "Файл пустой или без имени.");

            if (kind is ChildAttachmentPolicy.Kind.Image)
                return Ok(new { path = rel, kind = "image", name = Path.GetFileName(rel) });

            if (kind is ChildAttachmentPolicy.Kind.Pdf)
            {
                // Тот же путь, что files_document_read: MarkitdownService по абсолютному пути.
                // null или «не текст» (скан/битый) — отказ; PDF остаётся на диске учтённым,
                // но в ход не уходит: путь наружу не отдан, приложить его фронт не может.
                var md = await markitdown.ConvertAsync(
                    Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)), ct);
                if (md is null || !ChildAttachmentPolicy.IsUsableText(System.Text.Encoding.UTF8.GetBytes(md)))
                    return ChildError(422, "pdf_no_text", "В этом PDF нет текста — попробуйте картинку или обычный текст.");

                var text = ChildAttachmentPolicy.TruncateToTextBudget(md, out _);
                // Производный .txt учитывается рядом с учтённым PDF (доработка финала, п. c3.1):
                // путь вне Session.Attachments фильтр хаба (KM-B2) выбрасывает из attachedPaths,
                // и текст не доходит до модели. Учёт — под тем же VisibilityLock, запись файла —
                // до учёта (порядок SaveChatAttachment). Отказ — оригинала в учёте уже нет
                // (чата нет или его перенесла смена видимости): состояние не половиним, 503.
                var txtRel = sessions.SaveDerivedChatAttachment(id, rel, TextExtension,
                    r => files.WriteFileBytes(root, r, System.Text.Encoding.UTF8.GetBytes(text)));
                if (txtRel is null) return Unavailable();
                return Ok(new { path = txtRel, kind = "text", name = Path.GetFileName(rel) });
            }

            // text/plain: файл сохранён как есть — он и есть текстовое вложение.
            return Ok(new { path = rel, kind = "text", name = Path.GetFileName(rel) });
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Детский режим: вложение не сохранено (owner {Owner}, chat {Chat})", UserId, id);
            return Unavailable();
        }
    }

    // Расширение производного текста PDF: .txt рядом с оригиналом — ту же {guid}-папку и то же
    // имя даёт SaveDerivedChatAttachment по учтённому пути.
    private const string TextExtension = ".txt";

    private static JsonResult Unavailable() =>
        ChildError(503, "policy_unavailable", "Настройки режима не читаются — попросите взрослого проверить.");

    private static JsonResult ChildError(int status, string code, string message) =>
        new(new { code, message }) { StatusCode = status };
}
