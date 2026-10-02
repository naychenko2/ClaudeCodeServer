using System.Diagnostics;
using System.IO.Pipes;
using Microsoft.Win32;

namespace BrowserHandsProbe;

/// <summary>Наблюдение за выходом процесса по хэндлу, открытому пока процесс жив (PID не переиспользуется).</summary>
sealed class ProcWatch : IDisposable
{
    readonly IntPtr _handle;      // Windows
    readonly Process? _process;   // Linux

    ProcWatch(IntPtr handle, Process? process)
    {
        _handle = handle;
        _process = process;
    }

    public static ProcWatch FromHandle(IntPtr h) => new(h, null);

    public static ProcWatch FromProcess(Process p) => new(IntPtr.Zero, p);

    public static ProcWatch? Open(int pid)
    {
        if (OperatingSystem.IsWindows())
        {
            var h = Win32.OpenForWait(pid);
            return h == IntPtr.Zero ? null : new ProcWatch(h, null);
        }
        try
        {
            var p = Process.GetProcessById(pid);
            _ = p.StartTime;
            return new ProcWatch(IntPtr.Zero, p);
        }
        catch
        {
            return null;
        }
    }

    public bool HasExited => WaitExit(0);

    public bool WaitExit(int ms)
    {
        if (_process != null) return _process.WaitForExit(ms);
        return OperatingSystem.IsWindows() && Win32.WaitExit(_handle, ms);
    }

    public int? ExitCode
    {
        get
        {
            if (_process != null)
            {
                try { return _process.HasExited ? _process.ExitCode : null; }
                catch (InvalidOperationException) { return null; }
            }
            return OperatingSystem.IsWindows() ? Win32.ExitCode(_handle) : null;
        }
    }

    public void Kill()
    {
        try
        {
            if (_process != null) _process.Kill();
        }
        catch { }
    }

    public void Dispose()
    {
        _process?.Dispose();
        if (OperatingSystem.IsWindows()) Win32.CloseHandleSafe(_handle);
    }
}

sealed record Tracked(int Pid, int Ppid, string Exe, string Kind, ProcWatch? Watch)
{
    public bool Alive
    {
        get
        {
            if (Watch != null) return !Watch.HasExited;
            // Хэндл открыть не дали — сверяемся со снимком по паре PID + имя.
            return ProcessTree.Snapshot().Any(p => p.Pid == Pid && p.Exe == Exe);
        }
    }
}

static class ProcessTree
{
    public static List<(int Pid, int Ppid, string Exe)> Snapshot()
    {
        if (OperatingSystem.IsWindows()) return Win32.SnapshotProcesses();
        var list = new List<(int, int, string)>();
        foreach (var dir in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(dir), out int pid)) continue;
            try
            {
                var stat = File.ReadAllText(Path.Combine(dir, "stat"));
                var tail = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
                var comm = File.ReadAllText(Path.Combine(dir, "comm")).Trim();
                list.Add((pid, int.Parse(tail[1]), comm));
            }
            catch { }
        }
        return list;
    }

    static string? CommandLine(int pid)
    {
        if (OperatingSystem.IsWindows()) return Win32.CommandLine(pid);
        try { return File.ReadAllText($"/proc/{pid}/cmdline").Replace('\0', ' '); }
        catch { return null; }
    }

    /// <summary>Корень и все потомки по родительским PID, с типом процесса Chrome (--type=...).</summary>
    public static List<Tracked> Descendants(int rootPid)
    {
        var snap = Snapshot();
        var result = new List<Tracked>();
        var queue = new Queue<int>([rootPid]);
        var seen = new HashSet<int>();
        while (queue.Count > 0)
        {
            int pid = queue.Dequeue();
            if (!seen.Add(pid)) continue;
            var entry = snap.FirstOrDefault(p => p.Pid == pid);
            if (entry.Exe == null) continue;
            var cmd = CommandLine(pid);
            string kind = cmd == null ? "?" : KindOf(cmd, pid == rootPid);
            result.Add(new Tracked(pid, entry.Ppid, entry.Exe, kind, ProcWatch.Open(pid)));
            foreach (var child in snap.Where(p => p.Ppid == pid && p.Pid != pid)) queue.Enqueue(child.Pid);
        }
        return result;
    }

    static string KindOf(string cmd, bool isRoot)
    {
        const string key = "--type=";
        int i = cmd.IndexOf(key, StringComparison.Ordinal);
        if (i < 0) return isRoot ? "browser" : "(без --type)";
        int end = cmd.IndexOfAny([' ', '"'], i + key.Length);
        return cmd[(i + key.Length)..(end < 0 ? cmd.Length : end)];
    }

    public static string Describe(IEnumerable<Tracked> list) =>
        string.Join(", ", list.GroupBy(t => $"{t.Exe} {t.Kind}").OrderBy(g => g.Key).Select(g => $"{g.Key} ×{g.Count()}"));
}

sealed class BrowserInstance : IDisposable
{
    public required int Pid { get; init; }
    public required ProcWatch Proc { get; init; }
    public required CdpClient Cdp { get; init; }
    public required string Profile { get; init; }
    public required string LogFile { get; init; }
    public IntPtr Job { get; init; }
    public string PipeInfo { get; init; } = "";

    readonly Dictionary<int, Tracked> _seen = [];

    /// <summary>Снять дерево сейчас и запомнить всех, кого видели (для уборки сирот).</summary>
    public List<Tracked> Track()
    {
        var tree = ProcessTree.Descendants(Pid);
        foreach (var t in tree) _seen.TryAdd(t.Pid, t);
        return tree;
    }

    public IEnumerable<Tracked> Seen => _seen.Values;

    /// <summary>Гасит всё, что осталось: Job целиком или каждый увиденный процесс.</summary>
    public void KillAll()
    {
        if (!Proc.HasExited) Track();
        if (Job != IntPtr.Zero && OperatingSystem.IsWindows())
        {
            try { Win32.TerminateJob(Job); } catch { }
        }
        foreach (var t in _seen.Values.Where(t => t.Alive))
        {
            try { Process.GetProcessById(t.Pid).Kill(); } catch { }
        }
        if (!Proc.HasExited)
        {
            try { Process.GetProcessById(Pid).Kill(); } catch { }
        }
    }

    public void Dispose()
    {
        Cdp.Dispose();
        foreach (var t in _seen.Values) t.Watch?.Dispose();
        Proc.Dispose();
        if (OperatingSystem.IsWindows()) Win32.CloseHandleSafe(Job);
    }
}

static class Launcher
{
    public static string ChromePath { get; set; } = "";
    public static bool Headless { get; set; }
    public static readonly List<BrowserInstance> All = [];

    public static BrowserInstance Launch(string profile, string logFile, string url, bool inJob)
    {
        var common = new List<string>
        {
            "--remote-debugging-pipe",
            $"--user-data-dir={profile}",
            "--no-first-run",
            "--no-default-browser-check",
            "--enable-logging",
            $"--log-file={logFile}",
            "--v=0",
        };
        if (Headless) common.Add("--headless");

        var b = OperatingSystem.IsWindows()
            ? LaunchWindows(common, profile, logFile, url, inJob)
            : LaunchUnix(common, profile, logFile, url);
        lock (All) All.Add(b);
        return b;
    }

    static BrowserInstance LaunchWindows(List<string> common, string profile, string logFile, string url, bool inJob)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        // Chrome читает первую трубу и пишет во вторую (AdoptPipes в devtools_agent_host_impl.cc).
        var toChrome = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable);
        var fromChrome = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        string pipes = $"{toChrome.GetClientHandleAsString()},{fromChrome.GetClientHandleAsString()}";
        var args = new List<string>(common) { $"--remote-debugging-io-pipes={pipes}", url };
        string cmd = Quote(ChromePath) + " " + string.Join(" ", args.Select(Quote));

        IntPtr job = inJob ? Win32.CreateKillOnCloseJob() : IntPtr.Zero;
        int pid;
        IntPtr hProcess;
        try
        {
            (pid, hProcess) = Win32.CreateProcessInherit(cmd, job);
        }
        catch
        {
            Win32.CloseHandleSafe(job);
            toChrome.Dispose();
            fromChrome.Dispose();
            throw;
        }
        // Без этого EOF на нашей стороне не наступит никогда: копия пишущего конца Chrome жила бы у нас.
        toChrome.DisposeLocalCopyOfClientHandle();
        fromChrome.DisposeLocalCopyOfClientHandle();

        return new BrowserInstance
        {
            Pid = pid,
            Proc = ProcWatch.FromHandle(hProcess),
            Cdp = new CdpClient(toChrome, fromChrome),
            Profile = profile,
            LogFile = logFile,
            Job = job,
            PipeInfo = $"--remote-debugging-io-pipes={pipes}",
        };
    }

    /// <summary>Linux — только для самопроверки CDP-клиента: fd 3/4 через именованные FIFO.</summary>
    static BrowserInstance LaunchUnix(List<string> common, string profile, string logFile, string url)
    {
        string stem = profile + "-" + Guid.NewGuid().ToString("N")[..6];
        string inFifo = stem + ".in", outFifo = stem + ".out";
        foreach (var f in new[] { inFifo, outFifo })
            Process.Start("mkfifo", [f])!.WaitForExit();
        var psi = new ProcessStartInfo("/bin/sh") { UseShellExecute = false };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("exec \"$0\" \"$@\" 3<\"$CDP_IN\" 4>\"$CDP_OUT\" 2>/dev/null");
        psi.ArgumentList.Add(ChromePath);
        foreach (var a in common) psi.ArgumentList.Add(a);
        psi.ArgumentList.Add(url);
        psi.Environment["CDP_IN"] = inFifo;
        psi.Environment["CDP_OUT"] = outFifo;
        var p = Process.Start(psi)!;
        var w = Task.Run(() => (Stream)new FileStream(inFifo, FileMode.Open, FileAccess.Write));
        var r = Task.Run(() => (Stream)new FileStream(outFifo, FileMode.Open, FileAccess.Read));
        Task.WaitAll(w, r);
        return new BrowserInstance
        {
            Pid = p.Id,
            Proc = ProcWatch.FromProcess(p),
            Cdp = new CdpClient(w.Result, r.Result),
            Profile = profile,
            LogFile = logFile,
            PipeInfo = "fd 3/4 (FIFO)",
        };
    }

    static string Quote(string s) => s.Contains(' ') || s.Contains('\t') ? "\"" + s + "\"" : s;

    public static string? FindChrome()
    {
        var env = Environment.GetEnvironmentVariable("CHROME_PATH");
        if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;
        if (!OperatingSystem.IsWindows())
            return new[] { "/usr/bin/google-chrome", "/usr/bin/google-chrome-stable", "/opt/google/chrome/chrome" }
                .FirstOrDefault(File.Exists);

        var candidates = new List<string>();
        foreach (var root in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        {
            try
            {
                using var key = RegistryKey.OpenBaseKey(root, RegistryView.Default)
                    .OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\chrome.exe");
                if (key?.GetValue(null) is string path) candidates.Add(path.Trim('"'));
            }
            catch { }
        }
        foreach (var envVar in new[] { "ProgramFiles", "ProgramFiles(x86)", "LOCALAPPDATA" })
        {
            var dir = Environment.GetEnvironmentVariable(envVar);
            if (!string.IsNullOrEmpty(dir))
                candidates.Add(Path.Combine(dir, "Google", "Chrome", "Application", "chrome.exe"));
        }
        return candidates.FirstOrDefault(File.Exists);
    }

    public static string? ChromeFileVersion(string path)
    {
        try { return FileVersionInfo.GetVersionInfo(path).ProductVersion; }
        catch { return null; }
    }

    /// <summary>Занят ли профиль живым Chrome — проверка ДО запуска, без обращения к браузеру.</summary>
    public static (bool Busy, string How) ProfileLock(string profile)
    {
        if (!OperatingSystem.IsWindows())
        {
            var link = Path.Combine(profile, "SingletonLock");
            var target = new FileInfo(link).LinkTarget;
            return target == null ? (false, "SingletonLock нет") : (true, $"SingletonLock → {target}");
        }

        string lockState;
        bool lockBusy;
        var lockFile = Path.Combine(profile, "lockfile");
        if (!File.Exists(lockFile))
        {
            lockBusy = false;
            lockState = "lockfile нет";
        }
        else
        {
            try
            {
                using var fs = new FileStream(lockFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                lockBusy = false;
                lockState = "lockfile есть, открылся эксклюзивно";
            }
            catch (IOException ex)
            {
                lockBusy = true;
                lockState = $"lockfile держат: {ex.Message.Trim()} (HResult 0x{ex.HResult:X8})";
            }
            catch (UnauthorizedAccessException ex)
            {
                lockBusy = true;
                lockState = $"lockfile недоступен: {ex.Message.Trim()}";
            }
        }
        bool window = Win32.ChromeMessageWindowExists(profile);
        return (lockBusy || window, $"{lockState}; окно Chrome_MessageWindow «{profile}»: {(window ? "есть" : "нет")}");
    }
}
