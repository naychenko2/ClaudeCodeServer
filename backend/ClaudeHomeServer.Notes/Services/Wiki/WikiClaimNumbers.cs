using System.Text;
using System.Text.RegularExpressions;

namespace ClaudeHomeServer.Services.Notes.Wiki;

// Числа утверждения и их подтверждение цитатами (ADR-039, решение 2): каждое число
// в claim обязано встречаться в одной из его цитат, иначе операция идёт во
// «Входящие» с причиной «число не подтверждено цитатой». Проверка — по целым
// числам после нормализации: «12» не подтверждается цитатой с «123», а «−12» —
// цитатой без знака. Единицы в этой волне не сверяются, производные числа
// (среднее, темп) — следующая волна.
internal static class WikiClaimNumbers
{
    // Метки сносок [^1] и [^sN]: их цифры — не числа утверждения, вырезаются до
    // разбора и из claim, и из цитат: иначе номер сноски считался бы неподтверждённым
    // числом, а «[^1]» в цитате ложно подтверждал бы число claim («в 1 случае»).
    private static readonly Regex FootnoteMarker = new(@"\[\^s?\d+\]", RegexOptions.Compiled);

    // Разделители тысяч: обычный пробел, U+00A0, U+202F. Коды явные — в исходнике
    // нет невидимых литералов, класс регулярки и проверка собраны из этих констант.
    private const char SpaceNbsp = (char)0x00A0;
    private const char SpaceNarrow = (char)0x202F;

    // Знак числа: минус ASCII и «−» U+2212; к сравнению сводится к ASCII. Тире
    // (U+2013, U+2014) знаком не считается: «0,3–0,7» остаётся двумя числами.
    private const char MinusAscii = '-';
    private const char MinusUnicode = (char)0x2212;

    // Класс разделителей тысяч — один и тот же в хвосте числа и в охране знака
    // (ниже), собран из констант выше: невидимых литералов в исходнике нет.
    private static readonly string ThousandsSpaceClass = "[ " + SpaceNbsp + SpaceNarrow + "]";

    // Хвост числа: цифры; далее — только разделители тысяч строго перед группой ровно
    // из 3 цифр (группа из 4 цифр после пробела — уже не разделитель тысяч,
    // а другое число: «12 3456» даёт «12» и «3456»); далее — не более одного
    // десятичного разделителя «.» или «,». «0,3–0,7» — тире не входит в число:
    // два отдельных числа. «17.08» — одно число, даты тоже проверяются.
    private static readonly string NumberTail =
        "[0-9]+(?:" + ThousandsSpaceClass + "[0-9]{3}(?![0-9]))*(?:[.,][0-9]+)?";

    // Число — без знака (первая ветка) или со знаком минуса (вторая). Знак входит
    // в токен и не стоит после цифры или десятичного разделителя: «123-456» и
    // «0.3-0.7» — по-прежнему два числа, а не одно отрицательное. Не является знаком
    // и после числа с любым пробелом перед ним: «12 -34» — список из двух чисел,
    // «12» и «34» (дельта-ревью S2-2, п.8): дефис сразу после числа относится к
    // предыдущему числу, знак здесь был бы ложным чтением. Одиночное «−12 °C»
    // (без числа перед знаком) остаётся отрицательным.
    private static readonly Regex Number = new(
        NumberTail + "|(?<![0-9.])(?<![0-9]" + ThousandsSpaceClass + "+)[" + MinusAscii + MinusUnicode + "]" + NumberTail,
        RegexOptions.Compiled);

    /// <summary>Числа из claim, которых нет ни в одной цитате (пусто — всё
    /// подтверждено). Порядок — по первому вхождению в claim, без дублей; значения
    /// — нормализованные (пробелы внутри числа убраны, запятая свёрнута к точке,
    /// знак-min U+2212 свёрнут к ASCII). Перед разбором метки сносок вырезаны и из
    /// claim, и из цитат. Null claim — ничего проверять нечем: пустой результат.
    /// Null цитат и пустой список — одно и то же: утверждения без цитат подтвердить
    /// нечем, все его числа неподтверждены (дельта-ревью S2-2, п.9).</summary>
    internal static IReadOnlyList<string> Unconfirmed(string? claim, IReadOnlyList<string>? quotes)
    {
        if (claim is null)
            return [];

        var confirmed = new HashSet<string>();
        if (quotes is not null)
            foreach (var quote in quotes)
                foreach (var n in Numbers(FootnoteMarker.Replace(quote, "")))
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
    // разделители тысяч внутри числа убрать, десятичную запятую свести к точке,
    // знак-min U+2212 свести к ASCII: «−12» и «-12» — одно число.
    private static string Normalize(string raw)
    {
        var sb = new StringBuilder(raw.Length);
        foreach (var c in raw)
        {
            if (IsThousandsSpace(c))
                continue;
            sb.Append(c == MinusUnicode ? MinusAscii : c);
        }
        return sb.ToString().Replace(',', '.');
    }

    private static bool IsThousandsSpace(char c) => c is ' ' or SpaceNbsp or SpaceNarrow;
}
