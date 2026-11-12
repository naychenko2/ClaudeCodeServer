using System.Collections.Concurrent;
using System.IO.Enumeration;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace ClaudeHomeServer.Services.Backup;

public record BackupResult(
    bool Ok, string? ArchivePath, string? Error, BackupManifest? Manifest, BackupMetrics? Metrics = null);
public record RestoreResult(bool Ok, string? Error, string? MovedDataTo, BackupManifest? Manifest);

// Снятие и восстановление снапшота каталога data.
public static partial class BackupCore
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
        var metrics = new BackupMetrics { Mode = "zip" };
        BackupResult result;
        try
        {
            DeleteDirectoryForce(staging);
            Directory.CreateDirectory(staging);
            Directory.CreateDirectory(ctx.BackupDir);
            // Осиротевшие .part от прерванных снимков: свой part снапшот убирает сам,
            // но файл после краха между CreateFromDirectory и Move лежал вечно (прод:
            // четыре шт. от 26.07 до 11.08). Мьютекс уже наш — параллельного снапшота нет.
            DeleteOrphanParts(ctx.BackupDir);
            DeleteOrphanParts(ctx.SecretsDir);

            var copied = CopyDataTo(ctx, staging, log, metrics);
            metrics.Copied = copied.Count;
            metrics.CopiedBytes = copied.Sum(f => f.Size);

            BackupManifest manifest = null!;
            metrics.Measure("манифест", () =>
            {
                manifest = BuildManifest(ctx, staging, copied);
                File.WriteAllText(
                    Path.Combine(staging, ManifestEntryName),
                    JsonSerializer.Serialize(manifest, ManifestJson));
            });

            var name = $"{ArchivePrefix}{InstanceIdentity.Short(manifest.InstanceId)}-" +
                       $"{manifest.CreatedAt:yyyyMMdd-HHmmss}";
            var finalPath = Path.Combine(ctx.BackupDir, name + ArchiveExtension);
            // Пишем сразу в целевую папку временным именем и переименовываем НА МЕСТЕ:
            // File.Move между томами (облачная папка часто на другом диске) — это
            // copy+delete, и синхронизатор успел бы подхватить недописанный архив
            var partPath = finalPath + ".part";
            if (File.Exists(partPath)) File.Delete(partPath);
            // Fastest вместо Optimal по замеру на прод-корпусе (4.7 ГБ, ~26 тыс. файлов):
            // 61 с против 237 с (в 3.9 раза быстрее, zip — 61% времени всего снапшота)
            // ценой +37% размера (1789 → 2453 МБ). Время критичнее: таймаут агента
            // выкатки на съёмке бэкапа уже поднимали до 2400 с, снапшот рос с транскриптами
            metrics.Measure("zip", () =>
                ZipFile.CreateFromDirectory(staging, partPath, CompressionLevel.Fastest, false));
            metrics.Measure("финал", () =>
            {
                if (File.Exists(finalPath)) File.Delete(finalPath);
                File.Move(partPath, finalPath);

                File.WriteAllText(
                    Path.Combine(ctx.BackupDir, name + ManifestSuffix),
                    JsonSerializer.Serialize(manifest, ManifestJson));

                SnapshotSecrets(ctx, manifest.CreatedAt, log);
                if (rotate) Rotate(ctx, log);
            });

            metrics.ArchiveBytes = new FileInfo(finalPath).Length;
            log?.LogInformation("Бэкап снят: {Path} ({Size} КБ)\n{Metrics}",
                finalPath, metrics.ArchiveBytes / 1024, metrics.Format());
            result = new BackupResult(true, finalPath, null, manifest, metrics);
        }
        catch (Exception ex)
        {
            log?.LogError(ex, "Не удалось снять бэкап");
            result = new BackupResult(false, null, ex.Message, null, metrics);
        }
        finally
        {
            try { DeleteDirectoryForce(staging); }
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

    // Удаление осиротевших временных файлов архива (*.zip.part). Неудача удаления
    // одного файла не должна ронять снимок.
    private static void DeleteOrphanParts(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return;
            foreach (var part in Directory.GetFiles(dir, "*" + ArchiveExtension + ".part"))
            {
                try { File.Delete(part); }
                catch { /* занят или нет прав — оставим, не повод ронять снимок */ }
            }
        }
        catch { /* каталог не читается — пропускаем чистку */ }
    }

    // Копирование data в staging; SHA-256 считается конвейером параллельно копированию
    // (хеш-воркеры читают только что записанные копии из кэша записи), а не отдельным
    // линейным проходом по staging после — на проде тот проход стоил ещё ~4 ГБ чтения
    // поверх копирования. Само копирование оставлено на File.Copy: kernel-путь
    // (CopyFileW) стабильно быстр, user-mode пострельное копирование на загруженной
    // машине деградирует в разы (замер: 21 с против 233 с на одном корпусе).
    private static List<BackupFile> CopyDataTo(
        BackupContext ctx, string staging, ILogger? log, BackupMetrics metrics)
    {
        var copied = new List<string>();
        using var hashes = new HashPipeline();

        try
        {
            foreach (var (absolute, relative) in EnumerateDataFiles(ctx, metrics))
            {
                var target = Path.Combine(staging, relative.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                var start = BackupMetrics.Now();
                CopyWithRetry(absolute, target);
                metrics.AddSince("копирование", start);
                copied.Add(relative);
                hashes.Add(target, relative);
            }

            var eventsDb = Path.Combine(ctx.DataDir, EventsDbName);
            if (File.Exists(eventsDb))
            {
                var target = Path.Combine(staging, EventsDbName);
                var start = BackupMetrics.Now();
                if (TryBackupSqlite(eventsDb, target, log)) copied.Add(EventsDbName);
                metrics.AddSince("sqlite", start);
            }
        }
        finally
        {
            // Drain обязан быть рядом с исключением в цикле: при падении Snapshot из своего
            // finally начнёт DeleteDirectoryForce(staging), и без дожидания воркеров удаление
            // ловило бы sharing violation на читаемых файлах
            var start = BackupMetrics.Now();
            hashes.Drain();
            metrics.AddSince("хеши (ожидание)", start);
        }

        hashes.ThrowIfFailed();

        // Порядок записей — порядок обхода (как в манифесте до конвейера); events db,
        // скопированный последним, хешируем тут же: это один файл, а не проход по staging
        var result = new List<BackupFile>(copied.Count);
        foreach (var relative in copied)
        {
            if (relative == EventsDbName)
            {
                var target = Path.Combine(staging, EventsDbName);
                result.Add(new BackupFile
                {
                    Path = EventsDbName,
                    Size = new FileInfo(target).Length,
                    Sha256 = HashFile(target),
                });
            }
            else
            {
                result.Add(hashes.Entries[relative]);
            }
        }

        return result;
    }

    /// <summary>
    /// Файлы data, которые идут в бэкап (общий фильтр состава для zip и снимка): путь
    /// через <see cref="BackupPaths.ShouldInclude"/>, папки архивов/секретов/снимков
    /// отсекаются ЦЕЛИКОМ при обходе. Прунинг важен: каталог снимков лежит внутри data
    /// и держит сотни тысяч файлов — обход по ним впустую стоил бы секунд на каждом бэкапе.
    /// Время самого обхода уходит в фазу «обход».
    /// </summary>
    internal static IEnumerable<(string Absolute, string Relative)> EnumerateDataFiles(
        BackupContext ctx, BackupMetrics metrics)
    {
        // Папки архивов могли быть настроены куда угодно, в том числе внутрь data
        // под нестандартным именем — их ловим по абсолютному пути
        var excluded = new[]
        {
            SafeFull(ctx.BackupDir), SafeFull(ctx.SecretsDir), SafeFull(ctx.SnapshotsDir),
        };

        // Те же умолчания, что у Directory.EnumerateFiles(..., AllDirectories); добавлен
        // только отказ заходить в исключённые папки
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = 0,
            IgnoreInaccessible = false,
        };
        var enumerable = new FileSystemEnumerable<string>(
            ctx.DataDir, static (ref FileSystemEntry e) => e.ToFullPath(), options)
        {
            ShouldIncludePredicate = static (ref FileSystemEntry e) => !e.IsDirectory,
            ShouldRecursePredicate = (ref FileSystemEntry e) =>
            {
                var full = e.ToFullPath();
                foreach (var dir in excluded)
                    if (IsUnder(full, dir)) return false;
                return true;
            },
        };

        using var walker = enumerable.GetEnumerator();
        while (true)
        {
            var start = BackupMetrics.Now();
            var has = walker.MoveNext();
            metrics.AddSince("обход", start);
            if (!has) yield break;

            var absolute = walker.Current;
            var relative = Path.GetRelativePath(ctx.DataDir, absolute).Replace('\\', '/');

            if (!BackupPaths.ShouldInclude(relative)) continue;
            // Лог событий уносим отдельно, вместе с WAL-хвостом
            if (Path.GetFileName(relative).StartsWith(EventsDbName, StringComparison.OrdinalIgnoreCase))
                continue;

            yield return (absolute, relative);
        }
    }

    // Конвейер хеширования: воркеры читают только что записанные копии параллельно с
    // продолжающимся копированием. Хеш обязан быть у каждой записи манифеста — иначе
    // сверка контрольных сумм при восстановлении слабеет, поэтому ошибки копятся и
    // роняют снимок после Drain, а не теряются.
    private sealed class HashPipeline : IDisposable
    {
        private readonly BlockingCollection<(string Target, string Rel)> _queue = new();
        private readonly ConcurrentQueue<Exception> _failures = new();
        private readonly Thread[] _workers = new Thread[Math.Clamp(Environment.ProcessorCount - 1, 1, 4)];

        public ConcurrentDictionary<string, BackupFile> Entries { get; } = new();

        public HashPipeline()
        {
            for (var i = 0; i < _workers.Length; i++)
            {
                _workers[i] = new Thread(Work);
                _workers[i].Start();
            }
        }

        public void Add(string target, string rel) => _queue.Add((target, rel));

        // Идемпотентен: зовётся из finally, исключение цикла не должно оставить воркеров висеть
        public void Drain()
        {
            _queue.CompleteAdding();
            foreach (var worker in _workers) worker.Join();
        }

        public void ThrowIfFailed()
        {
            if (!_failures.IsEmpty) throw _failures.First();
        }

        private void Work()
        {
            foreach (var (target, rel) in _queue.GetConsumingEnumerable())
            {
                try
                {
                    Entries[rel] = new BackupFile
                    {
                        Path = rel,
                        Size = new FileInfo(target).Length,
                        Sha256 = HashFile(target),
                    };
                }
                catch (Exception ex)
                {
                    _failures.Enqueue(ex);
                }
            }
        }

        public void Dispose() => _queue.Dispose();
    }

    // Рекурсивное удаление со сбросом read-only. Git-объекты Forgejo помечены read-only,
    // File.Copy переносит атрибут в staging — и обычный Directory.Delete падает на них
    // «Access denied» (прод 25.07: зачистка молча не удалась, следующий бэкап валился
    // на своём же мусоре и стопорил деплой).
    internal static void DeleteDirectoryForce(string dir)
    {
        if (!Directory.Exists(dir)) return;
        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            var attrs = File.GetAttributes(file);
            if ((attrs & FileAttributes.ReadOnly) != 0)
                File.SetAttributes(file, attrs & ~FileAttributes.ReadOnly);
        }
        Directory.Delete(dir, recursive: true);
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
                // Копия наследует read-only источника (git-объекты) — снимаем сразу,
                // чтобы staging всегда удалялся и перезаписывался без плясок с атрибутами
                var attrs = File.GetAttributes(target);
                if ((attrs & FileAttributes.ReadOnly) != 0)
                    File.SetAttributes(target, attrs & ~FileAttributes.ReadOnly);
                return;
            }
            catch (Exception ex) when (i < attempts && ex is IOException or UnauthorizedAccessException)
            {
                // Перезапись поверх read-only цели — тоже «Access denied»: снять атрибут и повторить
                try
                {
                    if (File.Exists(target))
                        File.SetAttributes(target, File.GetAttributes(target) & ~FileAttributes.ReadOnly);
                }
                catch { /* не мешаем основному ретраю */ }
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

    // Записи манифеста приходят из CopyDataTo — они появляются строго после успешного
    // копирования, поэтому «манифест только из реально скопированного» держится по построению
    private static BackupManifest BuildManifest(BackupContext ctx, string staging, List<BackupFile> files) =>
        new()
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
            Files = files,
            Summary = BackupSummaryBuilder.Build(staging, files.Sum(f => f.Size)),
        };

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
