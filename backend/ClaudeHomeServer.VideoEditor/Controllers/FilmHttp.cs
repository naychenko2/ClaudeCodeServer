using ClaudeHomeServer.Services.VideoEditor.Contracts;
using ClaudeHomeServer.Services.VideoEditor.Films;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Services.VideoEditor.Controllers;

// Ответы ручек фильмов: один перевод «код ошибки → HTTP» на проектные и личные ручки. Чужое и несуществующее
// неотличимы (404); 409 revision_conflict несёт свежее состояние фильма; 422 — файл фильма невалиден или чужой схемы.
internal static class FilmHttp
{
    public static IActionResult Map<T>(FilmCallResult<T> result, Func<T, IActionResult> ok)
    {
        if (result.IsOk) return ok(result.Value!);
        var code = result.ErrorCode ?? VideoEditorErrors.InvalidRequest;
        var status = code switch
        {
            VideoEditorErrors.NameTaken or VideoEditorErrors.RevisionConflict
                or VideoEditorErrors.ProviderUnavailable => StatusCodes.Status409Conflict,
            VideoEditorErrors.FilmInvalid or VideoEditorErrors.FilmSchemaUnsupported => StatusCodes.Status422UnprocessableEntity,
            VideoEditorErrors.FileNotFound or VideoEditorErrors.SceneNotFound or VideoEditorErrors.VersionNotFound
                or VideoEditorErrors.ChatNotFound or VideoEditorErrors.JobNotFound => StatusCodes.Status404NotFound,
            VideoEditorErrors.TooManyJobs or VideoEditorErrors.HeavyBusy => StatusCodes.Status429TooManyRequests,
            VideoEditorErrors.DspUnavailable => StatusCodes.Status503ServiceUnavailable,
            _ => StatusCodes.Status400BadRequest,
        };
        var text = result.Error ?? "Запрос не выполнен";
        return new ObjectResult(code == VideoEditorErrors.RevisionConflict && result.Conflict is { } fresh
            ? new { error = text, code, state = fresh }
            : new { error = text, code }) { StatusCode = status };
    }

    public static ObjectResult PersonalRefusal() => new(new
    {
        error = "Фильмы — только в чате проекта",
        code = VideoEditorErrors.PersonalScopeNoFilms,
    }) { StatusCode = StatusCodes.Status400BadRequest };
}
