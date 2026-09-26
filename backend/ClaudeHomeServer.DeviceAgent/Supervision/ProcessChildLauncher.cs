using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using ClaudeHomeServer.DeviceAgent.Processes;

namespace ClaudeHomeServer.DeviceAgent.Supervision;

/// <summary>
/// Боевой запуск дочернего <c>run</c>. Дочерний не переживает супервизор:
/// - Windows — Job Object с KILL_ON_JOB_CLOSE (<see cref="WindowsJobProcess"/>): хэндл
///   держит только супервизор, его смерть закрывает хэндл и гасит всё дерево;
/// - Linux — дочерний сам взводит <see cref="ParentDeathSignal"/> по PID из контракта.
///   Процессы порождаются с одного вечного потока: для ядра «родитель» — поток fork.
/// Вывод дочернего построчно уходит в <paramref name="output"/> (журнал агента).
/// </summary>
internal sealed class ProcessChildLauncher(Action<string> output) : IChildLauncher
{
    private static readonly Lazy<Spawner> SpawnThread = new(() => new Spawner());

    public ISupervisedChild Start(string executable, string workingDirectory, string healthyFile)
    {
        var psi = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add(SupervisorContract.ChildCommand);
        psi.Environment[SupervisorContract.HealthyFileEnv] = healthyFile;
        psi.Environment[SupervisorContract.SupervisorPidEnv] = Environment.ProcessId.ToString();

        var process = SpawnThread.Value.Start(psi);
        JobHandle? job = null;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var attached = WindowsJobProcess.AttachToNewJob(process);
                job = new JobHandle(attached.Terminate, attached.Dispose);
            }
        }
        catch
        {
            try { process.Kill(entireProcessTree: true); } catch (Exception) { }
            process.Dispose();
            throw;
        }
        return new Child(process, job, output);
    }

    private sealed class Child : ISupervisedChild
    {
        private readonly Process _process;
        private readonly JobHandle? _job;
        private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Child(Process process, JobHandle? job, Action<string> output)
        {
            _process = process;
            _job = job;
            Id = process.Id;
            process.OutputDataReceived += (_, e) => { if (e.Data is { Length: > 0 } line) output(line); };
            process.ErrorDataReceived += (_, e) => { if (e.Data is { Length: > 0 } line) output(line); };
            // Выход — по событию процесса, а не WaitForExitAsync: тот ждёт ещё и конца
            // труб вывода, а их могут держать унаследовавшие хэндлы внуки
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) => _exited.TrySetResult(SafeExitCode());
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            if (process.HasExited) _exited.TrySetResult(SafeExitCode());
        }

        public int Id { get; }

        public Task<int> Exited => _exited.Task;

        public async Task StopAsync(TimeSpan grace)
        {
            if (Exited.IsCompleted) return;
            if (!OperatingSystem.IsWindows() && kill(Id, SIGTERM) == 0)
            {
                try
                {
                    await Exited.WaitAsync(grace);
                    return;
                }
                catch (TimeoutException) { }
            }
            Kill();
            try { await Exited.WaitAsync(TimeSpan.FromSeconds(10)); } catch (TimeoutException) { }
        }

        public void Kill()
        {
            if (Exited.IsCompleted) return;
            if (_job is not null && _job.Terminate() && _process.HasExited) return;
            try { _process.Kill(entireProcessTree: true); }
            catch (Exception e) when (e is InvalidOperationException or Win32Exception or NotSupportedException) { }
        }

        public void Dispose()
        {
            _job?.Dispose();
            _process.Dispose();
        }

        private int SafeExitCode()
        {
            try { return _process.ExitCode; }
            catch (InvalidOperationException) { return -1; }
        }
    }

    /// <summary>Job Object дочернего без платформенного типа в полях: Terminate и закрытие хэндла.</summary>
    private sealed record JobHandle(Func<bool> Terminate, Action Dispose);

    /// <summary>Вечный поток порождения процессов (см. грабля <see cref="ParentDeathSignal"/>).</summary>
    private sealed class Spawner
    {
        private readonly BlockingCollection<(ProcessStartInfo Psi, TaskCompletionSource<Process> Result)> _queue = new();

        public Spawner()
        {
            new Thread(Loop) { IsBackground = true, Name = "ai-home-agent spawner" }.Start();
        }

        public Process Start(ProcessStartInfo psi)
        {
            var result = new TaskCompletionSource<Process>(TaskCreationOptions.RunContinuationsAsynchronously);
            _queue.Add((psi, result));
            return result.Task.GetAwaiter().GetResult();
        }

        private void Loop()
        {
            foreach (var (psi, result) in _queue.GetConsumingEnumerable())
            {
                try { result.SetResult(Process.Start(psi) ?? throw new InvalidOperationException("дочерний не стартовал")); }
                catch (Exception e) { result.SetException(e); }
            }
        }
    }

    private const int SIGTERM = 15;

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int sig);
}
