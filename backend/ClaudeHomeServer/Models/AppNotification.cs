namespace ClaudeHomeServer.Models;

// Уведомление пользователя: напоминания, ответы агентов, системные события.
// Хранится в data/notifications.json (NotificationStore).
public class AppNotification
{
    public string Id { get; init; } = Guid.NewGuid().ToString();
    public string OwnerId { get; set; } = "";

    // Категория для иконки/цвета/фильтрации на фронте
    public string Kind { get; set; } = "info"; // reminder | claude | info | success

    // Подтип для точной классификации (напр. "task_reminder", "execution_completed", "briefing")
    public string Type { get; set; } = "";

    public string Title { get; set; } = "";
    public string Body { get; set; } = "";

    // Hash-диплинк для клика (открыть чат/задачу/заметку)
    public string? Url { get; set; }

    // Контекст: проект, чат, задача — для группировки и мета-информации
    public string? ProjectId { get; set; }
    public string? SessionId { get; set; }
    public string? TaskId { get; set; }

    // Текстовая метка источника (напр. "Проект «Сайт»", "Чат: Ревьюер")
    public string? Source { get; set; }

    // Тег для отображения (напр. "Напоминание", "Персона", "Исполнитель", "Дайджест")
    public string? Tag { get; set; }

    public bool IsRead { get; set; }
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime? ReadAt { get; set; }
}

// DTO для создания уведомления (REST + MCP)
public class CreateNotificationRequest
{
    public string Kind { get; set; } = "info";
    public string Type { get; set; } = "";
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public string? Url { get; set; }
    public string? ProjectId { get; set; }
    public string? SessionId { get; set; }
    public string? TaskId { get; set; }
    public string? Source { get; set; }
    public string? Tag { get; set; }
}

// DTO для списка (без лишних деталей)
public class NotificationListItem
{
    public string Id { get; init; } = "";
    public string Kind { get; init; } = "";
    public string Type { get; init; } = "";
    public string Title { get; init; } = "";
    public string Body { get; init; } = "";
    public string? Url { get; init; }
    public string? ProjectId { get; init; }
    public string? SessionId { get; init; }
    public string? TaskId { get; init; }
    public string? Source { get; init; }
    public string? Tag { get; init; }
    public bool IsRead { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? ReadAt { get; init; }
}

// Ответ со списком + мета
public class NotificationListResponse
{
    public List<NotificationListItem> Items { get; init; } = [];
    public int TotalCount { get; init; }
    public int UnreadCount { get; init; }
}

// DTO для массовых операций
public class NotificationBatchRequest
{
    public List<string> Ids { get; init; } = [];
}

public class NotificationMarkReadRequest
{
    public List<string>? Ids { get; init; } // null = все прочитать
}
