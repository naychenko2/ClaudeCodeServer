namespace ClaudeHomeServer.Services.Backup;

// Зеркало результатов бэкапа в data/backup-state.json.
//
// Виджет главной читает ТОЛЬКО его и никогда не ходит в папку архивов: та обычно
// синхронизируется с облаком, и на спящем/отключённом OneDrive перечисление файлов
// подвесило бы дашборд (а чтение плейсхолдера ещё и потянуло бы скачивание).
public class BackupState
{
    public DateTime? LastSuccessAt { get; set; }
    public string? LastError { get; set; }
    public DateTime? LastAttemptAt { get; set; }
    public List<BackupEntry> Recent { get; set; } = [];

    public const int RecentLimit = 3;

    public static string PathIn(string dataDir) => Path.Combine(dataDir, BackupPaths.StateFileName);

    public static BackupState Load(string dataDir) =>
        JsonFileStore.Load<BackupState>(PathIn(dataDir)) ?? new BackupState();

    /// <summary>
    /// Записать итог снимка в журнал. Зовётся из BackupCore, а не от инициатора: снимок
    /// делают четверо (таймер сервиса, кнопка виджета, меню трея, deploy80 — последние два
    /// отдельными процессами через exe --backup), и запись на стороне вызывающего означала
    /// бы, что виджет не видит ручные бэкапы и показывает «последний вчера» сразу после
    /// свежего снимка из трея. Гонок нет: вызов идёт внутри мьютекса ccs-backup-{dataDir}.
    /// </summary>
    public static void Record(string dataDir, BackupResult result)
    {
        var state = Load(dataDir);
        state.LastAttemptAt = DateTime.Now;

        if (result.Ok && result.Manifest is not null && result.ArchivePath is not null)
        {
            state.LastSuccessAt = result.Manifest.CreatedAt;
            state.LastError = null;

            long size = 0;
            try { size = new FileInfo(result.ArchivePath).Length; } catch { /* размер не критичен */ }

            state.Recent.Insert(0, new BackupEntry
            {
                FileName = Path.GetFileName(result.ArchivePath),
                CreatedAt = result.Manifest.CreatedAt,
                Size = size,
                Summary = result.Manifest.Summary,
            });
            if (state.Recent.Count > RecentLimit)
                state.Recent.RemoveRange(RecentLimit, state.Recent.Count - RecentLimit);
        }
        else
        {
            state.LastError = result.Error;
        }

        JsonFileStore.Save(PathIn(dataDir), state);
    }
}

public class BackupEntry
{
    public string FileName { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public long Size { get; set; }
    public BackupSummary Summary { get; set; } = new();
}
