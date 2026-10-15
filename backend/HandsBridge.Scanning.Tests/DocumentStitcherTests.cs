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
}
