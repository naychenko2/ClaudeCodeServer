using System.Collections.Concurrent;
using System.Diagnostics;
using ClaudeHomeServer.Hubs;
using ClaudeHomeServer.Protocol;
using Microsoft.AspNetCore.SignalR;

namespace ClaudeHomeServer.Services;

/// <summary>Экземпляр запущенного PTY-терминала для проекта.</summary>
internal sealed class TerminalInstance : IDisposable
{
    public string ProjectId { get; }
    public Process Process { get; }
    public string UserId { get; }
    public int Cols { get; set; }
    public int Rows { get; set; }
    public DateTime LastActivity { get; set; }

    /// <summary>Текущие подключённые viewer (connectionId).</summary>
    public HashSet<string> ConnectionIds { get; } = new();

    /// <summary>Поток записи в stdin PTY.</summary>
    public StreamWriter StdinWriter { get; }

    /// <summary>Контрольный поток для resize-команд в pty-bridge.</summary>
    public StreamWriter? ControlWriter { get; set; }

    public TerminalInstance(string projectId, Process process, string userId, int cols, int rows, StreamWriter stdinWriter)
    {
        ProjectId = projectId;
        Process = process;
        UserId = userId;
        Cols = cols;
        Rows = rows;
        LastActivity = DateTime.UtcNow;
        StdinWriter = stdinWriter;
    }

    public void AddViewer(string connId)
    {
        lock (ConnectionIds) ConnectionIds.Add(connId);
        LastActivity = DateTime.UtcNow;
    }

    public void RemoveViewer(string connId)
    {
        lock (ConnectionIds) ConnectionIds.Remove(connId);
    }

    public int ViewerCount
    {
        get { lock (ConnectionIds) return ConnectionIds.Count; }
    }

    public void Dispose()
    {
        StdinWriter.Dispose();
        ControlWriter?.Dispose();
        if (!Process.HasExited)
        {
            try { Process.Kill(entireProcessTree: true); } catch { /* best-effort */ }
            Process.WaitForExit(5000);
        }
        Process.Dispose();
    }
}

/// <summary>Менеджер PTY-терминалов: один экземпляр на проект.</summary>
public sealed class TerminalService : IDisposable
{
    private readonly ConcurrentDictionary<string, TerminalInstance> _terminals = new();
    private readonly IHubContext<TerminalHub> _hub;
    private readonly ProjectManager _projects;
    private readonly ILogger<TerminalService> _log;
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly Timer _cleanupTimer;

    // Путь к pty-bridge внутри контейнера
    private static readonly string PtyBridgePath = "/app/pty-bridge";

    public TerminalService(IHubContext<TerminalHub> hub, ProjectManager projects, ILogger<TerminalService> log)
    {
        _hub = hub;
        _projects = projects;
        _log = log;

        // Таймер чистоты: раз в 60 с проверяем брошенные терминалы (нет viewer >5 мин)
        _cleanupTimer = new Timer(_ => CleanupStale(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    public async Task StartAsync(string projectId, string userId, string connId, int cols, int rows)
    {
        // Если уже запущен — просто добавляем viewer
        if (_terminals.TryGetValue(projectId, out var existing))
        {
            existing.AddViewer(connId);
            // Шлём текущий статус новому viewer
            await _hub.Clients.Client(connId).SendAsync("message",
                new TerminalStatusMessage("running") with { SessionId = "" });
            return;
        }

        var project = _projects.GetById(projectId);
        if (project is null)
            throw new HubException("Проект не найден");

        if (!File.Exists(PtyBridgePath))
        {
            _log.LogError("pty-bridge не найден по пути {Path}. Терминал недоступен.", PtyBridgePath);
            throw new HubException("pty-bridge не установлен");
        }

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
        // TERM уже выставляет pty-bridge, но продублируем
        psi.EnvironmentVariables["TERM"] = "xterm-256color";

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.Start();

        var stdin = new StreamWriter(process.StandardInput.BaseStream, Console.OutputEncoding)
        {
            AutoFlush = true
        };

        var instance = new TerminalInstance(projectId, process, userId, cols, rows, stdin);
        instance.AddViewer(connId);
        _terminals[projectId] = instance;

        // Фоновое чтение stdout → SignalR
        _ = ReadStreamAsync(projectId, process.StandardOutput, false, _shutdownCts.Token);
        // Фоновое чтение stderr → SignalR
        _ = ReadStreamAsync(projectId, process.StandardError, true, _shutdownCts.Token);

        process.Exited += (_, _) => _ = HandleExitedAsync(projectId, process.ExitCode);

        await BroadcastToTerminalGroup(projectId, new TerminalStatusMessage("running"));
        _log.LogInformation("Терминал запущен для проекта {ProjectId} (pid={Pid})", projectId, process.Id);
    }

    public async Task StopAsync(string projectId, string userId)
    {
        if (_terminals.TryRemove(projectId, out var instance))
        {
            if (instance.UserId != userId)
                throw new HubException("Доступ запрещён");

            instance.Dispose();
            await BroadcastToTerminalGroup(projectId, new TerminalStatusMessage("stopped", 0));
            _log.LogInformation("Терминал проекта {ProjectId} остановлен", projectId);
        }
    }

    public async Task WriteInputAsync(string projectId, string data)
    {
        if (_terminals.TryGetValue(projectId, out var instance))
        {
            instance.LastActivity = DateTime.UtcNow;
            await instance.StdinWriter.WriteAsync(data);
            await instance.StdinWriter.FlushAsync();
        }
    }

    public void Resize(string projectId, int cols, int rows)
    {
        if (_terminals.TryGetValue(projectId, out var instance))
        {
            instance.Cols = cols;
            instance.Rows = rows;

            // PTY resize через pty-bridge: ESC + 'R' + 2 байта cols (big-endian) + 2 байта rows
            var resizeCmd = new byte[]
            {
                0x1B, (byte)'R',
                (byte)((cols >> 8) & 0xFF), (byte)(cols & 0xFF),
                (byte)((rows >> 8) & 0xFF), (byte)(rows & 0xFF),
            };
            try
            {
                instance.StdinWriter.BaseStream.Write(resizeCmd, 0, resizeCmd.Length);
                instance.StdinWriter.BaseStream.Flush();
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Ошибка resize терминала проекта {ProjectId}", projectId);
            }
        }
    }

    public async Task RemoveViewerAsync(string connId)
    {
        foreach (var (projectId, instance) in _terminals)
        {
            instance.RemoveViewer(connId);
        }
        await Task.CompletedTask;
    }

    private async Task ReadStreamAsync(string projectId, StreamReader reader, bool isError, CancellationToken ct)
    {
        var buf = new char[4096];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var n = await reader.ReadAsync(buf, 0, buf.Length);
                if (n == 0) break; // EOF

                var chunk = new string(buf, 0, n);
                await BroadcastToTerminalGroup(projectId,
                    new TerminalOutputMessage(chunk, isError));
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Ошибка чтения потока терминала проекта {ProjectId}", projectId);
        }
    }

    private async Task HandleExitedAsync(string projectId, int exitCode)
    {
        if (_terminals.TryRemove(projectId, out var instance))
        {
            instance.Dispose();
            await BroadcastToTerminalGroup(projectId,
                new TerminalStatusMessage("stopped", exitCode));
            _log.LogInformation("Терминал проекта {ProjectId} завершился с кодом {Code}", projectId, exitCode);
        }
    }

    private async Task BroadcastToTerminalGroup(string projectId, object message)
    {
        try
        {
            await _hub.Clients.Group("terminal_" + projectId).SendAsync("message", message);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Ошибка броадкаста терминала проекта {ProjectId}", projectId);
        }
    }

    private void CleanupStale()
    {
        var now = DateTime.UtcNow;
        foreach (var (projectId, instance) in _terminals)
        {
            if (instance.ViewerCount == 0 && (now - instance.LastActivity).TotalMinutes >= 5)
            {
                _log.LogInformation("Терминал проекта {ProjectId} неактивен >5 мин — остановка", projectId);
                if (_terminals.TryRemove(projectId, out var removed))
                {
                    removed.Dispose();
                    _ = BroadcastToTerminalGroup(projectId, new TerminalStatusMessage("stopped", 0));
                }
            }
        }
    }

    public void Dispose()
    {
        _shutdownCts.Cancel();
        _cleanupTimer.Dispose();
        foreach (var (_, instance) in _terminals)
            instance.Dispose();
        _terminals.Clear();
    }
}
