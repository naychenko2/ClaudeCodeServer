using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using ClaudeHomeServer.Hubs;
using ClaudeHomeServer.Protocol;
using Microsoft.AspNetCore.SignalR;

namespace ClaudeHomeServer.Services;

/// <summary>Экземпляр запущенного сервиса проекта (один процесс).</summary>
internal sealed class DevServerInstance : IDisposable
{
    public string ProjectId { get; }
    public string ServiceId { get; }
    public string Name { get; set; }
    public Process Process { get; set; }
    public string UserId { get; }
    public int Port { get; set; }
    public string Status { get; set; } = "starting"; // starting | started | stopped | error
    public string? Error { get; set; }
    public DateTime LastActivity { get; set; }

    // Хвост вывода (stdout+stderr) — для диагностики, когда старт провалился
    private readonly object _outLock = new();
    private readonly Queue<string> _outputTail = new();
    public void AppendOutput(string line)
    {
        lock (_outLock)
        {
            _outputTail.Enqueue(line);
            while (_outputTail.Count > 40) _outputTail.Dequeue();
        }
    }
    public string OutputTail()
    {
        lock (_outLock) return string.Join("\n", _outputTail);
    }

    // Драйвер среды, запустивший процесс + метка хода: в песочнице убить процесс
    // может только он (Kill docker-клиента не трогает процесс в контейнере)
    private readonly Execution.IProcessLauncher _launcher;
    private readonly string _turnId;

    public DevServerInstance(string projectId, string serviceId, string name, Process process, string userId,
        Execution.IProcessLauncher launcher, string turnId)
    {
        ProjectId = projectId;
        ServiceId = serviceId;
        Name = name;
        Process = process;
        UserId = userId;
        LastActivity = DateTime.UtcNow;
        _launcher = launcher;
        _turnId = turnId;
    }

    public void Dispose()
    {
        if (!Process.HasExited)
        {
            _launcher.Kill(Process, _turnId);
            Process.WaitForExit(5000);
        }
        Process.Dispose();
    }
}

/// <summary>Одна запущенная запись сервиса (для отдачи фронту).</summary>
public record RunningServiceInfo(string ServiceId, string Name, int? Port, string Status, string? Error);

/// <summary>
/// Менеджер сервисов Preview: несколько процессов на проект (ключ = projectId:serviceId).
/// Держит «активный для превью» сервис на проект — на его порт указывает iframe-прокси.
/// </summary>
public sealed class DevServerService
{
    private readonly ConcurrentDictionary<string, DevServerInstance> _servers = new();
    // projectId → serviceId активного для превью сервиса
    private readonly ConcurrentDictionary<string, string> _activePreview = new();
    private readonly ProjectManager _projects;
    private readonly IHubContext<SessionHub> _hub;
    private readonly ILogger<DevServerService> _log;
    private readonly Execution.ILauncherFactory _launchers;
    private readonly Execution.SandboxManager _sandbox;

    private static readonly Regex PortRegex = new(
        @"https?://(?:localhost|127\.0\.0\.1|0\.0\.0\.0|\[::1\]):(\d+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public DevServerService(ProjectManager projects, IHubContext<SessionHub> hub, ILogger<DevServerService> log,
        Execution.ILauncherFactory launchers, Execution.SandboxManager sandbox)
    {
        _projects = projects;
        _hub = hub;
        _log = log;
        _launchers = launchers;
        _sandbox = sandbox;
    }

    // Порт из опубликованного пула песочницы, не занятый другим сервисом
    // (порты проброшены на хост, preview-форвардер идёт на 127.0.0.1:{этот порт})
    private int PickSandboxPort()
    {
        var used = _servers.Values.Where(s => s.Port > 0).Select(s => s.Port).ToHashSet();
        var start = _sandbox.Options.PortRangeStart;
        for (var p = start; p < start + _sandbox.Options.PortRangeSize; p++)
            if (used.Add(p)) return p;
        throw new InvalidOperationException(
            "Исчерпан пул preview-портов песочницы — остановите неиспользуемые dev-серверы или увеличьте Sandbox:PortRangeSize");
    }

    private static string Key(string projectId, string serviceId) => projectId + ":" + serviceId;

    /// <summary>Запустить сервис. Уже запущен — возвращаем его порт и делаем активным для превью.</summary>
    public async Task<DevServerStartResult> StartAsync(string projectId, string userId, string serviceId,
        string name, string command, string[] args, string? cwd = null,
        int? port = null, bool autoPort = false, Dictionary<string, string>? env = null)
    {
        var key = Key(projectId, serviceId);
        if (_servers.TryGetValue(key, out var existing))
        {
            if (existing.Status == "started")
            {
                _activePreview[projectId] = serviceId;
                return new DevServerStartResult(true, existing.Port, "started");
            }
            if (existing.Status == "starting")
                return new DevServerStartResult(true, null, "starting");
            _servers.TryRemove(key, out _);
            existing.Dispose();
        }

        var project = _projects.GetById(projectId);
        if (project is null)
            return new DevServerStartResult(false, null, "error", "Проект не найден");

        string workingDir;
        try
        {
            workingDir = string.IsNullOrWhiteSpace(cwd)
                ? project.RootPath
                : FileService.SafeJoinPublic(project.RootPath, cwd);
        }
        catch (UnauthorizedAccessException)
        {
            return new DevServerStartResult(false, null, "error", "Недопустимый рабочий каталог");
        }

        var launcher = _launchers.ForOwner(project.OwnerId);
        var envVars = new Dictionary<string, string>();
        if (env != null)
            foreach (var (k, v) in env) envVars[k] = v;

        // autoPort без явного порта → берём свободный. Явный/авто-порт прокидываем в окружение
        // (PORT для Node-фреймворков, ASPNETCORE_URLS для .NET), чтобы сервис слушал именно его.
        // В песочнице порт обязан быть из опубликованного пула (иначе он не проброшен на хост
        // и preview-форвардер на 127.0.0.1 не достучится); случайный хостовый порт не подходит.
        int? fixedPort = port;
        if (launcher.IsSandboxed && (fixedPort is null || autoPort))
            fixedPort = PickSandboxPort();
        else if (!fixedPort.HasValue && autoPort) fixedPort = GetFreePort();
        if (fixedPort.HasValue)
        {
            envVars["PORT"] = fixedPort.Value.ToString();
            // Наследованный ASPNETCORE_URLS процесса бэкенда не перебиваем (историческое поведение)
            if (!envVars.ContainsKey("ASPNETCORE_URLS")
                && Environment.GetEnvironmentVariable("ASPNETCORE_URLS") is null)
                envVars["ASPNETCORE_URLS"] = $"http://localhost:{fixedPort.Value}";
        }

        var turnId = Guid.NewGuid().ToString("N")[..12];
        Process process;
        try
        {
            process = launcher.Start(new Execution.ProcessSpec
            {
                FileName = command,
                Args = args,
                WorkingDirectory = workingDir,
                Env = envVars,
                RedirectStdin = false,
                EnableRaisingEvents = true,
                TurnId = turnId,
            });
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Не удалось запустить сервис {ServiceId} ({Command})", serviceId, command);
            return new DevServerStartResult(false, null, "error", $"Не удалось запустить: {ex.Message}");
        }

        var instance = new DevServerInstance(projectId, serviceId, name, process, userId, launcher, turnId);
        _servers[key] = instance;
        process.Exited += (_, _) => OnExited(key);

        // Всегда дренируем оба потока (иначе буфер переполнится и процесс зависнет);
        // попутно детектим порт, если он не задан.
        _ = DrainStreams(process, instance);

        // Известный порт (из конфига/launchSettings) фиксируем, но «started» ставим ТОЛЬКО когда
        // приложение реально слушает порт — иначе iframe грузится в мёртвый порт (502/пустая страница).
        if (fixedPort.HasValue) instance.Port = fixedPort.Value;

        // Ждём до 30 сек: порт известен (fixed или из stdout) И реально принимает соединения.
        for (int i = 0; i < 60; i++)
        {
            if (process.HasExited) break;
            if (instance.Port != 0 && await IsPortListeningAsync(instance.Port))
            {
                instance.Status = "started";
                _activePreview[projectId] = serviceId;
                await BroadcastStatus(projectId, serviceId, "started", instance.Port);
                return new DevServerStartResult(true, instance.Port, "started");
            }
            await Task.Delay(500);
        }

        // Не поднялся: честная ошибка с хвостом вывода процесса.
        var exited = process.HasExited;
        var exitCode = exited ? SafeExitCode(process) : -1;
        var tail = instance.OutputTail();
        var reason = exited ? $"Процесс завершился с кодом {exitCode}." : "Таймаут: сервис не начал слушать порт.";
        instance.Status = "error";
        instance.Error = string.IsNullOrWhiteSpace(tail) ? reason : reason + "\n" + tail;
        _servers.TryRemove(key, out _);
        instance.Dispose();
        await BroadcastStatus(projectId, serviceId, "error", null, instance.Error);
        return new DevServerStartResult(false, null, "error", instance.Error);
    }

    private static int SafeExitCode(Process p)
    {
        try { return p.ExitCode; } catch { return -1; }
    }

    /// <summary>Проверить, принимает ли кто-то соединения на 127.0.0.1:port (готовность dev-сервера).</summary>
    private static async Task<bool> IsPortListeningAsync(int port)
    {
        try
        {
            using var client = new TcpClient();
            var connect = client.ConnectAsync(IPAddress.Loopback, port);
            var ok = await Task.WhenAny(connect, Task.Delay(400)) == connect;
            if (connect.IsFaulted) _ = connect.Exception; // погасить unobserved
            return ok && connect.IsCompletedSuccessfully && client.Connected;
        }
        catch { return false; }
    }

    /// <summary>Остановить сервис.</summary>
    public async Task StopAsync(string projectId, string userId, string serviceId)
    {
        if (_servers.TryGetValue(Key(projectId, serviceId), out var instance))
        {
            if (instance.UserId != userId)
                throw new UnauthorizedAccessException("Доступ запрещён");
            _servers.TryRemove(Key(projectId, serviceId), out _);
            instance.Dispose();
            if (_activePreview.TryGetValue(projectId, out var active) && active == serviceId)
                _activePreview.TryRemove(projectId, out _);
            _log.LogInformation("Сервис {ServiceId} проекта {ProjectId} остановлен", serviceId, projectId);
            await BroadcastStatus(projectId, serviceId, "stopped", null);
        }
    }

    /// <summary>Назначить активный для превью сервис (на его порт указывает iframe-прокси).</summary>
    public void SetActivePreview(string projectId, string serviceId) => _activePreview[projectId] = serviceId;

    /// <summary>Порт активного для превью сервиса проекта. Владельца проверяет вызывающий
    /// (preview-middleware сверяет OwnerId по токену до вызова); фолбэк ограничен тем же projectId.</summary>
    public int? GetActivePreviewPort(string projectId)
    {
        if (_activePreview.TryGetValue(projectId, out var serviceId) &&
            _servers.TryGetValue(Key(projectId, serviceId), out var inst) &&
            inst.Status == "started" && inst.Port > 0)
            return inst.Port;

        // Фолбэк: первый запущенный сервис проекта, если активный не задан.
        var prefix = projectId + ":";
        foreach (var (k, v) in _servers)
            if (k.StartsWith(prefix, StringComparison.Ordinal) && v.Status == "started" && v.Port > 0)
                return v.Port;
        return null;
    }

    /// <summary>Список запущенных (и недавно упавших) сервисов проекта.</summary>
    public List<RunningServiceInfo> GetRunning(string projectId, string userId)
    {
        var prefix = projectId + ":";
        var list = new List<RunningServiceInfo>();
        foreach (var (k, inst) in _servers)
        {
            if (!k.StartsWith(prefix, StringComparison.Ordinal)) continue;
            if (inst.UserId != userId) continue;
            list.Add(new RunningServiceInfo(inst.ServiceId, inst.Name, inst.Port == 0 ? null : inst.Port, inst.Status, inst.Error));
        }
        return list;
    }

    /// <summary>Id активного для превью сервиса проекта (для владельца).</summary>
    public string? GetActiveServiceId(string projectId, string userId)
    {
        if (_activePreview.TryGetValue(projectId, out var serviceId) &&
            _servers.TryGetValue(Key(projectId, serviceId), out var inst) &&
            inst.UserId == userId)
            return serviceId;
        return null;
    }

    /// <summary>Остановить всё (при shutdown).</summary>
    public void ShutdownAll()
    {
        foreach (var (id, instance) in _servers)
        {
            _log.LogInformation("Shutdown: останов сервиса {Key}", id);
            instance.Dispose();
        }
        _servers.Clear();
        _activePreview.Clear();
    }

    private void OnExited(string key)
    {
        if (_servers.TryGetValue(key, out var inst) && inst.Status == "started")
        {
            inst.Status = "stopped";
            if (_activePreview.TryGetValue(inst.ProjectId, out var active) && active == inst.ServiceId)
                _activePreview.TryRemove(inst.ProjectId, out _);
            _ = BroadcastStatus(inst.ProjectId, inst.ServiceId, "stopped", null);
        }
    }

    private async Task DrainStreams(Process process, DevServerInstance instance)
    {
        async Task Pump(TextReader reader)
        {
            string? line;
            while ((line = await reader.ReadLineAsync()) != null)
            {
                instance.LastActivity = DateTime.UtcNow;
                instance.AppendOutput(line);
                // Порт из вывода нужен только если он не задан заранее; готовность проверит StartAsync.
                if (instance.Port != 0) continue;
                var m = PortRegex.Match(line);
                if (m.Success) instance.Port = int.Parse(m.Groups[1].Value);
            }
        }

        try
        {
            await Task.WhenAll(Pump(process.StandardOutput), Pump(process.StandardError));
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Дренаж потоков сервиса {ServiceId} прерван", instance.ServiceId);
        }
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private async Task BroadcastStatus(string projectId, string serviceId, string status, int? port, string? error = null)
    {
        try
        {
            var project = _projects.GetById(projectId);
            if (project is null) return;
            await _hub.Clients.Group("user_" + project.OwnerId)
                .SendAsync("message", new PreviewStatusMessage(status, port, error, serviceId));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Ошибка броадкаста статуса preview проекта {ProjectId}", projectId);
        }
    }
}

public record DevServerStartResult(bool Success, int? Port, string Status, string? Error = null);
