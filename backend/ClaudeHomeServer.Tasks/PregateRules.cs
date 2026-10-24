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
        (role is TaskRoles.Step or TaskRoles.Rework) && !string.IsNullOrWhiteSpace(worktreePath);

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
        if (record.Green) return null;
        var text = $"Предгейт красный по коммиту {Short(record.Sha)}:\n{record.Summary}";
        return record.RedStreak >= MaxRedStreak
            ? text + $"\nВторой красный подряд по той же причине: докладывай постановщику (chats_report_up, blocker: true), "
                + "третьего круга нет."
            : text + "\nИсправь причину, закоммить и прогони pregate снова.";
    }

    // Новая запись по вердикту. Красный по той же причине (тот же отпечаток), что и предыдущий красный,
    // наращивает серию; зелёный и красный по новой причине начинают её заново
    public static PregateRecord Next(PregateRecord? previous, string sha, bool green, string summary,
        string? redKey, DateTime now)
    {
        if (green) return new PregateRecord(sha, true, "", now);
        var streak = previous is { Green: false } && previous.RedKey == redKey ? previous.RedStreak + 1 : 1;
        return new PregateRecord(sha, false, summary, now, streak, redKey);
    }

    // Предгейт больше не принимается: серия исчерпана — решение за постановщиком
    public static bool Exhausted(PregateRecord? record) =>
        record is { Green: false } && record.RedStreak >= MaxRedStreak;

    private static string Short(string sha) => sha.Length > 8 ? sha[..8] : sha;
}
