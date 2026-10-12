namespace ClaudeHomeServer.Services.Composition;

// Узкий шов «агент выкатки жив» для Deploy (ADR-010, Этап 5 волна 2). Windows — именованный
// мьютекс Global\ccs-deploy (Main/Services.Backup.InstanceLock); Linux — файловый лок
// <releasesDir>/deploy.lock (flock -x в deploy-linux.sh). Лок — инфраструктурный примитив, а
// не логика Backup: сам Backup держит два других (Instance/Backup), а Deploy — свой.
// TODO: когда вынесут общий примитив именованных мьютексов — заменить Windows-ветку на него.
public interface IDeployAgentLock
{
    /// <summary>
    /// null — лок занят (агент жив), иначе объект владения; Dispose освобождает.
    /// releasesDir — каталог релизов из конфига: на Linux именно в нём лежит deploy.lock.
    /// </summary>
    IDisposable? TryAcquireDeploy(string releasesDir);
}
