using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;

namespace ClaudeHomeServer.Services.Execution;

/// <summary>
/// Реестр процессов, запущенных сервером. Отслеживает PID'ы всех порождённых процессов,
/// при старте чистит сирот от предыдущего запуска (краш/форс-килл), при штатном останове
/// убивает всё дерево. Защита от накопления — на Windows дочерние процессы не умирают
/// автоматически при смерти родителя.
///
/// Хранит «паспорт» (PID + имя + время старта), а НЕ объект <see cref="Process"/>:
/// объект держал бы хэндл ОС до конца жизни сервера (записи отсюда не вычёркивались),
/// а его освобождение из реестра выдёргивало бы процесс из-под владельца, который в этот
/// момент читает результат. Время старта нужно потому, что номера процессов ОС
/// переиспользует: без него протухшая запись блокировала бы учёт нового процесса
/// с тем же номером, а при остановке под нож попал бы чужой процесс, занявший номер.
/// </summary>
public static class ProcessRegistry
{
    // Паспорт процесса. StartedAt == DateTime.MinValue — время недоступно (нет прав
    // на чтение у чужого процесса): тогда сверяем только по имени.
    internal sealed record TrackedProcess(int Pid, string Name, DateTime StartedAt);

    private static readonly ConcurrentDictionary<int, TrackedProcess> _tracked = new();
    private static readonly string _pidFile;
    private static bool _initialized;

    // Запись PID-файла — не на каждую регистрацию: процессы стартуют пачками,
    // а файл нужен лишь следующему запуску сервера, отставание на секунду безвредно.
    private const int PersistThrottleMs = 1000;
    private static readonly object _persistGate = new();
    private static DateTime _lastPersist = DateTime.MinValue;
    private static Timer? _persistTimer;

    static ProcessRegistry()
    {
        var dir = Path.Combine(
            Path.GetDirectoryName(typeof(ProcessRegistry).Assembly.Location) ?? ".",
            "data");
        _pidFile = Path.Combine(dir, "server-pids.txt");
    }

    /// <summary>
    /// Убить сирот от предыдущего запуска сервера и начать чистый трекинг.
    /// Идемпотентно — повторные вызовы no-op. Вызывать при старте приложения.
    /// </summary>
    public static void Initialize()
    {
        if (_initialized) return;
        _initialized = true;

        KillOrphansFromFile();
        PersistPids();
    }

    /// <summary>Зарегистрировать процесс, запущенный сервером.</summary>
    public static void Register(Process process)
    {
        if (process is null) return;
        var entry = Describe(process);
        if (entry is null) return; // процесс уже завершился — учитывать нечего

        // Присваивание, а не TryAdd: запись под тем же номером могла остаться
        // от давно умершего процесса, и она должна уступить живому.
        _tracked[entry.Pid] = entry;
        SchedulePersist();
    }

    /// <summary>Снять процесс с учёта (штатно завершён/убит).</summary>
    public static void Unregister(Process process)
    {
        if (process is null) return;
        int pid;
        try { pid = process.Id; }
        catch (InvalidOperationException) { return; } // объект уже освобождён
        if (_tracked.TryRemove(pid, out _))
            SchedulePersist();
    }

    /// <summary>Убить все отслеженные процессы и очистить реестр (graceful shutdown).</summary>
    public static void KillAll()
    {
        foreach (var (pid, entry) in _tracked)
        {
            try
            {
                // Свой объект — свой Dispose. Хэндл владельца процесса не трогаем.
                using var proc = Process.GetProcessById(pid);
                if (!Matches(entry, proc)) continue; // номер уже занят чужим — не наш клиент
                if (!proc.HasExited)
                    proc.Kill(entireProcessTree: true);
            }
            catch (ArgumentException) { /* процесс уже умер */ }
            catch (Exception) { /* нет доступа или гонка — остановку не роняем */ }
        }
        _tracked.Clear();
        StopPersistTimer();
        DeletePidFile();
    }

    // ---------- Паспорт и сверка ----------

    // null — процесс уже завершился (Id/имя недоступны). Время старта может быть
    // недоступно отдельно (Win32Exception) — тогда MinValue.
    private static TrackedProcess? Describe(Process process)
    {
        try
        {
            var pid = process.Id;
            var name = process.ProcessName;
            DateTime started;
            try { started = process.StartTime; }
            catch (Exception) { started = DateTime.MinValue; }
            return new TrackedProcess(pid, name, started);
        }
        catch (InvalidOperationException) { return null; }
        catch (Win32Exception) { return null; }
    }

    // Тот ли это процесс, что мы записывали, или номер уже переиспользован ОС.
    internal static bool Matches(TrackedProcess entry, Process actual)
    {
        string name;
        DateTime started;
        try
        {
            name = actual.ProcessName;
            try { started = actual.StartTime; }
            catch (Exception) { started = DateTime.MinValue; }
        }
        catch (Exception) { return false; }

        if (!string.Equals(name, entry.Name, StringComparison.OrdinalIgnoreCase)) return false;
        // Время старта неизвестно с одной из сторон — довольствуемся совпадением имени
        if (entry.StartedAt == DateTime.MinValue || started == DateTime.MinValue) return true;
        // ОС округляет время старта по-разному на разных платформах — сверяем с допуском
        return Math.Abs((started - entry.StartedAt).TotalMilliseconds) < 1000;
    }

    // Вычёркиваем завершившиеся: иначе реестр рос бы всё время жизни сервера.
    // Зовётся при записи PID-файла — там мы и так обходим весь список.
    internal static void PruneDead()
    {
        foreach (var (pid, entry) in _tracked)
        {
            if (IsAlive(pid, entry)) continue;
            _tracked.TryRemove(pid, out _);
        }
    }

    private static bool IsAlive(int pid, TrackedProcess entry)
    {
        try
        {
            using var proc = Process.GetProcessById(pid);
            return Matches(entry, proc) && !proc.HasExited;
        }
        catch (ArgumentException) { return false; } // такого PID больше нет
        catch (Exception) { return true; }          // нет доступа — считаем живым, не теряем учёт
    }

    // ---------- PID-файл ----------

    private static void KillOrphansFromFile()
    {
        if (!File.Exists(_pidFile)) return;

        try
        {
            var lines = File.ReadAllLines(_pidFile);
            foreach (var line in lines)
            {
                if (!int.TryParse(line.Trim(), out var pid)) continue;
                try
                {
                    using var proc = Process.GetProcessById(pid);
                    var name = proc.ProcessName.ToLowerInvariant();
                    // Только claude и node — наши рабочие процессы. Остальные
                    // (docker, pwsh терминалов) не трогаем: они могли остаться
                    // от другого экземпляра или пользовательской сессии
                    if (name is "claude" or "node")
                    {
                        try { proc.Kill(entireProcessTree: true); }
                        catch { /* процесс уже завершился */ }
                    }
                }
                catch (ArgumentException) { /* PID больше не существует */ }
                catch (InvalidOperationException) { /* нет доступа */ }
            }
        }
        catch (Exception ex)
        {
            // Файл мог быть битым или заблокирован — не фатально
            Console.Error.WriteLine($"[ProcessRegistry] Ошибка зачистки сирот: {ex.Message}");
        }
    }

    // Записать файл сразу, если с прошлой записи прошло достаточно времени; иначе
    // отложить одним таймером — чтобы пачка запусков не выливалась в пачку записей.
    private static void SchedulePersist()
    {
        lock (_persistGate)
        {
            var elapsed = DateTime.UtcNow - _lastPersist;
            if (elapsed.TotalMilliseconds >= PersistThrottleMs)
            {
                PersistPidsLocked();
                return;
            }
            if (_persistTimer is not null) return; // запись уже запланирована

            var delay = PersistThrottleMs - (int)elapsed.TotalMilliseconds;
            _persistTimer = new Timer(_ =>
            {
                lock (_persistGate)
                {
                    StopPersistTimerLocked();
                    PersistPidsLocked();
                }
            }, null, delay, Timeout.Infinite);
        }
    }

    private static void PersistPids()
    {
        lock (_persistGate) PersistPidsLocked();
    }

    private static void PersistPidsLocked()
    {
        _lastPersist = DateTime.UtcNow;
        PruneDead();
        try
        {
            var dir = Path.GetDirectoryName(_pidFile)!;
            Directory.CreateDirectory(dir);
            // PID текущего процесса + все отслеженные
            var pids = new HashSet<int> { Environment.ProcessId };
            foreach (var (id, _) in _tracked) pids.Add(id);
            File.WriteAllLines(_pidFile, pids.Select(p => p.ToString()));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ProcessRegistry] Не удалось записать PID-файл: {ex.Message}");
        }
    }

    private static void StopPersistTimer()
    {
        lock (_persistGate) StopPersistTimerLocked();
    }

    private static void StopPersistTimerLocked()
    {
        _persistTimer?.Dispose();
        _persistTimer = null;
    }

    private static void DeletePidFile()
    {
        try { File.Delete(_pidFile); }
        catch { /* не критично */ }
    }

    // ---------- Для тестов ----------

    internal static bool IsTracked(int pid) => _tracked.ContainsKey(pid);
    internal static void TrackForTests(TrackedProcess entry) => _tracked[entry.Pid] = entry;
    internal static bool TryGetTracked(int pid, out TrackedProcess? entry) =>
        _tracked.TryGetValue(pid, out entry);
}
