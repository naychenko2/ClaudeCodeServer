using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ClaudeHomeServer.DeviceAgent.Supervision;

/// <summary>
/// Windows: консольный exe из записи Run получает своё окно консоли. Закрыл его человек —
/// погас бы агент. Супервизор, единственный владелец консоли, отцепляется от неё сразу;
/// запущенный руками из терминала (владельцев больше одного) — остаётся в нём.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class ConsoleDetach
{
    /// <summary>true — отцепился: писать в консоль больше некуда, хэндлы вывода мертвы.</summary>
    public static bool IfSoleOwner()
    {
        var ids = new uint[2];
        return GetConsoleProcessList(ids, (uint)ids.Length) == 1 && FreeConsole();
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetConsoleProcessList([Out] uint[] processList, uint processCount);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeConsole();
}
