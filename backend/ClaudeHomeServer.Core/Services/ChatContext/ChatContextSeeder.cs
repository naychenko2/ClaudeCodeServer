namespace ClaudeHomeServer.Services.ChatContext;

// Производное состояние чата без файла контекста: основной объект из фокуса вертикали (первая по
// приоритету, у которой он есть). Ревизия 0 и пустые референсы; файл появляется на первой записи стора,
// которая применяется уже поверх засеянного состояния.
public sealed class ChatContextSeeder(
    IEnumerable<IChatContextSeedSource> sources,
    ISessionDirectory sessions,
    IProjectManager projects)
{
    private readonly IChatContextSeedSource[] _sources = [.. sources.OrderBy(s => s.SeedPriority)];

    public ChatContextState Seed(string ownerId, string sessionId)
    {
        var empty = new ChatContextState(0, null, []);
        if (_sources.Length == 0 || sessions.GetById(sessionId) is not { } session) return empty;
        var project = session.ProjectId is { } pid ? projects.GetById(pid) : null;
        var scope = new ContextScope(ownerId, session, project);
        foreach (var source in _sources)
        {
            try
            {
                if (source.SeedPrimary(scope) is { } primary) return empty with { Primary = primary };
            }
            catch
            {
                // Сбой вертикали не должен ронять чтение контекста: считаем, что фокуса у неё нет
            }
        }
        return empty;
    }
}
