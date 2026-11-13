using ClaudeHomeServer.Services.AudioEditor.Catalog;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.AudioEditor.Prefs;
using ClaudeHomeServer.Services.AudioEditor.Threads;
using ClaudeHomeServer.Services.ChatContext;
using ClaudeHomeServer.Services.Media;

namespace ClaudeHomeServer.Services.AudioEditor.Jobs;

/// <summary>
/// «Озвучить сцену» для агента (шов IVideoSceneVoice, инструмент video_voice): тот же путь, что у кнопки человека
/// (sceneSound.ts) — черновик нити звука → котировка local videoToAudio → задача с версией сцены и вклейкой. Новую
/// версию сцены со звуком добавляет сама задача через IVideoSceneSound. Модель не задана — первая локальная модель
/// каталога с videoToAudio («Авто»: Foley XL).
/// </summary>
public sealed class VideoSceneVoice(
    IEnumerable<IAudioEngine> engines,
    AudioEditJobService jobs,
    AudioJobThreads threads,
    AudioPrefsService prefs,
    IProjectManager projects,
    IVideoSceneSound scenes) : IVideoSceneVoice
{
    private const string LocalKey = "local";
    private const string MusicMode = AudioModes.Music;

    public async Task<VideoVoiceStart> StartAsync(VideoVoiceRequest r, CancellationToken ct)
    {
        if (!TryScope(r.OwnerId, r.ProjectId, out var scope))
            return VideoVoiceStart.Fail(AudioEditErrorCodes.InvalidRequest, "Область чата не найдена");
        if (!threads.OwnChat(r.OwnerId, scope.Key, r.SessionId))
            return VideoVoiceStart.Fail(AudioEditErrorCodes.InvalidRequest, "Чат не найден");

        var local = AudioCatalog.Registered(engines)
            .FirstOrDefault(e => string.Equals(e.Key, LocalKey, StringComparison.OrdinalIgnoreCase));
        var candidates = local?.Models.Where(m => m.Caps.Ops.Contains(AudioOp.VideoToAudio)).ToList() ?? [];
        if (candidates.Count == 0)
            return VideoVoiceStart.Fail(AudioEditErrorCodes.ProviderUnavailable, "Локальные модели звука по видео на этом сервере не заведены");
        AudioModelInfo? model = candidates[0];
        if (r.Model is { Length: > 0 } wanted && !string.Equals(wanted.Trim(), "auto", StringComparison.OrdinalIgnoreCase))
        {
            model = Match(candidates, wanted.Trim());
            if (model is null)
                return VideoVoiceStart.Fail(AudioEditErrorCodes.InvalidRequest,
                    $"Нет модели «{wanted}» для озвучки сцены. Доступны: " + string.Join(", ", candidates.Select(m => m.Id)));
        }

        var clip = await scenes.GetClipAsync(
            new VideoSceneRef(r.OwnerId, scope.Key, r.SessionId, r.SceneId, r.VersionId), ct);
        if (clip is null)
            return VideoVoiceStart.Fail(AudioEditErrorCodes.InvalidRequest,
                "Клип версии сцены не найден (сцена удалена или файл убрала чистка)");
        if (model.Caps.InputMaxSec is { } max && clip.Seconds > max + 0.25)
            return VideoVoiceStart.Fail(AudioEditErrorCodes.InvalidRequest,
                $"Сцена {clip.Seconds:0.0} с, а модель {model.Id} озвучивает клипы до {max} с");

        var opened = threads.Tracked(r.OwnerId, r.SessionId,
            () => threads.Store.Open(r.OwnerId, r.SessionId, null, "", null, prefs.ForNewThread(r.OwnerId, scope, MusicMode)),
            ContextActor.Agent);
        if (opened is not { Status: AudioThreadWriteStatus.Ok, Thread: { } thread })
            return VideoVoiceStart.Fail(AudioEditErrorCodes.InvalidRequest, "Звуки чата как раз меняются — повтори позже");
        await threads.AnchorAsync(r.SessionId, thread, ct);

        var seconds = (int)Math.Ceiling(clip.Seconds);
        var prompt = string.IsNullOrWhiteSpace(r.Text) ? null : r.Text.Trim();
        var quote = await jobs.QuoteAsync(r.OwnerId, scope,
            new AudioQuoteRequest(MusicMode, AudioEditJobService.OpName(AudioOp.VideoToAudio), LocalKey, model.Id, 1,
                SessionId: r.SessionId, ThreadId: thread.Id, DurationSec: seconds, Prompt: prompt), ct, r.ActorId);
        if (quote.Value is not { } q) return VideoVoiceStart.Fail(quote.ErrorCode ?? AudioEditErrorCodes.InvalidRequest, quote.Error ?? "Котировка не выписана");

        var started = await jobs.StartAsync(r.OwnerId, scope,
            new AudioJobInput(q.QuoteId, SessionId: r.SessionId, ThreadId: thread.Id, Initiator: AudioEditInitiator.Agent,
                Prompt: prompt, DurationSec: seconds, Video: new AudioBytes(clip.Bytes, "video/mp4"),
                VideoLink: new AudioVideoLink(r.SceneId, clip.VersionId, true)), ct, r.ActorId);
        if (started.Value is not { } created)
            return VideoVoiceStart.Fail(started.ErrorCode ?? AudioEditErrorCodes.InvalidRequest, started.Error ?? "Запуск не удался");
        return new VideoVoiceStart(new VideoVoiceStarted(created.JobId, thread.Id, q.Provider, q.Model, q.License, q.Price.Eta), null, null);
    }

    public VideoVoiceJob? Get(string ownerId, string scopeKey, string jobId) =>
        jobs.Get(ownerId, scopeKey, jobId) is { } job ? ToJob(job) : null;

    public async Task<VideoVoiceJob?> CancelAsync(string ownerId, string scopeKey, string jobId, CancellationToken ct) =>
        await jobs.CancelAsync(ownerId, scopeKey, jobId, ct) is { } job ? ToJob(job) : null;

    private static VideoVoiceJob ToJob(AudioJobDto j) => new(j.JobId, j.ScopeKey, j.ChatSessionId,
        j.Status.ToString().ToLowerInvariant(), j.Provider, j.Model,
        j.Status is AudioEditJobStatus.Completed or AudioEditJobStatus.Failed or AudioEditJobStatus.Cancelled,
        j.Variants.Count, j.QueuePosition, j.EtaSeconds, j.Error);

    // Точный id, затем часть id без регистра: «foley-xl», «foley-xxl», «mmaudio»; неоднозначное — отказ
    private static AudioModelInfo? Match(IReadOnlyList<AudioModelInfo> models, string wanted)
    {
        var exact = models.FirstOrDefault(m => string.Equals(m.Id, wanted, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact;
        var part = models.Where(m => m.Id.Contains(wanted, StringComparison.OrdinalIgnoreCase)).ToList();
        return part.Count == 1 ? part[0] : null;
    }

    private bool TryScope(string ownerId, string? projectId, out AudioEditScope scope)
    {
        scope = null!;
        if (projectId is null) { scope = new AudioEditScope(AudioEditScope.Personal, null); return true; }
        if (projects.GetById(projectId) is not { } project || project.OwnerId != ownerId) return false;
        scope = AudioEditScope.Of(project);
        return true;
    }
}
