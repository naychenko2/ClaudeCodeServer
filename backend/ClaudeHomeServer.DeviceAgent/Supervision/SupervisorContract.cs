namespace ClaudeHomeServer.DeviceAgent.Supervision;

/// <summary>
/// Контракт «супервизор ↔ дочерний» (agent-distribution Р7). ЗАМОРОЖЕН: супервизор
/// обновляется лениво (при следующем входе), поэтому супервизор версии N обязан поднимать
/// дочерний N+k. Любая правка здесь ломает уже установленных агентов — держит тест
/// совместимости с фикстурой, где эти значения записаны литералами.
///
///   {root}/versions/{v}/ai-home-agent(.exe) run  — дочерний, аргументы ровно «run»
///   {root}/active                               — имя активной версии
///   {root}/previous                             — имя прошлой версии (для отката)
///   {root}/versions/{v}/healthy                 — версия получила успешный ack на hello
///   {root}/versions/{v}/bad                     — версия откачена (UTC-время в файле)
///   код выхода 75                               — «переключись»: перечитать active
/// </summary>
internal static class SupervisorContract
{
    public const int SwitchExitCode = 75;

    public const string ChildCommand = "run";

    /// <summary>Куда дочернему писать маркер healthy после успешного ack на hello.</summary>
    public const string HealthyFileEnv = "AI_HOME_AGENT_HEALTHY_FILE";

    /// <summary>PID супервизора: дочерний сверяет с ним родителя и гибнет вместе с ним.</summary>
    public const string SupervisorPidEnv = "AI_HOME_AGENT_SUPERVISOR_PID";

    public const string VersionsDirName = "versions";
    public const string ActiveFileName = "active";
    public const string PreviousFileName = "previous";
    public const string HealthyFileName = "healthy";
    public const string BadFileName = "bad";

    public static string ExecutableName => OperatingSystem.IsWindows() ? "ai-home-agent.exe" : "ai-home-agent";
}
