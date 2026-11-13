namespace ClaudeHomeServer.Services.Media;

// Шов «Звук → Видео сцены» (Ш4, звук по видео): редактор звука берёт клип версии сцены и возвращает готовую
// дорожку в виде новой версии сцены. Реализация — VideoEditor; «Звук» в data/video-threads и рабочую папку
// видео не лезет. Нет подсистемы видео — нет и регистрации, запуск «Озвучить сцену» честно отказывает.
public interface IVideoSceneSound
{
    // Клип версии (VersionId не задана — текущая версия сцены) байтами; null — сцены, версии или файла нет
    Task<VideoSceneClip?> GetClipAsync(VideoSceneRef scene, CancellationToken ct);

    // Новая версия сцены: клип основы со вклеенным звуком. Model — подпись источника звука для версии
    Task<VideoSoundVersion> AddSoundVersionAsync(VideoSceneRef scene, byte[] audio, string audioExtension, string model,
        CancellationToken ct);
}

// Сцена чата: SessionId — чат, в котором лежит нить сцены; ScopeKey — ключ области (проект или личный чат)
public sealed record VideoSceneRef(string OwnerId, string ScopeKey, string SessionId, string SceneId, string? VersionId);

public sealed record VideoSceneClip(byte[] Bytes, string VersionId, double Seconds);

public sealed record VideoSoundVersion(string? VersionId, string? Error)
{
    public static VideoSoundVersion Fail(string error) => new(null, error);
}
