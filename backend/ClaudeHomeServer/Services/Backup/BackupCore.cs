using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace ClaudeHomeServer.Services.Backup;

public record BackupResult(bool Ok, string? ArchivePath, string? Error, BackupManifest? Manifest);
public record RestoreResult(bool Ok, string? Error, string? MovedDataTo, BackupManifest? Manifest);

// Снятие и восстановление снапшота каталога data.
public static class BackupCore
{
    private const string ManifestEntryName = "manifest.json";
    public const string ManifestSuffix = ".manifest.json";
    public const string ArchivePrefix = "ccs-";
    public const string ArchiveExtension = ".zip";
    // Файл лога событий копируется не как файл, а через online-backup API SQLite
    private const string EventsDbName = "project-events.db";

    private static readonly JsonSerializerOptions ManifestJson = new() { WriteIndented = true };

    // --- Снапшот ---

    /// <param name="recordState">
    /// Писать ли итог в data/backup-state.json (журнал для виджета). Выключается для
    /// страховочного снимка перед восстановлением: тот делается по своей причине и не
    /// должен подменять собой «последний плановый бэкап» в сводке на главной.
    /// </param>
    /// <param name="rotate">
    /// Чистить ли старые архивы. Тоже выключается для страховочного снимка: он добавляет
    /// в папку свежий файл, пересчёт корзин 7/4/3 мог бы выкинуть из окна ИМЕННО тот архив,
    /// который сейчас восстанавливают (и соседние, из которых пользователь выбирал) —
    /// текущее восстановление это переживёт, а повторить его будет уже не из чего.
    /// </param>
    public static BackupResult Snapshot(
        BackupContext ctx, ILogger? log = null, bool recordState = true, bool rotate = true)
    {
        using var gate = InstanceLock.TryAcquireBackup(ctx.DataDir);
        if (gate is null)
            return new BackupResult(false, null, "Бэкап уже выполняется", null);

        var staging = Path.Combine(ctx.DataDir, BackupPaths.StagingDirName);
        BackupResult result;
        try
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            Directory.CreateDirectory(staging);
            Directory.CreateDirectory(ctx.BackupDir);

            var copied = CopyDataTo(ctx, staging, log);
            var manifest = BuildManifest(ctx, staging, copied);

            File.WriteAllText(
                Path.Combine(staging, ManifestEntryName),
                JsonSerializer.Serialize(manifest, ManifestJson));

            var name = $"{ArchivePrefix}{InstanceIdentity.Short(manifest.InstanceId)}-" +
                       $"{manifest.CreatedAt:yyyyMMdd-HHmmss}";
            var finalPath = Path.Combine(ctx.BackupDir, name + ArchiveExtension);
            // Пишем сразу в целевую папку временным именем и переименовываем НА МЕСТЕ:
            // File.Move между томами (облачная папка часто на другом диске) — это
            // copy+delete, и синхронизатор успел бы подхватить недописанный архив
            var partPath = finalPath + ".part";
            if (File.Exists(partPath)) File.Delete(partPath);
            ZipFile.CreateFromDirectory(staging, partPath, CompressionLevel.Optimal, false);
            if (File.Exists(finalPath)) File.Delete(finalPath);
            File.Move(partPath, finalPath);

            File.WriteAllText(
                Path.Combine(ctx.BackupDir, name + ManifestSuffix),
                JsonSerializer.Serialize(manifest, ManifestJson));

            SnapshotSecrets(ctx, manifest.CreatedAt, log);
            if (rotate) Rotate(ctx, log);

            log?.LogInformation("Бэкап снят: {Path} ({Size} КБ)",
                finalPath, new FileInfo(finalPath).Length / 1024);
            result = new BackupResult(true, finalPath, null, manifest);
        }
        catch (Exception ex)
        {
            log?.LogError(ex, "Не удалось снять бэкап");
            result = new BackupResult(false, null, ex.Message, null);
        }
        finally
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); }
            catch { /* мусорный staging не важнее результата */ }
        }

        // Журнал пишем ДО освобождения мьютекса — иначе следующий снимок мог бы
        // перезаписать состояние, пока мы дописываем своё
        if (recordState)
        {
            try { BackupState.Record(ctx.DataDir, result); }
            catch (Exception ex) { log?.LogWarning(ex, "Не удалось обновить журнал бэкапов"); }
        }

        try { gate.ReleaseMutex(); } catch { /* мьютекс мог быть заброшен */ }
        return result;
    }

    // Копирование data в staging. Возвращает относительные пути скопированного.
    private static List<string> CopyDataTo(BackupContext ctx, string staging, ILogger? log)
    {
        var result = new List<string>();
        var backupDirFull = SafeFull(ctx.BackupDir);
        var secretsDirFull = SafeFull(ctx.SecretsDir);

        foreach (var absolute in Directory.EnumerateFiles(ctx.DataDir, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(ctx.DataDir, absolute).Replace('\\', '/');

            if (!BackupPaths.ShouldInclude(relative)) continue;
            // Папки архивов могли быть настроены куда угодно, в том числе внутрь data
            // под нестандартным именем — их ловим по абсолютному пути
            var fullPath = SafeFull(absolute);
            if (IsUnder(fullPath, backupDirFull) || IsUnder(fullPath, secretsDirFull)) continue;
            // Лог событий уносим отдельно, вместе с WAL-хвостом
            if (Path.GetFileName(relative).StartsWith(EventsDbName, StringComparison.OrdinalIgnoreCase))
                continue;

            var target = Path.Combine(staging, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            CopyWithRetry(absolute, target);
            result.Add(relative);
        }

        var eventsDb = Path.Combine(ctx.DataDir, EventsDbName);
        if (File.Exists(eventsDb))
        {
            var target = Path.Combine(staging, EventsDbName);
            if (TryBackupSqlite(eventsDb, target, log)) result.Add(EventsDbName);
        }

        return result;
    }

    // На Windows свежесозданный файл на доли секунды держит антивирус/индексатор, а
    // history.json и заметки пишутся постоянно. Тот же приём, что в JsonFileStore.MoveWithRetry.
    private static void CopyWithRetry(string source, string target)
    {
        const int attempts = 3;
        for (var i = 1; ; i++)
        {
            try
            {
                File.Copy(source, target, overwrite: true);
                return;
            }
            catch (Exception ex) when (i < attempts && ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(100 * i);
            }
        }
    }

    // Копия .db файлом взяла бы страницы без WAL-хвоста — свежие события просто пропали бы,
    // а при неудачном тайминге копия оказалась бы битой. Online-backup API отдаёт
    // согласованный снимок прямо под работающим сервером.
    private static bool TryBackupSqlite(string sourceDb, string targetDb, ILogger? log)
    {
        // ReadOnly сначала (не мешаем работающему серверу), ReadWrite вторым заходом:
        // после нечистого завершения у WAL-базы остаётся хвост, который нужно накатить,
        // и read-only открытие такой БД штатно проваливается. Именно этот случай —
        // бэкап после краха и страховочный снимок перед restore (сервер уже убит).
        var attempts = new[] { SqliteOpenMode.ReadOnly, SqliteOpenMode.ReadWrite };
        var targetConnStr = new SqliteConnectionStringBuilder
        {
            DataSource = targetDb,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString();

        for (var i = 0; i < attempts.Length; i++)
        {
            var sourceConnStr = new SqliteConnectionStringBuilder
            {
                DataSource = sourceDb,
                Mode = attempts[i],
            }.ToString();

            try
            {
                using (var source = new SqliteConnection(sourceConnStr))
                using (var target = new SqliteConnection(targetConnStr))
                {
                    source.Open();
                    target.Open();
                    source.BackupDatabase(target);
                }

                // Пул держит файл открытым и ПОСЛЕ закрытия соединения — упаковка в zip
                // упиралась бы в «файл занят другим процессом». Пулы у этих строк свои
                // (режим и путь отличаются от рабочего соединения лога событий), поэтому
                // чистим точечно, не трогая ClearAllPools у работающего сервера.
                SqliteConnection.ClearPool(new SqliteConnection(targetConnStr));
                SqliteConnection.ClearPool(new SqliteConnection(sourceConnStr));
                return true;
            }
            catch (Exception ex)
            {
                // Недописанный файл убираем сразу: zip собирается из всей папки staging,
                // а манифест — только из списка удачно скопированного, поэтому битый .db
                // уехал бы в архив мимо сверки контрольных сумм при восстановлении
                SqliteConnection.ClearPool(new SqliteConnection(targetConnStr));
                try { if (File.Exists(targetDb)) File.Delete(targetDb); } catch { /* попробуем на следующем круге */ }

                if (i < attempts.Length - 1) continue;

                // Лог событий — не критичный стор: фича деградирует до «нет ленты активности»,
                // ронять из-за него весь бэкап незачем
                log?.LogWarning(ex, "Не удалось снять копию {Db} — архив собран без него", EventsDbName);
                return false;
            }
        }

        return false;
    }

    private static BackupManifest BuildManifest(BackupContext ctx, string staging, List<string> files)
    {
        var entries = new List<BackupFile>(files.Count);
        long total = 0;

        foreach (var relative in files)
        {
            var path = Path.Combine(staging, relative.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path)) continue;
            var info = new FileInfo(path);
            total += info.Length;
            entries.Add(new BackupFile
            {
                Path = relative,
                Size = info.Length,
                Sha256 = HashFile(path),
            });
        }

        return new BackupManifest
        {
            // Снимок — единственное место, где отпечаток инстанса материализуется:
            // архив без него нельзя было бы проверить при восстановлении
            InstanceId = ctx.EnsureInstanceId(),
            SchemaVersion = BackupSchema.Version,
            AppVersion = ctx.AppVersion,
            Environment = ctx.Environment,
            DataPath = ctx.DataDir,
            DifyNamespace = ctx.DifyNamespace,
            CreatedAt = DateTime.Now,
            Owners = BackupSummaryBuilder.ReadOwners(staging),
            Files = entries,
            Summary = BackupSummaryBuilder.Build(staging, total),
        };
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    // --- Секреты (отдельный локальный архив) ---

    private static void SnapshotSecrets(BackupContext ctx, DateTime stamp, ILogger? log)
    {
        try
        {
            Directory.CreateDirectory(ctx.SecretsDir);
            var target = Path.Combine(ctx.SecretsDir, $"ccs-secrets-{stamp:yyyyMMdd-HHmmss}.zip");
            var part = target + ".part";
            if (File.Exists(part)) File.Delete(part);

            using (var zip = ZipFile.Open(part, ZipArchiveMode.Create))
            {
                foreach (var name in BackupPaths.SecretFileNames)
                {
                    var path = Path.Combine(ctx.DataDir, name);
                    if (File.Exists(path)) zip.CreateEntryFromFile(path, "data/" + name);
                }

                foreach (var name in SecretConfigFileNames(ctx))
                {
                    var path = Path.Combine(ctx.BaseDirectory, name);
                    if (File.Exists(path)) zip.CreateEntryFromFile(path, name);
                }
            }

            if (File.Exists(target)) File.Delete(target);
            File.Move(part, target);
            RotateSecrets(ctx.SecretsDir);
        }
        catch (Exception ex)
        {
            log?.LogWarning(ex, "Не удалось сохранить архив секретов");
        }
    }

    private static IEnumerable<string> SecretConfigFileNames(BackupContext ctx)
    {
        yield return "appsettings.Local.json";
        if (!string.IsNullOrWhiteSpace(ctx.Environment))
            yield return $"appsettings.{ctx.Environment}.json";
    }

    private static void RotateSecrets(string dir)
    {
        var files = Directory.GetFiles(dir, "ccs-secrets-*.zip")
            .OrderByDescending(f => f)
            .Skip(3);
        foreach (var file in files)
        {
            try { File.Delete(file); } catch { /* не критично */ }
        }
    }

    // --- Ротация основных архивов ---

    private static void Rotate(BackupContext ctx, ILogger? log)
    {
        try
        {
            var candidates = new List<BackupRotation.Candidate>();

            foreach (var archive in Directory.GetFiles(ctx.BackupDir, ArchivePrefix + "*" + ArchiveExtension))
            {
                var manifest = TryReadSidecar(archive);
                // Нечитаемый или отсутствующий sidecar = архив не наш (в общей облачной
                // папке лежат чужие инстансы, а OneDrive держит файлы плейсхолдерами).
                // Трогаем только то, чьё авторство подтверждено.
                if (manifest is null) continue;
                if (!string.Equals(manifest.InstanceId, ctx.InstanceId, StringComparison.OrdinalIgnoreCase))
                    continue;

                candidates.Add(new BackupRotation.Candidate(
                    Path.GetFileName(archive), manifest.CreatedAt));
            }

            foreach (var name in BackupRotation.SelectForDeletion(candidates))
            {
                var archive = Path.Combine(ctx.BackupDir, name);
                var sidecar = SidecarPathFor(archive);
                try
                {
                    File.Delete(archive);
                    if (File.Exists(sidecar)) File.Delete(sidecar);
                }
                catch (Exception ex) { log?.LogDebug(ex, "Не удалось удалить старый архив {Name}", name); }
            }
        }
        catch (Exception ex)
        {
            log?.LogWarning(ex, "Ротация архивов не выполнена");
        }
    }

    public static string SidecarPathFor(string archivePath) =>
        Path.Combine(
            Path.GetDirectoryName(archivePath) ?? "",
            Path.GetFileNameWithoutExtension(archivePath) + ManifestSuffix);

    public static BackupManifest? TryReadSidecar(string archivePath)
    {
        try
        {
            var sidecar = SidecarPathFor(archivePath);
            if (!File.Exists(sidecar)) return null;
            return JsonSerializer.Deserialize<BackupManifest>(File.ReadAllText(sidecar));
        }
        catch { return null; }
    }

    public static BackupManifest? TryReadManifestFromArchive(string archivePath)
    {
        try
        {
            using var zip = ZipFile.OpenRead(archivePath);
            var entry = zip.GetEntry(ManifestEntryName);
            if (entry is null) return null;
            using var stream = entry.Open();
            return JsonSerializer.Deserialize<BackupManifest>(stream);
        }
        catch { return null; }
    }

    private static string SafeFull(string path)
    {
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); }
        catch { return path; }
    }

    private static bool IsUnder(string path, string parent) =>
        path.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
        || string.Equals(path, parent, StringComparison.OrdinalIgnoreCase);
}
