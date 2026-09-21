using System.Collections;
using System.Collections.Concurrent;
using System.Text.Json;

namespace ClaudeHomeServer.Services;

// Надёжная JSON-персистентность для файловых хранилищ:
// - чтение не теряет данные: битая запись стоит только себя, повреждённый целиком файл
//   переименовывается в .corrupt-*.bak (а не перезатирается), состояние поднимается из
//   прошлого .bak, и лишь когда не вышло ничего — пустое состояние с алертом наверх;
// - запись атомарна: сериализация во временный файл + File.Move с заменой (на Windows — атомарная замена),
//   крэш посреди записи не портит целевой файл; tmp сбрасывается на диск (fsync) до переноса.
// Локов не берёт — синхронизация остаётся на стороне вызывающих.
public static class JsonFileStore
{
    /// <summary>
    /// Стор не прочитан вовсе: ни сам файл, ни бэкапы — подсистема стартовала с пустым состоянием.
    /// </summary>
    public sealed record DataLossAlert(string Path, string? BackupPath, string Reason);

    /// <summary>
    /// Читает и десериализует JSON. Файла нет → default. Парсинг упал — по порядку:
    /// поэлементный разбор коллекции (битые записи пропускаются с WARN, файл остаётся на месте) →
    /// откат на самый свежий {path}.corrupt-*.bak → default с алертом в <see cref="DataLossSink"/>.
    /// В последних двух случаях повреждённый файл сохраняется как {path}.corrupt-{timestamp}.bak.
    /// </summary>
    public static T? Load<T>(string path, JsonSerializerOptions? options = null, ILogger? logger = null)
    {
        if (!File.Exists(path)) return default;
        string? json = null;
        try
        {
            json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<T>(json, options);
        }
        catch (Exception ex)
        {
            // Одна битая запись из сотен не должна стоить всего стора (инцидент 19.09.2026:
            // 22 записи с null в non-nullable поле унесли все 866 сессий). Поэлементный разбор
            // включается ТОЛЬКО после провала обычного — на здоровых файлах путь чтения прежний.
            if (json is not null && TryLoadPerItem<T>(json, options, logger, path, out var partial))
                return partial;
            return Recover<T>(path, ex, options, logger);
        }
    }

    // Полный провал разбора: пробуем поднять состояние из прошлого снимка, и только если
    // не вышло — отдаём пустое, но громко.
    private static T? Recover<T>(string path, Exception ex, JsonSerializerOptions? options, ILogger? logger)
    {
        // Кандидатов на откат перечисляем ДО того, как положим рядом новый .bak: иначе
        // самым свежим окажется только что сохранённый мусор.
        var candidates = RecentBackups(path);
        var backupPath = MoveAside(path, logger);

        foreach (var candidate in candidates)
        {
            if (!TryReadFile<T>(candidate, options, logger, out var restored)) continue;
            LogWarn(logger, $"{path} не читается ({ex.Message}) — состояние поднято из {Path.GetFileName(candidate)}");
            return restored;
        }

        var reason = ex.Message;
        LogError(logger, ex, $"не удалось прочитать {path} — повреждённый файл сохранён как {backupPath}, стартуем с пустым состоянием");
        ReportDataLoss(new DataLossAlert(path, backupPath, reason));
        return default;
    }

    // Читает файл «тихо», без побочных эффектов: ни переименований, ни алертов — бэкап,
    // из которого не вышло подняться, должен остаться лежать как есть.
    private static bool TryReadFile<T>(string file, JsonSerializerOptions? options, ILogger? logger, out T? value)
    {
        value = default;
        string json;
        try
        {
            json = File.ReadAllText(file);
        }
        catch (Exception ex)
        {
            LogWarn(logger, $"бэкап {file} не прочитан: {ex.Message}");
            return false;
        }

        try
        {
            value = JsonSerializer.Deserialize<T>(json, options);
            // Разобранный в null бэкап (файл с текстом «null») — не состояние, а пустышка:
            // перебор кандидатов на нём обрываться не должен.
            return value is not null;
        }
        catch
        {
            return TryLoadPerItem(json, options, logger, file, out value);
        }
    }

    /// <summary>
    /// Разбирает коллекцию поэлементно: JSON-массив в List-подобный T, JSON-объект в
    /// Dictionary&lt;string, V&gt;. Битый элемент пропускается с WARN (индекс/ключ + текст ошибки).
    /// Не коллекция, не разбирается сам JSON или не уцелел ни один элемент — false, и вызывающий
    /// идёт прежним путём.
    /// </summary>
    private static bool TryLoadPerItem<T>(string json, JsonSerializerOptions? options, ILogger? logger, string path, out T? value)
    {
        value = default;
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch
        {
            // Сам JSON синтаксически битый — по элементам его не разложить.
            return false;
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Array && TryBuildList<T>(root, options, logger, path, out value))
                return true;
            if (root.ValueKind == JsonValueKind.Object && TryBuildDictionary<T>(root, options, logger, path, out value))
                return true;
            return false;
        }
    }

    private static bool TryBuildList<T>(JsonElement root, JsonSerializerOptions? options, ILogger? logger, string path, out T? value)
    {
        value = default;
        var itemType = EnumerableItemType(typeof(T));
        // Собираем в List<итем>: им закрываются все формы T у вызывающих (List<X>, IList<X>,
        // IReadOnlyList<X>, IEnumerable<X>). Экзотику (массивы, HashSet) в продукте не зовут,
        // и подменять её самодельной сборкой ради гипотетики незачем.
        if (itemType is null) return false;
        var listType = typeof(List<>).MakeGenericType(itemType);
        if (!typeof(T).IsAssignableFrom(listType)) return false;

        var list = (IList)Activator.CreateInstance(listType)!;
        var skipped = 0;
        var index = -1;
        foreach (var item in root.EnumerateArray())
        {
            index++;
            try
            {
                list.Add(item.Deserialize(itemType, options));
            }
            catch (Exception ex)
            {
                skipped++;
                LogWarn(logger, $"{path}: запись #{index} пропущена — {ex.Message}");
            }
        }

        // Не уцелело ничего — файл мусорный, дальше им занимается откат на бэкап.
        if (list.Count == 0) return false;
        if (skipped > 0)
            LogWarn(logger, $"{path}: поднято записей {list.Count}, пропущено битых {skipped} (файл оставлен на месте)");
        value = (T)list;
        return true;
    }

    private static bool TryBuildDictionary<T>(JsonElement root, JsonSerializerOptions? options, ILogger? logger, string path, out T? value)
    {
        value = default;
        // Только Dictionary<string, V>: ключи всех словарных сторов продукта строковые,
        // а разбор ключа произвольного типа пришлось бы дублировать за System.Text.Json.
        var type = typeof(T);
        if (!type.IsGenericType || type.GetGenericTypeDefinition() != typeof(Dictionary<,>)) return false;
        var args = type.GetGenericArguments();
        if (args[0] != typeof(string)) return false;

        var dict = (IDictionary)Activator.CreateInstance(type)!;
        var skipped = 0;
        foreach (var prop in root.EnumerateObject())
        {
            try
            {
                dict[prop.Name] = prop.Value.Deserialize(args[1], options);
            }
            catch (Exception ex)
            {
                skipped++;
                LogWarn(logger, $"{path}: запись «{prop.Name}» пропущена — {ex.Message}");
            }
        }

        if (dict.Count == 0) return false;
        if (skipped > 0)
            LogWarn(logger, $"{path}: поднято записей {dict.Count}, пропущено битых {skipped} (файл оставлен на месте)");
        value = (T)dict;
        return true;
    }

    // Тип элемента коллекции T (для T = IEnumerable<X> — сам T).
    private static Type? EnumerableItemType(Type type)
    {
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            return type.GetGenericArguments()[0];
        return type.GetInterfaces()
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            ?.GetGenericArguments()[0];
    }

    // Самые свежие .corrupt-*.bak рядом со стором, новые первыми. Потолок в пять штук:
    // перебирать все накопившиеся снимки (на проде их за десяток) дороже, чем полезно.
    private static IReadOnlyList<string> RecentBackups(string path)
    {
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return [];
            return new DirectoryInfo(dir)
                .GetFiles($"{Path.GetFileName(path)}.corrupt-*.bak")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Take(5)
                .Select(f => f.FullName)
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    // Уносит повреждённый файл в .corrupt-*.bak. Имя уникализируется: в пределах одной
    // секунды перенос с overwrite затёр бы прошлый снимок — то есть ровно то, из чего
    // мы собираемся восстанавливаться.
    private static string MoveAside(string path, ILogger? logger)
    {
        var stem = $"{path}.corrupt-{DateTime.UtcNow:yyyyMMdd-HHmmss}";
        var backupPath = $"{stem}.bak";
        for (var i = 2; File.Exists(backupPath); i++) backupPath = $"{stem}-{i}.bak";
        try
        {
            File.Move(path, backupPath);
        }
        catch (Exception moveEx)
        {
            LogError(logger, moveEx, $"не удалось сохранить повреждённый файл как {backupPath}");
        }
        return backupPath;
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
            // Данные tmp обязаны лечь на диск ДО переименования: иначе при внезапном
            // выключении ФС (ext4 delalloc, NTFS) успевает сохранить rename, но не
            // содержимое — и на месте стора остаётся файл нулевой длины или из нулей.
            // Так дважды терялся sessions.json (17.09 и 19.09.2026).
            using (var fs = new FileStream(tmpPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(fs, value, options);
                fs.Flush(flushToDisk: true);
            }
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

    private static readonly List<DataLossAlert> PendingAlerts = [];
    private static Action<DataLossAlert>? _dataLossSink;

    /// <summary>
    /// Куда сообщать о старте с пустым состоянием — доставку ставит композиция (DI здесь
    /// невозможен: сторы читаются из конструкторов, а часть — до сборки контейнера).
    /// Алерты, случившиеся ДО подписки, не теряются: они копятся и уходят в момент установки —
    /// иначе как раз случай инцидента (чтение сторов на старте) остался бы незамеченным.
    /// </summary>
    public static Action<DataLossAlert>? DataLossSink
    {
        get { lock (PendingAlerts) return _dataLossSink; }
        set
        {
            DataLossAlert[] pending;
            lock (PendingAlerts)
            {
                _dataLossSink = value;
                pending = [.. PendingAlerts];
                PendingAlerts.Clear();
            }
            if (value is null) return;
            foreach (var alert in pending) Invoke(value, alert);
        }
    }

    private static void ReportDataLoss(DataLossAlert alert)
    {
        Action<DataLossAlert>? sink;
        lock (PendingAlerts)
        {
            sink = _dataLossSink;
            // Потолок буфера: если пустыми встали все сторы разом, подписчику хватит первых —
            // авария уже очевидна, а неограниченный список тут не нужен никому.
            if (sink is null)
            {
                if (PendingAlerts.Count < 16) PendingAlerts.Add(alert);
                return;
            }
        }
        Invoke(sink, alert);
    }

    private static void Invoke(Action<DataLossAlert> sink, DataLossAlert alert)
    {
        try
        {
            sink(alert);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[JsonFileStore] доставка алерта о потере данных не удалась: {ex.Message}");
        }
    }

    private static void LogError(ILogger? logger, Exception ex, string message)
    {
        if (logger is not null)
            logger.LogError(ex, "JsonFileStore: {Message}", message);
        else
            Console.Error.WriteLine($"[JsonFileStore] {message}: {ex.Message}");
    }

    private static void LogWarn(ILogger? logger, string message)
    {
        if (logger is not null)
            logger.LogWarning("JsonFileStore: {Message}", message);
        else
            Console.Error.WriteLine($"[JsonFileStore] {message}");
    }
}
