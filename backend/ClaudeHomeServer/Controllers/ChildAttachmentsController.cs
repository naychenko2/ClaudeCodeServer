using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.ChildMode;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Controllers;

/// <summary>
/// Загрузка вложения в урок (ADR-041 «Контракт», ручка №8; решение Андрея №5, вариант «б»;
/// с решения 89 — запись в папку предмета). Маршрут, тело и коды ответа не меняются;
/// меняется то, что внутри: файл пишет общий писатель ChildTextCopyService (Б11b) в папку
/// предмета этого урока по пути «Вложения/{имя}», а его текстовую копию — там же по форме
/// «.ccs-text/{путь}.txt» (решение 90). Служебная раскладка вложений чата и учёт в
/// Session.Attachments у урока больше нет. Ответ — путь оригинала: фронт передаёт его в attachedPaths, а копию до
/// хода подставляет фильтр хаба (ChildHubFilter, решение 90). Тип — по сигнатуре байтов
/// (ChildAttachmentPolicy.Detect — allow-list ручки, как был: офисные файлы в него не входят);
/// размер — maxAttachmentBytes калькулятора; метр папки — писателя. Расширение сохраняемого
/// имени нормализуется по определённому типу (NormalizeExtension): kind в ответе и расширение
/// path не расходятся — их читает по расширению downstream.
///
/// Владелец — только из JWT; ребёнок опознаётся по стору через IChildModePolicy (как у
/// ChildChatsController). Чат — свой урок ребёнка в проекте-предмете этого ребёнка
/// (решение 89): чужой, не детский, несуществующий и старый урок без проекта (решение 51) —
/// одно тело 404 (существование не раскрываем). Лимит размера — единственный лимит
/// калькулятора, нужный здесь: загрузка не ход, поэтому состояния И3 (окно, бюджет,
/// правила) НЕ проверяются — их точка — гейт запуска хода (KM-B5d).
///
/// Порядок отказов: 403 not_child → 404 chat_not_found → 422 empty_file → 413 too_large →
/// 415 unsupported_type → 413 folder_limit → 409 exists → 422 pdf_no_text → 503
/// policy_unavailable. Неизвестного исключения наружу нет: storage_error писателя и любой
/// сбой вызова — 503; полуприложения в «Вложения/» не остаётся (писатель снимает то, что
/// записал этим же вызовом).
/// </summary>
[ApiController]
[Authorize]
[Route("api/child")]
public class ChildAttachmentsController(
    IChildModePolicy policy,
    ChildLimitsCalculator limits,
    SessionManager sessions,
    ProjectManager projects,
    ChildTextCopyService childTextCopy,
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

        // Свой, не детский и несуществующий — неотличимы (тот же приём, что у voice и чтения истории).
        // С решения 89 сюда относится и старый урок без проекта (решение 51): его вложений
        // эта ручка не принимает — то же 404, существование не раскрываем.
        var chat = sessions.GetOwnedVisible(id, UserId);
        if (chat is null || chat.ChildSpace is not string space)
            return ChildError(404, "chat_not_found", "Чат не найден.");

        // Урок — чат проекта-предмета этого ребёнка (решение 89): чужой или не предметный
        // проект — тот же 404 (per-owner: проект-предмета ищется по владельцу, как в
        // ChildChatsController.SubjectProjectOf).
        if (chat.ProjectId is not { } projectId
            || projects.GetById(projectId) is not { } subjectProject
            || !ProjectCapabilities.IsChildSubject(subjectProject)
            || subjectProject.OwnerId != UserId)
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

        // Корень чата урока — папка его проекта-предмета (тот же резолв, что у фильтра хаба
        // и рельсы «Файлы»): вложения урока живут внутри неё.
        var root = sessions.GetChatRoot(id, UserId);
        if (root is null) return ChildError(404, "chat_not_found", "Чат не найден.");

        // Имя — только имя файла: multipart-имя может нести путь, а папка предмета не должна
        // получать путь вне «Вложения/» (тот же смысл, что санация старой раскладки).
        var fileName = Path.GetFileName(file.FileName);
        if (fileName.Length == 0) return ChildError(422, "empty_file", "Файл пустой или без имени.");

        // Расширение — по определённому типу (возврат ревью R-KM-B6e): downstream читает тип
        // снова по расширению (AttachmentInliner.SplitImagePaths, ChildHubFilter.TextCopyIfExists)
        // — оно не должно расходиться с kind. Имя остаётся читаемым, меняется только расширение.
        var rel = ChildAttachmentPolicy.NormalizeExtension(AttachmentsFolder + "/" + fileName, kind, bytes);

        // Путь судит граница до писателя (тот не исходит за её вердикт): «Вложения/» — обычная
        // папка ребёнка, не защищённая ChildPathPolicy; судится и форма после нормализации.
        if (!ChildPathPolicy.Decide(ChildActor.Child, ChildPathOp.Write, rel, root).Allowed)
            return ChildError(422, "empty_file", "Файл пустой или без имени.");

        try
        {
            // Файл пишет общий писатель (Б11b, решения 56/89/90): тип, размер, метр папки и
            // текстовая копия решаются в нём один раз. requireText=true только для PDF: файла
            // без текста не будет — писатель снимает записанный этим же вызовом оригинал,
            // полуприложения в «Вложения/» не остаётся. Совпадение имени — правило писателя
            // (как у `upload` рельсы): файл перезаписывается, каталог на имени — 409.
            var res = await childTextCopy.SaveAsync(
                root, rel, bytes, maxBytes, requireText: kind is ChildAttachmentPolicy.Kind.Pdf, ct);
            if (!res.Accepted)
                return res.Code switch
                {
                    ChildTextCopyService.CodeTooLarge =>
                        ChildError(413, "too_large", "Файл больше разрешённого."),
                    ChildTextCopyService.CodeFolderLimit =>
                        ChildError(413, "folder_limit", "В этой папке уже всё занято. Позови взрослого."),
                    ChildTextCopyService.CodeUnsupportedType =>
                        ChildError(415, "unsupported_type", "Такой файл нельзя приложить."),
                    ChildTextCopyService.CodeExists =>
                        ChildError(409, "exists", "Файл с таким именем уже есть."),
                    ChildTextCopyService.CodeNoText =>
                        ChildError(422, "pdf_no_text", "В этом PDF нет текста — попробуйте картинку или обычный текст."),
                    _ => Unavailable(),
                };

            // Ответ — путь оригинала (решение 89): фронт передаёт его в attachedPaths, копию
            // до хода подставляет фильтр хаба. kind и name — как были (контракт фронта
            // image/text: PDF входит в ход текстом своей копии).
            var wire = kind is ChildAttachmentPolicy.Kind.Image ? "image" : "text";
            return Ok(new { path = res.Path, kind = wire, name = fileName });
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Детский режим: вложение урока не сохранено (owner {Owner}, chat {Chat})", UserId, id);
            return Unavailable();
        }
    }

    // Папка вложений урока в папке предмета (решение 89): её зовёт только эта ручка;
    // остальное в папке предмета — за рельсой.
    private const string AttachmentsFolder = "Вложения";

    private static JsonResult Unavailable() =>
        ChildError(503, "policy_unavailable", "Настройки режима не читаются — попросите взрослого проверить.");

    private static JsonResult ChildError(int status, string code, string message) =>
        new(new { code, message }) { StatusCode = status };
}
