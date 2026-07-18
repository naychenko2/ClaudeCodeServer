using System.Collections.Concurrent;
using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.Execution;

// Резолв драйвера среды исполнения по владельцу процесса.
public interface ILauncherFactory
{
    // Локальная среда — для системных вызовов бэкенда (каталог моделей, changelog)
    IProcessLauncher Local { get; }
    // Среда владельца: container-пользователь → docker-песочница; null/неизвестный → local
    IProcessLauncher ForOwner(string? ownerId);
}

public sealed class LauncherFactory(UserStore users, SandboxManager sandbox) : ILauncherFactory
{
    private readonly ConcurrentDictionary<string, DockerProcessRunner> _sandboxed = new();

    public IProcessLauncher Local => LocalProcessRunner.Instance;

    public IProcessLauncher ForOwner(string? ownerId)
    {
        if (ownerId is null) return Local;
        var env = users.GetById(ownerId)?.ExecutionEnvironment;
        // Fail closed: если песочница не настроена, container-пользователь получит
        // понятную ошибку из SandboxManager.EnsureRunningAsync, а не тихий запуск на хосте
        return env == ExecutionEnvironments.Container
            ? _sandboxed.GetOrAdd(ownerId, id => new DockerProcessRunner(sandbox, id))
            : Local;
    }
}
