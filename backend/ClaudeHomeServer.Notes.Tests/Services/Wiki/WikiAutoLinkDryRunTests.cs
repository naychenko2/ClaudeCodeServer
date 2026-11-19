using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ClaudeHomeServer.Services.Notes.Wiki;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

// Факт сухого прогона: без переменной WIKI_DRYRUN_DIR тест ПРОПУСКАЕТСЯ, а не падает.
// Пропуск решается на этапе discovery (конструктор атрибута читает окружение), поэтому
// обычный прогон CI/гейта показывает «пропущено», а не «красный» (динамический SkipException
// адаптер xunit v2 считает провалом).
public sealed class DryRunFactAttribute : FactAttribute
{
    public const string DirVariable = "WIKI_DRYRUN_DIR";

    public DryRunFactAttribute()
    {
        var dir = Environment.GetEnvironmentVariable(DirVariable);
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
            Skip = $"{DirVariable} не задан (или каталога нет) — сухой прогон пропущен";
    }
}

// A0 (очередь 2, волна A): сухой прогон автосвязывателя на КОПИИ боевой вики — только чтение.
// Каталог копии — переменная окружения WIKI_DRYRUN_DIR (ожидается <корень>/data, внутри
// pages/ и keeper/); без неё прогон пропускается: обычный прогон CI/гейта не должен ходить
// в боевые данные. Артефакт — JSON в WIKI_DRYRUN_OUT (по умолчанию <каталог копии>/../metrics.json).
//
// Что считает:
// - для каждой страницы знаний — WikiAutoLink.Apply к её тексту (снимок раздела и цепочки
//   вверх), число поставленных ссылок и число несточных совпадений (MissedWordForms);
// - степень страницы ДО и ПОСЛЕ — той же формулой, что у замера Q1-QA4:
//   |mentions| + |mentionedHere| + |backlinks| без потолка GroupLimit (WikiConnectivity);
// - три среза: все страницы знаний; страницы ночи 18.11 (applied create/append пакетов с
//   createdAt 2026-11-18); 76 мелких (страницы знаний разделов ночи 19.11 со сводкой
//   < 400 символов и ≤ 1 сноской — тот же признак «мелких», что в отчёте Q1-QA4);
// - прогноз «со словоформами и алиасами»: несточные совпадения страницы прибавляются к её
//   степени как исходящие связи, обратные не считаются (консервативно).
[Trait("Category", "Bench")]
public class WikiAutoLinkDryRunTests
{
    private static readonly Regex LinkRx = new(@"\[\[([^\]\r\n]+)\]\]", RegexOptions.Compiled);

    [DryRunFact]
    public void СухойПрогон_СчитаетСтепеньДоПосле_ИПишетАртефакт()
    {
        var dir = Environment.GetEnvironmentVariable("WIKI_DRYRUN_DIR");
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
            return; // пропуск: без каталога копии считать нечего (см. DryRunFactAttribute)

        var pagesRoot = Path.Combine(dir, "pages");
        Directory.Exists(pagesRoot).Should().BeTrue("в каталоге копии должен быть pages/");

        // ─── страницы ───
        var pages = new List<Page>();
        foreach (var full in Directory.EnumerateFiles(pagesRoot, "*.md", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(pagesRoot, full).Replace('\\', '/');
            var text = File.ReadAllText(full);
            var parsed = WikiPage.Parse(text);
            var section = WikiSections.Parse(rel);
            pages.Add(new Page(
                rel,
                section,
                text,
                parsed.Model,
                parsed.Errors));
        }

        // ─── разделы и цепочка вверх ───
        // Сферы в этой вики нет (в pages/ только «Проекты»), поэтому цепочка вверх — common.
        var sections = pages
            .Where(p => p.Section is not null)
            .Select(p => p.Section!)
            .Distinct()
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();
        var commonPages = pages.Where(p => p.Section == WikiSections.Common && p.Model is not null).ToList();

        // ─── снимок для автолинковки: свой раздел + цепочка вверх ───
        var snapshotBySection = new Dictionary<string, List<(string Section, WikiPage Page, string Path)>>(StringComparer.Ordinal);
        foreach (var section in sections)
        {
            var entries = new List<(string, WikiPage, string)>();
            foreach (var p in pages)
            {
                if (p.Model is null || p.Section is null)
                    continue;
                if (p.Section == section || p.Section == WikiSections.Common)
                    entries.Add((p.Section, p.Model, p.Path));
            }
            snapshotBySection[section] = entries;
        }

        // ─── прогон Apply по каждой странице знаний ───
        var addedLinks = new List<AddedLink>();
        var missedByPath = new Dictionary<string, int>(StringComparer.Ordinal);
        var afterText = new Dictionary<string, string>(StringComparer.Ordinal);
        var addedByPath = new Dictionary<string, int>(StringComparer.Ordinal);
        var reparsedBroken = 0;
        foreach (var p in pages)
        {
            afterText[p.Path] = p.Text;
            if (!IsKnowledge(p))
                continue;
            var snapshot = snapshotBySection[p.Section!];
            var res = WikiAutoLink.Apply(p.Text, p.Path, p.Section!, Chain(p.Section!), snapshot);
            afterText[p.Path] = res.Text;
            addedByPath[p.Path] = res.Added;
            missedByPath[p.Path] = res.MissedWordForms;
            if (res.Added > 0)
                foreach (var link in DiffLinks(p.Text, res.Text))
                    addedLinks.Add(new AddedLink(p.Path, link.Target, link.Context, p.Section!));
            // Страница после автолинковки должна остаться разбираемой: иначе фикстурный
            // снимок «после» построен на неверном тексте.
            if (WikiPage.Parse(res.Text).Model is null)
                reparsedBroken++;
        }

        // ─── степень до и после (формула Q1-QA4, без потолка группы) ───
        var draftsBefore = pages
            .Where(p => p.Model is not null)
            .Select(p => new WikiGate.Draft(p.Path, p.Text))
            .ToList();
        var draftsAfter = pages
            .Where(p => p.Model is not null)
            .Select(p => new WikiGate.Draft(p.Path, afterText[p.Path]))
            .ToList();

        // ─── срезы ───
        var night18 = NightPages(dir, "2026-11-18");
        var night19 = NightPages(dir, "2026-11-19");
        var night19Sections = NightSections(dir, "2026-11-19");

        var knowledge = pages.Where(IsKnowledge).ToList();
        var sliceAll = knowledge.Select(p => p.Path).ToList();
        var sliceNight18 = knowledge.Where(p => night18.Touched.Contains(p.Path)).Select(p => p.Path).ToList();
        var sliceNight18Created = knowledge.Where(p => night18.Created.Contains(p.Path)).Select(p => p.Path).ToList();
        var small = knowledge
            .Where(p => p.Model is not null && night19Sections.Contains(p.Section!))
            .Where(IsSmall)
            .ToList();
        var sliceSmall = small.Select(p => p.Path).ToList();
        // Срез в формате Q1-QA4: там в степень входят и карточки источников (их связь —
        // обратные ссылки со сносок). Автолинковка по карточкам не ходит, но для
        // сопоставимости чисел с отчётом Q1-QA4 срез нужен.
        var sliceNight18All = pages
            .Where(p => night18.Touched.Contains(p.Path) && p.Model is not null
                && !p.Path.Split('/')[^1].StartsWith('_'))
            .Select(p => p.Path)
            .ToList();
        var sliceAllPages = pages
            .Where(p => p.Model is not null && !p.Path.Split('/')[^1].StartsWith('_'))
            .Select(p => p.Path)
            .ToList();

        var slices = new List<SliceOut>();
        slices.Add(Measure("все страницы знаний", sliceAll, draftsBefore, draftsAfter, addedByPath, missedByPath));
        slices.Add(Measure("ночь 18.11 (тронутые знания)", sliceNight18, draftsBefore, draftsAfter, addedByPath, missedByPath));
        slices.Add(Measure("ночь 18.11 (созданные знания)", sliceNight18Created, draftsBefore, draftsAfter, addedByPath, missedByPath));
        slices.Add(Measure("ночь 18.11 (тронутые, с карточками источников)", sliceNight18All, draftsBefore, draftsAfter, addedByPath, missedByPath));
        slices.Add(Measure("все страницы (с карточками источников)", sliceAllPages, draftsBefore, draftsAfter, addedByPath, missedByPath));
        slices.Add(Measure("76 мелких (разделы ночи 19.11)", sliceSmall, draftsBefore, draftsAfter, addedByPath, missedByPath));

        // ─── диагностика: кандидаты в ссылку (то + словоформы) ───
        var candidates = Candidates(pages, knowledge);


        var outPath = Environment.GetEnvironmentVariable("WIKI_DRYRUN_OUT");
        if (string.IsNullOrWhiteSpace(outPath))
            outPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(dir))!, "metrics.json");

        var payload = new
        {
            generatedAt = DateTime.UtcNow.ToString("o"),
            copyDir = Path.GetFullPath(dir),
            pagesTotal = pages.Count,
            pagesParsed = pages.Count(p => p.Model is not null),
            pagesUnparsed = pages.Count(p => p.Model is null),
            pagesUnparsedList = pages.Where(p => p.Model is null).Select(p => p.Path).Take(50).ToList(),
            sections = sections.Count,
            pagesReparsedBroken = reparsedBroken,
            linksAddedTotal = addedByPath.Values.Sum(),
            pagesWithAdded = addedByPath.Count(kv => kv.Value > 0),
            missedTotal = missedByPath.Values.Sum(),
            night18Touched = night18.Touched.Count,
            night18Created = night18.Created.Count,
            night19Sections,
            smallCount = small.Count,
            smallPaths = sliceSmall,
            slices,
            addedLinks = addedLinks.Select(a => new { a.Path, a.Target, a.Context, a.Section }),
            candidatesTotal = candidates.Count,
            candidatesExact = candidates.Count(c => c.Kind == "exact"),
            candidatesForm = candidates.Count(c => c.Kind == "form"),
            candidatesEatsPunctuation = candidates.Count(c => c.EatsPunctuation),
            candidatesDigitLoss = candidates.Count(c => c.DigitLoss),
            candidates = candidates,
            missedByPath = missedByPath.Where(kv => kv.Value > 0)
                .ToDictionary(kv => kv.Key, kv => kv.Value),
            addedByPath = addedByPath.Where(kv => kv.Value > 0)
                .ToDictionary(kv => kv.Key, kv => kv.Value),
        };
        File.WriteAllText(outPath, JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }));

        // Sanity: прогон обязан что-то посчитать, иначе «нули» — не замер, а пустой вход.
        slices.Should().NotBeEmpty();
        slices.Should().OnlyContain(s => s.Count > 0, "в каждом срезе есть страницы: иначе замер пуст");
    }

    // ─── диагностика: кандидаты в ссылку ───

    private sealed record Candidate(
        string Path,
        string Target,
        string Kind,
        string Context,
        bool EatsPunctuation,
        bool DigitLoss,
        bool InUnlinkedSection,
        bool SpanHasDigit);

    // Кандидаты в ссылку: те же совпадения, что и у WikiAutoLink (слова по основам, подряд),
    // но со всеми вхождениями и без фильтров Apply (цифры в фразе, «История»/«Заметки»,
    // потолок ссылок на строке) — чтобы увидеть, что автолинковщик СДЕЛАЛ бы, если бы
    // линковал словоформы (вход решения по B5) и где его правила дают ложные ссылки.
    // Проверка сверки: среди кандидатов kind=exact должны быть все фактически
    // поставленные Apply ссылки (см. отчёт).
    private static List<Candidate> Candidates(List<Page> pages, List<Page> knowledge)
    {
        var bySection = pages
            .Where(p => p.Model is not null && p.Section is not null)
            .GroupBy(p => p.Section!)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        var result = new List<Candidate>();
        foreach (var page in knowledge)
        {
            var targets = bySection[page.Section!].Where(t =>
                t.Model is not null &&
                t.Model.Frontmatter.Type != WikiFrontmatter.TypeSource &&
                t.Model.Frontmatter.Type != WikiFrontmatter.TypeChronicle &&
                !t.Path.Split('/')[^1].StartsWith('_') &&
                t.Path != page.Path).ToList();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var section = (string?)null;
            foreach (var rawLine in page.Text.Split('\n'))
            {
                var line = rawLine.TrimEnd('\r');
                if (line.StartsWith("## ", StringComparison.Ordinal))
                {
                    section = line[3..].Trim();
                    continue;
                }
                if (line.Length == 0 || line[0] == '#')
                    continue;
                var inUnlinked = section is "История" or "Заметки";
                var norm = NormalizeKeepIndexes(line);
                if (norm is null)
                    continue;
                var words = WordSpans(norm);
                var stems = words.Select(w => WikiCandidates.StemOf(norm[w.Start..w.End])).ToList();
                foreach (var target in targets)
                {
                    var titleWords = WordsOf(Normalize(target.Model!.Title));
                    if (titleWords.Count == 0)
                        continue;
                    if (titleWords.Count == 1 &&
                        (titleWords[0].Length < WikiAutoLink.MinSingleWordTitleChars ||
                         WikiCandidates.IsStopWord(titleWords[0])))
                        continue;
                    var targetStems = titleWords.Select(WikiCandidates.StemOf).ToList();
                    for (var i = 0; i + titleWords.Count <= words.Count; i++)
                    {
                        var ok = true;
                        for (var k = 0; k < titleWords.Count; k++)
                            if (stems[i + k] != targetStems[k])
                            {
                                ok = false;
                                break;
                            }
                        if (!ok)
                            continue;
                        var start = words[i].Start;
                        var end = words[i + titleWords.Count - 1].End;
                        var exact = true;
                        for (var k = 0; k < titleWords.Count; k++)
                            if (norm[words[i + k].Start..words[i + k].End] != titleWords[k])
                                exact = false;
                        var span = line[start..end];
                        var title = target.Model.Title;
                        var candidate = new Candidate(
                            page.Path,
                            title,
                            exact ? "exact" : "form",
                            line.Length > 300 ? line[..300] + "…" : line,
                            span.Any(c => c is '.' or '!' or '?' or ';' or ':'),
                            title.Any(char.IsDigit) && !span.Any(char.IsDigit),
                            inUnlinked,
                            span.Any(char.IsDigit));
                        // Одна цель — одна ссылка на текст: повторы той же цели в тексте
                        // в population не входят (Apply берёт первое вхождение).
                        if (!seen.Add(target.Model.Title))
                            continue;
                        result.Add(candidate);
                    }
                }
            }
        }
        return result;
    }

    // Нормализация строки с сохранением индексов (как у WikiAutoLink): NFC, нижний регистр, ё→е.
    private static string? NormalizeKeepIndexes(string line)
    {
        if (line.Normalize(System.Text.NormalizationForm.FormC).Length != line.Length)
            return null;
        var sb = new StringBuilder(line.Length);
        foreach (var ch in line)
        {
            var low = char.ToLowerInvariant(ch);
            sb.Append(low == 'ё' ? 'е' : low);
        }
        return sb.ToString();
    }

    private static string Normalize(string text) =>
        text.Trim().Normalize(System.Text.NormalizationForm.FormC).ToLowerInvariant().Replace('ё', 'е');

    private static List<(int Start, int End)> WordSpans(string norm)
    {
        var spans = new List<(int Start, int End)>();
        var start = -1;
        for (var i = 0; i < norm.Length; i++)
        {
            if (char.IsLetter(norm[i]))
            {
                if (start < 0)
                    start = i;
            }
            else if (start >= 0)
            {
                spans.Add((start, i));
                start = -1;
            }
        }
        if (start >= 0)
            spans.Add((start, norm.Length));
        return spans;
    }

    private static List<string> WordsOf(string normText) =>
        [.. WordSpans(normText).Select(w => normText[w.Start..w.End])];

    // ─── страница ───

    private sealed record Page(
        string Path,
        string? Section,
        string Text,
        WikiPage? Model,
        IReadOnlyList<string> Errors);

    private sealed record AddedLink(string Path, string Target, string Context, string Section);

    private sealed record SliceOut(
        string Name,
        int Count,
        double AvgBefore,
        double ShareBefore,
        double AvgAfter,
        double ShareAfter,
        int Added,
        int Missed,
        double AvgForecast,
        double ShareForecast,
        IReadOnlyDictionary<string, double> DegreesBefore,
        IReadOnlyDictionary<string, double> DegreesAfter);

    // Страница знаний: не карточка источника, не хроника, не проекция «_*».
    private static bool IsKnowledge(Page p) =>
        p.Model is not null &&
        p.Section is not null &&
        p.Model.Frontmatter.Type != WikiFrontmatter.TypeSource &&
        p.Model.Frontmatter.Type != WikiFrontmatter.TypeChronicle &&
        !p.Path.Split('/')[^1].StartsWith('_');

    // «Мелкая» страница — тот же признак, что в отчёте Q1-QA4: сводка < 400 символов и
    // не больше одной сноски (утверждения).
    private static bool IsSmall(Page p)
    {
        if (p.Model is null)
            return false;
        var summary = p.Model.Sections.FirstOrDefault(s => s.Name == "Сводка");
        var summaryLen = summary is null ? 0 : string.Join("\n", summary.Lines).Trim().Length;
        return summaryLen < 400 && p.Model.Footnotes.Count <= 1;
    }

    // Цепочка вверх: сфер в вики нет — только common.
    private static IReadOnlyList<string> Chain(string section) =>
        section == WikiSections.Common ? [] : [WikiSections.Common];

    // ─── ссылки: что добавилось ───

    private static IEnumerable<(string Target, string Context)> DiffLinks(string before, string after)
    {
        var old = Counts(before);
        var newCounts = Counts(after);
        foreach (var (target, count) in newCounts)
        {
            var extra = count - (old.TryGetValue(target, out var o) ? o : 0);
            for (var i = 0; i < extra; i++)
                yield return (target, ContextOf(after, target, i));
        }
    }

    private static Dictionary<string, int> Counts(string text)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (Match m in LinkRx.Matches(text))
        {
            var inner = m.Groups[1].Value;
            var bar = inner.IndexOf('|');
            if (bar >= 0)
                inner = inner[..bar];
            counts[inner] = counts.GetValueOrDefault(inner) + 1;
        }
        return counts;
    }

    // Контекст ссылки: строка, где стоит [[цель]] (первое вхождение цели в тексте).
    private static string ContextOf(string text, string target, int occurrence)
    {
        var seen = 0;
        foreach (var line in text.Split('\n'))
        {
            foreach (Match m in LinkRx.Matches(line))
            {
                var inner = m.Groups[1].Value;
                var bar = inner.IndexOf('|');
                if (bar >= 0)
                    inner = inner[..bar];
                if (!string.Equals(inner, target, StringComparison.Ordinal))
                    continue;
                if (seen++ != occurrence)
                    continue;
                return line.Length > 300 ? line[..300] + "…" : line;
            }
        }
        return "";
    }

    // ─── ночь: страницы, тронутые пакетами даты ───

    private static (HashSet<string> Touched, HashSet<string> Created) NightPages(string dir, string date)
    {
        var touched = new HashSet<string>(StringComparer.Ordinal);
        var created = new HashSet<string>(StringComparer.Ordinal);
        var pkgDir = Path.Combine(dir, "keeper", "packages");
        if (!Directory.Exists(pkgDir))
            return (touched, created);
        foreach (var file in Directory.EnumerateFiles(pkgDir, "*.json"))
        {
            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(File.ReadAllText(file));
            }
            catch (JsonException)
            {
                continue;
            }
            using (doc)
            {
                var root = doc.RootElement;
                if (!root.TryGetProperty("createdAt", out var createdAt))
                    continue;
                var createdAtText = createdAt.ValueKind == JsonValueKind.String ? createdAt.GetString() : null;
                if (createdAtText is null || !createdAtText.StartsWith(date, StringComparison.Ordinal))
                    continue;
                var status = root.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.String
                    ? st.GetString() : null;
                if (status is not ("applied" or "accepted" or "partially_accepted"))
                    continue;
                if (!root.TryGetProperty("operations", out var ops) || ops.ValueKind != JsonValueKind.Array)
                    continue;
                foreach (var op in ops.EnumerateArray())
                {
                    var opName = op.TryGetProperty("op", out var o) ? o.GetString() : null;
                    if (opName is not ("create" or "append"))
                        continue;
                    var decision = op.TryGetProperty("decision", out var d) ? d.GetString() : null;
                    var judge = op.TryGetProperty("judgeDecision", out var j) ? j.GetString() : null;
                    var human = op.TryGetProperty("humanDecision", out var h) ? h.GetString() : null;
                    var applied = decision == "auto" || judge == "accepted" || human == "accepted";
                    if (!applied)
                        continue;
                    var page = op.TryGetProperty("page", out var pg) ? pg.GetString() : null;
                    if (string.IsNullOrWhiteSpace(page))
                        continue;
                    touched.Add(page);
                    if (opName == "create")
                        created.Add(page);
                }
            }
        }
        return (touched, created);
    }

    private static IReadOnlyList<string> NightSections(string dir, string date)
    {
        var set = new SortedSet<string>(StringComparer.Ordinal);
        var pkgDir = Path.Combine(dir, "keeper", "packages");
        if (!Directory.Exists(pkgDir))
            return [];
        foreach (var file in Directory.EnumerateFiles(pkgDir, "*.json"))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                var root = doc.RootElement;
                var createdAt = root.TryGetProperty("createdAt", out var c) ? c.GetString() : null;
                if (createdAt is null || !createdAt.StartsWith(date, StringComparison.Ordinal))
                    continue;
                var section = root.TryGetProperty("section", out var s) ? s.GetString() : null;
                if (!string.IsNullOrWhiteSpace(section))
                    set.Add(section);
            }
            catch (JsonException)
            {
                // нечитаемый пакет — просто не в срезе
            }
        }
        return [.. set];
    }

    // ─── замер среза ───

    private static SliceOut Measure(
        string name,
        IReadOnlyList<string> subset,
        IReadOnlyList<WikiGate.Draft> before,
        IReadOnlyList<WikiGate.Draft> after,
        IReadOnlyDictionary<string, int> addedByPath,
        IReadOnlyDictionary<string, int> missedByPath)
    {
        var mBefore = WikiConnectivity.Measure(before, subset);
        var mAfter = WikiConnectivity.Measure(after, subset);
        var degreesBefore = Degrees(before, subset);
        var degreesAfter = Degrees(after, subset);

        var added = 0;
        var missed = 0;
        var gained = 0;
        foreach (var path in subset)
        {
            added += addedByPath.GetValueOrDefault(path);
            missed += missedByPath.GetValueOrDefault(path);
            // Прогноз «со словоформами»: несточные совпадения страницы идут в её степень
            // как исходящие связи; обратные не считаем (консервативно).
            var d = degreesAfter.GetValueOrDefault(path);
            var forecast = d + missedByPath.GetValueOrDefault(path);
            if (forecast >= 1 && d < 1)
                gained++;
        }
        var count = subset.Count;
        var avgForecast = count == 0 ? 0 : (degreesAfter.Values.Sum() + (double)missed) / count;
        var linkedForecast = subset.Count(path => degreesAfter.GetValueOrDefault(path) >= 1) + gained;
        var shareForecast = count == 0 ? 0 : (double)linkedForecast / count;
        return new SliceOut(
            name,
            count,
            mBefore.AvgDegree,
            mBefore.LinkedShare,
            mAfter.AvgDegree,
            mAfter.LinkedShare,
            added,
            missed,
            avgForecast,
            shareForecast,
            degreesBefore,
            degreesAfter);
    }

    // Степень каждой страницы среза (для отчёта и для проверки «после ≥ до»).
    private static Dictionary<string, double> Degrees(IReadOnlyList<WikiGate.Draft> drafts, IReadOnlyList<string> subset)
    {
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var path in subset)
        {
            var page = drafts.FirstOrDefault(d => d.Path == path);
            if (page == default)
                continue;
            var groups = WikiRelated.Build(page, drafts, limit: null);
            result[path] = groups
                .Where(g => g.Kind is WikiRelated.KindMentions or WikiRelated.KindMentionedHere or WikiRelated.KindBacklinks)
                .Sum(g => g.Items.Count);
        }
        return result;
    }
}
