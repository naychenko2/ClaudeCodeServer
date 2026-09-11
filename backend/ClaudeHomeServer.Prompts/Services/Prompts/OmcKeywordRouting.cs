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
//
// Гарды контекста (2026-08-03, по спецификации docs/research/omc-hooks-tradeoff.md §4):
// раньше контекстный гард был только у wiki, остальные слова срабатывали на голом
// упоминании — обсуждение механики («а как работает autopilot?») само себя запускало.
// Три гарда применяются к каждому «тяжёлому» слову (Gated=true — все, кроме cancel,
// который уже предельно буквален, и wiki, у которого свой паттерн с действием):
//   1. эхо-гард — вырезаем наш же прежде впрыснутый блок [МАГИЧЕСК…], иначе цитата
//      подсказки в новом сообщении (постановка задачи, лог хода) реактивирует скилл;
//   2. глагол запуска в ±80 символах (рус+англ) — упоминание без команды не считается;
//   3. гард обсуждения — вопрос или маркер разбора («стоит ли», «сравни», «почему») рядом
//      гасит совпадение, даже если формальный глагол запуска тоже нашёлся.
public static class OmcKeywordRouting
{
    // Магслово → имя OMC-скилла. Порядок = приоритет вывода (как priorityOrder хука).
    // Gated — применять ли три контекстных гарда ниже (false — только граница слова,
    // как раньше: cancel уже однозначен, wiki уже гейтится собственным паттерном действия).
    private static readonly (string Skill, Regex Pattern, bool Gated)[] Keywords =
    [
        ("cancel",          Word(@"cancelomc|stopomc"), false),
        ("ralph",           Word(@"ralph"), true),
        ("ultragoal",       Word(@"ultragoal"), true),
        ("autopilot",       Word(@"autopilot|auto[\s-]?pilot|full\s?auto|fullsend"), true),
        ("ultrawork",       Word(@"ultrawork|ulw"), true),
        ("ccg",             Word(@"ccg|claude-codex-gemini"), true),
        ("ralplan",         Word(@"ralplan"), true),
        ("deep-interview",  Word(@"deep[\s-]interview|ouroboros"), true),
        ("ai-slop-cleaner", Word(@"ai[\s-]?slop|deslop"), true),
        // wiki — только с действием (wiki this/add/lint/query): голое «wiki» слишком часто
        // встречается в обычной речи («посмотри вики», «wiki page») и давало ложный запуск.
        ("wiki",            Word(@"wiki\s+(?:this|add|lint|query)"), false),
    ];

    // Граница по буквам/цифрам (юникод) — как MagicWordRe в OmcPersonaRouting:
    // «ulw» внутри других слов не считается магсловом.
    private static Regex Word(string alts) =>
        new($@"(?<![\p{{L}}\p{{N}}])(?:{alts})(?![\p{{L}}\p{{N}}])",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Ширина окна контекста вокруг совпадения (символов в каждую сторону) — гард 2.
    private const int ContextWindow = 80;

    // Гард 2: явный глагол запуска рядом со словом — рус+англ, императив.
    private static readonly Regex LaunchVerbRegex = new(
        @"(?<![\p{L}\p{N}])(?:запусти|включи|активируй|стартуй|погнали|" +
        @"run|start|use|enable|activate|launch)(?![\p{L}\p{N}])",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Гард 3: маркеры обсуждения/разбора — упоминание в вопросе или сравнении не команда.
    private static readonly Regex DiscussionMarkerRegex = new(
        @"(?<![\p{L}\p{N}])(?:обсуди|стоит ли|разбери|сравни|почему|что такое|нужно ли|" +
        @"документац|статья|vs)(?![\p{L}\p{N}])",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Гард 1 (эхо): наш собственный блок [МАГИЧЕСКОЕ СЛОВО: …]/[МАГИЧЕСКИЕ СЛОВА: …] целиком,
    // от заголовка до завершающего «ВАЖНО: начни …» (общий хвост обоих вариантов BuildKeywordHint).
    private static readonly Regex EchoBlockRegex = new(
        @"\[МАГИЧЕСК[^\]]*\][\s\S]*?ВАЖНО:\s*начни[^\n]*\.",
        RegexOptions.Compiled);

    // Обнаруженные скиллы в порядке приоритета (без дублей). Пустой список — ничего не найдено.
    public static IReadOnlyList<string> DetectSkills(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        var stripped = EchoBlockRegex.Replace(text, " ");

        var found = new List<string>();
        foreach (var (skill, re, gated) in Keywords)
        {
            if (!gated)
            {
                if (re.IsMatch(stripped)) found.Add(skill);
                continue;
            }
            foreach (Match m in re.Matches(stripped))
            {
                if (!ActivatedByContext(stripped, m)) continue;
                found.Add(skill);
                break;
            }
        }
        return found;
    }

    // Совпадение считается командой, если рядом (±ContextWindow) есть глагол запуска
    // и НЕТ вопроса/маркера обсуждения — оба условия проверяются в одном окне.
    private static bool ActivatedByContext(string text, Match m)
    {
        var winStart = Math.Max(0, m.Index - ContextWindow);
        var winEnd = Math.Min(text.Length, m.Index + m.Length + ContextWindow);
        var window = text[winStart..winEnd];
        if (window.Contains('?')) return false;
        if (DiscussionMarkerRegex.IsMatch(window)) return false;
        return LaunchVerbRegex.IsMatch(window);
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
