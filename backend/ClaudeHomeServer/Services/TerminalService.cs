using System.Collections.Concurrent;
using System.Diagnostics;
using ClaudeHomeServer.Hubs;
using ClaudeHomeServer.Protocol;
using Microsoft.AspNetCore.SignalR;

namespace ClaudeHomeServer.Services;

/// <summary>ДТО списка терминалов для фронта.</summary>
public record TerminalInfoDto(string Id, string ProjectId, string Name, string Status, string? Shell);

/// <summary>Экземпляр запущенного терминала.</summary>
internal sealed class TerminalInstance : IDisposable
{
    public string Id { get; }
    public string ProjectId { get; }
    public string Name { get; set; }
    public Process Process { get; }
    public string UserId { get; }
    public int Cols { get; set; }
    public int Rows { get; set; }
    public DateTime LastActivity { get; set; }
    public HashSet<string> ConnectionIds { get; } = new();
    public StreamWriter StdinWriter { get; }
    public string Status { get; set; } = "running";
    public string? Shell { get; }
    public DateTime CreatedAt { get; } = DateTime.UtcNow;

    // Для Windows-фолбэка — запоминаем shell
    public bool IsWindows { get; }

    public TerminalInstance(string id, string projectId, string name, Process process, string userId,
        int cols, int rows, StreamWriter stdinWriter, bool isWindows, string? shell = null)
    {
        Id = id;
        ProjectId = projectId;
        Name = name;
        Process = process;
        UserId = userId;
        Cols = cols;
        Rows = rows;
        LastActivity = DateTime.UtcNow;
        StdinWriter = stdinWriter;
        IsWindows = isWindows;
        Shell = shell;
    }

    public void AddViewer(string connId)   { lock (ConnectionIds) ConnectionIds.Add(connId); LastActivity = DateTime.UtcNow; }
    public void RemoveViewer(string connId) { lock (ConnectionIds) ConnectionIds.Remove(connId); }
    public int ViewerCount { get { lock (ConnectionIds) return ConnectionIds.Count; } }

    public void Dispose()
    {
        StdinWriter.Dispose();
        if (!Process.HasExited)
        {
            try { Process.Kill(entireProcessTree: true); } catch { }
            Process.WaitForExit(3000);
        }
        Process.Dispose();
    }
}

/// <summary>Менеджер терминалов: много на проект, с Windows-фолбэком.</summary>
public sealed class TerminalService : IDisposable
{
    private readonly ConcurrentDictionary<string, TerminalInstance> _terminals = new(); // key = terminalId
    private readonly IHubContext<TerminalHub> _hub;
    private readonly ProjectManager _projects;
    private readonly ILogger<TerminalService> _log;
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly Timer _cleanupTimer;

    private static readonly string PtyBridgePath = "/app/pty-bridge";

    public TerminalService(IHubContext<TerminalHub> hub, ProjectManager projects, ILogger<TerminalService> log)
    {
        _hub = hub;
        _projects = projects;
        _log = log;
        _cleanupTimer = new Timer(_ => CleanupStale(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    /// <summary>Создать и запустить новый терминал в проекте.</summary>
    public async Task<TerminalInfoDto> CreateAsync(string projectId, string userId, string connId,
        int cols, int rows, string? name = null)
    {
        var project = _projects.GetById(projectId)
            ?? throw new HubException("Проект не найден");

        var terminalId = Guid.NewGuid().ToString("N")[..12];
        if (string.IsNullOrWhiteSpace(name))
        {
            var count = _terminals.Values.Count(t => t.ProjectId == projectId) + 1;
            name = $"Терминал {count}";
        }

        var isWindows = OperatingSystem.IsWindows();

        Process process;
        StreamWriter stdin;
        string? shell;

        if (isWindows)
        {
            // Windows: PowerShell с перенаправлением (без PTY)
            shell = "powershell.exe";
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoLogo -NoExit -Command $Host.UI.RawUI.WindowTitle='terminal'",
                WorkingDirectory = project.RootPath,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            process.Start();
            stdin = new StreamWriter(process.StandardInput.BaseStream, Console.OutputEncoding) { AutoFlush = true };
        }
        else
        {
            // Linux: пытаемся pty-bridge, фолбэк на bash с перенаправлением
            shell = "bash";
            if (File.Exists(PtyBridgePath))
            {
                var psi = new ProcessStartInfo
                {
                    FileName = PtyBridgePath,
                    Arguments = $"{cols} {rows}",
                    WorkingDirectory = project.RootPath,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                psi.EnvironmentVariables["TERM"] = "xterm-256color";
                process = new Process { StartInfo = psi, EnableRaisingEvents = true };
                process.Start();
            }
            else
            {
                // Fallback без PTY
                _log.LogWarning("pty-bridge не найден, запуск bash с перенаправлением");
                var psi = new ProcessStartInfo
                {
                    FileName = "/bin/bash",
                    Arguments = "--norc --noediting -i",
                    WorkingDirectory = project.RootPath,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                psi.EnvironmentVariables["TERM"] = "xterm-256color";
                process = new Process { StartInfo = psi, EnableRaisingEvents = true };
                process.Start();
            }
            stdin = new StreamWriter(process.StandardInput.BaseStream, Console.OutputEncoding) { AutoFlush = true };
        }

        var instance = new TerminalInstance(terminalId, projectId, name, process, userId,
            cols, rows, stdin, isWindows, shell);
        instance.AddViewer(connId);
        _terminals[terminalId] = instance;

        _ = ReadStreamAsync(terminalId, process.StandardOutput, false, _shutdownCts.Token);
        _ = ReadStreamAsync(terminalId, process.StandardError, true, _shutdownCts.Token);
        process.Exited += (_, _) => _ = HandleExitedAsync(terminalId, process.ExitCode);

        await GroupsAdd(connId, terminalId);
        await SendToTerminalGroup(terminalId, new TerminalStatusMessage("running", TerminalId: terminalId));
        _log.LogInformation("Терминал {TerminalId} ({Name}) запущен для проекта {ProjectId}",
            terminalId, name, projectId);

        return ToDto(instance);
    }

    /// <summary>Подключиться к существующему терминалу.</summary>
    public async Task<TerminalInfoDto?> ConnectAsync(string terminalId, string userId, string connId)
    {
        if (!_terminals.TryGetValue(terminalId, out var instance))
            return null;
        if (instance.UserId != userId) return null;

        instance.AddViewer(connId);
        await GroupsAdd(connId, terminalId);
        return ToDto(instance);
    }

    /// <summary>Список терминалов проекта.</summary>
    public List<TerminalInfoDto> ListByProject(string projectId)
    {
        return _terminals.Values
            .Where(t => t.ProjectId == projectId)
            .Select(ToDto)
            .OrderBy(t => t.Id)
            .ToList();
    }

    public async Task StopAsync(string terminalId, string userId)
    {
        if (_terminals.TryRemove(terminalId, out var instance))
        {
            if (instance.UserId != userId)
                throw new HubException("Доступ запрещён");
            instance.Status = "stopped";
            instance.Dispose();
            await SendToTerminalGroup(terminalId, new TerminalStatusMessage("stopped", 0, terminalId));
            _log.LogInformation("Терминал {TerminalId} остановлен", terminalId);
        }
    }

    public async Task WriteInputAsync(string terminalId, string data)
    {
        if (_terminals.TryGetValue(terminalId, out var instance))
        {
            instance.LastActivity = DateTime.UtcNow;
            await instance.StdinWriter.WriteAsync(data);
            await instance.StdinWriter.FlushAsync();
        }
    }

    public void Resize(string terminalId, int cols, int rows)
    {
        if (!_terminals.TryGetValue(terminalId, out var instance)) return;
        instance.Cols = cols;
        instance.Rows = rows;

        if (!instance.IsWindows && File.Exists(PtyBridgePath))
        {
            var resizeCmd = new byte[]
            {
                0x1B, (byte)'R',
                (byte)((cols >> 8) & 0xFF), (byte)(cols & 0xFF),
                (byte)((rows >> 8) & 0xFF), (byte)(rows & 0xFF),
            };
            try { instance.StdinWriter.BaseStream.Write(resizeCmd, 0, resizeCmd.Length); instance.StdinWriter.BaseStream.Flush(); }
            catch { }
        }
    }

    public async Task RemoveViewerAsync(string connId)
    {
        foreach (var (_, instance) in _terminals)
            instance.RemoveViewer(connId);
        await Task.CompletedTask;
    }

    private async Task ReadStreamAsync(string terminalId, StreamReader reader, bool isError, CancellationToken ct)
    {
        var buf = new char[4096];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var n = await reader.ReadAsync(buf, 0, buf.Length);
                if (n == 0) break;
                var chunk = new string(buf, 0, n);
                await SendToTerminalGroup(terminalId, new TerminalOutputMessage(chunk, isError, terminalId));
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _log.LogWarning(ex, "Ошибка чтения терминала {TerminalId}", terminalId); }
    }

    private async Task HandleExitedAsync(string terminalId, int exitCode)
    {
        if (_terminals.TryRemove(terminalId, out var instance))
        {
            instance.Dispose();
            await SendToTerminalGroup(terminalId, new TerminalStatusMessage("stopped", exitCode, terminalId));
        }
    }

    private async Task SendToTerminalGroup(string terminalId, object message)
    {
        try { await _hub.Clients.Group("term_" + terminalId).SendAsync("message", message); }
        catch { }
    }

    private async Task GroupsAdd(string connId, string terminalId)
    {
        try { await _hub.Groups.AddToGroupAsync(connId, "term_" + terminalId); }
        catch { }
    }

    private void CleanupStale()
    {
        var now = DateTime.UtcNow;
        foreach (var (id, instance) in _terminals)
        {
            if (instance.ViewerCount == 0 && (now - instance.LastActivity).TotalMinutes >= 5)
            {
                _log.LogInformation("Терминал {TerminalId} неактивен >5 мин — остановка", id);
                if (_terminals.TryRemove(id, out var removed))
                {
                    removed.Dispose();
                    _ = SendToTerminalGroup(id, new TerminalStatusMessage("stopped", 0, id));
                }
            }
        }
    }

    private static TerminalInfoDto ToDto(TerminalInstance inst) => new(inst.Id, inst.ProjectId, inst.Name, inst.Status, inst.Shell);

    public void Dispose()
    {
        _shutdownCts.Cancel();
        _cleanupTimer.Dispose();
        foreach (var (_, instance) in _terminals) instance.Dispose();
        _terminals.Clear();
    }
}
