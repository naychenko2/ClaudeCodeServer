using ClaudeHomeServer.Services.Llm;

namespace ClaudeHomeServer.Services.Watchdog;

// Конкретная реализация IWatchdogAlarm: доставка будильника через SessionMessagingService.
// Живёт в Main: SessionMessagingService — тип Main (не в Llm-сборке),
// а IWatchdogAlarm — в Watchdog-сборке. Адаптер связывает их.
public sealed class WatchdogAlarm(SessionMessagingService messaging) : IWatchdogAlarm
{
    public async Task<bool> DeliverAsync(string ownerId, string sessionId, string text)
    {
        // callerSessionId: null — квота пробуждения штаба не тратится, глубина делегирования
        // берётся из agentDepthFallback (0): будильник — системный ход, не агентный.
        // preempt: false — живой ход получателя не рвём, сообщение встанет в очередь и
        // доставится по result. wait: none — сервис не ждёт завершения хода.
        var outcome = await messaging.SendAsync(ownerId, sessionId, text,
            callerSessionId: null, senderSessionId: null, agentDepthFallback: 0,
            wait: "none", timeoutSec: null, preempt: false);
        return outcome switch
        {
            SessionMessagingService.SendOutcome.Completed
                or SessionMessagingService.SendOutcome.Queued
                or SessionMessagingService.SendOutcome.Running => true,
            _ => false,
        };
    }
}
