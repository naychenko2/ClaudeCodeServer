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

    private string? UserId => Context.User?.FindFirstValue(JwtRegisteredClaimNames.Sub);
    private bool OwnsProject(string projectId)
        => _projects.GetById(projectId)?.OwnerId == UserId;
    private static HubException Denied() => new("Доступ запрещён");

    /// <summary>Создать новый терминал в проекте.</summary>
    public async Task<TerminalInfoDto> CreateTerminal(string projectId, int cols, int rows, string? name = null)
    {
        if (!OwnsProject(projectId)) throw Denied();
        return await _terminal.CreateAsync(projectId, UserId!, Context.ConnectionId, cols, rows, name);
    }

    /// <summary>Подключиться к существующему терминалу.</summary>
    public async Task<TerminalInfoDto?> ConnectTerminal(string terminalId)
    {
        return await _terminal.ConnectAsync(terminalId, UserId!, Context.ConnectionId);
    }

    /// <summary>Список терминалов проекта.</summary>
    public List<TerminalInfoDto> ListTerminals(string projectId)
    {
        if (!OwnsProject(projectId)) throw Denied();
        return _terminal.ListByProject(projectId);
    }

    /// <summary>Остановить терминал.</summary>
    public async Task StopTerminal(string terminalId)
    {
        await _terminal.StopAsync(terminalId, UserId!);
    }

    /// <summary>Переименовать терминал.</summary>
    public async Task<TerminalInfoDto?> RenameTerminal(string terminalId, string name)
    {
        return await _terminal.RenameAsync(terminalId, UserId!, name);
    }

    /// <summary>Ввод в терминал.</summary>
    public async Task TerminalInput(string terminalId, string data)
    {
        await _terminal.WriteInputAsync(terminalId, data);
    }

    /// <summary>Resize терминала.</summary>
    public Task TerminalResize(string terminalId, int cols, int rows)
    {
        _terminal.Resize(terminalId, cols, rows);
        return Task.CompletedTask;
    }

    public override async Task OnDisconnectedAsync(Exception? ex)
    {
        await _terminal.RemoveViewerAsync(Context.ConnectionId);
        await base.OnDisconnectedAsync(ex);
    }
}
