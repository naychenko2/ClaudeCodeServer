using ClaudeHomeServer.Services.Desktop;
using ClaudeHomeServer.Services.Execution;

namespace ClaudeHomeServer.Services.Composition;

/// <summary>
/// Диспетчер выхода устройства в онлайн (ADR-016, вариант А плана §5). Разовая фоновая работа
/// по локальному проекту (исполнитель задачи, под-задача штаба, сообщение в очереди чата) при
/// офлайн-устройстве не падает, а ждёт его; периодическая (автоматизации, опросы сторожа)
/// пропускается и догоняет одно последнее. Здесь — два общих триггера для всех механизмов:
/// <list type="bullet">
/// <item>событие маршрутизатора устройств «представилось» (Hello) — ждущее стартует сразу;</item>
/// <item>поминутный проход — потолок ожидания 24 ч и догон, если событие потерялось (рестарт
/// сервера, устройство вышло в онлайн между проверкой и отметкой ожидания).</item>
/// </list>
/// Обработчики (<see cref="IDeviceOnlineHandler"/>) резолвятся при каждом срабатывании, а не в
/// конструкторе: подписчики — тяжёлые синглтоны ядра, и ранний резолв из наблюдателя
/// маршрутизатора замкнул бы цикл в DI.
/// </summary>
public sealed class DeviceOnlineDispatcher(
    IServiceProvider services,
    ILogger<DeviceOnlineDispatcher> log,
    TimeProvider? time = null) : BackgroundService, IDeviceConnectionObserver
{
    internal static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(1);

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    // Один проход за раз: онлайн-событие и тик не гоняются за одними и теми же сущностями
    private readonly SemaphoreSlim _gate = new(1, 1);

    public Task OnDeviceOnlineAsync(DeviceConnection connection, CancellationToken ct = default)
    {
        // Hello устройства не ждёт запуска ходов: разбор уходит в фон
        _ = Task.Run(() => DispatchOnlineAsync(connection.OwnerId, connection.DeviceId, CancellationToken.None));
        return Task.CompletedTask;
    }

    public Task OnDeviceOfflineAsync(DeviceConnection connection, CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>Устройство вышло в онлайн: каждому механизму — запустить то, что его ждало.</summary>
    public async Task DispatchOnlineAsync(string ownerId, string deviceId, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            log.LogInformation("Устройство {DeviceId} владельца {OwnerId} в сети — запускаю ждавшую работу", deviceId, ownerId);
            foreach (var handler in Handlers())
            {
                try { await handler.OnDeviceOnlineAsync(ownerId, deviceId, ct); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log.LogError(ex, "Обработчик {Handler} не разобрал выход устройства {DeviceId} в онлайн",
                        handler.GetType().Name, deviceId);
                }
            }
        }
        finally { _gate.Release(); }
    }

    /// <summary>Периодический проход всех механизмов: потолок ожидания и догон.</summary>
    public async Task SweepAsync(DateTime nowUtc, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            foreach (var handler in Handlers())
            {
                try { await handler.SweepAsync(nowUtc, ct); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log.LogError(ex, "Обработчик {Handler}: проход ожидания устройства не удался", handler.GetType().Name);
                }
            }
        }
        finally { _gate.Release(); }
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(SweepInterval, _time);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
                await SweepAsync(_time.GetUtcNow().UtcDateTime, ct);
        }
        catch (OperationCanceledException) { /* остановка приложения */ }
    }

    private IEnumerable<IDeviceOnlineHandler> Handlers() => services.GetServices<IDeviceOnlineHandler>();
}
