namespace ClaudeHomeServer.Services.Backup;

// Снятие снапшотов по расписанию.
//
// Расписание интервальное, а не календарное («каждый день в 03:00»): машина — Windows,
// ночью может спать или быть выключена, и cron-подход молча пропустил бы окно. Проверяем
// раз в час; если с последнего успеха прошло больше заданного срока — снимаем. Проспала
// ночь — снимок сделается утром, ничего не теряется.
//
// Настройки — секция «Backup» конфига (см. BackupOptions), правятся руками.
public class BackupService(
    IConfiguration config,
    ProjectManager projects,
    ILogger<BackupService> log) : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromMinutes(60);
    // Не на первой же секунде старта: дать сторам подняться и не толкаться с прогревами
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(2);


    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        WarnAboutBadPath();

        try { await Task.Delay(StartupDelay, stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (IsDue()) RunSnapshot();
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Плановый бэкап не выполнен");
            }

            try { await Task.Delay(Tick, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    // Путь задан руками в конфиге, поэтому проверить его можно только предупреждением.
    // Молчать нельзя: архивы внутри папки проекта уедут документами в базу знаний, а
    // внутри корня песочницы станут читаемыми изолированным пользователям.
    private void WarnAboutBadPath()
    {
        var options = BackupOptions.From(config);
        if (!options.Enabled || string.IsNullOrWhiteSpace(options.Path)) return;

        var problem = ValidateBackupPath(options.Path);
        if (problem is not null)
            log.LogWarning("Backup:Path — {Problem}. Архивы будут складываться туда, куда указано; " +
                           "проверь настройку", problem);
    }

    private bool IsDue()
    {
        var options = BackupOptions.From(config);
        if (!options.Enabled) return false;

        var state = LoadState();
        if (state.LastSuccessAt is null) return true;

        return DateTime.Now - state.LastSuccessAt.Value >= TimeSpan.FromHours(options.IntervalHours);
    }

    /// <summary>Снять снапшот. Журнал для виджета пишет само ядро (BackupCore).</summary>
    public BackupResult RunSnapshot() => BackupCore.Snapshot(BuildContext(), log);

    public BackupContext BuildContext() => BackupContext.FromConfiguration(config);

    public BackupOptions Options => BackupOptions.From(config);

    public BackupState LoadState() => BackupState.Load(BuildContext().DataDir);

    /// <summary>Проверить путь для архивов настройками этого инстанса; null = путь годен.</summary>
    public string? ValidateBackupPath(string path)
    {
        var ctx = BuildContext();
        var roots = projects.GetAll().Select(p => p.RootPath).Where(p => !string.IsNullOrWhiteSpace(p));
        return BackupPaths.ValidateBackupPath(
            path, ctx.DataDir, ctx.BaseDirectory, roots, config["Sandbox:ProjectsRoot"]);
    }

}
