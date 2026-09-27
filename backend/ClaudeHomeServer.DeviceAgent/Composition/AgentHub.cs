using System.Collections.Concurrent;
using System.Security.Claims;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.ProjectServices;
using ClaudeHomeServer.Services.Terminal;
using Microsoft.AspNetCore.SignalR;

namespace ClaudeHomeServer.DeviceAgent.Composition;

/// <summary>
/// Проекты, которые агент знает по принятым билетам (задача 4.3). Вертикали Terminal и
/// ProjectServices спрашивают проект по id — у агента своего реестра проектов нет, есть
/// только то, что сервер подтвердил билетом, с корнем, уже сверенным с корнями машины.
/// </summary>
internal sealed class AgentProjectDirectory : IProjectManager
{
    private readonly ConcurrentDictionary<string, Project> _projects = new();

    public void Remember(Project project) => _projects[project.Id] = project;

    public Project? GetById(string id) => _projects.TryGetValue(id, out var p) ? p : null;
    public IReadOnlyCollection<Project> GetByOwner(string userId) => _projects.Values.Where(p => p.OwnerId == userId).ToList();
    public IReadOnlyCollection<Project> GetAll() => _projects.Values.ToList();
    public IReadOnlyCollection<Project> GetByRootPath(string rootPath) =>
        _projects.Values.Where(p => AgentPathPolicy.PathComparer.Equals(p.RootPath, rootPath)).ToList();
}

/// <summary>Песочницы у агента нет: пул портов песочницы вертикали не нужен.</summary>
internal sealed class NoSandboxPorts : Services.Execution.ISandboxPortRange
{
    public int PortRangeStart => 0;
    public int PortRangeSize => 0;
}

/// <summary>
/// Хаб агента (задача 4.3): терминалы проекта и логи его дев-серверов. Методы и события —
/// те же, что у серверных <c>TerminalHub</c> и <c>SessionHub.JoinPreviewLog</c> (сторож —
/// контракт-тест хабов), клиенту меняется только адрес. Подключение несёт билет хаба,
/// выданный на один проект: владелец и проект — из него, аргументы с чужим проектом
/// отвергаются. Сами терминалы и дев-серверы — вертикали Terminal и ProjectServices.
/// </summary>
public sealed class AgentHub : Hub
{
    internal const string ProjectClaim = "agent.project";

    private readonly TerminalService _terminal;
    private readonly DevServerService _devServer;

    public AgentHub(TerminalService terminal, DevServerService devServer)
    {
        _terminal = terminal;
        _devServer = devServer;
    }

    private string OwnerId => Context.User?.FindFirstValue(ClaimTypes.NameIdentifier) ?? throw Denied();
    private string ProjectId => Context.User?.FindFirstValue(ProjectClaim) ?? throw Denied();
    private static HubException Denied() => new("Доступ запрещён");

    private void EnsureProject(string projectId)
    {
        if (projectId != ProjectId) throw new HubException("Подключение выдано на другой проект");
    }

    // Терминал свой и из проекта подключения: владелец — вертикалью, проект — здесь
    private bool IsMine(string terminalId) =>
        _terminal.Owns(terminalId, OwnerId) && _terminal.ListByProject(ProjectId).Any(t => t.Id == terminalId);

    public override async Task OnConnectedAsync()
    {
        // Статусы дев-серверов вертикаль шлёт владельцу (ToOwner), как на сервере
        await Groups.AddToGroupAsync(Context.ConnectionId, AgentHubNotifier.OwnerGroup(OwnerId));
        await base.OnConnectedAsync();
    }

    /// <summary>Создать новый терминал в проекте.</summary>
    public async Task<TerminalInfoDto> CreateTerminal(string projectId, int cols, int rows, string? name = null)
    {
        EnsureProject(projectId);
        return await _terminal.CreateAsync(projectId, OwnerId, Context.ConnectionId, cols, rows, name);
    }

    /// <summary>Подключиться к существующему терминалу.</summary>
    public async Task<TerminalInfoDto?> ConnectTerminal(string terminalId) =>
        IsMine(terminalId) ? await _terminal.ConnectAsync(terminalId, OwnerId, Context.ConnectionId) : null;

    /// <summary>Список терминалов проекта.</summary>
    public List<TerminalInfoDto> ListTerminals(string projectId)
    {
        EnsureProject(projectId);
        return _terminal.ListByProject(projectId);
    }

    /// <summary>Остановить терминал.</summary>
    public async Task StopTerminal(string terminalId)
    {
        if (!IsMine(terminalId)) return;
        await _terminal.StopAsync(terminalId, OwnerId);
    }

    /// <summary>Переименовать терминал.</summary>
    public async Task<TerminalInfoDto?> RenameTerminal(string terminalId, string name) =>
        IsMine(terminalId) ? await _terminal.RenameAsync(terminalId, OwnerId, name) : null;

    /// <summary>Ввод в терминал.</summary>
    public async Task TerminalInput(string terminalId, string data)
    {
        if (!IsMine(terminalId)) throw Denied();
        await _terminal.WriteInputAsync(terminalId, data);
    }

    /// <summary>Resize терминала.</summary>
    public Task TerminalResize(string terminalId, int cols, int rows)
    {
        if (!IsMine(terminalId)) throw Denied();
        _terminal.Resize(terminalId, cols, rows);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Подписка на вывод дев-сервера. Накопленный буфер возвращается вызывающему, снимок —
    /// до входа в группу: смысл тот же, что у <c>SessionHub.JoinPreviewLog</c> сервера.
    /// </summary>
    public async Task<string?> JoinPreviewLog(string projectId, string serviceId)
    {
        EnsureProject(projectId);
        var buffered = _devServer.GetLogBuffer(projectId, serviceId, OwnerId);
        await Groups.AddToGroupAsync(Context.ConnectionId, DevServerService.LogGroup(projectId, serviceId));
        return buffered;
    }

    public Task LeavePreviewLog(string projectId, string serviceId) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, DevServerService.LogGroup(projectId, serviceId));

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        await _terminal.RemoveViewerAsync(Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }
}

/// <summary>
/// Доставка событий вертикалей в хаб агента: терминал (<see cref="ITerminalHubNotifier"/>) и
/// дев-серверы (<see cref="ISessionBroadcaster"/>). Имя метода клиента — «message», как у
/// серверных хабов; имена групп — те же, что у сервера.
/// </summary>
internal sealed class AgentHubNotifier(IHubContext<AgentHub> hub) : ITerminalHubNotifier, ISessionBroadcaster
{
    public static string OwnerGroup(string ownerId) => "user_" + ownerId;

    public Task SendToClientAsync(string connId, object message) =>
        hub.Clients.Client(connId).SendAsync("message", message);

    public Task SendToGroupAsync(string groupName, object message) =>
        hub.Clients.Group(groupName).SendAsync("message", message);

    public Task AddToGroupAsync(string connId, string groupName) =>
        hub.Groups.AddToGroupAsync(connId, groupName);

    public Task ToOwner(string ownerId, ServerMessage message) =>
        hub.Clients.Group(OwnerGroup(ownerId)).SendAsync("message", message);

    public Task ToPreviewLog(string projectId, string serviceId, ServerMessage message) =>
        hub.Clients.Group(DevServerService.LogGroup(projectId, serviceId)).SendAsync("message", message);

    // Чатов и проектных групп у хаба агента нет: вертикали, которые он собирает, сюда не шлют
    public Task ToSession(string sessionId, ServerMessage message) => Task.CompletedTask;
    public Task ToSessionExcept(string sessionId, string exceptConnectionId, ServerMessage message) => Task.CompletedTask;
    public Task ToProject(string projectId, ServerMessage message) => Task.CompletedTask;
}
