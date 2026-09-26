using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ClaudeHomeServer.DeviceAgent.Supervision;

namespace ClaudeHomeServer.DeviceAgent.Install;

/// <summary>
/// Команда <c>ai-home-agent</c> по имени из любого терминала: её дают копировать подсказки UI
/// (<c>ai-home-agent roots add …</c>), а бинарь живёт в <c>versions/{v}</c>, путь которого
/// меняется с каждым обновлением. Шим стабилен и сам смотрит на активную версию.
/// Пишет только в места текущего пользователя, прав администратора не требует.
/// </summary>
internal interface ICommandShim
{
    /// <summary>Путь, по которому команда доступна по имени.</summary>
    string Location { get; }

    /// <summary>Поставить или обновить шим (идемпотентно); вернуть, что сказать человеку.</summary>
    IReadOnlyList<string> Install();

    /// <summary>Снять шим и то, что ради него прописано (PATH на Windows).</summary>
    void Remove();
}

internal static class CommandShims
{
    public const string CommandName = "ai-home-agent";

    public static ICommandShim ForCurrentOs(AgentLayout layout)
    {
        if (OperatingSystem.IsWindows()) return new WindowsCmdShim(layout, new WindowsUserPath());
        return new UnixLinkShim(layout, UnixLinkShim.DefaultBinDirectory(), Environment.GetEnvironmentVariable("PATH"));
    }
}

/// <summary>Пользовательский PATH Windows — отдельно, чтобы правку списка проверяли тесты на любой ОС.</summary>
internal interface IUserPathStore
{
    string? Get();
    void Set(string value);
}

/// <summary>
/// Windows: <c>{root}\bin\ai-home-agent.cmd</c> читает указатель <c>active</c> и зовёт
/// <c>versions\{v}\ai-home-agent.exe</c>; каталог <c>bin</c> дописывается в PATH пользователя
/// (<c>HKCU\Environment</c>). Новый PATH видят только новые окна терминала.
/// </summary>
internal sealed class WindowsCmdShim(AgentLayout layout, IUserPathStore userPath) : ICommandShim
{
    // Только ASCII: cmd читает файл в OEM-кодировке консоли, кириллица в нём превратится в мусор.
    // Ветка через goto, а не блок в скобках: скобка в пути корня закрыла бы блок раньше времени
    public const string Script =
        "@echo off\r\n" +
        "setlocal\r\n" +
        "set \"AIHOME_VERSION=\"\r\n" +
        "set /p AIHOME_VERSION=<\"%~dp0..\\active\"\r\n" +
        "if defined AIHOME_VERSION goto run\r\n" +
        "echo ai-home-agent: no active version in \"%~dp0..\\active\" 1>&2\r\n" +
        "exit /b 1\r\n" +
        ":run\r\n" +
        "\"%~dp0..\\versions\\%AIHOME_VERSION%\\ai-home-agent.exe\" %*\r\n" +
        "exit /b %ERRORLEVEL%\r\n";

    public string BinDirectory => Path.Combine(layout.Root, "bin");
    public string Location => Path.Combine(BinDirectory, CommandShims.CommandName + ".cmd");

    public IReadOnlyList<string> Install()
    {
        AgentLayout.WriteAtomic(Location, Script);
        var before = userPath.Get();
        var after = UserPathList.With(before, BinDirectory);
        if (after == before) return [$"Команда {CommandShims.CommandName}: {Location}"];
        userPath.Set(after);
        return [$"Команда {CommandShims.CommandName}: {Location}; каталог {BinDirectory} добавлен в PATH пользователя — " +
            "по имени команда доступна в новых окнах терминала"];
    }

    public void Remove()
    {
        File.Delete(Location);
        if (Directory.Exists(BinDirectory) && !Directory.EnumerateFileSystemEntries(BinDirectory).Any())
            Directory.Delete(BinDirectory);
        if (userPath.Get() is { } before && UserPathList.Without(before, BinDirectory) is var after && after != before)
            userPath.Set(after);
    }
}

/// <summary>Правка списка PATH через <c>;</c>: чужие элементы и их запись не трогаются.</summary>
internal static class UserPathList
{
    public static string With(string? path, string entry)
    {
        if (string.IsNullOrEmpty(path)) return entry;
        if (path.Split(';').Any(e => Same(e, entry))) return path;
        return path.TrimEnd(';') + ";" + entry;
    }

    public static string Without(string path, string entry)
    {
        var parts = path.Split(';');
        return parts.Any(e => Same(e, entry)) ? string.Join(';', parts.Where(e => !Same(e, entry))) : path;
    }

    private static bool Same(string element, string entry) =>
        string.Equals(Normalize(element), Normalize(entry), StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string element) => element.Trim().Trim('"').TrimEnd('\\', '/');
}

[SupportedOSPlatform("windows")]
internal sealed class WindowsUserPath : IUserPathStore
{
    private const string KeyPath = "Environment";
    private const string ValueName = "Path";

    public string? Get()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(KeyPath);
        // Без раскрытия: иначе %USERPROFILE% и прочее в PATH пользователя записались бы обратно раскрытыми
        return key?.GetValue(ValueName, null, Microsoft.Win32.RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
    }

    public void Set(string value)
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(KeyPath, writable: true);
        var kind = key.GetValueNames().Contains(ValueName, StringComparer.OrdinalIgnoreCase)
            ? key.GetValueKind(ValueName)
            : Microsoft.Win32.RegistryValueKind.ExpandString;
        key.SetValue(ValueName, value, kind);
        // Проводник и новые терминалы подхватят PATH без перевхода
        SendMessageTimeout(HwndBroadcast, WmSettingChange, 0, "Environment", SmtoAbortIfHung, 5000, out _);
    }

    private const nint HwndBroadcast = 0xffff;
    private const uint WmSettingChange = 0x001A;
    private const uint SmtoAbortIfHung = 0x0002;

    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", CharSet = CharSet.Unicode)]
    private static extern nint SendMessageTimeout(
        nint hWnd, uint msg, nuint wParam, string lParam, uint flags, uint timeout, out nuint result);
}

/// <summary>
/// Linux: симлинк <c>~/.local/bin/ai-home-agent</c> на <c>{root}/current/ai-home-agent</c> —
/// <c>current</c> переставляет само переключение версии, поэтому шим не переписывается.
/// Обычный файл на этом месте — чужой: его не трогаем.
/// </summary>
internal sealed class UnixLinkShim(AgentLayout layout, string binDirectory, string? pathVariable) : ICommandShim
{
    public static string DefaultBinDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin");

    public string Location => Path.Combine(binDirectory, CommandShims.CommandName);
    public string Target => Path.Combine(layout.CurrentLink, SupervisorContract.ExecutableName);

    public IReadOnlyList<string> Install()
    {
        var existing = new FileInfo(Location);
        if (existing.LinkTarget is null && existing.Exists)
            return [$"{Location} — чужой файл, команду {CommandShims.CommandName} не ставлю; агент запускается по пути {Target}"];

        if (existing.LinkTarget != Target)
        {
            Directory.CreateDirectory(binDirectory);
            File.Delete(Location);
            File.CreateSymbolicLink(Location, Target);
        }

        var notes = new List<string> { $"Команда {CommandShims.CommandName}: {Location} -> {Target}" };
        if (!InPath())
            notes.Add($"Каталога {binDirectory} нет в PATH этого терминала: войди в систему заново " +
                "(стандартный ~/.profile добавляет его, когда каталог есть) или добавь его в PATH сам; " +
                $"до тех пор — {Location}");
        return notes;
    }

    public void Remove()
    {
        if (new FileInfo(Location).LinkTarget == Target) File.Delete(Location);
    }

    private bool InPath() =>
        (pathVariable ?? "").Split(':').Any(e => e.Length > 0 && e.TrimEnd('/') == binDirectory.TrimEnd('/'));
}
