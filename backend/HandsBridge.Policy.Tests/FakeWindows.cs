using ClaudeHomeServer.HandsBridge.Policy;
using ClaudeHomeServer.Protocol;

namespace HandsBridge.Policy.Tests;

/// <summary>Подделка WinAPI: окна, их процессы, членство в Job моста и образы.</summary>
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

    public bool IsProcessInAppsJob(int processId) => _processes.TryGetValue(processId, out var p) && p.InJob;

    public string? GetProcessImagePath(int processId) => _processes.TryGetValue(processId, out var p) ? p.Image : null;
}

/// <summary>Типовая машина теста: блокнот разрешён и запущен руками, остальное — вокруг.</summary>
internal static class Machine
{
    public const string Notepad = @"C:\Windows\notepad.exe";
    public const string Paint = @"C:\Program Files\Paint\paint.exe";
    public const string Helper = @"C:\Program Files\Paint\helper.exe";
    public const string Cmd = @"C:\Windows\System32\cmd.exe";

    /// <summary>Окно блокнота, запущенного руками.</summary>
    public const string Own = "100";

    /// <summary>Дочерний процесс разрешённой программы в том же Job, но вне белого списка.</summary>
    public const string ChildOutsideList = "200";

    /// <summary>Блокнот, который человек открыл сам: образ в списке, но процесс не в Job.</summary>
    public const string UserNotepad = "300";

    /// <summary>cmd в Job, внесённый человеком в список, — всё равно чужое.</summary>
    public const string ForbiddenInJob = "400";

    /// <summary>Окна нет вовсе.</summary>
    public const string Missing = "999";

    public static HandsAppsFile Apps(params string[] paths) =>
        new(HandsAppsFile.CurrentVersion, paths.Select(p => new HandsAppEntry(p, DateTimeOffset.UnixEpoch)).ToList());

    public static FakeWindows Windows() => new FakeWindows()
        .Process(1, inJob: true, image: @"c:/windows/NOTEPAD.EXE").Window(100, 1)
        .Process(2, inJob: true, image: Helper).Window(200, 2)
        .Process(3, inJob: false, image: Notepad).Window(300, 3)
        .Process(4, inJob: true, image: Cmd).Window(400, 4);

    public static HandsPolicy Policy(HandsAppsFile? apps = null, FakeWindows? windows = null) =>
        new(() => apps ?? Apps(Notepad, Paint, Cmd), windows ?? Windows());

    /// <summary>Идентификаторы элементов: «own» — из своего окна, «foreign» — из чужого, прочие неизвестны.</summary>
    public static long? ResolveElement(string elementId) => elementId switch
    {
        "own" => 100,
        "foreign" => 300,
        _ => null,
    };
}
