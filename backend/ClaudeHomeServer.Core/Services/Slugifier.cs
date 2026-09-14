using System.Text;

namespace ClaudeHomeServer.Services;

// Единственный алгоритм транслитерации кириллицы в ASCII-slug: строчные, кириллица →
// латиница по однозначной таблице (без контекстных правил — детерминизм важнее
// филологической точности), ъ/ь выбрасываются (объект→obekt), прочие символы → дефисы,
// повторы и краевые дефисы схлопываются.
//
// Вынесен из `PersonaManager.Slugify` и `DossierGitExporter.Slugify` (Этап 3): две
// независимые копии одного цикла жили в двух вертикалях, не принадлежа ни одной —
// чистый stateless-примитив спины, по прецеденту `PathNormalizer`/`ExecutableResolver`.
//
// Копии оказались НЕ тождественны ровно в одной букве: «х» давала `h` у персон и `kh`
// у летописи. Свести к одной нельзя — оба выхода персистятся (handle в personas.json и
// @упоминаниях, имена файлов в ветке летописи, имя репозитория в Forgejo), и смена
// буквы задним числом переименовала бы уже существующие сущности. Поэтому развилка
// осталась явным параметром <see cref="XStyle"/>, а не разошлась в два цикла.
public static class Slugifier
{
    /// <summary>Как транслитерировать «х» — единственное расхождение исторических копий.</summary>
    public enum XStyle
    {
        /// <summary>«х» → «h»: handle персон, имена репозиториев и ветвей.</summary>
        H,

        /// <summary>«х» → «kh»: пути летописи (паспорта изменений, конспекты).</summary>
        Kh,
    }

    private static readonly Dictionary<char, string> TranslitH = BuildTranslit("h");
    private static readonly Dictionary<char, string> TranslitKh = BuildTranslit("kh");

    private static Dictionary<char, string> BuildTranslit(string x) => new()
    {
        ['а'] = "a", ['б'] = "b", ['в'] = "v", ['г'] = "g", ['д'] = "d", ['е'] = "e", ['ё'] = "e",
        ['ж'] = "zh", ['з'] = "z", ['и'] = "i", ['й'] = "y", ['к'] = "k", ['л'] = "l", ['м'] = "m",
        ['н'] = "n", ['о'] = "o", ['п'] = "p", ['р'] = "r", ['с'] = "s", ['т'] = "t", ['у'] = "u",
        ['ф'] = "f", ['х'] = x, ['ц'] = "ts", ['ч'] = "ch", ['ш'] = "sh", ['щ'] = "sch",
        ['ъ'] = "", ['ы'] = "y", ['ь'] = "", ['э'] = "e", ['ю'] = "yu", ['я'] = "ya",
    };

    /// <summary>
    /// Slug из произвольной строки. <paramref name="maxChars"/> — потолок длины
    /// (0 и меньше — без потолка); после обрезки краевые дефисы снимаются повторно,
    /// иначе рез посреди разделителя оставлял бы хвостовой дефис.
    /// Пустой результат возвращается как есть: нейтральная замена («agent», «project»,
    /// «dossier») — политика вызывающего, а не примитива.
    /// </summary>
    public static string Slugify(string s, XStyle x = XStyle.H, int maxChars = 0)
    {
        if (string.IsNullOrEmpty(s)) return "";

        var translit = x == XStyle.Kh ? TranslitKh : TranslitH;
        var sb = new StringBuilder(s.Length);
        var prevDash = false;
        foreach (var ch in s.ToLowerInvariant())
        {
            if (translit.TryGetValue(ch, out var piece))
            {
                if (piece.Length == 0) continue;
                sb.Append(piece);
                prevDash = false;
            }
            else if (char.IsAsciiLetterOrDigit(ch))
            {
                sb.Append(ch);
                prevDash = false;
            }
            else if (!prevDash)
            {
                sb.Append('-');
                prevDash = true;
            }
        }

        var slug = sb.ToString().Trim('-');
        if (maxChars > 0 && slug.Length > maxChars) slug = slug[..maxChars].Trim('-');
        return slug;
    }
}
