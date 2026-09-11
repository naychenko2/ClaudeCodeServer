namespace ClaudeHomeServer.Services.Images;

/// <summary>
/// Триггеры догоняющей генерации по времени: отложенный прогон на старте сервера (сущности
/// могли остаться без картинки, пока инстанс был выключен) и периодический тик — им очередь
/// доходит и после того, как провайдер починился сам, без единого HTTP-запроса.
/// Третий триггер — <see cref="ImageBackfillService.KickOff"/> из настроек «Применение».
/// </summary>
public sealed class ImageBackfillHostedService(
    ImageBackfillService backfill, IHostEnvironment env, IConfiguration config,
    ILogger<ImageBackfillHostedService> log) : BackgroundService
{
    // Даём серверу подняться: прогон ходит в сеть и не должен спорить за старт
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(30);
    // Тик редкий: очередь пополняется руками пользователя, а не потоком событий
    private static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(10);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Тот же гейт, что у AddHosted в Program.cs: в Testing-среде фоновые циклы не
        // крутим (регистрация идёт из AddImageGeneration, где среды не видно)
        if (env.IsEnvironment("Testing") && !config.GetValue<bool>("Testing:EnableHostedServices"))
            return;

        try
        {
            await Task.Delay(StartupDelay, ct);
            using var timer = new PeriodicTimer(TickInterval);
            do
            {
                try { await backfill.RunAllAsync(ct); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { log.LogError(ex, "Прогон догоняющей генерации картинок сорвался"); }
            }
            while (await timer.WaitForNextTickAsync(ct));
        }
        catch (OperationCanceledException) { /* остановка приложения */ }
    }
}
