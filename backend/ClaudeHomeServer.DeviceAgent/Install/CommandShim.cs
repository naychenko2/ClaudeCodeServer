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

    /// <summary>
    /// Снять шим и то, что ради него прописано (PATH на Windows). <paramref name="withRoot"/> —
    /// заодно корень установки (<c>--purge</c>), если удалить его сейчас нельзя из-за самого шима.
    /// <see cref="ShimRemoval.Deferred"/> только планирует удаление — запускает его
    /// <see cref="StartDeferredRemoval"/>.
    /// </summary>
    ShimRemoval Remove(bool withRoot = false);

    /// <summary>
    /// Запустить удаление, отложенное <see cref="Remove"/>; вызывать последним действием процесса,
    /// когда больше ничего не пишется на диск. false — не запустилось, удалять придётся руками.
    /// </summary>
    bool StartDeferredRemoval();
}

internal enum ShimRemoval
{
    Removed,
    /// <summary>Шима уже нет: сняли раньше или не ставили.</summary>
    AlreadyGone,
    /// <summary>Файл удалит отложенная команда, когда вызвавший через шим cmd отпустит его.</summary>
    Deferred,
}

internal static class CommandShims
{
    public const string CommandName = "ai-home-agent";

    public static ICommandShim ForCurrentOs(AgentLayout layout)
    {
        if (OperatingSystem.IsWindows())
            return new WindowsCmdShim(layout, new WindowsUserPath(), new WindowsDetachedStarter(),
                () => Environment.GetEnvironmentVariable(WindowsCmdShim.VersionVariable) is not null);
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
///
/// Снятие через сам шим (<c>ai-home-agent uninstall</c>): cmd перечитывает .cmd после каждой
/// строки, и удалённый из-под него файл давал «Системе не удается найти указанный путь» и
/// exit=1. Поэтому под шимом файл удаляет отсоединённая команда, запущенная последним действием
/// uninstall: она ждёт выхода процесса агента по PID (с потолком) и ещё секунду, пока cmd
/// дочитает шим; признак «под шимом» — переменная, которую шим ставит всегда.
/// </summary>
internal sealed class WindowsCmdShim(
    AgentLayout layout, IUserPathStore userPath, IDetachedStarter? deferred = null, Func<bool>? runningUnderShim = null) : ICommandShim
{
    /// <summary>Переменная, которую шим выставляет перед вызовом exe (и в прежних версиях шима тоже).</summary>
    public const string VersionVariable = "AIHOME_VERSION";

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

    /// <summary>Сколько отложенная команда ждёт выхода процесса агента, прежде чем удалять всё равно.</summary>
    public static readonly TimeSpan ExitWaitCap = TimeSpan.FromSeconds(60);

    private bool? _pendingWithRoot;

    public ShimRemoval Remove(bool withRoot = false)
    {
        if (userPath.Get() is { } before && UserPathList.Without(before, BinDirectory) is var after && after != before)
            userPath.Set(after);

        if (!File.Exists(Location))
        {
            DeleteBinIfEmpty();
            return ShimRemoval.AlreadyGone;
        }

        if (deferred is not null && runningUnderShim?.Invoke() == true)
        {
            // В cmd /c «%» не экранируется: %имя% в пути подставилось бы, и rmdir ушёл бы не туда
            if (layout.Root.Contains('%'))
                throw new IOException($"в пути {layout.Root} есть «%», отложенное удаление через cmd исказило бы его");
            _pendingWithRoot = withRoot;
            return ShimRemoval.Deferred;
        }

        File.Delete(Location);
        DeleteBinIfEmpty();
        return ShimRemoval.Removed;
    }

    public bool StartDeferredRemoval()
    {
        if (_pendingWithRoot is not { } withRoot || deferred is null) return false;
        _pendingWithRoot = null;
        var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        var start = deferred.Start(cmd, ["/d", "/s", "/c", DeferredCommand(withRoot, Environment.ProcessId)], Path.GetTempPath());
        return start.Status == DetachedStartStatus.Started;
    }

    /// <summary>
    /// Текст для <c>cmd /d /s /c</c>: ждать выхода процесса <paramref name="waitForPid"/> (не
    /// дольше <see cref="ExitWaitCap"/>; нет PowerShell — фиксированные 3 с), ещё секунду, пока
    /// cmd дочитает шим, затем удалить. <c>/s</c> снимает только внешние кавычки — пути внутри
    /// остаются в своих.
    /// </summary>
    public string DeferredCommand(bool withRoot, int waitForPid)
    {
        var powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        return $"\"{powershell}\" -NoProfile -NonInteractive -Command \"Wait-Process -Id {waitForPid} " +
            $"-Timeout {(int)ExitWaitCap.TotalSeconds} -ErrorAction SilentlyContinue; exit 0\" 2>nul " +
            "|| ping -n 4 127.0.0.1 >nul & ping -n 2 127.0.0.1 >nul & " + (withRoot
                ? $"rmdir /s /q \"{layout.Root}\""
                : $"del /f /q \"{Location}\" & rmdir \"{BinDirectory}\" 2>nul");
    }

    private void DeleteBinIfEmpty()
    {
        if (Directory.Exists(BinDirectory) && !Directory.EnumerateFileSystemEntries(BinDirectory).Any())
            Directory.Delete(BinDirectory);
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

    // Симлинк под исполняемым процессом Unix удаляет без последствий — откладывать нечего
    public ShimRemoval Remove(bool withRoot = false)
    {
        if (new FileInfo(Location).LinkTarget != Target) return ShimRemoval.AlreadyGone;
        File.Delete(Location);
        return ShimRemoval.Removed;
    }

    public bool StartDeferredRemoval() => false;

    private bool InPath() =>
        (pathVariable ?? "").Split(':').Any(e => e.Length > 0 && e.TrimEnd('/') == binDirectory.TrimEnd('/'));
}
