using ClaudeHomeServer.HandsBridge.Policy;
using Xunit;

namespace HandsBridge.Policy.Tests;

/// <summary>
/// OCR (A4.2 «руки без UIA»): остаточный риск сетки A4.1 закрыт перечислением видимых окон.
/// <see cref="HandsPolicy.CheckOcr"/> для <c>region</c>/экрана идёт по окнам сверху вниз, вычитая
/// перекрытия, и отказывает ДО снимка, если видимая часть области падает на окно, которое не
/// принимает ввод (запрещённое приложение или неизвестный процесс). Сбой перечисления —
/// fail-closed. Сетка (шаг 200 px) — запасной путь для пустого перечисления.
/// </summary>
public class OcrWindowEnumerationTests
{
    // ---------- перечисление: маленький запрещённый ----------

    /// <summary>
    /// Терминал 50×50 строго МЕЖДУ узлами сетки (шаг 200 px): сетка A4.1 его не заденет,
    /// перечисление — да. Это и есть закрытый остаточный риск.
    /// </summary>
    [Fact]
    public void Small_forbidden_window_between_grid_nodes_is_denied()
    {
        var windows = new FakeWindows()
            .Process(4, Machine.Cmd).Window(400, 4)
            .Process(3, Machine.Notepad).Window(300, 3)
            .Visible(400, 250, 250, 300, 300, Machine.Cmd, true)      // верх: маленький терминал
            .Visible(300, 0, 0, 1000, 1000, Machine.Notepad, true);    // под ним: блокнот
        var policy = new HandsPolicy(windows);

        var denied = policy.CheckOcr("region", null, 0, 0, 1000, 1000);
        Assert.False(denied.Allowed);
        Assert.Contains("area overlaps a window that is not allowed", denied.Reason);
        Assert.Contains("cmd.exe", denied.Reason);
    }

    // ---------- перекрытия ----------

    /// <summary>
    /// Запрещённое окно ПОЛНОСТЬЮ закрыто разрешённым сверху: его видимой части нет —
    /// вычитание перекрытий его не считает, снимок разрешён.
    /// </summary>
    [Fact]
    public void Forbidden_window_fully_covered_by_allowed_on_top_is_allowed()
    {
        var windows = new FakeWindows()
            .Process(4, Machine.Cmd).Window(400, 4)
            .Process(3, Machine.Notepad).Window(300, 3)
            .Visible(300, 0, 0, 1000, 1000, Machine.Notepad, true)   // верх: блокнот
            .Visible(400, 0, 0, 1000, 1000, Machine.Cmd, true);       // под ним: терминал
        var policy = new HandsPolicy(windows);

        Assert.True(policy.CheckOcr("region", null, 0, 0, 1000, 1000).Allowed);
    }

    /// <summary>
    /// Запрещённое окно перекрыто разрешённым, но видна ПОЛОСКА (низ окна): вычитание не закрывает
    /// её — отказ ДО снимка.
    /// </summary>
    [Fact]
    public void Forbidden_window_partially_covered_visible_strip_is_denied()
    {
        var windows = new FakeWindows()
            .Process(4, Machine.Cmd).Window(400, 4)
            .Process(3, Machine.Notepad).Window(300, 3)
            .Visible(300, 0, 0, 1000, 900, Machine.Notepad, true)    // верх: блокнот, 900 px
            .Visible(400, 0, 0, 1000, 1000, Machine.Cmd, true);       // под ним: терминал, 1000 px
        var policy = new HandsPolicy(windows);

        var denied = policy.CheckOcr("region", null, 0, 0, 1000, 1000);
        Assert.False(denied.Allowed);
        Assert.Contains("area overlaps a window that is not allowed", denied.Reason);
        Assert.Contains("cmd.exe", denied.Reason);
    }

    // ---------- видимость ----------

    /// <summary>
    /// Свёрнутое (невидимое) запрещённое окно: его пиксели в снимке не участвуют —
    /// перечисление его не считает, снимок разрешён.
    /// </summary>
    [Fact]
    public void Minimized_forbidden_window_is_allowed()
    {
        var windows = new FakeWindows()
            .Process(4, Machine.Cmd).Window(400, 4)
            .Process(3, Machine.Notepad).Window(300, 3)
            .Visible(300, 0, 0, 1000, 1000, Machine.Notepad, true)    // верх: блокнот
            .Visible(400, 0, 0, 1000, 1000, Machine.Cmd, isVisible: false); // свёрнутый терминал
        var policy = new HandsPolicy(windows);

        Assert.True(policy.CheckOcr("region", null, 0, 0, 1000, 1000).Allowed);
    }

    /// <summary>
    /// Окно с непрочитанным образом (неизвестный процесс) — закрыто по умолчанию:
    /// отказ ДО снимка.
    /// </summary>
    [Fact]
    public void Unknown_process_window_is_denied()
    {
        var windows = new FakeWindows()
            .Process(6, null).Window(600, 6)
            .Visible(600, 0, 0, 1000, 1000, null, true);
        var policy = new HandsPolicy(windows);

        var denied = policy.CheckOcr("region", null, 0, 0, 1000, 1000);
        Assert.False(denied.Allowed);
        Assert.Contains("area overlaps a window that is not allowed", denied.Reason);
        Assert.Contains("unknown process", denied.Reason);
    }

    // ---------- fail-closed ----------

    /// <summary>
    /// Перечисление окон сбилось (null): fail-closed — отказ, а не «поверим сетке».
    /// </summary>
    [Fact]
    public void Enumeration_error_is_denied()
    {
        var windows = new FakeWindows().EnumerateFails();
        var policy = new HandsPolicy(windows);

        var denied = policy.CheckOcr("region", null, 0, 0, 1000, 1000);
        Assert.False(denied.Allowed);
        Assert.Contains("the window list could not be read", denied.Reason);
    }

    // ---------- геометрия ----------

    /// <summary>
    /// Область, уходящая за пределы экрана: пересечение считается по наэкранной части —
    /// запрещённое окно в ней ловится.
    /// </summary>
    [Fact]
    public void Region_beyond_screen_still_catches_a_forbidden_window()
    {
        var windows = new FakeWindows()
            .Process(4, Machine.Cmd).Window(400, 4)
            .Process(3, Machine.Notepad).Window(300, 3)
            .Bounds(new HandsScreenBounds(0, 0, 1920, 1080))
            .Visible(400, 1850, 1050, 1900, 1080, Machine.Cmd, true)   // терминал в углу экрана
            .Visible(300, 0, 0, 1920, 1080, Machine.Notepad, true);     // блокнот на весь экран
        var policy = new HandsPolicy(windows);

        // Область [1800,2000)×[1000,1200): часть — за экраном, но терминал в её наэкранной части.
        var denied = policy.CheckOcr("region", null, 1800, 1000, 200, 200);
        Assert.False(denied.Allowed);
        Assert.Contains("area overlaps a window that is not allowed", denied.Reason);
    }

    /// <summary>
    /// Экранная цель (primary_screen) с ВИДИМЫМ запрещённым окном на экране: перечисление по
    /// всему виртуальному экрану его ловит и отказывает ДО снимка.
    /// </summary>
    [Fact]
    public void Screen_with_forbidden_visible_window_is_denied()
    {
        var windows = new FakeWindows()
            .Process(4, Machine.Cmd).Window(400, 4)
            .Process(3, Machine.Notepad).Window(300, 3)
            .Bounds(new HandsScreenBounds(0, 0, 1920, 1080))
            .Visible(400, 800, 400, 1100, 700, Machine.Cmd, true)   // терминал в центре экрана
            .Visible(300, 0, 0, 1920, 1080, Machine.Notepad, true);  // блокнот на весь экран
        var policy = new HandsPolicy(windows);

        var denied = policy.CheckOcr("primary_screen", null, null, null, null, null);
        Assert.False(denied.Allowed);
        Assert.Contains("area overlaps a window that is not allowed", denied.Reason);
    }
}
