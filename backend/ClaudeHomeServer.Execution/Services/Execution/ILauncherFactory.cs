using System.Collections.Concurrent;
using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.Execution;

// Реализация ILauncherFactory (контракт в Core, реализация держит IUserStore/SandboxManager
// и потому живёт в Main).
public sealed class LauncherFactory(IUserStore users, SandboxManager sandbox) : ILauncherFactory
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
