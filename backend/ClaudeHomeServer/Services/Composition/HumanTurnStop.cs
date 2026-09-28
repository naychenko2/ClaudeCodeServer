using ClaudeHomeServer.Services.Execution;

namespace ClaudeHomeServer.Services.Composition;

// «Стоп» человека на устройстве (трей рук) ведётся ровно тем путём, что веб-«Стоп»
// (SessionHub.Interrupt): прерывание хода без фолбэка, отметка в истории, заморозка очереди,
// а в чате исполнителя задачи — отметка остановки человеком. Живёт в Main по той же причине,
// что LocalHandsNotifier: ядро сессий.
public sealed class HumanTurnStop(SessionManager sessions, TaskExecutionService? executions = null,
    ILogger<HumanTurnStop>? log = null) : IHumanTurnStop
{
    public void StoppedByHuman(string sessionId)
    {
        log?.LogWarning("«Стоп» из трея рук: ход чата {Session} прерван человеком", sessionId);
        sessions.Interrupt(sessionId);
        if (executions is not null) _ = MarkTaskAsync(executions, sessionId);
    }

    private async Task MarkTaskAsync(TaskExecutionService executions, string sessionId)
    {
        try { await executions.MarkStoppedByUserAsync(sessionId); }
        catch (Exception e)
        {
            log?.LogWarning(e, "Остановка исполнителя задачи чата {Session} не отмечена", sessionId);
        }
    }
}
