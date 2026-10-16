using System.Collections;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;
using Xunit;

namespace HandsBridge.Policy.Tests;

/// <summary>
/// Сквозные тесты <c>hands_batch</c> (D2e): инструмент вызывается по-настоящему (рефлексия по сборке
/// моста). На Linux гейт не настроен (NoWindows), поэтому шаги, которые исполняются целиком, —
/// <c>wait</c> и отказ <c>keyboard_control</c> без <c>windowHandle</c> (отказ гейта до WinAPI).
/// Отпускание удержанных клавиш и финальный снимок подменяются швами инструмента, потому что
/// сервисы ввода и снимка на Linux не работают. Что сервис ввода после отпускания клавиши не держит —
/// проверяет его собственный HeldKeyTracker (Windows); здесь доказано, что пакет отпускание ЗОВЁТ
/// при любом исходе.
/// </summary>
[Collection(HandsBridgeGateRuntimeTests.Collection)]
public sealed class HandsBatchToolTests : IDisposable
{
    private const string ToolType = "Sbroenne.WindowsMcp.Tools.HandsBatchTool";
    private const BindingFlags Seam = BindingFlags.NonPublic | BindingFlags.Static;

    private readonly Type _tool = HandsBridgeGateRuntimeTests.Bridge.Value.GetType(ToolType, throwOnError: true)!;
    private int _releases;
    private int _captures;
    private string? _captureHandle;

    public HandsBatchToolTests()
    {
        SetSeam("ReleaseHeldKeysAsync", (Func<CancellationToken, Task>)(_ =>
        {
            Interlocked.Increment(ref _releases);
            return Task.CompletedTask;
        }));
        SetSeam("TimeoutMsOverride", null);
        SetCapture();
    }

    public void Dispose()
    {
        // Шов отпускания без подмены вернёт боевую реализацию (сервис ввода) — на Linux она не нужна
        SetSeam("TimeoutMsOverride", null);
    }

    // ---------- по шагам ----------

    [Fact]
    public async Task Waits_run_in_order_and_report_every_step()
    {
        var result = await Run([Step("wait", 10), Step("wait", 10), Step("wait", 10)]);

        Assert.False(result.IsError, result.Text);
        Assert.True(result.Root.GetProperty("success").GetBoolean());
        Assert.Equal(3, result.Root.GetProperty("stepsTotal").GetInt32());
        Assert.Equal(3, result.Root.GetProperty("stepsExecuted").GetInt32());
        var steps = result.Root.GetProperty("results").EnumerateArray().ToList();
        Assert.Equal([0, 1, 2], steps.Select(s => s.GetProperty("index").GetInt32()));
        Assert.All(steps, s =>
        {
            Assert.Equal("wait", s.GetProperty("tool").GetString());
            Assert.Equal("ok", s.GetProperty("status").GetString());
        });
    }

    [Fact]
    public async Task Stops_at_the_first_failure_and_does_not_run_the_rest()
    {
        // Шаг 1 — печать без windowHandle: отказ гейта. Шаг 2 не должен выполниться.
        var result = await Run([
            Step("wait", 5),
            Step("keyboard_control", args: new { action = "type", text = "секретный текст" }),
            Step("wait", 5),
        ]);

        Assert.True(result.IsError, result.Text);
        Assert.False(result.Root.GetProperty("success").GetBoolean());
        Assert.Equal(1, result.Root.GetProperty("stoppedAt").GetInt32());
        Assert.Equal(1, result.Root.GetProperty("stepsExecuted").GetInt32());

        var steps = result.Root.GetProperty("results").EnumerateArray().ToList();
        Assert.Equal(2, steps.Count);
        Assert.Equal("ok", steps[0].GetProperty("status").GetString());
        Assert.Equal("keyboard_control", steps[1].GetProperty("tool").GetString());
        Assert.Equal("failed", steps[1].GetProperty("status").GetString());
        Assert.Contains("windowHandle", steps[1].GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Response_never_echoes_the_typed_text()
    {
        var result = await Run([Step("keyboard_control", args: new { action = "type", text = "секретный текст" })]);

        Assert.True(result.IsError, result.Text);
        Assert.DoesNotContain("секретный текст", result.Text);
    }

    // ---------- удержание не переживает пакет ----------

    [Fact]
    public async Task Held_keys_are_released_after_a_successful_batch()
    {
        await Run([Step("wait", 5)]);

        Assert.Equal(1, _releases);
    }

    [Fact]
    public async Task Held_keys_are_released_after_a_failed_step()
    {
        await Run([Step("keyboard_control", args: new { action = "key_down", key = "a" }), Step("wait", 5)]);

        Assert.Equal(1, _releases);
    }

    [Fact]
    public async Task Held_keys_are_released_when_the_turn_is_cancelled()
    {
        using var cts = new CancellationTokenSource();
        var running = Run([Step("wait", 2000)], token: cts.Token);
        await Task.Delay(50);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        Assert.Equal(1, _releases);
    }

    [Fact]
    public async Task Held_keys_are_released_when_the_time_cap_is_hit()
    {
        SetSeam("TimeoutMsOverride", 200);

        var result = await Run([Step("wait", 2000)]);

        Assert.True(result.IsError, result.Text);
        Assert.Equal(1, _releases);
    }

    // ---------- потолок времени ----------

    [Fact]
    public async Task Time_cap_stops_the_batch()
    {
        SetSeam("TimeoutMsOverride", 200);
        var started = DateTime.UtcNow;

        var result = await Run([Step("wait", 2000), Step("wait", 5)]);

        Assert.True(DateTime.UtcNow - started < TimeSpan.FromMilliseconds(1500), "пакет дождался всего ожидания");
        Assert.True(result.IsError, result.Text);
        Assert.False(result.Root.GetProperty("success").GetBoolean());
        Assert.Contains("timeout", result.Root.GetProperty("error").GetString());
        Assert.Equal(0, result.Root.GetProperty("stepsExecuted").GetInt32());
    }

    // ---------- финальный снимок ----------

    [Fact]
    public async Task Final_capture_is_taken_after_the_steps_and_returned_like_a_screenshot()
    {
        var result = await Run([Step("wait", 5)], capture: true, captureHandle: "4242");

        Assert.False(result.IsError, result.Text);
        Assert.Equal(1, _captures);
        Assert.Equal("4242", _captureHandle);
        Assert.Equal(3, result.ContentCount);
        Assert.Equal(1, _releases);
    }

    [Fact]
    public async Task Capture_is_not_taken_unless_requested()
    {
        await Run([Step("wait", 5)]);

        Assert.Equal(0, _captures);
    }

    // ---------- гейт пакета ----------

    [Fact]
    public async Task Nested_batch_is_denied_before_any_step()
    {
        var result = await Run([Step("wait", 5), Step("hands_batch")], capture: true);

        AssertDenied(result, "does not nest");
        Assert.Equal(0, _releases);
        Assert.Equal(0, _captures);
    }

    [Fact]
    public async Task Too_many_steps_are_denied_before_any_step()
    {
        var result = await Run(Enumerable.Range(0, 21).Select(_ => Step("wait", 1)).ToList());

        AssertDenied(result, "21 steps");
        Assert.Equal(0, _releases);
    }

    [Fact]
    public async Task Tool_outside_the_first_version_is_denied_before_any_step()
    {
        var result = await Run([Step("wait", 5), Step("ui_click")]);

        AssertDenied(result, "not supported in a batch");
        Assert.Equal(0, _releases);
    }

    /// <summary>
    /// Страж (замечание Глеба (b)): гейт <c>HandsPolicy.CheckBatch</c> ДОПУСКАЕТ <c>window_management</c>
    /// (кроме <c>close</c>), но мост первой версии его ещё не исполняет — <c>IsSupported</c> его не
    /// знает. Пока это так, пакет с <c>window_management</c> обязан умирать ОТКАЗОМ МОСТА, а не
    /// проскальзывать в окно. Когда <c>window_management</c> разрешат в пакете (и починят
    /// <c>GetStringArg</c> под <c>JsonElement</c>) — этот страж красит: мост его уже исполняет.
    /// </summary>
    [Fact]
    public async Task Window_management_in_a_batch_is_denied_by_the_bridge_until_the_lock()
    {
        var result = await Run([Step("wait", 5), Step("window_management", args: new { action = "activate" })]);

        AssertDenied(result, "not supported in a batch");
        Assert.Equal(0, _releases);
    }

    // ---------- хелперы ----------

    private static void AssertDenied(BatchResult result, string reason)
    {
        Assert.True(result.IsError, result.Text);
        Assert.Equal("hands_policy", result.Root.GetProperty("error").GetString());
        Assert.Equal("hands_batch", result.Root.GetProperty("tool").GetString());
        Assert.Contains(reason, result.Root.GetProperty("message").GetString());
    }

    private sealed record StepSpec(string Tool, int? DelayMs, object? Args);

    private static StepSpec Step(string tool, int? delay = null, object? args = null) => new(tool, delay, args);

    private sealed record BatchResult(bool IsError, string Text, int ContentCount)
    {
        public JsonElement Root { get; } = JsonDocument.Parse(Text).RootElement.Clone();
    }

    private async Task<BatchResult> Run(
        IReadOnlyList<StepSpec> steps, bool capture = false, string? captureHandle = null, CancellationToken token = default)
    {
        var method = _tool.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(m => m.GetCustomAttributes().Any(a => a.GetType().Name == "McpServerToolAttribute"));

        // HandsBatchStep живёт в копии Policy из контекста моста: берём тип из сигнатуры инструмента
        var listType = method.GetParameters()[0].ParameterType;
        var stepType = listType.GetGenericArguments()[0];
        var list = (IList)Activator.CreateInstance(listType)!;
        foreach (var s in steps)
        {
            object? args = s.Args is null ? null : JsonSerializer.SerializeToElement(s.Args);
            list.Add(Activator.CreateInstance(stepType, s.Tool, s.DelayMs, args));
        }

        var task = (Task)method.Invoke(null, [list, capture, captureHandle, token])!;
        await task;

        var result = task.GetType().GetProperty("Result")!.GetValue(task)!;
        var isError = (bool?)result.GetType().GetProperty("IsError")!.GetValue(result) ?? false;
        var blocks = ((IEnumerable)result.GetType().GetProperty("Content")!.GetValue(result)!).Cast<object>().ToList();
        var text = (string)blocks[0].GetType().GetProperty("Text")!.GetValue(blocks[0])!;
        return new BatchResult(isError, text, blocks.Count);
    }

    private void SetSeam(string name, object? value) =>
        _tool.GetField(name, Seam)!.SetValue(null, value);

    /// <summary>Подмена финального снимка: тип результата — из контекста моста, поэтому делегат собирается деревом выражений.</summary>
    private void SetCapture()
    {
        var field = _tool.GetField("CaptureAsync", Seam)!;
        var resultType = field.FieldType.GetGenericArguments()[2].GetGenericArguments()[0];
        var make = typeof(HandsBatchToolTests).GetMethod(nameof(Shot), BindingFlags.NonPublic | BindingFlags.Instance)!
            .MakeGenericMethod(resultType);

        var handle = Expression.Parameter(typeof(string), "handle");
        var ct = Expression.Parameter(typeof(CancellationToken), "ct");
        var body = Expression.Call(Expression.Constant(this), make, handle, Expression.Constant(resultType, typeof(Type)));
        field.SetValue(null, Expression.Lambda(field.FieldType, body, handle, ct).Compile());
    }

    private Task<T> Shot<T>(string? handle, Type resultType)
    {
        Interlocked.Increment(ref _captures);
        _captureHandle = handle;

        var result = Activator.CreateInstance(resultType)!;
        var contentProperty = resultType.GetProperty("Content")!;
        var blockBase = contentProperty.PropertyType.GetGenericArguments()[0];
        var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(blockBase))!;

        var textBlockType = resultType.Assembly.GetType("ModelContextProtocol.Protocol.TextContentBlock", throwOnError: true)!;
        var block = Activator.CreateInstance(textBlockType)!;
        textBlockType.GetProperty("Text")!.SetValue(block, "{\"success\":true,\"imageWidth\":1}");
        list.Add(block);
        list.Add(block);
        contentProperty.SetValue(result, list);
        return Task.FromResult((T)result);
    }
}
