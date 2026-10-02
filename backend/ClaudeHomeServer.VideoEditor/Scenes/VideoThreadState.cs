using ClaudeHomeServer.Services.VideoEditor.Contracts;

namespace ClaudeHomeServer.Services.VideoEditor.Scenes;

// Нити сцен чата в том виде, как лежат в data/video-threads/{ownerId}/{sessionId}.json (ADR-022 §2).
// Форма сцен — те же DTO контрактов (без вычисляемого Stale: он считается при выдаче). Events — журнал
// «с прошлого сообщения» для блока хвоста хода, TurnCursor — до какого момента журнал уже показан ходу
// (сдвиг курсора ревизию не поднимает: сборка хода — не правка). Добавлять поля только аддитивно:
// файл живёт в бэкапе.
public sealed record VideoThreadsState(VideoFocusDto Focus, long Revision, IReadOnlyList<VideoSceneDto> Scenes)
{
    public static VideoThreadsState Empty { get; } = new(new VideoFocusDto(null, null), 0, []);

    public IReadOnlyList<VideoThreadEvent> Events { get; init; } = [];
    public DateTime? TurnCursor { get; init; }

    // Контракт для ленты и ручек: у сцен посчитано «переснять»
    public VideoThreadsStateDto ToDto() => new(Focus, Revision, [.. Scenes.Select(VideoStale.Apply)]);
}

public sealed record VideoThreadEvent(DateTime At, string Kind, string Text, string? SceneId = null, string? JobId = null);

public static class VideoThreadEventKinds
{
    public const string Launched = "launched";
    // Результаты запуска стали версиями (или запуск кончился без них)
    public const string Versions = "versions";
    public const string Saved = "saved";
    public const string Interrupted = "interrupted";
}

public enum VideoThreadWriteStatus
{
    Ok,
    // Ревизия устарела — State несёт актуальное состояние
    Conflict,
    // Сцены с таким id в этом чате нет (чужая неотличима от несуществующей)
    SceneNotFound,
    VersionNotFound,
    // Запись не подходит: удалить сцену с версиями или идущим запуском, неверные настройки
    Invalid,
}

public sealed record VideoThreadWrite(VideoThreadWriteStatus Status, VideoThreadsState State)
{
    public VideoSceneDto? Scene { get; init; }
    public IReadOnlyList<VideoClipVersionDto> NewVersions { get; init; } = [];
}

// Итог одного варианта запуска: файл лежит в рабочей папке задачи (VideoEditWorkspace), здесь — метаданные
public sealed record VideoVariantResult(int Variant, double DurationSec, long SizeBytes, bool HasSound, VideoCostDto? Cost);

public static class VideoSignatures
{
    // Подпись кадра для снимка входов: «image:{threadId}:{versionId}» или «file:{path}»; нет кадра — null
    public static string? Of(FrameRef? frame) => frame switch
    {
        null => null,
        { Kind: FrameRef.KindImage } f => $"image:{f.ThreadId}:{f.VersionId}",
        { Kind: FrameRef.KindFile } f => $"file:{f.Path}",
        _ => null,
    };

    public static VideoInputsSnapshotDto Snapshot(VideoSceneSettingsDto settings) =>
        new(settings.Text, Of(settings.FrameA), Of(settings.FrameB));
}

// «Кадр/текст изменён после съёмки»: текущие входы сцены против снимка её текущей версии
public static class VideoStale
{
    public static VideoSceneDto Apply(VideoSceneDto scene) => scene with { Stale = Compute(scene) };

    public static VideoSceneStaleDto? Compute(VideoSceneDto scene)
    {
        var current = scene.Versions.FirstOrDefault(v => v.VersionId == scene.CurrentVersionId);
        if (current is null) return null;
        var now = VideoSignatures.Snapshot(scene.Settings);
        var text = now.Text != current.Inputs.Text;
        var a = now.FrameA != current.Inputs.FrameA;
        var b = now.FrameB != current.Inputs.FrameB;
        return text || a || b ? new VideoSceneStaleDto(text, a, b, current.VersionId) : null;
    }
}
