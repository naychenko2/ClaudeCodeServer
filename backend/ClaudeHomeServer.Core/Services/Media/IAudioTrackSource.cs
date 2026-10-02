namespace ClaudeHomeServer.Services.Media;

// Шов «Видео → Звук» (ADR-022 §3): музыка фильма сочиняется в редакторе звука. Реализация — AudioEditor;
// «Видео» читает треки только через шов и никогда не лезет в data/audio-threads.
public interface IAudioTrackSource
{
    // Черновик «Новый звук» в режиме музыки в чате: «Сочинить под фильм…». null — чат не свой
    Task<AudioTrackDraft?> CreateDraftAsync(string ownerId, string scopeKey, string sessionId, string folder,
        CancellationToken ct);

    // Основной файл версии нити владельца (роль main) байтами; нить ищется среди чатов владельца.
    // null — нити, версии или файла нет
    Task<AudioTrackFile?> GetMainFileAsync(string ownerId, string threadId, string versionId, CancellationToken ct);
}

public sealed record AudioTrackDraft(string ThreadId);

// Extension — с точкой, нижним регистром: «.mp3»
public sealed record AudioTrackFile(byte[] Bytes, string Extension);
