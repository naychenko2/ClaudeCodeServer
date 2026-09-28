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
    public static int Main(string[] args)
    {
        if (!OperatingSystem.IsWindows()) return HandsTrayProcess.UnsupportedCode;
        return Run(TrayOptions.Parse(args));
    }

    [SupportedOSPlatform("windows")]
    private static int Run(TrayOptions options)
    {
        // Один трей на pipe агента в сеансе: второй значок того же агента только путал бы
        var sid = WindowsIdentity.GetCurrent().User!.Value;
        var pipe = options.Pipe ?? HandsPipe.NameForCurrentUser();
        var mutex = options.Pipe is null ? $@"Local\AiHomeAgent.TrayUi.{sid}" : $@"Local\AiHomeAgent.TrayUi.{sid}.{pipe}";
        using var single = new Mutex(true, mutex, out var first);
        if (!first) return HandsTrayProcess.AlreadyRunningCode;

        Native.SetProcessDpiAwarenessContext(Native.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        using var app = new TrayApp(pipe, new TrayStateStore(options.DataDirectory ?? TrayStateStore.DefaultDirectory()));
        return app.Run();
    }
}

/// <summary>
/// Аргументы трея. Супервизор запускает его без аргументов; <c>--pipe</c> и <c>--data</c> —
/// для второго экземпляра рядом с боевым (проверка на живой машине): свой pipe, своя блокировка
/// «один трей», свой файл состояния.
/// </summary>
internal sealed record TrayOptions(string? Pipe, string? DataDirectory)
{
    public static TrayOptions Parse(IReadOnlyList<string> args)
    {
        string? pipe = null, data = null;
        for (var i = 0; i + 1 < args.Count; i++)
        {
            switch (args[i])
            {
                case "--pipe": pipe = args[++i]; break;
                case "--data": data = args[++i]; break;
            }
        }
        return new TrayOptions(pipe is { Length: > 0 } ? pipe : null, data is { Length: > 0 } ? data : null);
    }
}
