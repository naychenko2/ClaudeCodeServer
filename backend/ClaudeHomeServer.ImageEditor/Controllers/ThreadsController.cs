using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.ImageEditor.Threads;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Services.ImageEditor.Controllers;

// Нити картинок и фокус чата проекта (ADR-019 §1). Сессию не трогает: смена фокуса не пишет
// sessions.json и не двигает UpdatedAt — всё состояние в ImageThreadStore модуля. Следы в ленте
// (якоря стопок, тихие строки) и событие image_thread_changed пишет ImageThreadService.
//
// Гейты те же, что у остальных ручек редактора: флаг и чужой проект — одинаково 404. Чужой,
// несуществующий и чат другого проекта неотличимы — 404 с одним и тем же телом; чужая нить в
// своём чате — 404 thread_not_found. Каждая мутация несёт revision, от которой считал фронт:
// устарела — 409 с актуальным состоянием. Ответ мутации — полное состояние нитей, как у GET.
//
// Взять вариант, откатиться и сохранить — только человек (решение Андрея 1): ручки здесь,
// в тулсете агента таких инструментов нет.
[ProjectCapability(ProjectCapabilityArea.FileBound)]
[ApiController]
[Authorize]
[Route("api/projects/{projectId}/image-editor/sessions/{sessionId}/threads")]
public class ThreadsController(
    IFeatureFlagGate flags,
    IProjectManager projects,
    ISessionDirectory directory,
    ImageThreadService threads,
    IImageEditJobs? jobs = null) : ControllerBase
{
    private string UserId => User.FindFirstValue(JwtRegisteredClaimNames.Sub)!;

    [HttpGet]
    public IActionResult Get(string projectId, string sessionId)
    {
        if (Gate(projectId, sessionId, out _) is { } denied) return denied;
        return Ok(threads.Get(UserId, sessionId));
    }

    // Взять картинку в работу: ровно одно из file (путь в проекте) и draftFolder (черновик «Новая
    // картинка», "" — корень). Нить по этому файлу уже есть — фокус на неё, второй не будет
    [HttpPost]
    public async Task<IActionResult> Open(string projectId, string sessionId, [FromBody] ImageThreadOpenRequest req,
        CancellationToken ct)
    {
        if (Gate(projectId, sessionId, out var project) is { } denied) return denied;
        if ((req.File is null) == (req.DraftFolder is null))
            return Error(StatusCodes.Status400BadRequest, ImageEditErrorCodes.InvalidRequest,
                "Нужно ровно одно: файл картинки или папка черновика");

        string? file = null, folder = null;
        if (req.File is not null)
        {
            if (Inside(project.RootPath, req.File) is not { } full || !System.IO.File.Exists(full))
                return Error(StatusCodes.Status400BadRequest, ImageEditErrorCodes.InvalidRequest, "Файл не найден в проекте");
            file = Relative(project.RootPath, full);
        }
        else if (req.DraftFolder!.Trim().Trim('/', '\\').Length > 0)
        {
            if (Inside(project.RootPath, req.DraftFolder) is not { } full || !Directory.Exists(full))
                return Error(StatusCodes.Status400BadRequest, ImageEditErrorCodes.InvalidRequest, "Папка не найдена в проекте");
            folder = Relative(project.RootPath, full);
        }
        else folder = "";

        return Result(await threads.OpenAsync(UserId, projectId, sessionId, file, folder, req.Revision, ct));
    }

    // Взять картинку в работу или снять выбор (threadId = null)
    [HttpPut("focus")]
    public async Task<IActionResult> PutFocus(string projectId, string sessionId, [FromBody] ImageThreadFocusRequest req)
    {
        if (Gate(projectId, sessionId, out _) is { } denied) return denied;
        return Result(await threads.FocusAsync(UserId, projectId, sessionId, req.ThreadId, req.Revision));
    }

    // Убрать нить без шагов; у нити с шагами или с ожидающими вариантами — 400
    [HttpDelete("{threadId}")]
    public async Task<IActionResult> Remove(string projectId, string sessionId, string threadId, [FromQuery] long revision)
    {
        if (Gate(projectId, sessionId, out _) is { } denied) return denied;
        return Result(await threads.RemoveAsync(UserId, projectId, sessionId, threadId, revision));
    }

    // «Взять»: вариант задачи этой нити (jobId + variant) или шаг правки без ИИ (stepId)
    [HttpPost("{threadId}/take")]
    public async Task<IActionResult> Take(string projectId, string sessionId, string threadId,
        [FromBody] ImageThreadTakeRequest req, CancellationToken ct)
    {
        if (Gate(projectId, sessionId, out _) is { } denied) return denied;
        var hasJob = !string.IsNullOrWhiteSpace(req.JobId);
        if (hasJob == !string.IsNullOrWhiteSpace(req.StepId) || (hasJob && req.Variant is null))
            return Error(StatusCodes.Status400BadRequest, ImageEditErrorCodes.InvalidRequest,
                "Взять можно ровно одно: вариант задачи (jobId + variant) или шаг правки (stepId)");

        var taken = await threads.TakeAsync(UserId, projectId, sessionId, threadId, req.JobId, req.Variant, req.StepId,
            req.Revision, jobs, ct);
        if (taken.Write is { } written) return Result(written);
        var status = taken.ErrorCode switch
        {
            ImageEditErrorCodes.JobNotFound or ImageEditErrorCodes.StepNotFound => StatusCodes.Status404NotFound,
            ImageEditErrorCodes.RasterUnavailable => StatusCodes.Status503ServiceUnavailable,
            _ => StatusCodes.Status400BadRequest,
        };
        return Error(status, taken.ErrorCode!, taken.Error!);
    }

    // «Не брать»: варианты задачи больше не ждут выбора
    [HttpPost("{threadId}/dismiss")]
    public async Task<IActionResult> Dismiss(string projectId, string sessionId, string threadId,
        [FromBody] ImageThreadDismissRequest req)
    {
        if (Gate(projectId, sessionId, out _) is { } denied) return denied;
        if (string.IsNullOrWhiteSpace(req.JobId))
            return Error(StatusCodes.Status400BadRequest, ImageEditErrorCodes.InvalidRequest, "Не указана задача");
        return Result(await threads.DismissAsync(UserId, projectId, sessionId, threadId, req.JobId.Trim(), req.Revision));
    }

    // Откат: шаг нити становится текущим (null — исходник); стопки не трогает
    [HttpPost("{threadId}/rollback")]
    public async Task<IActionResult> Rollback(string projectId, string sessionId, string threadId,
        [FromBody] ImageThreadRollbackRequest req)
    {
        if (Gate(projectId, sessionId, out _) is { } denied) return denied;
        var stepId = string.IsNullOrWhiteSpace(req.StepId) ? null : req.StepId.Trim();
        return Result(await threads.RollbackAsync(UserId, projectId, sessionId, threadId, stepId, req.Revision));
    }

    [HttpPut("{threadId}/settings")]
    public async Task<IActionResult> Settings(string projectId, string sessionId, string threadId,
        [FromBody] ImageThreadSettingsRequest req)
    {
        if (Gate(projectId, sessionId, out _) is { } denied) return denied;
        if (req.Settings is not { } settings || settings.Count < 1 || settings.Count > ImageEditCatalog.DefaultLimits.MaxCount)
            return Error(StatusCodes.Status400BadRequest, ImageEditErrorCodes.InvalidRequest,
                $"Число вариантов — от 1 до {ImageEditCatalog.DefaultLimits.MaxCount}");
        return Result(await threads.SetSettingsAsync(UserId, projectId, sessionId, threadId, settings, req.Revision));
    }

    private IActionResult Result(ImageThreadWrite written) => written.Status switch
    {
        ImageThreadWriteStatus.Ok => Ok(written.State),
        ImageThreadWriteStatus.Conflict => StatusCode(StatusCodes.Status409Conflict, new
        {
            error = "Картинки чата уже поменялись — перечитайте их",
            code = ImageEditErrorCodes.RevisionConflict,
            state = written.State,
        }),
        ImageThreadWriteStatus.StepNotFound => Error(StatusCodes.Status404NotFound, ImageEditErrorCodes.StepNotFound,
            "Шага нет в этой картинке"),
        ImageThreadWriteStatus.Invalid => Error(StatusCodes.Status400BadRequest, ImageEditErrorCodes.InvalidRequest,
            "У картинки уже есть шаги или варианты ждут выбора — убрать её нельзя"),
        _ => Error(StatusCodes.Status404NotFound, ImageEditErrorCodes.ThreadNotFound, "Картинка не найдена в этом чате"),
    };

    private IActionResult? Gate(string projectId, string sessionId, out Project project)
    {
        project = null!;
        if (!flags.IsEnabled(UserId, FeatureFlagKeys.ImageEditor))
            return NotFound(new { error = "Редактор картинок выключен" });
        var found = projects.GetById(projectId);
        if (found is null || found.OwnerId != UserId)
            return NotFound(new { error = "Проект не найден" });
        // Чат этого проекта (а проект уже свой): владение следует из проекта
        if (directory.GetById(sessionId) is not { } session || session.ProjectId != found.Id)
            return Error(StatusCodes.Status404NotFound, ImageEditErrorCodes.ChatNotFound, "Чат не найден");
        project = found;
        return null;
    }

    // Строго внутри корня и не через символическую ссылку
    private static string? Inside(string root, string rel) => ProjectLinkGuard.ResolveInside(root, rel);

    private static string Relative(string root, string full) =>
        Path.GetRelativePath(root, full).Replace('\\', '/');

    private ObjectResult Error(int status, string code, string error) => StatusCode(status, new { error, code });
}

public sealed record ImageThreadFocusRequest(string? ThreadId, long Revision);

public sealed record ImageThreadOpenRequest(string? File, string? DraftFolder, long Revision);

public sealed record ImageThreadTakeRequest(string? JobId, int? Variant, string? StepId, long Revision);

public sealed record ImageThreadDismissRequest(string? JobId, long Revision);

public sealed record ImageThreadRollbackRequest(string? StepId, long Revision);

public sealed record ImageThreadSettingsRequest(ImageThreadSettings? Settings, long Revision);
