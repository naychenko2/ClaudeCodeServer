using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;
using ClaudeHomeServer.Services.AudioEditor.Jobs;
using ClaudeHomeServer.Services.AudioEditor.Threads;
using ClaudeHomeServer.Services.AudioEditor.Voices;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Media;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Services.AudioEditor.Controllers;

// Библиотека «Голоса» проекта (ADR-021 §2): список, голос из записей (загрузка и файлы проекта),
// переименование и расшифровка, образцы, удаление, модель RVC из версии нити обучения. Гейт — общий с
// модулем (флаг, свой проект — иначе 404); локальный проект отсекает атрибут FileBound до тела ручки, а
// VoiceLibrary ещё раз — до диска. У личного чата библиотеки нет: там только пустое состояние.
[ProjectCapability(ProjectCapabilityArea.FileBound)]
[ApiController]
[Authorize]
[Route("api/projects/{projectId}/audio-editor/voices")]
public class AudioVoicesController(
    AudioEditScopeGate gate,
    VoiceLibrary voices,
    AudioJobThreads threads,
    AudioEditWorkspace workspace,
    IProjectFiles? files = null) : ControllerBase
{
    private const string SubClaim = "sub";
    private const long MaxBodyBytes = (VoiceStore.MaxSamples + 1) * VoiceStore.MaxSampleMb * 1024L * 1024L;

    private string UserId => User.FindFirstValue(SubClaim)!;

    public sealed class VoiceSamplesForm
    {
        public string? Name { get; set; }
        public string? Transcript { get; set; }
        public List<IFormFile>? Files { get; set; }
        // Пути файлов проекта от корня
        public List<string>? ProjectFiles { get; set; }
    }

    public sealed record VoicePatchRequest(string? Name, string? Transcript);

    // Модель RVC из версии нити обучения: в версии обязаны быть файлы ролей model и index
    public sealed record VoiceFromRvcRequest(string? Name, string? SessionId, string? ThreadId, string? VersionId);

    [HttpGet]
    public IActionResult List(string projectId)
    {
        if (!Gate(projectId, out var scope, out var denied)) return denied;
        return voices.List(scope) is { } list ? Ok(new { available = true, voices = list }) : Refused(scope);
    }

    [HttpGet("{slug}")]
    public IActionResult Get(string projectId, string slug)
    {
        if (!Gate(projectId, out var scope, out var denied)) return denied;
        return voices.Get(scope, slug) is { } voice ? Ok(voice) : VoiceNotFound();
    }

    [HttpPost]
    [RequestSizeLimit(MaxBodyBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxBodyBytes)]
    public async Task<IActionResult> Create(string projectId, [FromForm] VoiceSamplesForm form, CancellationToken ct)
    {
        if (!Gate(projectId, out var scope, out var denied)) return denied;
        var (samples, bad) = await SamplesAsync(scope, form, ct);
        if (bad is not null) return bad;
        return Result(scope, voices.CreateFromSamples(scope, form.Name ?? "", form.Transcript, samples!), created: true);
    }

    [HttpPost("rvc")]
    public IActionResult CreateFromRvc(string projectId, [FromBody] VoiceFromRvcRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.SessionId))
            return Error(StatusCodes.Status400BadRequest, AudioEditErrorCodes.InvalidRequest, "Не указан чат");
        var sessionId = req.SessionId.Trim();
        if (!gate.TryProjectChat(UserId, projectId, sessionId, out var scope, out var denied)) return denied;

        var thread = threads.Store.Get(UserId, sessionId).Threads.FirstOrDefault(t => t.Id == req.ThreadId?.Trim());
        if (thread is null)
            return Error(StatusCodes.Status404NotFound, AudioEditErrorCodes.ThreadNotFound, "Звук не найден в этом чате");
        var version = string.IsNullOrWhiteSpace(req.VersionId) ? thread.CurrentVersion : thread.Version(req.VersionId.Trim());
        if (version is null)
            return Error(StatusCodes.Status404NotFound, AudioEditErrorCodes.VersionNotFound, "Версии нет в этом звуке");

        var model = version.Files.FirstOrDefault(f => f.Role == AudioFileRoles.Model);
        var index = version.Files.FirstOrDefault(f => f.Role == AudioFileRoles.Index);
        if (model is null || index is null)
            return Error(StatusCodes.Status400BadRequest, AudioEditErrorCodes.InvalidRequest,
                "В версии нет пары модели RVC: нужны .pth и .index");
        var modelPath = AudioVersionFiles.Resolve(workspace, UserId, scope, version, model);
        var indexPath = AudioVersionFiles.Resolve(workspace, UserId, scope, version, index);
        if (modelPath is null || indexPath is null)
            return Error(StatusCodes.Status404NotFound, AudioEditErrorCodes.FileNotFound,
                "Файлы модели больше не найдены — рабочая папка очищена");
        return Result(scope, voices.CreateFromRvc(scope, req.Name ?? "", modelPath, indexPath), created: true);
    }

    [HttpPatch("{slug}")]
    public IActionResult Update(string projectId, string slug, [FromBody] VoicePatchRequest req)
    {
        if (!Gate(projectId, out var scope, out var denied)) return denied;
        return voices.Update(scope, slug, req.Name, req.Transcript) is { } result ? Result(scope, result) : VoiceNotFound();
    }

    [HttpDelete("{slug}")]
    public IActionResult Delete(string projectId, string slug)
    {
        if (!Gate(projectId, out var scope, out var denied)) return denied;
        if (voices.RootOf(scope, out _) is null) return Refused(scope);
        if (!voices.Delete(scope, slug)) return VoiceNotFound();
        files?.NotifyMutated(scope.Project!.RootPath, $"{VoiceStore.Folder}/{slug}", FileMutationKind.Delete);
        return NoContent();
    }

    [HttpPost("{slug}/samples")]
    [RequestSizeLimit(MaxBodyBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxBodyBytes)]
    public async Task<IActionResult> AddSamples(string projectId, string slug, [FromForm] VoiceSamplesForm form, CancellationToken ct)
    {
        if (!Gate(projectId, out var scope, out var denied)) return denied;
        var (samples, bad) = await SamplesAsync(scope, form, ct);
        if (bad is not null) return bad;
        return voices.AddSamples(scope, slug, samples!) is { } result ? Result(scope, result) : VoiceNotFound();
    }

    [HttpDelete("{slug}/samples/{file}")]
    public IActionResult RemoveSample(string projectId, string slug, string file)
    {
        if (!Gate(projectId, out var scope, out var denied)) return denied;
        return voices.RemoveSample(scope, slug, file) is { } result ? Result(scope, result) : VoiceNotFound();
    }

    // Образец или файл модели для плеера и скачивания: только перечисленный в манифесте
    [HttpGet("{slug}/files/{file}")]
    public IActionResult VoiceFile(string projectId, string slug, string file)
    {
        if (!Gate(projectId, out var scope, out var denied)) return denied;
        if (voices.OpenFile(scope, slug, file) is not { } full)
            return Error(StatusCodes.Status404NotFound, AudioEditErrorCodes.FileNotFound, "Файл голоса не найден");
        return PhysicalFile(full, AudioVersionFiles.ContentTypeOf(full), enableRangeProcessing: true);
    }

    // Записи из загрузки и из проекта; байты читаются до библиотеки, формат она проверит сама
    private async Task<(List<VoiceSampleUpload>? Samples, IActionResult? Bad)> SamplesAsync(
        AudioEditScope scope, VoiceSamplesForm form, CancellationToken ct)
    {
        if (voices.RootOf(scope, out _) is not { } root) return (null, Refused(scope));
        var limit = VoiceStore.MaxSampleMb * 1024L * 1024L;
        var samples = new List<VoiceSampleUpload>();
        foreach (var upload in form.Files ?? [])
        {
            if (upload.Length > limit) return (null, TooLarge());
            using var ms = new MemoryStream();
            await upload.CopyToAsync(ms, ct);
            samples.Add(new VoiceSampleUpload(ms.ToArray()));
        }
        foreach (var rel in form.ProjectFiles ?? [])
        {
            if (string.IsNullOrWhiteSpace(rel) || ProjectLinkGuard.ResolveInside(root, rel.Trim()) is not { } full
                || !System.IO.File.Exists(full))
                return (null, Error(StatusCodes.Status400BadRequest, AudioEditErrorCodes.InvalidRequest,
                    "Файл не найден в проекте или путь идёт через символическую ссылку"));
            if (new FileInfo(full).Length > limit) return (null, TooLarge());
            samples.Add(new VoiceSampleUpload(await System.IO.File.ReadAllBytesAsync(full, ct)));
        }
        return (samples, null);
    }

    private IActionResult Result(AudioEditScope scope, AudioEditCallResult<VoiceChange> result, bool created = false)
    {
        if (result.Value is not { } change)
        {
            var status = result.ErrorCode switch
            {
                ProjectCapabilityGuard.Code => StatusCodes.Status409Conflict,
                AudioEditErrorCodes.FileNotFound => StatusCodes.Status404NotFound,
                _ => StatusCodes.Status400BadRequest,
            };
            return Error(status, result.ErrorCode ?? AudioEditErrorCodes.InvalidRequest, result.Error ?? "Запрос не выполнен");
        }
        // Новые файлы — обычная запись в проект: синк знаний и ватчеры узнают о них сразу
        var root = scope.Project!.RootPath;
        foreach (var rel in change.Written) files?.NotifyMutated(root, rel, FileMutationKind.Write);
        foreach (var rel in change.Deleted) files?.NotifyMutated(root, rel, FileMutationKind.Delete);
        var dto = VoiceDto.From(change.Manifest, voices.Now);
        return created ? StatusCode(StatusCodes.Status201Created, dto) : Ok(dto);
    }

    private IActionResult Refused(AudioEditScope scope)
    {
        voices.RootOf(scope, out var refusal);
        return Result(scope, refusal!);
    }

    private bool Gate(string projectId, [NotNullWhen(true)] out AudioEditScope? scope,
        [NotNullWhen(false)] out IActionResult? denied) =>
        gate.TryProject(UserId, projectId, out scope, out denied);

    private ObjectResult Error(int status, string code, string error) => StatusCode(status, new { error, code });

    private IActionResult VoiceNotFound() =>
        Error(StatusCodes.Status404NotFound, AudioEditErrorCodes.VoiceNotFound, "Голос не найден");

    private IActionResult TooLarge() =>
        Error(StatusCodes.Status400BadRequest, AudioEditErrorCodes.InvalidRequest, $"Запись больше {VoiceStore.MaxSampleMb} МБ");
}
