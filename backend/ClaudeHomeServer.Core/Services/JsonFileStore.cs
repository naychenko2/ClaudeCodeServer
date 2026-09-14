using System.Collections.Concurrent;
using System.Text.Json;

namespace ClaudeHomeServer.Services;

// Надёжная JSON-персистентность для файловых хранилищ:
// - чтение не теряет данные: повреждённый файл переименовывается в .corrupt-*.bak, а не перезатирается;
// - запись атомарна: сериализация во временный файл + File.Move с заменой (на Windows — атомарная замена),
//   крэш посреди записи не портит целевой файл.
// Локов не берёт — синхронизация остаётся на стороне вызывающих.
public static class JsonFileStore
{
    /// <summary>
    /// Читает и десериализует JSON. Файла нет → default. Парсинг упал → повреждённый файл
    /// сохраняется как {path}.corrupt-{timestamp}.bak, ошибка логируется, возвращается default.
    /// </summary>
    public static T? Load<T>(string path, JsonSerializerOptions? options = null, ILogger? logger = null)
    {
        if (!File.Exists(path)) return default;
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<T>(json, options);
        }
        catch (Exception ex)
        {
            var backupPath = $"{path}.corrupt-{DateTime.UtcNow:yyyyMMdd-HHmmss}.bak";
            try
            {
                File.Move(path, backupPath, overwrite: true);
            }
            catch (Exception moveEx)
            {
                LogError(logger, moveEx, $"не удалось сохранить повреждённый файл как {backupPath}");
            }
            LogError(logger, ex, $"не удалось прочитать {path} — повреждённый файл сохранён как {backupPath}, стартуем с пустым состоянием");
            return default;
        }
    }

    /// <summary>
    /// Атомарно записывает значение как JSON: во временный файл {path}.{uid}.tmp рядом,
    /// затем File.Move с заменой. Директория создаётся при необходимости.
    /// </summary>
    public static void Save<T>(string path, T value, JsonSerializerOptions? options = null)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        // Уникальное имя временного файла вместо фиксированного «{path}.tmp»: при двух
        // одновременных Save по одному пути они писали в ОДИН файл, и перенос падал
        // (один поток ещё держит его на запись). Суффикс .tmp сохранён — по нему
        // TurnFileWatcher отсеивает служебные файлы.
        var tmpPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(tmpPath, JsonSerializer.Serialize(value, options));
            // Внутри процесса записи по одному пути выстраиваются в очередь: два
            // одновременных File.Move поверх одного файла на Windows дают Access denied.
            lock (LockFor(path)) MoveWithRetry(tmpPath, path);
        }
        catch
        {
            try { File.Delete(tmpPath); } catch { /* мусорный tmp не важнее исходной ошибки */ }
            throw;
        }
    }

    // Лок на путь (не на файл): число сторов конечно, словарь не растёт бесконтрольно
    private static readonly ConcurrentDictionary<string, Lock> PathLocks = new(StringComparer.OrdinalIgnoreCase);

    private static Lock LockFor(string path) =>
        PathLocks.GetOrAdd(Path.GetFullPath(path), _ => new Lock());

    // На Windows перенос поверх существующего файла регулярно ловит транзиторный
    // UnauthorizedAccessException/IOException: свежесозданный tmp или целевой файл на доли
    // секунды держит антивирус/индексатор. Это давало «мигающие» падения записи (и тестов),
    // хотя никакой реальной проблемы с правами нет — поэтому короткий ретрай с бэкоффом.
    private static void MoveWithRetry(string tmpPath, string path)
    {
        // Бюджет ожидания ~1 с: пересидеть сканирование файла антивирусом дешевле,
        // чем потерять сохранение состояния.
        const int attempts = 10;
        for (var i = 1; ; i++)
        {
            try
            {
                File.Move(tmpPath, path, overwrite: true);
                return;
            }
            catch (Exception ex) when (i < attempts && ex is UnauthorizedAccessException or IOException)
            {
                Thread.Sleep(20 * i);
            }
        }
    }

    private static void LogError(ILogger? logger, Exception ex, string message)
    {
        if (logger is not null)
            logger.LogError(ex, "JsonFileStore: {Message}", message);
        else
            Console.Error.WriteLine($"[JsonFileStore] {message}: {ex.Message}");
    }
}
