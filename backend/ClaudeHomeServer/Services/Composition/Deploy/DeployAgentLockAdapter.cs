using System.IO;
using ClaudeHomeServer.Services.Backup;

namespace ClaudeHomeServer.Services.Composition.Deploy;

// Реализация IDeployAgentLock (Core): «агент выкатки жив».
// Windows — именованный мьютекс Global\ccs-deploy (InstanceLock, Main/Services.Backup),
// обёрнутый в MutexLease. Linux — файловый лок <releasesDir>/deploy.lock: deploy-linux.sh
// держит его через flock -x всё время работы, и FileStream с FileShare.None не откроется,
// пока flock не отпущен. Платформа — OperatingSystem: адаптер — инфраструктурный примитив,
// а не бизнес-метод, поэтому ветку выбирает платформа раннера, а не свойство конфига.
public sealed class DeployAgentLockAdapter : IDeployAgentLock
{
    public IDisposable? TryAcquireDeploy(string releasesDir)
    {
        if (OperatingSystem.IsLinux())
            return TryAcquireFileLock(releasesDir);
        return InstanceLock.TryAcquireDeploy() is { } mutex ? new MutexLease(mutex) : null;
    }

    // Файловый лок: тот же файл, что держит flock в deploy-linux.sh. FileShare.None —
    // ключевое: на Linux .NET реализует его через flock(LOCK_EX|LOCK_NB), поэтому пока
    // deploy-linux.sh держит flock -x, наш OpenOrCreate падает с IOException. IOException
    // → null («агент жив»); любой другой сбой — тоже null, чтобы приём заявки не ронялся
    // на лок-примитиве (предмет вызова — в другом).
    private static IDisposable? TryAcquireFileLock(string releasesDir)
    {
        try
        {
            var path = Path.Combine(releasesDir, "deploy.lock");
            var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return new FileLease(stream);
        }
        catch (Exception)
        {
            return null;
        }
    }

    // FileStream закрываем в try/catch: файл мог быть удалён агентом (deploy.lock), и падение
    // на Dispose не должно ронять приём заявки.
    private sealed class FileLease(FileStream stream) : IDisposable
    {
        public void Dispose()
        {
            try { stream.Dispose(); } catch { /* файл уже не наш */ }
        }
    }

    // MutexLease переехал из DeployHost: владение мьютексом принадлежит ПОТОКУ, поэтому
    // ReleaseMutex в try/catch — мьютекс мог быть заброшен умершим агентом (владение перешло
    // к нам через AbandonedMutexException), и падение на ReleaseMutex не должно ронять приём
    // заявки.
    private sealed class MutexLease(Mutex mutex) : IDisposable
    {
        public void Dispose()
        {
            try { mutex.ReleaseMutex(); } catch { /* не наш мьютекс или уже отпущен */ }
            mutex.Dispose();
        }
    }
}
