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
    /// Атомарно записывает значение как JSON: во временный файл {path}.tmp рядом,
    /// затем File.Move с заменой. Директория создаётся при необходимости.
    /// </summary>
    public static void Save<T>(string path, T value, JsonSerializerOptions? options = null)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var tmpPath = path + ".tmp";
        File.WriteAllText(tmpPath, JsonSerializer.Serialize(value, options));
        File.Move(tmpPath, path, overwrite: true);
    }

    private static void LogError(ILogger? logger, Exception ex, string message)
    {
        if (logger is not null)
            logger.LogError(ex, "JsonFileStore: {Message}", message);
        else
            Console.Error.WriteLine($"[JsonFileStore] {message}: {ex.Message}");
    }
}
