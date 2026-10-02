using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Media;

namespace ClaudeHomeServer.Services.ImageEditor.Threads;

// Реализация шва IImageFrameSource (ADR-022 §3): «Видео» берёт кадры сцены из нитей «Картинок», не зная
// их хранилища. Здесь ни слова про видео: шов просто отдаёт картинку версии и заводит черновик.
//
// Картинка версии — её шаг в рабочей папке (ImageStepOf); у исходника нити по файлу — сам файл проекта,
// прочитанный через ProjectLinkGuard тем же путём, что образцы и исходник запуска.
public sealed class ImageFrameSource(
    ImageThreadStore store,
    ImageThreadService threads,
    IProjectManager projects,
    ISessionDirectory directory,
    ImageEditSteps? steps = null) : IImageFrameSource
{
    // Сколько раз повторяем создание черновика, если человек записал нити между чтением и записью
    private const int DraftAttempts = 3;

    public async Task<ImageFrame?> GetAsync(string ownerId, string threadId, string versionId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(ownerId) || string.IsNullOrWhiteSpace(threadId) || string.IsNullOrWhiteSpace(versionId))
            return null;
        foreach (var (owner, sessionId) in store.Chats())
        {
            if (owner != ownerId) continue;
            var thread = store.Get(owner, sessionId).Threads.FirstOrDefault(t => t.Id == threadId);
            if (thread?.Version(versionId) is not { } version) continue;
            if (directory.GetById(sessionId) is not { } session) return null;
            var scopeKey = ImageEditScope.Of(session).Key;

            if (thread.ImageStepOf(version) is { Length: > 0 } stepId && steps?.Open(owner, scopeKey, stepId) is { } step)
                return new ImageFrame(step.Image.Bytes, step.Image.ContentType);
            // Исходник нити по файлу — файл проекта; у личной области проекта нет
            if (thread.File is { Length: > 0 } file && projects.GetById(scopeKey) is { } project)
            {
                var read = await ImageEditLaunchAssembler.ReadProjectImageAsync(project.RootPath, file, "Кадр", ct);
                return read.Value is { } image ? new ImageFrame(image.Bytes, image.ContentType) : null;
            }
            return null;
        }
        return null;
    }

    public async Task<ImageFrameDraft?> CreateDraftAsync(string ownerId, string scopeKey, string sessionId, string folder,
        CancellationToken ct)
    {
        if (directory.GetById(sessionId) is not { } session
            || directory.ResolveOwnerId(session) != ownerId
            || ImageEditScope.Of(session).Key != scopeKey)
            return null;
        for (var attempt = 0; attempt < DraftAttempts; attempt++)
        {
            var revision = store.Get(ownerId, sessionId).Revision;
            var written = await threads.OpenAsync(ownerId, scopeKey, sessionId, null, folder, revision, ct);
            if (written.Status == ImageThreadWriteStatus.Conflict) continue;
            return written is { Status: ImageThreadWriteStatus.Ok, Thread: { } thread }
                ? new ImageFrameDraft(thread.Id, ImageThreadVersion.OriginId)
                : null;
        }
        return null;
    }
}
