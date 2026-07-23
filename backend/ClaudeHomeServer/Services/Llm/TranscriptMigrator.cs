namespace ClaudeHomeServer.Services.Llm;

// Перенос транскрипта claude CLI между изолированными профилями (CLAUDE_CONFIG_DIR).
// Транскрипт сессии — локальный JSONL {профиль}/projects/{уплощённый cwd}/{sessionId}.jsonl
// (рядом может лежать папка {sessionId}/ с транскриптами сабагентов). Сам API stateless,
// поэтому «переезд» чата на другой аккаунт пула или сторонний эндпоинт — это копирование
// этих файлов в целевой профиль: --resume найдёт сессию там и продолжит разговор.
public static class TranscriptMigrator
{
    // Уплощение cwd по соглашению CLI: не-алфавитно-цифровые символы → '-'
    // (то же, что в SubagentStreamWatcher.ResolveDir)
    public static string FlattenCwd(string cwd) =>
        string.Concat(cwd.Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-'));

    // Найти файл транскрипта сессии в профиле: сначала по соглашению об уплощении cwd,
    // затем фолбэк-скан всех папок projects (раскладка старых версий CLI может отличаться)
    public static string? FindTranscript(string configRoot, string cwd, string claudeSessionId)
    {
        var projects = Path.Combine(configRoot, "projects");
        if (!Directory.Exists(projects)) return null;

        var byConvention = Path.Combine(projects, FlattenCwd(cwd), claudeSessionId + ".jsonl");
        if (File.Exists(byConvention)) return byConvention;

        foreach (var dir in Directory.GetDirectories(projects))
        {
            var candidate = Path.Combine(dir, claudeSessionId + ".jsonl");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    // Скопировать транскрипт (и папку сабагентов, best-effort) в целевой профиль.
    // false с причиной — транскрипт не найден или копирование не удалось; вызывающий
    // в этом случае НЕ меняет провайдера, иначе --resume молча начал бы разговор с нуля.
    public static bool TryMigrate(string srcRoot, string dstRoot, string cwd,
        string claudeSessionId, out string? error)
    {
        error = null;
        try
        {
            var src = FindTranscript(srcRoot, cwd, claudeSessionId);
            if (src is null)
            {
                error = $"транскрипт {claudeSessionId} не найден в {srcRoot}";
                return false;
            }

            // Имя папки проекта берём у источника (а не пересчитываем): если транскрипт
            // нашёлся фолбэк-сканом, у CLI этой версии своё соглашение об уплощении —
            // сохранённое имя гарантированно резолвится и в целевом профиле
            var dstDir = Path.Combine(dstRoot, "projects", Path.GetFileName(Path.GetDirectoryName(src))!);
            Directory.CreateDirectory(dstDir);
            File.Copy(src, Path.Combine(dstDir, claudeSessionId + ".jsonl"), overwrite: true);

            var srcSessionDir = Path.Combine(Path.GetDirectoryName(src)!, claudeSessionId);
            if (Directory.Exists(srcSessionDir))
                CopyDirectory(srcSessionDir, Path.Combine(dstDir, claudeSessionId));
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    // Переезд транскрипта между РАБОЧИМИ ПАПКАМИ в рамках одного профиля (worktree чата):
    // CLI ищет транскрипт в projects/{уплощённый cwd}, поэтому смена cwd без копии рвёт
    // --resume. Целевая папка считается от НОВОГО cwd (в отличие от TryMigrate, где она
    // намеренно берётся от источника). Исходник не удаляем — обратный переезд дешевле.
    public static bool TryRelocateCwd(string configRoot, string oldCwd, string newCwd,
        string claudeSessionId, out string? error)
    {
        error = null;
        try
        {
            var src = FindTranscript(configRoot, oldCwd, claudeSessionId);
            if (src is null)
            {
                error = $"транскрипт {claudeSessionId} не найден в {configRoot}";
                return false;
            }

            var dstDir = Path.Combine(configRoot, "projects", FlattenCwd(newCwd));
            Directory.CreateDirectory(dstDir);
            File.Copy(src, Path.Combine(dstDir, claudeSessionId + ".jsonl"), overwrite: true);

            var srcSessionDir = Path.Combine(Path.GetDirectoryName(src)!, claudeSessionId);
            if (Directory.Exists(srcSessionDir))
                CopyDirectory(srcSessionDir, Path.Combine(dstDir, claudeSessionId));
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    // Папка сессии (сабагенты и пр.) — без неё resume работает, поэтому ошибки глотаем
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
            Console.Error.WriteLine($"[TranscriptMigrator] Папка сессии не скопирована ({src}): {ex.Message}");
        }
    }
}
