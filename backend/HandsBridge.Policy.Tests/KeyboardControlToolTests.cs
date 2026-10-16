using System.Reflection;
using System.Text.Json;
using Xunit;

namespace HandsBridge.Policy.Tests;

/// <summary>
/// Сквозные тесты <c>keyboard_control</c> (D2a): инструмент вызывается по-настоящему (рефлексия
/// по сборке моста). На Linux гейт не настроен (NoWindows), поэтому путь отказа исполняется
/// целиком: без <c>windowHandle</c>, в «запрещённое» окно (процесс неизвестен) и потолки,
/// которые проверяются до гейта.
/// </summary>
[Collection(HandsBridgeGateRuntimeTests.Collection)]
public class KeyboardControlToolTests
{
    private const string ToolType = "Sbroenne.WindowsMcp.Tools.KeyboardControlTool";

    /// <summary>Значение enum <c>KeyboardControlAction</c> по имени JSON-действия.</summary>
    private static object ParseAction(string jsonName)
    {
        // JSON-имя (snake_case) → имя члена enum (PascalCase)
        var csName = jsonName switch
        {
            "type" => "Type",
            "press" => "Press",
            "key_down" => "KeyDown",
            "key_up" => "KeyUp",
            "sequence" => "Sequence",
            _ => throw new ArgumentException($"Неизвестное действие '{jsonName}'", nameof(jsonName)),
        };

        var type = HandsBridgeGateRuntimeTests.Bridge.Value.GetType(ToolType, throwOnError: true)
            ?? throw new InvalidOperationException($"{ToolType} не найден в мосту");
        var actionType = type.GetNestedType("KeyboardControlAction")
            ?? throw new InvalidOperationException("KeyboardControlAction не найден");
        return Enum.Parse(actionType, csName);
    }

    private static Task<(bool IsError, string Text)> Call(Dictionary<string, object?> args) =>
        HandsBridgeGateRuntimeTests.Call(ToolType, args);

    private static void AssertGateDenied((bool IsError, string Text) result, string reason)
    {
        Assert.True(result.IsError, result.Text);

        using var json = JsonDocument.Parse(result.Text);
        Assert.Equal("hands_policy", json.RootElement.GetProperty("error").GetString());
        Assert.Equal("keyboard_control", json.RootElement.GetProperty("tool").GetString());
        Assert.Contains(reason, json.RootElement.GetProperty("message").GetString());
    }

    // ---------- отказ без windowHandle ----------

    [Theory]
    [InlineData("type")]
    [InlineData("press")]
    [InlineData("key_down")]
    [InlineData("key_up")]
    [InlineData("sequence")]
    public async Task Without_window_handle_is_denied(string action)
    {
        var result = await Call(new()
        {
            ["action"] = ParseAction(action),
            ["text"] = "hi",
            ["key"] = "a",
            ["sequence"] = "[{\"key\":\"a\"}]",
        });

        AssertGateDenied(result, "requires windowHandle");
    }

    // ---------- запрещённое окно ----------

    [Fact]
    public async Task Into_window_of_unknown_process_is_denied()
    {
        // На Linux гейт NoWindows: любое windowHandle — «процесс с неизвестным образом»,
        // ввод в него запрещён (то же, что в терминал/интерпретатор).
        var result = await Call(new()
        {
            ["action"] = ParseAction("type"),
            ["text"] = "hi",
            ["windowHandle"] = "123",
        });

        AssertGateDenied(result, "never click or type");
    }

    // ---------- потолки и форма (проверяются до гейта) ----------
    // Отказ сериализуется через WindowsToolsBase (COM-интероп) — на Linux только Windows,
    // как у мыши: путь гейта (AssertGateDenied) на Linux исполняется, эти — нет.

    [SkippableFact]
    public async Task Text_above_the_cap_is_denied()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "потолок: сериализация через WindowsToolsBase (COM) — только Windows");

        var result = await Call(new()
        {
            ["action"] = ParseAction("type"),
            ["text"] = new string('a', 10_001),
            ["windowHandle"] = "123",
        });

        Assert.True(result.IsError, result.Text);
        Assert.Contains("10 000", result.Text);
    }

    [SkippableFact]
    public async Task Sequence_above_the_cap_is_denied()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "потолок: сериализация через WindowsToolsBase (COM) — только Windows");

        var items = string.Join(",", Enumerable.Repeat("{\"key\":\"a\"}", 51));
        var result = await Call(new()
        {
            ["action"] = ParseAction("sequence"),
            ["sequence"] = $"[{items}]",
            ["windowHandle"] = "123",
        });

        Assert.True(result.IsError, result.Text);
        Assert.Contains("50", result.Text);
    }

    [SkippableFact]
    public async Task Press_without_key_is_denied()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "форма: сериализация через WindowsToolsBase (COM) — только Windows");

        var result = await Call(new()
        {
            ["action"] = ParseAction("press"),
            ["windowHandle"] = "123",
        });

        Assert.True(result.IsError, result.Text);
        Assert.Contains("key", result.Text);
    }

    // ---------- схема ----------

    /// <summary>
    /// Схема <c>keyboard_control</c> (D2a): ровно пять действий (type, press, key_down, key_up,
    /// sequence). Upstream-действия <c>release_all</c>, <c>get_keyboard_layout</c>,
    /// <c>wait_for_idle</c> в схеме нет: удержанные клавиши отпускает key_up, а в конце хода
    /// мост отпускает их сам.
    /// </summary>
    [Fact]
    public void Schema_enum_has_exactly_the_five_supported_actions()
    {
        var type = HandsBridgeGateRuntimeTests.Bridge.Value.GetType(ToolType, throwOnError: true)
            ?? throw new InvalidOperationException($"{ToolType} не найден в мосту");
        var actionType = type.GetNestedType("KeyboardControlAction")
            ?? throw new InvalidOperationException("KeyboardControlAction не найден");

        var names = new List<string>();
        foreach (var field in actionType.GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            var attribute = field.GetCustomAttribute<System.Text.Json.Serialization.JsonStringEnumMemberNameAttribute>();
            Assert.NotNull(attribute);
            names.Add(attribute!.Name);
        }

        Assert.Equal(
            ["key_down", "key_up", "press", "sequence", "type"],
            names.OrderBy(n => n));
    }
}
