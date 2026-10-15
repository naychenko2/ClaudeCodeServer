using System.Reflection;
using System.Text.Json.Serialization;
using Xunit;

namespace HandsBridge.Policy.Tests;

/// <summary>
/// Схема <c>mouse_control</c> (D2b-2): MCP-SDK строит <c>enum</c> списка действий из C#-enum
/// <c>MouseControlAction</c>. В этой сборке моста — ровно восемь действий (move, click, double_click,
/// right_click, middle_click, drag, scroll, get_position); <c>polyline</c> в схеме нет.
/// Тест держит границу: лишнее действие не просочится в схему, а
/// <c>get_position</c> остаётся единственным, кто не трогает машину (см. <see cref="HandsActivityTests"/>).
/// </summary>
public class MouseControlToolSchemaTests
{
    private const string EnumTypeName = "Sbroenne.WindowsMcp.Tools.MouseControlTool+MouseControlAction";

    /// <summary>JSON-имена, которые обязан дать enum — порядок не важен, проверяется множество.</summary>
    private static readonly string[] ExpectedActions =
    [
        "move", "click", "double_click", "right_click", "middle_click", "drag", "scroll", "get_position",
    ];

    [Fact]
    public void Schema_enum_has_exactly_the_eight_supported_actions()
    {
        var names = EnumJsonNames();

        Assert.Equal(ExpectedActions.OrderBy(n => n), names.OrderBy(n => n));
    }

    [Fact]
    public void Drag_and_scroll_are_in_the_schema_but_polyline_is_not()
    {
        var names = EnumJsonNames();

        Assert.Contains("drag", names);
        Assert.Contains("scroll", names);
        Assert.DoesNotContain("polyline", names);
    }

    /// <summary>JSON-имена enum: значения <c>JsonStringEnumMemberName</c>, по одному на каждый член.</summary>
    private static List<string> EnumJsonNames()
    {
        var type = HandsBridgeGateRuntimeTests.Bridge.Value.GetType(EnumTypeName, throwOnError: true)
            ?? throw new InvalidOperationException($"{EnumTypeName} не найден в мосту");
        Assert.True(type.IsEnum, $"{EnumTypeName} не enum");

        var names = new List<string>();
        foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            var attribute = field.GetCustomAttribute<JsonStringEnumMemberNameAttribute>();
            Assert.NotNull(attribute);
            names.Add(attribute!.Name);
        }

        return names;
    }
}
