using System.Text.RegularExpressions;
using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services;

// Парсер чекбоксов markdown с метаданными в стиле Obsidian Tasks (флаг notes-task-sync).
// Понимает: `- [ ] текст 📅 2026-07-10 🔁 every week ⏳ 2026-07-09`.
// Маркеры списка -, *, +; состояние [ ] / [x]; дата срока 📅; правило повтора 🔁.
public static partial class NoteTaskParser
{
    [GeneratedRegex(@"^(\s*)[-*+]\s+\[([ xX])\]\s+(.*)$")]
    private static partial Regex CheckboxRe();

    [GeneratedRegex(@"📅\s*(\d{4}-\d{2}-\d{2})")]
    private static partial Regex DueRe();

    [GeneratedRegex(@"🔁\s*([^📅⏳⏫🔼🔽⏬✅❌]+)")]
    private static partial Regex RecurRe();

    // Токены-метаданные, которые вырезаются из заголовка задачи.
    // ВАЖНО: эмодзи приоритета перечислены альтернацией полных литералов, НЕ классом
    // символов [⏫🔼🔽⏬] — в .NET класс символов работает над UTF-16 code units и
    // разложил бы астральные эмодзи на одиночные суррогаты (D83D), из-за чего вырезался
    // бы старший суррогат любого эмодзи в тексте (🚀🔥🐛…) и заголовок бился бы.
    [GeneratedRegex(@"(📅|⏳|🛫|➕|✅|❌)\s*\d{4}-\d{2}-\d{2}|🔁\s*[^📅⏳⏫🔼🔽⏬✅❌]+|⏫|🔼|🔽|⏬")]
    private static partial Regex MetaRe();

    // Разбор всех чекбоксов заметки (индекс строки 0-based в исходном контенте)
    public static IReadOnlyList<NoteTaskLine> Parse(string content)
    {
        var lines = content.Replace("\r\n", "\n").Split('\n');
        var result = new List<NoteTaskLine>();
        for (var i = 0; i < lines.Length; i++)
        {
            var m = CheckboxRe().Match(lines[i]);
            if (!m.Success) continue;

            var body = m.Groups[3].Value;
            var done = m.Groups[2].Value is "x" or "X";
            var due = DueRe().Match(body) is { Success: true } d ? d.Groups[1].Value : null;
            var recur = ParseRecurrence(
                RecurRe().Match(body) is { Success: true } r ? r.Groups[1].Value.Trim() : null);
            var text = MetaRe().Replace(body, "").Replace("  ", " ").Trim();
            if (text.Length == 0) text = body.Trim();

            result.Add(new NoteTaskLine(i, text, done, due, recur));
        }
        return result;
    }

    // Поставить/снять галочку на строке (0-based). Возвращает новый контент или null,
    // если строка не чекбокс / индекс вне диапазона.
    public static string? SetChecked(string content, int line, bool done)
    {
        var nl = content.Contains("\r\n") ? "\r\n" : "\n";
        var lines = content.Replace("\r\n", "\n").Split('\n').ToList();
        if (line < 0 || line >= lines.Count) return null;

        var m = CheckboxRe().Match(lines[line]);
        if (!m.Success) return null;

        var current = m.Groups[2].Value is "x" or "X";
        if (current == done) return content; // уже в нужном состоянии

        // Заменяем только состояние скобок, не трогая остальную строку
        var idx = lines[line].IndexOf('[');
        if (idx < 0 || idx + 2 >= lines[line].Length) return null;
        var chars = lines[line].ToCharArray();
        chars[idx + 1] = done ? 'x' : ' ';
        lines[line] = new string(chars);
        return string.Join(nl, lines);
    }

    // Грубый разбор правила повтора Obsidian Tasks: "every week", "weekly", "every 2 days"…
    private static TaskRecurrence? ParseRecurrence(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.Trim().ToLowerInvariant();

        var interval = 1;
        var numMatch = NumberRe().Match(s);
        if (numMatch.Success && int.TryParse(numMatch.Value, out var n) && n > 0) interval = n;

        TaskRecurrenceType? type = s switch
        {
            _ when s.Contains("day") || s.Contains("daily") => TaskRecurrenceType.Daily,
            _ when s.Contains("week") => TaskRecurrenceType.Weekly,
            _ when s.Contains("month") => TaskRecurrenceType.Monthly,
            _ when s.Contains("year") => TaskRecurrenceType.Yearly,
            _ => null,
        };
        return type is null ? null : new TaskRecurrence { Type = type.Value, Interval = interval };
    }

    [GeneratedRegex(@"\d+")]
    private static partial Regex NumberRe();
}

// Разобранная строка-чекбокс заметки
public record NoteTaskLine(int Line, string Text, bool Done, string? Due, TaskRecurrence? Recurrence);
