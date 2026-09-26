using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.ImageEditor;

// «Обсудить с Claude» (ADR-017 §6) до шага 10 ADR-018. Ручка и проверки входа — в модуле
// редактора: маршрут image-editor/* целиком его, и выключенный модуль даёт 404 и здесь.
// Чат и сообщение создаёт Main — им нужны SessionManager, персоны и файлы. Шов временный:
// сносится вместе с ручкой, когда чат картинки заменит «Обсудить».
public interface IImageDiscussStarter
{
    Task<ImageDiscussOutcome> StartAsync(string ownerId, Project project, ImageDiscussInput input, CancellationToken ct);
}

// Annotated — байты размеченной копии, AnnotatedExtension — расширение по сигнатуре
// (null — не картинка). SourcePath уже проверен модулем: внутри проекта и существует.
// Character — Path папки персонажа от корня проекта. SessionId — чат этого сеанса редактора.
public sealed record ImageDiscussInput(
    string Text,
    byte[] Annotated,
    string? AnnotatedExtension,
    string? SourcePath,
    ImageDiscussCharacter? Character,
    string? SessionId);

public sealed record ImageDiscussCharacter(string Name, string Path);

// SessionId — чат, куда ушло сообщение; Created — чат новый (false — переиспользован)
public sealed record ImageDiscussResultDto(string SessionId, bool Created, IReadOnlyList<string> Attachments);

// Коды ошибок — те же строки, что у REST редактора (ImageEditErrorCodes в модуле)
public sealed record ImageDiscussOutcome(ImageDiscussResultDto? Value, string? ErrorCode, string? Error)
{
    public const string InvalidRequest = "invalid_request";
    public const string Unavailable = "image_editor_unavailable";

    public static ImageDiscussOutcome Ok(ImageDiscussResultDto value) => new(value, null, null);
    public static ImageDiscussOutcome Fail(string code, string error) => new(null, code, error);
}
