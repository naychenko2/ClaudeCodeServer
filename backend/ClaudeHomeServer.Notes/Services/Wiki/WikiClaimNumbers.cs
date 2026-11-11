using System.Text;
using System.Text.RegularExpressions;

namespace ClaudeHomeServer.Services.Notes.Wiki;

// Числа утверждения и их подтверждение цитатами (ADR-039, решение 2): каждое число
// в claim обязано встречаться в одной из его цитат, иначе операция идёт во
// «Входящие» с причиной «число не подтверждено цитатой». Проверка — по целым
// числам после нормализации: «12» не подтверждается цитатой с «123». Единицы
// в этой волне не сверяются, производные числа (среднее, темп) — следующая волна.
internal static class WikiClaimNumbers
{
    // Метки сносок [^1] и [^sN]: их цифры — не числа утверждения, вырезаются до
    // разбора, иначе номер сноски считался бы неподтверждённым числом.
    private static readonly Regex FootnoteMarker = new(@"\[\^s?\d+\]", RegexOptions.Compiled);

    // Разделители тысяч: обычный пробел, U+00A0, U+202F. Коды явные — в исходнике
    // нет невидимых литералов, класс регулярки и проверка собраны из этих констант.
    private const char SpaceNbsp = (char)0x00A0;
    private const char SpaceNarrow = (char)0x202F;

    // Число: цифры; далее — только разделители тысяч строго перед группой ровно
    // из 3 цифр (группа из 4 цифр после пробела — уже не разделитель тысяч,
    // а другое число: «12 3456» даёт «12» и «3456»); далее — не более одного
    // десятичного разделителя «.» или «,». «0,3–0,7» — тире не входит в число:
    // два отдельных числа. «17.08» — одно число, даты тоже проверяются.
    private static readonly Regex Number = new(
        "[0-9]+(?:[ " + SpaceNbsp + SpaceNarrow + "][0-9]{3}(?![0-9]))*(?:[.,][0-9]+)?",
        RegexOptions.Compiled);

    /// <summary>Числа из claim, которых нет ни в одной цитате (пусто — всё
    /// подтверждено). Порядок — по первому вхождению в claim, без дублей; значения
    /// — нормализованные (пробелы внутри числа убраны, запятая свёрнута к точке).
    /// Перед разбором из claim вырезаны метки сносок.</summary>
    internal static IReadOnlyList<string> Unconfirmed(string claim, IReadOnlyList<string> quotes)
    {
        var confirmed = new HashSet<string>();
        foreach (var quote in quotes)
            foreach (var n in Numbers(quote))
                confirmed.Add(n);

        var result = new List<string>();
        var seen = new HashSet<string>();
        foreach (var n in Numbers(FootnoteMarker.Replace(claim, "")))
            if (!confirmed.Contains(n) && seen.Add(n))
                result.Add(n);
        return result;
    }

    // Максимальные вхождения чисел по тексту, нормализованные: сравнение целыми
    // токенами, поэтому «123» не порождает «12» и «12» не подтверждается «123».
    private static IEnumerable<string> Numbers(string text)
    {
        foreach (Match m in Number.Matches(text))
            yield return Normalize(m.Value);
    }

    // Нормализация до сравнения (тот же порядок, что в QuoteMatcher): пробелы-
    // разделители тысяч внутри числа убрать, десятичную запятую свести к точке.
    private static string Normalize(string raw)
    {
        var sb = new StringBuilder(raw.Length);
        foreach (var c in raw)
            if (!IsThousandsSpace(c))
                sb.Append(c);
        return sb.ToString().Replace(',', '.');
    }

    private static bool IsThousandsSpace(char c) => c is ' ' or SpaceNbsp or SpaceNarrow;
}
