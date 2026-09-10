using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Tasks;

namespace ClaudeHomeServer.Services.Composition;

// Адаптеры четырёх узких швов для вертикали Spend (Этап 5, вынос Spend в
// отдельный .csproj). Все — тонкие обёртки один-к-одному: вызов приходит
// из SpendAnalyticsService/SpendMaintenanceService, адаптер форвардит в
// корневой сервис Main. Иначе Spend упирается в `ProjectReference` на Main,
// который сторож `SubsystemBoundaryTests` ловит.
//
// Соседняя линия `refactor/core-directories` готовит «директорию сессий» —
// `ISessionDirectory` подлежит слиянию с ней, когда она приедет в master
// (см. комментарий в Core/Services/ISessionDirectory.cs). До того момента
// живём с узким швом на три метода.

public sealed class SessionDirectoryAdapter(SessionManager sessions) : ISessionDirectory
{
    public Session? GetById(string id) => sessions.GetById(id);

    public IReadOnlyCollection<Session> GetAll() => sessions.GetAll();

    public string? ResolveOwnerId(Session s) => sessions.ResolveOwnerId(s);
}

public sealed class PersonaLookupAdapter(PersonaManager personas) : IPersonaLookup
{
    public Persona? GetByIdInternal(string id) => personas.GetByIdInternal(id);
}

public sealed class ChatHistoryLoaderAdapter(ChatHistoryService history) : IChatHistoryLoader
{
    public Task<List<StoredMessage>> LoadAsync(string claudeSessionId) =>
        history.LoadAsync(claudeSessionId);

    public DateTime? LastWriteUtc(string claudeSessionId) =>
        history.LastWriteUtc(claudeSessionId);
}

public sealed class TaskLookupAdapter(TaskManager tasks) : ITaskLookup
{
    public TaskItem? GetById(string id) => tasks.GetById(id);
}
