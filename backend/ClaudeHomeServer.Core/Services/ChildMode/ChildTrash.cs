using System.Globalization;
using System.Text.Json;

namespace ClaudeHomeServer.Services.ChildMode;

/// <summary>
/// Корзина ребёнка (ADR-041 ред. 5, решение 54): прежняя версия файла перед Write/Edit
/// репетитора и перед удалением ребёнка рельсой живёт в «.ccs-trash/» папки предмета,
/// хранение — 30 дней. Запись: «.ccs-trash/{yyyyMMddHHmmssfff}-{guid8}/{relPath}» +
/// meta.json (исходный путь, время, кто). Время метки — UTC: имя записи и сравнение
/// с «now» Sweep'а в одной шкале, часовой пояс сервера в них не участвует.
/// Блокировок по файлу нет: два снимка одного файла — две записи с разными id, имена
/// уникальны по метке времени и guid-суффиксу (доказано тестом).
/// «Дешёвый» Sweep при каждом снимке: отдельного таймера нет, просроченное сносится
/// следующим снимком; сам Sweep не бросает наружу — чистка не может отменить операцию,
/// которая её позвала.
/// </summary>
public static class ChildTrash
{
    /// <summary>Имя корзины в папке предмета. Сегмент с префиксом «.ccs-» защищён
    /// ChildPathPolicy: репетитор и ребёнок не пишут в неё напрямую, рельса не показывает.</summary>
    public const string TrashDir = ".ccs-trash";

    /// <summary>Срок хранения записи; старше — удаляет Sweep.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(30);

    private const string StampFormat = "yyyyMMddHHmmssfff";
    private static readonly JsonSerializerOptions MetaJsonOptions = new() { WriteIndented = true };

    /// <summary>
    /// Снимок прежней версии. relPath — путь относительно папки предмета (его уже судил
    /// ChildPathPolicy вызывающего; за пределы предмета SafePath.Join отказывает и здесь).
    /// Файла нет — записи нет и null: нового файла защищать нечем.
    /// Сбой записи — IOException наружу: нет снимка — нет перезаписи, вызывающий обязан
    /// отказать разрешению (так делает точка решения в ClaudeSession).
    /// Возвращает id записи.
    /// </summary>
    public static string? Snapshot(string subjectRoot, string relPath, string? actor = null)
    {
        var source = SafePath.Join(subjectRoot, relPath);
        if (!File.Exists(source)) return null;

        var at = DateTimeOffset.UtcNow;
        var id = NewId(at);
        var entryDir = SafePath.Join(subjectRoot, TrashDir + "/" + id);
        Directory.CreateDirectory(entryDir);
        var stored = SafePath.Join(entryDir, relPath);
        Directory.CreateDirectory(Path.GetDirectoryName(stored)!);
        File.Copy(source, stored);
        File.WriteAllText(SafePath.Join(entryDir, "meta.json"),
            JsonSerializer.Serialize(new ChildTrashMeta(relPath, at, actor), MetaJsonOptions));

        Sweep(subjectRoot, at);
        return id;
    }

    /// <summary>Записи корзины по возрастанию id (он начинается с метки времени — порядок
    /// хронологический). Каталоги без meta.json — не записи, их нет в списке.</summary>
    public static IReadOnlyList<ChildTrashEntry> List(string subjectRoot)
    {
        var trashRoot = SafePath.Join(subjectRoot, TrashDir);
        if (!Directory.Exists(trashRoot)) return [];
        var entries = new List<ChildTrashEntry>();
        foreach (var dir in Directory.GetDirectories(trashRoot))
        {
            if (TryReadEntry(trashRoot, dir) is not { } entry) continue;
            entries.Add(entry);
        }
        entries.Sort(static (a, b) => string.CompareOrdinal(a.Id, b.Id));
        return entries;
    }

    /// <summary>
    /// Вернуть содержимое записи на её исходный путь. Записи нет (нет и исходного пути) —
    /// false: «Restore во время Sweep'а той же записи» — либо успех, либо NotFound, без
    /// исключения наружу (и метка, и содержимое могут снест другой проход — тот же NotFound).
    /// На месте уже лежит другой файл — он сам уходит в корзину (снимок до перезаписи),
    /// потом восстановление: ничего не теряется.
    /// </summary>
    public static bool Restore(string subjectRoot, string entryId)
    {
        // id записи — одно имя в корзине: с «/», «\», «.» или пустой — не запись этой корзины.
        if (string.IsNullOrEmpty(entryId) || Path.GetFileName(entryId) != entryId) return false;
        var trashRoot = SafePath.Join(subjectRoot, TrashDir);
        string entryDir;
        try { entryDir = SafePath.Join(trashRoot, entryId); }
        catch (UnauthorizedAccessException) { return false; } // id с «..» — не запись этой корзины
        if (TryReadEntry(trashRoot, entryDir) is not { } entry) return false;

        var target = SafePath.Join(subjectRoot, entry.OriginalPath);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (File.Exists(target))
        {
            try
            {
                // снимок того, что на месте, — от того же, кто спросил исходный (meta.Actor);
                // сбой снимка — та же логика, что у вызывающего Write: нет снимка — нет перезаписи.
                if (Snapshot(subjectRoot, entry.OriginalPath, entry.Actor) is null)
                    return false; // гонка: цель исчезла с глаз до меты — тот же NotFound, без исключения
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // сбой снимка (диск или права) — нет снимка — нет перезаписи: наружу false,
                // тот же исход, что у NotFound по гонке.
                return false;
            }
        }
        try
        {
            File.Copy(SafePath.Join(entryDir, entry.OriginalPath), target, overwrite: true);
        }
        catch (IOException) { return false; } // запись снесена во время копия — Sweep'ом или другим Restore
        return true;
    }

    /// <summary>
    /// Снести записи старше Retention (сравнение — по метке в id, её не надо читать с диска).
    /// Наружу не бросает ничего: ни свой IO (каталога нет, записи не разобрать), ни чужой
    /// (неудаляемый из-за прав каталог) — ноль удалённых или что успели. Инвариант: чистка
    /// корзины не отменяет снимок, который её позвал; один неудаляемый просроченный каталог
    /// не может навсегда запретить репетитору Write/Edit в предмете.
    /// </summary>
    public static int Sweep(string subjectRoot, DateTimeOffset now)
    {
        var trashRoot = SafePath.Join(subjectRoot, TrashDir);
        if (!Directory.Exists(trashRoot)) return 0;
        var removed = 0;
        string[] dirs;
        try { dirs = Directory.GetDirectories(trashRoot); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return 0; // корзину не перечислить (нет прав на чтение) — чистка пропускается, не отменяет снимок
        }
        foreach (var dir in dirs)
        {
            var name = Path.GetFileName(dir);
            if (!TryParseStamp(name, out var at)) continue;
            if (now - at <= Retention) continue; // ровно 30 дней — ещё жива (half-open: старше — смерть)
            try { Directory.Delete(dir, recursive: true); removed++; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // гонка с другим проходом или чужой каталог без прав на удаление:
                // проходим мимо, остальные записи чистим как умели.
            }
        }
        return removed;
    }

    private static string NewId(DateTimeOffset at) =>
        at.ToUniversalTime().ToString(StampFormat, CultureInfo.InvariantCulture) +
        "-" + Guid.NewGuid().ToString("N")[..8];

    private static bool TryParseStamp(string entryName, out DateTimeOffset at)
    {
        var dash = entryName.IndexOf('-');
        if (dash <= 0)
        {
            at = default;
            return false;
        }
        return DateTimeOffset.TryParseExact(entryName[..dash], StampFormat, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out at);
    }

    private static ChildTrashEntry? TryReadEntry(string trashRoot, string entryDir)
    {
        var id = Path.GetFileName(entryDir);
        if (id.Length == 0 || !Directory.Exists(entryDir)) return null;
        var metaPath = Path.Combine(trashRoot, id, "meta.json");
        if (!File.Exists(metaPath)) return null;
        try
        {
            var meta = JsonSerializer.Deserialize<ChildTrashMeta>(File.ReadAllText(metaPath));
            if (meta is null || string.IsNullOrEmpty(meta.Path)) return null;
            var stored = SafePath.Join(entryDir, meta.Path);
            if (!File.Exists(stored)) return null;
            return new ChildTrashEntry(id, meta.Path, meta.At, meta.Actor);
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return null; // битая мета — не запись; List не чинит чужое и не бросает
        }
    }
}

/// <summary>Запись корзины: id (он же имя каталога), исходный путь относительно папки
/// предмета, время снимка (UTC), кто спросил снимок (ownerId ребёнка).</summary>
public sealed record ChildTrashEntry(string Id, string OriginalPath, DateTimeOffset At, string? Actor);

/// <summary>Содержимое meta.json записи: исходный путь, время, кто.</summary>
internal sealed record ChildTrashMeta(string Path, DateTimeOffset At, string? Actor);
