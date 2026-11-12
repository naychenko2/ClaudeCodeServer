using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Composition;

namespace ClaudeHomeServer.Services.Tasks;

// Локальные ходы задач для счётчика в хвосте хода оркестратора. Ход идёт, если чат-исполнитель
// задачи (в ЛЮБОЙ фиче и любом чате владельца) сейчас ведёт ход (Working). Ожидание ответа
// человека (Waiting) ходом не считается: движок в этот момент не занят.
internal static class LocalExecutorLoad
{
    // Ход чата идёт прямо сейчас. Starting не годится: пустой новорождённый чат висит в нём до
    // первого сообщения (SessionLiveness) — это не ход.
    internal static bool IsTurnRunning(Session s) => s.Status == SessionStatus.Working;

    internal sealed record LocalTurn(string Name, string FeatureTitle, string Step);

    // Локальные ходы на движке: задачи InProgress с ведущим ход чатом, чей эффективный ход
    // локальный (шов LocalTurnOf). Считаются задачами, не персонами: одна карточка — одно
    // физическое место на движке; в счёт идут и ревью/доработки вне ростера исполнителей.
    internal static List<LocalTurn> LocalTurnsBy(
        IReadOnlyCollection<TaskItem> ownerTasks, ISessionDirectory sessions, ILocalExecutorRoster roster, string ownerId)
    {
        var turns = new List<LocalTurn>();
        foreach (var t in ownerTasks)
        {
            if (t.Status != TaskItemStatus.InProgress || t.LinkedSessionId is not { } sid) continue;
            if (sessions.GetById(sid) is not { } session || !IsTurnRunning(session)) continue;
            if (roster.LocalTurnOf(t, ownerId) is not { } turn) continue;
            var feature = t.FeatureId is { } fid ? ownerTasks.FirstOrDefault(x => x.Id == fid)?.Title : null;
            turns.Add(new LocalTurn(turn.PersonaName ?? t.Title, feature ?? t.Title, t.StepRef ?? t.Title));
        }
        return turns;
    }
}
