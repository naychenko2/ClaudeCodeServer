using ClaudeHomeServer.Services.Media;

namespace ClaudeHomeServer.Services.Images.LocalMedia;

// Источник результата для истории (ADR-030 §3): локальная генерация (ComfyUI). Результат лежит
// в проекте: .cc-attachments/local-media/{дата}/{jobId}-… (LocalMediaService.CollectAsync). Задача
// берётся из стора по владельцу записи; путь результата — из job.Outputs (относительный, через
// SafePath.Join от корня проекта). Проект проверяем на каждом вызове: переехал на устройство —
// local_project, удалён — project_gone, не касаясь диска (дух сторожа G10).
public sealed class LocalMediaHistorySource(
    LocalMediaJobStore store,
    ILocalMediaProjectAccess projects,
    TimeProvider? time = null) : IMediaHistoryResultSource
{
    // Срок жизни рабочих файлов: 7 дней, как у LocalMediaJobStore.JobRetentionDays
    public static readonly TimeSpan FileRetention = TimeSpan.FromDays(7);

    public string Source => LocalJobOrigin.SourceLocalMedia;

    public Task<MediaHistoryResolution?> ResolveAsync(MediaHistoryRef reference, int variant, CancellationToken ct)
    {
        var now = (time ?? TimeProvider.System).GetUtcNow();

        // Job id — из метки происхождения; без него файл не найти (запись без задачи: старые прогоны)
        if (reference.SourceJobId is not { } jobId)
            return Task.FromResult<MediaHistoryResolution?>(new(0, null, null, MediaHistoryGoneReason.Missing));

        var job = store.Get(jobId, reference.OwnerId ?? "");
        if (job is null)
            return Task.FromResult<MediaHistoryResolution?>(new(0, null, null, MediaHistoryGoneReason.Missing));

        var outputs = job.Outputs;
        var variantCount = outputs.Count;

        // Задача завершилась (failed) или отменена, но файлов нет — no_result (не missing:
        // файл не «удалён», а просто не произведён)
        if (variantCount == 0 && (LocalMediaStatuses.IsTerminal(job.Status) || job.Cancelled))
            return Task.FromResult<MediaHistoryResolution?>(new(0, null, null, MediaHistoryGoneReason.NoResult));

        // Проект: удалён / на устройстве / на сервере. null — записи без проекта (не ждём, но
        // локальная генерация проект всегда имеет)
        var probe = projects.ProbeProject(reference.OwnerId ?? "", reference.ProjectId ?? "");
        if (probe is null || probe.State == MediaHistoryProjectState.Gone)
            return Task.FromResult<MediaHistoryResolution?>(new(variantCount, null, null, MediaHistoryGoneReason.ProjectGone));
        if (probe.State == MediaHistoryProjectState.Local)
            return Task.FromResult<MediaHistoryResolution?>(new(variantCount, null, null, MediaHistoryGoneReason.LocalProject));

        // n за пределами числа вариантов — защита (читатель тоже проверяет): файла такого нет
        if (variant < 0 || variant >= variantCount)
            return Task.FromResult<MediaHistoryResolution?>(new(variantCount, null, null, MediaHistoryGoneReason.Missing));

        var output = outputs[variant];
        string full;
        try
        {
            full = SafePath.Join(probe.RootPath!, output.Path);
        }
        catch (UnauthorizedAccessException)
        {
            return Task.FromResult<MediaHistoryResolution?>(new(variantCount, null, null, MediaHistoryGoneReason.Missing));
        }
        if (!File.Exists(full))
            return Task.FromResult<MediaHistoryResolution?>(new(variantCount, null, null, MediaHistoryGoneReason.Missing));

        // Срок: файл старше 7 дней — expired (рабочие файлы редактора/стора живут неделю)
        if (now - File.GetLastWriteTimeUtc(full) > FileRetention)
            return Task.FromResult<MediaHistoryResolution?>(new(variantCount, null, null, MediaHistoryGoneReason.Expired));

        // Content-type по сигнатуре; SVG/HTML не отдаём никогда
        var contentType = DetectContentType(full);
        if (contentType is null || MediaContentTypeSniffer.IsNeverServed(contentType))
            return Task.FromResult<MediaHistoryResolution?>(new(variantCount, null, null, MediaHistoryGoneReason.Missing));

        return Task.FromResult<MediaHistoryResolution?>(new(variantCount, full, contentType, null));
    }

    // Первые 64 байта → content-type по сигнатуре; сбой чтения — null (файл не отдаём)
    private static string? DetectContentType(string full)
    {
        try
        {
            using var fs = File.OpenRead(full);
            var head = new byte[64];
            var read = fs.Read(head, 0, head.Length);
            return MediaContentTypeSniffer.Detect(head.AsSpan(0, read).ToArray());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
