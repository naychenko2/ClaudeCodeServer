using ClaudeHomeServer.Services.ChatContext;
using ClaudeHomeServer.Services.Media;
using ClaudeHomeServer.Services.VideoEditor.Contracts;

namespace ClaudeHomeServer.Services.VideoEditor.Scenes;

// Формат видео-сцены (ADR-025): проверка желания, зеркало Aspect и замок формата фильма. Единственная точка для
// ручек человека и инструментов агента, чтобы они не разошлись.
public static class VideoSceneFormat
{
    // Тот же текст ждёт и фронт, и агент (записка Майи): «Фильм «утро» — 16:9. Сцена в 9:16 даст чёрные полосы…»
    public static string LockedText(string filmPath, string filmAspect, string sceneAspect) =>
        $"Фильм «{FilmName(filmPath)}» — {filmAspect}. Сцена в {sceneAspect} даст чёрные полосы по бокам при склейке.";

    // У видео «Точно…» и «Как у исходника» нет: только пропорции и ступень или «как у модели»
    public static string? Validate(MediaFormat? format)
    {
        if (format is null) return null;
        if (format.Mode is not (FormatModes.Model or FormatModes.Aspect))
            return "У видео формат — пропорции и ступень качества или «как у модели»";
        if (format.Aspect is { } aspect && !AspectCrop.TryParse(aspect, out _)) return $"Недопустимые пропорции: {aspect}";
        if (format.Tier is { } tier && !Enum.IsDefined(tier)) return "Недопустимая ступень формата";
        if (format.Mode == FormatModes.Aspect && format.Aspect is null) return "Для пропорций нужно указать aspect";
        return null;
    }

    // Рамка подрезки кадров
    public static string? ValidateFit(VideoFrameFitDto? fit) =>
        fit?.Anchor is { } anchor && !CropAnchors.All.Contains(anchor) ? $"Недопустимый якорь рамки: {anchor}" : null;

    // Время и автор ставит сервер: клиент их не присылает, а «✦» от чужого значения не получить
    public static MediaFormat Stamp(MediaFormat format, ContextActor by, DateTime now) => format with { By = by, At = now };

    // Aspect — зеркало Format.Aspect. Старый клиент (нет Format в теле или Format не менялся) правит одно Aspect: тогда оно
    // становится желанием, иначе правка бы молча потерялась за прежним Format
    public static VideoSceneSettingsDto Normalize(VideoSceneSettingsDto incoming, VideoSceneSettingsDto? current,
        ContextActor by, DateTime now)
    {
        var format = incoming.Format;
        if (format is not null && current?.Format == format && incoming.Aspect is { } legacy && legacy != format.Aspect)
            format = format with { Mode = FormatModes.Aspect, Aspect = legacy };
        else if (format is null && current?.Format is { } kept && incoming.Aspect is null)
            format = kept;
        else if (format is null && incoming.Aspect is { } aspectOnly && current?.Format is { } old && old.Aspect != aspectOnly)
            format = old with { Mode = FormatModes.Aspect, Aspect = aspectOnly };
        if (format is not null && format != current?.Format) format = Stamp(format, by, now);
        return incoming with { Format = format, Aspect = format is { Mode: FormatModes.Aspect } ? format.Aspect : incoming.Aspect };
    }

    // Пропорции, которые сцена просит сейчас (желание или прежнее поле)
    public static string? AspectOf(VideoSceneSettingsDto s) => s.Format is { Mode: FormatModes.Aspect } f ? f.Aspect : s.Aspect;

    // Замок: сцена фильма следует пропорциям фильма. Смена на ДРУГИЕ пропорции без force — отказ; «как у модели»
    // и те же пропорции пропускаются
    public static VideoFormatLockDto? LockedConflict(VideoSceneDto scene, VideoSceneSettingsDto incoming, bool force)
    {
        if (force || scene.LockedBy is not { } lockedBy) return null;
        var wanted = AspectOf(incoming);
        return wanted is not null && wanted != lockedBy.Aspect && wanted != AspectOf(scene.Settings) ? lockedBy : null;
    }

    private static string FilmName(string path)
    {
        var name = System.IO.Path.GetFileNameWithoutExtension(path);
        return string.IsNullOrEmpty(name) ? path : name;
    }
}
