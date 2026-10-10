using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Composition;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Services.ImageEditor.Controllers;

// Обучающие фото и обучение LoRA персонажа (ADR-026-contracts §7.1–7.2). Шов обучения
// необязательный: подсистема images выключена — 503 no_seam, а не 500. Чужой проект — 404 на гейте.
[ProjectCapability(ProjectCapabilityArea.FileBound)]
[ApiController]
[Authorize]
[Route("api/projects/{projectId}/image-editor/characters/{slug}")]
public class CharacterLoraTrainController(
    ImageEditScopeGate gate,
    CharacterTrainingPhotos photos,
    CharacterLoraRuns runs,
    IConfiguration config,
    ILocalLoraTrainer? trainer = null,
    LoraTrainingFinisher? finisher = null) : ControllerBase
{
    private const int MaxFilesPerRequest = 10;
    private const long MaxBodyBytes = 100L * 1024 * 1024;
    private const int DefaultStartSeconds = 150;

    private string UserId => User.FindFirstValue(JwtRegisteredClaimNames.Sub)!;

    // ── Обучающие фото ─────────────────────────────────────────────────────────────

    [HttpGet("training-photos")]
    public IActionResult Photos(string projectId, string slug) =>
        Character(projectId, slug, out _, out var denied) ? Ok(PhotosDto(projectId, slug)) : denied;

    [HttpPost("training-photos")]
    [RequestSizeLimit(MaxBodyBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxBodyBytes)]
    public async Task<IActionResult> AddPhotos(string projectId, string slug, CancellationToken ct)
    {
        if (!Character(projectId, slug, out _, out var denied)) return denied;
        // Файлы берём из формы вручную: пустое тело — наш 400 invalid_request, а не ProblemDetails биндера
        IReadOnlyList<IFormFile> files = [];
        try
        {
            if (Request.HasFormContentType) files = (await Request.ReadFormAsync(ct)).Files.GetFiles("files");
        }
        catch (InvalidDataException) { }
        if (files.Count is 0 or > MaxFilesPerRequest)
            return Error(StatusCodes.Status400BadRequest, ImageEditErrorCodes.InvalidRequest,
                $"Нужно от 1 до {MaxFilesPerRequest} файлов за запрос");
        var uploads = new List<PhotoUpload>(files.Count);
        var tooBig = new List<TrainingPhotoRejection>();
        foreach (var file in files)
        {
            // Крупнее потолка байты не читаем: сразу в rejected с причиной too_big
            if (file.Length > CharacterTrainingPhotos.MaxPhotoMb * 1024L * 1024)
            {
                tooBig.Add(new TrainingPhotoRejection(file.FileName, "too_big"));
                continue;
            }
            using var ms = new MemoryStream();
            await file.CopyToAsync(ms, ct);
            uploads.Add(new PhotoUpload(file.FileName, ms.ToArray()));
        }
        var result = uploads.Count > 0 ? photos.Add(UserId, projectId, slug, uploads) : null;
        return Ok(new
        {
            photos = PhotosDto(projectId, slug),
            rejected = tooBig.Concat(result?.Rejected ?? []).Select(r => new { r.FileName, r.Reason }),
        });
    }

    [HttpGet("training-photos/{name}")]
    public IActionResult Photo(string projectId, string slug, string name)
    {
        if (!Character(projectId, slug, out _, out var denied)) return denied;
        if (photos.PathOf(UserId, projectId, slug, name) is not { } path) return PhotoNotFound();
        var type = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".webp" => "image/webp",
            _ => "image/jpeg",
        };
        return PhysicalFile(path, type);
    }

    [HttpDelete("training-photos/{name}")]
    public IActionResult DeletePhoto(string projectId, string slug, string name)
    {
        if (!Character(projectId, slug, out _, out var denied)) return denied;
        return photos.Remove(UserId, projectId, slug, name) ? NoContent() : PhotoNotFound();
    }

    // ── Обучение ───────────────────────────────────────────────────────────────────

    [HttpGet("lora-train")]
    public async Task<IActionResult> TrainState(string projectId, string slug, CancellationToken ct)
    {
        if (!Character(projectId, slug, out var project, out var denied)) return denied;
        if (trainer is null) return NoSeam();
        // Итог обучения, завершившегося между кругами доводчика, подбираем до ответа
        if (finisher is not null) await finisher.SettleAsync(UserId, projectId, slug, ct);
        var busy = trainer.Busy(UserId);
        var all = runs.List(project.RootPath, slug);
        var training = all.FirstOrDefault(r => r.Status == CharacterLoraRuns.Status.Training);
        object? current = null;
        if (training is not null)
        {
            var state = training.JobId is null ? null : await trainer.GetAsync(UserId, training.JobId, ct);
            current = new
            {
                runId = training.RunId,
                jobId = training.JobId,
                status = state is null ? "running" : state.Status.ToString().ToLowerInvariant(),
                step = state?.Step,
                totalSteps = state?.TotalSteps,
                etaSeconds = state?.EtaSeconds,
                queuePosition = state?.QueuePosition,
                startedAt = state?.StartedAt,
            };
        }
        var active = CharacterStore.Get(project.RootPath, slug)?.ReadLora()?.Name;
        var connected = all.FirstOrDefault(r => CharacterLoraRuns.IsConnected(r, active));
        var changed = connected is not null && connected.PhotosHash != CurrentHash(projectId, slug);
        return Ok(new
        {
            enabled = trainer.Enabled,
            available = trainer.Enabled,
            gpu = new { busy = busy is not null, mine = busy?.Mine ?? false, busyUntil = busy?.BusyUntil },
            current,
            defaultTrigger = DefaultTrigger(slug),
            presets = new[] { new { key = LoraTrainPresets.Standard, etaSeconds = PresetEta() } },
            photosChanged = changed,
        });
    }

    [HttpPost("lora-train")]
    public async Task<IActionResult> StartTrain(string projectId, string slug, [FromBody] LoraTrainRequest? req, CancellationToken ct)
    {
        if (!Character(projectId, slug, out var project, out var denied)) return denied;
        if (trainer is null) return NoSeam();
        if (!trainer.Enabled)
            return Error(StatusCodes.Status403Forbidden, "disabled", "Обучение LoRA выключено на этом сервере");
        if (req?.Confirm != true)
            return Error(StatusCodes.Status400BadRequest, "confirm_required", "Нужно подтвердить запуск обучения");
        var preset = req.Preset ?? LoraTrainPresets.Standard;
        if (!LoraTrainPresets.All.Contains(preset))
            return Error(StatusCodes.Status400BadRequest, "bad_preset", "Неизвестный пресет обучения");
        var trigger = string.IsNullOrWhiteSpace(req.Trigger) ? DefaultTrigger(slug) : req.Trigger.Trim();
        if (!LoraTrigger.IsValid(trigger))
            return Error(StatusCodes.Status400BadRequest, "bad_trigger", "Триггерное слово: латиница, цифры и «_», 3–24 символа, первая буква");
        using var startLock = await runs.LockKeyAsync($"{UserId}|{projectId}|{slug}", ct);
        // Идущее обучение этого персонажа видно по записи training — не полагаемся на ответ сервиса
        if (runs.List(project.RootPath, slug).Any(r => r.Status == CharacterLoraRuns.Status.Training))
            return Error(StatusCodes.Status409Conflict, "owner_busy", "У вас уже идёт обучение");
        if (runs.CountTowardsLimit(project.RootPath, slug) >= CharacterLoraRuns.MaxVersions)
            return Error(StatusCodes.Status409Conflict, "version_limit",
                $"У персонажа уже {CharacterLoraRuns.MaxVersions} версий LoRA — удалите лишнюю");
        var list = photos.List(UserId, projectId, slug);
        if (list.Count < CharacterTrainingPhotos.MinPhotos)
            return Error(StatusCodes.Status400BadRequest, "too_few_photos",
                $"Для обучения нужно не меньше {CharacterTrainingPhotos.MinPhotos} фото");

        var trainPhotos = new List<LoraTrainPhoto>(list.Count);
        foreach (var p in list)
        {
            if (photos.PathOf(UserId, projectId, slug, p.Name) is not { } path) continue;
            trainPhotos.Add(new LoraTrainPhoto(p.Name, path));
        }
        var runId = Guid.NewGuid().ToString("N")[..12];
        var started = await trainer.StartAsync(
            new LoraTrainStart(UserId, projectId, slug, runId, preset, trigger, trainPhotos), ct);
        if (started.Error is { } error) return MapStartError(error, started);

        var run = new CharacterLoraRun(1, runId, started.JobId, CharacterLoraRuns.Status.Training, null, preset, trigger,
            trainPhotos.Count, CharacterTrainingPhotos.PhotosHash(trainPhotos.Select(p => p.Name)),
            DateTimeOffset.UtcNow, null, null, null, null, null);
        runs.Create(project.RootPath, slug, run);
        return Accepted(new { runId, jobId = started.JobId, queuePosition = started.QueuePosition ?? 0, etaSeconds = started.EtaSeconds });
    }

    [HttpDelete("lora-train")]
    public async Task<IActionResult> CancelTrain(string projectId, string slug, CancellationToken ct)
    {
        if (!Character(projectId, slug, out var project, out var denied)) return denied;
        if (trainer is null) return NoSeam();
        var training = runs.List(project.RootPath, slug)
            .FirstOrDefault(r => r.Status == CharacterLoraRuns.Status.Training && r.JobId is not null);
        if (training is null) return NotTraining();
        var result = await trainer.CancelAsync(UserId, training.JobId!, asAdmin: false, ct);
        return result == LoraCancelResult.NotFound ? NotTraining() : NoContent();
    }

    // ── Общее ──────────────────────────────────────────────────────────────────────

    private IActionResult MapStartError(LoraTrainErrorCode error, LoraTrainStartResult started)
    {
        var message = started.Message;
        return error switch
        {
            LoraTrainErrorCode.Disabled => Error(StatusCodes.Status403Forbidden, "disabled", message ?? "Обучение LoRA выключено на этом сервере"),
            LoraTrainErrorCode.Unavailable => Error(StatusCodes.Status503ServiceUnavailable, "unavailable", message ?? "ComfyUI не отвечает"),
            LoraTrainErrorCode.OwnerBusy => Error(StatusCodes.Status409Conflict, "owner_busy", message ?? "У вас уже идёт обучение"),
            LoraTrainErrorCode.GpuBusy => StatusCode(StatusCodes.Status409Conflict,
                new { error = message ?? "Видеокарта занята чужим обучением", code = "gpu_busy", busyUntil = started.Busy?.BusyUntil }),
            LoraTrainErrorCode.QueueFull => Error(StatusCodes.Status409Conflict, "queue_full", message ?? "Очередь занята"),
            LoraTrainErrorCode.TooFewPhotos => Error(StatusCodes.Status400BadRequest, "too_few_photos", message ?? "Слишком мало фото"),
            LoraTrainErrorCode.BadPreset => Error(StatusCodes.Status400BadRequest, "bad_preset", message ?? "Неизвестный пресет обучения"),
            LoraTrainErrorCode.BadTrigger => Error(StatusCodes.Status400BadRequest, "bad_trigger", message ?? "Недопустимое триггерное слово"),
            _ => Error(StatusCodes.Status400BadRequest, ImageEditErrorCodes.InvalidRequest, message ?? "Некорректный запрос"),
        };
    }

    private object PhotosDto(string projectId, string slug)
    {
        var list = photos.List(UserId, projectId, slug);
        return new
        {
            items = list.Select(p => new { p.Name, p.Bytes, p.Width, p.Height }),
            count = list.Count,
            min = CharacterTrainingPhotos.MinPhotos,
            max = CharacterTrainingPhotos.MaxPhotos,
            photosHash = CharacterTrainingPhotos.PhotosHash(list.Select(p => p.Name)),
        };
    }

    private string CurrentHash(string projectId, string slug) =>
        CharacterTrainingPhotos.PhotosHash(photos.List(UserId, projectId, slug).Select(p => p.Name));

    // "ccs" + slug без дефисов, до 24 символов: по регэкспу триггера всегда валиден
    private static string DefaultTrigger(string slug)
    {
        var raw = "ccs" + slug.Replace("-", "");
        return raw.Length > 24 ? raw[..24] : raw;
    }

    private int PresetEta() =>
        LoraTrainPresets.Eta(LoraTrainPresets.Standard, config.GetValue("LocalMedia:LoraTrainStartSeconds", DefaultStartSeconds))!.Value;

    private bool Character(string projectId, string slug, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Project? project,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out IActionResult? denied)
    {
        if (!gate.TryProject(UserId, projectId, out project, out denied)) return false;
        if (!CharacterStore.IsValidSlug(slug) || CharacterStore.Get(project.RootPath, slug) is null)
        {
            denied = Error(StatusCodes.Status404NotFound, ImageEditErrorCodes.CharacterNotFound, "Персонаж не найден");
            return false;
        }
        return true;
    }

    private ObjectResult Error(int status, string code, string error) => StatusCode(status, new { error, code });

    private IActionResult NoSeam() =>
        Error(StatusCodes.Status503ServiceUnavailable, "no_seam", "Обучение LoRA недоступно: подсистема картинок выключена");

    private IActionResult NotTraining() =>
        Error(StatusCodes.Status404NotFound, "not_training", "Обучение не идёт");

    private IActionResult PhotoNotFound() =>
        Error(StatusCodes.Status404NotFound, "photo_not_found", "Фото не найдено");

    public sealed record LoraTrainRequest(string? Preset, string? Trigger, bool? Confirm);
}
