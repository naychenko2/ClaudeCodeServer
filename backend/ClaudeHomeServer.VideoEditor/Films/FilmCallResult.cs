using ClaudeHomeServer.Services.VideoEditor.Contracts;

namespace ClaudeHomeServer.Services.VideoEditor.Films;

// Итог операции с фильмом: значение либо код ошибки из VideoEditorErrors с текстом для человека. Conflict —
// свежее состояние при revision_conflict: ручка кладёт его в 409
public sealed record FilmCallResult<T>(T? Value, string? ErrorCode, string? Error)
{
    public FilmStateDto? Conflict { get; init; }

    public bool IsOk => ErrorCode is null && Value is not null;

    public static FilmCallResult<T> Ok(T value) => new(value, null, null);
    public static FilmCallResult<T> Fail(string code, string error) => new(default, code, error);
}
