using System.Reflection;
using Xunit;

namespace HandsBridge.Policy.Tests;

/// <summary>
/// Блокер 1: удержанный модификатор (<c>key_down Ctrl</c>) + <c>press</c>/<c>sequence</c>/<c>type</c>
/// Escape — сочетание Ctrl+Esc, которое гейт обязан отклонить. Проверка — на уровне
/// <c>KeyboardInputService</c> (мост, рефлексия): гейт <c>CheckKeys</c> видит эффективные
/// модификаторы (вызов ∪ удержанные из <c>HeldKeyTracker</c>). Отказ гейта возвращается до
/// первого обращения к WinAPI, поэтому на Linux путь отказа исполняется целиком.
/// </summary>
[Collection(HandsBridgeGateRuntimeTests.Collection)]
public class HeldModifierTests
{
    private const string ServiceType = "Sbroenne.WindowsMcp.Input.KeyboardInputService";
    private const string ModifierKeyType = "Sbroenne.WindowsMcp.Models.ModifierKey";
    private const string SequenceItemType = "Sbroenne.WindowsMcp.Models.KeySequenceItem";

    /// <summary>Свежий экземпляр сервиса (не синглтон моста): свой <c>HeldKeyTracker</c>.</summary>
    private static object NewService() =>
        Activator.CreateInstance(HandsBridgeGateRuntimeTests.Bridge.Value
            .GetType(ServiceType, throwOnError: true)!)!;

    /// <summary>Виртуальный код модификатора по имени.</summary>
    private static int VkOf(string key) => key.ToLowerInvariant() switch
    {
        "ctrl" or "control" => 0x11,
        "shift" => 0x10,
        "alt" => 0x12,
        "win" => 0x5B,
        _ => throw new ArgumentException($"неизвестный модификатор '{key}'", nameof(key)),
    };

    /// <summary>Удерживает клавишу, минуя WinAPI (SendInput).</summary>
    private static void Hold(object service, string key)
    {
        var method = service.GetType().GetMethod("TrackHeldKeyForTest", BindingFlags.NonPublic | BindingFlags.Instance)!;
        method.Invoke(service, [key, VkOf(key)]);
    }

    /// <summary>Отпускает удержанную клавишу, минуя WinAPI.</summary>
    private static void Release(object service, string key)
    {
        var method = service.GetType().GetMethod("ReleaseHeldKeyForTest", BindingFlags.NonPublic | BindingFlags.Instance)!;
        method.Invoke(service, [key]);
    }

    /// <summary>
    /// Press (внутренний 5-аргументный overload). Возвращает (Success, Error); <paramref name="threw"/>
    /// — true, если исполнение ушло за гейт в WinAPI (на Linux: «гейт пропустил, дальше нет WinAPI»).
    /// </summary>
    private static (bool Success, string? Error, bool Threw) Press(
        object service, string key, string modifiers, nint window)
    {
        var type = service.GetType();
        var method = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Single(m => m.Name == "PressKeyAsync" && m.GetParameters().Length == 5);
        var mod = Enum.Parse(type.Assembly.GetType(ModifierKeyType)!, modifiers);

        try
        {
            var task = (Task)method.Invoke(service, [key, mod, 1, window, CancellationToken.None])!;
            task.GetAwaiter().GetResult();
            var result = task.GetType().GetProperty("Result")!.GetValue(task)!;
            return ((bool)result.GetType().GetProperty("Success")!.GetValue(result)!,
                    (string?)result.GetType().GetProperty("Error")!.GetValue(result),
                    false);
        }
        catch (TargetInvocationException)
        {
            return (false, null, true);
        }
    }

    /// <summary>Sequence (внутренний 4-аргументный overload): список нажатий, один шаг.</summary>
    private static (bool Success, string? Error, bool Threw) Sequence(
        object service, string key, string modifiers, nint window)
    {
        var type = service.GetType();
        var method = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Single(m => m.Name == "ExecuteSequenceAsync" && m.GetParameters().Length == 4);

        var itemType = type.Assembly.GetType(SequenceItemType)!;
        var mod = Enum.Parse(type.Assembly.GetType(ModifierKeyType)!, modifiers);
        var item = Activator.CreateInstance(itemType)!;
        itemType.GetProperty("Key")!.SetValue(item, key);
        itemType.GetProperty("Modifiers")!.SetValue(item, mod);

        // List<KeySequenceItem> (не List<object>): метод ждёт IReadOnlyList<KeySequenceItem>.
        var listType = typeof(List<>).MakeGenericType(itemType);
        var list = Activator.CreateInstance(listType)!;
        listType.GetMethod("Add")!.Invoke(list, [item]);

        try
        {
            var task = (Task)method.Invoke(service, [list, (object?)null, window, CancellationToken.None])!;
            task.GetAwaiter().GetResult();
            var result = task.GetType().GetProperty("Result")!.GetValue(task)!;
            return ((bool)result.GetType().GetProperty("Success")!.GetValue(result)!,
                    (string?)result.GetType().GetProperty("Error")!.GetValue(result),
                    false);
        }
        catch (TargetInvocationException)
        {
            return (false, null, true);
        }
    }

    /// <summary>Type (внутренний 3-аргументный overload): текст в окно.</summary>
    private static (bool Success, string? Error, bool Threw) Type(object service, string text, nint window)
    {
        var type = service.GetType();
        var method = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Single(m => m.Name == "TypeTextAsync" && m.GetParameters().Length == 3);

        try
        {
            var task = (Task)method.Invoke(service, [text, window, CancellationToken.None])!;
            task.GetAwaiter().GetResult();
            var result = task.GetType().GetProperty("Result")!.GetValue(task)!;
            return ((bool)result.GetType().GetProperty("Success")!.GetValue(result)!,
                    (string?)result.GetType().GetProperty("Error")!.GetValue(result),
                    false);
        }
        catch (TargetInvocationException)
        {
            return (false, null, true);
        }
    }

    /// <summary>KeyDown (внутренний 3-аргументный overload).</summary>
    private static (bool Success, string? Error, bool Threw) KeyDown(object service, string key, nint window)
    {
        var type = service.GetType();
        var method = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Single(m => m.Name == "KeyDownAsync" && m.GetParameters().Length == 3);

        try
        {
            var task = (Task)method.Invoke(service, [key, window, CancellationToken.None])!;
            task.GetAwaiter().GetResult();
            var result = task.GetType().GetProperty("Result")!.GetValue(task)!;
            return ((bool)result.GetType().GetProperty("Success")!.GetValue(result)!,
                    (string?)result.GetType().GetProperty("Error")!.GetValue(result),
                    false);
        }
        catch (TargetInvocationException)
        {
            return (false, null, true);
        }
    }

    // ---------- press: удержанный модификатор + Escape ----------

    [Fact]
    public void Held_ctrl_plus_press_escape_is_denied()
    {
        var service = NewService();
        Hold(service, "ctrl");

        var (success, error, threw) = Press(service, "escape", "None", nint.Zero);
        Assert.False(threw, "гейт должен отказать до WinAPI");
        Assert.False(success);
        Assert.Contains("Ctrl+Esc", error);
    }

    [Fact]
    public void Held_ctrl_shift_plus_press_escape_is_denied()
    {
        var service = NewService();
        Hold(service, "ctrl");
        Hold(service, "shift");

        var (success, error, threw) = Press(service, "escape", "None", nint.Zero);
        Assert.False(threw, "гейт должен отказать до WinAPI");
        Assert.False(success);
        Assert.Contains("Ctrl+Shift+Esc", error);
    }

    // ---------- sequence: удержанный модификатор + Escape ----------

    [Fact]
    public void Held_ctrl_plus_sequence_escape_is_denied()
    {
        var service = NewService();
        Hold(service, "ctrl");

        var (success, error, threw) = Sequence(service, "escape", "None", nint.Zero);
        Assert.False(threw, "гейт должен отказать до WinAPI");
        Assert.False(success);
        Assert.Contains("Ctrl+Esc", error);
    }

    // ---------- type: удержанный модификатор + символ ----------

    [Fact]
    public void Held_win_plus_type_r_is_denied()
    {
        var service = NewService();
        Hold(service, "win");

        var (success, error, threw) = Type(service, "r", nint.Zero);
        Assert.False(threw, "гейт должен отказать до WinAPI");
        Assert.False(success);
        Assert.Contains("Win+R", error);
    }

    // ---------- удержанный Alt + F4/Tab: как при явных модификаторах ----------

    [Fact]
    public void Held_alt_plus_f4_is_allowed_like_explicit()
    {
        var service = NewService();
        Hold(service, "alt");

        // Alt+F4 разрешён: гейт не отказывает (на Linux исполнение уходит в WinAPI — threw).
        var (_, _, threw) = Press(service, "f4", "None", nint.Zero);
        Assert.True(threw, "Alt+F4 должен пройти гейт (уходит в WinAPI), а не отклониться");
    }

    [Fact]
    public void Held_alt_plus_tab_is_allowed_like_explicit()
    {
        var service = NewService();
        Hold(service, "alt");

        var (_, _, threw) = Press(service, "tab", "None", nint.Zero);
        Assert.True(threw, "Alt+Tab должен пройти гейт (уходит в WinAPI), а не отклониться");
    }

    // ---------- отпущенный модификатор не мешает ----------

    [Fact]
    public void Released_ctrl_does_not_block_escape()
    {
        var service = NewService();
        Hold(service, "ctrl");
        Release(service, "ctrl");

        // Ctrl отпущен: Escape без модификаторов — гейт не отказывает (уходит в WinAPI).
        var (_, _, threw) = Press(service, "escape", "None", nint.Zero);
        Assert.True(threw, "отпущенный Ctrl не должен блокировать Escape");
    }

    // ---------- hands_batch: key_down в шаге 1, press в шаге 2 ----------

    /// <summary>
    /// Пакет: шаги идут через те же методы сервиса, поэтому удержание в шаге 1 видно в шаге 2.
    /// Проверяю на уровне сервиса (тот же HeldKeyTracker): key_down Ctrl, затем press Escape.
    /// </summary>
    [Fact]
    public void Batch_key_down_then_press_escape_is_denied()
    {
        var service = NewService();
        Hold(service, "ctrl");   // шаг 1 пакета: key_down Ctrl

        var (success, error, threw) = Press(service, "escape", "None", nint.Zero); // шаг 2: press Escape
        Assert.False(threw, "гейт должен отказать до WinAPI");
        Assert.False(success);
        Assert.Contains("Ctrl+Esc", error);
    }

    // ---------- key_down: удержанный модификатор + key_down Escape ----------

    [Fact]
    public void Held_ctrl_plus_key_down_escape_is_denied()
    {
        var service = NewService();
        Hold(service, "ctrl");

        var (success, error, threw) = KeyDown(service, "escape", nint.Zero);
        Assert.False(threw, "гейт должен отказать до WinAPI");
        Assert.False(success);
        Assert.Contains("Ctrl+Esc", error);
    }
}
