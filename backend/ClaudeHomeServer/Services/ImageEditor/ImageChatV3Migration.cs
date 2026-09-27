namespace ClaudeHomeServer.Services;

// Разовая миграция чатов картинки v2 («hero.png · правка») на редактор v3 (ADR-019, решение 2).
// Отдельного чата у картинки больше нет, поэтому такие чаты уходят в архив: ArchivedAt = now,
// UpdatedAt не меняется, маркер — SessionImageChat.MigratedAt. Транскрипт и --resume целы:
// первое же сообщение вернёт чат из архива, дальше это обычный чат проекта.
//
// Маркер-файла, как у GlmModelAliasMigration, нет намеренно: признак выполненной миграции
// лежит в самой сессии. Поэтому миграция идемпотентна и безопасна на каждом старте, а
// восстановленный старый бэкап (чаты без маркера) мигрирует тем же кодом. BackupSchema.Version
// не растёт: поле аддитивное. Ошибки не роняют старт приложения.
//
// Живёт в Main, а не в модуле редактора: правит sessions.json через SessionManager, а модуль
// ядра сессий не видит. Выключенный модуль миграцию не отменяет — чаты v2 всё равно мертвы.
public sealed class ImageChatV3Migration(SessionManager sessions, ILogger<ImageChatV3Migration> log,
    TimeProvider? time = null) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var migrated = Run();
            if (migrated > 0)
                log.LogInformation("Редактор картинок v3: чатов картинки v2 убрано в архив — {Count}", migrated);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Миграция чатов картинки v2 не выполнена — старт продолжается");
        }
        return Task.CompletedTask;
    }

    public int Run() => sessions.ArchiveLegacyImageChats((time ?? TimeProvider.System).GetUtcNow().UtcDateTime);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
