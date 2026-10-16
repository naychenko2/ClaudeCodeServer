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

    /// <summary>
    /// Верхнеуровневые окна в z-порядке (сверху вниз); null — перечисление не удалось (сбой WinAPI).
    /// Свёрнутые, невидимые и cloaked-окна исключены (их пиксели в снимке не участвуют); DWM-тени
    /// не считаются.
    /// </summary>
    IReadOnlyList<HandsVisibleWindow>? EnumerateVisibleWindows();
}

/// <summary>
/// Верхнеуровневое окно в z-порядке. Прямоугольник — в физических пикселях экрана (левый/верхний
/// край включены, правый/нижний — нет). <see cref="ImagePath"/> — путь образа процесса; null —
/// процесс не распознан (окно закрыто по умолчанию для ввода). <see cref="IsVisible"/> — видно ли
/// окно на экране: свёрнутое, невидимое или cloaked — нет, и его пиксели в снимке не участвуют.
/// </summary>
public readonly record struct HandsVisibleWindow(
    long Hwnd,
    int Left, int Top, int Right, int Bottom,
    string? ImagePath,
    bool IsVisible)
{
    /// <summary>Прямоугольник не вырожден (окно имеет ненулевой размер).</summary>
    public bool HasSize => Right > Left && Bottom > Top;
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
