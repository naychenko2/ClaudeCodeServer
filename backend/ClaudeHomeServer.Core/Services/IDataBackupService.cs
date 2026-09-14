namespace ClaudeHomeServer.Services;

// Шов «снимок data перед необратимой операцией» для ProjectIcons (Этап 5,
// волна C, шаг 2). Прежде ProjectIconMigration звал
// `BackupCore.Snapshot(BackupContext.FromConfiguration(config), log)` напрямую
// из Main, и комментарий `ProjectIconMigration.cs:78-84` помечал это «полумерой»:
// вынести примитив в Core мешало направление `Main → Core` (Core не видит
// Main/Backup), обёртка в root Services нарушала root-сторож. Курс Андрея
// 2026-09-08 (вынос ВСЕХ вертикалей) делает полумеру обязательной: либо шов,
// либо не выносим. Этот интерфейс — формализация шва.
//
// Результат минимальный: Ok/Error и опциональный путь к архиву. Манифест
// не нужен ProjectIcons (миграция значков использует только факт успеха и
// текст ошибки для лога).
public interface IDataBackupService
{
    // Снимок data/ согласно конфигурации. `config` — корневой IConfiguration
    // (миграция держит свой снимок, чтобы не привязываться к стейту DI).
    // `log` — логгер вызывающей стороны (имя категории = имя вертикали).
    DataBackupResult Snapshot(IConfiguration config, ILogger log);
}

// DTO результата бэкапа (Core, не привязан к Backup.BackupResult из Main).
// Поля совпадают семантически: Ok/ArchivePath/Error; манифест опущен —
// ProjectIconMigration его не использует, а другим потребителям шва
// (если появятся) достаточно Ok/Error.
public record DataBackupResult(bool Ok, string? ArchivePath, string? Error);
