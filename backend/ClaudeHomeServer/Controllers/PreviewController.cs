using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ClaudeHomeServer.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Controllers;

[ApiController]
[Authorize]
public class PreviewController : ControllerBase
{
    private readonly ProjectManager _projects;
    private readonly DevServerService _devServer;
    private readonly ProjectServiceDiscovery _discovery;
    private readonly LaunchConfigService _launch;
    private readonly ILogger<PreviewController> _log;

    public PreviewController(ProjectManager projects, DevServerService devServer,
        ProjectServiceDiscovery discovery, LaunchConfigService launch, ILogger<PreviewController> log)
    {
        _projects = projects;
        _devServer = devServer;
        _discovery = discovery;
        _launch = launch;
        _log = log;
    }

    private string UserId => User.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? "";

    private Models.Project? OwnedProject(string projectId)
    {
        var project = _projects.GetById(projectId);
        return project?.OwnerId == UserId ? project : null;
    }

    /// <summary>Список запускаемых сервисов проекта (инференс из манифестов + сохранённые) с runtime-статусом.</summary>
    [HttpGet("/api/projects/{projectId}/services")]
    public async Task<IActionResult> Services(string projectId)
    {
        var project = OwnedProject(projectId);
        if (project is null) return Forbid();

        var discovered = await _discovery.DiscoverAsync(project);
        var running = _devServer.GetRunning(projectId, UserId).ToDictionary(r => r.ServiceId);
        var activeId = _devServer.GetActiveServiceId(projectId, UserId);

        var services = new List<ServiceDto>();
        var covered = new HashSet<string>();
        foreach (var s in discovered)
        {
            running.TryGetValue(s.Id, out var run);
            covered.Add(s.Id);
            services.Add(new ServiceDto(s.Id, s.Name, s.Source, s.Command, s.Args, s.Cwd,
                s.SuggestedPort, s.AutoPort, s.Saved,
                run?.Status ?? "idle", run?.Port, run?.Error));
        }
        // Запущенные сервисы, которых нет в инференсе (напр. кастомная разовая команда).
        foreach (var run in running.Values)
        {
            if (covered.Contains(run.ServiceId)) continue;
            services.Add(new ServiceDto(run.ServiceId, run.Name, "custom", "", [], null,
                null, false, false, run.Status, run.Port, run.Error));
        }

        return Ok(new { services, activeServiceId = activeId });
    }

    [HttpPost("/api/projects/{projectId}/preview/start")]
    public async Task<IActionResult> Start(string projectId, [FromBody] PreviewStartRequest req)
    {
        if (OwnedProject(projectId) is null) return Forbid();
        if (string.IsNullOrWhiteSpace(req.Command))
            return BadRequest(new { error = "Команда не указана" });

        var serviceId = string.IsNullOrWhiteSpace(req.ServiceId)
            ? "custom-" + Guid.NewGuid().ToString("N")[..8]
            : req.ServiceId!;
        var name = string.IsNullOrWhiteSpace(req.Name) ? req.Command : req.Name!;

        var result = await _devServer.StartAsync(projectId, UserId, serviceId, name,
            req.Command, req.Args ?? [], req.Cwd, req.Port, req.AutoPort, req.Env);
        return Ok(new { status = result.Status, port = result.Port, error = result.Error, serviceId });
    }

    [HttpPost("/api/projects/{projectId}/preview/stop")]
    public async Task<IActionResult> Stop(string projectId, [FromBody] PreviewStopRequest? req)
    {
        if (OwnedProject(projectId) is null) return Forbid();
        if (string.IsNullOrWhiteSpace(req?.ServiceId))
            return BadRequest(new { error = "serviceId не указан" });
        await _devServer.StopAsync(projectId, UserId, req.ServiceId!);
        return Ok(new { status = "stopped" });
    }

    [HttpGet("/api/projects/{projectId}/preview/status")]
    public IActionResult Status(string projectId)
    {
        if (OwnedProject(projectId) is null) return Forbid();
        var running = _devServer.GetRunning(projectId, UserId);
        var activeId = _devServer.GetActiveServiceId(projectId, UserId);
        return Ok(new { running, activeServiceId = activeId });
    }

    /// <summary>Назначить активный для превью сервис (на его порт указывает iframe).</summary>
    [HttpPost("/api/projects/{projectId}/preview/active")]
    public IActionResult SetActive(string projectId, [FromBody] PreviewActiveRequest req)
    {
        if (OwnedProject(projectId) is null) return Forbid();
        if (string.IsNullOrWhiteSpace(req.ServiceId))
            return BadRequest(new { error = "serviceId не указан" });
        _devServer.SetActivePreview(projectId, req.ServiceId);
        return Ok(new { activeServiceId = req.ServiceId });
    }

    /// <summary>Прочитать .claude/launch.json проекта.</summary>
    [HttpGet("/api/projects/{projectId}/launch-config")]
    public async Task<IActionResult> GetLaunchConfig(string projectId)
    {
        var project = OwnedProject(projectId);
        if (project is null) return Forbid();
        var configs = await _launch.ReadAsync(project);
        return Ok(new { configurations = configs });
    }

    /// <summary>Записать .claude/launch.json проекта.</summary>
    [HttpPut("/api/projects/{projectId}/launch-config")]
    public async Task<IActionResult> PutLaunchConfig(string projectId, [FromBody] LaunchConfigPutRequest req)
    {
        var project = OwnedProject(projectId);
        if (project is null) return Forbid();
        await _launch.WriteAsync(project, req.Configurations ?? []);
        _discovery.Invalidate(projectId);
        return Ok(new { configurations = req.Configurations ?? [] });
    }
}

public record ServiceDto(
    string Id, string Name, string Source, string Command, string[] Args, string? Cwd,
    int? SuggestedPort, bool AutoPort, bool Saved,
    string Status, int? RunningPort, string? Error);

public record PreviewStartRequest(
    string Command, string[]? Args = null, int? Port = null,
    string? ServiceId = null, string? Name = null, string? Cwd = null,
    bool AutoPort = false, Dictionary<string, string>? Env = null);

public record PreviewStopRequest(string? ServiceId);
public record PreviewActiveRequest(string ServiceId);
public record LaunchConfigPutRequest(List<LaunchConfigEntry>? Configurations);
