using System.Text;
using System.Text.RegularExpressions;

namespace ClaudeHomeServer.Services.Notes.Wiki;

// Сопоставление цитаты с источником (ADR-035, решение 3; §12г мера 1). Нормализация
// вынесена из NotesService.Annotations один раз: тот же код зовут ре-якорь аннотаций
// и правило дословности WikiGate — второй копии нет.
internal static class QuoteMatcher
{
    // Канонизация текста «как рендер» — для сверки DOM-выделения с источником:
    // markdown-ссылки и вики-ссылки заменяются видимым текстом, маркеры эмфазиса/кода
    // выбрасываются, whitespace схлопывается. Карта — норм-индекс → исходный офсет.
    private static readonly Regex RenderLinks = new(
        @"\[\[(?<t>[^\]|]+)(?:\|(?<l>[^\]]+))?\]\]|!?\[(?<x>[^\]]*)\]\((?<u>[^)\s]*)\)",
        RegexOptions.Compiled);
    private static readonly char[] MarkupChars = ['*', '`', '_', '~', '\\'];

    internal static (string Norm, List<int> Map) NormalizeForMatch(string doc)
    {
        // Шаг 1: ссылки → видимый текст (позиции символов подписи — исходные)
        var buf = new StringBuilder(doc.Length);
        var map1 = new List<int>(doc.Length);
        var last = 0;
        foreach (Match m in RenderLinks.Matches(doc))
        {
            for (var i = last; i < m.Index; i++) { buf.Append(doc[i]); map1.Add(i); }
            var g = m.Groups["l"].Success ? m.Groups["l"] : m.Groups["t"].Success ? m.Groups["t"] : m.Groups["x"];
            for (var i = 0; i < g.Length; i++) { buf.Append(doc[g.Index + i]); map1.Add(g.Index + i); }
            last = m.Index + m.Length;
        }
        for (var i = last; i < doc.Length; i++) { buf.Append(doc[i]); map1.Add(i); }

        // Шаг 2: whitespace collapse + дроп маркеров эмфазиса/кода (симметрично для
        // обеих сторон сверки — подчёркивания в идентификаторах матчу не мешают) +
        // вычищение построчных структурных маркеров (заголовки #, маркеры списков
        // -/+/1., цитаты >): в рендере их не видно, DOM-выделение их не содержит, а
        // без этого любое выделение, пересекающее такую границу, отбивалось ложным 409.
        var sb = new StringBuilder(buf.Length);
        var map = new List<int>(buf.Length);
        var pendingSpace = false;
        var atLineStart = true;
        for (var i = 0; i < buf.Length; i++)
        {
            var ch = buf[i];
            if (char.IsWhiteSpace(ch))
            {
                if (ch == '\n') atLineStart = true;   // индент-пробелы начало строки не сбрасывают
                pendingSpace = sb.Length > 0;
                continue;
            }
            if (atLineStart)
            {
                // Съедаем ведущие маркеры строки (в т.ч. вложенные: «> - пункт»)
                int after;
                while ((after = SkipLineMarkers(buf, i)) > i) i = after;
                atLineStart = false;
                if (i >= buf.Length) break;
                ch = buf[i];
            }
            if (Array.IndexOf(MarkupChars, ch) >= 0) continue;
            if (pendingSpace) { sb.Append(' '); map.Add(map1[i]); pendingSpace = false; }
            sb.Append(ch); map.Add(map1[i]);
        }
        return (sb.ToString(), map);
    }

    // Ведущий структурный маркер строки, начиная с непробельной позиции i (первый
    // контентный символ строки). Возвращает индекс за маркером и его хвостовыми
    // пробелами, либо i, если маркера нет. «Как рендер»: заголовки/списки/цитаты
    // теряют свой синтаксис, но не текст.
    private static int SkipLineMarkers(StringBuilder s, int i)
    {
        var n = s.Length;
        // ATX-заголовок: 1..6 '#' + пробел (без пробела — это #тег, не заголовок)
        if (s[i] == '#')
        {
            var j = i; var cnt = 0;
            while (j < n && s[j] == '#' && cnt < 6) { j++; cnt++; }
            if (j < n && (s[j] == ' ' || s[j] == '\t'))
            {
                while (j < n && (s[j] == ' ' || s[j] == '\t')) j++;
                return j;
            }
            return i;
        }
        // Цитата: один+ '>' с необязательными пробелами
        if (s[i] == '>')
        {
            var j = i;
            while (j < n && (s[j] == '>' || s[j] == ' ' || s[j] == '\t')) j++;
            return j;
        }
        // Маркированный список: -/+ + пробел ('*' и так дропается как эмфазис)
        if ((s[i] == '-' || s[i] == '+') && i + 1 < n && (s[i + 1] == ' ' || s[i + 1] == '\t'))
        {
            var j = i + 1;
            while (j < n && (s[j] == ' ' || s[j] == '\t')) j++;
            return j;
        }
        // Нумерованный список: цифры + ('.'|')') + пробел
        if (char.IsDigit(s[i]))
        {
            var j = i;
            while (j < n && char.IsDigit(s[j])) j++;
            if (j + 1 < n && (s[j] == '.' || s[j] == ')') && (s[j + 1] == ' ' || s[j + 1] == '\t'))
            {
                j++;
                while (j < n && (s[j] == ' ' || s[j] == '\t')) j++;
                return j;
            }
        }
        return i;
    }

    // Сохранённая цитата прошла через Escape, документ — нет. Сверять их надо в
    // одной нормализации, иначе выделение с двойными кавычками не находится
    // никогда. Замена посимвольная — карта офсетов документа не съезжает.
    internal static string FoldQuotes(string s) => s.Replace('"', '\'');

    // Нормализованная цитата — та же канонизация, что и при сопоставлении: длина
    // цитаты меряется по ней, иначе цитата из одной разметки («*******a») проходит
    // как содержательная.
    internal static string NormalizeQuote(string quote) => FoldQuotes(NormalizeForMatch(quote).Norm);

    /// <summary>Контракт длины цитаты (8–300, WikiPage.MinQuoteLength/MaxQuoteLength) —
    /// одна мера формы пакета (WikiPackage) и гейта (WikiGate), минор R-S2-3: раньше форма
    /// меряла Trim().Length, а гейт — по нормализованной строке, и пакет, прошедший форму,
    /// мог отбиться гейтом. Нижняя граница — по нормализованной строке (той же, по которой
    /// сверяется дословность): цитата из одной разметки («*******a») не содержательна, даже
    /// если сырая длина в контракте. Верхняя граница — по сырой: нормализация только
    /// укорачивает, верхнюю меру она не обходит.</summary>
    internal static (int Raw, int Norm, bool OutOfContract) MeasureQuoteLength(string quote)
    {
        var norm = NormalizeQuote(quote).Length;
        return (quote.Length, norm, norm < WikiPage.MinQuoteLength || quote.Length > WikiPage.MaxQuoteLength);
    }

    /// <summary>Есть ли цитата в тексте источника с точностью до пробелов, кавычек
    /// и разметки (ADR-035, §12г мера 1): обе стороны канонизированы «как рендер»,
    /// прямые двойные кавычки свёрнуты. Регистр и прочие символы значимы — цитата
    /// дословная. Совпадение обязано быть выдержкой по границам слов: усечённая
    /// цитата («15 тыс.» из «115 тыс.», «планировать» из «перепланировать») — не
    /// дословная. В отличие от ре-якоря аннотаций единственность вхождения здесь
    /// не требуется: важно наличие в источнике, два вхождения в нём допустимы.</summary>
    internal static bool ContainsVerbatim(string sourceText, string quote)
    {
        var q = NormalizeQuote(quote);
        if (q.Length == 0)
            return false;
        var src = FoldQuotes(NormalizeForMatch(sourceText).Norm);
        for (var at = src.IndexOf(q, StringComparison.Ordinal); at >= 0; at = src.IndexOf(q, at + 1, StringComparison.Ordinal))
            if (!CutInsideWord(src, at) && !CutInsideWord(src, at + q.Length))
                return true;
        return false;
    }

    // Позиция pos лежит внутри буквенно-цифрового токена: с обеих сторон — буква
    // или цифра, т.е. токен начинается раньше и продолжается за pos. Совпадение,
    // ограниченное такой позицией, рассечено посреди слова или числа.
    private static bool CutInsideWord(string s, int pos) =>
        pos > 0 && pos < s.Length && char.IsLetterOrDigit(s[pos - 1]) && char.IsLetterOrDigit(s[pos]);
}
