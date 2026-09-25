using System.Collections.Concurrent;
using System.Diagnostics;
using ClaudeHomeServer.DeviceAgent.Processes;
using ClaudeHomeServer.Services.Execution;

namespace ClaudeHomeServer.DeviceAgent.Composition;

/// <summary>
/// Среда исполнения вертикалей в агенте: одна и та же — машина пользователя, песочницы и
/// маппинга путей нет. Через неё git (<c>GitService</c>), терминал (<c>TerminalService</c>) и
/// дев-серверы (<c>DevServerService</c>) запускают процессы так же, как на сервере. CLI хода
/// здесь не запускается — у ходов свой исполнитель (<c>Exec/TurnExecutor</c>).
///
/// Долгоживущие процессы (<see cref="ProcessSpec.Track"/>: терминал, дев-сервер) живут как
/// ход (задача 4.3, механизм 2.2): Unix — своя сессия и группа через <c>setsid</c>, kill бьёт
/// группу; Windows — свой Job Object. Каждый пишется в журнал ходов: агент, убитый не
/// штатно, добьёт их при следующем старте. Короткий git (<c>Track = false</c>) идёт как есть.
/// </summary>
internal sealed class AgentLauncherFactory : ILauncherFactory
{
    public static readonly AgentLauncherFactory Instance = new();

    private readonly AgentProcessLauncher _local;

    public AgentLauncherFactory(TurnJournal? journal = null) => _local = new AgentProcessLauncher(journal);

    public IProcessLauncher Local => _local;
    public IProcessLauncher ForOwner(string? ownerId) => _local;
    // Агент исполняет всё у себя: проект, пришедший на устройство, живёт здесь же
    public IProcessLauncher ForProject(Models.Project project) => _local;

    /// <summary>Добить все живые долгоживущие процессы: штатная остановка агента.</summary>
    public void KillAll() => _local.KillAll();

    /// <summary>Число живых долгоживущих процессов (для тестов).</summary>
    internal int TrackedCount => _local.TrackedCount;

    private sealed class AgentProcessLauncher(TurnJournal? journal) : IProcessLauncher
    {
        private readonly ConcurrentDictionary<int, Tracked> _tracked = new();

        public bool IsSandboxed => false;
        public bool TargetIsWindows => OperatingSystem.IsWindows();
        public IPathMapper Paths => IdentityPathMapper.Instance;
        public string ClaudeCliCommand => throw new NotSupportedException("CLI хода агент запускает своим исполнителем");
        public string HostTempDir => Path.GetTempPath();
        public string? McpApiUrlOverride => null;

        public int TrackedCount => _tracked.Count;

        public Process Start(ProcessSpec spec)
        {
            var grouped = spec.Track;
            var psi = new ProcessStartInfo(spec.FileName)
            {
                WorkingDirectory = spec.WorkingDirectory ?? "",
                RedirectStandardInput = spec.RedirectStdin,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = spec.StdioEncoding,
                StandardErrorEncoding = spec.StdioEncoding,
                StandardInputEncoding = spec.RedirectStdin ? spec.StdioEncoding : null,
            };
            if (grouped && !OperatingSystem.IsWindows())
            {
                // setsid без -f не форкается (дочерний процесс .NET не лидер группы): PID
                // процесса и есть PID команды, а группа — её собственная
                psi.FileName = UnixGroupProcess.FindSetsid() ?? throw new InvalidOperationException(
                    "не найден setsid (util-linux): без своей группы процессов терминал и дев-сервер нельзя убить целиком");
                if (spec.RawArguments is not null) psi.Arguments = $"\"{spec.FileName}\" {spec.RawArguments}";
                else psi.ArgumentList.Add(spec.FileName);
            }
            else if (spec.RawArguments is not null) psi.Arguments = spec.RawArguments;
            if (spec.RawArguments is null)
                foreach (var a in spec.Args) psi.ArgumentList.Add(a);
            foreach (var key in spec.ClearEnv ?? []) psi.Environment.Remove(key);
            foreach (var (k, v) in spec.Env ?? new Dictionary<string, string>()) psi.Environment[k] = v;

            var process = new Process { StartInfo = psi, EnableRaisingEvents = spec.EnableRaisingEvents };
            process.Start();
            // Без TurnId ключ записи — сам процесс: зачистка при старте сверяет pid и время
            // старта, а не ключ, так что такой процесс она находит и добивает так же
            if (grouped) Track(process, spec.TurnId ?? $"proc-{process.Id}");
            return process;
        }

        private void Track(Process process, string turnId)
        {
            IDisposable? job = null;
            Func<bool>? terminate = null;
            if (OperatingSystem.IsWindows())
            {
                // Остаточное окно то же, что у хода: потомок, рождённый до посадки в job, её
                // минует — его добивает запасной kill дерева
                try
                {
                    var windowsJob = WindowsJobProcess.AttachToNewJob(process);
                    (job, terminate) = (windowsJob, windowsJob.Terminate);
                }
                catch
                {
                    try { process.Kill(entireProcessTree: true); } catch (Exception) { }
                    throw;
                }
            }

            var tracked = new Tracked(process, turnId, job, terminate);
            _tracked[process.Id] = tracked;
            try { journal?.Add(new TurnJournal.Entry(turnId, process.Id, StartTimeOf(process), null)); }
            catch (IOException) { /* журнал — страховка от смерти агента, ход без него работает */ }

            // Лидер вышел сам — снять учёт. Группу на Unix после этого не бьём: номер
            // пожатого лидера может достаться чужому процессу-лидеру
            _ = process.WaitForExitAsync().ContinueWith(_ => Forget(tracked), TaskScheduler.Default);
        }

        public void Kill(Process process, string? turnId = null)
        {
            int pid;
            try { pid = process.Id; }
            catch (InvalidOperationException) { return; }

            if (_tracked.TryGetValue(pid, out var tracked) && ReferenceEquals(tracked.Process, process))
            {
                KillTracked(tracked);
                return;
            }
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { /* уже вышел */ }
        }

        public void KillAll()
        {
            foreach (var tracked in _tracked.Values) KillTracked(tracked);
        }

        private void KillTracked(Tracked tracked)
        {
            var process = tracked.Process;
            if (OperatingSystem.IsWindows())
            {
                var terminated = tracked.Terminate?.Invoke() == true;
                if (!terminated || !SafeHasExited(process))
                    try { process.Kill(entireProcessTree: true); } catch (Exception) { }
            }
            else
            {
                UnixGroupProcess.KillGroup(tracked.Pid, leaderAlive: () => !SafeHasExited(process));
            }
            Forget(tracked);
        }

        private void Forget(Tracked tracked)
        {
            if (!_tracked.TryRemove(new KeyValuePair<int, Tracked>(tracked.Pid, tracked))) return;
            tracked.Job?.Dispose();
            journal?.Remove(tracked.TurnId);
        }

        private static bool SafeHasExited(Process process)
        {
            try { return process.HasExited; }
            catch (InvalidOperationException) { return true; }
        }

        private static DateTime StartTimeOf(Process process)
        {
            try { return process.StartTime.ToUniversalTime(); }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
            {
                return DateTime.UtcNow;
            }
        }

        public int EstimateCommandLineLength(ProcessSpec spec) =>
            spec.FileName.Length + (spec.RawArguments?.Length ?? spec.Args.Sum(a => a.Length + 3));

        private sealed record Tracked(Process Process, string TurnId, IDisposable? Job, Func<bool>? Terminate)
        {
            public int Pid { get; } = Process.Id;
        }
    }
}
