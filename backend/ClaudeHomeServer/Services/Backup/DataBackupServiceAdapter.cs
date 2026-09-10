namespace ClaudeHomeServer.Services.Backup;

// Адаптер BackupCore → IDataBackupService (Core).
// Шов для вертикали ProjectIcons (Этап 5, волна C, шаг 2): миграция значков в
// отдельной сборке снимает снимок data через Core-интерфейс; конкретный
// `BackupCore.Snapshot(BackupContext.FromConfiguration(...), log)` живёт
// здесь же — `Backup` намеренно не выносится в отдельную сборку (см. ADR-014:
// «Backup — вычеркнут, не вертикаль ❌», инфраструктурный срез поперёк всех).
//
// Маппинг: `BackupResult { Ok, ArchivePath, Error, Manifest }` →
// `DataBackupResult { Ok, ArchivePath, Error }` (манифест ProjectIcons не нужен).
//
// Регистрация: один синглтон в Program.cs.
// Размещение: `Services/Backup/` рядом с `BackupCore` — не `Services/` корень,
// чтобы не срабатывал root-сторож `RootSubsystemBoundaryTests` (root не должен
// держать прямую ссылку на подсистемную вертикаль, а находясь в той же
// папке/неймспейсе, что и `BackupCore`, адаптер — peer).
public sealed class DataBackupServiceAdapter : IDataBackupService
{
    public DataBackupResult Snapshot(IConfiguration config, ILogger log)
    {
        var result = BackupCore.Snapshot(BackupContext.FromConfiguration(config), log);
        return new DataBackupResult(result.Ok, result.ArchivePath, result.Error);
    }
}
