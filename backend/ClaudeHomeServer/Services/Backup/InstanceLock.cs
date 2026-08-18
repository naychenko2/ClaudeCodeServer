using System.Security.Cryptography;
using System.Text;

namespace ClaudeHomeServer.Services.Backup;

// Именованные мьютексы вокруг каталога data.
//
// Instance — держит работающий сервер весь свой uptime. Это единственный надёжный признак
// «сервер запущен» для восстановления: порт из CLI не проверить (их два, один https,
// адреса в Kestrel:Endpoints), а restore под живым сервером означает, что тот продолжит
// писать в перемещённый каталог и пересоздаст data под собой.
//
// Backup — сериализует снапшоты: их инициируют трое (таймер сервиса, трей, deploy80).
//
// Ключ — от пути к data, поэтому инспекционная копия (свой каталог) боевому не мешает.
public static class InstanceLock
{
    public static Mutex? TryAcquireInstance(string dataDir) =>
        TryAcquire($"Global\\ccs-instance-{KeyFor(dataDir)}");

    public static Mutex? TryAcquireBackup(string dataDir) =>
        TryAcquire($"Global\\ccs-backup-{KeyFor(dataDir)}");

    /// <summary>
    /// Мьютекс выкатки — ТОТ ЖЕ, что берёт deploy-agent.ps1 на всё время работы (ADR-010).
    /// Имя фиксировано (не от пути к data): агент про каталог data ничего не знает. Взять его
    /// удалось = агента больше нет, и журнал выкатки можно править, не столкнувшись с его записью.
    /// </summary>
    public static Mutex? TryAcquireDeploy() => TryAcquire("Global\\ccs-deploy");

    /// <summary>Занят ли мьютекс инстанса, т.е. работает ли сервер на этом каталоге data.</summary>
    public static bool IsServerRunning(string dataDir)
    {
        var mutex = TryAcquireInstance(dataDir);
        if (mutex is null) return true;

        // Отпускаем в try/catch: проверка не обязана удаваться, а падение здесь роняло бы
        // восстановление ещё до его начала (вызов идёт из BackupRestore до входа в try)
        try { mutex.ReleaseMutex(); } catch { /* не наш мьютекс или уже отпущен */ }
        mutex.Dispose();
        return false;
    }

    /// <summary>Дождаться освобождения мьютекса инстанса (трей: Kill не мгновенный).</summary>
    public static bool WaitUntilFree(string dataDir, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (!IsServerRunning(dataDir)) return true;
            Thread.Sleep(250);
        }
        return !IsServerRunning(dataDir);
    }

    private static Mutex? TryAcquire(string name)
    {
        // Объект создаётся ДО try: при AbandonedMutexException владение уже перешло к нам
        // (в этом смысл исключения), и вернуть надо ИМЕННО ЭТОТ экземпляр. Раньше в catch
        // создавался новый — а `initiallyOwned: true` у существующего именованного мьютекса
        // игнорируется, так что возвращался невладеемый объект, и следующий ReleaseMutex
        // падал с ApplicationException. Сценарий не редкий, а штатный: трей гасит сервер
        // через Kill, то есть мьютекс инстанса остаётся заброшенным ВСЕГДА.
        Mutex? mutex = null;
        try
        {
            mutex = new Mutex(initiallyOwned: false, name);
            if (mutex.WaitOne(TimeSpan.Zero))
                return mutex;
            mutex.Dispose();
            return null;
        }
        catch (AbandonedMutexException)
        {
            // Прошлый владелец умер, не отпустив мьютекс: владение перешло к нам
            return mutex;
        }
        catch (UnauthorizedAccessException)
        {
            // Разные уровни целостности процессов (деплой из-под администратора против
            // сервера из «Автозагрузки») — объект существует, но открыть его нам не дают.
            // Значит кто-то мьютекс держит: считаем занятым, а не падаем.
            mutex?.Dispose();
            return null;
        }
        catch (Exception)
        {
            mutex?.Dispose();
            return null;
        }
    }

    private static string KeyFor(string dataDir)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataDir)).ToLowerInvariant();
        var hash = SHA1.HashData(Encoding.UTF8.GetBytes(full));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }
}
