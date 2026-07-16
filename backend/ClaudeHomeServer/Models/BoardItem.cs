namespace ClaudeHomeServer.Models;

// Элемент доски агентов (диспетчерская): задача + живая сессия исполнителя.
// Каждый элемент — одна задача с исполнителем Claude/персона,
// классифицированная в колонку по состоянию исполнения.
public record BoardItem(
    string TaskId,
    string Title,
    string? ProjectId,
    string? SessionId,
    // queue | working | waiting | done
    string Column,
    // Статус сессии (starting/working/waiting/active/finished/error) или "pending" для queue
    string SessionStatus,
    // Исполнитель
    string? PersonaId,
    // Название текущего инструмента (только для working)
    string? CurrentToolName,
    // Когда запущено исполнение (ClaudeStartedAt)
    DateTime? StartedAt,
    // Есть активный permission-запрос (ждёт пользователя)
    bool PermissionPending
);
