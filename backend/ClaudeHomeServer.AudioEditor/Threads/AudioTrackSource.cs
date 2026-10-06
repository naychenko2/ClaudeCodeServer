using ClaudeHomeServer.Services.AudioEditor.Controllers;
using ClaudeHomeServer.Services.AudioEditor.Jobs;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Media;

namespace ClaudeHomeServer.Services.AudioEditor.Threads;

// Реализация шва IAudioTrackSource (ADR-022 §3): «Видео» заводит черновик музыки и забирает готовый трек, не
// зная хранилища нитей звука. Здесь ни слова про видео: шов просто создаёт «Новый звук» в режиме музыки и
// отдаёт основной файл версии.
//
// Файл версии — только через AudioVersionFiles.Resolve: исходник внутри проекта, версия внутри рабочей папки
// своей задачи, символическая ссылка наружу — отказ.
public sealed class AudioTrackSource(
    AudioThreadStore store,
    AudioJobThreads threads,
    AudioEditWorkspace workspace,
    IProjectManager projects,
    ISessionDirectory directory) : IAudioTrackSource
{
    public async Task<AudioTrackDraft?> CreateDraftAsync(string ownerId, string scopeKey, string sessionId, string folder,
        CancellationToken ct)
    {
        if (directory.GetById(sessionId) is not { } session
            || directory.ResolveOwnerId(session) != ownerId
            || AudioEditScope.Of(session).Key != scopeKey)
            return null;
        // Запись сервера без сверки ревизии: черновик лишь дописывается в нити чата
        var written = store.Open(ownerId, sessionId, null, folder, null,
            new AudioThreadSettings(AudioModes.Music, null, null, null, null));
        if (written is not { Status: AudioThreadWriteStatus.Ok, Thread: { } thread }) return null;
        await threads.AnchorAsync(sessionId, thread, ct);
        await threads.BroadcastAsync(ownerId, scopeKey, sessionId, written.State);
        return new AudioTrackDraft(thread.Id);
    }

    public async Task<AudioTrackFile?> GetMainFileAsync(string ownerId, string threadId, string versionId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(ownerId) || string.IsNullOrWhiteSpace(threadId) || string.IsNullOrWhiteSpace(versionId))
            return null;
        foreach (var (owner, sessionId) in store.Chats())
        {
            if (owner != ownerId) continue;
            var thread = store.Get(owner, sessionId).Threads.FirstOrDefault(t => t.Id == threadId);
            if (thread?.Version(versionId) is not { } version) continue;
            if (version.File(AudioFileRoles.Main) is not { } main || directory.GetById(sessionId) is not { } session)
                return null;
            var scope = AudioEditScope.Of(session);
            // Исходник нити по файлу лежит в проекте: диск проекта нужен только ему
            if (!scope.IsPersonal && projects.GetById(scope.Key) is { } project) scope = AudioEditScope.Of(project);
            if (AudioVersionFiles.Resolve(workspace, owner, scope, version, main) is not { } path || !File.Exists(path))
                return null;
            return new AudioTrackFile(await File.ReadAllBytesAsync(path, ct), Path.GetExtension(path).ToLowerInvariant());
        }
        return null;
    }
}
