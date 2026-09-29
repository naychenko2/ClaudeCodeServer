using System.Diagnostics.CodeAnalysis;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.ImageEditor.Threads;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Services.ImageEditor.Controllers;

// Общее тело ручек нитей картинок (ADR-019 §1) — одна реализация на проектный ThreadsController
// и личный PersonalThreadsController (ADR-018 §2: две копии разошлись бы в проверках). Наследник
// проходит свой гейт и передаёт область; нить по файлу и черновик в папке требуют диска проекта,
// у личной области — 400 до обращения к RootPath.
//
// Сессию не трогает: смена фокуса не пишет sessions.json и не двигает UpdatedAt — всё состояние в
// ImageThreadStore модуля. Следы в ленте (якоря, тихие строки) и событие image_thread_changed пишет
// ImageThreadService. Чужая нить в своём чате — 404 thread_not_found. Каждая мутация несёт
// revision, от которой считал фронт: устарела — 409 с актуальным состоянием. Ответ мутации —
// полное состояние нитей, как у GET.
//
// Версии (изменение 27.09): каждый вариант запуска — версия нити сам, без «Взять». Ручки
// current («продолжить от версии») и steps (правка без ИИ в текущую версию) — для всех нитей;
// take / rollback / dismiss — только для нитей до 27.09 со стопками. Сохранить в проект — только
// человек (решение Андрея 1).
public abstract class ImageThreadEndpoints(ImageThreadService threads, IImageEditJobs? jobs) : ControllerBase
{
    protected string UserId => User.FindFirstValue(JwtRegisteredClaimNames.Sub)!;

    protected IActionResult GetIn(string sessionId) => Ok(threads.Get(UserId, sessionId));

    // Взять картинку в работу: ровно одно из file (путь в проекте) и draftFolder (черновик «Новая
    // картинка», "" — корень). Нить по этому файлу уже есть — фокус на неё, второй не будет
    protected async Task<IActionResult> OpenIn(ImageEditScope scope, string sessionId, ImageThreadOpenRequest req,
        CancellationToken ct)
    {
        if ((req.File is null) == (req.DraftFolder is null))
            return Error(StatusCodes.Status400BadRequest, ImageEditErrorCodes.InvalidRequest,
                "Нужно ровно одно: файл картинки или папка черновика");

        string? file = null, folder = null;
        if (req.File is not null)
        {
            if (scope.Project is not { } project)
                return NoProjectFiles();
            if (Inside(project.RootPath, req.File) is not { } full || !System.IO.File.Exists(full))
                return Error(StatusCodes.Status400BadRequest, ImageEditErrorCodes.InvalidRequest, "Файл не найден в проекте");
            file = Relative(project.RootPath, full);
        }
        else if (req.DraftFolder!.Trim().Trim('/', '\\').Length > 0)
        {
            if (scope.Project is not { } project)
                return NoProjectFiles();
            if (Inside(project.RootPath, req.DraftFolder) is not { } full || !Directory.Exists(full))
                return Error(StatusCodes.Status400BadRequest, ImageEditErrorCodes.InvalidRequest, "Папка не найдена в проекте");
            folder = Relative(project.RootPath, full);
        }
        else folder = "";

        return Result(await threads.OpenAsync(UserId, scope.Key, sessionId, file, folder, req.Revision, ct));
    }

    // Взять картинку в работу или снять выбор (threadId = null)
    protected async Task<IActionResult> FocusIn(ImageEditScope scope, string sessionId, ImageThreadFocusRequest req) =>
        Result(await threads.FocusAsync(UserId, scope.Key, sessionId, req.ThreadId, req.Revision));

    // Убрать нить без шагов; у нити с шагами или с ожидающими вариантами — 400
    protected async Task<IActionResult> RemoveIn(ImageEditScope scope, string sessionId, string threadId, long revision) =>
        Result(await threads.RemoveAsync(UserId, scope.Key, sessionId, threadId, revision));

    // «Продолжить от версии»: версия становится текущей (от неё пойдёт следующая правка), нить —
    // в работе; stepId — ещё и шаг этой версии. Ничего не удаляет
    protected async Task<IActionResult> ContinueIn(ImageEditScope scope, string sessionId, string threadId,
        ImageThreadContinueRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.VersionId))
            return Error(StatusCodes.Status400BadRequest, ImageEditErrorCodes.InvalidRequest, "Не указана версия");
        var stepId = string.IsNullOrWhiteSpace(req.StepId) ? null : req.StepId.Trim();
        return Result(await threads.ContinueAsync(UserId, scope.Key, sessionId, threadId, req.VersionId.Trim(), stepId,
            req.Revision));
    }

    // Правка без ИИ: готовый шаг (POST …/transform) ложится шагом текущей версии — новой версии нет
    protected async Task<IActionResult> AddStepIn(ImageEditScope scope, string sessionId, string threadId,
        ImageThreadStepRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.StepId))
            return Error(StatusCodes.Status400BadRequest, ImageEditErrorCodes.InvalidRequest, "Не указан шаг");
        return TakeResult(await threads.AddStepAsync(UserId, scope.Key, sessionId, threadId, req.StepId.Trim(), req.Revision));
    }

    // «Взять» (нити до 27.09): вариант задачи этой нити (jobId + variant) или шаг правки без ИИ (stepId)
    protected async Task<IActionResult> TakeIn(ImageEditScope scope, string sessionId, string threadId,
        ImageThreadTakeRequest req, CancellationToken ct)
    {
        var hasJob = !string.IsNullOrWhiteSpace(req.JobId);
        if (hasJob == !string.IsNullOrWhiteSpace(req.StepId) || (hasJob && req.Variant is null))
            return Error(StatusCodes.Status400BadRequest, ImageEditErrorCodes.InvalidRequest,
                "Взять можно ровно одно: вариант задачи (jobId + variant) или шаг правки (stepId)");

        return TakeResult(await threads.TakeAsync(UserId, scope.Key, sessionId, threadId, req.JobId, req.Variant, req.StepId,
            req.Revision, jobs, ct));
    }

    // «Не брать»: варианты задачи больше не ждут выбора
    protected async Task<IActionResult> DismissIn(ImageEditScope scope, string sessionId, string threadId,
        ImageThreadDismissRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.JobId))
            return Error(StatusCodes.Status400BadRequest, ImageEditErrorCodes.InvalidRequest, "Не указана задача");
        return Result(await threads.DismissAsync(UserId, scope.Key, sessionId, threadId, req.JobId.Trim(), req.Revision));
    }

    // Откат (нити до 27.09): шаг стопки становится текущим (null — исходник); стопки не трогает.
    // У нити без стопок — 400: там «откат» — продолжить от версии
    protected async Task<IActionResult> RollbackIn(ImageEditScope scope, string sessionId, string threadId,
        ImageThreadRollbackRequest req)
    {
        var stepId = string.IsNullOrWhiteSpace(req.StepId) ? null : req.StepId.Trim();
        return Result(await threads.RollbackAsync(UserId, scope.Key, sessionId, threadId, stepId, req.Revision));
    }

    protected async Task<IActionResult> SettingsIn(ImageEditScope scope, string sessionId, string threadId,
        ImageThreadSettingsRequest req)
    {
        if (req.Settings is not { } settings || settings.Count < 1 || settings.Count > ImageEditCatalog.DefaultLimits.MaxCount)
            return Error(StatusCodes.Status400BadRequest, ImageEditErrorCodes.InvalidRequest,
                $"Число вариантов — от 1 до {ImageEditCatalog.DefaultLimits.MaxCount}");
        return Result(await threads.SetSettingsAsync(UserId, scope.Key, sessionId, threadId, settings, req.Revision));
    }

    private IActionResult TakeResult(ImageThreadTake taken)
    {
        if (taken.Write is { } written) return Result(written);
        var status = taken.ErrorCode switch
        {
            ImageEditErrorCodes.JobNotFound or ImageEditErrorCodes.StepNotFound => StatusCodes.Status404NotFound,
            ImageEditErrorCodes.RasterUnavailable => StatusCodes.Status503ServiceUnavailable,
            _ => StatusCodes.Status400BadRequest,
        };
        return Error(status, taken.ErrorCode!, taken.Error!);
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
        ImageThreadWriteStatus.VersionNotFound => Error(StatusCodes.Status404NotFound, ImageEditErrorCodes.VersionNotFound,
            "Версии нет в этой картинке"),
        ImageThreadWriteStatus.Invalid => Error(StatusCodes.Status400BadRequest, ImageEditErrorCodes.InvalidRequest,
            "Действие не подходит этой картинке: у неё уже есть шаги, версии или идёт генерация"),
        _ => Error(StatusCodes.Status404NotFound, ImageEditErrorCodes.ThreadNotFound, "Картинка не найдена в этом чате"),
    };

    private IActionResult NoProjectFiles() =>
        Error(StatusCodes.Status400BadRequest, ImageEditErrorCodes.InvalidRequest,
            "У чата вне проекта нет файлов проекта: только новая картинка");

    // Строго внутри корня и не через символическую ссылку
    private static string? Inside(string root, string rel) => ProjectLinkGuard.ResolveInside(root, rel);

    private static string Relative(string root, string full) =>
        Path.GetRelativePath(root, full).Replace('\\', '/');

    private ObjectResult Error(int status, string code, string error) => StatusCode(status, new { error, code });
}

// Нити картинок и фокус чата проекта (ADR-019 §1). Тело ручек — в ImageThreadEndpoints, здесь
// проектный гейт: флаг и чужой проект — одинаково 404; чужой, несуществующий и чат другого проекта
// неотличимы — 404 с одним и тем же телом.
[ProjectCapability(ProjectCapabilityArea.FileBound)]
[ApiController]
[Authorize]
[Route("api/projects/{projectId}/image-editor/sessions/{sessionId}/threads")]
public class ThreadsController(
    ImageEditScopeGate gate,
    ImageThreadService threads,
    IImageEditJobs? jobs = null) : ImageThreadEndpoints(threads, jobs)
{
    [HttpGet]
    public IActionResult Get(string projectId, string sessionId) =>
        Gate(projectId, sessionId, out _, out var denied) ? GetIn(sessionId) : denied;

    [HttpPost]
    public async Task<IActionResult> Open(string projectId, string sessionId, [FromBody] ImageThreadOpenRequest req,
        CancellationToken ct) =>
        Gate(projectId, sessionId, out var scope, out var denied) ? await OpenIn(scope, sessionId, req, ct) : denied;

    [HttpPut("focus")]
    public async Task<IActionResult> PutFocus(string projectId, string sessionId, [FromBody] ImageThreadFocusRequest req) =>
        Gate(projectId, sessionId, out var scope, out var denied) ? await FocusIn(scope, sessionId, req) : denied;

    [HttpDelete("{threadId}")]
    public async Task<IActionResult> Remove(string projectId, string sessionId, string threadId, [FromQuery] long revision) =>
        Gate(projectId, sessionId, out var scope, out var denied) ? await RemoveIn(scope, sessionId, threadId, revision) : denied;

    [HttpPut("{threadId}/current")]
    public async Task<IActionResult> Continue(string projectId, string sessionId, string threadId,
        [FromBody] ImageThreadContinueRequest req) =>
        Gate(projectId, sessionId, out var scope, out var denied) ? await ContinueIn(scope, sessionId, threadId, req) : denied;

    [HttpPost("{threadId}/steps")]
    public async Task<IActionResult> AddStep(string projectId, string sessionId, string threadId,
        [FromBody] ImageThreadStepRequest req) =>
        Gate(projectId, sessionId, out var scope, out var denied) ? await AddStepIn(scope, sessionId, threadId, req) : denied;

    [HttpPost("{threadId}/take")]
    public async Task<IActionResult> Take(string projectId, string sessionId, string threadId,
        [FromBody] ImageThreadTakeRequest req, CancellationToken ct) =>
        Gate(projectId, sessionId, out var scope, out var denied) ? await TakeIn(scope, sessionId, threadId, req, ct) : denied;

    [HttpPost("{threadId}/dismiss")]
    public async Task<IActionResult> Dismiss(string projectId, string sessionId, string threadId,
        [FromBody] ImageThreadDismissRequest req) =>
        Gate(projectId, sessionId, out var scope, out var denied) ? await DismissIn(scope, sessionId, threadId, req) : denied;

    [HttpPost("{threadId}/rollback")]
    public async Task<IActionResult> Rollback(string projectId, string sessionId, string threadId,
        [FromBody] ImageThreadRollbackRequest req) =>
        Gate(projectId, sessionId, out var scope, out var denied) ? await RollbackIn(scope, sessionId, threadId, req) : denied;

    [HttpPut("{threadId}/settings")]
    public async Task<IActionResult> Settings(string projectId, string sessionId, string threadId,
        [FromBody] ImageThreadSettingsRequest req) =>
        Gate(projectId, sessionId, out var scope, out var denied) ? await SettingsIn(scope, sessionId, threadId, req) : denied;

    private bool Gate(string projectId, string sessionId, [NotNullWhen(true)] out ImageEditScope? scope,
        [NotNullWhen(false)] out IActionResult? denied) =>
        gate.TryProjectChat(UserId, projectId, sessionId, out scope, out denied);
}

public sealed record ImageThreadFocusRequest(string? ThreadId, long Revision);

public sealed record ImageThreadOpenRequest(string? File, string? DraftFolder, long Revision);

public sealed record ImageThreadTakeRequest(string? JobId, int? Variant, string? StepId, long Revision);

public sealed record ImageThreadDismissRequest(string? JobId, long Revision);

public sealed record ImageThreadRollbackRequest(string? StepId, long Revision);

public sealed record ImageThreadContinueRequest(string? VersionId, string? StepId, long Revision);

public sealed record ImageThreadStepRequest(string? StepId, long Revision);

public sealed record ImageThreadSettingsRequest(ImageThreadSettings? Settings, long Revision);
