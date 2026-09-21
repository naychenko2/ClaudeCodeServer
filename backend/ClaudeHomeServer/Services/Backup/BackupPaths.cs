namespace ClaudeHomeServer.Services.Backup;

// Что попадает в архив и куда его можно класть. Чистая логика над строками путей —
// вынесена из BackupCore, чтобы покрываться тестами без файловой системы.
public static class BackupPaths
{
    // Рабочая папка сборки архива внутри data (сама в архив не идёт)
    public const string StagingDirName = ".backup-staging";
    public const string StateFileName = "backup-state.json";
    public const string PostRestoreMarker = ".post-restore";
    public const string DefaultBackupDirName = "backups";
    public const string DefaultSecretsDirName = "backups-secrets";

    // Профили claude CLI: внутри них бэкапим ТОЛЬКО подпапку projects (транскрипты для
    // --resume; они невосстановимы). Всё остальное — `.credentials.json` с OAuth-токенами,
    // синканные plugins, кеши — осознанно за бортом: основной архив уезжает в облако.
    private static readonly string[] ProfileRoots = ["claude-profiles", "sandbox-profiles"];

    // Секреты: в основной архив не попадают никогда, уезжают отдельным локальным архивом.
    // Реестр имён — примитив инфраструктуры, вынесен в `Services.InstanceSecretFiles`
    // (задача `57b5e9bc`, шаг 5): общий источник для Backup и редактора секретов
    // (`Dossiers.InstanceSecretsProvider`). Здесь оставлен тонкий алиас ради обратной
    // совместимости внутренних вызовов Backup.
    public static readonly string[] SecretFileNames = InstanceSecretFiles.Names;

    /// <summary>
    /// Брать ли файл в основной архив. <paramref name="relativePath"/> — путь относительно
    /// корня data с разделителем '/'.
    /// </summary>
    public static bool ShouldInclude(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return false;

        var normalized = relativePath.Replace('\\', '/').TrimStart('/');
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0) return false;

        var fileName = segments[^1];

        // Креденшалы не уезжают в облако ни при каких обстоятельствах
        if (fileName.EndsWith(".credentials.json", StringComparison.OrdinalIgnoreCase)) return false;

        // Мусор и служебное
        if (fileName.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) return false;
        if (fileName.EndsWith(".part", StringComparison.OrdinalIgnoreCase)) return false;
        if (fileName.Contains(".corrupt-", StringComparison.OrdinalIgnoreCase)) return false;
        if (fileName.Equals(StateFileName, StringComparison.OrdinalIgnoreCase)) return false;
        if (fileName.Equals(PostRestoreMarker, StringComparison.OrdinalIgnoreCase)) return false;

        // Реестр PID процессов-детей. Восстановленный файл заставил бы KillOrphansFromFile
        // на следующем старте убить по протухшим PID всё, что зовётся claude/node —
        // включая чужие dev-серверы на машине.
        if (fileName.Equals(Execution.ProcessRegistry.PidFileName, StringComparison.OrdinalIgnoreCase))
            return false;

        var root = segments[0];

        if (root.Equals("logs", StringComparison.OrdinalIgnoreCase)) return false;
        // Конфиги MCP на один ход и cwd one-shot вызовов — живут минуты
        if (root.Equals("sandbox-tmp", StringComparison.OrdinalIgnoreCase)) return false;
        // Корзина синка профилей (ADR-015 §5.3): мусорная зона с TTL, в облако ехать
        // не должна и восстановлению из архива не подлежит. По ADR-015 восстановление
        // архива = повторное усыновление, а не возврат .sync-trash.
        if (root.Equals(Llm.SyncTrashPaths.RootDirName, StringComparison.OrdinalIgnoreCase)) return false;
        // Последний известный статус MCP-серверов: наблюдение, а не настройка. Восстановленное
        // из архива, оно врёт — описывает состояние чужой машины в прошлом. Заново приедет
        // из первого же system/init (или пробы по кнопке).
        if (segments.Length == 1
            && fileName.Equals(Mcp.McpStatusStore.FileName, StringComparison.OrdinalIgnoreCase))
            return false;

        // Кеш карт планов (место plan-map): восстанавливается одним вызовом по кнопке
        // «Собрать схему» — в облачный архив не едет
        if (segments.Length == 1
            && fileName.Equals(Llm.PlanMapService.CacheFileName, StringComparison.OrdinalIgnoreCase))
            return false;

        // Снимок списка инструментов Higgsfield — кеш tools/list, который прокси пишет
        // при каждом успешном опросе апстрима (шаг 2 дозадачи 6e309216). Секретов нет,
        // но восстанавливать его из архива бесполезно: список всё равно устаревший, а
        // первый же успешный handshake файл перезапишет. Переехавший из бэкапа снимок
        // заставит ходы 30 мин жить на чужой версии схем — это регрессия, а не польза.
        if (segments.Length == 1
            && fileName.Equals(Mcp.Http.HiggsfieldToolset.SnapshotFileName, StringComparison.OrdinalIgnoreCase))
            return false;

        // Снимки промпта ходов — диагностический лог (последние 50 ходов на чат):
        // восстанавливать нечего, а в облако они бы поехали десятками мегабайт
        if (root.Equals("prompt-snapshots", StringComparison.OrdinalIgnoreCase)) return false;
        // Замеры размера постановки задач — наблюдение, а не настройка (как mcp-status.json):
        // растут линейно с числом запусков, восстанавливать нечего. Сама аналитика расхода
        // (spend/turns-*.jsonl, spend/daily.json) в архив едет — исключён только этот файл.
        // Константы из TaskPromptMetricsStore (Spend) влиты сюда как литералы, чтобы Main
        // не тянул конкретную сборку Spend: смена имён файлов/директорий — редкое событие.
        if (root.Equals("spend", StringComparison.OrdinalIgnoreCase)
            && fileName.Equals("task-prompts.jsonl", StringComparison.OrdinalIgnoreCase))
            return false;
        if (root.Equals(StagingDirName, StringComparison.OrdinalIgnoreCase)) return false;

        // Встроенный Forgejo (forgejo/**): репозитории и gitea.db едут, но три подпапки нет.
        // ssh — ключи хоста (приватные; контейнер создаёт их от root с правами 600, сервер
        // их даже прочитать не может). gitea/queues (LevelDB) и gitea/indexers (bleve/bolt)
        // живой контейнер держит под блокировкой, и копирование обрывало весь бэкап. Все три
        // Forgejo пересоздаёт сам: ключи — при старте (сменится лишь отпечаток хоста),
        // очередь и поисковый индекс — пересборкой.
        if (root.Equals("forgejo", StringComparison.OrdinalIgnoreCase) && segments.Length >= 2)
        {
            if (segments[1].Equals("ssh", StringComparison.OrdinalIgnoreCase)) return false;
            if (segments.Length >= 3 && segments[1].Equals("gitea", StringComparison.OrdinalIgnoreCase)
                && (segments[2].Equals("queues", StringComparison.OrdinalIgnoreCase)
                    || segments[2].Equals("indexers", StringComparison.OrdinalIgnoreCase)))
                return false;
        }

        // Кеш CodeGraph: code-graphs/{hash}/cache/ — не едет в облако (пересобирается).
        // «cache» — третий сегмент: code-graphs / {hash} / cache / …
        if (root.Equals("code-graphs", StringComparison.OrdinalIgnoreCase))
        {
            // Основной граф (graph.json) — в архив, кеш — нет
            if (segments.Length >= 3 && segments[2].Equals("cache", StringComparison.OrdinalIgnoreCase))
                return false;
        }
        // Дефолтные папки архивов внутри data (нестандартные пути отсекаются по абсолютному
        // пути в BackupCore — имя папки там может быть любым)
        if (root.Equals(DefaultBackupDirName, StringComparison.OrdinalIgnoreCase)) return false;
        if (root.Equals(DefaultSecretsDirName, StringComparison.OrdinalIgnoreCase)) return false;

        // Секреты — только в отдельный архив
        if (segments.Length == 1 && SecretFileNames.Contains(fileName, StringComparer.OrdinalIgnoreCase))
            return false;

        if (ProfileRoots.Contains(root, StringComparer.OrdinalIgnoreCase))
        {
            // Профиль без вложенности — только сам каталог, брать нечего
            if (segments.Length < 3) return false;
            // Внутри профиля берём исключительно ветку projects/** (транскрипты)
            return segments.Skip(1).Any(s => s.Equals("projects", StringComparison.OrdinalIgnoreCase));
        }

        return true;
    }

    /// <summary>Результат проверки пути назначения: пусто = путь годен.</summary>
    public static string? ValidateBackupPath(
        string path, string dataDir, string baseDirectory,
        IEnumerable<string> projectRoots, string? sandboxProjectsRoot)
    {
        if (string.IsNullOrWhiteSpace(path)) return null; // пусто = дефолт {data}/backups

        if (!Path.IsPathRooted(path)) return "Путь должен быть абсолютным";

        var full = Normalize(path);

        // Внутри папки проекта архивы попали бы под файловый ватчер и уехали
        // документами в базу знаний Dify
        foreach (var rootPath in projectRoots)
        {
            if (string.IsNullOrWhiteSpace(rootPath)) continue;
            if (IsInside(full, Normalize(rootPath)))
                return "Папка внутри проекта: архивы попадут в базу знаний. Выбери папку вне проектов";
        }

        // Корень песочницы монтируется в контейнер целиком — архив со всеми данными
        // всех пользователей стал бы читаемым изнутри песочницы
        if (!string.IsNullOrWhiteSpace(sandboxProjectsRoot)
            && IsInside(full, Normalize(sandboxProjectsRoot)))
            return "Папка внутри корня песочницы: архив будет виден изолированным пользователям";

        var data = Normalize(dataDir);
        var defaultBackups = Normalize(Path.Combine(dataDir, DefaultBackupDirName));
        if (IsInside(full, data) && !PathsEqual(full, defaultBackups))
            return "Папка внутри data: бэкап окажется внутри того, что бэкапим";

        if (PathsEqual(full, Normalize(baseDirectory)))
            return "Папка приложения не годится: деплой перезаписывает её содержимое";

        return null;
    }

    /// <summary>
    /// Проверка пути для архива секретов. Правила мягче, чем у BackupPath: дефолт лежит
    /// ПОДПАПКОЙ каталога приложения (переживает restore, который уносит data целиком),
    /// а в облако он не уезжает — значит запреты про проекты и песочницу тут не нужны.
    /// </summary>
    public static string? ValidateSecretsPath(string path, string dataDir, string baseDirectory)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (!Path.IsPathRooted(path)) return "Путь должен быть абсолютным";

        var full = Normalize(path);

        if (IsInside(full, Normalize(dataDir)))
            return "Папка внутри data: восстановление уносит data целиком вместе с секретами";

        if (IsInside(full, Normalize(Path.Combine(baseDirectory, "wwwroot"))))
            return "Папка wwwroot очищается при деплое";

        return null;
    }

    public static string ResolveBackupDir(string? configured, string dataDir) =>
        string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(dataDir, DefaultBackupDirName)
            : configured;

    public static string ResolveSecretsDir(string? configured, string baseDirectory) =>
        string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(baseDirectory, DefaultSecretsDirName)
            : configured;

    private static string Normalize(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static bool PathsEqual(string a, string b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    // Внутри ли path каталога parent (или равен ему). Сравнение по сегментам, а не по
    // префиксу строки: иначе «C:\dataBackup» считался бы лежащим внутри «C:\data»
    private static bool IsInside(string path, string parent)
    {
        if (PathsEqual(path, parent)) return true;
        var withSeparator = parent + Path.DirectorySeparatorChar;
        return path.StartsWith(withSeparator, StringComparison.OrdinalIgnoreCase);
    }
}
