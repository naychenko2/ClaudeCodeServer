namespace ClaudeHomeServer.HandsBridge.Scanning.Text;

/// <summary>
/// Параметры склейки текста соседних страниц.
/// </summary>
public class StitchOptions
{
    /// <summary>Максимальная длина перекрытия (в строках), которую мы ищем в хвосте <c>existing</c>.</summary>
    public int MaxOverlapLines { get; init; } = 40;

    /// <summary>
    /// Минимум значимых (якорных) строк в перекрытии, чтобы шов считался надёжным.
    /// </summary>
    public int MinAnchorLines { get; init; } = 2;

    /// <summary>
    /// Порог нечёткого сравнения: строки совпадают, если нормированное расстояние Левенштейна
    /// ≤ этому значению (доля от длины) — ловит OCR-опечатки.
    /// </summary>
    public double FuzzyThreshold { get; init; } = 0.15;

    /// <summary>Игнорировать знаки препинания при сравнении (по умолчанию нет — регистр сохраняем).</summary>
    public bool IgnorePunctuation { get; init; }
}

/// <summary>
/// Результат склейки: <see cref="Text"/> — итоговый текст, <see cref="OverlapLines"/> — сколько
/// строк перекрытия было найдено (0 — перекрытия нет), <see cref="Doubtful"/> — шов сомнительный
/// (вставлен маркер <c>&lt;!-- шов: проверить --&gt;</c>).
/// </summary>
public readonly record struct StitchResult(string Text, int OverlapLines, bool Doubtful);

/// <summary>
/// Склейка текста соседних страниц: конец страницы N и начало страницы N+1 частично
/// перекрываются (листаем PageDown, строки повторяются). Ищем самое длинное совпадение хвоста
/// <paramref name="existing"/> (последние до <c>MaxOverlapLines</c> строк) с головой
/// <paramref name="next"/> по строкам с нечётким сравнением и склеиваем без дублей.
///
/// Окно — только на последние <c>MaxOverlapLines</c> строк <paramref name="existing"/>, поэтому
/// сложность не O(n²) на всём документе. Строки таблиц Markdown (начинаются с <c>|</c>)
/// сравниваются как обычные строки.
/// </summary>
public static class DocumentStitcher
{
    /// <summary>Маркер сомнительного шва, вставляется на отдельной строке в месте склейки.</summary>
    public const string DoubtfulMarker = "<!-- шов: проверить -->";

    /// <summary>
    /// Склеивает <paramref name="existing"/> (страница N) и <paramref name="next"/> (страница N+1).
    /// Возвращает итоговый текст, длину найденного перекрытия и признак сомнительности шва.
    /// </summary>
    public static StitchResult Append(string existing, string next, StitchOptions? options = null)
    {
        options ??= new StitchOptions();

        var existingLines = SplitLines(existing);
        var nextLines = SplitLines(next);

        var maxK = Math.Min(options.MaxOverlapLines, Math.Min(existingLines.Count, nextLines.Count));

        // Самое длинное валидное перекрытие: все строки пары совместимы (нет Mismatch).
        var bestK = 0;
        var bestAnchors = 0;
        var validCount = 0; // сколько разных длин k валидно — «несколько равноценных вариантов»

        for (var k = maxK; k >= 1; k--)
        {
            var (compatible, anchors) = CheckOverlap(existingLines, nextLines, k, options);
            if (!compatible)
                continue;

            validCount++;
            if (k > bestK)
            {
                bestK = k;
                bestAnchors = anchors;
            }
        }

        var doubtful = false;
        string text;

        if (bestK == 0)
        {
            // Перекрытия нет: присоединяем через пустую строку и помечаем шов.
            doubtful = true;
            text = JoinWithBlank(existingLines, nextLines);
        }
        else
        {
            // Перекрытие найдено. Надёжный шов — при достатке якорей И единственной валидной длине.
            if (bestAnchors < options.MinAnchorLines || validCount > 1)
                doubtful = true;

            // Итог = весь existing (с перекрытием) + маркер (если сомнительно) + new-строки next.
            // Перекрытие остаётся ОДИН раз (из existing), дубля нет: next без первых bestK строк.
            var tail = nextLines.Skip(bestK).ToList();
            text = JoinStitched(existingLines, tail, doubtful);
        }

        return new StitchResult(text, bestK, doubtful);
    }

    /// <summary>
    /// Проверяет перекрытие длины <paramref name="k"/>: совпадают ли последние <paramref name="k"/>
    /// строк <paramref name="existing"/> с первыми <paramref name="k"/> строками <paramref name="next"/>.
    /// Возвращает (все совместимы, число якорных строк).
    /// </summary>
    private static (bool Compatible, int Anchors) CheckOverlap(
        List<string> existing,
        List<string> next,
        int k,
        StitchOptions options)
    {
        var anchors = 0;
        for (var i = 0; i < k; i++)
        {
            var a = existing[existing.Count - k + i];
            var b = next[i];
            var kind = ClassifyLine(a, b, options);
            if (kind == LineKind.Mismatch)
                return (false, anchors);
            if (kind == LineKind.Anchor)
                anchors++;
        }

        return (true, anchors);
    }

    /// <summary>Вид пары строк: совпадают ли и считается ли строка якорем.</summary>
    private enum LineKind
    {
        /// <summary>Обе пустые: совместимы, но якорем не считаются.</summary>
        Blank,
        /// <summary>Совпали (нечётко): совместимы и считаются якорем.</summary>
        Anchor,
        /// <summary>Не совпали: перекрытие на этой строке рвётся.</summary>
        Mismatch,
    }

    private static LineKind ClassifyLine(string a, string b, StitchOptions options)
    {
        var na = Normalize(a, options);
        var nb = Normalize(b, options);

        // Пустые строки: совпадают, но якорем не считаются.
        if (na.Length == 0 && nb.Length == 0)
            return LineKind.Blank;

        // Одна пустая, другая нет — несовпадение.
        if (na.Length == 0 || nb.Length == 0)
            return LineKind.Mismatch;

        var maxLen = Math.Max(na.Length, nb.Length);
        if (maxLen < 3)
            return LineKind.Mismatch;

        var distance = Levenshtein(na, nb);
        return distance <= options.FuzzyThreshold * maxLen ? LineKind.Anchor : LineKind.Mismatch;
    }

    /// <summary>
    /// Нормализация строки для сравнения: обрезка пробелов по краям, сжатие внутренних пробельных
    /// символов в один пробел; регистр сохраняем; опционально — убрать знаки препинания.
    /// </summary>
    private static string Normalize(string line, StitchOptions options)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0)
            return string.Empty;

        // Сжимаем пробельные символы (включая табы) в один пробел.
        var collapsed = CollapseWhitespace(trimmed);

        if (!options.IgnorePunctuation)
            return collapsed;

        return new string(collapsed.Where(c => !char.IsPunctuation(c)).ToArray()).Trim();
    }

    private static string CollapseWhitespace(string s)
    {
        var result = new System.Text.StringBuilder(s.Length);
        var inSpace = false;
        foreach (var c in s)
        {
            if (char.IsWhiteSpace(c))
            {
                if (!inSpace && result.Length > 0)
                    result.Append(' ');
                inSpace = true;
            }
            else
            {
                result.Append(c);
                inSpace = false;
            }
        }

        return result.ToString();
    }

    /// <summary>
    /// Расстояние Левенштейна (число вставок/удалений/замен). Для длинных строк достаточно
    /// грубой оценки, но точность важнее скорости: строки короткие (строки документа).
    /// </summary>
    private static int Levenshtein(string a, string b)
    {
        if (a.Length == 0)
            return b.Length;
        if (b.Length == 0)
            return a.Length;

        var prev = new int[b.Length + 1];
        var curr = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
            prev[j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            curr[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                curr[j] = Math.Min(
                    Math.Min(prev[j] + 1, curr[j - 1] + 1),
                    prev[j - 1] + cost);
            }

            (prev, curr) = (curr, prev);
        }

        return prev[b.Length];
    }

    /// <summary>Разбивает текст на строки по <c>\n</c>, обрезая хвостовой <c>\r</c> (CRLF/LF).</summary>
    private static List<string> SplitLines(string text)
    {
        var lines = new List<string>();
        if (string.IsNullOrEmpty(text))
        {
            lines.Add(string.Empty);
            return lines;
        }

        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                var line = text[start..i];
                if (line.EndsWith('\r'))
                    line = line[..^1];
                lines.Add(line);
                start = i + 1;
            }
        }

        var last = text[start..];
        if (last.EndsWith('\r'))
            last = last[..^1];
        lines.Add(last);

        return lines;
    }

    /// <summary>Склеивает head и tail через пустую строку (случай «перекрытия нет»): страницы
    /// разделяются пустым рядом, а в месте шва стоит маркер.</summary>
    private static string JoinWithBlank(List<string> head, List<string> tail)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var line in head)
            sb.Append(line).Append('\n');

        sb.Append('\n');
        sb.Append(DoubtfulMarker).Append('\n');

        foreach (var line in tail)
            sb.Append(line).Append('\n');

        return sb.ToString().TrimEnd('\n');
    }

    /// <summary>Склеивает весь <paramref name="existing"/> (с перекрытием) и <paramref name="tail"/>
    /// (new-строки next), вставляя маркер шва, если шов сомнительный.</summary>
    private static string JoinStitched(List<string> existing, List<string> tail, bool doubtful)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var line in existing)
            sb.Append(line).Append('\n');

        if (doubtful)
            sb.Append(DoubtfulMarker).Append('\n');

        foreach (var line in tail)
            sb.Append(line).Append('\n');

        return sb.ToString().TrimEnd('\n');
    }
}
