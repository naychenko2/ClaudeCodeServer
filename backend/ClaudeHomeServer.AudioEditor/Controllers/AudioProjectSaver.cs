using System.Text.RegularExpressions;
using ClaudeHomeServer.Services.AudioEditor.Jobs;
using ClaudeHomeServer.Services.AudioEditor.Threads;
using ClaudeHomeServer.Services.Media;

namespace ClaudeHomeServer.Services.AudioEditor.Controllers;

// Запись версии звука в проект (ADR-021 §2, §6): только новыми файлами (FileMode.CreateNew), флага
// перезаписи нет по построению. Версия — набор файлов, и пишется она целиком под одним именем:
// основной звук — <имя>.mp3, партитура, субтитры, слова, текст, MIDI, .pth и .index — рядом с тем же
// именем и своим расширением, стемы — папкой <имя>.stems/ (vocals.mp3, drums.mp3…). Имя группы
// свободно, только если свободны ВСЕ её файлы и папка: иначе «следующая версия» берёт следующий
// номер, а «Сохранить как» отвечает NameTaken с подсказкой.
//
// «Следующая версия» — рядом с исходником: intro.mp3 → intro.v2.mp3, intro.v2 → intro.v3; у
// черновика — имя из запроса (или «audio») в папке черновика, первым без номера. «Сохранить как» —
// ровно выбранное имя в выбранной папке, папку не создаём. Пути — только ProjectLinkGuard.ResolveInside.
public static class AudioProjectSaver
{
    public const string NextVersion = "nextVersion";
    public const string As = "as";

    private static readonly char[] ForbiddenChars = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];
    private static readonly Regex VersionSuffix = new(@"^(.+)\.v\d+$", RegexOptions.Compiled);
    private const string DefaultStem = "audio";
    private const string StemsSuffix = ".stems";
    private const int MaxStemLength = 200;
    private const int MaxNumber = 10_000;

    // Source — абсолютный путь файла версии, уже проверенный вызывающим
    public sealed record Item(string Role, string Source);

    public sealed record Saved(string Path, IReadOnlyList<string> Files);

    public sealed record Outcome(Saved? Value, string? ErrorCode, string? Error, string? Suggestion = null);

    // folder и fileName: у «Сохранить как» — выбор человека; у «следующей версии» fileName — имя черновика
    public static Outcome Save(string root, AudioThread thread, IReadOnlyList<Item> items, string mode,
        string? folder, string? fileName)
    {
        if (items.Count == 0) return Invalid("У версии нет файлов");
        var primary = items.FirstOrDefault(i => i.Role == AudioFileRoles.Main) ?? items[0];

        string dirRel;
        string stem;
        if (mode == As)
        {
            if (ValidateName(fileName, Path.GetExtension(primary.Source), out stem) is { } nameError) return Invalid(nameError);
            var rel = folder ?? DefaultFolder(thread);
            if (ResolveFolder(root, rel, out dirRel) is { } folderError) return Invalid(folderError);
            var target = Plan(root, dirRel, stem, items);
            if (target.Any(t => Exists(t.Full)))
                return new Outcome(null, AudioEditErrorCodes.NameTaken, $"Файл {Rel(dirRel, stem + Ext(primary))} уже есть",
                    SuggestFree(root, dirRel, BaseStem(stem), items, from: 2));
            return Write(root, dirRel, stem, items, target);
        }

        if (ResolveFolder(root, DefaultFolder(thread), out dirRel) is { } error) return Invalid(error);
        int first;
        if (thread.File is { } file)
        {
            stem = BaseStem(Path.GetFileNameWithoutExtension(file));
            first = 2;
        }
        else
        {
            stem = DefaultStem;
            if (!string.IsNullOrWhiteSpace(fileName))
            {
                if (ValidateName(fileName, Path.GetExtension(primary.Source), out var named) is { } nameError) return Invalid(nameError);
                stem = BaseStem(named);
            }
            first = 1;
        }

        for (var n = first; n < MaxNumber; n++)
        {
            var candidate = n == 1 ? stem : $"{stem}.v{n}";
            var target = Plan(root, dirRel, candidate, items);
            if (target.Any(t => Exists(t.Full))) continue;
            var written = Write(root, dirRel, candidate, items, target);
            // Гонка двух сохранений: имя заняли между проверкой и записью — следующий номер
            if (written.ErrorCode != AudioEditErrorCodes.NameTaken) return written;
        }
        return Invalid("Не нашлось свободного имени");
    }

    private sealed record Target(Item? Item, string Full, string Rel, bool IsDir);

    // Раскладка группы: файлы рядом с именем, стемы — в папке; одинаковые расширения разводятся ролью
    private static List<Target> Plan(string root, string dirRel, string stem, IReadOnlyList<Item> items)
    {
        var dir = dirRel.Length == 0 ? root : Path.Combine(root, dirRel);
        var plan = new List<Target>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stems = items.Where(i => i.Role.StartsWith(AudioFileRoles.StemPrefix, StringComparison.Ordinal)).ToList();
        foreach (var item in items.Except(stems))
        {
            var name = stem + Ext(item);
            if (!used.Add(name)) name = $"{stem}.{item.Role}{Ext(item)}";
            used.Add(name);
            plan.Add(new Target(item, Path.Combine(dir, name), Rel(dirRel, name), false));
        }
        if (stems.Count > 0)
        {
            var folderName = stem + StemsSuffix;
            var folderFull = Path.Combine(dir, folderName);
            var folderRel = Rel(dirRel, folderName);
            plan.Add(new Target(null, folderFull, folderRel, true));
            foreach (var item in stems)
            {
                var name = item.Role[AudioFileRoles.StemPrefix.Length..] + Ext(item);
                plan.Add(new Target(item, Path.Combine(folderFull, name), $"{folderRel}/{name}", false));
            }
        }
        return plan;
    }

    // Всё или ничего: сбой посередине уносит уже записанное этой попыткой
    private static Outcome Write(string root, string dirRel, string stem, IReadOnlyList<Item> items, List<Target> plan)
    {
        var written = new List<string>();
        string? createdDir = null;
        try
        {
            foreach (var t in plan)
            {
                ProjectLinkGuard.EnsureNoLink(root, t.Full);
                if (t.IsDir)
                {
                    if (Exists(t.Full)) throw new IOException("Папка стемов уже есть");
                    Directory.CreateDirectory(t.Full);
                    createdDir = t.Full;
                    continue;
                }
                using (var src = new FileStream(t.Item!.Source, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var dst = new FileStream(t.Full, FileMode.CreateNew, FileAccess.Write))
                    src.CopyTo(dst);
                written.Add(t.Full);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            foreach (var path in written) TryDelete(path);
            if (createdDir is not null) TryDeleteDir(createdDir);
            if (ex is UnauthorizedAccessException) return Invalid("Путь вне папки проекта или идёт через символическую ссылку");
            if (plan.Any(t => !t.IsDir && t.Item is not null && !File.Exists(t.Item.Source)))
                return new Outcome(null, AudioEditErrorCodes.FileNotFound, "Файл версии больше не найден");
            return new Outcome(null, AudioEditErrorCodes.NameTaken, "Имя уже занято",
                SuggestFree(root, dirRel, BaseStem(stem), items, from: 2));
        }

        var files = plan.Where(t => !t.IsDir).Select(t => t.Rel).ToList();
        // Основной путь ответа — звук; у версии из одних стемов — папка стемов
        var primary = plan.FirstOrDefault(t => t.Item?.Role == AudioFileRoles.Main) ?? plan[0];
        return new Outcome(new Saved(primary.Rel, files), null, null);
    }

    private static string? SuggestFree(string root, string dirRel, string stem, IReadOnlyList<Item> items, int from)
    {
        for (var n = from; n < MaxNumber; n++)
        {
            var candidate = $"{stem}.v{n}";
            var plan = Plan(root, dirRel, candidate, items);
            if (!plan.Any(t => Exists(t.Full)))
                return (plan.FirstOrDefault(t => t.Item?.Role == AudioFileRoles.Main) ?? plan[0]).Rel;
        }
        return null;
    }

    private static string DefaultFolder(AudioThread thread) =>
        thread.File is { } file ? ParentOf(file) : thread.DraftFolder ?? "";

    private static string ParentOf(string rel)
    {
        var i = rel.Replace('\\', '/').LastIndexOf('/');
        return i < 0 ? "" : rel[..i];
    }

    // Папка обязана быть, лежать внутри корня и не идти через ссылку; "" — корень
    private static string? ResolveFolder(string root, string? folder, out string dirRel)
    {
        dirRel = "";
        var rel = (folder ?? "").Trim().Replace('\\', '/').TrimEnd('/');
        if (rel.Length == 0 || rel == ".")
            return Directory.Exists(root) ? null : "Папка не найдена";
        if (ProjectLinkGuard.ResolveInside(root, rel) is not { } full)
            return "Путь вне папки проекта или идёт через символическую ссылку";
        if (!Directory.Exists(full)) return "Папка не найдена";
        dirRel = Path.GetRelativePath(root, full).Replace('\\', '/');
        if (dirRel == ".") dirRel = "";
        return null;
    }

    // null — имя годится. Имя обязано быть именем, а не путём; вписанное расширение основного файла
    // срезается — его ставит сервер
    private static string? ValidateName(string? input, string ext, out string stem)
    {
        stem = "";
        var name = (input ?? "").Trim();
        if (name.Length == 0) return "Не указано имя файла";
        if (Path.GetFileName(name) != name || name.IndexOfAny(ForbiddenChars) >= 0 || name.Any(char.IsControl))
            return "Имя файла — без папок и символов < > : \" / \\ | ? *";
        if (name.StartsWith('.')) return "Имя файла не может начинаться с точки";
        if (ext.Length > 1 && name.Length > ext.Length && name.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
            name = name[..^ext.Length].TrimEnd();
        if (name.Length == 0 || name.EndsWith('.')) return "Недопустимое имя файла";
        if (name.Length > MaxStemLength) return $"Имя файла длиннее {MaxStemLength} символов";
        stem = name;
        return null;
    }

    private static string BaseStem(string stem)
    {
        var m = VersionSuffix.Match(stem);
        return m.Success ? m.Groups[1].Value : stem;
    }

    private static string Ext(Item item) => Path.GetExtension(item.Source).ToLowerInvariant();

    private static string Rel(string dirRel, string name) => dirRel.Length == 0 ? name : $"{dirRel}/{name}";

    // Висячая ссылка не Exists, но имя занято — CreateNew на ней тоже откажет
    private static bool Exists(string full) =>
        File.Exists(full) || Directory.Exists(full) || new FileInfo(full).LinkTarget is not null;

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static void TryDeleteDir(string path)
    {
        try { Directory.Delete(path, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static Outcome Invalid(string error) => new(null, AudioEditErrorCodes.InvalidRequest, error);
}
