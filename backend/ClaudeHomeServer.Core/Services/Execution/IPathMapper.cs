namespace ClaudeHomeServer.Services.Execution;

// Перевод путей между ФС бэкенда (хост) и ФС среды исполнения процессов пользователя.
// Контракт в Core: реализации (IdentityPathMapper/DockerPathMapper) живут в Main.
public interface IPathMapper
{
    string ToRuntime(string hostPath);
    string ToHost(string runtimePath);
}
