using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Services.Access;

/// <summary>
/// Отказ «не автор чата» (S1-7, план v2 К4): переименование, архив и удаление чата общего проекта
/// до S2a-10 доступны только владельцу. Участнику, которому чат виден, отвечаем 403 с этим
/// кодом; постороннему остаётся 404 (существование не раскрываем) — это решает вызывающий.
/// </summary>
public static class ChatAuthorGate
{
    public const string ErrorCode = "not_chat_author";

    public static ObjectResult Denied() =>
        new(new { error = "Только автор чата", code = ErrorCode }) { StatusCode = StatusCodes.Status403Forbidden };
}
