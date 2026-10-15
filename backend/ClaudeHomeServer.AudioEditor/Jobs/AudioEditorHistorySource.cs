using ClaudeHomeServer.Services.Media;

namespace ClaudeHomeServer.Services.AudioEditor.Jobs;

// Источник результата для истории (ADR-030 §3): модуль «Звук». Результат — основной файл роли
// main варианта в рабочей папке data/audio-editor/{owner}/{job}/{variant}/ (AudioEditWorkspace,
// кеш 7 дней). Файлы в data/, не в проекте: local_project/project_gone сюда не относятся.
public sealed class AudioEditorHistorySource(AudioEditWorkspace workspace, TimeProvider? time = null)
    : IMediaHistoryResultSource
{
    public string Source => LocalJobOrigin.SourceAudioEditor;

    public Task<MediaHistoryResolution?> ResolveAsync(MediaHistoryRef reference, int variant, CancellationToken ct)
    {
        var now = (time ?? TimeProvider.System).GetUtcNow();

        if (reference.SourceJobId is not { } jobId)
            return Task.FromResult<MediaHistoryResolution?>(new(0, null, null, MediaHistoryGoneReason.Missing));

        var owner = reference.OwnerId ?? "";
        var variants = workspace.VariantNumbers(owner, jobId);
        var variantCount = variants.Count;
        if (variantCount == 0)
            return Task.FromResult<MediaHistoryResolution?>(new(0, null, null, MediaHistoryGoneReason.Missing));
        // n — 0-based индекс клиента; редактор хранит варианты с 1 (папки 1/, 2/, …), поэтому
        // идём по реальному списку. n за пределами — 404 (читатель проверяет n < VariantCount)
        if (variant < 0 || variant >= variantCount)
            return Task.FromResult<MediaHistoryResolution?>(new(variantCount, null, null, null));
        var path = workspace.MainFile(owner, jobId, variants[variant]);
        if (path is null || !File.Exists(path))
            return Task.FromResult<MediaHistoryResolution?>(new(variantCount, null, null, MediaHistoryGoneReason.Missing));

        if (now - File.GetLastWriteTimeUtc(path) > AudioEditWorkspace.Ttl)
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
