using System.Text;
using System.Text.RegularExpressions;

namespace ClaudeHomeServer.Services.Prompts;

// Детект «магических слов» oh-my-claudecode на стороне сервера — замена хука
// keyword-detector плагина. Сам хук отключён вместе со всеми хуками (claude
// --settings disableAllHooks, см. ClaudeRuntimeSettings — иначе он на каждый ход
// плодит окна консоли на Windows-хосте). При детекте магслова к тексту хода
// дописывается компактная инструкция запустить соответствующий скилл — зеркалит
// createSkillInvocation хука (без инлайна SKILL.md).
//
// Воспроизводим только слова, ведущие на реально существующие скиллы плагина;
// шумные эвристики хука («build me an app», «search the codebase», «end to end»)
// намеренно не переносим — они дают ложные срабатывания. ultrathink опущен:
// это встроенный режим claude, отдельная активация не нужна.
public static class OmcKeywordRouting
{
    // Магслово → имя OMC-скилла. Порядок = приоритет вывода (как priorityOrder хука).
    private static readonly (string Skill, Regex Pattern)[] Keywords =
    [
        ("cancel",          Word(@"cancelomc|stopomc")),
        ("ralph",           Word(@"ralph")),
        ("ultragoal",       Word(@"ultragoal")),
        ("autopilot",       Word(@"autopilot|auto[\s-]?pilot|full\s?auto|fullsend")),
        ("ultrawork",       Word(@"ultrawork|ulw")),
        ("ccg",             Word(@"ccg|claude-codex-gemini")),
        ("ralplan",         Word(@"ralplan")),
        ("deep-interview",  Word(@"deep[\s-]interview|ouroboros")),
        ("ai-slop-cleaner", Word(@"ai[\s-]?slop|deslop")),
        // wiki — только с действием (wiki this/add/lint/query): голое «wiki» слишком часто
        // встречается в обычной речи («посмотри вики», «wiki page») и давало ложный запуск.
        ("wiki",            Word(@"wiki\s+(?:this|add|lint|query)")),
    ];

    // Граница по буквам/цифрам (юникод) — как MagicWordRe в OmcPersonaRouting:
    // «ulw» внутри других слов не считается магсловом.
    private static Regex Word(string alts) =>
        new($@"(?<![\p{{L}}\p{{N}}])(?:{alts})(?![\p{{L}}\p{{N}}])",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Обнаруженные скиллы в порядке приоритета (без дублей). Пустой список — ничего не найдено.
    public static IReadOnlyList<string> DetectSkills(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        var found = new List<string>();
        foreach (var (skill, re) in Keywords)
            if (re.IsMatch(text))
                found.Add(skill);
        return found;
    }

    // Инъекция в ход: инструкция запустить скилл(ы). null — если ничего не обнаружено.
    public static string? BuildKeywordHint(string? text)
    {
        var skills = DetectSkills(text);
        if (skills.Count == 0) return null;

        var sb = new StringBuilder();
        if (skills.Count == 1)
        {
            var s = skills[0];
            sb.Append($"[МАГИЧЕСКОЕ СЛОВО: {s.ToUpperInvariant()}]\n\n");
            sb.Append($"Обнаружено магслово oh-my-claudecode. Немедленно запусти скилл: /oh-my-claudecode:{s}\n");
            sb.Append($"Если слэш-вызов недоступен — найди skills/{s}/SKILL.md плагина oh-my-claudecode и следуй ему.\n");
            sb.Append($"ВАЖНО: начни workflow «{s}» сразу.");
        }
        else
        {
            sb.Append($"[МАГИЧЕСКИЕ СЛОВА: {string.Join(", ", skills.Select(x => x.ToUpperInvariant()))}]\n\n");
            sb.Append("Выполни ВСЕ обнаруженные режимы по порядку. Не инлайни SKILL.md в промпт.\n\n");
            foreach (var s in skills)
                sb.Append($"- /oh-my-claudecode:{s} (fallback: skills/{s}/SKILL.md)\n");
            sb.Append("\nВАЖНО: начни с первого режима немедленно.");
        }
        return sb.ToString();
    }
}
