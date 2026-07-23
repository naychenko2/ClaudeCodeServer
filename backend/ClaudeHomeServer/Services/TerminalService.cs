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

    // Терминал работает через pty-bridge (Linux + бинарь на месте) → ввод/resize
    // идут кадрами протокола. Иначе (Windows-powershell, bash-фолбэк) — сырой stdin.
    public bool UsesPtyBridge { get; }
    // Сериализация записи в stdin моста: ввод и resize не должны перемешать байты кадров.
    public object StdinLock { get; } = new();

    // Кольцевой буфер недавнего вывода — реплеится новому вьюеру при Connect
    // (уход с вкладки/reconnect/другое устройство размонтируют xterm и теряют его буфер).
    private readonly object _bufLock = new();
    private readonly System.Text.StringBuilder _outputBuffer = new();
    private const int MaxOutputBufferChars = 200_000;

    public void AppendOutput(string chunk)
    {
        lock (_bufLock)
        {
            _outputBuffer.Append(chunk);
            if (_outputBuffer.Length > MaxOutputBufferChars)
                _outputBuffer.Remove(0, _outputBuffer.Length - MaxOutputBufferChars);
        }
    }

    public string GetBufferedOutput()
    {
        lock (_bufLock) return _outputBuffer.ToString();
    }

    // Драйвер среды, запустивший процесс + метка: в песочнице убить процесс
    // может только он (Kill docker-клиента не трогает процесс в контейнере)
    private readonly Execution.IProcessLauncher _launcher;
    private readonly string _turnId;

    public TerminalInstance(string id, string projectId, string name, Process process, string userId,
        int cols, int rows, StreamWriter stdinWriter, bool isWindows, bool usesPtyBridge, string? shell,
        Execution.IProcessLauncher launcher, string turnId)
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
        UsesPtyBridge = usesPtyBridge;
        Shell = shell;
        _launcher = launcher;
        _turnId = turnId;
    }

    public void AddViewer(string connId)   { lock (ConnectionIds) ConnectionIds.Add(connId); LastActivity = DateTime.UtcNow; }
    public void RemoveViewer(string connId) { lock (ConnectionIds) ConnectionIds.Remove(connId); }
    public int ViewerCount { get { lock (ConnectionIds) return ConnectionIds.Count; } }

    public void Dispose()
    {
        StdinWriter.Dispose();
        if (!Process.HasExited)
        {
            _launcher.Kill(Process, _turnId);
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
    private readonly Execution.ILauncherFactory _launchers;
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly Timer _cleanupTimer;

    private static readonly string PtyBridgePath = "/app/pty-bridge";

    // Есть ли pty-bridge в целевой среде: локально — файл на диске,
    // в песочнице — гарантирован образом
    private static bool HasPtyBridge(Execution.IProcessLauncher launcher) =>
        launcher.IsSandboxed || File.Exists(PtyBridgePath);

    public TerminalService(IHubContext<TerminalHub> hub, ProjectManager projects, ILogger<TerminalService> log,
        Execution.ILauncherFactory launchers)
    {
        _hub = hub;
        _projects = projects;
        _log = log;
        _launchers = launchers;
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

        // Шелл выбираем по ОС ЦЕЛЕВОЙ среды: powershell на Windows-хосте,
        // pty-bridge/bash на Linux (в т.ч. внутри песочницы)
        var launcher = _launchers.ForOwner(project.OwnerId);
        var isWindows = launcher.TargetIsWindows;
        var usesPtyBridge = false;
        var turnId = Guid.NewGuid().ToString("N")[..12];

        Process process;
        StreamWriter stdin;
        string? shell;

        if (isWindows)
        {
            // Windows: PowerShell с перенаправлением (без PTY).
            // Кодировки: Windows PowerShell 5.1 при перенаправлении пишет вывод в OEM-кодировку
            // консоли (cp866 на русской Windows), а мы читаем UTF-8 → кириллица кракозябрами.
            // Поэтому явно переводим дочерний pwsh на UTF-8 стартовой командой ([Console]::*Encoding
            // + $OutputEncoding), а StdioEncoding=UTF-8-без-BOM синхронизирует чтение наших потоков.
            shell = "powershell.exe";
            const string bootstrap =
                "[Console]::OutputEncoding=[Text.Encoding]::UTF8; " +
                "[Console]::InputEncoding=[Text.Encoding]::UTF8; " +
                "$OutputEncoding=[Text.Encoding]::UTF8; " +
                "$Host.UI.RawUI.WindowTitle='terminal'";
            process = launcher.Start(new Execution.ProcessSpec
            {
                FileName = "powershell.exe",
                Args = ["-NoLogo", "-NoExit", "-Command", bootstrap],
                WorkingDirectory = project.RootPath,
                StdioEncoding = new System.Text.UTF8Encoding(false),
                EnableRaisingEvents = true,
                TurnId = turnId,
            });
        }
        else
        {
            // Linux: пытаемся pty-bridge, фолбэк на bash с перенаправлением
            shell = "bash";
            // bash идёт с --norc, его дефолтный промпт «bash-5.2$» не показывает cwd —
            // передаём PS1 с текущей папкой, чтобы было видно, что мы в папке проекта
            const string ps1 = @"\[\e[36m\]\w\[\e[0m\] \$ ";
            var env = new Dictionary<string, string>
            {
                ["TERM"] = "xterm-256color",
                ["PS1"] = ps1,
            };
            if (HasPtyBridge(launcher))
            {
                usesPtyBridge = true;
                process = launcher.Start(new Execution.ProcessSpec
                {
                    FileName = PtyBridgePath,
                    Args = [cols.ToString(), rows.ToString()],
                    WorkingDirectory = project.RootPath,
                    Env = env,
                    EnableRaisingEvents = true,
                    TurnId = turnId,
                });
            }
            else
            {
                // Fallback без PTY
                _log.LogWarning("pty-bridge не найден, запуск bash с перенаправлением");
                process = launcher.Start(new Execution.ProcessSpec
                {
                    FileName = "/bin/bash",
                    Args = ["--norc", "--noediting", "-i"],
                    WorkingDirectory = project.RootPath,
                    Env = env,
                    EnableRaisingEvents = true,
                    TurnId = turnId,
                });
            }
        }
        // UTF-8 БЕЗ BOM: Console.OutputEncoding — это UTF-8 с преамбулой (Program.cs), и StreamWriter
        // писал бы BOM в начало stdin. PowerShell тогда получал первую команду как «<BOM>ls» → ls не
        // распознаётся (CommandNotFoundException). Без BOM ввод чистый.
        stdin = new StreamWriter(process.StandardInput.BaseStream, new System.Text.UTF8Encoding(false)) { AutoFlush = true };

        var instance = new TerminalInstance(terminalId, projectId, name, process, userId,
            cols, rows, stdin, isWindows, usesPtyBridge, shell, launcher, turnId);
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
        // Реплей накопленного вывода ТОЛЬКО новому подключению (до входа в группу, чтобы
        // не задвоить с live-выводом): xterm при ремоунте пуст, история восстанавливается.
        var buffered = instance.GetBufferedOutput();
        if (buffered.Length > 0)
        {
            try { await _hub.Clients.Client(connId).SendAsync("message", new TerminalOutputMessage(buffered, false, terminalId)); }
            catch { }
        }
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

    /// <summary>Переименовать терминал.</summary>
    public async Task<TerminalInfoDto?> RenameAsync(string terminalId, string userId, string name)
    {
        if (!_terminals.TryGetValue(terminalId, out var instance)) return null;
        if (instance.UserId != userId) throw new HubException("Доступ запрещён");
        var trimmed = name.Trim();
        if (string.IsNullOrWhiteSpace(trimmed)) return ToDto(instance);
        instance.Name = trimmed.Length > 60 ? trimmed[..60] : trimmed;
        await SendToTerminalGroup(terminalId, new TerminalRenamedMessage(terminalId, instance.Name));
        return ToDto(instance);
    }

    public async Task StopAsync(string terminalId, string userId)
    {
        // Сверяем владельца ДО удаления из реестра: иначе чужой вызов вырывал бы терминал
        // из реестра (а процесс-сирота оставался жив) ещё до броска исключения.
        if (!_terminals.TryGetValue(terminalId, out var instance)) return;
        if (instance.UserId != userId)
            throw new HubException("Доступ запрещён");
        _terminals.TryRemove(terminalId, out _);
        instance.Status = "stopped";
        instance.Dispose();
        await SendToTerminalGroup(terminalId, new TerminalStatusMessage("stopped", 0, terminalId));
        _log.LogInformation("Терминал {TerminalId} остановлен", terminalId);
    }

    /// <summary>Владеет ли пользователь терминалом (существует и принадлежит ему).</summary>
    public bool Owns(string terminalId, string userId)
        => _terminals.TryGetValue(terminalId, out var instance) && instance.UserId == userId;

    public async Task WriteInputAsync(string terminalId, string data)
    {
        if (_terminals.TryGetValue(terminalId, out var instance))
        {
            instance.LastActivity = DateTime.UtcNow;
            if (instance.UsesPtyBridge)
            {
                // Данные ввода — кадром протокола (type=0x00), чтобы не смешиваться с resize
                WriteFrame(instance, 0x00, System.Text.Encoding.UTF8.GetBytes(data));
            }
            else
            {
                await instance.StdinWriter.WriteAsync(data);
                await instance.StdinWriter.FlushAsync();
            }
        }
    }

    public void Resize(string terminalId, int cols, int rows)
    {
        if (!_terminals.TryGetValue(terminalId, out var instance)) return;
        instance.Cols = cols;
        instance.Rows = rows;

        if (instance.UsesPtyBridge)
        {
            // resize-кадр (type=0x01): payload = cols BE + rows BE
            var payload = new byte[]
            {
                (byte)((cols >> 8) & 0xFF), (byte)(cols & 0xFF),
                (byte)((rows >> 8) & 0xFF), (byte)(rows & 0xFF),
            };
            WriteFrame(instance, 0x01, payload);
        }
    }

    // Кадр протокола pty-bridge: [type:1][len:4 BE][payload]. Пишем в stdin моста под
    // локом, чтобы конкурентные ввод и resize не перемешали байты соседних кадров.
    private static void WriteFrame(TerminalInstance instance, byte type, byte[] payload)
    {
        var header = new byte[5];
        header[0] = type;
        header[1] = (byte)((payload.Length >> 24) & 0xFF);
        header[2] = (byte)((payload.Length >> 16) & 0xFF);
        header[3] = (byte)((payload.Length >> 8) & 0xFF);
        header[4] = (byte)(payload.Length & 0xFF);
        try
        {
            lock (instance.StdinLock)
            {
                var s = instance.StdinWriter.BaseStream;
                s.Write(header, 0, header.Length);
                if (payload.Length > 0) s.Write(payload, 0, payload.Length);
                s.Flush();
            }
        }
        catch { /* мост закрыт/умер — ход завершится по exited */ }
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
                if (_terminals.TryGetValue(terminalId, out var inst)) inst.AppendOutput(chunk);
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
