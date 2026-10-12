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
    // deploy-linux.sh держит flock -x, наш OpenOrCreate падает с IOException (EWOULDBLOCK).
    //
    // Две ситуации, которые раньше маскировались одним catch (Exception) → null:
    //   1. Лок занят (EWOULDBLOCK) → «агент жив» → null.
    //   2. Файл недоступен (нет прав, нет каталога, ENOSPC) → не «агент жив», а сбой
    //      системы: пробрасываем, чтобы DeployService ответил понятной ошибкой, а не
    //      «другая выкатка идёт».
    private static IDisposable? TryAcquireFileLock(string releasesDir)
    {
        try
        {
            var path = Path.Combine(releasesDir, "deploy.lock");
            var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return new FileLease(stream);
        }
        catch (IOException ex) when (IsLockConflict(ex))
        {
            // flock(LOCK_EX|LOCK_NB) вернул EWOULDBLOCK — файл занят другим процессом.
            return null;
        }
        // IOException другого рода (DirectoryNotFoundException, ENOSPC) и
        // UnauthorizedAccessException — файл недоступен, не «агент жив»: пробрасываем.
    }

    // EWOULDBLOCK (errno 11) — .NET на Linux передаёт его как HResult без facility-кода.
    // Это единственный случай, когда FileShare.None → flock означает «занято», а не «сбой».
    private static bool IsLockConflict(IOException ex) => ex.HResult is 0x0000000B;

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
