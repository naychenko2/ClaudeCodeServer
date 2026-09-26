namespace ClaudeHomeServer.DeviceAgent.Supervision;

/// <summary>Дочерняя сторона контракта для боевого <c>run</c>: без супервизора — ничего не делает.</summary>
internal static class SupervisedRun
{
    /// <summary>Супервизор назвал себя — взвести гибель вместе с ним. false — его уже нет.</summary>
    public static bool ArmParentDeath() =>
        !int.TryParse(Environment.GetEnvironmentVariable(SupervisorContract.SupervisorPidEnv), out var pid)
        || ParentDeathSignal.Arm(pid);

    /// <summary>Процесс поднят супервизором: выход с кодом 75 кто-то услышит.</summary>
    public static bool IsSupervised => SupervisorPid() is not null;

    /// <summary>
    /// Версия, из каталога которой запущен супервизор: он обновляется лениво, и уборка версий
    /// не должна удалить его каталог. null — не установить (нет pid-файла, процесс чужой).
    /// </summary>
    public static string? SupervisorVersion(AgentLayout layout)
    {
        try
        {
            if (SupervisorPid() is not { } pid || File.ReadAllText(layout.SupervisorPidFile).Trim() != pid.ToString()) return null;
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            var dir = Path.GetDirectoryName(process.MainModule?.FileName);
            var version = Path.GetFileName(dir);
            return dir is not null && Path.GetFileName(Path.GetDirectoryName(dir)) == SupervisorContract.VersionsDirName
                   && AgentLayout.IsValidVersion(version)
                ? version
                : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException
                                      or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static int? SupervisorPid() =>
        int.TryParse(Environment.GetEnvironmentVariable(SupervisorContract.SupervisorPidEnv), out var pid) ? pid : null;

    /// <summary>Успешный ack на hello — маркер healthy, по нему супервизор не откатывает версию.</summary>
    public static void MarkHealthy()
    {
        if (Environment.GetEnvironmentVariable(SupervisorContract.HealthyFileEnv) is not { Length: > 0 } file) return;
        AgentLayout.WriteAtomic(file, DateTime.UtcNow.ToString("O"));
    }
}
