using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace ClaudeHomeServer.Services.Backup;

// Восстановление из архива. Порядок гейтов важен: всё, что может отказать, отказывает
// ДО того, как каталог data сдвинут с места.
public static class BackupRestore
{
    public static RestoreResult Restore(
        BackupContext ctx, string archivePath, string? secretsArchive, bool force,
        Action<string>? report = null, ILogger? log = null)
    {
        void Say(string message) { report?.Invoke(message); log?.LogInformation("{Message}", message); }

        if (!File.Exists(archivePath))
            return new RestoreResult(false, $"Архив не найден: {archivePath}", null, null);

        // Гейт 0. Живой сервер продолжит писать в перемещённый каталог и пересоздаст data
        // под собой — восстановление получится наполовину.
        if (InstanceLock.IsServerRunning(ctx.DataDir))
            return new RestoreResult(false,
                "Сервер запущен на этом каталоге data. Останови его и повтори", null, null);

        var manifest = BackupCore.TryReadManifestFromArchive(archivePath);
        if (manifest is null)
            return new RestoreResult(false, "В архиве нет manifest.json — это не бэкап CCS", null, null);

        // Гейт 1. Три случая, и «файла нет» — не то же самое, что «не совпал».
        // Берём id из контекста: тот его только читает, но НЕ создаёт — иначе к этому
        // моменту на чистой машине уже лежал бы свежесгенерированный чужой отпечаток
        var localId = ctx.InstanceId;
        if (localId is null)
        {
            Say($"Инстанс без отпечатка — принимаем id из архива ({InstanceIdentity.Short(manifest.InstanceId)})");
        }
        else if (!string.Equals(localId, manifest.InstanceId, StringComparison.OrdinalIgnoreCase))
        {
            if (!force)
            {
                var owners = string.Join(", ", manifest.Owners.Select(o => o.Username));
                return new RestoreResult(false,
                    $"Архив снят другим инстансом (окружение «{manifest.Environment}», владельцы: {owners}). " +
                    "Восстановление затрёт стейт этого инстанса. Нужен --force, если это осознанно",
                    null, manifest);
            }
            Say("ВНИМАНИЕ: архив чужого инстанса, продолжаем по --force");
        }

        // Гейт 2. Архив новее кода: незнакомые поля/значения enum уронят десериализацию,
        // а JsonFileStore на этом молча отдаст пустые сторы
        if (manifest.SchemaVersion > BackupSchema.Version)
            return new RestoreResult(false,
                $"Архив сделан более новой версией приложения (формат {manifest.SchemaVersion}, " +
                $"поддерживается {BackupSchema.Version}). Обнови приложение и повтори",
                null, manifest);

        var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(ctx.DataDir))
                     ?? ctx.DataDir;
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var unpacked = Path.Combine(parent, $".restore-{stamp}");
        var movedTo = Path.Combine(parent, $"data.old-{stamp}");

        try
        {
            // Гейт 3. Распаковываем рядом и проверяем ДО подмены: сюда же попадает
            // сверка контрольных сумм и строгое чтение сторов
            Say("Распаковка и проверка архива…");
            BackupCore.DeleteDirectoryForce(unpacked);
            ZipFile.ExtractToDirectory(archivePath, unpacked);

            var checksumProblem = VerifyChecksums(unpacked, manifest);
            if (checksumProblem is not null)
                return Fail($"Архив повреждён: {checksumProblem}");

            var problems = BackupValidation.Validate(unpacked);
            if (problems.Count > 0)
                return Fail("Данные в архиве не проходят проверку:\n  - " + string.Join("\n  - ", problems));

            // graph.json — regenerable (перестраивается из кода проекта), поэтому его порча
            // не блокирует восстановление: сообщаем предупреждением, граф пересоберётся при
            // первом обращении. Не должно попадать в fatal-гейт Validate.
            var graphWarnings = BackupValidation.ValidateGraphWarnings(unpacked);
            if (graphWarnings.Count > 0)
                Say("ВНИМАНИЕ: graph.json требует пересборки (не блокирует восстановление):\n  - "
                    + string.Join("\n  - ", graphWarnings));

            if (!HasFreeSpace(ctx.DataDir, out var needed))
                Say($"ВНИМАНИЕ: мало места на диске (нужно ~{needed / 1024 / 1024} МБ)");

            // Песочница монтирует data/sandbox-* внутрь контейнера и держит каталог —
            // без остановки переименование не пройдёт
            StopSandboxContainer(ctx, Say);

            Say("Снимаю страховочный бэкап текущего состояния…");
            // recordState: false — снимок делается по случаю восстановления и не должен
            // выдавать себя в сводке за очередной плановый бэкап.
            // rotate: false — иначе пересчёт корзин мог бы удалить тот самый архив,
            // который мы сейчас восстанавливаем.
            var pre = BackupCore.Snapshot(ctx, log, recordState: false, rotate: false);
            if (pre.Ok) Say($"  сохранён: {pre.ArchivePath}");
            else Say($"  не удалось ({pre.Error}) — продолжаем");

            // Пул Microsoft.Data.Sqlite держит файл БД открытым и после закрытия соединений;
            // без этого каталог не переименовать (см. комментарий в ProjectEventLogService)
            SqliteConnection.ClearAllPools();

            Say("Подменяю каталог данных…");
            Directory.Move(ctx.DataDir, movedTo);
            try
            {
                Directory.Move(unpacked, ctx.DataDir);
            }
            catch (Exception ex)
            {
                // Откат: без него инстанс остался бы вообще без data
                TryRollback(movedTo, ctx.DataDir);
                return new RestoreResult(false,
                    $"Не удалось поставить восстановленные данные ({ex.Message}). Прежние данные возвращены на место",
                    null, manifest);
            }

            // Служебный файл манифеста внутри data не нужен
            TryDelete(Path.Combine(ctx.DataDir, "manifest.json"));

            // Секретов в архиве нет (он уезжает в облако), а каталог мы заменили целиком —
            // значит текущие ключи остались в data.old. Переносим их обратно, иначе
            // обычный откат втихую разлогинивал бы всех (новый jwt-secret) и убивал
            // push-подписки (новые VAPID). Явный --secrets ниже перезапишет их архивными.
            CarryOverSecrets(movedTo, ctx.DataDir, Say);
            CarryOverBackups(ctx, movedTo, Say);

            if (localId is null || force)
                InstanceIdentity.Adopt(ctx.DataDir, manifest.InstanceId);

            // Метка для стартового хука: сбросить карты знаний, чтобы Dify-слой
            // пересобрался с натуры
            File.WriteAllText(Path.Combine(ctx.DataDir, BackupPaths.PostRestoreMarker),
                DateTime.UtcNow.ToString("O"));

            if (!string.IsNullOrWhiteSpace(secretsArchive))
                RestoreSecrets(ctx, secretsArchive!, Say);

            Say($"Готово. Прежние данные: {movedTo}");
            return new RestoreResult(true, null, movedTo, manifest);
        }
        catch (Exception ex)
        {
            log?.LogError(ex, "Восстановление не удалось");
            return Fail(ex.Message);
        }
        finally
        {
            try { BackupCore.DeleteDirectoryForce(unpacked); }
            catch { /* остатки распаковки не важнее результата */ }
        }

        RestoreResult Fail(string error) => new(false, error, null, manifest);
    }

    private static string? VerifyChecksums(string dir, BackupManifest manifest)
    {
        foreach (var file in manifest.Files)
        {
            var path = Path.Combine(dir, file.Path.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path)) return $"нет файла {file.Path}";
            if (string.IsNullOrEmpty(file.Sha256)) continue;

            using var stream = File.OpenRead(path);
            var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            if (!hash.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                return $"контрольная сумма не сошлась у {file.Path}";
        }
        return null;
    }

    private static bool HasFreeSpace(string dataDir, out long needed)
    {
        needed = 0;
        try
        {
            var size = Directory.EnumerateFiles(dataDir, "*", SearchOption.AllDirectories)
                .Sum(f => { try { return new FileInfo(f).Length; } catch { return 0L; } });
            needed = size * 2;
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(dataDir))!);
            return drive.AvailableFreeSpace > needed;
        }
        catch { return true; }
    }

    private static void StopSandboxContainer(BackupContext ctx, Action<string> say)
    {
        if (string.IsNullOrWhiteSpace(ctx.SandboxContainerName)) return;
        try
        {
            var psi = new ProcessStartInfo("docker", $"rm -f {ctx.SandboxContainerName}")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var process = Process.Start(psi);
            if (process is null) return;
            process.WaitForExit(15_000);
            if (process.ExitCode == 0)
                say($"Контейнер песочницы {ctx.SandboxContainerName} остановлен (поднимется сам при первом ходе)");
        }
        catch { /* docker может быть не установлен — это норма для инстанса без песочницы */ }
    }

    // Перенести секреты из прежнего каталога данных в восстановленный.
    // Молча пропускаем то, чего не было: на чистой машине переносить нечего, там
    // секреты приходят только из --secrets.
    private static void CarryOverSecrets(string oldDataDir, string newDataDir, Action<string> say)
    {
        var carried = 0;
        foreach (var name in BackupPaths.SecretFileNames)
        {
            var source = Path.Combine(oldDataDir, name);
            var target = Path.Combine(newDataDir, name);
            if (!File.Exists(source) || File.Exists(target)) continue;

            try
            {
                File.Copy(source, target);
                carried++;
            }
            catch (Exception ex)
            {
                say($"ВНИМАНИЕ: не удалось перенести {name}: {ex.Message}");
            }
        }

        if (carried > 0)
            say($"Секреты текущего инстанса сохранены ({carried}) — сессии и push-подписки живы");
    }

    // Вернуть сами архивы, если папка бэкапов лежит внутри data (дефолт {data}/backups).
    // В архив она не входит (иначе бэкап содержал бы бэкапы), а каталог мы заменили —
    // без переноса первое же восстановление стирало бы всю историю снапшотов, включая
    // страховочный, снятый минуту назад.
    private static void CarryOverBackups(BackupContext ctx, string oldDataDir, Action<string> say)
    {
        var backupDir = Path.GetFullPath(ctx.BackupDir);
        var dataDir = Path.GetFullPath(ctx.DataDir);
        if (!backupDir.StartsWith(dataDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return;

        var relative = Path.GetRelativePath(dataDir, backupDir);
        var source = Path.Combine(oldDataDir, relative);
        if (!Directory.Exists(source)) return;

        try
        {
            Directory.CreateDirectory(backupDir);
            var moved = 0;
            foreach (var file in Directory.GetFiles(source))
            {
                var target = Path.Combine(backupDir, Path.GetFileName(file));
                if (File.Exists(target)) continue;
                File.Copy(file, target);
                moved++;
            }
            if (moved > 0) say($"История бэкапов возвращена на место ({moved} файлов)");
        }
        catch (Exception ex)
        {
            say($"ВНИМАНИЕ: архивы остались только в {source}: {ex.Message}");
        }
    }

    private static void RestoreSecrets(BackupContext ctx, string archive, Action<string> say)
    {
        try
        {
            using var zip = ZipFile.OpenRead(archive);
            foreach (var entry in zip.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name)) continue;

                // В архиве секретов две группы: data/<файл> и <appsettings рядом с exe>
                var target = entry.FullName.StartsWith("data/", StringComparison.OrdinalIgnoreCase)
                    ? Path.Combine(ctx.DataDir, entry.Name)
                    : Path.Combine(ctx.BaseDirectory, entry.Name);

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, overwrite: true);
            }
            say("Секреты восстановлены");
        }
        catch (Exception ex)
        {
            say($"ВНИМАНИЕ: секреты восстановить не удалось: {ex.Message}");
        }
    }

    private static void TryRollback(string movedTo, string dataDir)
    {
        try
        {
            if (Directory.Exists(movedTo) && !Directory.Exists(dataDir))
                Directory.Move(movedTo, dataDir);
        }
        catch { /* дальше уже только руками */ }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* не критично */ }
    }
}
