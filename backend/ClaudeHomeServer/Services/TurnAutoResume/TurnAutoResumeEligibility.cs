using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.TurnAutoResume;

/// <summary>
/// Результат отбора кандидата на авто-возобновление хода.
/// </summary>
/// <param name="Eligible">Кандидат проходит все проверки и может быть возобновлён.</param>
/// <param name="Reason">Код причины исключения (null, если Eligible). Используется в логах и метриках.</param>
public sealed record EligibilityResult(bool Eligible, string? Reason = null)
{
    public static readonly EligibilityResult Ok = new(true);

    public static EligibilityResult Exclude(string reason) => new(false, reason);
}

/// <summary>
/// Чистая функция отбора кандидатов на авто-возобновление хода после рестарта сервера
/// (флаг turn-auto-resume). Без side-effects, без await: принимает всё необходимое
/// как параметры. Вызывается фоновым сторожом после ApplicationStarted.
/// </summary>
public static class TurnAutoResumeEligibility
{
    // Коды причин исключения — константы для тестов и метрик.
    public const string ReasonAlreadyAttempted = "already_attempted";
    public const string ReasonTeamImplement = "team_implement";
    public const string ReasonGroupChat = "group_chat";
    public const string ReasonDeviceOffline = "device_offline_v1_excluded";
    public const string ReasonDeployLoop = "deploy_loop";
    public const string ReasonArchived = "archived";

    /// <summary>
    /// Проверяет, eligible ли сессия для авто-возобновления.
    /// </summary>
    /// <param name="session">Сессия-кандидат.</param>
    /// <param name="project">Проект сессии (null — чат вне проекта, считается серверным).</param>
    /// <param name="deviceOnline">Онлайн ли устройство локального проекта (актуально только при project.DeviceId != null).</param>
    /// <param name="lastTurnWasDeployOrRollback">Последний tool_use без result — deploy_start или deploy_rollback (петля выкатки).</param>
    public static EligibilityResult Check(Session session, Project? project, bool deviceOnline, bool lastTurnWasDeployOrRollback)
    {
        // 1. Уже была попытка возобновления — crash-loop guard.
        if (session.RestartResumeAttempts >= 1)
            return EligibilityResult.Exclude(ReasonAlreadyAttempted);

        // 2. Штаб «Командной реализации» — у него свой сторож.
        if (session.TeamImplement != null)
            return EligibilityResult.Exclude(ReasonTeamImplement);

        // 3. Групповой чат (2+ участника) — не возобновляем.
        if (session.Participants is { Count: >= 2 })
            return EligibilityResult.Exclude(ReasonGroupChat);

        // 4. Локальный проект с офлайн-устройством.
        if (project is { DeviceId: not null } && !deviceOnline)
            return EligibilityResult.Exclude(ReasonDeviceOffline);

        // 5. Петля выкатки: последний tool_use — deploy_start/deploy_rollback без result.
        if (lastTurnWasDeployOrRollback)
            return EligibilityResult.Exclude(ReasonDeployLoop);

        // 6. Чат в архиве.
        if (session.IsArchived)
            return EligibilityResult.Exclude(ReasonArchived);

        return EligibilityResult.Ok;
    }
}
