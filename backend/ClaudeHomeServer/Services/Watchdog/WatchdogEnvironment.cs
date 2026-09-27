using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.Watchdog;

// Конкретная реализация IWatchdogEnvironment: SessionManager + IProjectManager + IUserStore + UserHomeResolver.
// Живёт в Main: SessionManager и UserHomeResolver — типы Main.
public sealed class WatchdogEnvironment(
    SessionManager sessions,
    IProjectManager projects,
    IUserStore users,
    UserHomeResolver homeResolver,
    Execution.IProjectDeviceGate? deviceGate = null) : IWatchdogEnvironment
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

    // Опрос локального проекта идёт на устройстве (ForProject в раннере) — офлайн-устройство
    // означает пропуск. Отозванное устройство не «ждём»: раннер получит отказ и посчитает сбой.
    public string? DeviceWaitReason(WatchdogRecord w) =>
        w.ProjectId is { } pid && projects.GetById(pid) is { } project
            && deviceGate?.Check(project) is { MustWait: true } gate
            ? gate.Reason ?? "устройство проекта не в сети"
            : null;
}
