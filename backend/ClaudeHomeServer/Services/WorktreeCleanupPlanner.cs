namespace ClaudeHomeServer.Services;

public enum CleanupAction { Skip, CleanArtifacts, RemoveWorktree }

public record CleanupDecision(CleanupAction Action, string Reason);

// Известное о worktree к моменту решения. IsMerged/IsClean — null, пока дорогая проверка
// через git не делалась или не удалась: «не знаем» никогда не равно «можно».
public record WorktreeFacts(
    string Path,
    bool IsPrimary,
    bool Exists,
    bool Locked,
    bool Busy,
    DateTime LastActivityUtc,
    bool HasArtifacts,
    bool? IsMerged = null,
    bool? IsClean = null,
    bool HasOpenTask = false);

// Выбор кандидатов — чистая логика без диска и git (тестируется напрямую). Два этапа,
// чтобы git-проверки (влито? чисто?) запускались только для того, что дожило до них.
public static class WorktreeCleanupPlanner
{
    /// <summary>
    /// Дешёвый этап: причина пропуска или null, если worktree — кандидат.
    /// Основное дерево, отсутствующее, занятое процессом и недавно менявшееся не трогаем.
    /// </summary>
    public static string? SkipReason(WorktreeFacts f, DateTime nowUtc, TimeSpan idle)
    {
        if (f.IsPrimary) return "основное дерево";
        if (!f.Exists) return "каталога нет";
        if (f.Busy) return "занят процессом";
        if (nowUtc - f.LastActivityUtc < idle) return "правки моложе порога";
        return null;
    }

    public static CleanupDecision Decide(WorktreeFacts f, DateTime nowUtc, TimeSpan idle, bool removeMerged)
    {
        if (SkipReason(f, nowUtc, idle) is { } reason) return new(CleanupAction.Skip, reason);

        if (removeMerged && !f.Locked && !f.HasOpenTask && f.IsMerged == true && f.IsClean == true)
            return new(CleanupAction.RemoveWorktree, "ветка влита, дерево чистое");

        return f.HasArtifacts
            ? new(CleanupAction.CleanArtifacts, "простаивает")
            : new(CleanupAction.Skip, "артефактов нет");
    }
}
