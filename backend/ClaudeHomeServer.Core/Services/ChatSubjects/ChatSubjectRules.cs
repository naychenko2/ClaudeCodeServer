using System.Globalization;
using System.Text;

namespace ClaudeHomeServer.Services.ChatSubjects;

// Лимиты и чистка имён тем чатов. Потолки держит КОД при любом ответе
// модели: промпт только просит, а распухание реестра и мусор в нём — забота этого класса.
// Корень Subject, а не Topic: Topic в коде — значок чата.
public static class ChatSubjectRules
{
    // Потолок тем, которые вправе завести модель: дальше чат остаётся «без темы»
    public const int MaxSubjectsPerProject = 12;
    // Потолок тем в реестре вообще (ручной PUT): человек может завести больше, чем модель
    public const int MaxSubjectsHuman = 50;
    // Новых тем от модели за ОДИН проход (по всем пачкам прохода)
    public const int MaxNewPerPass = 2;
    public const int MinNameLength = 2;
    public const int MaxNameLength = 40;

    // Вход модели на чат
    public const int TitleMaxChars = 80;
    public const int SnippetMaxChars = 200;
    public const int SnippetMinChars = 120;

    // Имя темы — только буквы, цифры, пробел и дефис: без управляющих символов, bidi,
    // переводов строк, разметки и ссылок. Пробелы схлопываются, 2–40 символов, хотя бы одна
    // буква. Иначе null — «без темы». Имя из реестра едет в следующий промпт уже очищенным,
    // поэтому хранимой инъекции через имя темы нет.
    public static string? SanitizeName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        // Ссылка темой не бывает: «https evil com» после чистки выглядел бы безобидно
        if (raw.Contains("://", StringComparison.Ordinal)
            || raw.Contains("www.", StringComparison.OrdinalIgnoreCase))
            return null;

        var sb = new StringBuilder(raw.Length);
        var pendingSpace = false;
        foreach (var ch in raw.Normalize(NormalizationForm.FormC))
        {
            // Комбинирующий знак, у которого после FormC не нашлось готовой буквы (ударение
            // над кириллицей: «за́мок»), выбрасывается целиком: разделителем он рвал бы слово
            // надвое («за мок»), а оставленный — пускал бы в имя «залго» из сотни знаков
            if (CharUnicodeInfo.GetUnicodeCategory(ch) is UnicodeCategory.NonSpacingMark
                or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark)
                continue;
            if (char.IsLetterOrDigit(ch) || ch == '-')
            {
                if (pendingSpace && sb.Length > 0) sb.Append(' ');
                pendingSpace = false;
                sb.Append(ch);
            }
            else
            {
                // Пробел, перевод строки, пунктуация, разметка, bidi и прочие управляющие —
                // всё это разделитель, а не часть имени
                pendingSpace = true;
            }
        }

        var name = sb.ToString().Trim('-', ' ');
        if (name.Length < MinNameLength || name.Length > MaxNameLength) return null;
        if (!name.Any(char.IsLetter)) return null;
        return name;
    }

    // Ключ сравнения имён: без учёта регистра, пробелов, дефисов и «ё/е» — «Деплой»,
    // «деплой » и «ДЕ-ПЛОЙ» одна тема.
    public static string NameKey(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name)
        {
            if (char.IsWhiteSpace(ch) || ch == '-') continue;
            var lower = char.ToLower(ch, CultureInfo.InvariantCulture);
            sb.Append(lower == 'ё' ? 'е' : lower);
        }
        return sb.ToString();
    }
}
