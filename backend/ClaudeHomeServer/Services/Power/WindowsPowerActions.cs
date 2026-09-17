using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ClaudeHomeServer.Services.Power;

/// <summary>
/// Windows-путь к питанию машины: выключение и перезагрузка — через shutdown.exe, сон — через
/// powrprof.dll.
///
/// Почему выключение и перезагрузка идут с ключом <c>/f</c>. Без него приложение с
/// несохранённым документом останавливает завершение работы экраном «программа не даёт
/// завершить работу» и ждёт человека у монитора — а его там нет, кнопку жмут издалека. Тихий
/// отказ хуже потерянного черновика: нажавший уверен, что машина погасла, и уходит. Именно
/// поэтому отсрочка перед командой не косметическая (см. PowerControlService) — она и есть
/// окно, в которое можно передумать.
///
/// Сон командой shutdown.exe недостижим (её <c>/h</c> — это гибернация), поэтому здесь
/// SetSuspendState: первый аргумент false = спящий режим, а не гибернация. Отсрочки у неё нет
/// вовсе — ещё одна причина отсчитывать время самим, а не полагаться на <c>shutdown /t</c>.
/// </summary>
public sealed class WindowsPowerActions(ILogger<WindowsPowerActions> log) : IPowerActions
{
    public bool Available => OperatingSystem.IsWindows();

    // bHibernate: false — уснуть, а не гибернировать; bForce игнорируется современной Windows;
    // bWakeupEventsDisabled: false — будильники и Wake-on-LAN оставляем рабочими, иначе машину
    // после сна нечем будет поднять удалённо.
    [DllImport("powrprof.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool SetSuspendState(
        [MarshalAs(UnmanagedType.I1)] bool hibernate,
        [MarshalAs(UnmanagedType.I1)] bool force,
        [MarshalAs(UnmanagedType.I1)] bool wakeupEventsDisabled);

    public bool Execute(PowerAction action)
    {
        if (!Available)
        {
            log.LogWarning("Управление питанием доступно только на Windows — команда {Action} пропущена.", action);
            return false;
        }

        try
        {
            return action switch
            {
                PowerAction.Shutdown => Run("/s /f /t 0"),
                PowerAction.Restart => Run("/r /f /t 0"),
                PowerAction.Sleep => Suspend(),
                _ => false,
            };
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Команда питания {Action} не выполнена.", action);
            return false;
        }
    }

    private bool Suspend()
    {
        if (SetSuspendState(hibernate: false, force: false, wakeupEventsDisabled: false)) return true;
        log.LogError("SetSuspendState отказал, код {Code}.", Marshal.GetLastWin32Error());
        return false;
    }

    // Процесс не ждём: shutdown.exe отдаёт команду системе и выходит сам, а ждать выхода
    // изнутри процесса, который эта же команда сейчас погасит, смысла нет.
    private bool Run(string args)
    {
        var process = Process.Start(new ProcessStartInfo("shutdown.exe", args)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
        });

        if (process is not null) return true;
        log.LogError("Не удалось запустить shutdown.exe {Args}.", args);
        return false;
    }
}
