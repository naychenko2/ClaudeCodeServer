namespace ClaudeHomeServer.Services.Composition;

// Узкий шов SessionManager.SendOrEnqueueAsync для выноса Deploy (Этап 5, волна 2).
// DeployReportService докладывает итог выкатки в чат-инициатор: шлёт сообщение
// в сессию (или ставит в очередь, если чат занят). Existence-check делает вызывающий
// код через ISessionDirectory.GetById — этот шов только отправляет.
public interface ISessionMessageSender
{
    Task<bool> SendOrEnqueueAsync(string sessionId, string text, bool suppressTasksExecute = false);
}
