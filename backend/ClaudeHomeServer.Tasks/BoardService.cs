using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Access;

namespace ClaudeHomeServer.Services.Tasks;

// Диспетчерская доска агентов: агрегирует задачи с исполнителем Claude/персона
// и их живые сессии, классифицирует по колонкам (очередь/работает/ждёт/готово).
// Данные — только чтение из существующих TaskManager + ISessionDirectory
// (Core-шов для выноса вертикали, см. ISessionDirectory.cs).
// Ссылка на чат — только тем, кто его видит (S2d-4c): видимость SessionAccess
// (Core-примитив, та же точка, что GetOwnedVisible и шлюзы редакторов), проект
// для неё — через Core-шов IProjectManager.
public class BoardService
{
    private readonly TaskManager _tasks;
    private readonly ISessionDirectory _sessions;
    private readonly IProjectManager _projects;

    public BoardService(TaskManager tasks, ISessionDirectory sessions, IProjectManager projects)
    {
        _tasks = tasks;
        _sessions = sessions;
        _projects = projects;
    }

    /// <summary>
    /// Собрать доску агентов для владельца: все задачи с исполнителем Claude/персона,
    /// классифицированные по колонкам с привязкой к живым сессиям. Чужой приватный чат
    /// (невидимый зрителю) позиции не снимает, но id чата и флаг permission у позиции
    /// обнуляются (S2d-4c).
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

            // S2d-4c (находка ревью R-S2d-3b §3): к задаче зрителя может быть привязан чужой
            // приватный чат — не видимый даже владельцу проекта (решение 28.10). Позиция
            // остаётся: задача и её колонка исполнения — зрителей; ссылка на чат не показывается
            // — SessionId и PermissionPending обнуляются. Проверка — видимость CanSee, а не
            // владение: владельца проектного чата выводит проект, и по владению участник потерял
            // бы ссылку на свой живой агент в общем чате.
            var visibleSession = session is not null && VisibleToViewer(session, ownerId) ? session : null;

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
                SessionId: visibleSession?.Id,
                Column: column,
                SessionStatus: sessionStatus,
                PersonaId: task.PersonaId,
                CurrentToolName: null, // v1: без текущего инструмента
                StartedAt: task.ClaudeStartedAt ?? task.UpdatedAt,
                PermissionPending: permissionPending && visibleSession is not null
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

    // Видимость чата зрителю доски: Core-точка SessionAccess.CanSee (та же, что
    // SessionManager.GetOwnedVisible и шлюзы редакторов S2d-1d); проект — через Core-шов
    // IProjectManager. Осиротевший проектный чат (проект не найден) не виден: резолвить
    // доступ нечем, fail-closed по контракту CanSee.
    private bool VisibleToViewer(Session session, string viewerId)
    {
        var project = session.ProjectId is not null ? _projects.GetById(session.ProjectId) : null;
        return SessionAccess.CanSee(session, project, viewerId);
    }
}
