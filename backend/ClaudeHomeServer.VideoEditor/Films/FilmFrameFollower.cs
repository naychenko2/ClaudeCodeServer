using ClaudeHomeServer.Services.Media;
using ClaudeHomeServer.Services.VideoEditor.Contracts;
using ClaudeHomeServer.Services.VideoEditor.Jobs;
using ClaudeHomeServer.Services.VideoEditor.Scenes;

namespace ClaudeHomeServer.Services.VideoEditor.Films;

// Кадр «следует» за нитью «Картинок» (ADR-022 §3): новая версия нити с follow — кадр сцены переходит на неё. Если
// клип сцены уже снят, сцена получает «Кадр изменён — переснять» (признак вычисляется из снимка входов версии),
// а в ленте появляется тихая строка. Узнаёт о версии ТОЛЬКО событием IMediaEvents: нитей картинок модуль не
// читает. Кадр без follow и кадр-файл не трогаются.
public sealed class FilmFrameFollower(VideoJobThreads threads, ILogger<FilmFrameFollower> log)
{
    public async Task OnImageVersionAsync(ImageVersionAdded evt)
    {
        foreach (var (owner, sessionId) in threads.Store.Chats())
        {
            if (owner != evt.OwnerId) continue;
            foreach (var scene in threads.Store.Get(owner, sessionId).Scenes)
                await FollowAsync(owner, sessionId, scene, evt);
        }
    }

    private async Task FollowAsync(string owner, string sessionId, VideoSceneDto scene, ImageVersionAdded evt)
    {
        var settings = scene.Settings;
        var a = Follows(settings.FrameA, evt);
        var b = Follows(settings.FrameB, evt);
        if (!a && !b) return;

        var moved = settings with
        {
            FrameA = a ? FrameRef.Image(evt.ThreadId, evt.VersionId) : settings.FrameA,
            FrameB = b ? FrameRef.Image(evt.ThreadId, evt.VersionId) : settings.FrameB,
        };
        // Клип уже снят — это «переснять»: строка для хода и тихая строка в ленте; иначе кадр просто переехал
        var shot = scene.CurrentVersionId is not null;
        var which = a && b ? "A и B" : a ? "A" : "B";
        var text = $"Кадр {which} сцены «{scene.Name}» изменён в «Картинках» — переснять";
        var written = threads.Store.SetSettings(owner, sessionId, scene.SceneId, moved, null,
            shot ? new VideoThreadEvent(threads.Store.Now(), VideoThreadEventKinds.FrameChanged, text, scene.SceneId) : null);
        if (written.Status != VideoThreadWriteStatus.Ok) return;
        try
        {
            if (shot)
                await threads.NoteAsync(sessionId, $"Кадр изменён — переснять: {scene.Name}",
                    new { sceneId = scene.SceneId, frame = which, threadId = evt.ThreadId, versionId = evt.VersionId });
            await threads.BroadcastAsync(owner, threads.ScopeKeyOf(sessionId), sessionId, written.State);
        }
        catch (Exception ex)
        {
            // Кадр уже переехал — сбой следа его не отменяет
            log.LogWarning(ex, "Видео: след смены кадра сцены {SceneId} не записан", scene.SceneId);
        }
    }

    // Кадр следует, если он из «Картинок», с follow и с этой нитью; та же версия — ничего менять
    private static bool Follows(FrameRef? frame, ImageVersionAdded evt) =>
        frame is { Kind: FrameRef.KindImage, Follow: true } f && f.ThreadId == evt.ThreadId && f.VersionId != evt.VersionId;
}
