using ClaudeHomeServer.HandsBridge.Policy;

namespace HandsBridge.Policy.Tests;

/// <summary>Подделка WinAPI: окна, их процессы и образы.</summary>
internal sealed class FakeWindows : IHandsWindowSystem
{
    private readonly Dictionary<long, int> _windowProcess = [];
    private readonly Dictionary<int, string?> _images = [];

    public FakeWindows Process(int pid, string? image)
    {
        _images[pid] = image;
        return this;
    }

    public FakeWindows Window(long hwnd, int pid)
    {
        _windowProcess[hwnd] = pid;
        return this;
    }

    public int? GetWindowProcessId(long hwnd) => _windowProcess.TryGetValue(hwnd, out var pid) ? pid : null;

    public string? GetProcessImagePath(int processId) => _images.TryGetValue(processId, out var image) ? image : null;
}

/// <summary>Типовая машина теста: блокнот запущен ходом, остальное открыл человек.</summary>
internal static class Machine
{
    public const string Notepad = @"C:\Windows\notepad.exe";
    public const string Music = @"C:\Users\an\AppData\Local\Yandex\YandexMusic\YandexMusic.exe";
    public const string Cmd = @"C:\Windows\System32\cmd.exe";
    public const string Pwsh = @"C:\Program Files\PowerShell\7-preview\pwsh-preview.exe";
    public const string Terminal = @"C:\Program Files\WindowsApps\Microsoft.WindowsTerminal\WindowsTerminal.exe";
    public const string Explorer = @"C:\Windows\explorer.exe";

    /// <summary>Окно блокнота, запущенного руками хода.</summary>
    public const string TurnNotepad = "100";

    /// <summary>Блокнот, который человек открыл сам (в спайке 1 модель полезла именно в такой).</summary>
    public const string UserNotepad = "300";

    /// <summary>Яндекс Музыка человека — чужая программа вне хода.</summary>
    public const string UserMusic = "310";

    /// <summary>cmd человека: ввод в него — исполнение команд.</summary>
    public const string UserCmd = "400";

    /// <summary>Терминал человека.</summary>
    public const string UserTerminal = "500";

    /// <summary>pwsh-preview человека — запрещён по префиксу pwsh*.</summary>
    public const string UserPwsh = "510";

    /// <summary>Окно Проводника: адресная строка запускает команды.</summary>
    public const string ExplorerWindow = "520";

    /// <summary>Окно процесса, образ которого не прочитался (закрыто по умолчанию для ввода).</summary>
    public const string UnknownImage = "600";

    /// <summary>Окна нет вовсе.</summary>
    public const string Missing = "999";

    public static FakeWindows Windows() => new FakeWindows()
        .Process(1, @"c:/windows/NOTEPAD.EXE").Window(100, 1)
        .Process(3, Notepad).Window(300, 3)
        .Process(31, Music).Window(310, 31)
        .Process(4, Cmd).Window(400, 4)
        .Process(5, Terminal).Window(500, 5)
        .Process(51, Pwsh).Window(510, 51)
        .Process(52, Explorer).Window(520, 52)
        .Process(6, null).Window(600, 6);

    public static HandsPolicy Policy(FakeWindows? windows = null) => new(windows ?? Windows());

    /// <summary>
    /// Идентификаторы элементов: «turn» — из окна хода, «user» — из блокнота человека,
    /// «terminal» — из cmd человека, прочие неизвестны.
    /// </summary>
    public static long? ResolveElement(string elementId) => elementId switch
    {
        "turn" => 100,
        "user" => 300,
        "terminal" => 400,
        _ => null,
    };
}
