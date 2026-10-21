using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;

namespace ClaudeHomeServer.Services.Docs;

// Каталог макетов фичи (волна 3, шаг Б-mk-a). Читает записки docs/mockups/*.md через корпус
// DocsIndexService (ФИКСИРОВАННАЯ область, а не настройка проекта — её владелец может сузить,
// и макеты пропали бы) и собирает:
//   • макеты по шапке записки: «**Фича:** <guid> … **Статус:** … **Файлы:** …»;
//   • запасной путь — linkedFiles задач фичи (только .html под docs/mockups/).
// Шапка главнее: linkedFiles, уже покрытый шапкой (как путь записки или в её «Файлы»),
// не дублируется.
//
// Зависимостей от других вертикалей нет: только DocsIndexService и DocProperties (та же
// вертикаль) и спина (SafePath, модели Core). Регистрация — тем же путём, что DocsIndexService.
// Не sealed + virtual Find: тест Main подменяет каталог заглушкой, что считает вызовы
// (проверка, что для локального проекта Docs не дёргается — сторож G10).
public class MockupCatalog(DocsIndexService docs)
{
    // Фиксированная область: docs/mockups + markdown. Отдельный слот кеша DocsIndexService —
    // он ключуется корнем + областью, поэтому не мешает и не дёргает область проекта.
    private static readonly DocsScope MockupsScope = new(["docs/mockups"], [], ["markdown"]);

    // Префикс, под который обязаны лежать все файлы макета (и шапки, и linkedFiles)
    private const string MockupsPrefix = "docs/mockups/";

    public virtual IReadOnlyList<MockupEntry> Find(string rootPath, string featureId,
        IReadOnlyCollection<string> linkedPaths)
    {
        var corpus = docs.GetCorpus(rootPath, MockupsScope);

        var entries = new List<MockupEntry>();
        // Пути, уже покрытые шапкой (записка + её файлы): linkedFiles их не дублирует
        var covered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var doc in corpus.Docs)
        {
            if (doc.Binary) continue;
            if (!corpus.Texts.TryGetValue(doc.Path, out var text)) continue;

            // Разбор шапки — только DocProperties.Parse, своего парсера нет
            var parsed = DocProperties.Parse(text);
            if (parsed.Items.Count == 0) continue;                 // шапки нет — не макет

            var feature = FirstValue(parsed, "Фича");
            if (feature is null || !FeatureMatches(feature, featureId)) continue;

            var files = ParseFiles(rootPath, doc.Path, FirstValue(parsed, "Файлы"));

            entries.Add(new MockupEntry(
                Path: doc.Path,
                Title: doc.Title,
                Status: ParseStatus(FirstValue(parsed, "Статус")),
                Files: files,
                Source: MockupSource.Header,
                ModifiedAt: doc.Modified));

            covered.Add(doc.Path);
            foreach (var f in files) covered.Add(f.Path);
        }

        // Запасной путь: linkedFiles задач фичи. Только .html под docs/mockups/;
        // уже покрытые шапкой пропускаются; несуществующий на диске — «не в master».
        // Симлинк на цель ВНЕ docs/mockups/ → записи нет вовсе (exists/mtime цели не утекают).
        foreach (var linked in DistinctLinkedHtml(linkedPaths))
        {
            if (covered.Contains(linked)) continue;
            var info = ResolveOnDiskInfo(rootPath, linked);
            if (info.EscapesArea) continue;
            entries.Add(new MockupEntry(
                Path: linked,
                Title: Path.GetFileNameWithoutExtension(linked),
                Status: null,
                Files: [new MockupFile(linked, info.Mtime is not null)],
                Source: MockupSource.LinkedFiles,
                ModifiedAt: info.Mtime));
        }

        return entries;
    }

    // ---------- шапка записки ----------

    // Первое значение свойства с таким ключом (регистр ключа не важен). null — свойства нет.
    private static string? FirstValue(DocProperties.ParsedProperties parsed, string key)
    {
        foreach (var item in parsed.Items)
            if (item.Key.Equals(key, StringComparison.OrdinalIgnoreCase)) return item.Value;
        return null;
    }

    // «Фича»: значения через запятую, обратные кавычки снимаются, сравнение полного guid
    // без учёта регистра. Несколько guid в значении — допустимо (записка общей для фич).
    private static bool FeatureMatches(string raw, string featureId)
    {
        var stripped = raw.Replace('`', ' ');
        foreach (var token in stripped.Split(','))
        {
            var t = token.Trim();
            if (t.Length > 0 && t.Equals(featureId, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    // «Статус» → enum; ё/е равны (нормализуем ё→е), иначе null. Значение — свободный текст
    // шапки, поэтому не распознанный (опечатка, «на ревью») → null, а не бросок.
    private static MockupStatus? ParseStatus(string? raw)
    {
        if (raw is null) return null;
        var s = raw.Replace('ё', 'е').Replace('Ё', 'Е').Trim().ToLowerInvariant();
        return s switch
        {
            "черновик" => MockupStatus.Draft,
            "утвержден" => MockupStatus.Approved,
            "отклонен" => MockupStatus.Rejected,
            _ => null,
        };
    }

    // «Файлы»: через запятую, markdown-ссылки и кавычки снимаются, относительные пути — от
    // папки записки. Каждый путь: только под docs/mockups/ и только существующий (для файлов
    // шапки несуществующий отбрасывается — в отличие от linkedFiles, где это «не в master»).
    private static IReadOnlyList<MockupFile> ParseFiles(string rootPath, string notePath, string? raw)
    {
        if (raw is null) return [];
        var result = new List<MockupFile>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var token in raw.Split(','))
        {
            var target = CleanToken(token);
            if (target.Length == 0) continue;

            // Относительный путь — от папки записки; «/…» — от корня проекта
            var resolved = DocsIndexService.ResolveRelative(notePath, target);
            if (resolved is null) continue;
            if (!resolved.StartsWith(MockupsPrefix, StringComparison.OrdinalIgnoreCase)) continue;
            if (!seen.Add(resolved)) continue;

            var full = JoinSafe(rootPath, resolved);
            if (full is null || !File.Exists(full)) continue;
            result.Add(new MockupFile(resolved, true));
        }
        return result;
    }

    // Токен «Файлы»: markdown-ссылка [подпись](цель) → цель; кавычки (одинарные/двойные)
    // снимаются; пробелы по краям — тоже. Остальной текст (голый путь) возвращается как есть.
    private static string CleanToken(string token)
    {
        var t = token.Trim();
        var m = DocsIndexService.LinkRegex().Match(t);
        if (m.Success) t = m.Groups[2].Value.Trim();
        t = t.Trim('"', '\'');
        return t;
    }

    // ---------- linkedFiles ----------

    // linkedFiles → только .html под docs/mockups/ (записки .md и -tokens.md макетом не
    // становятся), дубли схлопнуты, порядок появления сохранён.
    //
    // Нормализация «..» (тем же приёмом, что ParseFiles → DocsIndexService.ResolveRelative):
    // docs/mockups/../../x.html → x.html (вне области) — путь отбрасывается, иначе наружу
    // ушёл бы путь с «..» (SafePath.Join защищает корень проекта, но не область mockups);
    // выход выше корня → null. Префикс проверяем ПОСЛЕ нормализации. Регистр схлопнут в
    // канонический нижний: на Linux «DOCS/MOCKUPS/…» — другой (несуществующий) каталог,
    // а не шум exists=false в списке.
    private static IEnumerable<string> DistinctLinkedHtml(IReadOnlyCollection<string> linkedPaths)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in linkedPaths)
        {
            if (p is null) continue;
            var norm = DocsIndexService.ResolveRelative("", p);
            if (norm is null) continue;
            if (!norm.StartsWith(MockupsPrefix, StringComparison.OrdinalIgnoreCase)) continue;
            if (!norm.EndsWith(".html", StringComparison.OrdinalIgnoreCase)) continue;
            if (seen.Add(norm)) yield return norm.ToLowerInvariant();
        }
    }

    // ---------- диск ----------

    // SafePath.Join + File.Exists: цель приходит из шапки/linkedFiles, «../../etc» обязан
    // отказать, а не проверить чужой файл. html в корпус Docs не попадает (нет в TypeGroups),
    // поэтому живость проверяется по диску, а не через корпус.
    //
    // Mtime — null, когда файла нет на диске («не в master»). EscapesArea — когда файл под
    // docs/mockups/ — симлинк, чья реальная цель лежит ВНЕ docs/mockups/ проекта
    // (docs/mockups/link.html → /tmp/x.html): File.Exists/FileInfo следуют ссылке, поэтому
    // exists/mtime цели утекали бы в ответ — такую запись отбрасываем вовсе.
    private readonly record struct DiskInfo(DateTime? Mtime, bool EscapesArea);

    private static DiskInfo ResolveOnDiskInfo(string rootPath, string relativePath)
    {
        var full = JoinSafe(rootPath, relativePath);
        if (full is null || !File.Exists(full)) return new DiskInfo(null, false);

        // Реальный путь (после разрешения ссылок) обязан лежать внутри docs/mockups/ проекта.
        var real = ResolveRealPath(full);
        if (real is null) return new DiskInfo(null, false);
        var mockupsRoot = JoinSafe(rootPath, MockupsPrefix);
        if (mockupsRoot is null || !IsUnder(mockupsRoot, real))
            return new DiskInfo(null, true);   // симлинк на цель вне области → записи нет

        return new DiskInfo(new FileInfo(full).LastWriteTimeUtc, false);
    }

    // Реальный путь файла: симлинк разрешается до конечной цели (включая цепочки), обычный
    // файл — его GetFullPath. null — цель не разрешается (битая/циклическая ссылка).
    private static string? ResolveRealPath(string full)
    {
        try
        {
            var info = new FileInfo(full);
            // ResolveLinkTarget → FileSystemInfo? (не string): цель как путь через FullName.
            var target = info.ResolveLinkTarget(returnFinalTarget: true)?.FullName;
            if (target is null) return Path.GetFullPath(full);
            // Цель может быть относительной — резолвим от папки, где лежит ссылка.
            return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(full)!, target));
        }
        catch
        {
            return null;
        }
    }

    // real лежит внутри dir (dir — без хвостового разделителя; real == dir тоже «внутри»).
    private static bool IsUnder(string dir, string real)
    {
        var d = dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return real.Equals(d, StringComparison.OrdinalIgnoreCase)
            || real.StartsWith(d + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    // null — путь вывел за пределы проекта (SafePath.Join бросил) либо битый разделитель
    private static string? JoinSafe(string rootPath, string relativePath)
    {
        try
        {
            return SafePath.Join(rootPath, relativePath);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or ArgumentException
            or NotSupportedException or PathTooLongException or IOException)
        {
            return null;
        }
    }
}
