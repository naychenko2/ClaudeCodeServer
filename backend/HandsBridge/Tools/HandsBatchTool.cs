using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Sbroenne.WindowsMcp.Tools;

/// <summary>
/// MCP tool: a batch of hands actions in one call (hands without UIA, D2e). Every step is a call of
/// the real tool method (keyboard_control, mouse_control), so each step passes its own gate and its
/// own window checks; the batch never touches the input services directly.
/// </summary>
[McpServerToolType]
[SupportedOSPlatform("windows")]
public static partial class HandsBatchTool
{
    /// <summary>Потолок причины отказа шага в ответе, символов.</summary>
    private const int MaxReasonLength = 300;

    /// <summary>
    /// Шов для тестов: общий потолок времени пакета вместо <see cref="HandsPolicy.MaxBatchTotalMs"/>.
    /// В бою не задан.
    /// </summary>
    internal static int? TimeoutMsOverride = null;

    /// <summary>
    /// Шов для тестов: отпускание всех удержанных клавиш (HeldKeyTracker сервиса клавиатуры).
    /// Зовётся в finally пакета при любом исходе. По умолчанию — единственное обращение пакета к сервису ввода.
    /// </summary>
    internal static Func<CancellationToken, Task> ReleaseHeldKeysAsync =
        ct => WindowsToolsBase.KeyboardInputService.ReleaseAllKeysAsync(ct);

    /// <summary>Шов для тестов: финальный снимок. По умолчанию — screenshot_control.</summary>
    internal static Func<string?, CancellationToken, Task<CallToolResult>> CaptureAsync =
        (windowHandle, ct) => ScreenshotControlTool.ExecuteAsync(
            action: "capture",
            annotate: false,
            target: string.IsNullOrWhiteSpace(windowHandle) ? "primary_screen" : "window",
            monitorIndex: null,
            windowHandle: string.IsNullOrWhiteSpace(windowHandle) ? null : windowHandle,
            regionX: null,
            regionY: null,
            regionWidth: null,
            regionHeight: null,
            includeCursor: false,
            imageFormat: null,
            quality: null,
            outputMode: null,
            outputPath: null,
            includeImage: true,
            saveTo: null,
            cancellationToken: ct);

    /// <summary>
    /// Run several keyboard, mouse and wait steps in ONE call: faster and cheaper than many separate calls.
    /// The steps run in order and stop at the first failure; the response lists every executed step
    /// (index, tool, status, reason) and never repeats the typed text. The hands are locked once for the
    /// whole batch. Keys and buttons may be held across steps (key_down in one step, key_up in a later
    /// one), but whatever is still held when the batch ends, fails or is cancelled is released. The batch
    /// is capped at 20 steps and 60 seconds in total (waits and execution). Nested hands_batch, app,
    /// browser_*, window_management and screenshots are not steps: use the capture field for a final screenshot.
    /// </summary>
    /// <param name="steps">Steps in order. Each step: tool = keyboard_control, mouse_control or wait; args = the arguments of that tool as an object (for wait there are none); delayMs = pause AFTER the step in milliseconds (0-2000), and for a wait step the length of the wait (0-5000).</param>
    /// <param name="capture">Take a screenshot after the batch (as screenshot_control with includeImage). Default false.</param>
    /// <param name="captureWindowHandle">Window handle (decimal string) for the final screenshot; without it the primary screen is captured.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A call result with the per-step results and, with capture, the screenshot content blocks. <c>IsError</c> is true when a step failed or the time cap was hit.</returns>
    [McpServerTool(Name = "hands_batch", Title = "Hands Batch", Destructive = true, OpenWorld = false)]
    public static async partial Task<CallToolResult> ExecuteAsync(
        List<HandsBatchStep> steps,
        [DefaultValue(false)] bool capture,
        [DefaultValue(null)] string? captureWindowHandle,
        CancellationToken cancellationToken)
    {
        if (steps is null || steps.Any(s => s is null || string.IsNullOrWhiteSpace(s.Tool)))
        {
            return Failure("hands_batch needs a non-empty list of steps, each with a tool name.");
        }

        // Гейт пакета: форма, потолки шагов и ожиданий, запрещённые инструменты — до первого шага.
        var gate = HandsGate.Policy.CheckBatch(steps, TotalWaitMs(steps), capture);
        if (!gate.Allowed)
        {
            return HandsGate.Deny(HandsTools.HandsBatch, gate);
        }

        // Первая версия: только клавиатура, мышь и пауза; остальное гейт допускает, а мост — пока нет.
        var unsupported = steps.FirstOrDefault(s => !IsSupported(s.Tool));
        if (unsupported is not null)
        {
            return HandsGate.Deny(HandsTools.HandsBatch,
                $"hands_batch runs keyboard_control, mouse_control and wait steps; '{unsupported.Tool.Trim()}' is not supported in a batch yet: call it as a separate tool.");
        }

        // Замок рук и сигнал активности — один раз на пакет; шаги берут тот же замок (повторный захват
        // в том же ходу — не новый захват), между шагами он не отпускается.
        if (HandsGate.Acted(HandsTools.HandsBatch) is { } busy)
        {
            return busy;
        }

        var results = new List<StepOutcome>(steps.Count);
        var timedOut = false;
        var clock = Stopwatch.StartNew();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeoutMsOverride ?? HandsPolicy.MaxBatchTotalMs);

        try
        {
            for (var i = 0; i < steps.Count; i++)
            {
                var step = steps[i];
                var tool = step.Tool.Trim().ToLowerInvariant();
                var outcome = await RunStepAsync(i, tool, step, budget.Token);
                results.Add(outcome);
                if (outcome.Status != "ok")
                {
                    break;
                }

                // Пауза после шага — только если дальше есть шаг; у wait длительность и есть сам шаг.
                if (tool != "wait" && step.DelayMs is > 0 && i < steps.Count - 1)
                {
                    await Task.Delay(step.DelayMs.Value, budget.Token);
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Вышел общий потолок пакета, а не отмена хода
            timedOut = true;
        }
        finally
        {
            // Удержанные между шагами клавиши не переживают пакет: ни при отказе шага, ни при потолке,
            // ни при исключении или отмене хода.
            try
            {
                await ReleaseHeldKeysAsync(CancellationToken.None);
            }
            catch (Exception)
            {
                // Сбой отпускания не должен скрыть исход пакета
            }
        }

        var failed = results.FirstOrDefault(r => r.Status != "ok");
        var stoppedAt = failed?.Index ?? (timedOut ? results.Count : (int?)null);
        var summary = new
        {
            success = failed is null && !timedOut,
            stepsTotal = steps.Count,
            stepsExecuted = results.Count(r => r.Status == "ok"),
            stoppedAt,
            error = timedOut ? $"timeout: the batch exceeded {(TimeoutMsOverride ?? HandsPolicy.MaxBatchTotalMs)} ms" : null,
            elapsedMs = (int)clock.ElapsedMilliseconds,
            results = results.Select(r => new { index = r.Index, tool = r.Tool, status = r.Status, reason = r.Reason }),
        };

        var content = new List<ContentBlock>
        {
            new TextContentBlock
            {
                Text = JsonSerializer.Serialize(summary, ResponseOptions),
            },
        };
        var isError = failed is not null || timedOut;

        // Финальный снимок — после пакета, даже если шаг отказал: состояние окна после частичного ввода
        // важно видеть. Не снимаем только после потолка и отмены: бюджет времени исчерпан.
        if (capture && !timedOut)
        {
            var shot = await CaptureAsync(captureWindowHandle, cancellationToken);
            content.AddRange(shot.Content);
            isError |= shot.IsError == true;
        }

        return new CallToolResult { Content = content, IsError = isError };
    }

    private static readonly JsonSerializerOptions ResponseOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private static bool IsSupported(string tool) =>
        tool.Trim().ToLowerInvariant() is HandsTools.KeyboardControl or HandsTools.MouseControl or "wait";

    /// <summary>
    /// Сумма ожиданий пакета, как её считает мост: все wait-шаги плюс паузы между шагами
    /// (после последнего шага пауза не нужна). Гейт пересчитывает её сам и берёт большее.
    /// </summary>
    private static int TotalWaitMs(List<HandsBatchStep> steps)
    {
        long total = 0;
        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            if (step is null || step.Tool is null)
            {
                continue;
            }

            if (string.Equals(step.Tool.Trim(), "wait", StringComparison.OrdinalIgnoreCase))
            {
                total += Math.Max(0, step.DelayMs ?? 0);
            }
            else if (i < steps.Count - 1)
            {
                total += Math.Max(0, step.DelayMs ?? 0);
            }
        }

        return (int)Math.Min(total, int.MaxValue);
    }

    private sealed record StepOutcome(int Index, string Tool, string Status, string? Reason);

    private static async Task<StepOutcome> RunStepAsync(
        int index, string tool, HandsBatchStep step, CancellationToken cancellationToken)
    {
        if (tool == "wait")
        {
            await Task.Delay(step.DelayMs ?? 0, cancellationToken);
            return new StepOutcome(index, tool, "ok", null);
        }

        JsonElement args;
        try
        {
            args = ToElement(step.Args);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return new StepOutcome(index, tool, "failed", "The step args are not a JSON object.");
        }

        CallToolResult result;
        switch (tool)
        {
            case HandsTools.KeyboardControl:
            {
                if (!TryKeyboardAction(Str(args, "action"), out var keyboardAction))
                {
                    return new StepOutcome(index, tool, "failed",
                        "keyboard_control needs action: type, press, key_down, key_up or sequence.");
                }

                result = await KeyboardControlTool.ExecuteAsync(
                    keyboardAction,
                    Str(args, "text"),
                    Str(args, "key"),
                    Str(args, "modifiers"),
                    Int(args, "repeat") ?? 1,
                    SequenceArg(args),
                    Int(args, "interKeyDelayMs"),
                    Str(args, "windowHandle"),
                    cancellationToken);
                break;
            }

            case HandsTools.MouseControl:
            {
                if (!TryMouseAction(Str(args, "action"), out var mouseAction))
                {
                    return new StepOutcome(index, tool, "failed",
                        "mouse_control needs action: move, click, double_click, right_click, middle_click, drag, scroll or get_position.");
                }

                result = await MouseControlTool.ExecuteAsync(
                    mouseAction,
                    Int(args, "x"),
                    Int(args, "y"),
                    Int(args, "toX"),
                    Int(args, "toY"),
                    Str(args, "button"),
                    Str(args, "direction"),
                    Int(args, "amount"),
                    Str(args, "windowHandle"),
                    cancellationToken);
                break;
            }

            default:
                return new StepOutcome(index, tool, "failed", $"'{tool}' is not supported in a batch.");
        }

        return result.IsError == true
            ? new StepOutcome(index, tool, "failed", ReasonOf(result))
            : new StepOutcome(index, tool, "ok", null);
    }

    /// <summary>Аргументы шага как JSON-объект: после биндинга MCP это JsonElement, в тестах бывает и объект.</summary>
    private static JsonElement ToElement(object? args)
    {
        if (args is null)
        {
            return JsonSerializer.SerializeToElement(new { });
        }

        var element = args is JsonElement e ? e : JsonSerializer.SerializeToElement(args);
        if (element.ValueKind == JsonValueKind.Null)
        {
            return JsonSerializer.SerializeToElement(new { });
        }

        return element.ValueKind == JsonValueKind.Object
            ? element
            : throw new JsonException("The step args are not an object.");
    }

    private static bool TryGet(JsonElement args, string name, out JsonElement value)
    {
        foreach (var property in args.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return value.ValueKind != JsonValueKind.Null;
            }
        }

        value = default;
        return false;
    }

    private static string? Str(JsonElement args, string name) =>
        TryGet(args, name, out var v) ? (v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText()) : null;

    private static int? Int(JsonElement args, string name) =>
        TryGet(args, name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;

    /// <summary>sequence клавиатуры — строка с JSON-массивом; в пакете допускается и сам массив.</summary>
    private static string? SequenceArg(JsonElement args) =>
        TryGet(args, "sequence", out var v) ? (v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText()) : null;

    private static bool TryKeyboardAction(string? name, out KeyboardControlTool.KeyboardControlAction action)
    {
        action = name?.Trim().ToLowerInvariant() switch
        {
            "type" => KeyboardControlTool.KeyboardControlAction.Type,
            "press" => KeyboardControlTool.KeyboardControlAction.Press,
            "key_down" => KeyboardControlTool.KeyboardControlAction.KeyDown,
            "key_up" => KeyboardControlTool.KeyboardControlAction.KeyUp,
            "sequence" => KeyboardControlTool.KeyboardControlAction.Sequence,
            _ => (KeyboardControlTool.KeyboardControlAction)(-1),
        };
        return (int)action >= 0;
    }

    private static bool TryMouseAction(string? name, out MouseControlTool.MouseControlAction action)
    {
        action = name?.Trim().ToLowerInvariant() switch
        {
            "move" => MouseControlTool.MouseControlAction.Move,
            "click" => MouseControlTool.MouseControlAction.Click,
            "double_click" => MouseControlTool.MouseControlAction.DoubleClick,
            "right_click" => MouseControlTool.MouseControlAction.RightClick,
            "middle_click" => MouseControlTool.MouseControlAction.MiddleClick,
            "drag" => MouseControlTool.MouseControlAction.Drag,
            "scroll" => MouseControlTool.MouseControlAction.Scroll,
            "get_position" => MouseControlTool.MouseControlAction.GetPosition,
            _ => (MouseControlTool.MouseControlAction)(-1),
        };
        return (int)action >= 0;
    }

    /// <summary>Причина отказа шага из его ответа (message или error); текст ввода в ответах шагов не эхоится.</summary>
    private static string ReasonOf(CallToolResult result)
    {
        var text = result.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            return "The step failed.";
        }

        try
        {
            using var doc = JsonDocument.Parse(text);
            foreach (var name in new[] { "message", "error" })
            {
                if (doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty(name, out var value)
                    && value.ValueKind == JsonValueKind.String)
                {
                    return Truncate(value.GetString()!);
                }
            }
        }
        catch (JsonException)
        {
            // Ответ не JSON — отдаём как есть, обрезанным
        }

        return Truncate(text);
    }

    private static string Truncate(string text) =>
        text.Length <= MaxReasonLength ? text : text[..MaxReasonLength] + "…";

    /// <summary>Отказ формы вызова в своём виде, без WindowsToolsBase (его статика тянет WinAPI).</summary>
    private static CallToolResult Failure(string message) =>
        new()
        {
            Content = [new TextContentBlock { Text = JsonSerializer.Serialize(new { success = false, error = message }) }],
            IsError = true,
        };
}
