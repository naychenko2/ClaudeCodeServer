using System.Text;
using System.Text.RegularExpressions;
using ClaudeHomeServer.Services.Notes;

namespace ClaudeHomeServer.Services.Notes.Wiki;

// Q2-A2: чистая функция автосвязей (разрез очереди 2, волна A). Вход — текст, снимок разделов
// и цепочки вверх (тот же набор, что у WikiGate.ResolveLink и StripUnresolvedLinks) и путь своей
// страницы; выход — текст с поставленными [[…]], их число и число потерянных словоформ/алиасов
// (связей, которых эта версия не ставит — вход замера A0). Диска не читает, ничего не пишет;
// вызывающие (A3 — валидатор перед Text пакета, B1 — догон Хранителя) берут снимок сами:
// это текст, не хранилище.
// Правила:
// - Цель — только заголовок (алиасы целью не становятся; их точные вхождения считаются
//   в MissedWordForms). Не цель: карточки source и chronicle, проекции "_*", своя страница.
// - Совпадение — последовательность слов заголовка по основам WikiCandidates (NFC, нижний
//   регистр, ё→е, те же основы) на границах слов: слова — полные последовательности букв и
//   цифр строки (цифра внутри слова — часть слова, «qwen3» ≠ «qwen»), в промежутке между
//   словами цели — только пробельные символы («очередь. задачи» — не вхождение фразы).
//   Самое длинное совпадение первым: точное совпадение
//   занимает свой интервал, и более короткое внутри него не линкуется. Однословная цель — от
//   MinSingleWordTitleChars букв и не из стоп-списка (короткие заголовки неотличимы от
//   обычных слов).
// - Ставится только точное совпадение заголовка (после нормализации) — фраза заменяется на
//   [[Заголовок]] в каноническом написании заголовка — и только если ResolveLink даёт Resolved
//   в ту же цель (тот же раздел): боковых ссылок нет по построению (инвариант разреза).
//   Словоформы не линкуются и текст не меняют — считаются в MissedWordForms.
// - Не трогает: текст внутри [[…]] и [^…], определения сносок, код (NotesService.CodeMask),
//   строки заголовков "#", фразы с цифрами (правило чисел claim не ослабляется), секции
//   «История» и «Заметки».
// - Одна цель — одна ссылка на текст (первое вхождение). Строка, где ссылок уже
//   MaxLinksPerLine (существующих и новых вместе), новых не получает: считались бы только
//   новые — идемпотентность f(f(x)) == f(x) ломалась бы на строке с четвёртой целью.
// - CRLF-текст сохраняется: строка без правок выходит побайтно равной входу (с \r),
//   строка с правкой меняет только интервалы совпадений, \r на конце остаётся.
public static class WikiAutoLink
{
    /// <summary>Минимальная длина однословного заголовка, символов.</summary>
    public const int MinSingleWordTitleChars = 6;

    /// <summary>Потолок ссылок на строке: существующие + новые (см. комментарий к правилу).</summary>
    public const int MaxLinksPerLine = 3;

    // Секции, текст которых не автосвязывается: «История» несёт перенесённые утверждения
    // (запись прошлого), «Заметки» — текст человека, код его не переписывает (ADR-038).
    private static readonly HashSet<string> UnlinkedSections = new(StringComparer.Ordinal)
    {
        "История",
        WikiPage.NotesSectionName,
    };

    private static readonly Regex SectionRx = new(@"^## (.+)$", RegexOptions.CultureInvariant);
    private static readonly Regex FootnoteDefRx = new(@"^\[\^([^]\r\n]+)\]: ", RegexOptions.CultureInvariant);
    // Ссылка сноски [^…] — тот же образец, что у парсера страницы (WikiPage.FootnoteRefRx);
    // здесь она только защищённый интервал. Копия обязательна: приватное поле чужого класса
    // валидатор согласия ссылок (Q1-M2) не видит.
    private static readonly Regex FootnoteRefRx = new(@"\[\^([^]\r\n]+)\]", RegexOptions.CultureInvariant);

    public readonly record struct Result(string Text, int Added, int MissedWordForms);

    // Род совпадения: точное вхождение заголовка (единственное, что линкуется), словоформа
    // заголовка и точное вхождение алиаса (оба только считаются).
    private enum MatchKind
    {
        TitleExact,
        TitleForm,
        AliasExact,
    }

    public static Result Apply(
        string text,
        string selfPath,
        string sectionKey,
        IReadOnlyList<string> chain,
        IReadOnlyList<(string Section, WikiPage Page, string Path)> snapshot)
    {
        if (text.Length == 0)
            return new Result(text, 0, 0);
        var targets = BuildTargets(snapshot, selfPath, sectionKey, chain);
        if (targets.Count == 0)
            return new Result(text, 0, 0);

        var codeMask = NotesService.CodeMask(text);
        // Защищённые интервалы всего текста: уже стоящие [[…]] и ссылки сносок [^…] (цель
        // сноски — карточка source, её не линкуем; текст внутри — не текст для совпадения).
        var protectedSpans = new List<(int Start, int End)>();
        // Цели, уже связанные ссылкой где угодно в тексте (в т.ч. в нелинкуемых секциях):
        // «одна цель — одна ссылка на текст» считается по документу, её словоформы и алиасы
        // потенциальной связи не добавляют.
        var alreadyLinked = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in WikiPage.LinkRx.Matches(text))
        {
            if (NotesService.InCode(codeMask, m.Index))
                continue; // ссылка в коде — не ссылка (тот же разбор, что у парсера страницы)
            protectedSpans.Add((m.Index, m.Index + m.Length));
            var inner = m.Groups[1].Value;
            var bar = inner.IndexOf('|');
            if (bar >= 0)
                inner = inner[..bar];
            var slash = inner.LastIndexOf('/');
            if (slash >= 0)
                inner = inner[(slash + 1)..];
            alreadyLinked.Add(Normalize(inner));
        }
        foreach (Match m in FootnoteRefRx.Matches(text))
            protectedSpans.Add((m.Index, m.Index + m.Length));

        var lines = text.Split('\n');
        var rendered = new string[lines.Length];
        // Цели с точным вхождением заголовка в линкуемом тексте: их словоформы связи не
        // добавляют — полная реализация поставила бы ровно одну ссылку, она уже есть.
        var hadExact = new HashSet<string>(StringComparer.Ordinal);
        // Цели, к которым в линкуемом тексте есть только словоформа или алиас: кандидаты
        // в замер; считаются те, у которых нет точного вхождения (ниже).
        var missedCandidates = new HashSet<string>(StringComparer.Ordinal);
        var linkedTitles = new HashSet<string>(StringComparer.Ordinal);
        var currentSection = (string?)null;
        var offset = 0;
        var added = 0;
        for (var li = 0; li < lines.Length; li++)
        {
            var rawLine = lines[li];
            var line = rawLine.TrimEnd('\r');
            var baseOffset = offset;
            offset += rawLine.Length + 1;

            var sectionMatch = SectionRx.Match(line);
            if (sectionMatch.Success)
            {
                currentSection = sectionMatch.Groups[1].Value;
                rendered[li] = rawLine;
                continue;
            }
            if (line.Length == 0 || line[0] == '#' || FootnoteDefRx.IsMatch(line)
                || currentSection is { } sec && UnlinkedSections.Contains(sec))
            {
                rendered[li] = rawLine;
                continue;
            }
            // Нормализация с сохранением индексов; NFC, изменивший длину строки
            // (разложенные Combining-последовательности), — строка вне автолинкования.
            var norm = NormalizeKeepIndexes(line);
            if (norm is null)
            {
                rendered[li] = rawLine;
                continue;
            }

            var words = WordSpans(norm);
            // Основы слов строки — те же, что у значимых слов: сравнение идёт по основам
            // с обеих сторон (полное слово строки и основа заголовка — разные строки).
            var wordStems = new List<(string Text, string Stem)>(words.Count);
            foreach (var (start, end) in words)
            {
                var wordText = norm[start..end];
                wordStems.Add((wordText, WikiCandidates.StemOf(wordText)));
            }
            // Занятые интервалы строки: защищённые и точные совпадения этого прогона
            // (цели идут от длинного к короткому — занимает длинное).
            var claimed = new List<(int Start, int End)>();
            var lineEdits = new List<(int Start, int End, string Insert)>();
            var lineLinks = CountLinks(line);
            foreach (var target in targets)
            {
                foreach (var (start, end, kind) in MatchesOf(target, norm, words, wordStems))
                {
                    var gStart = baseOffset + start;
                    var gEnd = baseOffset + end;
                    if (NotesService.InCode(codeMask, gStart))
                        continue; // в коде нет ни ссылок, ни счёта
                    if (kind == MatchKind.TitleExact)
                        hadExact.Add(target.NormTitle);
                    if (OverlapsAny(protectedSpans, gStart, gEnd) || OverlapsAny(claimed, start, end))
                        continue; // внутри существующей ссылки/сноски или более длинного совпадения
                    if (norm.AsSpan(start, end - start).IndexOfAnyInRange('0', '9') >= 0)
                        continue; // интервал с цифрой не линкуется и не считается
                                    // (цифра внутри слова — часть слова, «qwen3» ≠ «qwen»)
                    if (kind != MatchKind.TitleForm)
                        claimed.Add((start, end));
                    if (kind == MatchKind.TitleExact)
                    {
                        if (!target.Accepted || linkedTitles.Contains(target.NormTitle)
                            || alreadyLinked.Contains(target.NormTitle))
                            continue; // цель уже связана ссылкой — в тексте или этим прогоном
                        if (lineLinks >= MaxLinksPerLine)
                            continue;
                        lineEdits.Add((start, end, $"[[{target.Title}]]"));
                        linkedTitles.Add(target.NormTitle);
                        lineLinks++;
                        added++;
                    }
                    else if (target.Accepted)
                    {
                        missedCandidates.Add(target.NormTitle);
                    }
                }
            }
            lineEdits.Sort((a, b) => a.Start.CompareTo(b.Start));
            rendered[li] = ApplyEdits(rawLine, line, lineEdits);
        }

        var missed = 0;
        foreach (var target in targets)
            if (target.Accepted && missedCandidates.Contains(target.NormTitle)
                && !hadExact.Contains(target.NormTitle) && !alreadyLinked.Contains(target.NormTitle))
                missed++;
        return new Result(string.Join('\n', rendered), added, missed);
    }

    // ─── цели ───

    private sealed class Target
    {
        public required string Title;
        public required string NormTitle;
        public required string[] Words;   // нормализованные слова заголовка
        public required string[] Stems;   // те же основы (WikiCandidates.StemOf)
        public required List<(string[] Words, string[] Stems)> Aliases;
        public bool Accepted;
    }

    // Цели — заголовки страниц снимка (алиасы — только для счёта). Порядок: самое длинное
    // совпадение первым (по сумме букв заголовка, затем по числу слов). Дубль нормализованного
    // имени — одна цель (первая). Accepted — ResolveLink даёт Resolved ровно в раздел цели:
    // боковая цель остаётся в списке ради счёта словоформ, но ссылки не получает.
    private static List<Target> BuildTargets(
        IReadOnlyList<(string Section, WikiPage Page, string Path)> snapshot,
        string selfPath,
        string sectionKey,
        IReadOnlyList<string> chain)
    {
        var targets = new List<Target>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (section, page, path) in snapshot)
        {
            var type = page.Frontmatter.Type;
            if (type == WikiFrontmatter.TypeSource || type == WikiFrontmatter.TypeChronicle)
                continue;
            if (path == selfPath || page.Title.StartsWith('_'))
                continue;
            var normTitle = Normalize(page.Title);
            if (normTitle.Length == 0 || !seen.Add(normTitle))
                continue;
            var words = WordsOf(normTitle);
            if (words.Length == 0)
                continue;
            if (words.Length == 1 &&
                (words[0].Length < MinSingleWordTitleChars || WikiCandidates.IsStopWord(words[0])))
                continue;
            var aliases = new List<(string[], string[])>();
            foreach (var alias in page.Frontmatter.Aliases)
            {
                var aw = WordsOf(Normalize(alias));
                if (aw.Length > 0)
                    aliases.Add((aw, [.. aw.Select(WikiCandidates.StemOf)]));
            }
            var res = WikiGate.ResolveLink(page.Title, sectionKey, chain, snapshot);
            targets.Add(new Target
            {
                Title = page.Title,
                NormTitle = normTitle,
                Words = words,
                Stems = [.. words.Select(WikiCandidates.StemOf)],
                Aliases = aliases,
                Accepted = res.Outcome == WikiGate.LinkOutcome.Resolved && res.Section == section,
            });
        }
        return [.. targets.OrderByDescending(t => t.Words.Sum(w => w.Length))
            .ThenByDescending(t => t.Words.Length)
            .ThenBy(t => t.NormTitle, StringComparer.Ordinal)];
    }

    // ─── совпадение на строке ───

    // Все вхождения цели в строке. Заголовок: слово за словом по основе; слова — подряд
    // идущие последовательности букв и цифр строки (каждая — полное слово; в промежутке
    // между словами цели — только пробелы: «очередь. задачи» — не вхождение фразы, интервал
    // замены не должен есть чужую пунктуацию). Точное
    // совпадение всех слов — TitleExact, иначе словоформа. Алиас: только точное вхождение
    // (его в A2 не линкуем, только считаем).
    private static IEnumerable<(int Start, int End, MatchKind Kind)> MatchesOf(
        Target target, string norm, List<(int Start, int End)> words, List<(string Text, string Stem)> wordStems)
    {
        var n = target.Words.Length;
        for (var i = 0; i + n <= words.Count; i++)
        {
            if (!GapsAreWhitespace(norm, words, i, n))
                continue; // между словами цели — не-пробел: разные фразы, не вхождение
            var exact = true;
            for (var k = 0; k < n; k++)
            {
                if (wordStems[i + k].Stem != target.Stems[k])
                    break;
                if (wordStems[i + k].Text != target.Words[k])
                    exact = false;
                if (k == n - 1)
                    yield return (words[i].Start, words[i + n - 1].End,
                        exact ? MatchKind.TitleExact : MatchKind.TitleForm);
            }
        }
        foreach (var (aw, aStems) in target.Aliases)
        {
            var m = aw.Length;
            for (var i = 0; i + m <= words.Count; i++)
            {
                if (!GapsAreWhitespace(norm, words, i, m))
                    continue; // та же граница фразы, что у заголовка
                var ok = true;
                for (var k = 0; k < m; k++)
                {
                    if (wordStems[i + k].Text != aw[k])
                    {
                        ok = false;
                        break;
                    }
                }
                if (ok)
                    yield return (words[i].Start, words[i + m - 1].End, MatchKind.AliasExact);
            }
        }
    }

    // Слова строки: полные последовательности букв и цифр (цифра внутри слова — часть
    // слова: заголовок «Qwen3 модель» не матчится на «qwen модель»; «2026» — слово-токен,
    // поэтому между словами заголовка не окажется цифра). Промежуток между соседними
    // словами — непустой по построению (максимальные последовательности).
    private static List<(int Start, int End)> WordSpans(string norm)
    {
        var spans = new List<(int Start, int End)>();
        var start = -1;
        for (var i = 0; i < norm.Length; i++)
        {
            if (char.IsLetter(norm[i]) || char.IsDigit(norm[i]))
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

    // Промежутки между словами кандидата i..i+count-1: только пробельные символы. Пустой
    // промежуток невозможен (слова максимальны), но и он не считается пробелом.
    private static bool GapsAreWhitespace(string norm, List<(int Start, int End)> words, int i, int count)
    {
        for (var k = 0; k < count - 1; k++)
        {
            for (var p = words[i + k].End; p < words[i + k + 1].Start; p++)
                if (!char.IsWhiteSpace(norm[p]))
                    return false;
        }
        return true;
    }

    // ─── строки и интервалы ───

    // Строка с правками: нормализованная копия (line) до первого правого края, затем
    // хвост rawLine — с \r на конце (CRLF-текст меняется только там, где стоит ссылка;
    // при Added=0 вывод побайтно равен входу).
    private static string ApplyEdits(string rawLine, string line, List<(int Start, int End, string Insert)> edits)
    {
        if (edits.Count == 0)
            return rawLine;
        var sb = new StringBuilder(rawLine.Length + 64);
        var pos = 0;
        foreach (var (start, end, insert) in edits) // добавлены по возрастанию start
        {
            sb.Append(line, pos, start - pos).Append(insert);
            pos = end;
        }
        sb.Append(line, pos, line.Length - pos);
        var tail = rawLine.Length - line.Length; // \r строки CRLF
        if (tail > 0)
            sb.Append(rawLine, line.Length, tail);
        return sb.ToString();
    }

    private static bool OverlapsAny(List<(int Start, int End)> spans, int start, int end)
    {
        foreach (var (s, e) in spans)
            if (start < e && s < end)
                return true;
        return false;
    }

    private static int CountLinks(string line)
    {
        var count = 0;
        foreach (Match m in WikiPage.LinkRx.Matches(line))
            count++;
        return count;
    }

    // ─── нормализация ───

    // Нормализация для сравнения: та же, что у значимых слов (NFC, нижний регистр, ё→е).
    private static string Normalize(string text) =>
        text.Trim().Normalize(NormalizationForm.FormC).ToLowerInvariant().Replace('ё', 'е');

    // Та же нормализация посимвольно — с сохранением индексов исходной строки (замена
    // односимвольная, длина не меняется). Строка, где NFC меняет длину (разложенные
    // Combining-последовательности), — null: карта индексов рвётся, в формате страницы
    // таких строк нет.
    private static string? NormalizeKeepIndexes(string line)
    {
        if (line.Normalize(NormalizationForm.FormC).Length != line.Length)
            return null;
        var sb = new StringBuilder(line.Length);
        foreach (var ch in line)
        {
            var low = char.ToLowerInvariant(ch);
            sb.Append(low == 'ё' ? 'е' : low);
        }
        return sb.ToString();
    }

    private static string[] WordsOf(string normText)
    {
        var words = new List<string>();
        foreach (var (start, end) in WordSpans(normText))
            words.Add(normText[start..end]);
        return [.. words];
    }
}
