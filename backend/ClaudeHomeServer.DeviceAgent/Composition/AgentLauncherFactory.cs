using System.Diagnostics;
using ClaudeHomeServer.Services.Execution;

namespace ClaudeHomeServer.DeviceAgent.Composition;

/// <summary>
/// Среда исполнения вертикалей в агенте: одна и та же — машина пользователя, песочницы и
/// маппинга путей нет. Нужна <c>GitService</c>: он запускает git через
/// <see cref="ILauncherFactory"/>, как на сервере. CLI хода здесь не запускается — у ходов
/// свой исполнитель (<c>Exec/TurnExecutor</c>) с изоляцией группы процессов.
/// </summary>
internal sealed class AgentLauncherFactory : ILauncherFactory
{
    public static readonly AgentLauncherFactory Instance = new();

    public IProcessLauncher Local { get; } = new AgentProcessLauncher();
    public IProcessLauncher ForOwner(string? ownerId) => Local;
    // Агент исполняет всё у себя: проект, пришедший на устройство, живёт здесь же
    public IProcessLauncher ForProject(Models.Project project) => Local;

    private sealed class AgentProcessLauncher : IProcessLauncher
    {
        public bool IsSandboxed => false;
        public bool TargetIsWindows => OperatingSystem.IsWindows();
        public IPathMapper Paths => IdentityPathMapper.Instance;
        public string ClaudeCliCommand => throw new NotSupportedException("CLI хода агент запускает своим исполнителем");
        public string HostTempDir => Path.GetTempPath();
        public string? McpApiUrlOverride => null;

        public Process Start(ProcessSpec spec)
        {
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
            if (spec.RawArguments is not null) psi.Arguments = spec.RawArguments;
            else foreach (var a in spec.Args) psi.ArgumentList.Add(a);
            foreach (var key in spec.ClearEnv ?? []) psi.Environment.Remove(key);
            foreach (var (k, v) in spec.Env ?? new Dictionary<string, string>()) psi.Environment[k] = v;

            var process = new Process { StartInfo = psi, EnableRaisingEvents = spec.EnableRaisingEvents };
            process.Start();
            return process;
        }

        public void Kill(Process process, string? turnId = null)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { /* уже вышел */ }
        }

        public int EstimateCommandLineLength(ProcessSpec spec) =>
            spec.FileName.Length + (spec.RawArguments?.Length ?? spec.Args.Sum(a => a.Length + 3));
    }
}
