using ClaudeHomeServer.Services.Backup;

namespace ClaudeHomeServer.Services.Composition.Deploy;

// Реализация IDeployAgentLock (Core) поверх InstanceLock (Main/Services.Backup).
// Статический форвардер: InstanceLock — статический класс, DI не нужен.
public sealed class DeployAgentLockAdapter : IDeployAgentLock
{
    public Mutex? TryAcquireDeploy() => InstanceLock.TryAcquireDeploy();
}
