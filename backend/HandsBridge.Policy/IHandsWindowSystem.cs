namespace ClaudeHomeServer.HandsBridge.Policy;

/// <summary>
/// Всё, что гейту нужно знать об окнах и процессах машины. На Windows — WinAPI
/// (<c>GetWindowThreadProcessId</c>, <c>QueryFullProcessImageName</c>), в тестах — подделка.
/// Любой сбой запроса — null: гейт читает его как «ввод запрещён».
/// </summary>
public interface IHandsWindowSystem
{
    /// <summary>Процесс, которому принадлежит окно; null — окна нет.</summary>
    int? GetWindowProcessId(long hwnd);

    /// <summary>Полный путь образа процесса в Win32-форме; null — узнать не удалось.</summary>
    string? GetProcessImagePath(int processId);
}
