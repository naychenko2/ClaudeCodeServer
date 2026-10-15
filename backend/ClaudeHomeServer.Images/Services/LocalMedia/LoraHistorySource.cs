using System.Text.RegularExpressions;
using ClaudeHomeServer.Services.ImageEditor;
using ClaudeHomeServer.Services.Media;

namespace ClaudeHomeServer.Services.Images.LocalMedia;

// Источник результата для истории (ADR-030 §3, ADR-026 §14): обучение LoRA персонажа.
// Результат прогона train — обучающие фото: data/lora-training/<ownerKey>/<projectKey>/<slug>/
// <sha16>.<ext> (ADR-026 §5), вне проекта и вне бэкапа. Путь — только SafePath.Join от корня
// data, по владельцу/проекту/slug из записи; клиентского пути нет. Админская видимость —
// исключение из LoraNamespace.VisibleTo: только на чтение, только в этом источнике (ADR-026 §14)
// — админ читает фото владельца записи, а не свои, поэтому VisibleTo (правило «свои LoRA»)
// здесь не применяем. Фото удалены уборкой (ADR-026 §11) — 410 missing; срок времени не режет.
public sealed class LoraHistorySource(LocalMediaJobStore store, IConfiguration config)
    : IMediaHistoryResultSource
{
    // Имя обучающего фото: первые 16 hex SHA-256 содержимого + расширение (ADR-026 §5)
    private static readonly Regex PhotoName = new(@"^[0-9a-f]{16}\.(jpg|png|webp)\z", RegexOptions.Compiled);

    public string Source => LocalJobOrigin.SourceLora;

    public Task<MediaHistoryResolution?> ResolveAsync(MediaHistoryRef reference, int variant, CancellationToken ct)
    {
        // Job id — из метки происхождения; без него slug не найти (запись без задачи: старые прогоны)
        if (reference.SourceJobId is not { } jobId)
            return Task.FromResult<MediaHistoryResolution?>(new(0, null, null, MediaHistoryGoneReason.Missing));

        // Задача обучения: чужой/неизвестный владелец — null (как у LocalMediaHistorySource);
        // lora_delete — уборка, фото не отдаёт
        var job = store.Get(jobId, reference.OwnerId ?? "");
        if (job is null || job.Op != LocalMediaOps.LoraTrain)
            return Task.FromResult<MediaHistoryResolution?>(new(0, null, null, MediaHistoryGoneReason.Missing));

        // slug персонажа — из задачи; невалидный — missing (fail-closed, путь не собираем)
        if (job.LoraSlug is not { } slug || !LoraNamespace.IsValidSlug(slug))
            return Task.FromResult<MediaHistoryResolution?>(new(0, null, null, MediaHistoryGoneReason.Missing));

        // Ключи владельца/проекта: Guid без дефисов (LoraNamespace.OwnerKey); не Guid — missing
        string ownerKey, projectKey;
        try
        {
            ownerKey = LoraNamespace.OwnerKey(job.OwnerId);
            projectKey = LoraNamespace.OwnerKey(job.ProjectId);
        }
        catch (ArgumentException)
        {
            return Task.FromResult<MediaHistoryResolution?>(new(0, null, null, MediaHistoryGoneReason.Missing));
        }

        // Корень данных LoRA: data/lora-training/<ownerKey>/<projectKey>/<slug>/
        string dir;
        try
        {
            dir = SafePath.Join(DataDir(), Path.Combine(ImageEditorPaths.TrainingDirName, ownerKey, projectKey, slug));
        }
        catch (UnauthorizedAccessException)
        {
            return Task.FromResult<MediaHistoryResolution?>(new(0, null, null, MediaHistoryGoneReason.Missing));
        }
        if (!Directory.Exists(dir))
            return Task.FromResult<MediaHistoryResolution?>(new(0, null, null, MediaHistoryGoneReason.Missing));

        // Фото: отсортированы по имени (хеши содержимого — порядок загрузки на результат не влияет)
        var photos = Directory.EnumerateFiles(dir)
            .Select(Path.GetFileName)
            .Where(n => n is not null && PhotoName.IsMatch(n))
            .Select(n => n!)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
        var variantCount = photos.Count;
        if (variant < 0 || variant >= variantCount)
            return Task.FromResult<MediaHistoryResolution?>(new(variantCount, null, null, MediaHistoryGoneReason.Missing));

        string full;
        try
        {
            full = SafePath.Join(dir, photos[variant]);
        }
        catch (UnauthorizedAccessException)
        {
            return Task.FromResult<MediaHistoryResolution?>(new(variantCount, null, null, MediaHistoryGoneReason.Missing));
        }
        if (!File.Exists(full))
            return Task.FromResult<MediaHistoryResolution?>(new(variantCount, null, null, MediaHistoryGoneReason.Missing));

        // Content-type по сигнатуре; SVG/HTML не отдаём (у фото — jpg/png/webp)
        var contentType = DetectContentType(full);
        if (contentType is null || MediaContentTypeSniffer.IsNeverServed(contentType))
            return Task.FromResult<MediaHistoryResolution?>(new(variantCount, null, null, MediaHistoryGoneReason.Missing));

        return Task.FromResult<MediaHistoryResolution?>(new(variantCount, full, contentType, null));
    }

    // Корень data: родитель DataPath (data/projects.json → data)
    private string DataDir()
    {
        var dataPath = config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json");
        return Path.GetDirectoryName(dataPath)!;
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
