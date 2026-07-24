using System.Collections.Concurrent;
using System.Diagnostics;

namespace ClaudeHomeServer.Services.Execution;

/// <summary>
/// Реестр процессов, запущенных сервером. Отслеживает PID'ы всех порождённых процессов,
/// при старте чистит сирот от предыдущего запуска (краш/форс-килл), при штатном останове
/// убивает всё дерево. Защита от накопления — на Windows дочерние процессы не умирают
/// автоматически при смерти родителя.
/// </summary>
public static class ProcessRegistry
{
    private static readonly ConcurrentDictionary<int, Process> _tracked = new();
    private static readonly string _pidFile;
    private static bool _initialized;

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
        try
        {
            if (_tracked.TryAdd(process.Id, process))
                PersistPids();
        }
        catch (InvalidOperationException) { /* процесс уже завершился — не страшно */ }
    }

    /// <summary>Снять процесс с учёта (штатно завершён/убит).</summary>
    public static void Unregister(Process process)
    {
        if (process is null) return;
        if (_tracked.TryRemove(process.Id, out _))
            PersistPids();
    }

    /// <summary>Убить все отслеженные процессы и очистить реестр (graceful shutdown).</summary>
    public static void KillAll()
    {
        foreach (var (id, proc) in _tracked)
        {
            try
            {
                if (!proc.HasExited)
                    proc.Kill(entireProcessTree: true);
            }
            catch { /* уже мёртв */ }
            proc.Dispose();
        }
        _tracked.Clear();
        DeletePidFile();
    }

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

    private static void PersistPids()
    {
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

    private static void DeletePidFile()
    {
        try { File.Delete(_pidFile); }
        catch { /* не критично */ }
    }
}
