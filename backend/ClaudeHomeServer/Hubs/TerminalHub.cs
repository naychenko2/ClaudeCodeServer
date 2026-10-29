using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ClaudeHomeServer.Services.Access;
using ClaudeHomeServer.Services.Terminal;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using ClaudeHomeServer.Services.Composition;

namespace ClaudeHomeServer.Hubs;

[Authorize]
public class TerminalHub : Hub
{
    private readonly TerminalService _terminal;
    private readonly IProjectAccess _access;

    public TerminalHub(TerminalService terminal, IProjectAccess access)
    {
        _terminal = terminal;
        _access = access;
    }

    private string? UserId => Context.User?.FindFirstValue(JwtRegisteredClaimNames.Sub);

    // Право на проект резолвит одна точка доступа (S0-6), а не сравнение владельца с id
    // соединения: просмотр — Read, запуск терминала (shell на хосте) — RunTurn, только владелец (в S0 роль одна — владелец,
    // поведение не меняется). Ключом самого терминала остаётся создавший его человек
    // (TerminalInstance.UserId): терминалы не входят в перечень хранилищ с ключом {ownerId}
    // (карта S0-3 §3), их поведение в общем проекте решает S1.
    private bool CanProject(string projectId, ProjectRight right = ProjectRight.Read) =>
        _access.Resolve(UserId, projectId, right) is not null;

    private static HubException Denied() => new("Доступ запрещён");

    /// <summary>Создать новый терминал в проекте.</summary>
    [ProjectCapability(ProjectCapabilityArea.FileBound)]
    public async Task<TerminalInfoDto> CreateTerminal(string projectId, int cols, int rows, string? name = null)
    {
        if (!CanProject(projectId, ProjectRight.RunTurn)) throw Denied();
        return await _terminal.CreateAsync(projectId, UserId!, Context.ConnectionId, cols, rows, name);
    }

    /// <summary>Подключиться к существующему терминалу.</summary>
    public async Task<TerminalInfoDto?> ConnectTerminal(string terminalId)
    {
        return await _terminal.ConnectAsync(terminalId, UserId!, Context.ConnectionId);
    }

    /// <summary>Список терминалов проекта.</summary>
    [ProjectCapability(ProjectCapabilityArea.FileBound)]
    public List<TerminalInfoDto> ListTerminals(string projectId)
    {
        if (!CanProject(projectId)) throw Denied();
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
        if (!_terminal.Owns(terminalId, UserId!)) throw Denied();
        await _terminal.WriteInputAsync(terminalId, data);
    }

    /// <summary>Resize терминала.</summary>
    public Task TerminalResize(string terminalId, int cols, int rows)
    {
        if (!_terminal.Owns(terminalId, UserId!)) throw Denied();
        _terminal.Resize(terminalId, cols, rows);
        return Task.CompletedTask;
    }

    public override async Task OnDisconnectedAsync(Exception? ex)
    {
        await _terminal.RemoveViewerAsync(Context.ConnectionId);
        await base.OnDisconnectedAsync(ex);
    }
}
