using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeHomeServer.Services.AudioEditor.Jobs;
using ClaudeHomeServer.Services.AudioEditor.Prefs;
using ClaudeHomeServer.Services.AudioEditor.Threads;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Media;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Services.AudioEditor.Controllers;

// Общее тело ручек модуля «Звук» (ADR-021 §2, как ImageEditorEndpoints у картинок): одна реализация
// проверок на проектные и личные ручки. Наследник проходит свой гейт (флаг, проект, чат — 404) и
// передаёт область; всё, что читает диск проекта, берёт scope.Project и у личной области отказывает
// до обращения к RootPath. Сохранения в проект здесь нет: оно только у проектного контроллера.
//
// Нити: чужая нить в своём чате — 404 thread_not_found; каждая мутация несёт revision, от которой
// считал фронт, устарела — 409 с актуальным состоянием; ответ мутации — полное состояние нитей.
public abstract class AudioEditorEndpoints(
    IEnumerable<IAudioEngine> engines,
    AudioEditJobService jobs,
    AudioJobThreads threads,
    AudioPrefsService prefs,
    AudioEditWorkspace workspace,
    Engines.DspAudioEngine dsp,
    AudioConcatService concat) : ControllerBase
{
    // Владелец — claim sub сервисного JWT. Константой, а не JwtRegisteredClaimNames: своих пакетов у
    // модуля нет (DynamicModulePackagesGuardTests)
    private const string SubClaim = "sub";

    // Потолок одного входного файла, который ручка запуска читает в память
    private const long MaxInputFileBytes = 200L * 1024 * 1024;

    // Потолок тела запуска: образец, до десятка записей для обучения голоса
    protected const long MaxJobBodyBytes = 500L * 1024 * 1024;

    // Операции, которым нужен исходный звук: его даёт главный файл версии-основы нити (ручки и тулсет агента)
    internal static readonly IReadOnlySet<AudioOp> NeedsSource = new HashSet<AudioOp>
    {
        AudioOp.ConvertVoice, AudioOp.Cover, AudioOp.Repaint, AudioOp.Outpaint, AudioOp.Extract, AudioOp.Lego,
        AudioOp.Complete, AudioOp.Separate, AudioOp.Denoise, AudioOp.Upsample, AudioOp.Master, AudioOp.Transcribe,
        AudioOp.ToMidi, AudioOp.Align,
    };

    protected string UserId => User.FindFirstValue(SubClaim)!;

    // ── Состояние, каталог, префы ────────────────────────────────────────────────

    protected IActionResult StateIn(AudioEditScope scope, string sessionId) =>
        Ok(new AudioStateDto(threads.Store.Get(UserId, sessionId), AudioCatalogView.Build(engines, scope), PrefsOf(scope)));

    protected IActionResult CatalogIn(AudioEditScope scope) => Ok(AudioCatalogView.Build(engines, scope));

    protected IActionResult PrefsIn(AudioEditScope scope) => Ok(PrefsOf(scope));

    protected async Task<IActionResult> PutPrefsIn(AudioEditScope scope, string mode, AudioModePrefs? req)
    {
        if (!AudioModes.IsValid(mode))
            return Error(StatusCodes.Status400BadRequest, AudioEditErrorCodes.InvalidRequest, $"Неизвестный режим: {mode}");
        if (req is null)
            return Error(StatusCodes.Status400BadRequest, AudioEditErrorCodes.InvalidRequest, "Пустые настройки");
        if (req.Count is { } count && (count < 1 || count > AudioModePrefs.MaxCount))
            return Error(StatusCodes.Status400BadRequest, AudioEditErrorCodes.InvalidRequest,
                $"Вариантов — от 1 до {AudioModePrefs.MaxCount}");
        await prefs.SaveAsync(UserId, scope, mode, req);
        return Ok(PrefsOf(scope));
    }

    private AudioPrefsDto PrefsOf(AudioEditScope scope) =>
        new(prefs.Get(UserId, scope, AudioModes.Voice), prefs.Get(UserId, scope, AudioModes.Music),
            prefs.Get(UserId, scope, AudioModes.Process));

    // ── Котировка, запуск, отмена ────────────────────────────────────────────────

    protected async Task<IActionResult> QuoteIn(AudioEditScope scope, AudioQuoteRequest? req, CancellationToken ct)
    {
        if (req is null || string.IsNullOrWhiteSpace(req.Mode))
            return Error(StatusCodes.Status400BadRequest, AudioEditErrorCodes.InvalidRequest, "Не указан режим");
        return Map(await jobs.QuoteAsync(UserId, scope, req, ct), Ok);
    }

    protected async Task<IActionResult> StartIn(AudioEditScope scope, AudioStartJobForm form, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(form.QuoteId)
            || jobs.FindQuote(UserId, scope.Key, form.QuoteId.Trim()) is not { } quote)
            return Error(StatusCodes.Status404NotFound, AudioEditErrorCodes.QuoteNotFound, AudioEditJobService.QuoteExpiredText);

        // Нить своя или её нет вовсе: чужая — 404 до запуска, а не тихий запуск без нити
        AudioThread? thread = null;
        if (!string.IsNullOrWhiteSpace(form.ThreadId))
        {
            if (!threads.OwnThread(UserId, scope.Key, form.SessionId, form.ThreadId?.Trim()))
                return ThreadNotFound();
            thread = threads.Store.Get(UserId, form.SessionId!.Trim()).Threads.First(t => t.Id == form.ThreadId!.Trim());
        }
        AudioThreadVersion? baseVersion = thread?.CurrentVersion;
        if (!string.IsNullOrWhiteSpace(form.BaseVersionId))
        {
            baseVersion = thread?.Version(form.BaseVersionId.Trim());
            if (baseVersion is null) return VersionNotFound();
        }

        JsonObject? parameters = null;
        if (!string.IsNullOrWhiteSpace(form.Params))
        {
            try { parameters = JsonNode.Parse(form.Params) as JsonObject; }
            catch (JsonException) { }
            if (parameters is null)
                return Error(StatusCodes.Status400BadRequest, AudioEditErrorCodes.InvalidRequest, "params — не JSON-объект");
        }

        AudioBytes? source = null;
        if (NeedsSource.Contains(quote.Op))
        {
            var main = baseVersion?.File(AudioFileRoles.Main);
            var path = main is null ? null : AudioVersionFiles.Resolve(workspace, UserId, scope, baseVersion!, main);
            if (path is null || !System.IO.File.Exists(path))
                return Error(StatusCodes.Status400BadRequest, AudioEditErrorCodes.InvalidRequest,
                    "Для этой операции нужен звук: возьмите в работу файл или версию со звуком");
            if (await ReadAsync(path, ct) is not { } bytes) return TooLarge();
            source = new AudioBytes(bytes, AudioVersionFiles.ContentTypeOf(path));
        }

        AudioBytes? reference = null;
        if (form.Reference is { } upload)
            reference = await ReadAsync(upload, ct) is { } bytes ? new AudioBytes(bytes, ContentTypeOf(upload)) : null;
        else if (!string.IsNullOrWhiteSpace(form.ReferencePath))
        {
            var (bytes, path, denied) = await ProjectFileAsync(scope, form.ReferencePath, ct);
            if (denied is not null) return denied;
            reference = new AudioBytes(bytes!, AudioVersionFiles.ContentTypeOf(path!));
        }
        if ((form.Reference is not null || !string.IsNullOrWhiteSpace(form.ReferencePath)) && reference is null) return TooLarge();

        var clips = new List<AudioBytes>();
        foreach (var clip in form.Clips ?? [])
        {
            if (await ReadAsync(clip, ct) is not { } bytes) return TooLarge();
            clips.Add(new AudioBytes(bytes, ContentTypeOf(clip)));
        }
        foreach (var rel in form.ClipPaths ?? [])
        {
            var (bytes, path, denied) = await ProjectFileAsync(scope, rel, ct);
            if (denied is not null) return denied;
            clips.Add(new AudioBytes(bytes!, AudioVersionFiles.ContentTypeOf(path!)));
        }

        byte[]? voiceModel = null, voiceIndex = null;
        if (!string.IsNullOrWhiteSpace(form.VoiceModelPath))
        {
            var (bytes, _, denied) = await ProjectFileAsync(scope, form.VoiceModelPath, ct);
            if (denied is not null) return denied;
            voiceModel = bytes;
        }
        if (!string.IsNullOrWhiteSpace(form.VoiceIndexPath))
        {
            var (bytes, _, denied) = await ProjectFileAsync(scope, form.VoiceIndexPath, ct);
            if (denied is not null) return denied;
            voiceIndex = bytes;
        }

        var input = new AudioJobInput(
            form.QuoteId.Trim(),
            SessionId: thread is null ? null : form.SessionId!.Trim(),
            ThreadId: thread?.Id,
            BaseVersionId: baseVersion?.Id,
            Initiator: AudioEditInitiator.Human,
            Text: form.Text,
            Prompt: form.Prompt,
            Lyrics: form.Lyrics,
            Language: form.Language,
            DurationSec: form.DurationSec,
            StartSec: form.StartSec,
            EndSec: form.EndSec,
            Params: parameters,
            Source: source,
            Reference: reference,
            Clips: clips.Count > 0 ? clips : null,
            VoiceModel: voiceModel,
            VoiceIndex: voiceIndex,
            Seed: form.Seed,
            Voice: form.Voice);
        return Map(await jobs.StartAsync(UserId, scope, input, ct), created => StatusCode(StatusCodes.Status202Accepted, created));
    }

    protected IActionResult JobIn(AudioEditScope scope, string jobId) =>
        jobs.Get(UserId, scope.Key, jobId) is { } job ? Ok(job) : JobNotFound();

    protected async Task<IActionResult> CancelIn(AudioEditScope scope, string jobId, CancellationToken ct) =>
        await jobs.CancelAsync(UserId, scope.Key, jobId, ct) is { } job ? Ok(job) : JobNotFound();

    // ── Нити ─────────────────────────────────────────────────────────────────────

    protected IActionResult ThreadsIn(string sessionId) => Ok(threads.Store.Get(UserId, sessionId));

    // Взять звук в работу: ровно одно из file и draftFolder. Нить по этому файлу уже есть — фокус на
    // неё, второй не будет. Файл и папка черновика — только у проекта
    protected async Task<IActionResult> OpenIn(AudioEditScope scope, string sessionId, AudioThreadOpenRequest req,
        CancellationToken ct)
    {
        if ((req.File is null) == (req.DraftFolder is null))
            return Error(StatusCodes.Status400BadRequest, AudioEditErrorCodes.InvalidRequest,
                "Нужно ровно одно: звуковой файл или папка черновика");
        if (req.Mode is not null && !AudioModes.IsValid(req.Mode))
            return Error(StatusCodes.Status400BadRequest, AudioEditErrorCodes.InvalidRequest, $"Неизвестный режим: {req.Mode}");

        string? file = null, folder = null;
        if (req.File is not null)
        {
            if (scope.Project is not { } project) return NoProjectFiles();
            if (ProjectLinkGuard.ResolveInside(project.RootPath, req.File) is not { } full || !System.IO.File.Exists(full))
                return Error(StatusCodes.Status400BadRequest, AudioEditErrorCodes.InvalidRequest, "Файл не найден в проекте");
            file = Relative(project.RootPath, full);
        }
        else if (req.DraftFolder!.Trim().Trim('/', '\\').Length > 0)
        {
            if (scope.Project is not { } project) return NoProjectFiles();
            if (ProjectLinkGuard.ResolveInside(project.RootPath, req.DraftFolder) is not { } full || !Directory.Exists(full))
                return Error(StatusCodes.Status400BadRequest, AudioEditErrorCodes.InvalidRequest, "Папка не найдена в проекте");
            folder = Relative(project.RootPath, full);
        }
        else folder = "";

        var settings = req.Mode is null ? null : prefs.ForNewThread(UserId, scope, req.Mode);
        var written = threads.Store.Open(UserId, sessionId, file, folder, req.Revision, settings);
        if (written is { Status: AudioThreadWriteStatus.Ok, Existing: false, Thread: { } thread })
            await threads.AnchorAsync(sessionId, thread, ct);
        return await ResultAsync(scope, sessionId, written);
    }

    protected async Task<IActionResult> FocusIn(AudioEditScope scope, string sessionId, AudioThreadFocusRequest req) =>
        await ResultAsync(scope, sessionId,
            threads.Store.SetFocus(UserId, sessionId, string.IsNullOrWhiteSpace(req.ThreadId) ? null : req.ThreadId.Trim(), req.Revision));

    // Убрать нить, где нечего терять; у нити с версиями или идущим запуском — 400
    protected async Task<IActionResult> RemoveIn(AudioEditScope scope, string sessionId, string threadId, long revision) =>
        await ResultAsync(scope, sessionId, threads.Store.Remove(UserId, sessionId, threadId, revision));

    protected async Task<IActionResult> SettingsIn(AudioEditScope scope, string sessionId, string threadId,
        AudioThreadSettingsRequest req)
    {
        if (req.Settings is not { } settings)
            return Error(StatusCodes.Status400BadRequest, AudioEditErrorCodes.InvalidRequest, "Пустые настройки");
        if (settings.Count is { } count && (count < 1 || count > AudioModePrefs.MaxCount))
            return Error(StatusCodes.Status400BadRequest, AudioEditErrorCodes.InvalidRequest,
                $"Вариантов — от 1 до {AudioModePrefs.MaxCount}");
        return await ResultAsync(scope, sessionId, threads.Store.SetSettings(UserId, sessionId, threadId, settings, req.Revision));
    }

    // «Продолжить от версии»: версия становится текущей, нить — в работе. Ничего не удаляет
    protected async Task<IActionResult> CurrentIn(AudioEditScope scope, string sessionId, string threadId,
        AudioThreadCurrentRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.VersionId))
            return Error(StatusCodes.Status400BadRequest, AudioEditErrorCodes.InvalidRequest, "Не указана версия");
        return await ResultAsync(scope, sessionId,
            threads.Store.SetCurrentVersion(UserId, sessionId, threadId, req.VersionId.Trim(), req.Revision, focus: true));
    }

    // Файл версии для плеера: Range — перемотка без скачивания целиком. Только файлы своих нитей;
    // токен для <audio src> — через ?access_token=, как у картинок
    protected IActionResult VersionFileIn(AudioEditScope scope, string sessionId, string threadId, string versionId,
        string role, bool download)
    {
        if (FindVersion(sessionId, threadId, versionId, out var thread, out var version) is { } denied) return denied;
        if (version!.File(role) is not { } file
            || AudioVersionFiles.Resolve(workspace, UserId, scope, version, file) is not { } path
            || !System.IO.File.Exists(path))
            return Error(StatusCodes.Status404NotFound, AudioEditErrorCodes.FileNotFound, "Файла нет в этой версии");
        var result = PhysicalFile(path, AudioVersionFiles.ContentTypeOf(path), enableRangeProcessing: true);
        if (download) result.FileDownloadName = DownloadName(thread!, version, file, path);
        return result;
    }

    // Нить и версия своего чата; null — нашлись
    protected IActionResult? FindVersion(string sessionId, string threadId, string versionId,
        out AudioThread? thread, out AudioThreadVersion? version)
    {
        version = null;
        thread = threads.Store.Get(UserId, sessionId).Threads.FirstOrDefault(t => t.Id == threadId);
        if (thread is null) return ThreadNotFound();
        version = thread.Version(versionId);
        return version is null ? VersionNotFound() : null;
    }

    // «intro.version3.mp3», у стема — «intro.version3.vocals.mp3»; у исходника — его имя
    private static string DownloadName(AudioThread thread, AudioThreadVersion version, AudioVersionFile file, string path)
    {
        var stem = thread.File is { } f ? Path.GetFileNameWithoutExtension(f) : "audio";
        var suffix = version.IsOrigin ? "" : $".version{version.Number}";
        var role = file.Role == AudioFileRoles.Main ? "" : "." + file.Role.Replace(AudioFileRoles.StemPrefix, "");
        return stem + suffix + role + Path.GetExtension(path);
    }

    private async Task<IActionResult> ResultAsync(AudioEditScope scope, string sessionId, AudioThreadWrite written)
    {
        if (written.Status == AudioThreadWriteStatus.Ok)
            await threads.BroadcastAsync(UserId, scope.Key, sessionId, written.State);
        return written.Status switch
        {
            AudioThreadWriteStatus.Ok => Ok(written.State),
            AudioThreadWriteStatus.Conflict => StatusCode(StatusCodes.Status409Conflict, new
            {
                error = "Звуки чата уже поменялись — перечитайте их",
                code = AudioEditErrorCodes.RevisionConflict,
                state = written.State,
            }),
            AudioThreadWriteStatus.VersionNotFound => VersionNotFound(),
            AudioThreadWriteStatus.Invalid => Error(StatusCodes.Status400BadRequest, AudioEditErrorCodes.InvalidRequest,
                "Действие не подходит этому звуку: у него уже есть версии, идёт генерация или настройки неверны"),
            _ => ThreadNotFound(),
        };
    }

    // ── Без ИИ: правка, сведение, пики, склейка ──────────────────────────────────
    // Ждут итог прямо в запросе: ffmpeg на хосте, без очереди и денег. Нет шва — 503 dsp_unavailable

    protected async Task<IActionResult> EditIn(AudioEditScope scope, string sessionId, string threadId,
        AudioDspEditRequest? req, CancellationToken ct)
    {
        if (req is null || !Enum.TryParse<Engines.AudioDspEditOp>(req.Op, ignoreCase: true, out var op) || !Enum.IsDefined(op))
            return Error(StatusCodes.Status400BadRequest, AudioEditErrorCodes.InvalidRequest,
                "Операция правки — trim, gainFade, normalize или convert");
        if (!TryFormat(req.Format, out var format)) return UnknownFormat();
        var input = new Engines.AudioDspEditInput(sessionId, threadId, op, req.BaseVersionId, req.StartSec, req.EndSec,
            req.FadeInSec ?? 0, req.FadeOutSec ?? 0, req.GainDb ?? 0, req.TargetLufs, format, req.SampleRate, req.Channels,
            req.Revision);
        return Map(await dsp.EditAsync(UserId, scope, input, ct), Ok);
    }

    protected async Task<IActionResult> MixIn(AudioEditScope scope, string sessionId, string threadId,
        AudioMixRequest? req, CancellationToken ct)
    {
        if (req?.Stems is not { } stems)
            return Error(StatusCodes.Status400BadRequest, AudioEditErrorCodes.InvalidRequest, "Выберите хотя бы один стем");
        if (!TryFormat(req.Format, out var format)) return UnknownFormat();
        return Map(await dsp.MixAsync(UserId, scope,
            new Engines.AudioMixInput(sessionId, threadId, stems, req.BaseVersionId, format, req.Revision), ct), Ok);
    }

    protected async Task<IActionResult> PeaksIn(AudioEditScope scope, string sessionId, string threadId, string versionId,
        string? role, int points, CancellationToken ct) =>
        Map(await dsp.PeaksAsync(UserId, scope, sessionId, threadId, versionId, role, points, ct), Ok);

    protected async Task<IActionResult> ConcatIn(AudioEditScope scope, string sessionId, AudioConcatRequest? req,
        CancellationToken ct)
    {
        if (req?.Pieces is not { } pieces)
            return Error(StatusCodes.Status400BadRequest, AudioEditErrorCodes.InvalidRequest, "Нужно хотя бы два куска — добавьте ещё один");
        if (!TryFormat(req.Format, out var format)) return UnknownFormat();
        if (!TryJoint(req.Joint, out var joint)) return UnknownJoint();
        var joints = new List<AudioJoint?>();
        foreach (var j in req.Joints ?? [])
        {
            if (!TryJoint(j, out var parsed)) return UnknownJoint();
            joints.Add(parsed);
        }
        var input = new AudioConcatInput(sessionId, pieces, joint, req.Joints is null ? null : joints,
            req.NormalizeLoudness ?? true, req.Name, format ?? AudioFormat.Wav, req.Folder);
        return Map(await concat.ConcatAsync(UserId, scope, input, ct), Ok);
    }

    // null — формат не задан; незнакомое имя — false
    private static bool TryFormat(string? value, out AudioFormat? format)
    {
        format = null;
        if (string.IsNullOrWhiteSpace(value)) return true;
        if (!Enum.TryParse<AudioFormat>(value.Trim(), ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed)) return false;
        format = parsed;
        return true;
    }

    private static bool TryJoint(AudioJointRequest? value, out AudioJoint? joint)
    {
        joint = null;
        if (value is null) return true;
        if (!Enum.TryParse<AudioJointKind>(value.Kind?.Trim(), ignoreCase: true, out var kind) || !Enum.IsDefined(kind)) return false;
        joint = new AudioJoint(kind, value.Seconds);
        return true;
    }

    private IActionResult UnknownFormat() =>
        Error(StatusCodes.Status400BadRequest, AudioEditErrorCodes.InvalidRequest, "Формат — wav, mp3, flac или ogg");

    private IActionResult UnknownJoint() =>
        Error(StatusCodes.Status400BadRequest, AudioEditErrorCodes.InvalidRequest, "Стык — butt, pause или crossfade");

    // ── Помощники ────────────────────────────────────────────────────────────────

    // Файл проекта для входа запуска: у личной области — 400 до диска, путь — только ResolveInside
    private async Task<(byte[]? Bytes, string? Path, IActionResult? Denied)> ProjectFileAsync(
        AudioEditScope scope, string rel, CancellationToken ct)
    {
        if (scope.Project is not { } project) return (null, null, NoProjectFiles());
        if (ProjectLinkGuard.ResolveInside(project.RootPath, rel) is not { } full || !System.IO.File.Exists(full))
            return (null, null, Error(StatusCodes.Status400BadRequest, AudioEditErrorCodes.InvalidRequest,
                "Файл не найден в проекте или путь идёт через символическую ссылку"));
        return await ReadAsync(full, ct) is { } bytes ? (bytes, full, null) : (null, null, TooLarge());
    }

    private static async Task<byte[]?> ReadAsync(string path, CancellationToken ct) =>
        new FileInfo(path).Length > MaxInputFileBytes ? null : await System.IO.File.ReadAllBytesAsync(path, ct);

    private static async Task<byte[]?> ReadAsync(IFormFile file, CancellationToken ct)
    {
        if (file.Length > MaxInputFileBytes) return null;
        using var ms = new MemoryStream();
        await file.CopyToAsync(ms, ct);
        return ms.ToArray();
    }

    private static string ContentTypeOf(IFormFile file) =>
        string.IsNullOrWhiteSpace(file.ContentType) || file.ContentType == "application/octet-stream"
            ? AudioVersionFiles.ContentTypeOf(file.FileName)
            : file.ContentType;

    protected static string Relative(string root, string full) => Path.GetRelativePath(root, full).Replace('\\', '/');

    protected ObjectResult Error(int status, string code, string error) => StatusCode(status, new { error, code });

    protected IActionResult ThreadNotFound() =>
        Error(StatusCodes.Status404NotFound, AudioEditErrorCodes.ThreadNotFound, "Звук не найден в этом чате");

    protected IActionResult VersionNotFound() =>
        Error(StatusCodes.Status404NotFound, AudioEditErrorCodes.VersionNotFound, "Версии нет в этом звуке");

    private IActionResult JobNotFound() =>
        Error(StatusCodes.Status404NotFound, AudioEditErrorCodes.JobNotFound, "Задача не найдена");

    private IActionResult NoProjectFiles() =>
        Error(StatusCodes.Status400BadRequest, AudioEditErrorCodes.InvalidRequest,
            "У чата вне проекта нет файлов проекта: только новый звук и загрузка");

    private IActionResult TooLarge() =>
        Error(StatusCodes.Status400BadRequest, AudioEditErrorCodes.InvalidRequest,
            $"Входной файл больше {MaxInputFileBytes / 1024 / 1024} МБ");

    protected IActionResult Map<T>(AudioEditCallResult<T> result, Func<T, IActionResult> ok)
    {
        if (result.ErrorCode is null && result.Value is not null) return ok(result.Value);
        var code = result.ErrorCode ?? AudioEditErrorCodes.InvalidRequest;
        var status = code switch
        {
            AudioEditErrorCodes.ProviderUnavailable or AudioEditErrorCodes.NameTaken
                or AudioEditErrorCodes.RevisionConflict or AudioEditErrorCodes.VoiceCloneStale
                or AudioEditErrorCodes.VoiceCloneMissing => StatusCodes.Status409Conflict,
            AudioEditErrorCodes.VoiceNotFound => StatusCodes.Status404NotFound,
            AudioEditErrorCodes.QuoteNotFound or AudioEditErrorCodes.JobNotFound or AudioEditErrorCodes.ThreadNotFound
                or AudioEditErrorCodes.VersionNotFound or AudioEditErrorCodes.FileNotFound => StatusCodes.Status404NotFound,
            AudioEditErrorCodes.TooManyJobs or AudioEditErrorCodes.HeavyBusy => StatusCodes.Status429TooManyRequests,
            AudioEditErrorCodes.Unavailable or AudioEditErrorCodes.DspUnavailable => StatusCodes.Status503ServiceUnavailable,
            _ => StatusCodes.Status400BadRequest,
        };
        // Протухший клон: в отказе — котировка пересоздания, кнопка «Пересоздать» показывает её цену
        if (result.Recreate is { } recreate)
            return StatusCode(status, new { error = result.Error ?? "Запрос не выполнен", code, recreate });
        return Error(status, code, result.Error ?? "Запрос не выполнен");
    }
}
