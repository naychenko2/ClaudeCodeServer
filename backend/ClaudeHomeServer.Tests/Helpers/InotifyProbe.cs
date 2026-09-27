using System.Runtime.InteropServices;

namespace ClaudeHomeServer.Tests.Helpers;

// Замеры inotify для регрессий утечки (инцидент 2026-09-19). Только Linux.
// Лимиты inotify считаются на пользователя ОС: утечка в тестовом процессе бьёт и по
// соседним процессам того же пользователя (на машине разработчика — по продовому
// инстансу), поэтому опасные сценарии гоняются под потолком дескрипторов процесса
// (WithFdLimit): EMFILE обрывает шторм, не выходя за пределы теста.
internal static class InotifyProbe
{
    // Число открытых inotify-экземпляров процесса.
    public static int CountInotifyFds()
    {
        var n = 0;
        foreach (var fd in new DirectoryInfo("/proc/self/fd").EnumerateFileSystemInfos())
        {
            try { if (fd.LinkTarget == "anon_inode:inotify") n++; }
            catch { /* fd закрылся между перечислением и чтением ссылки */ }
        }
        return n;
    }

    public static int CountOpenFds() => new DirectoryInfo("/proc/self/fd").EnumerateFileSystemInfos().Count();

    // Число inotify-СЛЕЖЕК процесса (не экземпляров): на каждую слежку ядро печатает
    // в /proc/self/fdinfo/<fd> экземпляра строку «inotify wd:<номер> …». Лимит
    // fs.inotify.max_user_watches считается именно по ним и общий на пользователя ОС.
    public static int CountWatches()
    {
        var n = 0;
        foreach (var fd in InotifyFdNumbers())
        {
            try
            {
                foreach (var line in File.ReadLines($"/proc/self/fdinfo/{fd}"))
                    if (line.StartsWith("inotify wd:", StringComparison.Ordinal)) n++;
            }
            catch { /* экземпляр закрылся между перечислением и чтением */ }
        }
        return n;
    }

    // Ждём, пока число inotify-fd не опустится до limit (закрытие асинхронно — на потоке
    // чтения .NET). Возвращает последнее замеренное значение.
    public static async Task<int> WaitInotifyAtMost(int limit, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        int count;
        while ((count = CountInotifyFds()) > limit && DateTime.UtcNow < deadline)
            await Task.Delay(50);
        return count;
    }

    // Прогрев до урезания лимита: загрузка сборки наблюдателя и нативной обвязки сама
    // открывает файлы, и под потолком fd тест упал бы на FileNotFoundException, а не на
    // проверяемом месте.
    public static void WarmUp(string dir)
    {
        Directory.CreateDirectory(dir);
        using var w = new FileSystemWatcher(dir) { IncludeSubdirectories = true };
        w.Error += (_, _) => { };
        w.EnableRaisingEvents = true;
        _ = LowestFreeFd();
        _ = InotifyFdNumbers();
        _ = CountInotifyFds();
    }

    // Номера открытых inotify-fd процесса.
    public static HashSet<int> InotifyFdNumbers()
    {
        var set = new HashSet<int>();
        foreach (var fd in new DirectoryInfo("/proc/self/fd").EnumerateFileSystemInfos())
        {
            try { if (fd.LinkTarget == "anon_inode:inotify") set.Add(int.Parse(fd.Name)); }
            catch { /* fd закрылся между перечислением и чтением ссылки */ }
        }
        return set;
    }

    // Наименьший свободный номер дескриптора: ядро выдаёт новый fd именно с него.
    public static int LowestFreeFd()
    {
        var used = new DirectoryInfo("/proc/self/fd").EnumerateFileSystemInfos()
            .Select(f => int.TryParse(f.Name, out var n) ? n : -1).ToHashSet();
        var i = 0;
        while (used.Contains(i)) i++;
        return i;
    }

    // Наибольший занятый номер дескриптора: всё выше него свободно.
    public static int HighestFd() =>
        new DirectoryInfo("/proc/self/fd").EnumerateFileSystemInfos()
            .Select(f => int.TryParse(f.Name, out var n) ? n : -1).Max();

    // Мягкий лимит дескрипторов процесса на время action. RLIMIT_NOFILE ограничивает НОМЕР
    // нового fd (он обязан быть меньше лимита), а не их число: при дырах в нумерации лимит
    // «открытые + N» ничего не запрещает. Поэтому вызывающий даёт сам потолок номера.
    // Процесс-глобально: на время action НИКТО в процессе (включая раннер xunit и соседние
    // потоки) не откроет fd с номером >= limit, поэтому вызывающие классы обязаны сидеть в
    // коллекции TestCollections.Inotify (без параллелизма), а action — быть коротким.
    // limit <= 0 запрещён: он не нужен ни одному сценарию и запирает вообще любые новые fd.
    // Прежний лимит возвращается в finally при любом исходе action; не вернулся — бросок,
    // а не тихое продолжение прогона под урезанным лимитом.
    public static async Task WithFdLimit(int limit, Func<Task> action)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        if (getrlimit(RlimitNofile, out var saved) != 0)
            throw new InvalidOperationException("getrlimit(RLIMIT_NOFILE) не удался");
        var capped = saved with { Cur = Math.Min((ulong)limit, saved.Cur) };
        if (setrlimit(RlimitNofile, in capped) != 0)
            throw new InvalidOperationException("setrlimit(RLIMIT_NOFILE) не удался");
        try { await action(); }
        finally
        {
            if (setrlimit(RlimitNofile, in saved) != 0)
                throw new InvalidOperationException("не удалось вернуть RLIMIT_NOFILE — прогон дальше идёт под урезанным лимитом");
        }
    }

    private const int RlimitNofile = 7; // RLIMIT_NOFILE на Linux (x64 и arm64)

    [StructLayout(LayoutKind.Sequential)]
    private record struct RLimit(ulong Cur, ulong Max);

    [DllImport("libc", SetLastError = true)]
    private static extern int getrlimit(int resource, out RLimit rlim);

    [DllImport("libc", SetLastError = true)]
    private static extern int setrlimit(int resource, in RLimit rlim);
}
