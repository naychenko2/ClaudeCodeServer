using System.Runtime.Versioning;
using System.Security.Principal;
using ClaudeHomeServer.DeviceAgent.Tray.Win32;
using ClaudeHomeServer.Protocol;

namespace ClaudeHomeServer.DeviceAgent.Tray;

/// <summary>
/// <c>ai-home-agent-tray</c> — значок агента устройства (руки, Ш7). Поднимает его
/// <c>ai-home-agent supervise</c> в сеансе пользователя и перезапускает при падении; с агентом
/// говорит только через pipe <see cref="HandsPipe"/>.
/// Коды выхода: 0 — обычный выход, <see cref="HandsTrayProcess.ExitAgentCode"/> — «Выйти из
/// агента», <see cref="HandsTrayProcess.AlreadyRunningCode"/> — трей уже запущен,
/// <see cref="HandsTrayProcess.UnsupportedCode"/> — не Windows.
/// </summary>
internal static class TrayProgram
{
    [STAThread]
    public static int Main()
    {
        if (!OperatingSystem.IsWindows()) return HandsTrayProcess.UnsupportedCode;
        return Run();
    }

    [SupportedOSPlatform("windows")]
    private static int Run()
    {
        // Один трей на пользователя в сеансе: второй значок того же агента только путал бы
        using var single = new Mutex(true, $@"Local\AiHomeAgent.TrayUi.{WindowsIdentity.GetCurrent().User!.Value}", out var first);
        if (!first) return HandsTrayProcess.AlreadyRunningCode;

        Win32.Native.SetProcessDpiAwarenessContext(Win32.Native.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        using var app = new TrayApp(HandsPipe.NameForCurrentUser());
        return app.Run();
    }
}
