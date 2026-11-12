using System.Globalization;
using System.Text.Json;
using ClaudeHomeServer.Services;

namespace ClaudeHomeServer.Services.Notes.Wiki;

// keeper/backlog-until.json — явный флаг бэклога разбора вики (разрез 2026-11-12, J-P1c).
// Чистка старой вики (J-P1a) ставит дату, до которой ночной проход считается проходном
// бэклога: лимит новых страниц на раздел поднимается (WikiApplyPolicy.
// MaxNewKnowledgePagesPerSectionBacklog). Флаг держится ровно отмеренный срок и молча
// истекает: не было чистки — нет и бэклога, обычный режим — состояние по умолчанию.
//
// Файл — не под pages/ и не файл раздела: он владельца, как keeper/settings.json. Писатель
// один — код вертикали. Запись атомарна (temp+fsync→Move, тот же писатель, что у настроек
// и reads.json): половина файла флага притворилась бы половиной флага, и ночь решила бы
// по ней как по целому. Чтение — без замка: при атомарной замене читатель видит либо
// прежний файл, либо новый целиком (тот же порядок, что у WikiReadsStore).
//
// IsActive не бросает исключений: нет файла, мусор, файл не читается — false. Отсутствие
// флага значит «бэклога нет», а не «неизвестно»: поднять лимит может только реально
// сделанная чистка, а не испорченный файл.
public static class WikiBacklogFlag
{
    private const string FlagRelPath = "keeper/backlog-until.json";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly record struct FlagFile(string Until);

    /// <summary>Поставить флаг до указанной даты включительно (until — последний день
    /// флага). Атомарная запись (temp+fsync→Move), каталог keeper/ создаётся при нужде.
    /// Сбой записи — исключение вызывающему: чистка, ставящая флаг, должна знать,
    /// что флаг не поставлен, а не молча начать обычный проход.</summary>
    public static void Set(string root, DateOnly until)
    {
        var full = SafePath.Join(root, FlagRelPath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            new FlagFile(until.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)), JsonOpts);
        var temp = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write))
            {
                fs.Write(bytes);
                fs.Flush(true);
            }
            File.Move(temp, full, overwrite: true);
        }
        catch
        {
            try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    /// <summary>Активен ли флаг на эту дату: today ≤ until (граница — последний день
    /// флага). Нет файла, мусор, дата не читается, файл не открылся — false, исключения
    /// наружу нет: по нечитаемом флаге обычный проход с обычным лимитом.</summary>
    public static bool IsActive(string root, DateOnly today)
    {
        var full = SafePath.Join(root, FlagRelPath);
        try
        {
            if (!File.Exists(full))
                return false;
            // FileShare.Delete — тот, кто читает, не мешает атомарной замене: rename поверх
            // открытого на чтение файла разрешён только с ним (гонка «чтение во время записи»).
            using var fs = new FileStream(full, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var doc = JsonDocument.Parse(fs);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("until", out var until)
                && until.ValueKind == JsonValueKind.String
                && DateOnly.TryParseExact(until.GetString(), "yyyy-MM-dd",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var untilDate)
                && today <= untilDate;
        }
        catch (IOException) { return false; }
        catch (JsonException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
}
