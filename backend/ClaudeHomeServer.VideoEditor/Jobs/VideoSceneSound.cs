using ClaudeHomeServer.Services.Media;
using ClaudeHomeServer.Services.VideoEditor.Contracts;
using ClaudeHomeServer.Services.VideoEditor.Scenes;

namespace ClaudeHomeServer.Services.VideoEditor.Jobs;

// Реализация шва IVideoSceneSound (Ш4, «Озвучить сцену»): «Звук» читает клип версии сцены и возвращает дорожку,
// а здесь она вклеивается в копию клипа и встаёт новой версией той же сцены. Версия заводится как обычный
// запуск (AddLaunch + FinishLaunch): клип лежит в рабочей папке под своим jobId, чистка и «сохранить в проект»
// работают с ней как с любой другой версией. Основа не меняется — прежняя версия остаётся рядом.
public sealed class VideoSceneSound(
    VideoThreadStore store,
    VideoEditWorkspace workspace,
    VideoJobThreads threads,
    ILogger<VideoSceneSound> log,
    IVideoDsp? dsp = null) : IVideoSceneSound
{
    // Потолок клипа, который читаем в память: клипы сцен — десятки мегабайт, сотни не бывает
    private const long MaxClipBytes = 300L * 1024 * 1024;
    public const string Provider = "sound";

    public Task<VideoSceneClip?> GetClipAsync(VideoSceneRef scene, CancellationToken ct)
    {
        if (!threads.OwnScene(scene.OwnerId, scene.ScopeKey, scene.SessionId, scene.SceneId)) return Task.FromResult<VideoSceneClip?>(null);
        var found = Find(scene);
        if (found is not { } f || workspace.FindClip(scene.OwnerId, f.Version.JobId, f.Version.Variant) is not { } path
            || new FileInfo(path).Length > MaxClipBytes)
            return Task.FromResult<VideoSceneClip?>(null);
        return Task.FromResult<VideoSceneClip?>(new VideoSceneClip(File.ReadAllBytes(path), f.Version.VersionId, f.Version.DurationSec));
    }

    public async Task<VideoSoundVersion> AddSoundVersionAsync(VideoSceneRef scene, byte[] audio, string audioExtension, string model,
        CancellationToken ct)
    {
        if (dsp is not { Available: true }) return VideoSoundVersion.Fail("ffmpeg на сервере недоступен: вклеить звук нечем.");
        if (!threads.OwnScene(scene.OwnerId, scene.ScopeKey, scene.SessionId, scene.SceneId))
            return VideoSoundVersion.Fail("Сцены уже нет в этом чате.");
        if (Find(scene) is not { } found || workspace.FindClip(scene.OwnerId, found.Version.JobId, found.Version.Variant) is not { } source)
            return VideoSoundVersion.Fail("Клип версии недоступен (возможно, его убрала чистка).");

        var jobId = "vs_" + Guid.NewGuid().ToString("N");
        var tmp = Path.Combine(Path.GetTempPath(), "ccs-v2a-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            var ext = audioExtension is { Length: > 1 and <= 10 } && audioExtension[0] == '.' && audioExtension[1..].All(char.IsAsciiLetterOrDigit)
                ? audioExtension.ToLowerInvariant() : ".wav";
            var audioPath = Path.Combine(tmp, "sound" + ext);
            var outPath = Path.Combine(tmp, "out.mp4");
            await File.WriteAllBytesAsync(audioPath, audio, ct);
            var muxed = await dsp.MuxAudioAsync(source, audioPath, outPath, ct);
            if (!muxed.Ok) return VideoSoundVersion.Fail(muxed.Error ?? "Не удалось вклеить звук.");

            var bytes = await File.ReadAllBytesAsync(outPath, ct);
            workspace.SaveClip(scene.OwnerId, jobId, 1, bytes, ".mp4");

            var baseVersion = found.Version;
            var launch = new VideoLaunchDto(jobId, store.Now(), VideoLaunchStatus.Running, false, VideoInitiators.Human, Provider,
                model, 1, null, baseVersion.License, null);
            var added = store.AddLaunch(scene.OwnerId, scene.SessionId, scene.SceneId, launch);
            if (added.Status != VideoThreadWriteStatus.Ok) return VideoSoundVersion.Fail("Сцена изменилась: версия со звуком не добавлена.");
            var written = store.FinishLaunch(scene.OwnerId, scene.SessionId, scene.SceneId, jobId, VideoLaunchStatus.Done,
                [new VideoVariantResult(1, baseVersion.DurationSec, bytes.LongLength, true, null, baseVersion.Width, baseVersion.Height)],
                baseVersion.Inputs, null,
                new VideoThreadEvent(store.Now(), VideoThreadEventKinds.Versions,
                    $"Озвучена сцена: новая версия со звуком ({model})", scene.SceneId, jobId));
            if (written.Status != VideoThreadWriteStatus.Ok || written.NewVersions.Count == 0)
                return VideoSoundVersion.Fail("Версия со звуком не записалась в сцену.");
            await threads.BroadcastAsync(scene.OwnerId, scene.ScopeKey, scene.SessionId, written.State);
            return new VideoSoundVersion(written.NewVersions[0].VersionId, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogWarning(ex, "Видео: версия со звуком для сцены {SceneId} не записана", scene.SceneId);
            return VideoSoundVersion.Fail("Не удалось записать версию со звуком.");
        }
        finally
        {
            try { Directory.Delete(tmp, true); }
            catch (IOException) { }
        }
    }

    private (VideoSceneDto Scene, VideoClipVersionDto Version)? Find(VideoSceneRef scene)
    {
        var found = store.Get(scene.OwnerId, scene.SessionId).Scenes.FirstOrDefault(s => s.SceneId == scene.SceneId);
        if (found is null) return null;
        var id = scene.VersionId ?? found.CurrentVersionId;
        return found.Versions.FirstOrDefault(v => v.VersionId == id) is { } version ? (found, version) : null;
    }
}
