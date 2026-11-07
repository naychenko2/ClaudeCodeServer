namespace ClaudeHomeServer.Services.Composition;

// Загрузка и периодический сброс счётчика использования подсистем на диск (шаг S1b).
// Load — на старте, Save — раз в 5 минут и в StopAsync (дожидается текущего сброса:
// стор сериализует Save семафором на путь файла). Исключение сброса наружу не выходит:
// по умолчанию .NET гасит весь хост при падении BackgroundService.
public sealed class SubsystemUsageFlushService(
    SubsystemUsageStore store, IConfiguration config, ILogger<SubsystemUsageFlushService> log)
    : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);
    public const string FileName = "subsystem-usage.json";

    private readonly string _path = Path.Combine(
        Path.GetDirectoryName(Path.GetFullPath(config["DataPath"]
            ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json")))!,
        FileName);

    // Load — до приёма запросов (hosted стартуют раньше Kestrel): ранний Hit не потеряет файловую историю.
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        try { store.Load(_path, DateTimeOffset.UtcNow); }
        catch (Exception ex) { log.LogWarning(ex, "Не удалось загрузить счётчик использования из {Path}", _path); }
        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                Flush();
        }
        catch (OperationCanceledException) { /* штатная остановка */ }
    }

    // Финальный Flush — в finally: любой отказ base.StopAsync не смеет снять последний сброс
    // (данные за последние 5 минут). В прежних реализациях .NET отменённый токен давал
    // OperationCanceledException именно здесь; net10 base не ретраит, но полагаться на это
    // нечем (R-S1b, п.3). Исключение base наружу не гасится — вызывающий видит его, как раньше.
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        try { await base.StopAsync(cancellationToken); }
        finally { Flush(); }
    }

    private void Flush()
    {
        try { store.Save(_path); }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Не удалось сбросить счётчик использования подсистем в {Path}", _path);
        }
    }
}
