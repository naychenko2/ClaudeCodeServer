using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using static ConPtyBridge.ConPtyNative;

namespace ConPtyBridge;

/// <summary>
/// Жизненный цикл псевдоконсоли: пайпы → CreatePseudoConsole → attribute list →
/// CreateProcessW(shell). Владеет хендлами и закрывает их в безопасном порядке.
///
/// Два критичных для отсутствия дедлоков момента (обе — классические грабли ConPTY):
///  1. Свои копии хендлов, переданных псевдоконсоли (inputRead/outputWrite),
///     закрываются СРАЗУ после CreatePseudoConsole — ConPTY уже сдублировал их
///     себе; иначе чтение output-пайпа никогда не увидит EOF после Close.
///  2. bInheritHandles=false и std-хендлы в STARTUPINFO не задаются — ребёнок
///     должен видеть только псевдоконсоль, не наши пайпы.
/// </summary>
internal sealed class PseudoConsoleSession : IDisposable
{
    /// <summary>Наш конец для записи ввода (уходит в input-пайп ConPTY).</summary>
    public SafeFileHandle InputWrite { get; }
    /// <summary>Наш конец для чтения VT-вывода ConPTY.</summary>
    public SafeFileHandle OutputRead { get; }
    /// <summary>Хендл дочернего процесса (шелла) — для ожидания/exit code/Terminate.</summary>
    public IntPtr ChildProcess { get; private set; }

    private IntPtr _hpc;                 // хендл псевдоконсоли
    private IntPtr _attrList;            // память attribute list (освобождаем в Dispose)
    private bool _consoleClosed;

    private PseudoConsoleSession(SafeFileHandle inputWrite, SafeFileHandle outputRead, IntPtr hpc)
    {
        InputWrite = inputWrite;
        OutputRead = outputRead;
        _hpc = hpc;
    }

    /// <summary>Создать псевдоконсоль размером cols×rows (процесс ещё не запущен).</summary>
    public static PseudoConsoleSession Create(short cols, short rows)
    {
        // Пайп ввода: read-конец отдаём ConPTY, write-конец оставляем себе
        if (!CreatePipe(out var inputRead, out var inputWrite, IntPtr.Zero, 0))
            throw new InvalidOperationException($"CreatePipe(input) не удался: {Marshal.GetLastWin32Error()}");
        // Пайп вывода: write-конец отдаём ConPTY, read-конец оставляем себе
        if (!CreatePipe(out var outputRead, out var outputWrite, IntPtr.Zero, 0))
            throw new InvalidOperationException($"CreatePipe(output) не удался: {Marshal.GetLastWin32Error()}");

        var hr = CreatePseudoConsole(new COORD { X = cols, Y = rows }, inputRead, outputWrite, 0, out var hpc);
        if (hr != 0)
            throw new InvalidOperationException($"CreatePseudoConsole не удался: HRESULT 0x{hr:X8}");

        // Грабля №1: ConPTY сдублировал переданные хендлы — наши копии закрываем
        // немедленно, иначе EOF на OutputRead после ClosePseudoConsole не придёт
        inputRead.Dispose();
        outputWrite.Dispose();

        return new PseudoConsoleSession(inputWrite, outputRead, hpc);
    }

    /// <summary>Запустить шелл, привязанный к псевдоконсоли.</summary>
    public void Spawn(string commandLine)
    {
        // Attribute list на один атрибут — привязка к псевдоконсоли
        var size = IntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
        _attrList = Marshal.AllocHGlobal(size);
        if (!InitializeProcThreadAttributeList(_attrList, 1, 0, ref size))
            throw new InvalidOperationException($"InitializeProcThreadAttributeList не удался: {Marshal.GetLastWin32Error()}");
        if (!UpdateProcThreadAttribute(_attrList, 0, (IntPtr)ProcThreadAttributePseudoConsole,
                _hpc, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
            throw new InvalidOperationException($"UpdateProcThreadAttribute не удался: {Marshal.GetLastWin32Error()}");

        var si = new STARTUPINFOEX
        {
            StartupInfo = new STARTUPINFO
            {
                cb = Marshal.SizeOf<STARTUPINFOEX>(),
                // NULL-хендлы с USESTDHANDLES: отрубаем наследование std родителя
                // через PEB — иначе powershell видит наши пайпы и уходит в
                // неинтерактивный режим (см. комментарий в STARTUPINFO)
                dwFlags = StartfUseStdHandles,
                hStdInput = IntPtr.Zero,
                hStdOutput = IntPtr.Zero,
                hStdError = IntPtr.Zero,
            },
            lpAttributeList = _attrList,
        };

        // Грабля №2: bInheritHandles=false, std-хендлы не заданы — ребёнок видит
        // только псевдоконсоль. cwd/env наследуются от моста (их выставил launcher)
        if (!CreateProcessW(null, commandLine, IntPtr.Zero, IntPtr.Zero, false,
                ExtendedStartupInfoPresent, IntPtr.Zero, null, ref si, out var pi))
            throw new InvalidOperationException($"CreateProcessW не удался: {Marshal.GetLastWin32Error()}");

        CloseHandle(pi.hThread);
        ChildProcess = pi.hProcess;
    }

    public void Resize(int cols, int rows)
    {
        if (_hpc != IntPtr.Zero && !_consoleClosed)
            ResizePseudoConsole(_hpc, new COORD { X = (short)cols, Y = (short)rows });
    }

    /// <summary>
    /// Закрыть псевдоконсоль. Для привязанного процесса это аналог SIGHUP —
    /// conhost завершает его и закрывает output-пайп (наш reader получает EOF).
    /// Reader обязан продолжать читать во время этого вызова (на старых билдах
    /// Windows ClosePseudoConsole блокируется, пока output-пайп не вычитан).
    /// </summary>
    public void CloseConsole()
    {
        if (_consoleClosed || _hpc == IntPtr.Zero) return;
        _consoleClosed = true;
        ClosePseudoConsole(_hpc);
        _hpc = IntPtr.Zero;
    }

    public void Dispose()
    {
        CloseConsole();
        if (_attrList != IntPtr.Zero)
        {
            DeleteProcThreadAttributeList(_attrList);
            Marshal.FreeHGlobal(_attrList);
            _attrList = IntPtr.Zero;
        }
        if (ChildProcess != IntPtr.Zero)
        {
            CloseHandle(ChildProcess);
            ChildProcess = IntPtr.Zero;
        }
        InputWrite.Dispose();
        OutputRead.Dispose();
    }
}
