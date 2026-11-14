using System.Text.Json;
using System.Text.RegularExpressions;

namespace ClaudeHomeServer.Services.Backup;

// Инкрементальные снимки перед выкаткой (режим CLI --snapshot): каталог
// data/backups/snapshots/{stamp}/ с файлами данных и manifest.json, где неизменившиеся
// с прошлого снимка файлы — жёсткие ссылки, а не копии. Плановый бэкап (zip для облака)
// этим не заменяется: снимок живёт на том же диске, что и data, и от его потери не спасёт.
//
// ИНВАРИАНТ ЖЁСТКИХ ССЫЛОК: файлы внутри снимков никогда не меняются на месте и не
// переносятся обратно ссылкой. Один inode разделён между снимками, поэтому правка файла
// в одном снимке испортила бы все. Восстановление только КОПИРУЕТ наружу
// (<see cref="CopySnapshotTo"/>), удаление снимка — Directory.Delete его каталога.
public static partial class BackupCore
{
    /// <summary>Сколько завершённых снимков держим (старшие удаляются каталогом).</summary>
    public const int SnapshotKeepLast = 3;

    private const string PartSuffix = ".part";

    // Имя каталога завершённого снимка: метка времени, при коллизии в одну секунду — «-N»
    private static readonly Regex SnapshotNamePattern =
        new(@"^\d{8}-\d{6}(-\d+)?$", RegexOptions.Compiled);

    // Завершённый снимок, взятый источником ссылок
    private sealed record PreviousSnapshot(string Dir, Dictionary<string, BackupFile> Files);

    /// <summary>
    /// Снять инкрементальный снимок. Пишем в {stamp}.part, в конце — атомарный rename;
    /// незавершённые .part прошлых прогонов убираются на старте, а источником ссылок
    /// берётся последний ЗАВЕРШЁННЫЙ снимок.
    /// </summary>
    public static BackupResult SnapshotIncremental(BackupContext ctx, ILogger? log = null)
    {
        using var gate = InstanceLock.TryAcquireBackup(ctx.DataDir);
        if (gate is null)
            return new BackupResult(false, null, "Бэкап уже выполняется", null);

        var metrics = new BackupMetrics { Mode = "snapshot" };
        string? partDir = null;
        BackupResult result;
        try
        {
            Directory.CreateDirectory(ctx.SnapshotsDir);
            DeleteOrphanSnapshotParts(ctx.SnapshotsDir, log);
            DeleteOrphanParts(ctx.SecretsDir);

            var previous = FindLastCompletedSnapshot(ctx.SnapshotsDir);
            log?.LogInformation("Снимок: источник ссылок — {Previous}",
                previous is null ? "нет (первый снимок, полная копия)" : previous.Dir);

            var createdAt = DateTime.Now;
            var name = UniqueSnapshotName(ctx.SnapshotsDir, createdAt);
            var finalDir = Path.Combine(ctx.SnapshotsDir, name);
            partDir = finalDir + PartSuffix;
            Directory.CreateDirectory(partDir);

            var files = CopyDataToSnapshot(ctx, partDir, previous, log, metrics);

            BackupManifest manifest = null!;
            metrics.Measure("манифест", () =>
            {
                manifest = BuildManifest(ctx, partDir, files);
                // Время манифеста — момент начала снимка: по нему имя каталога и CreatedAt согласованы
                manifest.CreatedAt = createdAt;
                File.WriteAllText(
                    Path.Combine(partDir, ManifestEntryName),
                    JsonSerializer.Serialize(manifest, ManifestJson));
            });

            metrics.Measure("финал", () =>
            {
                Directory.Move(partDir, finalDir);
                partDir = null;
                SnapshotSecrets(ctx, createdAt, log);
                RotateSnapshots(ctx.SnapshotsDir, name, log);
            });

            log?.LogInformation("Снимок снят: {Path}\n{Metrics}", finalDir, metrics.Format());
            result = new BackupResult(true, finalDir, null, manifest, metrics);
        }
        catch (Exception ex)
        {
            log?.LogError(ex, "Не удалось снять снимок");
            result = new BackupResult(false, null, ex.Message, null, metrics);
            // Недописанный .part убираем сразу; убитый по таймауту процесс уберёт следующий прогон
            if (partDir is not null)
            {
                try { DeleteSnapshotDir(partDir); }
                catch { /* остаток уберёт следующий прогон */ }
            }
        }

        try { gate.ReleaseMutex(); } catch { /* мьютекс мог быть заброшен */ }
        return result;
    }

    // Обход data: свежая копия + хеш конвейером либо жёсткая ссылка на файл прошлого снимка
    private static List<BackupFile> CopyDataToSnapshot(
        BackupContext ctx, string target, PreviousSnapshot? previous, ILogger? log, BackupMetrics metrics)
    {
        var order = new List<string>();
        var linked = new Dictionary<string, BackupFile>();
        using var hashes = new HashPipeline();
        string? lastDir = null;
        var linkFailureLogged = false;

        try
        {
            foreach (var (absolute, relative) in EnumerateDataFiles(ctx, metrics))
            {
                var destination = Path.Combine(target, relative.Replace('/', Path.DirectorySeparatorChar));
                var destinationDir = Path.GetDirectoryName(destination)!;
                if (destinationDir != lastDir)
                {
                    Directory.CreateDirectory(destinationDir);
                    lastDir = destinationDir;
                }

                // Размер и mtime — ДО копирования: если файл меняется под нами, копия получит
                // старый mtime при новых байтах, и следующий снимок перекопирует её, а не
                // примет за актуальную
                var info = new FileInfo(absolute);
                var size = info.Length;
                var modified = info.LastWriteTimeUtc;

                var start = BackupMetrics.Now();
                var reusable = previous is null ? null : FindReusable(previous, relative, size, modified);
                metrics.AddSince("сверка", start);

                if (reusable is not null)
                {
                    start = BackupMetrics.Now();
                    var ok = BackupHardLink.TryCreate(reusable.Value.PreviousPath, destination);
                    metrics.AddSince("ссылки", start);
                    if (ok)
                    {
                        metrics.Linked++;
                        metrics.LinkedBytes += size;
                        linked[relative] = new BackupFile
                        {
                            Path = relative, Size = reusable.Value.Entry.Size, Sha256 = reusable.Value.Entry.Sha256,
                        };
                        order.Add(relative);
                        continue;
                    }

                    // Другая ФС, EXDEV, нет прав: честная копия, а не провал снимка
                    metrics.LinkFailed++;
                    if (!linkFailureLogged)
                    {
                        linkFailureLogged = true;
                        log?.LogWarning("Жёсткая ссылка не создалась ({File}) — такие файлы копируются", relative);
                    }
                }

                start = BackupMetrics.Now();
                CopyWithRetry(absolute, destination);
                // File.Copy не обязан переносить mtime — ставим явно: по нему следующий снимок
                // решает, ссылаться на этот файл или перекопировать
                File.SetLastWriteTimeUtc(destination, modified);
                metrics.AddSince("копирование", start);
                metrics.Copied++;
                metrics.CopiedBytes += size;
                order.Add(relative);
                hashes.Add(destination, relative);
            }

            // SQLite — всегда свежая копия через online-backup API, никогда не ссылка:
            // файл базы живой, а прошлая копия не содержит свежего WAL-хвоста
            var eventsDb = Path.Combine(ctx.DataDir, EventsDbName);
            if (File.Exists(eventsDb))
            {
                var start = BackupMetrics.Now();
                var destination = Path.Combine(target, EventsDbName);
                if (TryBackupSqlite(eventsDb, destination, log))
                {
                    order.Add(EventsDbName);
                    hashes.Add(destination, EventsDbName);
                    metrics.Copied++;
                    metrics.CopiedBytes += new FileInfo(destination).Length;
                }
                metrics.AddSince("sqlite", start);
            }
        }
        finally
        {
            var start = BackupMetrics.Now();
            hashes.Drain();
            metrics.AddSince("хеши (ожидание)", start);
        }

        hashes.ThrowIfFailed();

        var result = new List<BackupFile>(order.Count);
        foreach (var relative in order)
            result.Add(linked.TryGetValue(relative, out var entry) ? entry : hashes.Entries[relative]);
        return result;
    }

    // Файл прошлого снимка годится под ссылку, если путь, размер и mtime совпадают с источником,
    // а в манифесте есть хеш. Размер и mtime сверяются по самому файлу снимка (копия получила
    // mtime источника явно, ссылка делит inode), а не по манифесту: в манифесте mtime нет.
    private static (string PreviousPath, BackupFile Entry)? FindReusable(
        PreviousSnapshot previous, string relative, long size, DateTime modified)
    {
        if (!previous.Files.TryGetValue(relative, out var entry)) return null;
        if (entry.Size != size || string.IsNullOrEmpty(entry.Sha256)) return null;

        var path = Path.Combine(previous.Dir, relative.Replace('/', Path.DirectorySeparatorChar));
        var info = new FileInfo(path);
        if (!info.Exists || info.Length != size || info.LastWriteTimeUtc != modified) return null;
        return (path, entry);
    }

    // --- Каталог снимков ---

    private static string UniqueSnapshotName(string snapshotsDir, DateTime createdAt)
    {
        var baseName = createdAt.ToString("yyyyMMdd-HHmmss");
        var name = baseName;
        for (var i = 2; Directory.Exists(Path.Combine(snapshotsDir, name))
                        || Directory.Exists(Path.Combine(snapshotsDir, name + PartSuffix)); i++)
            name = $"{baseName}-{i}";
        return name;
    }

    // Имена завершённых снимков (есть manifest.json), свежие первыми. Порядок по имени:
    // метка времени сортируется как строка, а «-N» при коллизии идёт после неё.
    private static List<string> ListCompletedSnapshots(string snapshotsDir)
    {
        if (!Directory.Exists(snapshotsDir)) return [];
        return Directory.GetDirectories(snapshotsDir)
            .Select(d => Path.GetFileName(d))
            .Where(n => SnapshotNamePattern.IsMatch(n))
            .Where(n => File.Exists(Path.Combine(snapshotsDir, n, ManifestEntryName)))
            .OrderByDescending(n => n, StringComparer.Ordinal)
            .ToList();
    }

    private static PreviousSnapshot? FindLastCompletedSnapshot(string snapshotsDir)
    {
        foreach (var name in ListCompletedSnapshots(snapshotsDir))
        {
            var dir = Path.Combine(snapshotsDir, name);
            var manifest = TryReadSnapshotManifest(dir);
            if (manifest is null) continue;
            // Дубли путей в манифесте не ждём, но последнее значение побеждает, а не падение
            var files = new Dictionary<string, BackupFile>(StringComparer.Ordinal);
            foreach (var file in manifest.Files) files[file.Path] = file;
            return new PreviousSnapshot(dir, files);
        }
        return null;
    }

    // Недописанные {stamp}.part от прерванных прогонов (убитых по таймауту выкатки)
    private static void DeleteOrphanSnapshotParts(string snapshotsDir, ILogger? log)
    {
        try
        {
            foreach (var part in Directory.GetDirectories(snapshotsDir, "*" + PartSuffix))
            {
                try
                {
                    DeleteSnapshotDir(part);
                    log?.LogInformation("Убран недописанный снимок {Part}", part);
                }
                catch (Exception ex) { log?.LogWarning(ex, "Не удалось убрать {Part}", part); }
            }
        }
        catch { /* каталог не читается — пропускаем чистку */ }
    }

    // Остаются текущий снимок и (SnapshotKeepLast - 1) самых свежих из прочих: текущий
    // не должен вылететь из окна, даже если часы ушли назад
    private static void RotateSnapshots(string snapshotsDir, string currentName, ILogger? log)
    {
        try
        {
            var keep = ListCompletedSnapshots(snapshotsDir)
                .Where(n => n != currentName)
                .Take(SnapshotKeepLast - 1)
                .Append(currentName)
                .ToHashSet(StringComparer.Ordinal);

            foreach (var name in ListCompletedSnapshots(snapshotsDir).Where(n => !keep.Contains(n)))
            {
                try { DeleteSnapshotDir(Path.Combine(snapshotsDir, name)); }
                catch (Exception ex) { log?.LogWarning(ex, "Не удалось удалить старый снимок {Name}", name); }
            }
        }
        catch (Exception ex)
        {
            log?.LogWarning(ex, "Ротация снимков не выполнена");
        }
    }

    // Удаляются только имена этого каталога: данные живут, пока на них ссылаются другие
    // снимки. Принудительное снятие read-only — запасной путь для Windows: там read-only
    // файл (git-объект) не удалить, а атрибут у жёстких ссылок общий — это единственное
    // изменение «на месте», и оно касается атрибута, а не содержимого.
    private static void DeleteSnapshotDir(string dir)
    {
        try { Directory.Delete(dir, recursive: true); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            DeleteDirectoryForce(dir);
        }
    }

    // --- Чтение и восстановление ---

    public static BackupManifest? TryReadSnapshotManifest(string snapshotDir)
    {
        try
        {
            var path = Path.Combine(snapshotDir, ManifestEntryName);
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<BackupManifest>(File.ReadAllText(path));
        }
        catch { return null; }
    }

    /// <summary>Манифест источника восстановления: каталог снимка или zip-архив.</summary>
    public static BackupManifest? TryReadManifest(string source) =>
        Directory.Exists(source) ? TryReadSnapshotManifest(source) : TryReadManifestFromArchive(source);

    /// <summary>
    /// Скопировать файлы снимка (по списку манифеста) в <paramref name="targetDir"/>.
    /// Только File.Copy — снимок остаётся нетронутым и не делит inode с результатом.
    /// Отсутствующий в снимке файл не скрывается: его поймает сверка контрольных сумм.
    /// </summary>
    public static void CopySnapshotTo(string snapshotDir, BackupManifest manifest, string targetDir)
    {
        Directory.CreateDirectory(targetDir);
        foreach (var file in manifest.Files)
        {
            // Манифест лежит в самом снимке и считается недоверенным: путь наружу — отказ
            var source = SafePath.Join(snapshotDir, file.Path);
            var destination = SafePath.Join(targetDir, file.Path);
            if (!File.Exists(source)) continue;

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination, overwrite: true);
            // Копия наследует read-only (git-объекты) — снимаем, чтобы подмена каталога и
            // зачистка распаковки не упирались в «Access denied». Это атрибут копии, а не снимка
            var attrs = File.GetAttributes(destination);
            if ((attrs & FileAttributes.ReadOnly) != 0)
                File.SetAttributes(destination, attrs & ~FileAttributes.ReadOnly);
        }
    }
}
