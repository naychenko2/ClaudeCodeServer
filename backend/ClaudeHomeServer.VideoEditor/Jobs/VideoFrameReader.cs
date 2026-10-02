using ClaudeHomeServer.Services.Media;
using ClaudeHomeServer.Services.VideoEditor.Contracts;

namespace ClaudeHomeServer.Services.VideoEditor.Jobs;

// Читает кадры сцены в байты для драйвера (драйвер диска проекта не касается). Кадр-файл — только из
// проекта и только через ProjectLinkGuard (символическая ссылка наружу — отказ); у личной области проекта
// нет — отказ ДО диска. Кадр из «Картинок» (image) берётся ТОЛЬКО швом IImageFrameSource: хранилища нитей
// картинок модуль не читает. Нет шва (редактор картинок выключен) или версии нет — frame_unavailable.
public sealed class VideoFrameReader(IImageFrameSource? images = null)
{
    public const long MaxFrameBytes = 20L * 1024 * 1024;

    public sealed record Frame(VideoFrameBytes? Bytes, string? ErrorCode, string? Error)
    {
        public static Frame None { get; } = new(null, null, null);
        public static Frame Fail(string error) => new(null, VideoEditorErrors.FrameUnavailable, error);
    }

    public async Task<Frame> ReadAsync(string ownerId, VideoEditScope scope, FrameRef? frame, CancellationToken ct)
    {
        if (frame is null) return Frame.None;
        if (frame.Kind == FrameRef.KindImage) return await ReadImageAsync(ownerId, frame, ct);
        if (frame.Kind != FrameRef.KindFile || string.IsNullOrWhiteSpace(frame.Path))
            return Frame.Fail("Неизвестный вид кадра");
        if (scope.Project is not { } project)
            return Frame.Fail("Кадры-файлы есть только у чата проекта");

        if (ProjectLinkGuard.ResolveInside(project.RootPath, frame.Path) is not { } full || !File.Exists(full))
            return Frame.Fail($"Кадр «{frame.Path}» не найден в проекте");
        var contentType = ContentTypeOf(full);
        if (contentType is null) return Frame.Fail($"Кадр «{frame.Path}» — не png, jpg или webp");
        var info = new FileInfo(full);
        if (info.Length > MaxFrameBytes) return Frame.Fail($"Кадр «{frame.Path}» больше 20 МБ");
        return new Frame(new VideoFrameBytes(await File.ReadAllBytesAsync(full, ct), contentType), null, null);
    }

    private async Task<Frame> ReadImageAsync(string ownerId, FrameRef frame, CancellationToken ct)
    {
        if (images is null) return Frame.Fail("Редактор картинок выключен: кадр из «Картинок» недоступен, выберите кадр-файл проекта");
        if (string.IsNullOrWhiteSpace(frame.ThreadId) || string.IsNullOrWhiteSpace(frame.VersionId))
            return Frame.Fail("У кадра из «Картинок» не указаны нить и версия");
        if (await images.GetAsync(ownerId, frame.ThreadId, frame.VersionId, ct) is not { } image)
            return Frame.Fail("Кадр из «Картинок» не найден: нить или версия удалены, либо у картинки ещё нет изображения");
        if (image.Bytes.LongLength > MaxFrameBytes) return Frame.Fail("Кадр из «Картинок» больше 20 МБ");
        return new Frame(new VideoFrameBytes(image.Bytes, image.ContentType), null, null);
    }

    private static string? ContentTypeOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".webp" => "image/webp",
        _ => null,
    };
}
