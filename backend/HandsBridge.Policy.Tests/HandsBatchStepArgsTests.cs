using System.Reflection;
using System.Text.Json;
using Xunit;

namespace HandsBridge.Policy.Tests;

/// <summary>
/// Чистый разбор аргументов шага <c>hands_batch</c> (D2f, замечание Глеба (a)): функции
/// <c>HandsBatchStepArgs</c> без <c>WindowsToolsBase</c> и WinAPI, поэтому разбор числа, строки,
/// массива и «число как строка» проверяется на Linux напрямую. Мутация (сломать разбор числа)
/// красит тест: <c>Int</c> обязан вернуть null для нецелого, а не бросить.
/// </summary>
[Collection(HandsBridgeGateRuntimeTests.Collection)]
public class HandsBatchStepArgsTests
{
    private static Type ArgsType =>
        HandsBridgeGateRuntimeTests.Bridge.Value.GetType("Sbroenne.WindowsMcp.Tools.HandsBatchStepArgs", throwOnError: true)!;

    private static object? Call2(string method, object? a, object? b)
    {
        var m = ArgsType.GetMethod(method, BindingFlags.Public | BindingFlags.Static)!;
        return m.Invoke(null, [a, b]);
    }

    private static object? Call1(string method, object? a)
    {
        var m = ArgsType.GetMethod(method, BindingFlags.Public | BindingFlags.Static)!;
        return m.Invoke(null, [a]);
    }

    private static JsonElement El(string json) => JsonDocument.Parse(json).RootElement.Clone();

    // ---------- Int: целое ----------

    [Fact]
    public void Int_parses_a_number()
    {
        Assert.Equal(3, Call2("Int", El("""{"repeat":3}"""), "repeat"));
    }

    [Fact]
    public void Int_returns_null_for_a_non_integer()
    {
        // 1.5 не влезает в int32 — null, а не исключение
        Assert.Null(Call2("Int", El("""{"x":1.5}"""), "x"));
    }

    [Fact]
    public void Int_returns_null_for_a_string()
    {
        Assert.Null(Call2("Int", El("""{"x":"3"}"""), "x"));
    }

    [Fact]
    public void Int_returns_null_when_absent()
    {
        Assert.Null(Call2("Int", El("""{}"""), "x"));
    }

    [Fact]
    public void Int_returns_null_for_json_null()
    {
        Assert.Null(Call2("Int", El("""{"x":null}"""), "x"));
    }

    // ---------- Str: строка и «число как строка» ----------

    [Fact]
    public void Str_returns_a_string()
    {
        Assert.Equal("ctrl", Call2("Str", El("""{"modifiers":"ctrl"}"""), "modifiers"));
    }

    [Fact]
    public void Str_returns_raw_text_for_a_number()
    {
        // windowHandle числом: Str отдаёт его «сырой» текст, чтобы гейт прочитал число
        Assert.Equal("65552", Call2("Str", El("""{"windowHandle":65552}"""), "windowHandle"));
    }

    [Fact]
    public void Str_is_case_insensitive()
    {
        Assert.Equal("ctrl", Call2("Str", El("""{"Modifiers":"ctrl"}"""), "modifiers"));
    }

    [Fact]
    public void Str_returns_null_when_absent()
    {
        Assert.Null(Call2("Str", El("""{}"""), "modifiers"));
    }

    // ---------- Sequence: строка и сам массив ----------

    [Fact]
    public void Sequence_returns_a_string_as_is()
    {
        Assert.Equal("""["a","b"]""", Call1("Sequence", El("""{"sequence":"[\"a\",\"b\"]"}""")));
    }

    [Fact]
    public void Sequence_returns_raw_text_for_an_array()
    {
        // sequence сам JSON-массивом: «сырой» текст массива
        Assert.Equal("""["a","b"]""", Call1("Sequence", El("""{"sequence":["a","b"]}""")));
    }

    [Fact]
    public void Sequence_returns_null_when_absent()
    {
        Assert.Null(Call1("Sequence", El("""{}""")));
    }
}
