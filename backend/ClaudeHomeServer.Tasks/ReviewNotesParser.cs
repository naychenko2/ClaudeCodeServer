using System.Text.RegularExpressions;

namespace ClaudeHomeServer.Services.Tasks;

// Разбор notes ревью на блокеры и замечания (B05). Договорённость формата: каждый пункт — строка,
// начинающаяся (после маркера списка «-», «*», «•», «1.») с тега: «блокер» / «blocker» / «major» /
// «critical» — блокер; «замечание» / «remark» / «minor» / «nit» — замечание. Тег может стоять в
// скобках и заканчиваться «:» или тире. В тексте блокера «файл.расш:строка» выделяется в file/line.
// Реальный формат notes ревьюера — одна строка «<вердикт>. Причина: <фраза>. До мержа: <п1>; <п2> | нет»:
// секция «До мержа:» режется по «;» в блокеры (тег «блокер», file:line — из текста), «нет» — пусто;
// строка с такой секцией даёт Structured=true. Тег блокера с текстом «нет» («Блокеров нет») — не блокер.
// Нет ни одной размеченной строки — структуры нет: blockers пуст, remarks = весь текст одним пунктом.
public static class ReviewNotesParser
{
    private static readonly Regex Line = new(
        @"^\s*(?:[-*•]|\d+[.)])?\s*[\[(]?(?<tag>блокер\w*|blocker|major|critical|замечани\w*|remark|minor|nit)\b[\])]?\s*[:—–-]?\s*(?<text>.*)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex Location = new(
        @"(?<file>[\w./\\-]+\.\w{1,6}):(?<line>\d+)", RegexOptions.Compiled);

    private static readonly Regex BeforeMerge = new(
        @"(?<!«)До\s+мержа\s*:", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly string[] BlockerTags = ["блокер", "blocker", "major", "critical"];

    public static (List<StageReviewBlocker> Blockers, List<string> Remarks, bool Structured) Parse(string? notes)
    {
        var blockers = new List<StageReviewBlocker>();
        var remarks = new List<string>();
        if (string.IsNullOrWhiteSpace(notes)) return (blockers, remarks, false);

        var tagged = false;
        foreach (var raw in notes.Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var m = Line.Match(raw);
            // секция — хвост строки: «Причина» может цитировать сам формат, поэтому берём ПОСЛЕДНЕЕ
            // совпадение (цитата в кавычках «До мержа:» отсекается lookbehind'ом)
            if (!m.Success && BeforeMerge.Matches(raw) is { Count: > 0 } bms)
            {
                tagged = true;
                foreach (var item in raw[(bms[^1].Index + bms[^1].Length)..].Split(';'))
                    AddBlocker(blockers, "блокер", item.Trim());
                continue;
            }
            if (!m.Success)
            {
                remarks.Add(raw.Trim());
                continue;
            }
            tagged = true;
            var tag = m.Groups["tag"].Value.ToLowerInvariant();
            var text = m.Groups["text"].Value.Trim();
            if (!BlockerTags.Any(tag.StartsWith)) { remarks.Add(text); continue; }

            AddBlocker(blockers, tag, text);
        }
        if (tagged) return (blockers, remarks, true);
        // структуры нет: весь текст — одно замечание
        return ([], [notes.Trim()], false);
    }

    private static void AddBlocker(List<StageReviewBlocker> blockers, string tag, string text)
    {
        if (IsNone(text)) return;
        string? file = null;
        int? line = null;
        if (Location.Match(text) is { Success: true } loc && int.TryParse(loc.Groups["line"].Value, out var n))
        {
            file = loc.Groups["file"].Value;
            line = n;
            text = (text[..loc.Index] + text[(loc.Index + loc.Length)..]).Trim().TrimStart('—', '–', '-', ':', ' ');
        }
        blockers.Add(new StageReviewBlocker(tag, file, line, text));
    }

    // «нет» / «нет.» / «—» вместо пункта — блокеров нет
    private static bool IsNone(string text)
    {
        var t = text.Trim().TrimEnd('.', '!').Trim();
        return t.Length == 0 || t.Equals("нет", StringComparison.OrdinalIgnoreCase);
    }
}
