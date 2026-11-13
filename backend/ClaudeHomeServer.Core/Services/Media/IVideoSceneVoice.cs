namespace ClaudeHomeServer.Services.Media;

// Шов «Видео → Звук» (агентская «Озвучить сцену»): инструмент video_voice модуля «Видео» запускает звук по видео
// версии сцены тем же путём, что кнопка человека (черновик нити звука → котировка → задача с версией сцены и
// вклейкой). Реализация — AudioEditor; видео-модуль код звука не знает. Нет подсистемы звука — нет и регистрации,
// инструмент честно отказывает. Результат — новая версия сцены со звуком и версия нити звука.
public interface IVideoSceneVoice
{
    Task<VideoVoiceStart> StartAsync(VideoVoiceRequest request, CancellationToken ct);

    // Задача озвучки: чужая и несуществующая неотличимы (null)
    VideoVoiceJob? Get(string ownerId, string scopeKey, string jobId);

    Task<VideoVoiceJob?> CancelAsync(string ownerId, string scopeKey, string jobId, CancellationToken ct);
}

// ProjectId — null у личного чата. Model — подпись модели звука (id, ключ или часть имени); null — «Авто» каталога
public sealed record VideoVoiceRequest(string OwnerId, string? ActorId, string? ProjectId, string SessionId, string SceneId,
    string? VersionId, string? Text, string? Model);

public sealed record VideoVoiceStart(VideoVoiceStarted? Value, string? Code, string? Error)
{
    public static VideoVoiceStart Fail(string code, string error) => new(null, code, error);
}

public sealed record VideoVoiceStarted(string JobId, string ThreadId, string Provider, string Model, string License,
    int? EtaSeconds);

public sealed record VideoVoiceJob(string JobId, string ScopeKey, string? ChatSessionId, string Status, string Provider,
    string Model, bool Terminal, int Variants, int? QueuePosition, int? EtaSeconds, string? Error);
