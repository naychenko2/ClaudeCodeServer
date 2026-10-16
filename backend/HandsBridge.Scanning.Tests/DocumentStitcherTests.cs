using ClaudeHomeServer.HandsBridge.Scanning.Text;
using Xunit;

namespace HandsBridge.Scanning.Tests;

/// <summary>
/// Тесты склейки текста соседних страниц. Контракт: конец страницы N и начало N+1 перекрываются
/// (PageDown, строки повторяются) — ищем самое длинное совпадение хвоста с головой и склеиваем
/// без дублей; сомнительный шов помечаем маркером.
/// </summary>
public class DocumentStitcherTests
{
    /// <summary>
    /// Детерминированно-уникальная строка: 12 символов из hash по номеру. Consecutive строки
    /// различаются по большинству позиций (нечёткое сравнение их не спутает), а одинаковый номер
    /// даёт одинаковую строку — то, что нужно для перекрытия.
    /// </summary>
    private static string Line(int i)
    {
        // LCG от детерминированного начального h: 12 букв, consecutive номера дают разные строки.
        var h = (uint)(i * 2654435761);
        var sb = new System.Text.StringBuilder(12);
        for (var k = 0; k < 12; k++)
        {
            sb.Append((char)('a' + (int)(h % 26)));
            h = h * 1103515245 + 12345;
        }

        return sb.ToString();
    }

    [Fact]
    public void Clean_five_line_overlap_is_merged_without_duplicates()
    {
        var existing = string.Join("\n", Enumerable.Range(1, 9).Select(Line)); // rows 1..9
        var next = string.Join("\n", Enumerable.Range(5, 9).Select(Line)); // rows 5..13

        var result = DocumentStitcher.Append(existing, next);

        Assert.Equal(5, result.OverlapLines);
        Assert.False(result.Doubtful);
        Assert.Equal(string.Join("\n", Enumerable.Range(1, 13).Select(Line)), result.Text);
    }

    [Fact]
    public void Single_long_line_overlap_is_doubtful()
    {
        var existing = "alpha\nbeta\ngamma\ndelta";
        var next = "delta\nepsilon\nzeta";

        var result = DocumentStitcher.Append(existing, next);

        Assert.Equal(1, result.OverlapLines);
        // Обоснование: перекрытие из ОДНОЙ значимой строки — слабый якорь (< MinAnchorLines = 2),
        // поэтому шов помечен сомнительным, хотя строка совпала точно.
        Assert.True(result.Doubtful);
        Assert.Contains(DocumentStitcher.DoubtfulMarker, result.Text);
    }

    [Fact]
    public void No_overlap_appends_with_blank_line_and_is_doubtful()
    {
        var existing = "alpha\nbeta\ngamma";
        var next = "delta\nepsilon\nzeta";

        var result = DocumentStitcher.Append(existing, next);

        Assert.Equal(0, result.OverlapLines);
        Assert.True(result.Doubtful);
        Assert.Contains(DocumentStitcher.DoubtfulMarker, result.Text);
        // Страницы разделены пустым рядом, порядок сохранён.
        Assert.Contains("gamma\n\n<!-- шов: проверить -->\ndelta", result.Text);
    }

    [Fact]
    public void Identical_page_returns_existing_without_duplicate()
    {
        var page = "alpha\nbeta\ngamma";

        var result = DocumentStitcher.Append(page, page);

        // Повторный кадр: весь next — перекрытие, дубля нет, итог = исходный текст.
        Assert.Equal(3, result.OverlapLines);
        Assert.Equal(page, result.Text);
    }

    [Fact]
    public void Ocr_typo_in_one_line_still_matches_fuzzily()
    {
        // «line three content here» в next опечатано как «...herq» (1 символ в 23-символьной строке):
        // нормированное расстояние 1/23 ≈ 0.04 ≤ 0.15 → нечёткое совпадение срабатывает, шов найден.
        var existing = "line one content here\nline two content here\nline three content here\nline four content here";
        var next = "line three content herq\nline four content here\nline five content here";

        var result = DocumentStitcher.Append(existing, next);

        Assert.Equal(2, result.OverlapLines);
        Assert.False(result.Doubtful);
        // Опечатанная строка из next не дублируется: в итоге чистая версия из existing.
        Assert.Contains("line three content here", result.Text);
        Assert.DoesNotContain("line three content herq", result.Text);
    }

    [Fact]
    public void Empty_lines_at_the_seam_do_not_break_the_overlap()
    {
        // Пустая строка на стыке: не якорь, но и не рвёт совпадение.
        var existing = "alpha\n\nbeta";
        var next = "\nbeta\ngamma";

        var result = DocumentStitcher.Append(existing, next);

        // Совпало 2 строки (пустая + "beta"); якорей 1 ("beta") → сомнительно, но перекрыто.
        Assert.Equal(2, result.OverlapLines);
        Assert.True(result.Doubtful);
    }

    [Fact]
    public void Repeating_table_lines_take_the_longest_match_from_the_end()
    {
        // Строки таблицы повторяются: "...| x |" встречается и в середине, и в хвосте.
        // Берём самое длинное совпадение с конца, а не первое вхождение.
        var existing = "header\n| a |\n| b |\n| a |\n| b |";
        var next = "| a |\n| b |\n| c |\n| d |";

        var result = DocumentStitcher.Append(existing, next);

        // Хвост existing ("| a |", "| b |") совпадает с головой next ("| a |", "| b |") — 2 строки.
        Assert.Equal(2, result.OverlapLines);
        // Итог: header, | a |, | b |, | a |, | b |, | c |, | d | — без дубля хвоста.
        Assert.Equal("header\n| a |\n| b |\n| a |\n| b |\n| c |\n| d |", result.Text);
    }

    [Fact]
    public void Russian_text_overlaps_and_merges()
    {
        var existing = "Первая строка\nВторая строка\nТретья строка\nЧетвёртая строка";
        var next = "Третья строка\nЧетвёртая строка\nПятая строка";

        var result = DocumentStitcher.Append(existing, next);

        Assert.Equal(2, result.OverlapLines);
        Assert.False(result.Doubtful);
        Assert.Equal(
            "Первая строка\nВторая строка\nТретья строка\nЧетвёртая строка\nПятая строка",
            result.Text);
    }

    [Fact]
    public void Crlf_and_lf_are_both_handled()
    {
        var existing = "alpha\r\nbeta\r\ngamma\r\ndelta";
        var next = "gamma\r\ndelta\r\nepsilon\r\nzeta";

        var result = DocumentStitcher.Append(existing, next);

        Assert.Equal(2, result.OverlapLines);
        // Итог в LF-нормализованном виде, без \r, без маркера (чистый шов из 2 якорей).
        Assert.Equal("alpha\nbeta\ngamma\ndelta\nepsilon\nzeta", result.Text);
    }

    [Fact]
    public void Very_long_page_stays_fast()
    {
        // 10 000 строк, перекрытие 40 строк: окно на хвост, не O(n²) на всём документе.
        var existing = string.Join("\n", Enumerable.Range(1, 10_000).Select(Line));
        var next = string.Join("\n", Enumerable.Range(9961, 10_000).Select(Line)); // rows 9961..19960

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var result = DocumentStitcher.Append(existing, next);
        stopwatch.Stop();

        Assert.Equal(40, result.OverlapLines);
        Assert.False(result.Doubtful);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1), $"Приняло {stopwatch.Elapsed}");
    }

    // ─── Меняющиеся номера страниц и колонтитулы ───────────────────────────────────────────────

    [Fact]
    public void Changing_page_number_and_header_do_not_break_the_seam()
    {
        // existing: контент + «Page 1» внизу; next: шапка «Header» + тот же контент + «Page 2».
        // «Page 1»/«Page 2» совпадают только после нормализации цифр (raw различаются) → якорем
        // не считаются; контент-строки совпадают точно → якоря. Шов надёжный, дублей и потерь нет.
        var existing = "Alpha\nBeta\nGamma\nPage 1";
        var next = "Header\nAlpha\nBeta\nGamma\nPage 2";

        var result = DocumentStitcher.Append(existing, next);

        // Перекрытие = Alpha/Beta/Gamma + Page 1/Page 2 (4 строки); «Page 1»/«Page 2» — тривиальные.
        Assert.Equal(4, result.OverlapLines);
        Assert.False(result.Doubtful);
        // Контент не потерян и не задублирован; шапка новой страницы добавлена, номер не дублируется.
        Assert.Contains("Alpha", result.Text);
        Assert.Contains("Beta", result.Text);
        Assert.Contains("Gamma", result.Text);
        Assert.Contains("Header", result.Text);
        Assert.Equal(1, CountOccurrences(result.Text, "Alpha"));
        Assert.Equal(1, CountOccurrences(result.Text, "Beta"));
    }

    [Fact]
    public void Changing_number_in_core_of_overlap_requires_digit_normalization()
    {
        // Меняющаяся строка (номер «Page 1» → «Page 2») стоит в ЯДРЕ перекрытия, а не на краю:
        // вокруг неё совпадающий контент. Без нормализации цифр «Page 1» и «Page 2» — несовпадение
        // (расстояние 1 > 0.15*6), и ядровый mismatch рвёт шов. С нормализацией — тривиальное
        // совпадение, якоря (Top/Bottom) держат перекрытие.
        var existing = "Top content\nPage 1\nBottom content";
        var next = "Top content\nPage 2\nBottom content\nNew content";

        var result = DocumentStitcher.Append(existing, next);

        // Перекрытие 3 строки (Top/Page/Bottom) найдено, 2 якоря (Top, Bottom), шов надёжный.
        Assert.Equal(3, result.OverlapLines);
        Assert.False(result.Doubtful);
        // Контент не задублирован, новая строка добавлена.
        Assert.Equal(1, CountOccurrences(result.Text, "Top content"));
        Assert.Equal(1, CountOccurrences(result.Text, "Bottom content"));
        Assert.Contains("New content", result.Text);
    }

    [Fact]
    public void Russian_page_number_and_date_header_do_not_break_the_seam()
    {
        // Russian: «Страница 3 из 16» → «Страница 4 из 16» (номера меняются), дата в шапке
        // «15.10.2026» → «16.10.2026». Обе строки совпадают только после нормализации цифр.
        var existing = "Дата: 15.10.2026\nСтрока текста один\nСтрока текста два\nСтраница 3 из 16";
        var next = "Дата: 16.10.2026\nСтрока текста один\nСтрока текста два\nСтраница 4 из 16";

        var result = DocumentStitcher.Append(existing, next);

        // Контент-строки (2) — якоря; дата и номер — тривиальные. Шов надёжный.
        Assert.Equal(4, result.OverlapLines);
        Assert.False(result.Doubtful);
        // Контент сохранён один раз, без дубля.
        Assert.Equal(1, CountOccurrences(result.Text, "Строка текста один"));
        Assert.Equal(1, CountOccurrences(result.Text, "Строка текста два"));
    }

    [Fact]
    public void Footer_only_in_tail_of_existing_still_stitches()
    {
        // Колонтитул «Footer 1» есть только в хвосте existing; в next его нет (при прокрутке
        // колонтитул страницы N уходит за верх кадра N+1). Контент-строки перекрываются.
        var existing = "Alpha one\nBeta two\nGamma three\nDelta four\nFooter 1";
        var next = "Gamma three\nDelta four\nEpsilon five\nZeta six\nFooter 2";

        var result = DocumentStitcher.Append(existing, next);

        // Перекрытие Gamma three/Delta four (2 якоря) + колонтитул на краю найдено, шов надёжный.
        Assert.Equal(3, result.OverlapLines);
        Assert.False(result.Doubtful);
        // Контент existing сохранён один раз, без дублей.
        Assert.Equal(1, CountOccurrences(result.Text, "Alpha one"));
        Assert.Equal(1, CountOccurrences(result.Text, "Beta two"));
        Assert.Equal(1, CountOccurrences(result.Text, "Gamma three"));
        Assert.Equal(1, CountOccurrences(result.Text, "Delta four"));
    }

    // ─── Ложный якорь: две страницы целиком из «Page #» ─────────────────────────────────────────

    [Fact]
    public void Two_pages_of_page_numbers_only_give_doubtful_seam()
    {
        // Обе страницы состоят только из «Page N». После нормализации все строки = «Page #»,
        // но raw различаются (Page 1 ≠ Page 4) → ни одна не якорь. Якорей 0 < MinAnchorLines →
        // шов сомнительный, страницы не склеиваются в одно.
        var existing = "Page 1\nPage 2\nPage 3";
        var next = "Page 4\nPage 5\nPage 6";

        var result = DocumentStitcher.Append(existing, next);

        Assert.Equal(0, result.OverlapLines);
        Assert.True(result.Doubtful);
        Assert.Contains(DocumentStitcher.DoubtfulMarker, result.Text);
        // Обе страницы целиком присутствуют в итоге.
        Assert.Contains("Page 1", result.Text);
        Assert.Contains("Page 6", result.Text);
    }

    // ─── Таблица Markdown с числовыми столбцами: разные строки ≠ перекрытие ─────────────────────

    [Fact]
    public void Different_markdown_table_rows_are_not_treated_as_overlap()
    {
        // Два РАЗНЫХ блока таблиц: цифры нормализуются («| # | # |»), но raw различаются,
        // поэтому ни одна строка не является якорем (якорей 0 < MinAnchorLines). Перекрытия нет
        // → шов сомнительный, оба блока присутствуют полностью.
        var existing = "| 12 | 34 |\n| 13 | 35 |\n| 14 | 36 |";
        var next = "| 99 | 88 |\n| 77 | 66 |\n| 55 | 44 |";

        var result = DocumentStitcher.Append(existing, next);

        Assert.Equal(0, result.OverlapLines);
        Assert.True(result.Doubtful);
        Assert.Contains(DocumentStitcher.DoubtfulMarker, result.Text);
        // Оба блока таблиц на месте, строки не потеряны.
        Assert.Contains("| 12 | 34 |", result.Text);
        Assert.Contains("| 13 | 35 |", result.Text);
        Assert.Contains("| 99 | 88 |", result.Text);
        Assert.Contains("| 55 | 44 |", result.Text);
    }

    // ─── Перекрытие больше окна ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Overlap_larger_than_window_is_found_by_adaptive_window()
    {
        // existing = line 1..100, next = line 51..150: реальное перекрытие 50 строк > окна 40.
        // Адаптивное окно удваивается (40 → 80) и находит перекрытие. Итог ровно 150 строк,
        // без дублей, шов надёжный (50 якорей — строки совпадают точно).
        var existing = string.Join("\n", Enumerable.Range(1, 100).Select(i => "line " + i));
        var next = string.Join("\n", Enumerable.Range(51, 100).Select(i => "line " + i));

        var result = DocumentStitcher.Append(existing, next);

        Assert.Equal(50, result.OverlapLines);
        Assert.False(result.Doubtful);
        Assert.Equal(150, result.Text.Split('\n').Length);
        // Каждая строка ровно один раз: line 1..150 без дублей.
        Assert.Equal(1, CountOccurrences(result.Text, "line 51"));
        Assert.Equal(1, CountOccurrences(result.Text, "line 100"));
        Assert.Equal(1, CountOccurrences(result.Text, "line 150"));
    }

    [Fact]
    public void Overlap_at_absolute_max_window_is_capped()
    {
        // Перекрытие 300 строк: окно растёт 40 → 80 → 160 → 320, но AbsoluteMaxOverlapLines=250
        // ограничивает рост. На границе 250 совпадение всё равно находится (300 > 250, но окно
        // 250 ⊂ перекрытия 300 → валидный k на границе). Флаг OverlapExceedsWindow = true.
        var existing = string.Join("\n", Enumerable.Range(1, 400).Select(i => "row " + i));
        var next = string.Join("\n", Enumerable.Range(101, 400).Select(i => "row " + i));

        var options = new StitchOptions { AbsoluteMaxOverlapLines = 250 };
        var result = DocumentStitcher.Append(existing, next, options);

        // Окно уперлось в потолок 250 < перекрытия 300 → флаг превышения окна.
        Assert.True(result.OverlapExceedsWindow);
        // Найденное перекрытие не превышает потолка.
        Assert.True(result.OverlapLines <= 250);
    }

    // ─── Свойство «без потерь» на случайных документах ──────────────────────────────────────────

    [Fact]
    public void Sequential_stitching_of_random_pages_loses_no_content()
    {
        // Собираем «документ» из 200 детерминированных уникальных строк, режем на кадры
        // с перекрытием 20 строк (каждый следующий кадр начинается 20 строками раньше конца
        // предыдущего) и склеиваем последовательно. Итог должен содержать каждую исходную строку
        // ровно один раз — ни дублей, ни потерь.
        var rng = new Random(42);
        var doc = new List<string>();
        for (var i = 0; i < 200; i++)
            doc.Add($"content-{i:000}-{rng.Next(1000000)}");

        const int frameSize = 40;
        const int overlap = 20;

        var frames = new List<string>();
        for (var start = 0; start < doc.Count; start += frameSize - overlap)
        {
            var end = Math.Min(start + frameSize, doc.Count);
            frames.Add(string.Join("\n", doc[start..end]));
        }

        var stitched = frames[0];
        for (var i = 1; i < frames.Count; i++)
            stitched = DocumentStitcher.Append(stitched, frames[i]).Text;

        // Каждая строка документа присутствует в итоге ровно один раз.
        foreach (var line in doc)
            Assert.Equal(1, CountOccurrences(stitched, line));
    }

    // ─── Вспомогательное ───────────────────────────────────────────────────────────────────────

    /// <summary>Считает вхождения <paramref name="needle"/> как подстроки в <paramref name="haystack"/>.</summary>
    private static int CountOccurrences(string haystack, string needle)
    {
        if (needle.Length == 0)
            return 0;

        var count = 0;
        var idx = 0;
        while ((idx = haystack.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += needle.Length;
        }

        return count;
    }
}
