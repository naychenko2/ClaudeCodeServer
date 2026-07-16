using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;
using ClaudeHomeServer.Hubs;
using ClaudeHomeServer.Protocol;
using Microsoft.AspNetCore.SignalR;

namespace ClaudeHomeServer.Services;

/// <summary>Экземпляр запущенного dev-сервера проекта.</summary>
internal sealed class DevServerInstance : IDisposable
{
    public string ProjectId { get; }
    public Process Process { get; set; }
    public string UserId { get; }
    public int Port { get; set; }
    public string Status { get; set; } = "starting"; // starting | started | stopped | error
    public string? Error { get; set; }
    public DateTime LastActivity { get; set; }

    public DevServerInstance(string projectId, Process process, string userId)
    {
        ProjectId = projectId;
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

/// <summary>Менеджер dev-серверов: запуск/остановка per-project.</summary>
public sealed class DevServerService
{
    private readonly ConcurrentDictionary<string, DevServerInstance> _servers = new();
    private readonly ProjectManager _projects;
    private readonly IHubContext<SessionHub> _hub;
    private readonly ILogger<DevServerService> _log;

    public DevServerService(ProjectManager projects, IHubContext<SessionHub> hub, ILogger<DevServerService> log)
    {
        _projects = projects;
        _hub = hub;
        _log = log;
    }

    /// <summary>Запустить dev-сервер. Если уже запущен — порт из instance.</summary>
    public async Task<DevServerStartResult> StartAsync(string projectId, string userId,
        string command, string[] args, int? fixedPort = null)
    {
        if (_servers.TryGetValue(projectId, out var existing))
        {
            if (existing.Status == "started")
                return new DevServerStartResult(true, existing.Port, existing.Status);
            if (existing.Status == "starting")
                return new DevServerStartResult(true, null, "starting");
            // stopped/error — удаляем и стартуем заново
            _servers.TryRemove(projectId, out _);
            existing.Dispose();
        }

        var project = _projects.GetById(projectId);
        if (project is null)
            return new DevServerStartResult(false, null, "error", "Проект не найден");

        var psi = new ProcessStartInfo
        {
            FileName = command,
            Arguments = string.Join(" ", args),
            WorkingDirectory = project.RootPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.Start();

        var instance = new DevServerInstance(projectId, process, userId);
        _servers[projectId] = instance;

        // Если порт задан явно — сразу считаем стартовавшим
        if (fixedPort.HasValue)
        {
            instance.Port = fixedPort.Value;
            instance.Status = "started";
            await BroadcastStatus(projectId, "started", instance.Port);
            return new DevServerStartResult(true, instance.Port, "started");
        }

        // Иначе парсим stdout на http://localhost:PORT
        _ = ParsePortFromOutput(process, instance, projectId);
        // Ждём до 30 секунд
        for (int i = 0; i < 60; i++)
        {
            await Task.Delay(500);
            if (instance.Status == "started")
                return new DevServerStartResult(true, instance.Port, "started");
            if (process.HasExited) break;
        }

        var exited = process.WaitForExit(2000);
        var exitCode = process.ExitCode;
        _servers.TryRemove(projectId, out _);
        instance.Dispose();

        return new DevServerStartResult(false, null, "error",
            exited ? $"Процесс завершился с кодом {exitCode}" : "Таймаут: порт не обнаружен");
    }

    /// <summary>Остановить dev-сервер.</summary>
    public async Task StopAsync(string projectId, string userId)
    {
        if (_servers.TryRemove(projectId, out var instance))
        {
            if (instance.UserId != userId)
                throw new UnauthorizedAccessException("Доступ запрещён");
            instance.Dispose();
            _log.LogInformation("Dev-сервер проекта {ProjectId} остановлен", projectId);
        }
        await Task.CompletedTask;
    }

    /// <summary>Получить порт без проверки пользователя (для middleware).</summary>
    public int? GetPortNoAuth(string projectId)
    {
        if (_servers.TryGetValue(projectId, out var instance))
        {
            if (instance.Status == "started") return instance.Port;
        }
        return null;
    }

    /// <summary>Получить порт запущенного dev-сервера.</summary>
    public int? GetPort(string projectId, string userId)
    {
        if (_servers.TryGetValue(projectId, out var instance))
        {
            if (instance.UserId != userId) return null;
            if (instance.Status == "started") return instance.Port;
        }
        return null;
    }

    /// <summary>Статус dev-сервера.</summary>
    public (string status, int? port, string? error) GetStatus(string projectId, string userId)
    {
        if (_servers.TryGetValue(projectId, out var instance))
        {
            if (instance.UserId != userId) return ("stopped", null, null);
            return (instance.Status, instance.Port, instance.Error);
        }
        return ("stopped", null, null);
    }

    /// <summary>Остановить всё (при shutdown).</summary>
    public void ShutdownAll()
    {
        foreach (var (id, instance) in _servers)
        {
            _log.LogInformation("Shutdown: останов dev-сервера проекта {ProjectId}", id);
            instance.Dispose();
        }
        _servers.Clear();
    }

    private async Task ParsePortFromOutput(Process process, DevServerInstance instance, string projectId)
    {
        var reg = new Regex(@"http://localhost:(\d+)", RegexOptions.IgnoreCase);
        var tcs = new TaskCompletionSource<bool>();

        // Читаем оба потока (stdout + stderr) — Vite пишет URL в stderr
        var stdoutTask = Task.Run(() =>
        {
            string? line;
            while ((line = process.StandardOutput.ReadLine()) != null)
            {
                var m = reg.Match(line);
                if (m.Success && instance.Port == 0)
                {
                    instance.Port = int.Parse(m.Groups[1].Value);
                    instance.Status = "started";
                    _ = BroadcastStatus(projectId, "started", instance.Port);
                    tcs.TrySetResult(true);
                }
            }
            return Task.CompletedTask;
        });

        var stderrTask = Task.Run(() =>
        {
            string? line;
            while ((line = process.StandardError.ReadLine()) != null)
            {
                var m = reg.Match(line);
                if (m.Success && instance.Port == 0)
                {
                    instance.Port = int.Parse(m.Groups[1].Value);
                    instance.Status = "started";
                    _ = BroadcastStatus(projectId, "started", instance.Port);
                    tcs.TrySetResult(true);
                }
            }
            return Task.CompletedTask;
        });

        await Task.WhenAny(stdoutTask, stderrTask);
    }

    private async Task BroadcastStatus(string projectId, string status, int? port, string? error = null)
    {
        try
        {
            var project = _projects.GetById(projectId);
            if (project is null) return;
            await _hub.Clients.Group("user_" + project.OwnerId)
                .SendAsync("message", new PreviewStatusMessage(status, port, error));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Ошибка броадкаста статуса preview проекта {ProjectId}", projectId);
        }
    }
}

public record DevServerStartResult(bool Success, int? Port, string Status, string? Error = null);
