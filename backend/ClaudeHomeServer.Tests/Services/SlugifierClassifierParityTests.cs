using ClaudeHomeServer.Services;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// Второй сторож переноса `Slugify` в спину, и он про ДРУГУЮ дверь, чем
/// <see cref="SlugifierTests"/>. Тот сравнивает выход на конечном наборе строк —
/// красным он станет только если расхождение попало в `Samples`.
///
/// Здесь закрывается сам источник сомнения: старая `PersonaManager.Slugify`
/// классифицировала символ как `char.IsLetterOrDigit(ch) && ch &lt; 128`, новая —
/// `char.IsAsciiLetterOrDigit(ch)`. Это РАЗНЫЕ предикаты в общем случае
/// (`IsLetterOrDigit` знает про все алфавиты Unicode), и совпадают они только
/// потому, что второй множитель режет диапазон до ASCII. Проверяем это
/// исчерпывающим перебором, а не рассуждением: 0..127 — 128 символов,
/// перебор дешевле любого доказательства.
///
/// Плюс исчерпывающий перебор ВСЕГО BMP на эквивалентность выхода старого и
/// нового цикла: класс входов «не-ASCII буква вне транслит-таблицы» (латиница
/// с диакритикой, японский, греческий) — ровно тот, где предикаты расходятся,
/// и точечными примерами его не закрыть.
/// </summary>
public class SlugifierClassifierParityTests
{
    [Fact]
    public void ПредикатыСимвола_СовпадаютВоВсёмДиапазонеAscii()
    {
        for (var i = 0; i < 128; i++)
        {
            var ch = (char)i;
            var legacy = char.IsLetterOrDigit(ch) && ch < 128;
            char.IsAsciiLetterOrDigit(ch).Should().Be(legacy,
                $"символ U+{i:X4} ({(char.IsControl(ch) ? "control" : ch.ToString())}) "
                + "классифицируется старым и новым предикатом по-разному");
        }
    }

    // Каждый символ BMP по отдельности: старый цикл (дословная копия) против нового.
    // Одиночный символ — минимальный вход, на котором расхождение классификации
    // проявляется наблюдаемо (буква → сама себя, прочее → дефис → Trim → пусто).
    [Fact]
    public void ВыходСовпадает_НаКаждомСимволеBmp()
    {
        var mismatches = new List<string>();
        for (var i = 0; i <= 0xFFFF; i++)
        {
            // Суррогаты в одиночку — не символ; ToLowerInvariant на них не определён осмысленно
            if (char.IsSurrogate((char)i)) continue;

            var s = ((char)i).ToString();
            var legacy = LegacyPersonaSlugify(s);
            var actual = Slugifier.Slugify(s, Slugifier.XStyle.H);
            if (legacy != actual)
                mismatches.Add($"U+{i:X4}: старый «{legacy}» ≠ новый «{actual}»");
        }

        mismatches.Should().BeEmpty(
            "выход handle персистится — расхождение хотя бы на одном символе переименует персон");
    }

    // Тот же перебор в обрамлении: символ между буквами и на краях строки — там,
    // где различалась политика дефиса (старый добавлял его только при непустом
    // буфере, новый — всегда с последующим Trim('-')).
    [Theory]
    [InlineData("a{0}b")]
    [InlineData("{0}ab")]
    [InlineData("ab{0}")]
    [InlineData("{0}")]
    [InlineData("а{0}я")]
    [InlineData(" {0} ")]
    public void ВыходСовпадает_НаКаждомСимволеBmpВОбрамлении(string template)
    {
        var mismatches = new List<string>();
        for (var i = 0; i <= 0xFFFF; i++)
        {
            if (char.IsSurrogate((char)i)) continue;

            var s = string.Format(template, (char)i);
            var legacy = LegacyPersonaSlugify(s);
            var actual = Slugifier.Slugify(s, Slugifier.XStyle.H);
            if (legacy != actual)
                mismatches.Add($"U+{i:X4} в «{template}»: старый «{legacy}» ≠ новый «{actual}»");
        }

        mismatches.Should().BeEmpty();
    }

    // Дословная копия PersonaManager.Slugify до переноса: Trim() перед циклом,
    // IsLetterOrDigit + ch < 128, дефис только при sb.Length > 0.
    private static string LegacyPersonaSlugify(string s)
    {
        var sb = new System.Text.StringBuilder();
        var prevDash = false;
        foreach (var ch in s.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch) && ch < 128)
            {
                sb.Append(ch);
                prevDash = false;
            }
            else if (LegacyTranslit.TryGetValue(ch, out var tr))
            {
                if (tr.Length > 0) { sb.Append(tr); prevDash = false; }
            }
            else if (!prevDash && sb.Length > 0)
            {
                sb.Append('-');
                prevDash = true;
            }
        }
        return sb.ToString().Trim('-');
    }

    private static readonly Dictionary<char, string> LegacyTranslit = new()
    {
        ['а'] = "a", ['б'] = "b", ['в'] = "v", ['г'] = "g", ['д'] = "d", ['е'] = "e", ['ё'] = "e",
        ['ж'] = "zh", ['з'] = "z", ['и'] = "i", ['й'] = "y", ['к'] = "k", ['л'] = "l", ['м'] = "m",
        ['н'] = "n", ['о'] = "o", ['п'] = "p", ['р'] = "r", ['с'] = "s", ['т'] = "t", ['у'] = "u",
        ['ф'] = "f", ['х'] = "h", ['ц'] = "ts", ['ч'] = "ch", ['ш'] = "sh", ['щ'] = "sch",
        ['ъ'] = "", ['ы'] = "y", ['ь'] = "", ['э'] = "e", ['ю'] = "yu", ['я'] = "ya",
    };
}
