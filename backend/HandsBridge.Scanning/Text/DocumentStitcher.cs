namespace ClaudeHomeServer.HandsBridge.Scanning.Text;

/// <summary>
/// Параметры склейки текста соседних страниц.
/// </summary>
public class StitchOptions
{
    /// <summary>
    /// Максимальная длина перекрытия (в строках), которую мы ищем в хвосте <c>existing</c>.
    /// Поиск идёт от этой длины вниз. Если лучшее совпадение упёрлось в эту границу
    /// (лучший k == MaxOverlapLines), поиск автоматически повторяется с удвоенным окном,
    /// пока не найдётся валидный k или не будет достигнут <see cref="AbsoluteMaxOverlapLines"/>.
    /// Это покрывает случай, когда реальное перекрытие больше окна (скан книги с большим overlap).
    /// </summary>
    public int MaxOverlapLines { get; init; } = 40;

    /// <summary>
    /// Абсолютный потолок длины перекрытия: окно удваивается (MaxOverlapLines → 2× → 4× → …)
    /// до этого значения. По умолчанию 400.
    /// </summary>
    public int AbsoluteMaxOverlapLines { get; init; } = 400;

    /// <summary>
    /// Минимум значимых (якорных) строк в перекрытии, чтобы шов считался надёжным.
    /// Якорная строка — та, что совпала ТОЧНО (raw, без учёта нормализации цифр): настоящий
    /// повтор контента. Строка, совпавшая только после замены цифр на плейсхолдер <c>#</c>
    /// (raw различаются — «Page 1» vs «Page 2», разные строки таблицы), якорем НЕ считается:
    /// это меняющийся колонтитул/номер или разные данные, и сам по себе такой шов ненадёжен.
    /// </summary>
    public int MinAnchorLines { get; init; } = 2;

    /// <summary>
    /// Порог нечёткого сравнения: строки совпадают, если нормированное расстояние Левенштейна
    /// ≤ этому значению (доля от длины) — ловит OCR-опечатки.
    /// </summary>
    public double FuzzyThreshold { get; init; } = 0.15;

    /// <summary>Игнорировать знаки препинания при сравнении (по умолчанию нет — регистр сохраняем).</summary>
    public bool IgnorePunctuation { get; init; }

    /// <summary>
    /// Сколько строк из перекрытия (k) могут не совпасть, при условии что совпавших значимых
    /// (якорных) ≥ <see cref="MinAnchorLines"/>. По умолчанию 2: это покрывает меняющиеся
    /// колонтитулы/номера страниц на краях перекрытия.
    /// </summary>
    public int MaxMismatchedLines { get; init; } = 2;

    /// <summary>
    /// Ширина «краёв» перекрытия (в строках с каждого края): на краях допускается несовпадение
    /// (меняющиеся колонтитулы/номера), а в ядре — нет. Также задаёт, на сколько строк вперёд
    /// в <paramref name="next"/> сдвигать поиск overlap (шапка/колонтитул новой страницы перед ним).
    /// </summary>
    public int TolerantEdgeLines { get; init; } = 3;
}

/// <summary>
/// Результат склейки: <see cref="Text"/> — итоговый текст, <see cref="OverlapLines"/> — сколько
/// строк перекрытия было найдено (0 — перекрытия нет), <see cref="Doubtful"/> — шов сомнительный
/// (вставлен маркер <c>&lt;!-- шов: проверить --&gt;</c>), <see cref="OverlapExceedsWindow"/> —
/// перекрытие упёрлось в границу окна <c>MaxOverlapLines</c> (реальное перекрытие, вероятно, больше).
/// </summary>
public readonly record struct StitchResult(string Text, int OverlapLines, bool Doubtful, bool OverlapExceedsWindow);

/// <summary>
/// Склейка текста соседних страниц: конец страницы N и начало страницы N+1 частично
/// перекрываются (листаем PageDown, строки повторяются). Ищем самое длинное совпадение хвоста
/// <paramref name="existing"/> (последние до <c>MaxOverlapLines</c> строк) с головой
/// <paramref name="next"/> по строкам с нечётким сравнением и склеиваем без дублей.
///
/// Сравнение строк устойчиво к цифрам: для СРАВНЕНИЯ любая последовательность цифр заменяется
/// на плейсхолдер <c>#</c>, поэтому «Page 1» и «Page 2», «Страница 3 из 16» и «Страница 4 из 16»,
/// «15.10.2026» и «16.10.2026» совпадают. Но якорем (надёжной строкой) считается только точное
/// совпадение (raw): строки, совпавшие лишь после нормализации цифр (меняющийся колонтитул/номер
/// или разные строки таблицы), совпадение дают, но якорем не считаются — иначе «Page 1 / Page 2»
/// или две разные строки таблицы дали бы ложный шов.
///
/// Окно — только на последние <c>MaxOverlapLines</c> строк <paramref name="existing"/>, поэтому
/// сложность не O(n²) на всём документе. Если лучшее совпадение упёрлось в границу окна, поиск
/// повторяется с удвоенным окном (до <c>AbsoluteMaxOverlapLines</c>). Строки таблиц Markdown
/// (начинаются с <c>|</c>) сравниваются как обычные строки.
/// </summary>
public static class DocumentStitcher
{
    /// <summary>Маркер сомнительного шва, вставляется на отдельной строке в месте склейки.</summary>
    public const string DoubtfulMarker = "<!-- шов: проверить -->";

    /// <summary>
    /// Склеивает <paramref name="existing"/> (страница N) и <paramref name="next"/> (страница N+1).
    /// Возвращает итоговый текст, длину найденного перекрытия, признак сомнительности шва и признак
    /// того, что перекрытие упёрлось в границу окна.
    /// </summary>
    public static StitchResult Append(string existing, string next, StitchOptions? options = null)
    {
        options ??= new StitchOptions();

        var existingLines = SplitLines(existing);
        var nextLines = SplitLines(next);

        var maxK = Math.Min(options.MaxOverlapLines, Math.Min(existingLines.Count, nextLines.Count));
        var fullOverlap = Math.Min(existingLines.Count, nextLines.Count);

        // Фазовый поиск: сначала с окном maxK; пока лучшее совпадение упёрлось в границу окна
        // (exceedsWindow) и окно ещё не достигло AbsoluteMaxOverlapLines — удваиваем окно и ищем заново.
        var (bestK, bestAnchors, validCount, bestOffset, exceedsWindow) = SearchWindow(existingLines, nextLines, maxK, options);

        while (exceedsWindow && maxK < options.AbsoluteMaxOverlapLines)
        {
            var newMaxK = Math.Min(maxK * 2, Math.Min(options.AbsoluteMaxOverlapLines, fullOverlap));
            if (newMaxK <= maxK)
                break; // окно не растёт: достигли потолка AbsoluteMaxOverlapLines
            // Окно только расширяется, поэтому лучшее совпадение монотонно не ухудшается:
            // берём результат целиком (включает и прежние, и новые более длинные перекрытия).
            (bestK, bestAnchors, validCount, bestOffset, exceedsWindow) = SearchWindow(existingLines, nextLines, newMaxK, options);
            maxK = newMaxK;
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

            // Якорь по середине: overlap — это bestK строк, в next он начинается со смещения bestOffset
            // (до него могут идти head-строки next, например колонтитул/шапка новой страницы).
            // Итог = весь existing + head-строки next (до overlap) + строки next после overlap.
            // Перекрытие остаётся ОДИН раз (из existing), дубля нет.
            var head = nextLines.Take(bestOffset).ToList();
            var tail = nextLines.Skip(bestOffset + bestK).ToList();
            text = JoinStitched(existingLines, head, tail, doubtful);
        }

        return new StitchResult(text, bestK, doubtful, exceedsWindow);
    }

    /// <summary>
    /// Ищет валидное перекрытие в окне до <paramref name="maxK"/> строк. Возвращает лучшее k,
    /// число якорей, число валидных длин, смещение overlap в <paramref name="next"/> и признак того,
    /// что лучшее совпадение упёрлось в границу окна.
    /// </summary>
    private static (int, int, int, int, bool) SearchWindow(
        List<string> existing,
        List<string> next,
        int maxK,
        StitchOptions options)
    {
        var bestK = 0;
        var bestAnchors = 0;
        var bestEdge = int.MaxValue;
        var bestOffset = 0;

        // Для каждого смещения off (0..TolerantEdgeLines) ищем крупнейший k, при котором
        // хвост existing длины k совпадает с next[off..off+k-1]. Колонтитул/шапка новой страницы
        // может стоять перед overlap в next, поэтому off > 0.
        // Среди смещений выбираем лучшее по: больше k → больше якорей → меньше edge-mismatch →
        // меньший off. Это «якорь по середине»: overlap может быть смещён в next (шапка/колонтитул
        // перед ним), и мы берём самое чистое и самое длинное совпадение, а не первое попавшееся.
        var maxOffset = Math.Min(options.TolerantEdgeLines, Math.Max(0, next.Count - 1));
        var perOffsetK = new int[maxOffset + 1];
        for (var off = 0; off <= maxOffset; off++)
        {
            var maxKForOff = Math.Min(maxK, Math.Min(existing.Count, next.Count - off));
            var offBestK = 0;
            var offBestAnchors = 0;
            var offBestEdge = 0;
            for (var k = maxKForOff; k >= 1; k--)
            {
                var (compatible, anchors, edge) = CheckOverlapAt(existing, next, k, off, options);
                if (!compatible)
                    continue;
                offBestK = k;
                offBestAnchors = anchors;
                offBestEdge = edge;
                break; // крупнейший k для данного off
            }

            perOffsetK[off] = offBestK;
            if (offBestK == 0)
                continue;

            // Лучшее смещение: больше k, при равенстве — больше якорей, затем меньше edge-mismatch,
            // затем меньший off.
            if (offBestK > bestK
                || (offBestK == bestK && offBestAnchors > bestAnchors)
                || (offBestK == bestK && offBestAnchors == bestAnchors && offBestEdge < bestEdge)
                || (offBestK == bestK && offBestAnchors == bestAnchors && offBestEdge == bestEdge && off < bestOffset))
            {
                bestK = offBestK;
                bestAnchors = offBestAnchors;
                bestOffset = off;
                bestEdge = offBestEdge;
            }
        }

        // Неоднозначность: сколько смещений дали ТОТ ЖЕ лучший k? Вложенные выравнивания
        // (off=0 k=4 и off=1 k=3) — это одно и то же перекрытие в разных сдвигах, а не два
        // разных шва, поэтому в сомнительность не входят: учитываем только равные по длине.
        var validCount = 0;
        for (var off = 0; off <= maxOffset; off++)
        {
            if (bestK > 0 && perOffsetK[off] == bestK)
                validCount++;
        }

        // Упёрлись в границу окна в двух случаях:
        //  (а) лучшее совпадение == maxK (и maxK < полного перекрытия) — нашли валидный k на границе;
        //  (б) ни одно смещение не дало валидного k (validCount == 0), но окно короче полного
        //      перекрытия — реальное перекрытие длиннее окна, и ни одна длина в [1..maxK] не совпала.
        // Во втором случае Append удваивает окно и ищет заново.
        var fullOverlap = Math.Min(existing.Count, next.Count);
        var exceedsWindow = maxK < fullOverlap
            && (bestK == maxK || validCount == 0);

        return (bestK, bestAnchors, validCount, bestOffset, exceedsWindow);
    }

    /// <summary>
    /// Проверяет перекрытие длины <paramref name="k"/> со смещением <paramref name="offset"/>:
    /// совпадают ли последние <paramref name="k"/> строк <paramref name="existing"/> с
    /// <paramref name="next"/>[<paramref name="offset"/>..<paramref name="offset"/>+<paramref name="k"/>].
    /// Мismatch допустим только на КРАЯХ перекрытия (первые/последние <c>TolerantEdgeLines</c> строк)
    /// и в сумме ≤ <c>MaxMismatchedLines</c> — это меняющиеся колонтитулы/номера. В ЯДРЕ перекрытия
    /// mismatch быть не должно (иначе это не перекрытие, а разные строки). Якорных строк должно быть ≥ 1.
    /// Надёжность (≥ <c>MinAnchorLines</c>) — отдельная проверка в <c>Append</c>.
    /// </summary>
    private static (bool Compatible, int Anchors, int EdgeMismatches) CheckOverlapAt(
        List<string> existing,
        List<string> next,
        int k,
        int offset,
        StitchOptions options)
    {
        var anchors = 0;
        var edgeMismatches = 0;
        var coreMismatches = 0;
        var tailStart = existing.Count - k;
        // Ширина «краёв» (зоны допустимого mismatch): до TolerantEdgeLines строк с каждого края,
        // но не больше, чтобы в ядре оставалась хотя бы одна строка (иначе всё — «край»).
        var edgeSize = Math.Min(options.TolerantEdgeLines, (k - 1) / 2);
        for (var i = 0; i < k; i++)
        {
            var a = existing[tailStart + i];
            var b = next[offset + i];
            var kind = ClassifyLine(a, b, options);
            if (kind == LineKind.Anchor)
            {
                anchors++;
            }
            else if (kind == LineKind.Mismatch)
            {
                // Краевые строки (первые/последние edgeSize) могут не совпадать —
                // это колонтитулы/номера. Ядровые — нет.
                var isEdge = i < edgeSize || i >= k - edgeSize;
                if (isEdge)
                    edgeMismatches++;
                else
                    coreMismatches++;
            }
            // Trivial (колонтитул/номер): совпадает, но якорем не считается; mismatch'ом не является.
        }

        var compatible = coreMismatches == 0
            && edgeMismatches <= options.MaxMismatchedLines
            && anchors >= 1;
        return (compatible, anchors, edgeMismatches);
    }

    /// <summary>Вид пары строк: совпадают ли и считается ли строка якорем.</summary>
    private enum LineKind
    {
        /// <summary>Обе пустые: совместимы, но якорем не считаются.</summary>
        Blank,
        /// <summary>Совпали (нечётко) и строка значимая: совместимы и считаются якорем.</summary>
        Anchor,
        /// <summary>
        /// Совпали, но различаются ТОЛЬКО в цифрах (после замены цифр на # совпадают, raw — нет):
        /// меняющийся колонтитул/номер страницы или разные строки таблицы. Совместимы, но якорем
        /// не считаются, чтобы не дать ложный шов.
        /// </summary>
        Trivial,
        /// <summary>Не совпали: несовпадение на этой строке.</summary>
        Mismatch,
    }

    private static LineKind ClassifyLine(string a, string b, StitchOptions options)
    {
        var na = Normalize(a, options);
        var nb = Normalize(b, options);
        var ra = RawNormalize(a, options);
        var rb = RawNormalize(b, options);

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
        if (distance > options.FuzzyThreshold * maxLen)
            return LineKind.Mismatch;

        // Совпали (точно или нечётко). Якорем НЕ считается, если строки различаются ТОЛЬКО в цифрах:
        // после замены цифр на # они совпадают (na == nb), но raw-формы различаются (ra != rb) —
        // это меняющийся колонтитул/номер страницы («Page 1» vs «Page 2») или разные строки таблицы
        // («| 12 | 34 |» vs «| 99 | 88 |»). Иначе — настоящий якорь: точное совпадение (ra == rb)
        // или нечёткое совпадение в нецифровой части (опечатка OCR).
        if (na == nb && ra != rb)
            return LineKind.Trivial;

        return LineKind.Anchor;
    }

    /// <summary>
    /// Базовая нормализация строки: обрезка пробелов по краям, сжатие внутренних пробельных
    /// символов в один пробел; регистр сохраняем; опционально — убрать знаки препинания.
    /// Цифры НЕ трогаем — это <see cref="Normalize"/> делает поверх.
    /// </summary>
    private static string RawNormalize(string line, StitchOptions options)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0)
            return string.Empty;

        // Сжимаем пробельные символы (включая табы) в один пробел.
        var collapsed = CollapseWhitespace(trimmed);

        if (options.IgnorePunctuation)
            collapsed = new string(collapsed.Where(c => !char.IsPunctuation(c)).ToArray()).Trim();

        return collapsed;
    }

    /// <summary>
    /// Нормализация строки для сравнения: базовая нормализация (<see cref="RawNormalize"/>) плюс
    /// замена любой последовательности цифр на один плейсхолдер <c>#</c>.
    /// </summary>
    private static string Normalize(string line, StitchOptions options)
    {
        return ReplaceDigitsWithPlaceholder(RawNormalize(line, options));
    }

    /// <summary>Заменяет любую последовательность цифр на один символ <c>#</c>.</summary>
    private static string ReplaceDigitsWithPlaceholder(string s)
    {
        if (s.Length == 0)
            return s;

        var sb = new System.Text.StringBuilder(s.Length);
        var inDigit = false;
        foreach (var c in s)
        {
            if (char.IsDigit(c))
            {
                if (!inDigit)
                {
                    sb.Append('#');
                    inDigit = true;
                }
            }
            else
            {
                sb.Append(c);
                inDigit = false;
            }
        }

        return sb.ToString();
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

    /// <summary>Склеивает весь <paramref name="existing"/> (с перекрытием), <paramref name="head"/>
    /// (строки next до overlap, например шапка новой страницы) и <paramref name="tail"/>
    /// (new-строки next после overlap), вставляя маркер шва, если шов сомнительный.</summary>
    private static string JoinStitched(List<string> existing, List<string> head, List<string> tail, bool doubtful)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var line in existing)
            sb.Append(line).Append('\n');

        if (doubtful)
            sb.Append(DoubtfulMarker).Append('\n');

        foreach (var line in head)
            sb.Append(line).Append('\n');

        foreach (var line in tail)
            sb.Append(line).Append('\n');

        return sb.ToString().TrimEnd('\n');
    }
}
