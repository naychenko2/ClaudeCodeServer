using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Services.Access;

/// <summary>
/// Отказ «не автор задачи» (F2-T1, разрез §2.1, по образцу чатов — вопрос 2 макета): удаление
/// задачи общего проекта может её автор (<c>TaskItem.CreatedByUserId</c>) или её владелец
/// (арендатор проекта). Участнику с правом записи на чужой задаче отвечаем 403 с этим кодом —
/// по нему фронт выключает кнопку с причиной; читателю отказывает раньше право (403
/// project_scope), невидимому — 404 (существование не раскрываем). Автор неизвестен
/// (<c>CreatedByUserId == null</c>, старые записи и серверные пути) — автором не считается
/// (тот же fail-closed, что <see cref="ChatAuthorGate"/> и DenyTurnAuthor): остаётся владелец.
/// </summary>
public static class TaskAuthorGate
{
    public const string ErrorCode = "not_task_author";
    public const string DeniedText = "Удалить задачу может её автор или владелец проекта";

    public static ObjectResult Denied() =>
        new(new { error = DeniedText, code = ErrorCode }) { StatusCode = StatusCodes.Status403Forbidden };
}
