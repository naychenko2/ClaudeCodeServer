using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services;

// Диспетчерская доска агентов: агрегирует задачи с исполнителем Claude/персона
// и их живые сессии, классифицирует по колонкам (очередь/работает/ждёт/готово).
// Данные — только чтение из существующих TaskManager + SessionManager.
public class BoardService
{
    private readonly TaskManager _tasks;
    private readonly SessionManager _sessions;
    private readonly PersonaManager _personas;

    public BoardService(TaskManager tasks, SessionManager sessions, PersonaManager personas)
    {
        _tasks = tasks;
        _sessions = sessions;
        _personas = personas;
    }

    /// <summary>
    /// Собрать доску агентов для владельца: все задачи с исполнителем Claude/персона,
    /// классифицированные по колонкам с привязкой к живым сессиям.
    /// </summary>
    public List<BoardItem> GetBoard(string ownerId)
    {
        // Все задачи владельца с исполнителем Claude или персоной
        var tasks = _tasks.GetByOwner(ownerId)
            .Where(t => t.Assignee == TaskItemAssignee.Claude || t.PersonaId != null)
            .ToList();

        // Индекс живых сессий (sessionId → Session)
        var allSessions = _sessions.GetAll();
        var liveSessionIds = allSessions
            .Where(s => s.Status is SessionStatus.Starting or SessionStatus.Working or SessionStatus.Waiting)
            .Select(s => s.Id)
            .ToHashSet();

        var items = new List<BoardItem>(tasks.Count);
        var now = DateTime.UtcNow;

        foreach (var task in tasks)
        {
            var sessionId = task.LinkedSessionId;
            Session? session = null;
            if (sessionId is not null)
                session = _sessions.GetById(sessionId);

            string column;
            string sessionStatus;
            bool permissionPending;

            if (task.Status == TaskItemStatus.Done)
            {
                // Завершённая задача — всегда «Готово» (даже если сессия ещё жива)
                column = "done";
                sessionStatus = session?.Status.ToString().ToLower() ?? "finished";
                permissionPending = false;
            }
            else if (session is null || !liveSessionIds.Contains(session.Id))
            {
                // Нет живой сессии — «Очередь» (ждёт запуска)
                column = "queue";
                sessionStatus = session is not null ? session.Status.ToString().ToLower() : "pending";
                permissionPending = false;
            }
            else if (session.Status == SessionStatus.Waiting)
            {
                // Сессия ждёт ответа пользователя (permission / вопрос)
                column = "waiting";
                sessionStatus = "waiting";
                permissionPending = true;
            }
            else
            {
                // Сессия работает (Starting или Working)
                column = "working";
                sessionStatus = session.Status.ToString().ToLower();
                permissionPending = false;
            }

            items.Add(new BoardItem(
                TaskId: task.Id,
                Title: task.Title,
                ProjectId: task.ProjectId,
                SessionId: session?.Id,
                Column: column,
                SessionStatus: sessionStatus,
                PersonaId: task.PersonaId,
                CurrentToolName: null, // v1: без текущего инструмента
                StartedAt: task.ClaudeStartedAt ?? task.UpdatedAt,
                PermissionPending: permissionPending
            ));
        }

        // Сортировка: живые (working/waiting) — сверху, по StartedAt; затем queue по CreatedAt; done — вниз
        items.Sort((a, b) =>
        {
            var orderA = a.Column switch { "working" => 0, "waiting" => 1, "queue" => 2, _ => 3 };
            var orderB = b.Column switch { "working" => 0, "waiting" => 1, "queue" => 2, _ => 3 };
            var cmp = orderA.CompareTo(orderB);
            if (cmp != 0) return cmp;
            // Внутри колонки — от новых к старым
            var startedA = a.StartedAt ?? DateTime.MinValue;
            var startedB = b.StartedAt ?? DateTime.MinValue;
            return startedB.CompareTo(startedA);
        });

        return items;
    }
}
