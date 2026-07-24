using System.Diagnostics;

namespace ClaudeHomeServer.Services.Execution;

// Локальная среда: процессы запускаются на машине сервера (историческое поведение).
public sealed class LocalProcessRunner : IProcessLauncher
{
    public static readonly LocalProcessRunner Instance = new();

    public bool IsSandboxed => false;
    public bool TargetIsWindows => OperatingSystem.IsWindows();
    public IPathMapper Paths => IdentityPathMapper.Instance;
    public string ClaudeCliCommand => Llm.Claude.ClaudeCliLocator.FindClaudeExecutable();
    public string HostTempDir => Path.GetTempPath();
    public string? McpApiUrlOverride => null;

    public Process Start(ProcessSpec spec)
    {
        var psi = new ProcessStartInfo
        {
            FileName = spec.FileName,
            UseShellExecute = false,
            RedirectStandardInput = spec.RedirectStdin,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        if (spec.WorkingDirectory is not null)
            psi.WorkingDirectory = spec.WorkingDirectory;
        if (spec.StdioEncoding is { } enc)
        {
            psi.StandardOutputEncoding = enc;
            psi.StandardErrorEncoding = enc;
            if (spec.RedirectStdin) psi.StandardInputEncoding = enc;
        }
        foreach (var a in spec.Args) psi.ArgumentList.Add(a);
        if (spec.Env is not null)
            foreach (var (k, v) in spec.Env) psi.Environment[k] = v;

        var process = new Process { StartInfo = psi, EnableRaisingEvents = spec.EnableRaisingEvents };
        if (!process.Start())
            throw new InvalidOperationException($"Не удалось запустить {spec.FileName}");
        ProcessRegistry.Register(process);
        return process;
    }

    public void Kill(Process process, string? turnId = null)
    {
        // Всё дерево: claude порождает node-процессы MCP-серверов
        try { process.Kill(entireProcessTree: true); }
        catch { /* процесс уже завершился */ }
    }
}
