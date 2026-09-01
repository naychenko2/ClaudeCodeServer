using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.Watchdog;

/// <summary>
/// Живое окружение цикла сторожей: чаты (гашение при удалении/архивации) и рабочий
/// каталог опроса. Узкий шов над SessionManager/ProjectManager/UserStore — сервис цикла
/// не тащит тяжеловесов, а юнит-тесты подменяют окружение фейком (CI Linux — без хостов).
/// </summary>
public interface IWatchdogEnvironment
{
    /// <summary>Живой чат владельца (SessionManager.GetOwned); null — удалён.</summary>
    Session? FindChat(string sessionId, string ownerId);

    /// <summary>Мостик к SessionManager.OnSessionDeleted — мгновенное гашение сторожей.</summary>
    event Action<Session>? ChatDeleted;

    /// <summary>
    /// Рабочий каталог poll-запуска — ЖИВОЙ резолв на каждый опрос (план): rootPath проекта
    /// сторожа; чат вне проектов — домашняя папка владельца (UserHomeResolver).
    /// null — запуск невозможен (проект удалён / дом не настроен).
    /// </summary>
    string? ResolveWorkDir(WatchdogRecord w);
}

public sealed class WatchdogEnvironment(
    SessionManager sessions,
    ProjectManager projects,
    UserStore users,
    UserHomeResolver homeResolver) : IWatchdogEnvironment
{
    // Событие пробрасываем явными аксессорами: поле-событие интерфейса иначе держало бы
    // отдельный список обработчиков, не доходящий до SessionManager
    public event Action<Session>? ChatDeleted
    {
        add => sessions.OnSessionDeleted += value;
        remove => sessions.OnSessionDeleted -= value;
    }

    public Session? FindChat(string sessionId, string ownerId) =>
        sessions.GetOwned(sessionId, ownerId);

    public string? ResolveWorkDir(WatchdogRecord w) =>
        w.ProjectId is { } pid
            ? projects.GetById(pid)?.RootPath
            : homeResolver.Resolve(users.GetById(w.OwnerId));
}
