using ClaudeHomeServer.HandsBridge.Policy;

namespace HandsBridge.Policy.Tests;

/// <summary>Подделка WinAPI: окна, их процессы, членство в Job хода и образы.</summary>
internal sealed class FakeWindows : IHandsWindowSystem
{
    private readonly Dictionary<long, int> _windowProcess = [];
    private readonly Dictionary<int, (bool InJob, string? Image)> _processes = [];

    public FakeWindows Process(int pid, bool inJob, string? image)
    {
        _processes[pid] = (inJob, image);
        return this;
    }

    public FakeWindows Window(long hwnd, int pid)
    {
        _windowProcess[hwnd] = pid;
        return this;
    }

    public int? GetWindowProcessId(long hwnd) => _windowProcess.TryGetValue(hwnd, out var pid) ? pid : null;

    public bool IsProcessInTurnJob(int processId) => _processes.TryGetValue(processId, out var p) && p.InJob;

    public string? GetProcessImagePath(int processId) => _processes.TryGetValue(processId, out var p) ? p.Image : null;
}

/// <summary>Типовая машина теста: блокнот запущен ходом, остальное — вокруг.</summary>
internal static class Machine
{
    public const string Notepad = @"C:\Windows\notepad.exe";
    public const string Paint = @"C:\Program Files\Paint\paint.exe";
    public const string Helper = @"C:\Program Files\Paint\helper.exe";
    public const string Cmd = @"C:\Windows\System32\cmd.exe";
    public const string Terminal = @"C:\Program Files\WindowsApps\Microsoft.WindowsTerminal\WindowsTerminal.exe";

    /// <summary>Окно блокнота, запущенного руками хода.</summary>
    public const string Own = "100";

    /// <summary>Другая программа в Job хода (потомок или запуск командой хода) — тоже своя.</summary>
    public const string ChildInJob = "200";

    /// <summary>Блокнот, который человек открыл сам: процесс не в Job хода.</summary>
    public const string UserNotepad = "300";

    /// <summary>cmd в Job хода — всё равно чужое: ввод в терминал исполняет команды.</summary>
    public const string ForbiddenInJob = "400";

    /// <summary>Терминал в Job хода — чужое по той же причине.</summary>
    public const string TerminalInJob = "500";

    /// <summary>Процесс в Job, образ которого не прочитался, — чужое (закрыто по умолчанию).</summary>
    public const string UnknownImageInJob = "600";

    /// <summary>Окна нет вовсе.</summary>
    public const string Missing = "999";

    public static FakeWindows Windows() => new FakeWindows()
        .Process(1, inJob: true, image: @"c:/windows/NOTEPAD.EXE").Window(100, 1)
        .Process(2, inJob: true, image: Helper).Window(200, 2)
        .Process(3, inJob: false, image: Notepad).Window(300, 3)
        .Process(4, inJob: true, image: Cmd).Window(400, 4)
        .Process(5, inJob: true, image: Terminal).Window(500, 5)
        .Process(6, inJob: true, image: null).Window(600, 6);

    public static HandsPolicy Policy(FakeWindows? windows = null) => new(windows ?? Windows());

    /// <summary>Идентификаторы элементов: «own» — из своего окна, «foreign» — из чужого, прочие неизвестны.</summary>
    public static long? ResolveElement(string elementId) => elementId switch
    {
        "own" => 100,
        "foreign" => 300,
        _ => null,
    };
}
