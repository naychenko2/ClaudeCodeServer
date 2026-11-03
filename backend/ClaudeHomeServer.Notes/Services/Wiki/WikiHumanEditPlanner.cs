using System.Text;
using System.Text.RegularExpressions;

namespace ClaudeHomeServer.Services.Notes.Wiki;

// Разбор правки страницы человеком «было → стало» в операции пакета автора human
// (ADR-038, решения 1–3; шаг B3a). Чистая функция без I/O и записи: страницы приходят
// текстом, результат — список намерений. Сноски `human`, карточку правки и пакет собирает
// следующий шаг (B3b); здесь решается только ЧТО изменил человек.
//
// Правила:
//  • Утверждение — строка секции без ссылок на сноски. Совпавшее по тексту в той же секции
//    утверждение не меняется и операции НЕ порождает: сноска `human` ставится только тому,
//    что человек написал сам (иначе чужое утверждение ИИ присвоено человеку).
//  • Перестановка строк внутри секции — не правка.
//  • Изменённое утверждение (похожее по словам ≥ EditSimilarity) — `edit`; остальное
//    убранное — `supersede` с причиной «снято человеком» (в «Историю», физического удаления
//    нет); остальное добавленное — `append`.
//  • Секция «Заметки» — отдельная операция `notes` без цитат: целиком текстом секции.
//  • Поля шапки: человек правит только summary, aliases, owns. Поля кода (sources, status,
//    updated), type, kind и заголовок страницы — отказ.
//  • Ошибки разбора и запрещённые правки — Failure с причиной; исключений наружу нет.
public static class WikiHumanEditPlanner
{
    public const string KindCreate = "create";
    public const string KindAppend = "append";
    public const string KindEdit = "edit";
    public const string KindSupersede = "supersede";
    public const string KindNotes = "notes";
    public const string KindHeader = "header";

    /// <summary>Причина `supersede` убранного человеком утверждения (ADR-038, решение 3).</summary>
    public const string SupersedeReason = "снято человеком";

    /// <summary>Порог похожести (доля общих слов) для пары «было → стало» = `edit`.</summary>
    public const double EditSimilarity = 0.5;

    private static readonly Regex FootnoteRefRx = new(@"\s*\[\^[^\]\r\n]+\]", RegexOptions.CultureInvariant);
    private static readonly Regex WordRx = new(@"[\p{L}\p{N}]+", RegexOptions.CultureInvariant);
    private static readonly string[] HumanHeaderFields = ["summary", "aliases", "owns"];

    /// <summary>Намерение пакета human. Page — путь страницы; Section — секция (null у create и
    /// header); Target — замещаемый текст (edit, supersede) или имя поля шапки (header);
    /// Text — новый текст (без ссылок на сноски); Reason — причина (supersede).</summary>
    public sealed record Op(string Kind, string Page, string? Section, string? Target, string? Text, string? Reason = null);

    public sealed record Plan(bool Ok, string? Reason, IReadOnlyList<Op> Operations)
    {
        public static Plan Failure(string reason) => new(false, reason, []);
    }

    /// <summary>Разбирает правку. was == null — страницы не было (создание).</summary>
    public static Plan Build(string pagePath, string? was, string now)
    {
        try
        {
            return BuildCore(pagePath, was, now);
        }
        catch (Exception)
        {
            return Plan.Failure("внутренняя ошибка разбора правки");
        }
    }

    private static Plan BuildCore(string pagePath, string? was, string now)
    {
        if (string.IsNullOrWhiteSpace(pagePath))
            return Plan.Failure("страница не указана");
        if (now is null)
            return Plan.Failure("нет нового текста страницы");

        var after = WikiPage.Parse(now);
        if (after.Model is null)
            return Plan.Failure("новый текст страницы не разобран: " + string.Join("; ", after.Errors));
        var next = after.Model;

        WikiPage prev;
        var ops = new List<Op>();
        if (was is null)
        {
            // Страницы не было: сравниваем с пустой того же типа; create идёт первой.
            prev = new WikiPage
            {
                Title = next.Title,
                // Поля кода у новой страницы считает код при применении: сравнивать их не с чем.
                Frontmatter = new WikiFrontmatter
                {
                    Type = next.Frontmatter.Type,
                    Kind = next.Frontmatter.Kind,
                    Sources = next.Frontmatter.Sources,
                    Updated = next.Frontmatter.Updated,
                    Status = next.Frontmatter.Status,
                },
                Sections = next.Sections
                    .Where(s => s.Name != WikiPage.NotesSectionName)
                    .Select(s => new WikiPage.Section { Name = s.Name, Lines = [] })
                    .ToList(),
            };
            ops.Add(new Op(KindCreate, pagePath, null, null, next.Title));
        }
        else
        {
            var before = WikiPage.Parse(was);
            if (before.Model is null)
                return Plan.Failure("прежний текст страницы не разобран: " + string.Join("; ", before.Errors));
            prev = before.Model;
        }

        if (next.Frontmatter.Type == WikiFrontmatter.TypeSource || prev.Frontmatter.Type == WikiFrontmatter.TypeSource)
            return Plan.Failure("карточка источника неизменяема: её не правят");
        if (prev.Title != next.Title)
            return Plan.Failure("заголовок страницы не правится: переименование — отдельное действие");

        var header = HeaderChanges(pagePath, prev.Frontmatter, next.Frontmatter, out var refusal);
        if (refusal is not null)
            return Plan.Failure(refusal);
        ops.AddRange(header);

        foreach (var section in next.Sections.Where(s => s.Name != WikiPage.NotesSectionName))
        {
            var old = prev.Sections.FirstOrDefault(s => s.Name == section.Name);
            ops.AddRange(DiffSection(pagePath, section.Name,
                old is null ? [] : Statements(old), Statements(section)));
        }

        var oldNotes = NotesText(prev.Notes);
        var newNotes = NotesText(next.Notes);
        if (oldNotes != newNotes)
            ops.Add(new Op(KindNotes, pagePath, WikiPage.NotesSectionName, null, newNotes));

        return new Plan(true, null, ops);
    }

    private static List<Op> HeaderChanges(string page, WikiFrontmatter was, WikiFrontmatter now, out string? refusal)
    {
        refusal = null;
        var forbidden = new List<string>();
        if (was.Type != now.Type) forbidden.Add("type");
        if (was.Kind != now.Kind) forbidden.Add("kind");
        if (!was.Sources.SequenceEqual(now.Sources)) forbidden.Add("sources");
        if (was.Status != now.Status) forbidden.Add("status");
        if (was.Updated != now.Updated) forbidden.Add("updated");
        if (forbidden.Count > 0)
        {
            refusal = $"поля шапки {string.Join(", ", forbidden)} человек не правит: " +
                      $"доступны только {string.Join(", ", HumanHeaderFields)}";
            return [];
        }

        var changes = new List<Op>();
        if (was.Summary != now.Summary)
            changes.Add(new Op(KindHeader, page, null, "summary", now.Summary ?? ""));
        if (!was.Aliases.SequenceEqual(now.Aliases))
            changes.Add(new Op(KindHeader, page, null, "aliases", string.Join(", ", now.Aliases)));
        if (!was.Owns.SequenceEqual(now.Owns))
            changes.Add(new Op(KindHeader, page, null, "owns", string.Join(", ", now.Owns)));
        return changes;
    }

    // Утверждения секции: текст строки без ссылок на сноски, с нормализованными пробелами.
    private static List<string> Statements(WikiPage.Section section) =>
        section.Lines.Select(Normalize).Where(t => t.Length > 0).ToList();

    // NFC: форма Unicode («ё» цельная/составная, «é») не считается правкой содержания.
    internal static string Normalize(string line) =>
        string.Join(' ', FootnoteRefRx.Replace(line, "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .Normalize(NormalizationForm.FormC);

    private static string NotesText(WikiPage.Section? notes) =>
        notes is null ? "" : string.Join('\n', notes.Lines.Select(l => l.TrimEnd())).Trim();

    private static List<Op> DiffSection(string page, string section, List<string> was, List<string> now)
    {
        var oldLeft = Enumerable.Range(0, was.Count).ToList();
        var newLeft = new List<int>();

        // 1. Дословно совпавшие в той же секции — неизменённые, порядок не важен.
        for (var n = 0; n < now.Count; n++)
        {
            var hit = oldLeft.FirstOrDefault(o => was[o] == now[n], -1);
            if (hit >= 0)
                oldLeft.Remove(hit);
            else
                newLeft.Add(n);
        }

        // 2. Оставшиеся: пары «было → стало» по похожести слов, жадно по убыванию.
        var wasWords = oldLeft.ToDictionary(o => o, o => Words(was[o]));
        var nowWords = newLeft.ToDictionary(n => n, n => Words(now[n]));
        var pairs = (from o in oldLeft
                     from n in newLeft
                     let score = Similarity(wasWords[o], nowWords[n])
                     where score >= EditSimilarity
                     orderby score descending, o, n
                     select (o, n)).ToList();
        var edits = new List<(int Old, int New)>();
        foreach (var (o, n) in pairs)
        {
            if (!oldLeft.Contains(o) || !newLeft.Contains(n))
                continue;
            oldLeft.Remove(o);
            newLeft.Remove(n);
            edits.Add((o, n));
        }

        var ops = new List<Op>();
        foreach (var (o, n) in edits.OrderBy(e => e.New))
            ops.Add(new Op(KindEdit, page, section, was[o], now[n]));
        foreach (var o in oldLeft)
            ops.Add(new Op(KindSupersede, page, section, was[o], null, SupersedeReason));
        foreach (var n in newLeft)
            ops.Add(new Op(KindAppend, page, section, null, now[n]));
        return ops;
    }

    private static double Similarity(HashSet<string> x, HashSet<string> y)
    {
        if (x.Count == 0 || y.Count == 0)
            return 0;
        var common = x.Intersect(y).Count();
        return (double)common / x.Union(y).Count();
    }

    private static HashSet<string> Words(string text) =>
        WordRx.Matches(text).Select(m => m.Value.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
}
