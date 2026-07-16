namespace ClaudeHomeServer.Services.Execution;

// Перевод путей между ФС бэкенда (хост) и ФС среды исполнения процессов пользователя.
public interface IPathMapper
{
    string ToRuntime(string hostPath);
    string ToHost(string runtimePath);
}

// Локальная среда: процессы видят ту же ФС, что и бэкенд
public sealed class IdentityPathMapper : IPathMapper
{
    public static readonly IdentityPathMapper Instance = new();
    public string ToRuntime(string hostPath) => hostPath;
    public string ToHost(string runtimePath) => runtimePath;
}
