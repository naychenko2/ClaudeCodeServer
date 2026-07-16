using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ClaudeHomeServer.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace ClaudeHomeServer.Hubs;

[Authorize]
public class TerminalHub : Hub
{
    private readonly TerminalService _terminal;
    private readonly ProjectManager _projects;

    public TerminalHub(TerminalService terminal, ProjectManager projects)
    {
        _terminal = terminal;
        _projects = projects;
    }

    // DefaultMapInboundClaims = false → sub не ремапится
    private string? UserId => Context.User?.FindFirstValue(JwtRegisteredClaimNames.Sub);

    private bool OwnsProject(string projectId)
    {
        var ownerId = _projects.GetById(projectId)?.OwnerId;
        return ownerId is not null && ownerId == UserId;
    }

    private static HubException Denied() => new("Доступ запрещён");

    /// <summary>Запустить терминал для проекта (или подключиться к существующему).</summary>
    public async Task StartTerminal(string projectId, int cols = 80, int rows = 24)
    {
        if (!OwnsProject(projectId)) throw Denied();
        var userId = UserId!;
        var connId = Context.ConnectionId;

        await _terminal.StartAsync(projectId, userId, connId, cols, rows);
        await Groups.AddToGroupAsync(Context.ConnectionId, "terminal_" + projectId);
    }

    /// <summary>Остановить терминал проекта.</summary>
    public async Task StopTerminal(string projectId)
    {
        if (!OwnsProject(projectId)) throw Denied();
        await _terminal.StopAsync(projectId, UserId!);
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, "terminal_" + projectId);
    }

    /// <summary>Отправить ввод в PTY терминала.</summary>
    public async Task TerminalInput(string projectId, string data)
    {
        if (!OwnsProject(projectId)) throw Denied();
        await _terminal.WriteInputAsync(projectId, data);
    }

    /// <summary>Изменить размер терминала.</summary>
    public Task TerminalResize(string projectId, int cols, int rows)
    {
        if (!OwnsProject(projectId)) throw Denied();
        _terminal.Resize(projectId, cols, rows);
        return Task.CompletedTask;
    }

    public override async Task OnDisconnectedAsync(Exception? ex)
    {
        var connId = Context.ConnectionId;
        await _terminal.RemoveViewerAsync(connId);
        await base.OnDisconnectedAsync(ex);
    }
}
