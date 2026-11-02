using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ClaudeHomeServer.Services;

namespace ClaudeHomeServer.Services.Notes.Wiki;

// Проекции раздела (ADR-035, решение 4, пункт 6; ADR-035, решение 6): _index.md — каталог
// страниц раздела, _log.md — последние LogLinesLimit строк журнала этого раздела, новые сверху.
// Оба файла — производные: их генерирует только код, ЦЕЛИКОМ при каждой перегенерации
// (никакой частичной правки, никакого слияния с ручной), поэтому их никогда нет ни в манифесте
// раздела, ни в сверке дрейфа: дрейф — про страницу, а не про её каталог.
// Вызывается только Хранителем под блокировкой этого же раздела (вторую блокировку того же
// ключа брать нельзя — зависание). В Dify проекции не индексируются (решение 8), в граф и
// список заметок не попадают (WikiPaths.IsProjection — один предикат на весь сервер).
// Строки журнала читаются терпимо: обрыв или невалидный JSON пропускается, перегенерация не
// падает — журнал дописывается append и мог дойти до читателя оборванным хвостом.
public static class WikiProjections
{
    public const string IndexFileName = "_index.md";
    public const string LogFileName = "_log.md";

    // Столько последних записей журнала показывает _log.md: окно, читаемое человеком целиком;
    // полная история остаётся в log.jsonl — его строки не переписываются (решение 13).
    public const int LogLinesLimit = 200;

    // Признак авторства страницы (ADR-038): считает код по сноскам, в шапке не хранится.
    // «ИИ» — нет сносок на карточки origin: human; «вместе» — есть и такие, и другие;
    // «человек» — только на карточки human. Страница без сносок — «ИИ» (человеческого в ней нет).
    public const string AuthorshipAi = "ai";
    public const string AuthorshipTogether = "together";
    public const string AuthorshipHuman = "human";

    private static readonly Regex TitleRx = new(@"^# (.+)$", RegexOptions.CultureInvariant);
    private static readonly Regex FootnoteRefRx = new(@"\[\^(s\d+)\]", RegexOptions.CultureInvariant);

    // Секция, чьи сноски авторство не считает: в «Истории» лежат снятые и замещённые утверждения,
    // а не то, что страница говорит сейчас (ADR-038, §2).
    private const string HistorySectionName = "История";

    /// <summary>Что известно о карточке источника по её шапке: origin и дата захвата.</summary>
    public readonly record struct CardInfo(string? Origin, string? Captured);

    /// <summary>Сноска страницы для чтения: label — s1…sN, card — заголовок карточки из цели,
    /// origin/captured — из шапки карточки (null, когда карточки нет).</summary>
    public sealed record FootnoteInfo(string Label, string Card, string? Origin, string? Captured);

    /// <summary>Карточки источников состояния: нормализованный заголовок → origin и captured.
    /// Неразобранные страницы пропускаются; повтор заголовка — первая карточка.</summary>
    public static IReadOnlyDictionary<string, CardInfo> Cards(IEnumerable<WikiGate.Draft> state)
    {
        var cards = new Dictionary<string, CardInfo>(StringComparer.Ordinal);
        foreach (var d in state)
            if (WikiPage.Parse(d.Content).Model is { } page && page.Frontmatter.Type == WikiFrontmatter.TypeSource)
                cards.TryAdd(NormTitle(page.Title), new CardInfo(page.Frontmatter.Origin, page.Frontmatter.Captured));
        return cards;
    }

    /// <summary>Сноски страницы с происхождением карточек — как их видит страница в UI.</summary>
    public static IReadOnlyList<FootnoteInfo> Footnotes(WikiPage page, IReadOnlyDictionary<string, CardInfo> cards) =>
        page.Footnotes
            .Select((f, i) =>
            {
                cards.TryGetValue(NormTitle(f.Target.Text), out var card);
                return new FootnoteInfo($"s{i + 1}", f.Target.Text, card.Origin, card.Captured);
            })
            .ToList();

    /// <summary>Признак авторства страницы по сноскам ДЕЙСТВУЮЩИХ утверждений: сноски, на которые
    /// ссылаются только из «Истории» (и повисшие без ссылок), не считаются. originOfCard — origin
    /// карточки по заголовку из цели сноски (null — карточка неизвестна, считается не-human).</summary>
    public static string PageAuthorship(WikiPage page, Func<string, string?> originOfCard)
    {
        var active = new HashSet<string>(StringComparer.Ordinal);
        foreach (var section in page.Sections)
        {
            if (section.Name == HistorySectionName)
                continue;
            foreach (var line in section.Lines)
                foreach (Match m in FootnoteRefRx.Matches(line))
                    active.Add(m.Groups[1].Value);
        }
        var human = 0;
        var other = 0;
        for (var i = 0; i < page.Footnotes.Count; i++)
        {
            if (!active.Contains($"s{i + 1}"))
                continue;
            var target = page.Footnotes[i].Target;
            if (target.IsLink && originOfCard(target.Text) == WikiGate.OriginHuman)
                human++;
            else
                other++;
        }
        return human == 0 ? AuthorshipAi : other == 0 ? AuthorshipHuman : AuthorshipTogether;
    }

    /// <summary>Признак авторства каждой страницы-не-карточки состояния: путь → признак.
    /// Карточки (type: source) — данные, строки в дереве не образуют; неразобранные страницы
    /// пропускаются. Заголовок карточки сверяется как в гейте: без регистра и краевых пробелов.</summary>
    public static IReadOnlyDictionary<string, string> Authorship(IReadOnlyList<WikiGate.Draft> state)
    {
        var parsed = new List<(string Path, WikiPage Page)>();
        foreach (var d in state)
            if (WikiPage.Parse(d.Content).Model is { } model)
                parsed.Add((d.Path, model));
        var cards = Cards(state);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (path, page) in parsed)
            if (page.Frontmatter.Type != WikiFrontmatter.TypeSource)
                result[path] = PageAuthorship(page, t => cards.TryGetValue(NormTitle(t), out var c) ? c.Origin : null);
        return result;
    }

    private static string NormTitle(string title) => WikiGate.Norm(title);

    /// <summary>Перегенерировать обе проекции раздела целиком. Раздел обязан быть
    /// разбираемым ключом (тот же контракт, что у CheckPage Хранителя).</summary>
    public static void RegenerateSection(string root, string sectionKey)
    {
        var folder = WikiSections.Folder(sectionKey);
        if (folder is null)
            throw new ArgumentException($"проекции пишутся только в раздел, а «{sectionKey}» — не ключ раздела", nameof(sectionKey));
        WriteIndex(root, folder, sectionKey);
        WriteLog(root, folder, sectionKey);
    }

    // _index.md: строка на каждую страницу раздела — имя (заголовок страницы), type и одна
    // строка описания из шапки (summary). Порядок строк — по относительному пути (Ordinal):
    // diff между перегенерациями читаем и не зависит от обхода ФС.
    // Страницы раздела — все .md под его папкой рекурсивно: карточки источников живут в
    // «Источники/» своего раздела (решение 3) и тоже относятся к его каталогу. Ссылка — голой
    // [[Заголовок]]: внутри раздела она резолвится без префикса (решение 1).
    private static void WriteIndex(string root, string folder, string sectionKey)
    {
        var sectionDir = SafePath.JoinBelowRoot(root, "pages/" + folder);
        var sb = new StringBuilder();
        sb.Append("# Каталог раздела «").Append(sectionKey).Append("»\n\n");
        foreach (var rel in EnumeratePageRels(sectionDir))
        {
            string text;
            try { text = File.ReadAllText(Path.Combine(sectionDir, rel), Encoding.UTF8); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { text = ""; }
            sb.Append(IndexLine(rel, text)).Append('\n');
        }
        WikiFileWriter.Write(root, "pages/" + folder + IndexFileName, sb.ToString());
    }

    private static IEnumerable<string> EnumeratePageRels(string sectionDir)
    {
        if (!Directory.Exists(sectionDir))
            return [];
        return Directory.EnumerateFiles(sectionDir, "*.md", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(sectionDir, f).Replace('\\', '/'))
            // Служебные файлы раздела (_index/_log/_health) — не страницы: каталог сам себя
            // не перечисляет (тот же предикат, что у графа заметок и датасета Dify).
            .Where(rel => !WikiPaths.IsProjection(rel))
            .OrderBy(rel => rel, StringComparer.Ordinal);
    }

    private static string IndexLine(string rel, string text)
    {
        var parsed = WikiFrontmatter.Parse(text);
        var title = parsed.Model is null ? null : TitleOf(text, parsed.CloseLineIndex);
        // Имя, которого нельзя достать из шапки, — имя файла: страница остаётся в каталоге
        // (она есть на диске) и помечается как неразобранная — молчание о странице хуже.
        var name = title ?? Path.GetFileNameWithoutExtension(rel);
        if (parsed.Model is null)
            return $"- [[{name}]] — (шапка не разобрана)";
        var line = $"- [[{name}]] — {parsed.Model.Type}";
        if (parsed.Model.Summary is { Length: > 0 })
            line += " — " + parsed.Model.Summary;
        return line;
    }

    private static string? TitleOf(string text, int closeLineIndex)
    {
        var lines = WikiFrontmatter.SplitLines(text);
        for (var i = closeLineIndex + 1; i < lines.Length; i++)
        {
            var m = TitleRx.Match(lines[i]);
            if (m.Success)
                return m.Groups[1].Value;
        }
        return null;
    }

    // _log.md: строки log.jsonl этого раздела (журнал общий на владельца — фильтр по section
    // здесь обязателен), последние LogLinesLimit, новые сверху: последние по порядку файла
    // строки в обратном порядке (файл дописывается append — его порядок и есть хронология).
    private static void WriteLog(string root, string folder, string sectionKey)
    {
        var entries = new List<string>();
        var logPath = SafePath.JoinBelowRoot(root, "keeper/log.jsonl");
        string[] lines = [];
        try
        {
            if (File.Exists(logPath))
                lines = File.ReadAllLines(logPath, Encoding.UTF8);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        foreach (var line in lines)
        {
            var rendered = RenderLogLine(line, sectionKey);
            if (rendered is not null)
                entries.Add(rendered);
        }

        var sb = new StringBuilder();
        sb.Append("# Журнал раздела «").Append(sectionKey).Append("»\n\n");
        foreach (var entry in Enumerable.Reverse(entries).Take(LogLinesLimit))
            sb.Append(entry).Append('\n');
        WikiFileWriter.Write(root, "pages/" + folder + LogFileName, sb.ToString());
    }

    // Строка журнала формата ADR: {ts, op, section, pages, source, actor}. Нечитается —
    // пропускается: обрыв посреди записи, невалидный JSON, объект без ts/op/actor или
    // чужой раздел. Ни одно из них не роняет перегенерацию: _log.md — витрина, а не стор.
    private static string? RenderLogLine(string line, string sectionKey)
    {
        if (line.Length == 0)
            return null;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var obj = doc.RootElement;
            if (obj.ValueKind != JsonValueKind.Object)
                return null;
            if (!TryStr(obj, "section", out var section) || section != sectionKey)
                return null;
            if (!TryStr(obj, "ts", out var ts) || !TryStr(obj, "op", out var op) || !TryStr(obj, "actor", out var actor))
                return null;

            var parts = new List<string> { ts, op, actor };
            if (obj.TryGetProperty("pages", out var pages) && pages.ValueKind == JsonValueKind.Array)
            {
                var listed = pages.EnumerateArray()
                    .Where(p => p.ValueKind == JsonValueKind.String && p.GetString() is { Length: > 0 })
                    .Select(p => p.GetString()!)
                    .ToList();
                if (listed.Count > 0)
                    parts.Add(string.Join(", ", listed));
            }
            if (TryStr(obj, "source", out var source))
                parts.Add(source);
            return "- " + string.Join(" · ", parts);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TryStr(JsonElement obj, string name, out string value)
    {
        value = "";
        if (!obj.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.String)
            return false;
        value = el.GetString() ?? "";
        return value.Length > 0;
    }
}
