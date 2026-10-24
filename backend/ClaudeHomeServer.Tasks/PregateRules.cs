using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.Tasks;

// Правила гейта «предгейт перед закрытием шага» (NC-P1b): чистые функции без хранилища и git.
// Единственное место решения «можно ли закрыть» — вызывает только TaskManager на переходе в Done.
public static class PregateRules
{
    // Сколько красных подряд по одной причине допускается: на втором кругу исполнитель докладывает
    // постановщику, третьего круга нет
    public const int MaxRedStreak = 2;

    // Гейт касается только шага/доработки с собственным деревом
    public static bool Applies(string? role, string? worktreePath) =>
        IsGatedRole(role) && !string.IsNullOrWhiteSpace(worktreePath);

    // Путь дерева, по которому гейт сверяет коммит при закрытии; null — гейт не касается. Касается, если роль
    // была ИЛИ стала step/rework и дерево было ИЛИ стало непустым: иначе «step→cut» или сброс дерева
    // в том же запросе, что и закрытие, открывали бы обход. Подмену одного дерева другим ловит
    // TreeSwapRefusal — GatedPath возвращает «до»
    public static string? GatedPath(string? roleBefore, string? roleAfter, string? pathBefore, string? pathAfter)
    {
        if (!IsGatedRole(roleBefore) && !IsGatedRole(roleAfter)) return null;
        return !string.IsNullOrWhiteSpace(pathBefore) ? pathBefore
            : !string.IsNullOrWhiteSpace(pathAfter) ? pathAfter : null;
    }

    // Дерево «до» и «после» одного запроса не должно расходиться при закрытии: результат предгейта привязан к
    // коммиту дерева задачи, а закрыться задача хочет уже с другим. null — расхождения нет
    public static string? TreeSwapRefusal(string? pathBefore, string? pathAfter)
    {
        if (string.IsNullOrWhiteSpace(pathBefore) || string.IsNullOrWhiteSpace(pathAfter)) return null;
        return string.Equals(pathBefore.TrimEnd('/', '\\'), pathAfter.TrimEnd('/', '\\'), StringComparison.Ordinal)
            ? null
            : "Предгейт: рабочее дерево шага меняется в том же запросе, что и закрытие — результат привязан к коммиту "
                + "прежнего дерева. Смени дерево отдельным запросом, прогони pregate по нему и закрывай снова.";
    }

    private static bool IsGatedRole(string? role) => role is TaskRoles.Step or TaskRoles.Rework;

    // null — закрывать можно; иначе текст отказа с выводом гейта. head — текущий sha дерева
    // (null — прочитать не удалось: закрыть нельзя)
    public static string? Refusal(PregateRecord? record, string? head)
    {
        if (head is null)
            return "Предгейт: не удалось определить текущий коммит рабочего дерева шага — закрытие отклонено.";
        if (record is null)
            return "Предгейт не пройден: у шага нет результата. Вызови инструмент pregate (или POST /api/tasks/{id}/pregate), "
                + "добейся зелёного результата и закрывай задачу снова.";
        if (!string.Equals(record.Sha, head, StringComparison.OrdinalIgnoreCase))
            return $"Предгейт устарел: он прогнан по коммиту {Short(record.Sha)}, а дерево сейчас на {Short(head)}. "
                + "Закоммить правки, прогони pregate заново и закрывай снова.";
        if (record.Green || record.NothingApplicable) return null;
        var text = $"Предгейт красный по коммиту {Short(record.Sha)}:\n{record.Summary}";
        return record.RedStreak >= MaxRedStreak
            ? text + $"\nВторой красный подряд по той же причине: докладывай постановщику (chats_report_up, blocker: true), "
                + "третьего круга нет."
            : text + "\nИсправь причину, закоммить и прогони pregate снова.";
    }

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

    // Предгейт больше не принимается: серия исчерпана — решение за постановщиком
    public static bool Exhausted(PregateRecord? record) =>
        record is { Green: false, NothingApplicable: false } && record.RedStreak >= MaxRedStreak;

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

    private static string Short(string sha) => sha.Length > 8 ? sha[..8] : sha;
}
