namespace ClaudeHomeServer.HandsBridge.Policy;

/// <summary>
/// Всё, что гейту нужно знать об окнах и процессах машины. На Windows — WinAPI
/// (<c>GetWindowThreadProcessId</c>, <c>IsProcessInJob</c>, <c>QueryFullProcessImageName</c>),
/// в тестах — подделка. Любой сбой запроса — null/false: гейт читает его как «чужое».
/// </summary>
public interface IHandsWindowSystem
{
    /// <summary>Процесс, которому принадлежит окно; null — окна нет.</summary>
    int? GetWindowProcessId(long hwnd);

    /// <summary>Процесс во вложенном Job моста — то есть запущен инструментом <c>app</c> или его потомок.</summary>
    bool IsProcessInAppsJob(int processId);

    /// <summary>Полный путь образа процесса в Win32-форме; null — узнать не удалось.</summary>
    string? GetProcessImagePath(int processId);
}
