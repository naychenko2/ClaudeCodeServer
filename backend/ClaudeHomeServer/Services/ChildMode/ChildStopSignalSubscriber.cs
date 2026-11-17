using ClaudeHomeServer.Services.Turn;

namespace ClaudeHomeServer.Services.ChildMode;

/// <summary>
/// Подписчик шины на маркер стоп-сигнала тьютора (KM-B6d): событие <see cref="ChildStopSignalMarker"/>
/// из TurnCompletion → <see cref="ChildStopSignalService.RaiseAsync"/>. Так ядру сессий не нужна
/// ссылка на сервис рассылки (Main), а решение о схлопывании по-прежнему принимает сам сервис.
/// </summary>
public sealed class ChildStopSignalSubscriber(ChildStopSignalService signals, ITurnEventBus events) : IHostedService
{
    private int _subscribed;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Отписки у шины нет: повторный старт хоста не должен удваивать подписчика
        if (Interlocked.Exchange(ref _subscribed, 1) == 0)
            events.OnNotification<ChildStopSignalMarker>(OnMarker, "ChildStopSignalSubscriber");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    internal Task OnMarker(ChildStopSignalMarker e) =>
        e.Turn.OwnerId is { Length: > 0 } owner
            ? signals.RaiseAsync(owner, e.Turn.SessionId, StopSignalSource.Tutor)
            : Task.CompletedTask;
}
