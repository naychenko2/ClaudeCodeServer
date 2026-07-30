namespace ClaudeHomeServer.Services;

// Сторож зависших волн режима «Командная реализация» (Э4): раз в минуту проверяет, не
// идёт ли волна дольше таймаута. Без него молчаливо умерший исполнитель оставлял бы штаб
// в стадии «волна N» навсегда, а человек не знал бы, ждут его или нет.
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
        }
    }
}
