using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services;

/// <summary>
/// Запись контекста чата, как её видят потребители: та же <see cref="SessionContextEntry"/>
/// плюс признак «не найден». Не хранится — считается на каждый запрос.
/// </summary>
public record SessionContextItem(string Type, string Id, string? Title, bool Missing);

/// <summary>
/// Единая точка резолва контекста чата (фича chat-context): по составу записей считает
/// признак «не найден». Потребителей двое — REST фронта
/// (<c>GET {sessionId}/context</c>) и MCP-тул <c>context_list</c>; своя копия правила у
/// каждого развела бы их в оценке одной и той же записи.
///
/// Правила существования: file — SafeJoin от корня проекта + File.Exists, task — резолв
/// задачи в ТОМ ЖЕ проекте и у того же владельца (контекст проектного чата адресуется
/// только внутри проекта), url — не проверяем (валидна сама ссылка).
/// </summary>
public class SessionContextResolver(ProjectManager projects, TaskManager tasks)
{
    /// <summary>
    /// Состав контекста сессии с признаками missing. ownerId — владелец сессии
    /// (по нему проверяется принадлежность задач).
    /// </summary>
    public IReadOnlyList<SessionContextItem> Resolve(Session session, string ownerId)
    {
        var rootPath = session.ProjectId is { } projectId
            ? projects.GetById(projectId)?.RootPath
            : null;

        return session.Context
            .Select(e => new SessionContextItem(e.Type, e.Id, e.Title,
                Missing: IsMissing(e, rootPath, session.ProjectId, ownerId)))
            .ToList();
    }

    private bool IsMissing(SessionContextEntry entry, string? rootPath, string? projectId, string ownerId) =>
        entry.Type switch
        {
            // Файл адресуется относительно корня проекта: чат вне проекта такую запись
            // развернуть не может — она «не найдена», а не молча валидна.
            SessionContextTypes.File => rootPath is null || !FileExistsInProject(rootPath, entry.Id),
            SessionContextTypes.Task => tasks.GetById(entry.Id) is not { } t
                || t.ProjectId != projectId || t.OwnerId != ownerId,
            _ => false,
        };

    // SafeJoin бросает на пути «наружу проекта» — ловим: битая запись это missing,
    // а не 500 всего запроса.
    private static bool FileExistsInProject(string rootPath, string relativePath)
    {
        try { return File.Exists(FileService.SafeJoinPublic(rootPath, relativePath)); }
        catch (UnauthorizedAccessException) { return false; }
    }
}
