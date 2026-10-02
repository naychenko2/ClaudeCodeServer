namespace ClaudeHomeServer.Services.Media;

// Шов «Видео → Картинки» (ADR-022 §3): кадр сцены — версия нити редактора картинок. Реализация —
// ImageEditor; «Видео» читает кадры только ЧЕРЕЗ шов и никогда не лезет в data/image-threads.
// Нет подсистемы image-editor — нет и регистрации, потребитель держит шов nullable.
public interface IImageFrameSource
{
    // Картинка версии нити владельца: нить ищется среди чатов владельца по threadId (кадр хранит нить и
    // версию, а чат нити — не обязательно чат сцены). null — нити или версии нет, у черновика нет картинки
    // или файл не читается
    Task<ImageFrame?> GetAsync(string ownerId, string threadId, string versionId, CancellationToken ct);

    // Черновик «Новая картинка» в чате: кадр сцены рисуется в «Картинках». null — чат не свой или область
    // не подходит. scopeKey — id проекта или «personal», folder — папка проекта ("" — корень)
    Task<ImageFrameDraft?> CreateDraftAsync(string ownerId, string scopeKey, string sessionId, string folder,
        CancellationToken ct);
}

public sealed record ImageFrame(byte[] Bytes, string ContentType);

public sealed record ImageFrameDraft(string ThreadId, string VersionId);
