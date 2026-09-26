namespace ClaudeHomeServer.DeviceAgent.Supervision;

/// <summary>Дочерняя сторона контракта для боевого <c>run</c>: без супервизора — ничего не делает.</summary>
internal static class SupervisedRun
{
    /// <summary>Супервизор назвал себя — взвести гибель вместе с ним. false — его уже нет.</summary>
    public static bool ArmParentDeath() =>
        !int.TryParse(Environment.GetEnvironmentVariable(SupervisorContract.SupervisorPidEnv), out var pid)
        || ParentDeathSignal.Arm(pid);

    /// <summary>Успешный ack на hello — маркер healthy, по нему супервизор не откатывает версию.</summary>
    public static void MarkHealthy()
    {
        if (Environment.GetEnvironmentVariable(SupervisorContract.HealthyFileEnv) is not { Length: > 0 } file) return;
        AgentLayout.WriteAtomic(file, DateTime.UtcNow.ToString("O"));
    }
}
