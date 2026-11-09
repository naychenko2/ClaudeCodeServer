using System.ComponentModel;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Sbroenne.WindowsMcp.Native;
using Sbroenne.WindowsMcp.Serialization;

namespace Sbroenne.WindowsMcp.Tools;

/// <summary>
/// MCP tool for keyboard input on Windows (hands without UIA, D2a): keys and typing into
/// windows that expose no UI Automation elements (remote desktops, canvases).
/// </summary>
[McpServerToolType]
[SupportedOSPlatform("windows")]
public static partial class KeyboardControlTool
{
    /// <summary>Потолок текста действия type: 10 000 символов.</summary>
    private const int MaxTextLength = 10_000;

    /// <summary>Потолок числа нажатий действия sequence: 50.</summary>
    private const int MaxSequenceKeys = 50;

    /// <summary>
    /// Клавиатурные действия этой сборки моста (D2a). Upstream-действия <c>release_all</c>,
    /// <c>get_keyboard_layout</c> и <c>wait_for_idle</c> в схеме нет: удержанные клавиши
    /// отпускает <c>key_up</c>, а в конце хода мост отпускает их сам. Схема статична и не
    /// зависит от хода.
    /// </summary>
    public enum KeyboardControlAction
    {
        /// <summary>Type text (Unicode, layout-independent).</summary>
        [JsonStringEnumMemberName("type")] Type,

        /// <summary>Press a key (with modifiers) and release it.</summary>
        [JsonStringEnumMemberName("press")] Press,

        /// <summary>Press a key and hold it until key_up or the end of the turn.</summary>
        [JsonStringEnumMemberName("key_down")] KeyDown,

        /// <summary>Release a key held with key_down.</summary>
        [JsonStringEnumMemberName("key_up")] KeyUp,

        /// <summary>Press a sequence of keys (JSON array).</summary>
        [JsonStringEnumMemberName("sequence")] Sequence,
    }

    /// <summary>
    /// Type text and press keys in a window that exposes no UI Automation elements (remote
    /// desktops, canvases). Use ui_type when ui_snapshot shows elements. The window must be the
    /// foreground window (activate it with window_management first) — otherwise the keys land in
    /// another window. For Ctrl+S use key='s' with modifiers='ctrl' (not key='ctrl+s').
    /// key_down holds the key until key_up or the end of the turn; the bridge releases held keys
    /// automatically when the turn ends.
    /// </summary>
    /// <param name="action">The keyboard action: type, press, key_down, key_up, or sequence.</param>
    /// <param name="text">Text to type (required for type, at most 10 000 characters).</param>
    /// <param name="key">The main key (required for press, key_down, key_up). Examples: enter, tab, escape, f1, a, s.</param>
    /// <param name="modifiers">Modifier keys held during the key press: ctrl, shift, alt, win (comma-separated).</param>
    /// <param name="repeat">Number of key presses (for press, default 1).</param>
    /// <param name="sequence">JSON array for the sequence action: [{"key":"f","modifiers":"alt"},{"key":"s"}]. At most 50 key presses.</param>
    /// <param name="interKeyDelayMs">Delay between keys in the sequence (milliseconds).</param>
    /// <param name="windowHandle">Window handle (decimal string from window_management). Required: the window must accept input and be the foreground window.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A call result with the operation outcome: characters typed, the key pressed, or the keys still held. <c>IsError</c> reflects success.</returns>
    [McpServerTool(Name = "keyboard_control", Title = "Keyboard Control", Destructive = true, OpenWorld = false)]
    public static async partial Task<CallToolResult> ExecuteAsync(
        KeyboardControlAction action,
        [DefaultValue(null)] string? text,
        [DefaultValue(null)] string? key,
        [DefaultValue(null)] string? modifiers,
        [DefaultValue(1)] int repeat,
        [DefaultValue(null)] string? sequence,
        [DefaultValue(null)] int? interKeyDelayMs,
        [DefaultValue(null)] string? windowHandle,
        CancellationToken cancellationToken)
    {
        var actionName = JsonNamingPolicy.SnakeCaseLower.ConvertName(action.ToString());

        // Параметры проверяются до гейта: некорректный вызов не должен зажигать сигнал активности.
        if (action == KeyboardControlAction.Type)
        {
            if (string.IsNullOrEmpty(text))
            {
                return ToCallToolResult(KeyboardControlResult.CreateFailure(
                    KeyboardControlErrorCode.MissingRequiredParameter,
                    "The type action requires the text parameter."));
            }

            if (text.Length > MaxTextLength)
            {
                return ToCallToolResult(KeyboardControlResult.CreateFailure(
                    KeyboardControlErrorCode.InvalidAction,
                    $"The text is {text.Length} characters; type accepts at most {MaxTextLength}."));
            }
        }

        if (action is KeyboardControlAction.Press or KeyboardControlAction.KeyDown or KeyboardControlAction.KeyUp
            && string.IsNullOrWhiteSpace(key))
        {
            return ToCallToolResult(KeyboardControlResult.CreateFailure(
                KeyboardControlErrorCode.MissingRequiredParameter,
                $"The {actionName} action requires the key parameter."));
        }

        if (action == KeyboardControlAction.Sequence)
        {
            if (string.IsNullOrWhiteSpace(sequence))
            {
                return ToCallToolResult(KeyboardControlResult.CreateFailure(
                    KeyboardControlErrorCode.MissingRequiredParameter,
                    "The sequence action requires the sequence parameter: a JSON array of key presses."));
            }

            var items = TryParseSequence(sequence);
            if (items is null)
            {
                return ToCallToolResult(KeyboardControlResult.CreateFailure(
                    KeyboardControlErrorCode.InvalidAction,
                    "Invalid sequence JSON: a JSON array of objects with a 'key' property is expected."));
            }

            if (items.Count > MaxSequenceKeys)
            {
                return ToCallToolResult(KeyboardControlResult.CreateFailure(
                    KeyboardControlErrorCode.InvalidAction,
                    $"The sequence has {items.Count} key presses; at most {MaxSequenceKeys} are allowed."));
            }
        }

        // Гейт: окно обязано принимать ввод и быть окном переднего плана, иначе клавиши
        // улетят в чужое окно.
        long? window = null;
        if (!string.IsNullOrWhiteSpace(windowHandle))
        {
            if (!WindowHandleParser.TryParse(windowHandle, out var parsedHandle) || parsedHandle == IntPtr.Zero)
            {
                return ToCallToolResult(KeyboardControlResult.CreateFailure(
                    KeyboardControlErrorCode.InvalidAction,
                    $"Invalid windowHandle: '{windowHandle}'. Expected a decimal string from window_management."));
            }

            window = parsedHandle.ToInt64();
        }

        var gate = HandsGate.Policy.CheckKeyboard(window, actionName);
        if (!gate.Allowed)
        {
            return HandsGate.Deny(HandsTools.KeyboardControl, gate);
        }

        // Первое действие хода, прошедшее гейт, поднимает сигнал активности
        if (HandsGate.Acted(HandsTools.KeyboardControl, actionName) is { } busy)
        {
            return busy;
        }

        // Все пути — через internal-перегрузки с expectedForegroundWindow: сервис перепроверяет
        // окно перед каждым SendInput (гонка «гейт → ввод»).
        nint target = IntPtr.Zero;
        KeyboardControlResult? operationResult = null;
        try
        {
            target = ResolveTargetWindow(windowHandle);

            operationResult = action switch
            {
                KeyboardControlAction.Type => await HandleTypeAsync(text, target, cancellationToken),
                KeyboardControlAction.Press => await HandlePressAsync(key, modifiers, repeat, target, cancellationToken),
                KeyboardControlAction.KeyDown => await HandleKeyDownAsync(key, target, cancellationToken),
                KeyboardControlAction.KeyUp => await HandleKeyUpAsync(key, target, cancellationToken),
                KeyboardControlAction.Sequence => await HandleSequenceAsync(sequence, interKeyDelayMs, target, cancellationToken),
                _ => KeyboardControlResult.CreateFailure(
                    KeyboardControlErrorCode.InvalidAction,
                    $"Unknown action: '{actionName}'"),
            };

            return ToCallToolResult(operationResult);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ErrorResult(WindowsToolsBase.SerializeToolError("keyboard_control", ex));
        }
        finally
        {
            // key_down, не завершившийся успехом: отпускаем клавишу, иначе она «залипнет»
            // (повторный key_down даст «Key already held»). Успешный key_down клавишу держит.
            if (action == KeyboardControlAction.KeyDown &&
                (operationResult is null || !operationResult.Success) &&
                !string.IsNullOrWhiteSpace(key))
            {
                _ = WindowsToolsBase.KeyboardInputService.KeyUpAsync(key!, target, CancellationToken.None);
            }
        }
    }

    /// <summary>
    /// Разрешает окно, за которое ведётся ввод: явный <paramref name="windowHandle"/> (гейт
    /// уже проверил, что оно принимает ввод и в переднем плане), иначе текущее окно переднего
    /// плана — чтобы ввод всё равно прервался, если окно потеряет фокус в середине действия.
    /// </summary>
    private static nint ResolveTargetWindow(string? windowHandle)
    {
        if (!string.IsNullOrWhiteSpace(windowHandle) && WindowHandleParser.TryParse(windowHandle, out var handle)
            && handle != IntPtr.Zero)
        {
            return handle;
        }

        return NativeMethods.GetForegroundWindow();
    }

    private static Task<KeyboardControlResult> HandleTypeAsync(string? text, nint window, CancellationToken cancellationToken) =>
        WindowsToolsBase.KeyboardInputService.TypeTextAsync(text!, window, cancellationToken);

    private static Task<KeyboardControlResult> HandlePressAsync(
        string? key, string? modifiers, int repeat, nint window, CancellationToken cancellationToken) =>
        WindowsToolsBase.KeyboardInputService.PressKeyAsync(key!, ParseModifiers(modifiers), repeat, window, cancellationToken);

    private static Task<KeyboardControlResult> HandleKeyDownAsync(string? key, nint window, CancellationToken cancellationToken) =>
        WindowsToolsBase.KeyboardInputService.KeyDownAsync(key!, window, cancellationToken);

    private static Task<KeyboardControlResult> HandleKeyUpAsync(string? key, nint window, CancellationToken cancellationToken) =>
        WindowsToolsBase.KeyboardInputService.KeyUpAsync(key!, window, cancellationToken);

    private static Task<KeyboardControlResult> HandleSequenceAsync(
        string? sequence, int? interKeyDelayMs, nint window, CancellationToken cancellationToken) =>
        WindowsToolsBase.KeyboardInputService.ExecuteSequenceAsync(
            TryParseSequence(sequence)!, interKeyDelayMs, window, cancellationToken);

    /// <summary>
    /// Разбор JSON-массива нажатий; null — не массив или нет свойства <c>key</c>.
    /// </summary>
    private static List<KeySequenceItem>? TryParseSequence(string? sequenceJson)
    {
        if (string.IsNullOrWhiteSpace(sequenceJson))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<List<KeySequenceItem>>(sequenceJson, McpJsonOptions.Default);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Модификаторы: ctrl, shift, alt, win (через запятую); «control»/«windows»/«meta» — синонимы.
    /// </summary>
    private static ModifierKey ParseModifiers(string? modifiers)
    {
        if (string.IsNullOrWhiteSpace(modifiers))
        {
            return ModifierKey.None;
        }

        var result = ModifierKey.None;
        var parts = modifiers.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var part in parts)
        {
            result |= part.ToLowerInvariant() switch
            {
                "ctrl" or "control" => ModifierKey.Ctrl,
                "shift" => ModifierKey.Shift,
                "alt" => ModifierKey.Alt,
                "win" or "windows" or "meta" => ModifierKey.Win,
                _ => ModifierKey.None
            };
        }

        return result;
    }

    /// <summary>
    /// Преобразует результат клавиатуры в MCP-результат. <see cref="CallToolResult.IsError"/>
    /// зеркалит <see cref="KeyboardControlResult.Success"/>.
    /// </summary>
    private static CallToolResult ToCallToolResult(KeyboardControlResult result) =>
        new()
        {
            Content = [new TextContentBlock { Text = JsonSerializer.Serialize(result, WindowsToolsBase.JsonOptions) }],
            IsError = !result.Success
        };

    /// <summary>
    /// Оборачивает уже сериализованный JSON-ошибка в неудачный результат вызова.
    /// </summary>
    private static CallToolResult ErrorResult(string json) =>
        new()
        {
            Content = [new TextContentBlock { Text = json }],
            IsError = true
        };
}
