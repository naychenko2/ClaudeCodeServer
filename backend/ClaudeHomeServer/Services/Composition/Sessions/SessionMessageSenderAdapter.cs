using ClaudeHomeServer.Services.Composition;

namespace ClaudeHomeServer.Services.Composition.Sessions;

// Реализация ISessionMessageSender (Core) поверх SessionManager (Main).
// Тонкий форвардер для DeployReportService: отправка доклада в чат-инициатор.
// SessionManager — god-объект; вертикаль Deploy не должна знать о нём напрямую.
public sealed class SessionMessageSenderAdapter(SessionManager sessions) : ISessionMessageSender
{
    public Task<bool> SendOrEnqueueAsync(string sessionId, string text, bool suppressTasksExecute = false) =>
        sessions.SendOrEnqueueAsync(sessionId, text, suppressTasksExecute: suppressTasksExecute);
}
