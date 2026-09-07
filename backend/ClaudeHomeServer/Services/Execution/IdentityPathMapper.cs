namespace ClaudeHomeServer.Services.Execution;

// Локальная среда: процессы видят ту же ФС, что и бэкенд.
// Конкретная реализация IPathMapper в Main — интерфейс живёт в Core.
public sealed class IdentityPathMapper : IPathMapper
{
    public static readonly IdentityPathMapper Instance = new();
    public string ToRuntime(string hostPath) => hostPath;
    public string ToHost(string runtimePath) => runtimePath;
}
