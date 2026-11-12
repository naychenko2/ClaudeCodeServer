using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Composition;

namespace ClaudeHomeServer.Services.Tasks;

// Занятость локальных исполнителей: общая для счётчика в хвосте хода оркестратора и для пинка
// простоя — второго правила «занят» нет. Занята персона, у которой есть задача (в ЛЮБОЙ фиче и
// любом чате владельца), чей чат-исполнитель сейчас ведёт ход (Working). Ожидание ответа
// человека (Waiting) занятостью не считается: ходом исполнитель в этот момент не занят.
internal static class LocalExecutorLoad
{
    internal sealed record BusyEntry(string FeatureTitle, string Step);

    // Ход чата идёт прямо сейчас. Starting не годится: пустой новорождённый чат висит в нём до
    // первого сообщения (SessionLiveness) — это не ход.
    internal static bool IsTurnRunning(Session s) => s.Status == SessionStatus.Working;

    internal sealed record LocalTurn(string Name, string FeatureTitle, string Step);

    // Локальные ходы на движке: задачи того же признака занятости (InProgress + чат Working),
    // чей эффективный ход локальный (шов LocalTurnOf). Считаются задачами, не персонами:
    // одна карточка — одно физическое место на движке; в счёт идут и ревью/доработки вне
    // ростера исполнителей. Пинка простоя это не касается: он ведёт учёт по персонам.
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

    // personaId → чем занята (пусто — свободна). В словаре только занятые.
    internal static Dictionary<string, List<BusyEntry>> BusyBy(
        IReadOnlyCollection<TaskItem> ownerTasks, IReadOnlyCollection<LocalExecutor> roster, ISessionDirectory sessions)
    {
        var ids = roster.Select(r => r.PersonaId).ToHashSet();
        var busy = new Dictionary<string, List<BusyEntry>>();
        foreach (var t in ownerTasks)
        {
            if (t.PersonaId is not { } personaId || !ids.Contains(personaId)) continue;
            if (t.Status != TaskItemStatus.InProgress || t.LinkedSessionId is not { } sid) continue;
            if (sessions.GetById(sid) is not { } session || !IsTurnRunning(session)) continue;
            var feature = t.FeatureId is { } fid ? ownerTasks.FirstOrDefault(x => x.Id == fid)?.Title : null;
            if (!busy.TryGetValue(personaId, out var list)) busy[personaId] = list = [];
            list.Add(new BusyEntry(feature ?? t.Title, t.StepRef ?? t.Title));
        }
        return busy;
    }
}
