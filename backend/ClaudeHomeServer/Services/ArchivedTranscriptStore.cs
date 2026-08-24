using ClaudeHomeServer.Services.Llm;

namespace ClaudeHomeServer.Services;

/// <summary>
/// Копии транскриптов claude CLI заархивированных чатов: data/archived-transcripts/{csid}.jsonl
/// (+ одноимённая папка с сабагентами). Ретенция CLI (cleanupPeriodDays, дефолт ~30 дней)
/// вычищает {csid}.jsonl из профиля — и вместе с ним контекст --resume возвращённого из
/// архива чата: карточка и лента целы (history.json наш), а разговор начался бы с нуля.
/// Копия на нашей территории от ретенции не зависит и работает одинаково для local и
/// container. Корень лежит в data → едет в бэкап автоматически (BackupPaths трогать не
/// нужно; цена — байты транскриптов в архиве бэкапа удваиваются, это осознанный дубль).
/// Всё best-effort: сбой копии не имеет права ронять архивацию или возврат — карточка
/// архива в этом случае честно говорит «контекст разговора мог устареть».
/// </summary>
public sealed class ArchivedTranscriptStore
{
    private const string DirName = "archived-transcripts";

    // Потолок копии — защита места на диске, и только она. Гейт десктопных чатов —
    // отдельный (desktopChat) и по другой причине: кадры рабочего стола не отдаём
    // наружу. Порог по размеру в качестве того гейта промахивается в обе стороны:
    // мелкий десктопный скопировался бы, а крупный обычный — самый ценный — нет.
    // internal для теста: юнит уменьшает порог, не разводя полгигабайта на диске.
    internal long MaxCopyBytes = 512L * 1024 * 1024;

    private readonly string _root;
    private readonly ILogger<ArchivedTranscriptStore>? _log;

    public ArchivedTranscriptStore(IConfiguration config, ILogger<ArchivedTranscriptStore>? log = null)
    {
        var dataDir = Path.GetDirectoryName(
            config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json"))
            ?? Path.Combine(AppContext.BaseDirectory, "data");
        _root = Path.Combine(dataDir, DirName);
        _log = log;
    }

    // Прямой корень — для юнит-тестов без IConfiguration
    internal ArchivedTranscriptStore(string rootDir, ILogger<ArchivedTranscriptStore>? log = null)
    {
        _root = rootDir;
        _log = log;
    }

    /// <summary>
    /// Захоронить копию транскрипта при архивации чата. Источник ищется по тем же правилам,
    /// что и уборка при удалении (FindAllTranscripts по всем корням профилей); из нескольких
    /// копий берётся самая длинная — самые полные. false = копии нет (транскрипт уже вычистил
    /// CLI, ходов не было, десктопный чат, ключ небезопасен) — не ошибка, чат архивируется.
    /// </summary>
    public bool Archive(string? claudeSessionId, bool desktopChat,
        IEnumerable<string> searchRoots, string? cwd)
    {
        // Десктопные чаты: в их jsonl — кадры рабочего стола; наружу (бэкап уезжает в облако)
        // не отдаём. Гейт по признаку чата, а не по размеру файла — см. MaxCopyBytes
        if (desktopChat) return false;
        // ClaudeSessionId — внешний ключ (resumeSessionId из POST /api/chats): без белого
        // списка запись по нему — path traversal (тот же инвариант, что у DeleteEverywhere)
        if (!TranscriptMigrator.IsSafeSessionId(claudeSessionId)) return false;

        try
        {
            var src = FindLongest(searchRoots, cwd, claudeSessionId!);
            if (src is null) return false;

            var srcLen = new FileInfo(src).Length;
            if (srcLen > MaxCopyBytes)
            {
                _log?.LogInformation(
                    "Копия транскрипта {SessionId} не создана: {Size} байт выше порога {Limit}",
                    claudeSessionId, srcLen, MaxCopyBytes);
                return false;
            }

            var dstFile = FileService.SafeJoin(_root, claudeSessionId + ".jsonl");
            // Не затираем более полную копию усечённым источником (та же страховка, что
            // preserveLongerDestination в TryMigrate): после возврата и новых ходов источник
            // длиннее и перезапишет, а короче бывает только у повреждённого/пустого профиля
            if (File.Exists(dstFile) && new FileInfo(dstFile).Length > srcLen) return true;

            Directory.CreateDirectory(_root);
            CopyShared(src, dstFile);

            // Папка сабагентов {csid}/ рядом с транскриптом — копируется и удаляется вместе
            // с ним; без неё resume работает, поэтому ошибки глотаем (как CopyDirectory в
            // TranscriptMigrator)
            var srcSessionDir = Path.Combine(Path.GetDirectoryName(src)!, claudeSessionId!);
            if (Directory.Exists(srcSessionDir))
                CopyDirectory(srcSessionDir, FileService.SafeJoin(_root, claudeSessionId!));
            return true;
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "Копия транскрипта {SessionId} не создана", claudeSessionId);
            return false;
        }
    }

    /// <summary>
    /// Положить копию обратно при возврате чата. Цель резолвится НА МОМЕНТ возврата и только
    /// из аргументов вызова: dstRoot — текущий профиль провайдера чата (ConfigRootFor),
    /// cwd — текущая рабочая папка глазами CLI (CwdForOwner). За время в архиве могли
    /// смениться и профиль (MigrateProviderAsync), и папка уплощённого cwd (worktree, правка
    /// RootPath) — ничего не запоминаем, архив плоский. false = копии нет (штатно: чат
    /// архивирован до появления стора, десктопный) — чат возвращается без контекста CLI.
    /// </summary>
    public bool Restore(string? claudeSessionId, string dstRoot, string cwd)
    {
        if (!TranscriptMigrator.IsSafeSessionId(claudeSessionId)) return false;
        try
        {
            var archived = FileService.SafeJoin(_root, claudeSessionId + ".jsonl");
            if (!File.Exists(archived)) return false;

            // Правила TryRelocateCwd: целевая папка считается от ТЕКУЩЕГО cwd (у TryMigrate
            // она берётся от источника, но у архивной копии структуры профиля нет)
            var dstDir = Path.Combine(dstRoot, "projects", TranscriptMigrator.FlattenCwd(cwd));
            Directory.CreateDirectory(dstDir);
            var dstFile = Path.Combine(dstDir, claudeSessionId + ".jsonl");

            // CLI ещё держит живую историю (возврат раньше ретенции) — не затираем её копией:
            // приёмник не короче архива, значит цель уже достигнута
            if (File.Exists(dstFile) && new FileInfo(dstFile).Length >= new FileInfo(archived).Length)
                return true;

            File.Copy(archived, dstFile, overwrite: true);

            var archivedDir = FileService.SafeJoin(_root, claudeSessionId);
            if (Directory.Exists(archivedDir))
                CopyDirectory(archivedDir, Path.Combine(dstDir, claudeSessionId));
            // Копию из архива не удаляем: при повторной архивации перезапишется, а срыв
            // файловой системы после возврата оставил бы чат без страховки
            return true;
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "Копия транскрипта {SessionId} не возвращена", claudeSessionId);
            return false;
        }
    }

    /// <summary>Есть ли архивная копия (для карточки: без неё «контекст мог устареть»).</summary>
    public bool HasCopy(string? claudeSessionId) =>
        TranscriptMigrator.IsSafeSessionId(claudeSessionId)
        && File.Exists(FileService.SafeJoin(_root, claudeSessionId + ".jsonl"));

    /// <summary>
    /// Унести копию при удалении чата — иначе переписка переживёт сам чат и в data, и в
    /// бэкапе. Best-effort: удаление чата важнее уборки, ошибки глотаем.
    /// </summary>
    public void Delete(string? claudeSessionId)
    {
        if (!TranscriptMigrator.IsSafeSessionId(claudeSessionId)) return;
        try
        {
            File.Delete(FileService.SafeJoin(_root, claudeSessionId + ".jsonl"));
            var dir = FileService.SafeJoin(_root, claudeSessionId);
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "Архивная копия транскрипта {SessionId} не убрана", claudeSessionId);
        }
    }

    // Самая длинная из всех копий транскрипта в профилях: длина файла — дешёвый proxy
    // полноты (прецедент — сравнение в TryMigrate). Копии остаются после миграций
    // (TryMigrate/TryRelocateCwd исходники не удаляют), «первая найденная» могла бы
    // оказаться устаревшим срезом
    private static string? FindLongest(IEnumerable<string> roots, string? cwd, string csid)
    {
        string? best = null;
        long bestLen = -1;
        foreach (var file in TranscriptMigrator.FindAllTranscripts(roots, cwd, csid))
        {
            var len = new FileInfo(file).Length;
            if (len <= bestLen) continue;
            best = file;
            bestLen = len;
        }
        return best;
    }

    // Копия в обход эксклюзивного захвата источника (упрощённый CopyFileShared из
    // TranscriptMigrator): при архивации хода в полёте нет (гейт живости), но умирающий
    // процесс CLI способен ещё пару секунд держать .jsonl — FileShare.ReadWrite открывает
    // его параллельно с записью; не вышло — IOException уходит в catch Archive (best-effort)
    private static void CopyShared(string src, string dst)
    {
        const int bufferSize = 81920;
        using var srcStream = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize);
        using var dstStream = new FileStream(dst, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize);
        srcStream.CopyTo(dstStream);
    }

    // Папка сессии (сабагенты) — без неё resume работает, ошибки глотаем
    private static void CopyDirectory(string src, string dst)
    {
        try
        {
            Directory.CreateDirectory(dst);
            foreach (var file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(dst, Path.GetRelativePath(src, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, overwrite: true);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ArchivedTranscriptStore] Папка сессии не скопирована ({src}): {ex.Message}");
        }
    }
}
