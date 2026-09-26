using ClaudeHomeServer.DeviceAgent.Supervision;

namespace ClaudeHomeServer.DeviceAgent.Install;

/// <summary>Итог регистрации автозапуска: прописан или нет, плюс что сказать человеку.</summary>
internal sealed record AutostartResult(bool Registered, IReadOnlyList<string> Notes);

internal enum SupervisorStartStatus
{
    /// <summary>Супервизор запущен и переживёт закрытие окна или сессии установки.</summary>
    Started,
    /// <summary>Запустить отсоединённо нельзя (breakaway из Job запрещён) — поднимется при следующем входе.</summary>
    Deferred,
    /// <summary>Автозапуска нет (нет <c>systemd --user</c>) — запуск руками, инструкция в тексте.</summary>
    Manual,
}

internal sealed record SupervisorStart(SupervisorStartStatus Status, string Message);

/// <summary>
/// Автозапуск супервизора (Р6): Windows — значение в <c>HKCU\…\Run</c>, Linux — unit
/// <c>systemd --user</c> на симлинк <c>current</c>. Пишет только в места текущего
/// пользователя, прав администратора не требует.
/// </summary>
internal interface IAutostart
{
    string Describe { get; }

    /// <summary>Прописать автозапуск супервизора версии; <paramref name="alwaysOn"/> — жить без входа (Linux: linger).</summary>
    AutostartResult Register(string version, bool alwaysOn);

    /// <summary>Откат или переключение: существующую запись перевести на версию. Записи нет — ничего.</summary>
    void Repoint(string version);

    /// <summary>Запустить супервизор сейчас, отсоединённо от консоли и сессии установки.</summary>
    SupervisorStart StartNow(string version);

    /// <summary>Снять автозапуск и остановить то, чем он управляет.</summary>
    void Unregister();
}

internal static class Autostarts
{
    public const string SupervisorCommand = "supervise";

    public static IAutostart ForCurrentOs(AgentLayout layout)
    {
        if (OperatingSystem.IsWindows())
            return new WindowsRunAutostart(layout, new WindowsRunRegistry(), new WindowsDetachedStarter());
        return new SystemdUserAutostart(layout, new ProcessCommandRunner(), SystemdUserAutostart.DefaultUnitDirectory(),
            SystemdUserAutostart.InheritedEnvironment());
    }
}
