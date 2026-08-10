using System.Text.RegularExpressions;

namespace ClaudeHomeServer.Services.Llm;

// Перенос транскрипта claude CLI между изолированными профилями (CLAUDE_CONFIG_DIR).
// Транскрипт сессии — локальный JSONL {профиль}/projects/{уплощённый cwd}/{sessionId}.jsonl
// (рядом может лежать папка {sessionId}/ с транскриптами сабагентов). Сам API stateless,
// поэтому «переезд» чата на другой аккаунт пула или сторонний эндпоинт — это копирование
// этих файлов в целевой профиль: --resume найдёт сессию там и продолжит разговор.
public static class TranscriptMigrator
{
    // Плоский идентификатор: uuid от CLI (17d2c868-5897-4c32-9019-cf5c6912fc9b) и ничего,
    // что может увести путь в сторону. Именно белый список, а не запрет разделителей:
    // Path.GetFileName пропускает «.» и «..» (в них разделителей нет), а из них
    // Path.Combine собирает саму папку профиля и ее родителя — и рекурсивное удаление
    // папки сессии снесло бы транскрипты всех инстансов и интерактивных сессий разом.
    private static readonly Regex SafeSessionId = new(@"^[A-Za-z0-9_-]{1,128}$", RegexOptions.Compiled);

    // Годится ли значение как имя файла/папки транскрипта. Проверять обязательно: в
    // sessions.json ключ попадает не только от CLI, но и снаружи — параметром
    // resumeSessionId в POST /sessions|/chats|/personas/{id}/chats, а сам файл еще и
    // правится руками при восстановлении чатов.
    public static bool IsSafeSessionId(string? claudeSessionId) =>
        claudeSessionId is not null && SafeSessionId.IsMatch(claudeSessionId);

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

    // Все копии транскрипта сессии в перечисленных профилях. В отличие от FindTranscript
    // не останавливается на первой находке: копии остаются намеренно — TryMigrate (смена
    // провайдера) и TryRelocateCwd (переезд в worktree) исходники не удаляют. cwd = null
    // допустим (рабочую папку определить не удалось) — тогда работает только фолбэк-скан.
    public static IReadOnlyList<string> FindAllTranscripts(
        IEnumerable<string> configRoots, string? cwd, string claudeSessionId)
    {
        // Полагаться на происхождение ключа нельзя — ниже по нему удаляются файлы и папки
        if (!IsSafeSessionId(claudeSessionId)) return [];

        var found = new List<string>();
        void Add(string path)
        {
            // Один и тот же файл может прийти дважды: корни в списке способны повторяться
            // (профиль подписки и ~/.claude указывают на одну папку), а найденное по
            // соглашению повторно попадается фолбэк-скану
            if (!found.Contains(path, StringComparer.OrdinalIgnoreCase)) found.Add(path);
        }

        foreach (var root in configRoots)
        {
            // Профиль на отключенном сетевом диске или без прав не должен отменять уборку в
            // остальных — ловим на каждом корне отдельно, а не одним try на весь обход
            try
            {
                var projects = Path.Combine(root, "projects");
                if (!Directory.Exists(projects)) continue;

                if (cwd is not null)
                {
                    var byConvention = Path.Combine(projects, FlattenCwd(cwd), claudeSessionId + ".jsonl");
                    if (File.Exists(byConvention)) Add(byConvention);
                }
                // Фолбэк-скан: раскладки других версий CLI и копии, осевшие после переездов
                // cwd (TryMigrate/TryRelocateCwd исходники не удаляют). Отдельно он важен для
                // container-пользователей: CLI уплощает КОНТЕЙНЕРНЫЙ cwd (/projects/…), а сюда
                // от DeleteTranscript приходит хостовый (C:\…) — путь «по соглашению» там не
                // сходится в принципе, и файл находит именно скан
                foreach (var dir in Directory.GetDirectories(projects))
                {
                    var candidate = Path.Combine(dir, claudeSessionId + ".jsonl");
                    if (File.Exists(candidate)) Add(candidate);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[TranscriptMigrator] Профиль {root} не просмотрен: {ex.Message}");
            }
        }
        return found;
    }

    // Удалить транскрипт сессии (и папку ее сабагентов) из всех перечисленных профилей;
    // возвращает число удаленных файлов. Зовется при удалении чата — иначе переписка живет
    // на диске до плановой уборки CLI (~30 дней), хотя чат пользователь уже удалил.
    //
    // ВАЖНО: удаляется ТОЛЬКО файл с точным именем {claudeSessionId}.jsonl — никогда по
    // маске, никогда по времени и никогда сама папка проекта. Один ~/.claude делят между
    // собой несколько инстансов сервера (dev/прод/контейнер) и интерактивные сессии
    // пользователя, поэтому «вычистить лишнее из папки» снесло бы чужую историю. Uuid же
    // принадлежит ровно одному чату, и промахнуться им невозможно.
    //
    // Ошибки ФС глотаем: уборка не должна ронять удаление чата (файл может быть занят
    // антивирусом, профиль — лежать на отключенном диске).
    public static int DeleteEverywhere(
        IEnumerable<string> configRoots, string? cwd, string claudeSessionId)
    {
        IReadOnlyList<string> targets;
        try { targets = FindAllTranscripts(configRoots, cwd, claudeSessionId); }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"[TranscriptMigrator] Поиск транскриптов не удался ({claudeSessionId}): {ex.Message}");
            return 0;
        }

        var deleted = 0;
        foreach (var file in targets)
        {
            try
            {
                File.Delete(file);
                deleted++;
                // Рядом с транскриптом лежит одноименная папка с транскриптами сабагентов
                var sessionDir = Path.Combine(Path.GetDirectoryName(file)!, claudeSessionId);
                if (Directory.Exists(sessionDir)) Directory.Delete(sessionDir, recursive: true);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[TranscriptMigrator] Транскрипт не удален ({file}): {ex.Message}");
            }
        }
        return deleted;
    }

    // Скопировать транскрипт (и папку сабагентов, best-effort) в целевой профиль.
    // false с причиной — транскрипт не найден или копирование не удалось; вызывающий
    // в этом случае НЕ меняет провайдера, иначе --resume молча начал бы разговор с нуля.
    //
    // preserveLongerDestination — страховка для ОБРАТНОГО переноса (restore после смены типа
    // поставщика): прямой перенос — это Copy, исходник не удаляется, поэтому в профиле исходного
    // провайдера остаётся полная история. Если к моменту обратного переноса источник (профиль
    // подмены) оказался КОРОЧЕ приёмника (гонка «result записался в профиль раньше финального
    // флаша CLI», либо профиль подмены пуст/повреждён), безусловный overwrite затёр бы полную
    // историю усечённой. При true — пропускаем копию, если приёмник длиннее источника, и
    // возвращаем true (приёмник уже полнее, цели достигнуты), причину — в error для лога.
    public static bool TryMigrate(string srcRoot, string dstRoot, string cwd,
        string claudeSessionId, out string? error, bool preserveLongerDestination = false)
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
            var dstFile = Path.Combine(dstDir, claudeSessionId + ".jsonl");
            // Страховка от усечения (Minor 1): при обратном переносе не затираем более полную
            // историю приёмника усечённым/повреждённым источником. Сравнение по длине файла —
            // достаточно и дёшево; свежий ответ хода делает источник длиннее, так что штатный
            // обратный перенос (источник с новым ответом длиннее старого приёмника) проходит.
            if (preserveLongerDestination && File.Exists(dstFile))
            {
                var dstLen = new FileInfo(dstFile).Length;
                var srcLen = new FileInfo(src).Length;
                if (dstLen > srcLen)
                {
                    error = $"пропущено: приёмник длиннее источника ({dstLen} > {srcLen} байт) — сохраняем полную историю";
                    return true;
                }
            }
            CopyFileShared(src, dstFile);

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
            CopyFileShared(src, Path.Combine(dstDir, claudeSessionId + ".jsonl"));

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

    // Копирование файла транскрипта в обход эксклюзивного захвата источника. Живой или умирающий
    // процесс CLI держит активный .jsonl на запись (durable-очередь queue-operation), и File.Copy
    // падал «The process cannot access the file … because it is being used by another process» —
    // кандидат миграции пропускался, подмена провайдера срывалась, а на --resume в новом профиле
    // разговор начинался с нуля (инцидент 2026-08-10). FileShare.ReadWrite открывает источник
    // параллельно с записью CLI (если он делится чтением — копия мгновенна); иначе — короткий
    // ретрай: процесс при миграции уже умирающий/убитый и вот-вот отпустит файл. Копия может
    // отставать на недописанный хвост текущей строки, но полной на момент старта истории хватает
    // для resume.
    private static void CopyFileShared(string src, string dst)
    {
        const int bufferSize = 81920;
        const int maxAttempts = 10;
        const int stepMs = 150;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var srcStream = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize);
                using var dstStream = new FileStream(dst, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize);
                srcStream.CopyTo(dstStream);
                return;
            }
            // Занят — подождём отпускания и повторим. Файл отсутствует или недоступен по иной
            // причине — не ретраим: ошибка уйдёт наверх, TryMigrate вернёт false с причиной.
            catch (IOException) when (attempt < maxAttempts)
            {
                Thread.Sleep(stepMs);
            }
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
                CopyFileShared(file, target);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[TranscriptMigrator] Папка сессии не скопирована ({src}): {ex.Message}");
        }
    }
}
