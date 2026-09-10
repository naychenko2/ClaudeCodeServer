namespace ClaudeHomeServer.Services.Composition;

// Шов рассылки WS-событий терминала фронту (Этап 5, волна C, шаг 2). Прежде
// TerminalService держал `IHubContext<TerminalHub>` из Main (`Services.Terminal`
// → `Hubs.TerminalHub` напрямую), и вертикаль физически не могла выехать
// в отдельную сборку без ProjectReference на Main. Здесь — три метода
// по образцу `ISessionBroadcaster` (см. CLAUDE.md «SignalR Hub`): вертикаль
// шлёт и управляет группами через Core-интерфейс, реализация в Main
// держит `IHubContext<TerminalHub>`.
//
// Префикс имени группы (`"term_" + terminalId`) собирает сам TerminalService
// (здесь нет строки-константы — формат группы знает только он); реализация
// не дописывает ничего к имени.
//
// Метод «message» канала — единственный (контракт с фронтом, см.
// docs/architecture/api.md §SignalR Hub). Под него и лежит параметр
// `object` (конкретный тип — record из `Protocol.*`).
public interface ITerminalHubNotifier
{
    // Реплей буфера новому подключению: конктретный connId,
    // терминал ещё не в группе (SendAsync до AddToGroupAsync).
    Task SendToClientAsync(string connId, object message);

    // Рассылка событий терминала всем зрителям группы.
    Task SendToGroupAsync(string groupName, object message);

    // Управление членством: добавление подключения в группу терминала.
    Task AddToGroupAsync(string connId, string groupName);
}
