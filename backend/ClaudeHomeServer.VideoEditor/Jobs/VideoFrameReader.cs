using ClaudeHomeServer.Services.Images.Editing.Raster;
using ClaudeHomeServer.Services.Media;
using ClaudeHomeServer.Services.VideoEditor.Contracts;

namespace ClaudeHomeServer.Services.VideoEditor.Jobs;

// Читает кадры сцены в байты для драйвера (драйвер диска проекта не касается). Кадр-файл — только из
// проекта и только через ProjectLinkGuard (символическая ссылка наружу — отказ); у личной области проекта
// нет — отказ ДО диска. Кадр из «Картинок» (image) берётся ТОЛЬКО швом IImageFrameSource: хранилища нитей
// картинок модуль не читает. Нет шва (редактор картинок выключен) или версии нет — frame_unavailable.
public sealed class VideoFrameReader(IImageFrameSource? images = null, VideoEditWorkspace? workspace = null,
    IImageRaster? raster = null)
{
    public const long MaxFrameBytes = 20L * 1024 * 1024;

    public sealed record Frame(VideoFrameBytes? Bytes, string? ErrorCode, string? Error)
    {
        public static Frame None { get; } = new(null, null, null);
        public static Frame Fail(string error) => new(null, VideoEditorErrors.FrameUnavailable, error);
    }

    // Подрезка кадра под пропорции формата сцены (ADR-025 §5): до модели, по рамке AspectCrop.Rect с якорем.
    // Пропорции совпали — кадр не трогаем (растр не нужен). Без растра (Images выключена) подрезка невозможна — dsp_unavailable.
    // SourceAspect — пропорции исходного кадра, если его подрезали (для framesCropped), иначе null
    public sealed record Fitted(VideoFrameBytes? Frame, string? ErrorCode, string? Error, string? SourceAspect)
    {
        public static Fitted Fail(string code, string error) => new(null, code, error, null);
    }

    public Fitted Fit(VideoFrameBytes frame, string aspect, string? anchor)
    {
        if (raster is null)
            return Fitted.Fail(VideoEditorErrors.DspUnavailable,
                "Подрезать кадры под формат сцены нельзя: на сервере не включена обработка изображений");
        if (raster.Probe(frame.Bytes) is not { } probe)
            return Fitted.Fail(VideoEditorErrors.FrameUnavailable, "Кадр не png, jpg или webp — подрезать его нельзя");
        var plan = Plan(probe.DisplayWidth, probe.DisplayHeight, aspect, anchor);
        if (plan is null) return Fitted.Fail(VideoEditorErrors.InvalidRequest, $"Пропорции «{aspect}» или якорь рамки неверны");
        if (!plan.Cropped) return new Fitted(frame, null, null, null);
        var cropped = AspectCrop.Crop(raster, frame.Bytes, AspectCrop.Rect(probe.DisplayWidth, probe.DisplayHeight, aspect, anchor)!);
        if (cropped.Bytes is null)
            return Fitted.Fail(cropped.Busy ? VideoEditorErrors.TooManyJobs : VideoEditorErrors.FrameUnavailable, cropped.Error!);
        return new Fitted(new VideoFrameBytes(cropped.Bytes, cropped.ContentType!), null, null, plan.SourceAspect);
    }

    // Превью подрезки (dryRun): тот же расчёт, что у Fit, ничего не пишет
    public (VideoFramePreviewDto? Preview, string? ErrorCode, string? Error) Preview(VideoFrameBytes frame, string aspect, string? anchor)
    {
        if (raster is null)
            return (null, VideoEditorErrors.DspUnavailable,
                "Превью подрезки недоступно: на сервере не включена обработка изображений");
        if (raster.Probe(frame.Bytes) is not { } probe)
            return (null, VideoEditorErrors.FrameUnavailable, "Кадр не png, jpg или webp");
        return Plan(probe.DisplayWidth, probe.DisplayHeight, aspect, anchor) is { } plan
            ? (plan, null, null)
            : (null, VideoEditorErrors.InvalidRequest, $"Пропорции «{aspect}» или якорь рамки неверны");
    }

    private static VideoFramePreviewDto? Plan(int width, int height, string aspect, string? anchor)
    {
        if (AspectCrop.Rect(width, height, aspect, anchor) is not { } rect) return null;
        var size = AspectCrop.ResultSize(width, height, aspect, anchor)!;
        var cropped = rect.Width < 0.999999 || rect.Height < 0.999999;
        return new VideoFramePreviewDto(width, height, Reduce(width, height), cropped, size.W, size.H, rect.X, rect.Y,
            rect.Width, rect.Height);
    }

    // 1920×1080 → «16:9»; не из малых пропорций — «W:H» по НОД
    internal static string Reduce(int w, int h)
    {
        var a = w;
        var b = h;
        while (b != 0) (a, b) = (b, a % b);
        return $"{w / a}:{h / a}";
    }

    public async Task<Frame> ReadAsync(string ownerId, VideoEditScope scope, FrameRef? frame, CancellationToken ct)
    {
        if (frame is null) return Frame.None;
        if (frame.Kind == FrameRef.KindImage) return await ReadImageAsync(ownerId, frame, ct);
        if (frame.Kind != FrameRef.KindFile || string.IsNullOrWhiteSpace(frame.Path))
            return Frame.Fail("Неизвестный вид кадра");
        if (scope.Project is not { } project)
            return await ReadPersonalFileAsync(ownerId, frame.Path, ct);

        if (ProjectLinkGuard.ResolveInside(project.RootPath, frame.Path) is not { } full || !File.Exists(full))
            return Frame.Fail($"Кадр «{frame.Path}» не найден в проекте");
        var contentType = ContentTypeOf(full);
        if (contentType is null) return Frame.Fail($"Кадр «{frame.Path}» — не png, jpg или webp");
        var info = new FileInfo(full);
        if (info.Length > MaxFrameBytes) return Frame.Fail($"Кадр «{frame.Path}» больше 20 МБ");
        return new Frame(new VideoFrameBytes(await File.ReadAllBytesAsync(full, ct), contentType), null, null);
    }

    // Личный чат: кадр лежит в рабочей папке владельца (загрузка «С компьютера»), не в проекте
    private async Task<Frame> ReadPersonalFileAsync(string ownerId, string path, CancellationToken ct)
    {
        if (workspace?.FindFrame(ownerId, path) is not { } full) return Frame.Fail($"Кадр «{path}» не найден");
        var contentType = ContentTypeOf(full);
        if (contentType is null) return Frame.Fail($"Кадр «{path}» — не png, jpg или webp");
        if (new FileInfo(full).Length > MaxFrameBytes) return Frame.Fail($"Кадр «{path}» больше 20 МБ");
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
