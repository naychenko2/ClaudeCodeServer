namespace ClaudeHomeServer.Services.Reader;

/// <summary>
/// Пятнадцать кодов ошибки ридера (docs/adr/ADR-005-link-reader-server.md, раздел 6).
/// Тексты для человека пишет фронт — здесь только машинный код.
/// </summary>
public enum ReaderErrorCode
{
    InvalidUrl,
    LocalAddress,
    DnsFailed,
    Unreachable,
    TlsInvalid,
    Timeout,
    AuthRequired,
    BlockedBySite,
    NotFound,
    ServerError,
    TooManyRedirects,
    NotAPage,
    Pdf,
    TooLarge,
    NotReadable,
}

public static class ReaderErrorCodeNames
{
    /// <summary>kebab-case ключ — ровно как в таблице ADR, идёт на провод как есть.</summary>
    public static string ToWireName(this ReaderErrorCode code) => code switch
    {
        ReaderErrorCode.InvalidUrl => "invalid-url",
        ReaderErrorCode.LocalAddress => "local-address",
        ReaderErrorCode.DnsFailed => "dns-failed",
        ReaderErrorCode.Unreachable => "unreachable",
        ReaderErrorCode.TlsInvalid => "tls-invalid",
        ReaderErrorCode.Timeout => "timeout",
        ReaderErrorCode.AuthRequired => "auth-required",
        ReaderErrorCode.BlockedBySite => "blocked-by-site",
        ReaderErrorCode.NotFound => "not-found",
        ReaderErrorCode.ServerError => "server-error",
        ReaderErrorCode.TooManyRedirects => "too-many-redirects",
        ReaderErrorCode.NotAPage => "not-a-page",
        ReaderErrorCode.Pdf => "pdf",
        ReaderErrorCode.TooLarge => "too-large",
        ReaderErrorCode.NotReadable => "not-readable",
        _ => throw new ArgumentOutOfRangeException(nameof(code)),
    };
}

/// <summary>Исход чтения — успех с markdown либо код ошибки (+httpStatus для диагностики).</summary>
public sealed record ReaderOutcome
{
    public bool Success { get; private init; }
    // Контракт с фронтом (frontend/src/types/index.ts ReaderPage): title обязателен и не null —
    // вызывающий обязан подставить фолбэк (обычно host), когда у страницы нет естественного заголовка.
    public string? Title { get; private init; }
    public string? SiteName { get; private init; }
    public string? Byline { get; private init; }
    public string? Markdown { get; private init; }
    public ReaderErrorCode? Error { get; private init; }
    public int? HttpStatus { get; private init; }

    public static ReaderOutcome Ok(string title, string? siteName, string? byline, string markdown) => new()
    {
        Success = true, Title = title, SiteName = siteName, Byline = byline, Markdown = markdown,
    };

    public static ReaderOutcome Fail(ReaderErrorCode error, int? httpStatus = null) => new()
    {
        Success = false, Error = error, HttpStatus = httpStatus,
    };
}

/// <summary>
/// ConnectCallback клиента "link-reader" бросает это, когда прямое (без прокси) соединение
/// целится в приватный/loopback-адрес — резолвился заново прямо перед подключением, поэтому
/// закрывает TOCTOU-окно между предварительной проверкой в ReaderService и реальным connect
/// (см. ADR-005, раздел 2, «Про DNS rebinding честно»).
/// </summary>
public sealed class ReaderConnectBlockedException(bool dnsFailed) : Exception
{
    public bool DnsFailed { get; } = dnsFailed;
}
