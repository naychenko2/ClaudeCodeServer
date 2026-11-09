using ClaudeHomeServer.Services;

namespace ClaudeHomeServer.Services.Notes.Wiki;

// Атомарная запись файла вики (ADR-035, решение 4, пункт 5): temp-файл в той же папке
// плюс File.Move(overwrite: true). Целевой файл — либо прежнее содержимое, либо новое
// целиком: исключение на любом этапе оставляет цель как было и не оставляет temp-мусора.
// Каталог создаётся при необходимости — вызывающий не обязан готовить хранилище.
// Пути склеивает только SafePath.Join: относительный путь за пределы корня — отказ,
// своей защиты путей здесь нет.
public static class WikiFileWriter
{
    /// <summary>Записать содержимое атомарно. Отдаёт то же исключение, что отдала ФС;
    /// цель нетронутой и отсутствие *.tmp гарантированы в любом исходе.</summary>
    public static void Write(string root, string relPath, string content)
    {
        var full = SafePath.Join(root, relPath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        // Имя temp уникально на каждый вызов (ревью S2b-2, MAJOR): постоянное имя сталкивает
        // одновременных писателей одного файла — второй Move падает, а его уборка сносит temp
        // победителя. Уникальное имя снимает весь класс коллизий: и между операциями вне
        // блокировки раздела, и между процессами. Остаток от упавшего процесса по-прежнему
        // виден как мусор *.tmp и удаляется по маске.
        var temp = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, content);
            File.Move(temp, full, overwrite: true);
        }
        catch
        {
            // Отказ на записи temp или на Move: цель остаётся как была, temp не остаётся.
            // Ошибка уборки не подменяет исходную причину — исходная идёт дальше.
            try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    /// <summary>Прочитать файл. null — файла нет: отсутствие страницы или манифеста —
    /// нормальное состояние хранилища до первого применения.</summary>
    public static string? Read(string root, string relPath)
    {
        var full = SafePath.Join(root, relPath);
        return File.Exists(full) ? File.ReadAllText(full) : null;
    }
}
