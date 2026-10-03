using ClaudeHomeServer.Services.Media;
using ClaudeHomeServer.Services.VideoEditor.Contracts;
using ClaudeHomeServer.Services.VideoEditor.Jobs;
using ClaudeHomeServer.Services.VideoEditor.Scenes;

namespace ClaudeHomeServer.Services.VideoEditor.Films;

// Кадр «следует» за нитью «Картинок» (ADR-022 §3): новая версия нити с follow — кадр сцены переходит на неё. Если
// клип сцены уже снят, сцена получает «Кадр изменён — переснять» (признак вычисляется из снимка входов версии),
// а в ленте появляется тихая строка. Узнаёт о версии ТОЛЬКО событием IMediaEvents: нитей картинок модуль не
// читает. Кадр без follow и кадр-файл не трогаются.
//
// Тихая строка «Кадр изменён — переснять» пишется ОДИН раз на смену: если кадр сцены уже «переснять» (вторая версия
// той же правки, повтор события), строки не будет — «переснять» уже сказано. События обрабатываются по одному:
// без замка два одновременных события оба увидели бы кадр ещё не устаревшим и написали бы по строке (B15).
public sealed class FilmFrameFollower(VideoJobThreads threads, ILogger<FilmFrameFollower> log)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task OnImageVersionAsync(ImageVersionAdded evt)
    {
        await _gate.WaitAsync();
        try
        {
            foreach (var (owner, sessionId) in threads.Store.Chats())
            {
                if (owner != evt.OwnerId) continue;
                foreach (var scene in threads.Store.Get(owner, sessionId).Scenes)
                    await FollowAsync(owner, sessionId, scene, evt);
            }
        }
        finally { _gate.Release(); }
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
        // Кадр уже «переснять» до этой версии — строку в ленту не повторяем (журнал для хода и событие остаются)
        var stale = shot ? VideoStale.Compute(scene) : null;
        var alreadyStale = stale is not null && (!a || stale.FrameA) && (!b || stale.FrameB);
        var which = a && b ? "A и B" : a ? "A" : "B";
        var text = $"Кадр {which} сцены «{scene.Name}» изменён в «Картинках» — переснять";
        var written = threads.Store.SetSettings(owner, sessionId, scene.SceneId, moved, null,
            shot ? new VideoThreadEvent(threads.Store.Now(), VideoThreadEventKinds.FrameChanged, text, scene.SceneId) : null);
        if (written.Status != VideoThreadWriteStatus.Ok) return;
        try
        {
            if (shot && !alreadyStale)
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
