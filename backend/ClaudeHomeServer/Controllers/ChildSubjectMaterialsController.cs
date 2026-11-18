using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Auth;
using ClaudeHomeServer.Services.ChildMode;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Controllers;

/// <summary>
/// Материалы предмета — опись, загрузка, удаление (разрез «Знания репетитора» §2, К2b).
/// Ручки только для админа: класс закрывает роль по claim ([Authorize(Roles="admin")],
/// как у UsersController) и роль владельца по стору (AdminByStore: сервисный токен хода
/// админа допускается, снятое админство не ждёт истечения токена). [ChildAllowed] здесь
/// нет намеренно: И6 — ни один путь не отдаёт материалы ребёнку. Ребёнок отбивается здесь
/// раньше детского гейта: классовой роли его токен (role "user", JwtService.cs:58) не
/// удовлетворяет на UseAuthorization (Program.cs:1151 стоит раньше UseChildModeGate, :1155),
/// поэтому отказ ребёнка — 403 без тела {error:"child_mode"}, как у PUT child-profile
/// (тот же признак, что у UsersChildProfileTests.NotAdmin_Returns403).
///
/// Работа — только через ChildSubjectMaterialsStore (К2a): опись data/child-subjects/
/// {childId}/{subjectId}/materials.json, оригиналы и текст — там же. Ручки «скачать
/// материал» нет: содержимое файла наружу не отдаётся никогда, опись — только строки
/// {id, name, kind, bytes, uploadedAt} (внутренние storedName/textName и uploadedBy
/// наружу не выходят).
///
/// Порядок отказов: 404 child_not_found (пользователь {id} не ребёнок — проверка ДО
/// стора; отсутствующий пользователь и не-ребёнок дают одно тело, существование не
/// раскрываем) → 404 subject_not_found (предмета нет в профиле ребёнка) → коды стора:
/// 413 too_large, 413 subject_full, 415 unsupported_type, 422 pdf_no_text, 422 docx_no_text.
/// Ревью КИ1b: код стора child_gone (ребёнка удалили, пока шла загрузка) наружу — то же
/// 404 child_not_found, что и у проверки до стора: нового кода клиенту не нужно.
/// Пустой файл отдельного кода не имеет: пустые байты — не поддерживаемый тип, стор
/// отвечает unsupported_type (ChildAttachmentPolicy.Detect, ChildAttachmentPolicy.cs:33).
/// Исключение стора наружу не выходит: 500 {code:"storage_error"}.
/// uploadedBy — id вызывающего админа из sub токена: его пишет стор, в ответе поля нет.
/// </summary>
[ApiController]
[Authorize(Roles = "admin")]
[Authorize(Policy = AdminByStoreRequirement.PolicyName)]
[Route("api/users/{id}/child-subjects")]
public class ChildSubjectMaterialsController(
    ChildSubjectMaterialsStore materials,
    UserStore users,
    ILogger<ChildSubjectMaterialsController> logger) : ControllerBase
{
    // Транспортный потолок тела — дефолт ChildMode:Materials:MaxFileBytes (20 МБ):
    // контрактная проверка — в сторе (значение конфига может быть снижено без правки
    // здесь; поднять выше этого потолка — правка обеих величин одним решением,
    // тот же приём, что у ChildAttachmentsController).
    [HttpGet("{subjectId}/materials")]
    public async Task<IActionResult> ListMaterials(string id, string subjectId)
    {
        if (ChildOf(id) is not { } profile) return ChildNotFound();
        if (!HasSubject(profile, subjectId)) return SubjectNotFound();

        try
        {
            var list = await materials.ListAsync(id, subjectId);
            return Ok(list.Select(ToDto).ToList());
        }
        catch (Exception e)
        {
            return StorageFailure(e, id, subjectId);
        }
    }

    [HttpPost("{subjectId}/materials")]
    [RequestSizeLimit(ChildSubjectMaterialsStore.DefaultMaxFileBytes)]
    public async Task<IActionResult> AddMaterial(string id, string subjectId, IFormFile? file, CancellationToken ct)
    {
        // Автор загрузки — вызывающий админ по стору (политика AdminByStore уже проверила
        // его по стору; sub токена — тот же id).
        var callerId = User.FindFirstValue(JwtRegisteredClaimNames.Sub);
        if (string.IsNullOrWhiteSpace(callerId)) return StorageFailure(
            new InvalidOperationException("У токена нет владельца."), id, subjectId);

        if (ChildOf(id) is not { } profile) return ChildNotFound();
        if (!HasSubject(profile, subjectId)) return SubjectNotFound();

        using var ms = new MemoryStream();
        if (file is not null) await file.CopyToAsync(ms, ct);
        var bytes = ms.ToArray();

        try
        {
            var res = await materials.AddAsync(id, subjectId, file?.FileName ?? "", bytes, callerId);
            if (res.Accepted)
                return CreatedAtAction(nameof(ListMaterials), new { id, subjectId }, ToDto(res.Material!));

            var (status, code, message) = Refusal(res.Code!);
            return new JsonResult(new { code, message }) { StatusCode = status };
        }
        catch (Exception e)
        {
            return StorageFailure(e, id, subjectId);
        }
    }

    [HttpDelete("{subjectId}/materials/{materialId}")]
    public async Task<IActionResult> DeleteMaterial(string id, string subjectId, string materialId)
    {
        if (ChildOf(id) is not { } profile) return ChildNotFound();
        if (!HasSubject(profile, subjectId)) return SubjectNotFound();

        try
        {
            if (!await materials.DeleteAsync(id, subjectId, materialId))
                return NotFound(new { code = "material_not_found", message = "Материал не найден." });
            return NoContent();
        }
        catch (Exception e)
        {
            return StorageFailure(e, id, subjectId);
        }
    }

    // ── проверки до стора ────────────────────────────────────────────────────────────

    // «Ребёнок» = живой профиль с Enabled (ChildModePolicy: «профиль выключен» = обычный
    // пользователь). Отсутствующий пользователь и не-ребёнок — одно тело 404.
    private ChildProfile? ChildOf(string id) =>
        users.GetById(id)?.ChildMode is { Enabled: true } profile ? profile : null;

    // Предмет — по живому профилю ребёнка (любой Enabled: админ правит материалы и
    // скрытого предмета). id — Ordinal: slug из валидатора профиля, case-insensitive
    // здесь не нужен и открыл бы чужой предмет.
    private static bool HasSubject(ChildProfile profile, string subjectId) =>
        profile.Subjects?.Any(s => string.Equals(s?.Id, subjectId, StringComparison.Ordinal)) == true;

    private static JsonResult ChildNotFound() =>
        new(new { code = "child_not_found", message = "Ребёнок не найден." }) { StatusCode = StatusCodes.Status404NotFound };

    private static JsonResult SubjectNotFound() =>
        new(new { code = "subject_not_found", message = "Предмет не найден." }) { StatusCode = StatusCodes.Status404NotFound };

    // Коды отказа стора → HTTP-коды разреза §2. storage_error (и любой незнакомый код) —
    // 500: необработанного исключения стора наружу нет.
    private static (int Status, string Code, string Message) Refusal(string code) => code switch
    {
        ChildSubjectMaterialsStore.CodeTooLarge =>
            (StatusCodes.Status413PayloadTooLarge, code, "Файл больше разрешённого."),
        ChildSubjectMaterialsStore.CodeSubjectFull =>
            (StatusCodes.Status413PayloadTooLarge, code, "Предмет заполнен: достигнут суммарный размер материалов."),
        ChildSubjectMaterialsStore.CodeUnsupportedType =>
            (StatusCodes.Status415UnsupportedMediaType, code, "Такой файл нельзя добавить: подходит текстовый PDF, DOCX или TXT."),
        ChildSubjectMaterialsStore.CodePdfNoText =>
            (StatusCodes.Status422UnprocessableEntity, code, "В этом PDF нет текста — попробуйте текстовый файл."),
        ChildSubjectMaterialsStore.CodeDocxNoText =>
            (StatusCodes.Status422UnprocessableEntity, code, "В этом DOCX нет текста."),
        // Ребёнка удалили посреди загрузки (ревью КИ1b): наружу — то же тело, что у своей
        // проверки до стора; код стора в ответ не выходит.
        ChildSubjectMaterialsStore.CodeChildGone =>
            (StatusCodes.Status404NotFound, "child_not_found", "Ребёнок не найден."),
        _ =>
            (StatusCodes.Status500InternalServerError, code, "Материал не сохранён."),
    };

    private JsonResult StorageFailure(Exception e, string id, string subjectId)
    {
        logger.LogWarning(e, "Детский режим: ручка материалов предмета не отработала (child {Child}, subject {Subject})", id, subjectId);
        return new JsonResult(new { code = ChildSubjectMaterialsStore.CodeStorageError, message = "Материал не сохранён." })
        { StatusCode = StatusCodes.Status500InternalServerError };
    }

    // Wire-строка описи (разрез §2): {id, name, kind, bytes, uploadedAt}. Содержания нет;
    // внутренних имён стора (storedName/textName) и автора загрузки в ответе нет.
    private static MaterialDto ToDto(ChildSubjectMaterial m) =>
        new(m.Id, m.Name, m.Kind, m.Bytes, m.UploadedAt);

    private sealed record MaterialDto(string Id, string Name, string Kind, long Bytes, DateTimeOffset UploadedAt);
}
