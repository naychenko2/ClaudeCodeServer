using System.Text.Json;

namespace ClaudeHomeServer.Services.Llm.Claude;

// Точечный доступ к главному транскрипту сессии (<flat-cwd>/<sessionId>.jsonl) без ватчера.
// Нужен для проверки durability сабмита перед skip-реаттемптом хода (инцидент 16.08.2026):
// CLI пишет user-сообщение в .jsonl только когда ЧИТАЕТ его из stdin — убитый до чтения
// процесс не фиксирует сообщение, и повторный ход обязан идти обычным submit'ом, а не
// «доиграется через --resume».
//
// Примитив спины (Этап 5, финал линии Llm): чистая работа с файлом транскрипта без
// состояния и без чужих зависимостей (BCL + `TranscriptRoots`, тоже Core). Нужен обеим
// сторонам границы — вертикали (`ClaudeSession`, `MainTranscriptTailer`,
// `WorkflowAgentParser`) и спине (`SessionManager`), поэтому переехал в Core целиком
// по образцу `SafePath`/`CmdlineEstimate`, а не через шов: статической функции без
// состояния интерфейс не нужен. Namespace сохранён — call-site'ы не изменились.
// `public`, а не `internal`: из Core internal-тип не виден ни Main, ни Llm
// (IVT у Core стоит только на тесты).
public static class TranscriptProbe
{
    // Путь главного транскрипта — те же корни и уплощение cwd, что у MainTranscriptTailer
    // (единственная точка поиска, чтобы поиск не разъезжался между потребителями).
    public static string? FindMainTranscript(string cwd, string claudeSessionId)
    {
        var flat = string.Concat(cwd.Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-'));
        foreach (var root in TranscriptRoots.AllowedRoots)
        {
            if (!Directory.Exists(root)) continue;

            var byConvention = Path.Combine(root, flat, claudeSessionId + ".jsonl");
            if (File.Exists(byConvention)) return byConvention;

            foreach (var projDir in Directory.GetDirectories(root))
            {
                var candidate = Path.Combine(projDir, claudeSessionId + ".jsonl");
                if (File.Exists(candidate)) return candidate;
            }
        }
        return null;
    }

    // Целостность хвоста транскрипта (КР-наблюдаемость, этап 3): последняя непустая строка
    // JSONL обязана парситься и быть непустым объектом. Оборванная запись (kill посреди
    // записи файла) даёт недописанную строку — такой транскрипт нельзя продолжать через
    // --resume. Хвост читается тем же способом, что LastUserText: у длинных сессий файл —
    // десятки МБ, целиком его не читаем.
    public static bool IsTailIntact(string transcriptPath)
    {
        try
        {
            using var fs = new FileStream(transcriptPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var tail = new byte[Math.Min(256 * 1024, fs.Length)];
            fs.Seek(-tail.Length, SeekOrigin.End);
            fs.ReadExactly(tail);

            var lines = System.Text.Encoding.UTF8.GetString(tail).Split('\n');
            for (var i = lines.Length - 1; i >= 0; i--)
            {
                var line = lines[i].TrimEnd('\r');
                if (line.Length == 0) continue;
                using var doc = JsonDocument.Parse(line);
                return doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.EnumerateObject().Any();
            }
            return true; // файл из пустых строк — целостности не нарушена
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[TranscriptProbe] Не удалось проверить хвост транскрипта {transcriptPath}: {ex.Message}");
            return false;
        }
    }

    // uuid ПОСЛЕДНЕЙ записи транскрипта — точный якорь границы хода для ветвления чата
    // (шаг 5 фичи chat-branch, документ-основание §4 «Точный якорь»). Пишется на конце
    // каждого хода в StoredResultMessage.TranscriptTailUuid, и тогда резак ищет границу
    // точным сравнением вместо текстового сопоставления.
    //
    // null — файла нет, хвост битый, uuid в последних записях отсутствует либо любая
    // ошибка ФС: вызывающий остаётся на прежнем текстовом пути (fail-open в сторону
    // старого поведения, а не отказ хода из-за не прочитанного якоря).
    // Читается только хвост файла — транскрипты длинных сессий весят десятки МБ.
    public static string? LastRecordUuid(string? transcriptPath, int tailBytes = 256 * 1024)
    {
        if (transcriptPath is null) return null;
        try
        {
            using var fs = new FileStream(transcriptPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var tail = new byte[Math.Min(tailBytes, fs.Length)];
            fs.Seek(-tail.Length, SeekOrigin.End);
            fs.ReadExactly(tail);

            // Края хвоста режут строки пополам — битые отсеиваются JsonException'ом,
            // идём с конца к первой целой записи с собственным uuid
            var lines = System.Text.Encoding.UTF8.GetString(tail).Split('\n');
            for (var i = lines.Length - 1; i >= 0; i--)
            {
                var line = lines[i].TrimEnd('\r');
                if (line.Length == 0) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    if (doc.RootElement.ValueKind != JsonValueKind.Object) continue;
                    if (!doc.RootElement.TryGetProperty("uuid", out var uuid)
                        || uuid.ValueKind != JsonValueKind.String) continue;
                    var value = uuid.GetString();
                    if (!string.IsNullOrEmpty(value)) return value;
                }
                catch (JsonException) { /* обрезанная/битая строка хвоста — идём дальше к целым */ }
            }
            return null;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[TranscriptProbe] Не удалось прочитать uuid хвоста транскрипта {transcriptPath}: {ex.Message}");
            return null;
        }
    }

    // Текст последнего user-сообщения транскрипта (content-строка; массивы блоков — вложения —
    // не сравнить со стартовым текстом хода, возвращаем null). null и при любой ошибке ФС —
    // вызывающий трактует как «текста нет» и не skip'ает submit (безопасная сторона).
    // Читается только хвост файла: транскрипты длинных сессий — десятки МБ.
    public static string? LastUserText(string? transcriptPath, int tailBytes = 256 * 1024)
    {
        if (transcriptPath is null) return null;
        try
        {
            using var fs = new FileStream(transcriptPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var tail = new byte[Math.Min(tailBytes, fs.Length)];
            fs.Seek(-tail.Length, SeekOrigin.End);
            fs.ReadExactly(tail);

            // Границы хвоста могут резать строки пополам: битые края отбрасываются
            // JsonException'ом при парсинге, целые строки идут с конца
            var lines = System.Text.Encoding.UTF8.GetString(tail).Split('\n');
            for (var i = lines.Length - 1; i >= 0; i--)
            {
                var line = lines[i].TrimEnd('\r');
                if (line.Length == 0) continue;
                // Быстрый отсев без парсинга всей строки
                if (!line.Contains("\"type\":\"user\"", StringComparison.Ordinal)
                    && !line.Contains("\"type\": \"user\"", StringComparison.Ordinal)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (!root.TryGetProperty("message", out var msg)) continue;
                    // Служебные вставки CLI (isMeta «Continue…», task-notification) — не стартовые
                    // тексты ходов: проскакиваем, durability текста хода они не отменяют
                    if (root.TryGetProperty("isMeta", out var meta) && meta.ValueKind == JsonValueKind.True) continue;
                    if (root.TryGetProperty("origin", out var origin)
                        && origin.ValueKind == JsonValueKind.Object
                        && origin.TryGetProperty("kind", out var ok)
                        && ok.GetString() == "task-notification") continue;
                    if (!msg.TryGetProperty("role", out var role) || role.GetString() != "user") continue;
                    // Content-массив (вложения) — чужой пользовательский ход: сравнить со строкой
                    // хода нельзя, durability недоказуема — НЕ ищем дальше (safe: без skip)
                    if (!msg.TryGetProperty("content", out var content)) return null;
                    return content.ValueKind == JsonValueKind.String ? content.GetString() : null;
                }
                catch (JsonException) { /* обрезанная/битая строка хвоста — идём дальше к целым */ }
            }
            return null;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[TranscriptProbe] Не удалось прочитать хвост транскрипта {transcriptPath}: {ex.Message}");
            return null;
        }
    }
}
