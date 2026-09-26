using System.Runtime.Versioning;
using ClaudeHomeServer.DeviceAgent.Supervision;

namespace ClaudeHomeServer.DeviceAgent.Install;

/// <summary>Значение автозапуска в реестре — отдельно, чтобы генерацию записи проверяли тесты на любой ОС.</summary>
internal interface IRunRegistry
{
    string Location { get; }
    string? Get();
    void Set(string command);
    void Delete();
}

/// <summary>
/// Windows: <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c> (Р6). Без админа,
/// запись значения атомарна. Цена — агент живёт, пока пользователь вошёл в систему.
/// Запись указывает на <c>versions/{v}/ai-home-agent.exe supervise</c>: сам супервизор
/// обновляется лениво, при следующем входе.
/// </summary>
internal sealed class WindowsRunAutostart(AgentLayout layout, IRunRegistry registry, IDetachedStarter starter) : IAutostart
{
    public const string AlwaysOnNote =
        "на Windows агент работает, пока пользователь вошёл в систему; режим без входа (служба Windows) не поддерживается";

    public string Describe => registry.Location;

    /// <summary>Командная строка записи Run: путь в кавычках (в нём бывают пробелы) и команда супервизора.</summary>
    public static string CommandFor(string executable)
    {
        if (executable.Contains('"')) throw new ArgumentException("кавычка в пути к агенту", nameof(executable));
        return $"\"{executable}\" {Autostarts.SupervisorCommand}";
    }

    public AutostartResult Register(string version, bool alwaysOn)
    {
        registry.Set(CommandFor(layout.ExeOf(version)));
        return new AutostartResult(true, alwaysOn ? [AlwaysOnNote] : []);
    }

    public void Repoint(string version)
    {
        if (registry.Get() is not null) registry.Set(CommandFor(layout.ExeOf(version)));
    }

    public SupervisorStart StartNow(string version)
    {
        var result = starter.Start(layout.ExeOf(version), [Autostarts.SupervisorCommand], layout.VersionDir(version));
        return result.Status switch
        {
            DetachedStartStatus.Started => new(SupervisorStartStatus.Started, $"супервизор запущен, pid {result.ProcessId}"),
            DetachedStartStatus.BreakawayDenied => new(SupervisorStartStatus.Deferred,
                "окно установки не отпускает дочерние процессы (Job без breakaway), поэтому сейчас агент не запущен: " +
                "он запустится сам при следующем входе в систему"),
            _ => new(SupervisorStartStatus.Deferred,
                $"супервизор не запустился ({result.Error}); агент запустится при следующем входе в систему"),
        };
    }

    public void Unregister() => registry.Delete();
}

[SupportedOSPlatform("windows")]
internal sealed class WindowsRunRegistry : IRunRegistry
{
    public const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ValueName = "AiHomeAgent";

    public string Location => $@"HKCU\{KeyPath}\{ValueName}";

    public string? Get()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(KeyPath);
        return key?.GetValue(ValueName) as string;
    }

    public void Set(string command)
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(KeyPath, writable: true);
        key.SetValue(ValueName, command, Microsoft.Win32.RegistryValueKind.String);
    }

    public void Delete()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(KeyPath, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
