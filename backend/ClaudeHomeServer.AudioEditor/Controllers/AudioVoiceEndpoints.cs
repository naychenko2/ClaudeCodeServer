using System.Security.Claims;
using ClaudeHomeServer.Services.AudioEditor.Jobs;
using ClaudeHomeServer.Services.AudioEditor.Threads;
using ClaudeHomeServer.Services.AudioEditor.Voices;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Media;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Services.AudioEditor.Controllers;

// Тело ручек библиотеки «Голоса» (ADR-021 §2), общее для проекта (AudioVoicesController) и личного
// чата (PersonalAudioEditorController): гейт области — на стороне наследника, здесь только работа с
// областью. У личной области без папки библиотека пуста: чтение отвечает «голосов нет» и папку не
// создаёт, а создавать её перед записью обязан наследник (PersonalFolderResolver.EnsureAsync).
public abstract class AudioVoiceEndpoints(
    VoiceLibrary voices,
    AudioJobThreads threads,
    AudioEditWorkspace workspace,
    AudioEditJobService jobs,
    IProjectFiles? files) : ControllerBase
{
    private const string SubClaim = "sub";
    protected const long MaxBodyBytes = (VoiceStore.MaxSamples + 1) * VoiceStore.MaxSampleMb * 1024L * 1024L;

    protected string UserId => User.FindFirstValue(SubClaim)!;

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

    // Личная область, у которой папки «Личного» ещё нет: голосов не может быть
    private static bool NoPersonalFolder(AudioEditScope scope) => scope.IsPersonal && !scope.HasFolder;

    protected IActionResult ListIn(AudioEditScope scope)
    {
        if (NoPersonalFolder(scope)) return Ok(new { available = true, voices = Array.Empty<object>() });
        return voices.List(scope) is { } list ? Ok(new { available = true, voices = list }) : Refused(scope);
    }

    protected IActionResult GetIn(AudioEditScope scope, string slug) =>
        voices.Get(scope, slug) is { } voice ? Ok(voice) : VoiceNotFound();

    protected async Task<IActionResult> CreateIn(AudioEditScope scope, VoiceSamplesForm form, CancellationToken ct)
    {
        var (samples, bad) = await SamplesAsync(scope, form, ct);
        if (bad is not null) return bad;
        return Result(scope, voices.CreateFromSamples(scope, form.Name ?? "", form.Transcript, samples!), created: true);
    }

    // sessionId — чат, чьи нити ищутся; гейт чата уже пройден наследником
    protected IActionResult CreateFromRvcIn(AudioEditScope scope, string sessionId, VoiceFromRvcRequest req)
    {
        var thread = threads.Store.Get(AudioEditorEndpoints.Owner(scope), sessionId).Threads.FirstOrDefault(t => t.Id == req.ThreadId?.Trim());
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
        var modelPath = AudioVersionFiles.Resolve(workspace, AudioEditorEndpoints.Owner(scope), UserId, scope, version, model);
        var indexPath = AudioVersionFiles.Resolve(workspace, AudioEditorEndpoints.Owner(scope), UserId, scope, version, index);
        if (modelPath is null || indexPath is null)
            return Error(StatusCodes.Status404NotFound, AudioEditErrorCodes.FileNotFound,
                "Файлы модели больше не найдены — рабочая папка очищена");
        return Result(scope, voices.CreateFromRvc(scope, req.Name ?? "", modelPath, indexPath), created: true);
    }

    protected IActionResult UpdateIn(AudioEditScope scope, string slug, VoicePatchRequest req)
    {
        if (NoPersonalFolder(scope)) return VoiceNotFound();
        return voices.Update(scope, slug, req.Name, req.Transcript) is { } result ? Result(scope, result) : VoiceNotFound();
    }

    protected IActionResult DeleteIn(AudioEditScope scope, string slug)
    {
        if (NoPersonalFolder(scope)) return VoiceNotFound();
        if (voices.RootOf(scope, out _) is null) return Refused(scope);
        if (!voices.Delete(scope, slug)) return VoiceNotFound();
        Notify(scope, $"{VoiceStore.Folder}/{slug}", FileMutationKind.Delete);
        return NoContent();
    }

    protected async Task<IActionResult> AddSamplesIn(AudioEditScope scope, string slug, VoiceSamplesForm form, CancellationToken ct)
    {
        if (NoPersonalFolder(scope)) return VoiceNotFound();
        var (samples, bad) = await SamplesAsync(scope, form, ct);
        if (bad is not null) return bad;
        return voices.AddSamples(scope, slug, samples!) is { } result ? Result(scope, result) : VoiceNotFound();
    }

    protected IActionResult RemoveSampleIn(AudioEditScope scope, string slug, string file)
    {
        if (NoPersonalFolder(scope)) return VoiceNotFound();
        return voices.RemoveSample(scope, slug, file) is { } result ? Result(scope, result) : VoiceNotFound();
    }

    protected async Task<IActionResult> RecreateIn(AudioEditScope scope, string slug, string? provider, string? quoteId,
        CancellationToken ct)
    {
        if (voices.Get(scope, slug) is null) return VoiceNotFound();
        if (string.IsNullOrWhiteSpace(quoteId))
        {
            var quote = await jobs.QuoteVoiceCloneAsync(AudioEditorEndpoints.Owner(scope), scope, slug, provider ?? "", ct);
            return quote.Value is { } q ? Ok(q) : JobError(quote.ErrorCode, quote.Error);
        }
        // Запуск — только по котировке пересоздания ЭТОГО голоса
        if (jobs.FindQuote(AudioEditorEndpoints.Owner(scope), scope.Key, quoteId.Trim()) is not { } found || found.RecreateVoice != slug)
            return Error(StatusCodes.Status404NotFound, AudioEditErrorCodes.QuoteNotFound, AudioEditJobService.QuoteExpiredText);
        var started = await jobs.StartAsync(AudioEditorEndpoints.Owner(scope), scope,
            new AudioJobInput(quoteId.Trim(), Voice: AudioVoiceRefs.Prefix + slug), ct);
        return started.Value is { } created
            ? StatusCode(StatusCodes.Status202Accepted, created)
            : JobError(started.ErrorCode, started.Error);
    }

    // Образец или файл модели для плеера и скачивания: только перечисленный в манифесте
    protected IActionResult VoiceFileIn(AudioEditScope scope, string slug, string file)
    {
        if (voices.OpenFile(scope, slug, file) is not { } full)
            return Error(StatusCodes.Status404NotFound, AudioEditErrorCodes.FileNotFound, "Файл голоса не найден");
        return PhysicalFile(full, AudioVersionFiles.ContentTypeOf(full), enableRangeProcessing: true);
    }

    private ObjectResult JobError(string? code, string? error) => Error(code switch
    {
        AudioEditErrorCodes.QuoteNotFound or AudioEditErrorCodes.VoiceNotFound or AudioEditErrorCodes.FileNotFound
            => StatusCodes.Status404NotFound,
        AudioEditErrorCodes.ProviderUnavailable => StatusCodes.Status409Conflict,
        AudioEditErrorCodes.TooManyJobs or AudioEditErrorCodes.HeavyBusy => StatusCodes.Status429TooManyRequests,
        AudioEditErrorCodes.Unavailable => StatusCodes.Status503ServiceUnavailable,
        _ => StatusCodes.Status400BadRequest,
    }, code ?? AudioEditErrorCodes.InvalidRequest, error ?? "Запрос не выполнен");

    // Записи из загрузки и из проекта; байты читаются до библиотеки, формат она проверит сама.
    // Путь проекта — только ResolveInsideAs (чужое private участника — тот же отказ, что «вне проекта»:
    // наличие не раскрываем, F2-M1). У личной области файлов проекта нет (входы из файлов — отдельный шаг)
    private async Task<(List<VoiceSampleUpload>? Samples, IActionResult? Bad)> SamplesAsync(
        AudioEditScope scope, VoiceSamplesForm form, CancellationToken ct)
    {
        if (voices.RootOf(scope, out _) is not { } root) return (null, Refused(scope));
        if (scope.IsPersonal && form.ProjectFiles is { Count: > 0 })
            return (null, Error(StatusCodes.Status400BadRequest, AudioEditErrorCodes.InvalidRequest,
                "Файлов проекта у личного чата нет: загрузите записи"));
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
            if (string.IsNullOrWhiteSpace(rel) || ProjectLinkGuard.ResolveInsideAs(root, rel.Trim(), UserId) is not { } full
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
        foreach (var rel in change.Written) Notify(scope, rel, FileMutationKind.Write);
        foreach (var rel in change.Deleted) Notify(scope, rel, FileMutationKind.Delete);
        var dto = VoiceDto.From(change.Manifest, voices.Now);
        return created ? StatusCode(StatusCodes.Status201Created, dto) : Ok(dto);
    }

    // «Личное» ни синк знаний, ни ватчеры не отслеживают (ADR-040 §2.3): уведомлять некого
    private void Notify(AudioEditScope scope, string rel, FileMutationKind kind)
    {
        if (!scope.IsPersonal) files?.NotifyMutated(scope.Project!.RootPath, rel, kind);
    }

    protected IActionResult Refused(AudioEditScope scope)
    {
        voices.RootOf(scope, out var refusal);
        return Result(scope, refusal!);
    }

    protected ObjectResult Error(int status, string code, string error) => StatusCode(status, new { error, code });

    protected IActionResult VoiceNotFound() =>
        Error(StatusCodes.Status404NotFound, AudioEditErrorCodes.VoiceNotFound, "Голос не найден");

    private IActionResult TooLarge() =>
        Error(StatusCodes.Status400BadRequest, AudioEditErrorCodes.InvalidRequest, $"Запись больше {VoiceStore.MaxSampleMb} МБ");
}
