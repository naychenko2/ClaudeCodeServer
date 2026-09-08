using ClaudeHomeServer.Hubs;
using ClaudeHomeServer.Protocol;
using Microsoft.AspNetCore.SignalR;

namespace ClaudeHomeServer.Services.Composition;

// Реализация ISessionBroadcaster (Core) поверх IHubContext<SessionHub>.
// Тонкий адаптер: префиксы групп "user_" и "project_" собираются ТОЛЬКО ЗДЕСЬ.
// Это единственная точка, где строки-префиксы материализуются — раньше они
// собирались вручную в двух десятках потребителей, и опечатка давала молчаливую
// недоставку (сигнал уходил в несуществующую группу, исключения нет, в логах
// пусто). Теперь любая правка префикса — один коммит.
//
// Канал — ровно один: метод «message», параметр — ServerMessage. Это контракт
// с фронтом; см. SignalR Hub в docs/architecture/api.md.
//
// Что НЕ покрывает (см. задачу Ф4 и ADR-014):
//   - DesktopCallRouter — IHubContext<DeviceHub> + типизированные вызовы
//     Clients.Client(connectionId).Call/Go/Cancel; оставлен как есть;
//   - TerminalService — IHubContext<TerminalHub> + Groups.AddToGroupAsync,
//     управление членством; оставлен как есть.
//
// Регистрация — в Program.cs, рядом с другими форвардерами Core-швов
// (NotesHubNotifier и т.п.).
public sealed class SessionHubBroadcaster(IHubContext<SessionHub> hub) : ISessionBroadcaster
{
    // Префиксы групп — единственный источник правды в коде. Совпадают с
    // SessionHub.JoinUser/JoinProject, которые добавляют соединение в группу.
    // Формат preview_<projectId>:<serviceId> зеркалится из DevServerService.LogGroup —
    // SessionHub зовёт её же для Groups.AddToGroupAsync; расхождение поймает парный
    // тест (см. отчёт Ф4, обоснование 4-го метода шва).
    private const string OwnerGroupPrefix = "user_";
    private const string ProjectGroupPrefix = "project_";
    private const string PreviewLogGroupPrefix = "preview_";

    public Task ToSession(string sessionId, ServerMessage message) =>
        hub.Clients.Group(sessionId).SendAsync("message", message);

    public Task ToOwner(string ownerId, ServerMessage message) =>
        hub.Clients.Group(OwnerGroupPrefix + ownerId).SendAsync("message", message);

    public Task ToProject(string projectId, ServerMessage message) =>
        hub.Clients.Group(ProjectGroupPrefix + projectId).SendAsync("message", message);

    public Task ToPreviewLog(string projectId, string serviceId, ServerMessage message) =>
        hub.Clients.Group($"{PreviewLogGroupPrefix}{projectId}:{serviceId}")
            .SendAsync("message", message);
}
