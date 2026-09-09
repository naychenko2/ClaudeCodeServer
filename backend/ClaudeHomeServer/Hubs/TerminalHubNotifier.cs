using ClaudeHomeServer.Services.Composition;
using Microsoft.AspNetCore.SignalR;

namespace ClaudeHomeServer.Hubs;

// Адаптер IHubContext<TerminalHub> → ITerminalHubNotifier (Core).
// Шов для вертикали Terminal (Этап 5, волна C, шаг 2): TerminalService в
// отдельной сборке шлёт WS-события через Core-интерфейс, реализация держит
// `IHubContext<TerminalHub>` из `Hubs/` Main (терминальный хаб остаётся
// транспортом в Main, как `SessionHub` и `DeviceHub`).
//
// Размещение: `Hubs/` рядом с `TerminalHub` — формально `Hubs` неймспейс
// НЕ является вертикалью с точки зрения root-сторожа (см.
// `RootAllowedSubVerticalPrefixes` — там только `Services.*` инфраструктура,
// `Hubs` нет), а адаптер формально шовный код, не имеющий ничего против
// peer-Hubs-классов.
//
// Регистрация: один синглтон в Program.cs рядом с регистрацией TerminalService.
public sealed class TerminalHubNotifier : ITerminalHubNotifier
{
    private readonly IHubContext<TerminalHub> _hub;

    public TerminalHubNotifier(IHubContext<TerminalHub> hub)
    {
        _hub = hub;
    }

    public Task SendToClientAsync(string connId, object message) =>
        _hub.Clients.Client(connId).SendAsync("message", message);

    public Task SendToGroupAsync(string groupName, object message) =>
        _hub.Clients.Group(groupName).SendAsync("message", message);

    public Task AddToGroupAsync(string connId, string groupName) =>
        _hub.Groups.AddToGroupAsync(connId, groupName);
}
