namespace ClaudeHomeServer.Services;

// Сторож режима «Командная реализация» (Э4): раз в минуту проверяет, не идёт ли волна
// дольше таймаута, и повторяет уведомление по открытым карточкам, на которых человек
// не отвечает. Без первого молчаливо умерший исполнитель оставлял бы штаб в стадии
// «волна N» навсегда; без второго одноразовый оклик о карточке остановки терялся — и
// практика простаивала часами (прод 15→16.08).
// Тик и стиль — как у ChatExpiryService.
public class TeamWaveWatchdog(TeamWaveService waves, ILogger<TeamWaveWatchdog> log) : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Task.Delay(Tick, stoppingToken); }
            catch (OperationCanceledException) { return; }

            try { await waves.CheckStalledWavesAsync(); }
            catch (Exception ex)
            {
                log.LogError(ex, "Проверка зависших волн «Командной реализации» не удалась");
            }

            try { await waves.CheckAwaitingEscalationsAsync(); }
            catch (Exception ex)
            {
                log.LogError(ex, "Проверка ожидающих карточек «Командной реализации» не удалась");
            }
        }
    }
}
