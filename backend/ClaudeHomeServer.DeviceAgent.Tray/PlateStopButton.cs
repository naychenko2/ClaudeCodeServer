namespace ClaudeHomeServer.DeviceAgent.Tray;

/// <summary>
/// Кнопка «Стоп» плашки без Win32 — как обычная кнопка Windows: взводится нажатием на ней,
/// срабатывает отпусканием на ней же. Отпускание само по себе не значит ничего: без захвата мыши
/// <c>WM_LBUTTONUP</c> получает окно под курсором, и клик или перетаскивание, начатые в окне
/// человека и отпущенные над плашкой, прерывали ход (инцидент 28.09). Увод курсора с кнопки и
/// потеря захвата снимают взвод насовсем: вернуться и отпустить — уже не «Стоп».
/// </summary>
internal sealed class PlateStopButton
{
    public bool Armed { get; private set; }

    /// <summary>Нажата левая кнопка. true — взвелась, окну пора захватить мышь.</summary>
    public bool Down(bool onStop)
    {
        Armed = onStop;
        return Armed;
    }

    /// <summary>Курсор сдвинулся; ушёл с кнопки — взвод снят. true — снят только что, захват пора отпустить.</summary>
    public bool Move(bool onStop)
    {
        if (!Armed || onStop) return false;
        Armed = false;
        return true;
    }

    /// <summary>Отпустили левую кнопку. true — нажать «Стоп».</summary>
    public bool Up(bool onStop)
    {
        var fire = Armed && onStop;
        Armed = false;
        return fire;
    }

    /// <summary>Мышь ушла из окна, захват отняли, «Стоп» спрятали.</summary>
    public void Cancel() => Armed = false;
}
