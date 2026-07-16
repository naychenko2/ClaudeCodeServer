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

    public DevServerInstance(string projectId, string serviceId, string name, Process process, string userId)
    {
        ProjectId = projectId;
        ServiceId = serviceId;
        Name = name;
        Process = process;
        UserId = userId;
        LastActivity = DateTime.UtcNow;
    }

    public void Dispose()
    {
        if (!Process.HasExited)
        {
            try { Process.Kill(entireProcessTree: true); } catch { /* best-effort */ }
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

    private static readonly Regex PortRegex = new(
        @"https?://(?:localhost|127\.0\.0\.1|0\.0\.0\.0|\[::1\]):(\d+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public DevServerService(ProjectManager projects, IHubContext<SessionHub> hub, ILogger<DevServerService> log)
    {
        _projects = projects;
        _hub = hub;
        _log = log;
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

        var psi = new ProcessStartInfo
        {
            FileName = command,
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (env != null)
            foreach (var (k, v) in env) psi.Environment[k] = v;

        // autoPort без явного порта → берём свободный. Явный/авто-порт прокидываем в окружение
        // (PORT для Node-фреймворков, ASPNETCORE_URLS для .NET), чтобы сервис слушал именно его.
        int? fixedPort = port;
        if (!fixedPort.HasValue && autoPort) fixedPort = GetFreePort();
        if (fixedPort.HasValue)
        {
            psi.Environment["PORT"] = fixedPort.Value.ToString();
            if (!psi.Environment.ContainsKey("ASPNETCORE_URLS"))
                psi.Environment["ASPNETCORE_URLS"] = $"http://localhost:{fixedPort.Value}";
        }

        Process process;
        try
        {
            process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            process.Start();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Не удалось запустить сервис {ServiceId} ({Command})", serviceId, command);
            return new DevServerStartResult(false, null, "error", $"Не удалось запустить: {ex.Message}");
        }

        var instance = new DevServerInstance(projectId, serviceId, name, process, userId);
        _servers[key] = instance;
        process.Exited += (_, _) => OnExited(key);

        // Всегда дренируем оба потока (иначе буфер переполнится и процесс зависнет);
        // попутно детектим порт, если он не задан.
        _ = DrainStreams(process, instance);

        if (fixedPort.HasValue)
        {
            instance.Port = fixedPort.Value;
            instance.Status = "started";
            _activePreview[projectId] = serviceId;
            await BroadcastStatus(projectId, serviceId, "started", instance.Port);
            return new DevServerStartResult(true, instance.Port, "started");
        }

        // Ждём до 30 секунд появления порта в выводе.
        for (int i = 0; i < 60; i++)
        {
            await Task.Delay(500);
            if (instance.Status == "started")
                return new DevServerStartResult(true, instance.Port, "started");
            if (process.HasExited) break;
        }

        var exited = process.WaitForExit(2000);
        var exitCode = exited ? process.ExitCode : -1;
        instance.Status = "error";
        instance.Error = exited ? $"Процесс завершился с кодом {exitCode}" : "Таймаут: порт не обнаружен";
        _servers.TryRemove(key, out _);
        instance.Dispose();
        await BroadcastStatus(projectId, serviceId, "error", null, instance.Error);
        return new DevServerStartResult(false, null, "error", instance.Error);
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

    /// <summary>Порт активного для превью сервиса (для middleware, без проверки владельца).</summary>
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
                if (instance.Port != 0) continue;
                var m = PortRegex.Match(line);
                if (m.Success)
                {
                    instance.Port = int.Parse(m.Groups[1].Value);
                    instance.Status = "started";
                    _activePreview[instance.ProjectId] = instance.ServiceId;
                    _ = BroadcastStatus(instance.ProjectId, instance.ServiceId, "started", instance.Port);
                }
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
