using ClaudeHomeServer.HandsBridge.Policy;
using Xunit;

namespace HandsBridge.Policy.Tests;

/// <summary>
/// M-E: чистое перемещение (move, без кнопок) проверяет ЦЕЛЬ (x, y), а не текущую позицию
/// курсора; кнопки/колесо (click, double, right, middle, drag, scroll) проверяют ТЕКУЩУЮ позицию.
/// Решение вынесено в чистую функцию <see cref="HandsPolicy.DecideMovePoint"/>, чтобы ветки
/// проверялись таблично на Linux (без <c>SendInput</c>/<c>GetCursorPos</c>).
/// </summary>
public class MouseMoveTargetTests
{
    // Точки на «экране» теста: каждая ведёт в своё окно (корень).
    private const int AllowedPoint = 100;    // → блокнот хода (100), разрешён
    private const int TerminalPoint = 300;  // → терминал (500), запрещён
    private const int UnknownPoint = 600;   // → неизвестный процесс (600), запрещён

    /// <summary>
    /// Машина: стандартные окна, точки над ними, блокнот хода (100) — передний план,
    /// рабочий стол 0..1920 × 0..1080.
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
        .At(TerminalPoint, TerminalPoint, 500)
        .At(UnknownPoint, UnknownPoint, 600)
        .Foreground(100)
        .Bounds(new HandsScreenBounds(0, 0, 1920, 1080));

    private static HandsPolicy Policy() => new(Rig());

    /// <summary>
    /// M-E: чистое перемещение из-под запрещённого окна (курсор над терминалом) в разрешённое
    /// (цель — блокнот хода) не отказывает: перемещение В разрешённое окно — не ввод в окно.
    /// Краснеет, если в чистый move вернули проверку ТЕКУЩЕЙ позиции (курсор над терминалом → отказ).
    /// </summary>
    [Fact]
    public void Pure_move_from_forbidden_cursor_to_allowed_target_is_allowed()
    {
        var (allowed, reason) = HandsPolicy.DecideMovePoint(
            x: AllowedPoint, y: AllowedPoint,
            cursorX: TerminalPoint, cursorY: TerminalPoint,
            expectedRoot: 100,
            checkCurrentPosition: false,
            Policy());
        Assert.True(allowed, $"чистый move в разрешённое окно должен пройти: {reason}");
    }

    /// <summary>
    /// M-E: чистое перемещение из разрешённого окна (курсор над блокнотом) в запрещённое
    /// (цель — терминал) отказывает: цель вне разрешённого окна. Краснеет, если в move
    /// убрали проверку ЦЕЛИ (тогда перемещение в терминал проходило бы).
    /// </summary>
    [Fact]
    public void Pure_move_from_allowed_cursor_to_forbidden_target_is_denied()
    {
        var (allowed, reason) = HandsPolicy.DecideMovePoint(
            x: TerminalPoint, y: TerminalPoint,
            cursorX: AllowedPoint, cursorY: AllowedPoint,
            expectedRoot: 100,
            checkCurrentPosition: false,
            Policy());
        Assert.False(allowed, "чистый move в терминал должен быть отклонён");
        Assert.Contains("never click or type", reason);
    }

    /// <summary>
    /// M-E: чистое перемещение в окно неизвестного процесса (образ не прочитан) отказывает:
    /// окно закрыто по умолчанию для ввода.
    /// </summary>
    [Fact]
    public void Pure_move_to_unknown_process_window_is_denied()
    {
        var (allowed, reason) = HandsPolicy.DecideMovePoint(
            x: UnknownPoint, y: UnknownPoint,
            cursorX: AllowedPoint, cursorY: AllowedPoint,
            expectedRoot: 100,
            checkCurrentPosition: false,
            Policy());
        Assert.False(allowed, "move в окно неизвестного процесса должен быть отклонён");
    }

    /// <summary>
    /// M-E: кнопки/колесо (click, double, right, middle, drag, scroll) проверяют ТЕКУЩУЮ позицию
    /// курсора: если курсор над запрещённым окном (терминал) — отказ, даже если цель разрешена.
    /// Краснеет, если для кнопок случайно включили проверку цели вместо текущей позиции.
    /// </summary>
    [Fact]
    public void Button_with_cursor_over_forbidden_window_is_denied()
    {
        var (allowed, reason) = HandsPolicy.DecideMovePoint(
            x: AllowedPoint, y: AllowedPoint,
            cursorX: TerminalPoint, cursorY: TerminalPoint,
            expectedRoot: 100,
            checkCurrentPosition: true,
            Policy());
        Assert.False(allowed, "клик с курсором над терминалом должен быть отклонён");
        Assert.Contains("never click or type", reason);
    }

    /// <summary>
    /// M-E: кнопки/колесо с курсором над разрешённым окном проходят проверку текущей позиции.
    /// </summary>
    [Fact]
    public void Button_with_cursor_over_allowed_window_is_allowed()
    {
        var (allowed, reason) = HandsPolicy.DecideMovePoint(
            x: AllowedPoint, y: AllowedPoint,
            cursorX: AllowedPoint, cursorY: AllowedPoint,
            expectedRoot: 100,
            checkCurrentPosition: true,
            Policy());
        Assert.True(allowed, $"клик с курсором над блокнотом должен пройти: {reason}");
    }
}
