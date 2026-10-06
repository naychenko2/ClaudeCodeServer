using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.ChatContext;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.VideoEditor.ChatContext;
using ClaudeHomeServer.Services.VideoEditor.Contracts;
using ClaudeHomeServer.Services.VideoEditor.Scenes;

namespace ClaudeHomeServer.Services.VideoEditor.Jobs;

// Следы задачи съёмки в нитях чата (ADR-022 §2, как AudioJobThreads): запуск пишется в сцену, внизу
// ленты — якорь запуска (module_record с module "videoeditor"), итог задачи — новые версии сцены; каждая
// запись нитей уходит владельцу событием video_thread_changed. Чужой чат или сцена молча отбрасываются —
// задача идёт без сцены, ответ не выдаёт существование чужого. Сбой следа задачу не роняет.
public sealed class VideoJobThreads(
    VideoThreadStore store,
    ILogger<VideoJobThreads> log,
    ISessionDirectory? directory = null,
    IChatFeed? feed = null,
    ISessionBroadcaster? broadcaster = null,
    ChatContextFocusMirror? mirror = null)
{
    public VideoThreadStore Store => store;

    // Нити для DTO (ручки, ответы мутаций, событие): составной фокус «Видео» распадается —
    // сцена и фильм берутся из основного объекта контекста чата (по одному). Читатели хода (`video_state`, умолчания путей, хвост) берут её же; сырой Focus нити — внутреннее «до/после» для Sync
    public VideoThreadsStateDto Dto(string ownerId, string sessionId, VideoThreadsState state)
    {
        if (mirror is null) return state.ToDto();
        var scene = mirror.ProjectFocus(ownerId, sessionId, VideoContextKind.SceneKind, state.Focus.SceneId,
            id => state.Scenes.Any(s => s.SceneId == id), VideoContextKind.SceneKey);
        var film = mirror.ProjectFocus(ownerId, sessionId, VideoContextKind.FilmKind, state.Focus.FilmPath,
            _ => true, VideoContextKind.FilmKey);
        return (state with { Focus = new VideoFocusDto(scene, film) }).ToDto();
    }

    public VideoThreadsStateDto View(string ownerId, string sessionId) => Dto(ownerId, sessionId, store.Get(ownerId, sessionId));

    // Запись, которая может сменить фокус: смена попадает в стор контекста. Основным становится
    // выбранное последним: фильм обрабатывается раньше сцены, поэтому при смене обоих разом выигрывает сцена
    public VideoThreadWrite Tracked(string ownerId, string sessionId, Func<VideoThreadWrite> write, ContextActor by)
    {
        // «До» — то, что человек видит (проекция из контекста), а не сырой Focus нити: он мог устареть, пока
        // выбор жил только в сторе, и тогда Sync принял бы чужую сцену за смену
        var current = store.Get(ownerId, sessionId);
        var before = Dto(ownerId, sessionId, current).Focus;
        var written = write();
        if (written.Status != VideoThreadWriteStatus.Ok || mirror is null) return written;
        var after = written.State.Focus;
        mirror.Sync(ownerId, sessionId, VideoContextKind.FilmKind, before.FilmPath, after.FilmPath, by, refKey: VideoContextKind.FilmKey);
        mirror.Sync(ownerId, sessionId, VideoContextKind.SceneKind, before.SceneId, after.SceneId, by, refKey: VideoContextKind.SceneKey);
        return written;
    }

    // Сцена исчезла — из контекста чата уходит и она
    public void Forget(string ownerId, string sessionId, string sceneId) =>
        mirror?.Forget(ownerId, sessionId, VideoContextKind.SceneKind, sceneId, VideoContextKind.SceneKey);

    // Своя сцена чата своей области; без справочника чатов (тесты без DI) — только по хранилищу владельца
    public bool OwnScene(string ownerId, string scopeKey, string? sessionId, string? sceneId)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(sceneId)) return false;
        if (directory is not null
            && (directory.GetById(sessionId) is not { } session || VideoEditScope.Of(session).Key != scopeKey))
            return false;
        return store.Get(ownerId, sessionId).Scenes.Any(s => s.SceneId == sceneId);
    }

    // Свой чат своей области; без справочника чатов — любой непустой id: нити всё равно ложатся в хранилище владельца
    public bool OwnChat(string ownerId, string scopeKey, string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return false;
        if (directory is null) return true;
        return directory.GetById(sessionId) is { } session
            && VideoEditScope.Of(session).Key == scopeKey
            && directory.ResolveOwnerId(session) == ownerId;
    }

    // Область чата (id проекта или personal) — для рассылки события, когда вызывающий знает только чат.
    // Без справочника чатов (тесты без DI) — personal: событие всё равно уходит владельцу нитей
    public string ScopeKeyOf(string sessionId) =>
        directory?.GetById(sessionId) is { } session ? VideoEditScope.Of(session).Key : VideoEditScope.Personal;

    // Якорь сцены в ленте — зовут ручки при заведении сцены
    public Task AnchorAsync(string sessionId, VideoSceneDto scene, CancellationToken ct) =>
        RecordAsync(sessionId, VideoThreadRecordTypes.Scene, $"Видео: {scene.Name}",
            new { sceneId = scene.SceneId, versionId = scene.CurrentVersionId }, ct);

    // Задача принята: запуск в сцене с лицензией модели на этот момент, настройки сцены, якорь запуска в
    // ленте и журнал для хода
    public async Task OnLaunchedAsync(string ownerId, string scopeKey, string sessionId, string sceneId, string jobId,
        VideoQuoteResponse quote, string? prompt, string initiator, VideoSceneSettingsDto settings, CancellationToken ct)
    {
        try
        {
            var agent = initiator == VideoInitiators.Agent;
            var what = string.IsNullOrWhiteSpace(prompt) ? "съёмка" : $"«{Cut(prompt.Trim())}»";
            store.SetSettings(ownerId, sessionId, sceneId, settings, null);
            var written = store.AddLaunch(ownerId, sessionId, sceneId,
                new VideoLaunchDto(jobId, store.Now(), VideoLaunchStatus.Running, false, initiator, quote.Provider,
                    quote.Model, quote.Count, prompt?.Trim(), quote.License, null),
                new VideoThreadEvent(store.Now(), VideoThreadEventKinds.Launched,
                    $"{(agent ? "Claude запустил" : "Вы запустили")}: {what} · {quote.Provider}/{quote.Model} · " +
                    $"вариантов: {quote.Count}", sceneId, jobId));
            if (written.Status != VideoThreadWriteStatus.Ok) return;

            await RecordAsync(sessionId, VideoThreadRecordTypes.LaunchVersions,
                $"{(agent ? "Claude запустил" : "Вы запустили")}: {what} · {quote.Model}",
                new
                {
                    sceneId,
                    jobId,
                    provider = quote.Provider,
                    model = quote.Model,
                    count = quote.Count,
                    durationSec = quote.DurationSec,
                    price = quote.Price,
                    license = quote.License,
                    initiator,
                }, ct);
            await BroadcastAsync(ownerId, scopeKey, sessionId, written.State);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Видео: запуск {JobId} не записан в сцену {SceneId}", jobId, sceneId);
        }
    }

    // Задача кончилась: каждый готовый вариант — новая версия сцены; статус запуска — по итогу.
    // Повтор ничего не дублирует (FinishLaunch идемпотентен)
    public async Task OnFinishedAsync(string ownerId, string scopeKey, string sessionId, string sceneId, string jobId,
        VideoEditJobStatus status, IReadOnlyList<VideoVariantResult> variants, VideoInputsSnapshotDto inputs, string? error)
    {
        try
        {
            var launchStatus = status switch
            {
                VideoEditJobStatus.Cancelled => VideoLaunchStatus.Cancelled,
                _ when variants.Count == 0 => VideoLaunchStatus.Failed,
                _ => VideoLaunchStatus.Done,
            };
            var text = launchStatus switch
            {
                VideoLaunchStatus.Cancelled => $"Запуск {jobId} отменён" + (variants.Count > 0 ? $", готово вариантов: {variants.Count}" : ""),
                VideoLaunchStatus.Failed => "Запуск не получился" + (string.IsNullOrWhiteSpace(error) ? "" : $" ({error})"),
                _ => $"Готово: новых версий {variants.Count}",
            };
            var written = store.FinishLaunch(ownerId, sessionId, sceneId, jobId, launchStatus, variants, inputs, error,
                new VideoThreadEvent(store.Now(), VideoThreadEventKinds.Versions, text, sceneId, jobId));
            if (written.Status == VideoThreadWriteStatus.Ok)
                await BroadcastAsync(ownerId, scopeKey, sessionId, written.State);
            else
                log.LogWarning("Видео: итог задачи {JobId} не лёг в сцену {SceneId}: {Status}", jobId, sceneId, written.Status);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Видео: итог задачи {JobId} не записан в сцену {SceneId}", jobId, sceneId);
        }
    }

    // Тихая строка в ленте (video_note): без карточки, модель её не видит; журнал для хода пишет вызывающий в нить
    public Task NoteAsync(string sessionId, string text, object data, CancellationToken ct = default) =>
        RecordAsync(sessionId, VideoThreadRecordTypes.Note, text, data, ct);

    // Карточка «фильм собран» — одна на человека и агента, различает только initiator в data
    public Task FilmBuiltAsync(string sessionId, string text, object data, CancellationToken ct = default) =>
        RecordAsync(sessionId, VideoThreadRecordTypes.FilmBuilt, text, data, ct);

    // Карточка «сцена сохранена в проект»
    public Task SavedAsync(string sessionId, string text, object data, CancellationToken ct = default) =>
        RecordAsync(sessionId, VideoThreadRecordTypes.Saved, text, data, ct);

    // Фильм изменён (правка, сборка, музыка): свежее состояние уходит владельцу. Не привязано к чату
    public async Task BroadcastFilmAsync(string ownerId, string scopeKey, string path, FilmStateDto state)
    {
        if (broadcaster is null) return;
        try
        {
            await broadcaster.ToOwner(ownerId, new VideoFilmChangedMessage(scopeKey, path, state));
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Видео: состояние фильма {Path} не разослано", path);
        }
    }

    public async Task BroadcastAsync(string ownerId, string scopeKey, string sessionId, VideoThreadsState state)
    {
        if (broadcaster is null) return;
        try
        {
            await broadcaster.ToOwner(ownerId,
                new VideoThreadChangedMessage(scopeKey, state.Revision, Dto(ownerId, sessionId, state)) { SessionId = sessionId });
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Видео: нити чата {SessionId} не разосланы", sessionId);
        }
    }

    private async Task RecordAsync(string sessionId, string recordType, string fallback, object data, CancellationToken ct)
    {
        if (feed is null) return;
        try
        {
            await feed.AppendRecordAsync(sessionId, new StoredModuleRecord
            {
                Module = VideoThreadRecordTypes.Module,
                RecordType = recordType,
                Data = JsonSerializer.SerializeToElement(data, VideoThreadStore.Json),
                Fallback = fallback,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            }, ct);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Видео: запись {RecordType} не легла в ленту чата {SessionId}", recordType, sessionId);
        }
    }

    private static string Cut(string text) => text.Length <= 80 ? text : text[..80] + "…";
}
