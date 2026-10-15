using ClaudeHomeServer.HandsBridge.Policy;
using Xunit;

namespace HandsBridge.Policy.Tests;

/// <summary>
/// <c>mouse_control</c> и <c>keyboard_control</c> (D1b): гейт проверяет окно ПОД ТОЧКОЙ перед
/// нажатием и на конце перетаскивания, колесо держит в пределах, а клавиатура требует окно
/// переднего плана. Главная ошибка мыши — клик по устаревшим координатам в чужое окно.
/// </summary>
public class HandsPointerTests
{
    // Точки на «экране» теста: каждая ведёт в своё окно (корень).
    private const int AllowedPoint = 100;    // → блокнот хода (100), разрешён
    private const int AllowedPoint2 = 150;   // → тоже блокнот хода (100): вторая точка drag/polyline
    private const int OtherWindowPoint = 200; // → блокнот человека (300), разрешён
    private const int TerminalPoint = 300;   // → терминал (500), запрещён
    private const int CmdPoint = 400;        // → cmd (400), запрещён
    private const int MusicPoint = 500;       // → Яндекс Музыка (310), разрешена
    private const int OffScreenPoint = 9000; // вне виртуального рабочего стола

    /// <summary>
    /// Машина: стандартные окна (как в <see cref="Machine"/>), точки над ними, блокнот хода
    /// (100) — передний план, рабочий стол 0..1920 × 0..1080.
    /// </summary>
    private static FakeWindows Rig() => new FakeWindows()
        .Process(1, @"c:/windows/NOTEPAD.EXE").Window(100, 1)
        .Process(3, Machine.Notepad).Window(300, 3)
        .Process(31, Machine.Music).Window(310, 31)
        .Process(4, Machine.Cmd).Window(400, 4)
        .Process(5, Machine.Terminal).Window(500, 5)
        .Process(51, Machine.Pwsh).Window(510, 51)
        .Process(52, Machine.Explorer).Window(520, 52)
        .Process(6, null).Window(600, 6)
        .At(AllowedPoint, AllowedPoint, 100)
        .At(AllowedPoint2, AllowedPoint2, 100)
        .At(OtherWindowPoint, OtherWindowPoint, 300)
        .At(TerminalPoint, TerminalPoint, 500)
        .At(CmdPoint, CmdPoint, 400)
        .At(MusicPoint, MusicPoint, 310)
        .Foreground(100)
        .Bounds(new HandsScreenBounds(0, 0, 1920, 1080));

    private static HandsPolicy Policy() => new(Rig());

    private static IReadOnlyList<(int X, int Y)> Point(int x, int y) => new[] { (x, y) };

    // ---------- окно под точкой ----------

    [Fact]
    public void Point_over_forbidden_window_is_denied()
    {
        var decision = Policy().AcceptsInputAt(TerminalPoint, TerminalPoint);
        Assert.False(decision.Allowed);
        Assert.Contains("never click or type", decision.Reason);
    }

    [Fact]
    public void Point_over_allowed_window_is_allowed()
    {
        Assert.True(Policy().AcceptsInputAt(AllowedPoint, AllowedPoint).Allowed);
    }

    [Fact]
    public void Point_with_no_window_under_it_is_denied()
    {
        var decision = Policy().AcceptsInputAt(7777, 7777);
        Assert.False(decision.Allowed);
        Assert.Contains("No window", decision.Reason);
    }

    [Fact]
    public void Point_over_foreign_window_with_expected_handle_is_denied()
    {
        // Точка над окном 300, а действие нацелено на 100 → другое окно.
        var decision = Policy().AcceptsInputAt(OtherWindowPoint, OtherWindowPoint, 100);
        Assert.False(decision.Allowed);
        Assert.Contains("another window", decision.Reason);
    }

    [Fact]
    public void Point_over_the_expected_window_is_allowed()
    {
        Assert.True(Policy().AcceptsInputAt(AllowedPoint, AllowedPoint, 100).Allowed);
    }

    [Fact]
    public void Pointer_is_denied_when_the_virtual_desktop_bounds_are_unknown()
    {
        // Границы экрана не прочитаны (null) — точку сверить с экраном нельзя.
        var rig = new FakeWindows()
            .Process(1, @"c:/windows/NOTEPAD.EXE").Window(100, 1)
            .At(AllowedPoint, AllowedPoint, 100)
            .Foreground(100)
            .Bounds(null);
        var decision = new HandsPolicy(rig).CheckPointer("click", Point(AllowedPoint, AllowedPoint), 100, 0);
        Assert.False(decision.Allowed);
        Assert.Contains("virtual desktop bounds are unknown", decision.Reason);
    }

    [Fact]
    public void Pointer_is_denied_when_the_expected_root_is_zero()
    {
        // Действие нацелено на окно 0 (окна нет), а под точкой — блокнот (100) → другое окно.
        var decision = Policy().AcceptsInputAt(AllowedPoint, AllowedPoint, 0);
        Assert.False(decision.Allowed);
        Assert.Contains("not the window the action targets", decision.Reason);
    }

    // ---------- форма и потолки мыши ----------

    [Theory]
    [InlineData("move")]
    [InlineData("click")]
    [InlineData("double_click")]
    [InlineData("right_click")]
    [InlineData("middle_click")]
    public void Single_point_actions_are_allowed_over_allowed_window(string action)
    {
        Assert.True(Policy().CheckPointer(action, Point(AllowedPoint, AllowedPoint), 100, 0).Allowed);
    }

    [Fact]
    public void Unknown_pointer_action_is_denied()
    {
        var decision = Policy().CheckPointer("hover", Point(AllowedPoint, AllowedPoint), 100, 0);
        Assert.False(decision.Allowed);
        Assert.Contains("Unknown mouse_control action", decision.Reason);
    }

    [Fact]
    public void Drag_needs_exactly_two_points()
    {
        var policy = Policy();
        Assert.False(policy.CheckPointer("drag", Point(AllowedPoint, AllowedPoint), 100, 0).Allowed);
        Assert.False(policy.CheckPointer("drag", new[] { (AllowedPoint, AllowedPoint), (AllowedPoint2, AllowedPoint2), (MusicPoint, MusicPoint) }, 100, 0).Allowed);
        Assert.True(policy.CheckPointer("drag", new[] { (AllowedPoint, AllowedPoint), (AllowedPoint2, AllowedPoint2) }, 100, 0).Allowed);
    }

    [Fact]
    public void Polyline_needs_at_least_two_points()
    {
        var policy = Policy();
        Assert.False(policy.CheckPointer("polyline", Point(AllowedPoint, AllowedPoint), 100, 0).Allowed);
        Assert.True(policy.CheckPointer("polyline", new[] { (AllowedPoint, AllowedPoint), (AllowedPoint2, AllowedPoint2) }, 100, 0).Allowed);
    }

    [Fact]
    public void Get_position_takes_no_points()
    {
        var policy = Policy();
        Assert.True(policy.CheckPointer("get_position", Array.Empty<(int X, int Y)>(), 100, 0).Allowed);
        Assert.False(policy.CheckPointer("get_position", Point(AllowedPoint, AllowedPoint), 100, 0).Allowed);
    }

    [Fact]
    public void Drag_end_in_a_terminal_is_denied_even_if_start_is_allowed()
    {
        // Старт над блокнотом (разрешён), конец над терминалом (запрещён) → отказ.
        var decision = Policy().CheckPointer("drag", new[] { (AllowedPoint, AllowedPoint), (TerminalPoint, TerminalPoint) }, 100, 0);
        Assert.False(decision.Allowed);
        Assert.Contains("never click or type", decision.Reason);
    }

    [Fact]
    public void Point_outside_the_virtual_desktop_is_denied()
    {
        var decision = Policy().CheckPointer("click", Point(OffScreenPoint, OffScreenPoint), 100, 0);
        Assert.False(decision.Allowed);
        Assert.Contains("outside the virtual desktop", decision.Reason);
    }

    [Fact]
    public void Scroll_amount_is_capped()
    {
        var policy = Policy();
        Assert.True(policy.CheckPointer("scroll", Point(AllowedPoint, AllowedPoint), 100, 20).Allowed);
        Assert.False(policy.CheckPointer("scroll", Point(AllowedPoint, AllowedPoint), 100, 21).Allowed);
        Assert.False(policy.CheckPointer("scroll", Point(AllowedPoint, AllowedPoint), 100, 0).Allowed);
        Assert.False(policy.CheckPointer("scroll", Point(AllowedPoint, AllowedPoint), 100, -3).Allowed);
    }

    // ---------- клавиатура ----------

    [Theory]
    [InlineData("type")]
    [InlineData("press")]
    [InlineData("key_down")]
    [InlineData("key_up")]
    [InlineData("sequence")]
    public void Keyboard_input_without_window_handle_is_denied(string action)
    {
        var decision = Policy().CheckKeyboard(null, action);
        Assert.False(decision.Allowed);
        Assert.Contains("requires windowHandle", decision.Reason);
    }

    [Fact]
    public void Keyboard_into_forbidden_window_is_denied()
    {
        // Окно 500 (терминал) принимает ввод? Нет. Но оно и не передний план (тот 100).
        // Сначала проверка AcceptsInput: терминал запрещён → отказ.
        var decision = Policy().CheckKeyboard(500, "type");
        Assert.False(decision.Allowed);
        Assert.Contains("never click or type", decision.Reason);
    }

    [Fact]
    public void Keyboard_into_non_foreground_window_is_denied()
    {
        // Окно 300 (блокнот человека) принимает ввод, но передний план — 100 → отказ.
        var decision = Policy().CheckKeyboard(300, "type");
        Assert.False(decision.Allowed);
        Assert.Contains("not the foreground window", decision.Reason);
    }

    [Fact]
    public void Keyboard_into_allowed_foreground_window_is_allowed()
    {
        // Окно 100 (блокнот хода) — разрешено и передний план → разрешено.
        Assert.True(Policy().CheckKeyboard(100, "type").Allowed);
    }

    [Fact]
    public void Keyboard_service_actions_need_no_window()
    {
        var policy = Policy();
        Assert.True(policy.CheckKeyboard(null, "release_all").Allowed);
        Assert.True(policy.CheckKeyboard(null, "get_keyboard_layout").Allowed);
        Assert.True(policy.CheckKeyboard(null, "wait_for_idle").Allowed);
    }

    [Fact]
    public void Unknown_keyboard_action_is_denied()
    {
        var decision = Policy().CheckKeyboard(100, "chord");
        Assert.False(decision.Allowed);
        Assert.Contains("Unknown keyboard_control action", decision.Reason);
    }
}
