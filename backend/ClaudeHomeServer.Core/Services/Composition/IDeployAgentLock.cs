namespace ClaudeHomeServer.Services.Composition;

// Узкий шов InstanceLock (Main/Services.Backup) для выноса Deploy (Этап 5, волна 2).
// DeployHost.TakeLockAgent проверяет, жив ли deploy-agent.ps1, через именованный
// мьютекс Global\ccs-deploy (ADR-010). Мьютекс — инфраструктурный примитив, а не
// логика Backup: сам Backup держит два других (Instance/Backup), а Deploy только Deploy.
// TODO: когда вынесут общий примитив именованных мьютексов — заменить на него.
public interface IDeployAgentLock
{
    /// <summary>null — мьютекс занят (агент жив), иначе объект владения.</summary>
    Mutex? TryAcquireDeploy();
}
