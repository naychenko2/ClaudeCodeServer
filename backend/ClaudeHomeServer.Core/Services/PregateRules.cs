using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services;

// Правила предгейта шага (NC-P1b): чистые функции без хранилища и git. Закрытие шага предгейт не блокирует —
// результат пишется в карточку и виден ревьюеру.
public static class PregateRules
{
    // Второй красный подряд по одной причине — исполнитель докладывает постановщику
    public const int MaxRedStreak = 2;

    // Предгейт запускается только для шага/доработки с собственным деревом
    public static bool Applies(string? role, string? worktreePath) =>
        role is TaskRoles.Step or TaskRoles.Rework && !string.IsNullOrWhiteSpace(worktreePath);

    // Новая запись по вердикту. Красный по той же причине (тот же отпечаток), что и предыдущий красный,
    // наращивает серию; зелёный и красный по новой причине начинают её заново
    public static PregateRecord Next(PregateRecord? previous, string sha, bool green, string summary,
        string? redKey, DateTime now, bool nothingApplicable = false)
    {
        if (nothingApplicable) return new PregateRecord(sha, false, summary, now, NothingApplicable: true);
        if (green) return new PregateRecord(sha, true, summary, now);
        var streak = previous is { Green: false, NothingApplicable: false } && previous.RedKey == redKey ? previous.RedStreak + 1 : 1;
        return new PregateRecord(sha, false, summary, now, streak, redKey);
    }

    public const string UiRoutesMarker = "UI-маршруты:";

    // Маршруты ui_probe — только то, что после маркера в строке постановки; никакого разбора остального текста
    public static IReadOnlyList<string> UiRoutes(string? description)
    {
        foreach (var raw in (description ?? "").Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith(UiRoutesMarker, StringComparison.OrdinalIgnoreCase)) continue;
            return [.. line[UiRoutesMarker.Length..].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)];
        }
        return [];
    }
}
