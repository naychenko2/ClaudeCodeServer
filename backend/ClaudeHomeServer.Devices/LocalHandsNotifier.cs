namespace ClaudeHomeServer.Services.Devices;

/// <summary>
/// Рассылка статуса рук локального проекта (ADR-016 §7) в чат хода — событие <c>hands_status</c>.
/// Интерфейс объявляет вертикаль, реализует Main: нужен веер ядра сессий
/// (<c>SessionManager.BroadcastSessionMessageAsync</c>).
/// </summary>
public interface ILocalHandsNotifier
{
    /// <param name="state">Одно из <c>HandsChatStates</c>.</param>
    /// <param name="reason"><c>HandsEndReason</c> у состояния «остановлено».</param>
    Task HandsStatusAsync(string ownerId, string deviceId, string sessionId, string state, string? reason,
        CancellationToken ct = default);
}
