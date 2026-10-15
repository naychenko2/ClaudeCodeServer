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

    /// <summary>Корень окна под экранной точкой; 0 — окна нет.</summary>
    long GetRootWindowAt(int x, int y);

    /// <summary>Корень окна по hwnd; 0 — окна нет.</summary>
    long GetRootWindow(long hwnd);

    /// <summary>Корень окна переднего плана; 0 — окна нет.</summary>
    long GetForegroundWindow();

    /// <summary>Границы виртуального рабочего стола (все мониторы); null — узнать не удалось.</summary>
    HandsScreenBounds? GetVirtualScreenBounds();
}

/// <summary>
/// Границы виртуального рабочего стола: включает <c>Left</c>/<c>Top</c>, исключает
/// <c>Right</c>/<c>Bottom</c> (то же соглашение, что <c>ValidateCoordinates</c> в мосту).
/// </summary>
public readonly record struct HandsScreenBounds(int Left, int Top, int Right, int Bottom)
{
    /// <summary>Точка внутри рабочего стола (левый/верхний край включены, правый/нижний — нет).</summary>
    public bool Contains(int x, int y) => x >= Left && x < Right && y >= Top && y < Bottom;
}
