using ClaudeHomeServer.Services.Media;

namespace ClaudeHomeServer.Services.VideoEditor.Jobs;

// Источник результата для истории (ADR-030 §3): модуль «Видео». Результат — клип варианта
// {job}/{variant}/clip.mp4 в рабочей папке data/video-editor (VideoEditWorkspace, кеш 7 дней).
// Файлы в data/, не в проекте: local_project/project_gone сюда не относятся.
public sealed class VideoEditorHistorySource(VideoEditWorkspace workspace, TimeProvider? time = null)
    : IMediaHistoryResultSource
{
    public string Source => LocalJobOrigin.SourceVideoEditor;

    public Task<MediaHistoryResolution?> ResolveAsync(MediaHistoryRef reference, int variant, CancellationToken ct)
    {
        var now = (time ?? TimeProvider.System).GetUtcNow();

        if (reference.SourceJobId is not { } jobId)
            return Task.FromResult<MediaHistoryResolution?>(new(0, null, null, MediaHistoryGoneReason.Missing));

        var owner = reference.OwnerId ?? "";
        var variantCount = workspace.VariantNumbers(owner, jobId).Count;
        if (variantCount == 0)
            return Task.FromResult<MediaHistoryResolution?>(new(0, null, null, MediaHistoryGoneReason.Missing));

        var path = workspace.FindClip(owner, jobId, variant);
        if (path is null || !File.Exists(path))
            return Task.FromResult<MediaHistoryResolution?>(new(variantCount, null, null, MediaHistoryGoneReason.Missing));

        if (now - File.GetLastWriteTimeUtc(path) > VideoEditWorkspace.Ttl)
            return Task.FromResult<MediaHistoryResolution?>(new(variantCount, null, null, MediaHistoryGoneReason.Expired));

        var contentType = MediaContentTypeSniffer.Detect(ReadHead(path));
        if (contentType is null || MediaContentTypeSniffer.IsNeverServed(contentType))
            return Task.FromResult<MediaHistoryResolution?>(new(variantCount, null, null, MediaHistoryGoneReason.Missing));

        return Task.FromResult<MediaHistoryResolution?>(new(variantCount, path, contentType, null));
    }

    private static byte[] ReadHead(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            var head = new byte[64];
            var read = fs.Read(head, 0, head.Length);
            return head.AsSpan(0, read).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
