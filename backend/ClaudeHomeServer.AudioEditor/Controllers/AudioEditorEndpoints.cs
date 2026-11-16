using ClaudeHomeServer.Services.ChatContext;
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
    AudioConcatService concat,
    ChatContext.AudioContextLaunch? context = null,
    IVideoSceneSound? videoScenes = null) : ControllerBase
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

    // Вызывающий — только для проверки доступа в гейте; ключом хранилищ он не служит
    protected string UserId => User.FindFirstValue(SubClaim)!;

    // Ключ хранилищ (нити, префы, задачи, рабочая папка): арендатор области, а не вызывающий.
    // В общем проекте они расходятся. Гейт всегда заполняет TenantOwnerId; пусто — ошибка сборки области
    internal static string Owner(AudioEditScope scope) =>
        scope.TenantOwnerId ?? throw new InvalidOperationException("У области нет арендатора: область выдаёт только гейт");

    // ── Состояние, каталог, префы ────────────────────────────────────────────────

    protected IActionResult StateIn(AudioEditScope scope, string sessionId) =>
        Ok(new AudioStateDto(threads.Store.Get(Owner(scope), sessionId), AudioCatalogView.Build(engines, scope, true), PrefsOf(scope)));

    protected IActionResult CatalogIn(AudioEditScope scope) => Ok(AudioCatalogView.Build(engines, scope, true));

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
        if (AudioOpInputs.Validate(req.Inputs, scope) is { } badInputs)
            return Error(StatusCodes.Status400BadRequest, AudioEditErrorCodes.InvalidRequest, badInputs);
        await prefs.SaveAsync(Owner(scope), scope, mode, req);
        return Ok(PrefsOf(scope));
    }

    private AudioPrefsDto PrefsOf(AudioEditScope scope) =>
        new(prefs.Get(Owner(scope), scope, AudioModes.Voice), prefs.Get(Owner(scope), scope, AudioModes.Music),
            prefs.Get(Owner(scope), scope, AudioModes.Process));

    // ── Котировка, запуск, отмена ────────────────────────────────────────────────

    protected async Task<IActionResult> QuoteIn(AudioEditScope scope, AudioQuoteRequest? req, CancellationToken ct)
    {
        if (req is null || string.IsNullOrWhiteSpace(req.Mode))
            return Error(StatusCodes.Status400BadRequest, AudioEditErrorCodes.InvalidRequest, "Не указан режим");
        return Map(await jobs.QuoteAsync(Owner(scope), scope, req, ct, UserId), Ok);
    }

    protected async Task<IActionResult> StartIn(AudioEditScope scope, AudioStartJobForm form, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(form.QuoteId)
            || jobs.FindQuote(Owner(scope), scope.Key, form.QuoteId.Trim()) is not { } quote)
            return Error(StatusCodes.Status404NotFound, AudioEditErrorCodes.QuoteNotFound, AudioEditJobService.QuoteExpiredText);

        // Ревизия контекста: входы берутся из стора, одноимённые поля тела игнорируются (ADR-023 §Д2.1)
        ChatContext.AudioContextInputs? fromContext = null;
        if (form.ContextRevision is { } revision)
        {
            if (context is null) return Error(StatusCodes.Status503ServiceUnavailable, AudioEditErrorCodes.Unavailable,
                ChatContext.AudioContextLaunch.UnavailableText);
            var read = context.Read(Owner(scope), scope, form.SessionId, revision, UserId);
            if (!read.Ok) return Map(read.Fail<object>(), Ok);
            fromContext = ChatContext.AudioContextLaunch.Extract(scope, read.State!, AudioEditJobService.OpName(quote.Op));
            form.ThreadId = fromContext.ThreadId;
            form.BaseVersionId = fromContext.VersionId;
            form.Voice = fromContext.VoiceRef;
            form.ReferencePath = fromContext.Reference?.ProjectFile;
            form.ClipPaths = null;
            form.Reference = null;
            form.Clips = null;
            form.VoiceModelPath = null;
            form.VoiceIndexPath = null;
        }

        // Нить своя или её нет вовсе: чужая — 404 до запуска, а не тихий запуск без нити
        AudioThread? thread = null;
        if (!string.IsNullOrWhiteSpace(form.ThreadId))
        {
            if (!threads.OwnThread(Owner(scope), scope.Key, form.SessionId, form.ThreadId?.Trim()))
                return ThreadNotFound();
            thread = threads.Store.Get(Owner(scope), form.SessionId!.Trim()).Threads.First(t => t.Id == form.ThreadId!.Trim());
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
            var path = main is null ? null : AudioVersionFiles.Resolve(workspace, Owner(scope), UserId, scope, baseVersion!, main);
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
        else if (fromContext?.Reference is { ThreadId: { } refThread } refPiece)
        {
            // Образец контекста — версия звука этого же чата: главный файл, как исходник операции
            var refVersion = threads.Store.Get(Owner(scope), form.SessionId!.Trim()).Threads.FirstOrDefault(t => t.Id == refThread) is { } rt
                ? (refPiece.VersionId is null ? rt.CurrentVersion : rt.Version(refPiece.VersionId))
                : null;
            var refMain = refVersion?.File(AudioFileRoles.Main);
            var refPath = refMain is null ? null : AudioVersionFiles.Resolve(workspace, Owner(scope), UserId, scope, refVersion!, refMain);
            if (refPath is null || !System.IO.File.Exists(refPath))
                return Error(StatusCodes.Status400BadRequest, AudioEditErrorCodes.InvalidRequest, "Образец из контекста не найден или без звука");
            reference = await ReadAsync(refPath, ct) is { } refBytes ? new AudioBytes(refBytes, AudioVersionFiles.ContentTypeOf(refPath)) : null;
            if (reference is null) return TooLarge();
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

        AudioBytes? video = null;
        AudioVideoLink? videoLink = null;
        if (quote.Op == AudioOp.VideoToAudio)
        {
            if (!string.IsNullOrWhiteSpace(form.VideoSceneId))
            {
                if (videoScenes is null)
                    return Error(StatusCodes.Status503ServiceUnavailable, AudioEditErrorCodes.Unavailable,
                        "Модуль «Видео» на этом сервере не подключён: сцену озвучить нельзя");
                if (string.IsNullOrWhiteSpace(form.SessionId))
                    return Error(StatusCodes.Status400BadRequest, AudioEditErrorCodes.InvalidRequest, "Для сцены нужен чат (sessionId)");
                var clip = await videoScenes.GetClipAsync(new VideoSceneRef(Owner(scope), scope.Key, form.SessionId.Trim(),
                    form.VideoSceneId.Trim(), string.IsNullOrWhiteSpace(form.VideoVersionId) ? null : form.VideoVersionId.Trim()), ct);
                if (clip is null)
                    return Error(StatusCodes.Status400BadRequest, AudioEditErrorCodes.InvalidRequest,
                        "Клип версии сцены не найден (сцена удалена или файл убрала чистка)");
                video = new AudioBytes(clip.Bytes, "video/mp4");
                videoLink = new AudioVideoLink(form.VideoSceneId.Trim(), clip.VersionId, form.PasteIntoScene == true);
            }
            else if (!string.IsNullOrWhiteSpace(form.VideoPath))
            {
                var (bytes, path, denied) = await ProjectFileAsync(scope, form.VideoPath, ct);
                if (denied is not null) return denied;
                video = new AudioBytes(bytes!, AudioVersionFiles.ContentTypeOf(path!));
            }
            else
                return Error(StatusCodes.Status400BadRequest, AudioEditErrorCodes.InvalidRequest,
                    "Для этой операции нужно видео: версия сцены или файл проекта");
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
            Voice: form.Voice,
            ContextRevision: form.ContextRevision,
            Video: video,
            VideoLink: videoLink);
        return Map(await jobs.StartAsync(Owner(scope), scope, input, ct, UserId), created => StatusCode(StatusCodes.Status202Accepted, created));
    }

    protected IActionResult JobIn(AudioEditScope scope, string jobId) =>
        jobs.Get(Owner(scope), scope.Key, jobId) is { } job ? Ok(job) : JobNotFound();

    protected async Task<IActionResult> CancelIn(AudioEditScope scope, string jobId, CancellationToken ct) =>
        await jobs.CancelAsync(Owner(scope), scope.Key, jobId, ct) is { } job ? Ok(job) : JobNotFound();

    // ── Нити ─────────────────────────────────────────────────────────────────────

    protected IActionResult ThreadsIn(AudioEditScope scope, string sessionId) => Ok(threads.View(Owner(scope), sessionId));

    // Взять звук в работу: ровно одно из file и draftFolder. Нить по этому файлу уже есть — фокус на
    // неё, второй не будет. Файл и папка черновика — внутри корня области: у проекта это корень проекта,
    // у личной области — «Личное» (гейт кладёт его в scope.Project через PersonalFolderResolver, как на
    // других личных путах). Без папки читать нечего: OpenIn — читающий путь, отказ до диска, папку не создаёт
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
            if (scope.Project is not { } root) return NotFoundFile(scope);
            if (ProjectLinkGuard.ResolveInsideAs(root.RootPath, req.File, UserId) is not { } full || !System.IO.File.Exists(full))
                return NotFoundFile(scope);
            file = Relative(root.RootPath, full);
        }
        else if (req.DraftFolder!.Trim().Trim('/', '\\').Length > 0)
        {
            if (scope.Project is not { } root) return NotFoundFolder(scope);
            if (ProjectLinkGuard.ResolveInside(root.RootPath, req.DraftFolder) is not { } full || !Directory.Exists(full))
                return NotFoundFolder(scope);
            folder = Relative(root.RootPath, full);
        }
        else folder = "";

        var settings = req.Mode is null ? null : prefs.ForNewThread(Owner(scope), scope, req.Mode);
        var written = threads.Tracked(Owner(scope), sessionId,
            () => threads.Store.Open(Owner(scope), sessionId, file, folder, req.Revision, settings), ContextActor.Human);
        if (written is { Status: AudioThreadWriteStatus.Ok, Existing: false, Thread: { } thread })
            await threads.AnchorAsync(sessionId, thread, ct);
        return await ResultAsync(scope, sessionId, written);
    }

    protected async Task<IActionResult> FocusIn(AudioEditScope scope, string sessionId, AudioThreadFocusRequest req) =>
        await ResultAsync(scope, sessionId,
            threads.Tracked(Owner(scope), sessionId, () => threads.Store.SetFocus(Owner(scope), sessionId,
                string.IsNullOrWhiteSpace(req.ThreadId) ? null : req.ThreadId.Trim(), req.Revision), ContextActor.Human));

    // Убрать нить, где нечего терять; у нити с версиями или идущим запуском — 400
    protected async Task<IActionResult> RemoveIn(AudioEditScope scope, string sessionId, string threadId, long revision)
    {
        var written = threads.Store.Remove(Owner(scope), sessionId, threadId, revision);
        if (written.Status == AudioThreadWriteStatus.Ok) threads.Forget(Owner(scope), sessionId, threadId);
        return await ResultAsync(scope, sessionId, written);
    }

    protected async Task<IActionResult> SettingsIn(AudioEditScope scope, string sessionId, string threadId,
        AudioThreadSettingsRequest req)
    {
        if (req.Settings is not { } settings)
            return Error(StatusCodes.Status400BadRequest, AudioEditErrorCodes.InvalidRequest, "Пустые настройки");
        if (settings.Count is { } count && (count < 1 || count > AudioModePrefs.MaxCount))
            return Error(StatusCodes.Status400BadRequest, AudioEditErrorCodes.InvalidRequest,
                $"Вариантов — от 1 до {AudioModePrefs.MaxCount}");
        if (AudioOpInputs.Validate(settings.Inputs, scope) is { } badInputs)
            return Error(StatusCodes.Status400BadRequest, AudioEditErrorCodes.InvalidRequest, badInputs);
        return await ResultAsync(scope, sessionId, threads.Store.SetSettings(Owner(scope), sessionId, threadId, settings, req.Revision));
    }

    // «Продолжить от версии»: версия становится текущей, нить — в работе. Ничего не удаляет
    protected async Task<IActionResult> CurrentIn(AudioEditScope scope, string sessionId, string threadId,
        AudioThreadCurrentRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.VersionId))
            return Error(StatusCodes.Status400BadRequest, AudioEditErrorCodes.InvalidRequest, "Не указана версия");
        return await ResultAsync(scope, sessionId,
            threads.Tracked(Owner(scope), sessionId,
                () => threads.Store.SetCurrentVersion(Owner(scope), sessionId, threadId, req.VersionId.Trim(), req.Revision, focus: true),
                ContextActor.Human));
    }

    // Файл версии для плеера: Range — перемотка без скачивания целиком. Только файлы своих нитей;
    // токен для <audio src> — через ?access_token=, как у картинок
    protected IActionResult VersionFileIn(AudioEditScope scope, string sessionId, string threadId, string versionId,
        string role, bool download)
    {
        if (FindVersion(scope, sessionId, threadId, versionId, out var thread, out var version) is { } denied) return denied;
        if (version!.File(role) is not { } file
            || AudioVersionFiles.Resolve(workspace, Owner(scope), UserId, scope, version, file) is not { } path
            || !System.IO.File.Exists(path))
            return Error(StatusCodes.Status404NotFound, AudioEditErrorCodes.FileNotFound, "Файла нет в этой версии");
        var result = PhysicalFile(path, AudioVersionFiles.ContentTypeOf(path), enableRangeProcessing: true);
        if (download) result.FileDownloadName = DownloadName(thread!, version, file, path);
        return result;
    }

    // Нить и версия своего чата; null — нашлись
    protected IActionResult? FindVersion(AudioEditScope scope, string sessionId, string threadId, string versionId,
        out AudioThread? thread, out AudioThreadVersion? version)
    {
        version = null;
        thread = threads.Store.Get(Owner(scope), sessionId).Threads.FirstOrDefault(t => t.Id == threadId);
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
            await threads.BroadcastAsync(Owner(scope), scope.Key, sessionId, written.State);
        return written.Status switch
        {
            AudioThreadWriteStatus.Ok => Ok(threads.Project(Owner(scope), sessionId, written.State)),
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
        return Map(await dsp.EditAsync(Owner(scope), UserId, scope, input, ct), Ok);
    }

    protected async Task<IActionResult> MixIn(AudioEditScope scope, string sessionId, string threadId,
        AudioMixRequest? req, CancellationToken ct)
    {
        if (req?.Stems is not { } stems)
            return Error(StatusCodes.Status400BadRequest, AudioEditErrorCodes.InvalidRequest, "Выберите хотя бы один стем");
        if (!TryFormat(req.Format, out var format)) return UnknownFormat();
        return Map(await dsp.MixAsync(Owner(scope), UserId, scope,
            new Engines.AudioMixInput(sessionId, threadId, stems, req.BaseVersionId, format, req.Revision,
                ContextRevision: req.ContextRevision), ct), Ok);
    }

    protected async Task<IActionResult> PeaksIn(AudioEditScope scope, string sessionId, string threadId, string versionId,
        string? role, int points, CancellationToken ct) =>
        Map(await dsp.PeaksAsync(Owner(scope), UserId, scope, sessionId, threadId, versionId, role, points, ct), Ok);

    protected async Task<IActionResult> ConcatIn(AudioEditScope scope, string sessionId, AudioConcatRequest? req,
        CancellationToken ct)
    {
        // С ревизией куски берёт сам сервис из контекста (роль piece по AddedAt), поле pieces игнорируется
        if (req?.Pieces is null && req?.ContextRevision is null)
            return Error(StatusCodes.Status400BadRequest, AudioEditErrorCodes.InvalidRequest, "Нужно хотя бы два куска — добавьте ещё один");
        if (req is null) return Error(StatusCodes.Status400BadRequest, AudioEditErrorCodes.InvalidRequest, "Пустой запрос склейки");
        if (!TryFormat(req.Format, out var format)) return UnknownFormat();
        if (!TryJoint(req.Joint, out var joint)) return UnknownJoint();
        var joints = new List<AudioJoint?>();
        foreach (var j in req.Joints ?? [])
        {
            if (!TryJoint(j, out var parsed)) return UnknownJoint();
            joints.Add(parsed);
        }
        var input = new AudioConcatInput(sessionId, req.Pieces ?? [], joint, req.Joints is null ? null : joints,
            req.NormalizeLoudness ?? true, req.Name, format ?? AudioFormat.Wav, req.Folder,
            ContextRevision: req.ContextRevision);
        return Map(await concat.ConcatAsync(Owner(scope), UserId, scope, input, ct), Ok);
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

    // Корень «Личного» на пишущем пути: база отдаёт область как есть (у проекта «Личного» нет), личный
    // контроллер переопределяет её ленивым гейтом. Точка вызова — запись, после всех проверок запроса:
    // отказ не оставляет папки на диске (ADR-040, решение 2)
    protected virtual Task<AudioEditScope> EnsurePersonalRootAsync(AudioEditScope scope, CancellationToken ct) =>
        Task.FromResult(scope);

    // Сохранить версию на диск: у проекта — следующая версия рядом с исходником или «Сохранить как»; у личной
    // области — только в Personal/audio/ (ADR-040 §5), корень «Личного» создаёт хук EnsurePersonalRootAsync
    // на пишущем пути, после всех проверок запроса. Только новыми
    // файлами; занятое имя «Сохранить как» — 409 name_taken с подсказкой. files — уведомление проекта о новых файлах
    protected async Task<IActionResult> SaveIn(AudioEditScope scope, string sessionId, string threadId, AudioSaveRequest req,
        IProjectFiles? files = null, CancellationToken ct = default)
    {
        var mode = string.IsNullOrWhiteSpace(req.Mode) ? AudioProjectSaver.NextVersion : req.Mode.Trim();
        if (mode is not (AudioProjectSaver.NextVersion or AudioProjectSaver.As))
            return Error(StatusCodes.Status400BadRequest, AudioEditErrorCodes.InvalidRequest, $"Неизвестный режим сохранения «{mode}»");

        string? folder = req.Folder, defaultFolder = null;
        if (scope.IsPersonal)
        {
            if (PersonalMediaFolders.Resolve(PersonalMediaFolders.Audio, req.Folder, out var personalFolder) is { } folderError)
                return Error(StatusCodes.Status400BadRequest, AudioEditErrorCodes.InvalidRequest, folderError);
            folder = string.IsNullOrWhiteSpace(req.Folder) ? null : personalFolder;
            defaultFolder = personalFolder;
        }

        var thread = threads.Store.Get(Owner(scope), sessionId).Threads.FirstOrDefault(t => t.Id == threadId);
        if (thread is null) return ThreadNotFound();
        var version = string.IsNullOrWhiteSpace(req.VersionId) ? thread.CurrentVersion : thread.Version(req.VersionId.Trim());
        if (version is null) return VersionNotFound();
        if (version.IsOrigin)
            return Error(StatusCodes.Status400BadRequest, AudioEditErrorCodes.InvalidRequest, "Исходник уже лежит в проекте");

        var items = new List<AudioProjectSaver.Item>();
        foreach (var file in version.Files)
        {
            if (AudioVersionFiles.Resolve(workspace, Owner(scope), UserId, scope, version, file) is not { } path || !System.IO.File.Exists(path))
                return Error(StatusCodes.Status404NotFound, AudioEditErrorCodes.FileNotFound,
                    "Файлы версии больше не найдены — рабочая папка очищена");
            items.Add(new AudioProjectSaver.Item(file.Role, path));
        }

        // Версия «Личного» читается из рабочей папки (Resolve выше), а запись идёт в папку «Личного»:
        // корень создаётся хуком только здесь, когда уже есть что сохранять — отказ по mode/folder/thread
        // остался позади и не оставляет папки на диске (ADR-040, решение 2)
        if (scope.IsPersonal)
            scope = await EnsurePersonalRootAsync(scope, ct);
        if (scope.IsPersonal && scope.Project is null)
            return Error(StatusCodes.Status503ServiceUnavailable, PersonalFolderResolver.UnavailableCode, PersonalFolderResolver.UnavailableText);
        var project = scope.Project!;
        if (scope.IsPersonal && PersonalMediaFolders.EnsureDirectory(project.RootPath, defaultFolder!) is null)
            return Error(StatusCodes.Status400BadRequest, AudioEditErrorCodes.InvalidRequest, "Путь вне папки проекта");

        // «Следующая версия» нити по файлу остаётся рядом с файлом; «Сохранить как» и черновик «Личного» идут в
        // выбранную папку личной области (по умолчанию audio/), иначе выбор папки у нити по файлу терялся
        var saved = AudioProjectSaver.Save(project.RootPath, thread, items, mode, folder, req.FileName,
            mode == AudioProjectSaver.As || thread.File is null ? defaultFolder : null);
        if (saved.ErrorCode == AudioEditErrorCodes.NameTaken)
            return StatusCode(StatusCodes.Status409Conflict, new { error = saved.Error, code = saved.ErrorCode, suggestion = saved.Suggestion });
        if (saved.Value is not { } result)
            return Error(saved.ErrorCode == AudioEditErrorCodes.FileNotFound ? StatusCodes.Status404NotFound : StatusCodes.Status400BadRequest,
                saved.ErrorCode ?? AudioEditErrorCodes.InvalidRequest, saved.Error ?? "Не сохранено");
        // Новые файлы проекта — обычная запись: синк знаний и ватчеры узнают о них сразу
        foreach (var rel in result.Files) files?.NotifyMutated(project.RootPath, rel, FileMutationKind.Create);
        // Нить помнит, что звук уже сохранён; версия из одних стемов звука не даёт — нить остаётся при своём
        await threads.OnSavedAsync(Owner(scope), scope.Key, sessionId, threadId, result.Path,
            result.HasAudio ? null : result.Name);
        return Ok(result);
    }

    // ── Помощники ────────────────────────────────────────────────────────────────

    // Файл проекта для входа запуска: у личной области — 400 до диска, путь — только ResolveInsideAs
    // (чужое private участника — тот же отказ, что «вне проекта»: наличие не раскрываем)
    private async Task<(byte[]? Bytes, string? Path, IActionResult? Denied)> ProjectFileAsync(
        AudioEditScope scope, string rel, CancellationToken ct)
    {
        if (scope.Project is not { } project) return (null, null, NoProjectFiles());
        if (ProjectLinkGuard.ResolveInsideAs(project.RootPath, rel, UserId) is not { } full || !System.IO.File.Exists(full))
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

    protected IActionResult JobNotFound() =>
        Error(StatusCodes.Status404NotFound, AudioEditErrorCodes.JobNotFound, "Задача не найдена");

    private IActionResult NoProjectFiles() =>
        Error(StatusCodes.Status400BadRequest, AudioEditErrorCodes.InvalidRequest,
            "У чата вне проекта нет файлов проекта: только новый звук и загрузка");

    // Отказ OpenIn называет корень своей области: у личного чата проекта нет, и «в проекте» там —
    // текст, который вводит в заблуждение (дефект e3b442e5)
    private IActionResult NotFoundFile(AudioEditScope scope) =>
        Error(StatusCodes.Status400BadRequest, AudioEditErrorCodes.InvalidRequest,
            scope.IsPersonal ? "Файл не найден в «Личном»" : "Файл не найден в проекте");

    private IActionResult NotFoundFolder(AudioEditScope scope) =>
        Error(StatusCodes.Status400BadRequest, AudioEditErrorCodes.InvalidRequest,
            scope.IsPersonal ? "Папка не найдена в «Личном»" : "Папка не найдена в проекте");

    private IActionResult TooLarge() =>
        Error(StatusCodes.Status400BadRequest, AudioEditErrorCodes.InvalidRequest,
            $"Входной файл больше {MaxInputFileBytes / 1024 / 1024} МБ");

    protected IActionResult Map<T>(AudioEditCallResult<T> result, Func<T, IActionResult> ok)
    {
        if (result.ErrorCode is null && result.Value is not null) return ok(result.Value);
        var code = result.ErrorCode ?? AudioEditErrorCodes.InvalidRequest;
        if (result.Context is { } fresh)
            return StatusCode(StatusCodes.Status409Conflict, new Protocol.ChatContextConflictDto(code, fresh));
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
