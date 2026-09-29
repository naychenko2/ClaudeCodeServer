using System.Text.Json.Nodes;
using ClaudeHomeServer.Services.Llm.Gateway;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services.Gateway;

/// <summary>
/// Разворот обёртки {"item": [...]} в tool_use.input (дефект MiniMax-M3): схема инструмента
/// главнее эвристики, неизменённый input возвращается тем же экземпляром.
/// </summary>
public sealed class ToolInputNormalizerTests
{
    private static (JsonNode? Result, List<string> Changed) Run(string input, string? schema = null)
    {
        var changed = new List<string>();
        var result = ToolInputNormalizer.Normalize(JsonNode.Parse(input), schema is null ? null : JsonNode.Parse(schema), changed);
        return (result, changed);
    }

    private const string ArraySchema = """
        {"type":"object","properties":{"image_urls":{"type":"array","items":{"type":"string"}}}}
        """;

    [Fact]
    public void ОбёрткаМассива_РазворачиваетсяПоСхеме()
    {
        var (result, changed) = Run("""{"image_urls":{"item":["a","b"]},"n":1}""", ArraySchema);

        result!.ToJsonString().Should().Be("""{"image_urls":["a","b"],"n":1}""", "порядок полей сохранён");
        changed.Should().Equal("$.image_urls");
    }

    [Fact]
    public void ОбёрткаСкаляра_СтановитсяМассивомИзОдногоЭлемента()
    {
        var (result, changed) = Run("""{"image_urls":{"item":"x"}}""", ArraySchema);

        result!.ToJsonString().Should().Be("""{"image_urls":["x"]}""");
        changed.Should().ContainSingle();
    }

    [Fact]
    public void ВложенныеОбёртки_РазворачиваютсяНаВсехУровнях()
    {
        const string schema = """
            {"type":"object","properties":{"groups":{"type":"array","items":
              {"type":"object","properties":{"tags":{"type":"array","items":{"type":"string"}}}}}}}
            """;

        var (result, changed) = Run("""{"groups":{"item":[{"tags":{"item":["a"]}},{"tags":["b"]}]}}""", schema);

        result!.ToJsonString().Should().Be("""{"groups":[{"tags":["a"]},{"tags":["b"]}]}""");
        changed.Should().Equal("$.groups", "$.groups[0].tags");
    }

    [Fact]
    public void ЗаконноеПолеItem_ПриСхемеObject_НеТрогается()
    {
        const string schema = """
            {"type":"object","properties":{"order":{"type":"object","properties":{"item":{"type":"array"}}}}}
            """;
        var input = JsonNode.Parse("""{"order":{"item":["sku-1"]}}""");
        var changed = new List<string>();

        var result = ToolInputNormalizer.Normalize(input, JsonNode.Parse(schema), changed);

        result.Should().BeSameAs(input);
        result!.ToJsonString().Should().Be("""{"order":{"item":["sku-1"]}}""");
        changed.Should().BeEmpty();
    }

    [Fact]
    public void СвободныйObjectБезСхемыПоля_ЭвристикаРазворачивает()
    {
        // Форма fal submit_job: input — object без описанных свойств
        const string schema = """
            {"type":"object","properties":{"endpoint_id":{"type":"string"},"input":{"type":"object","additionalProperties":true}}}
            """;

        var (result, changed) = Run("""{"endpoint_id":"e","input":{"image_urls":{"item":["a","b"]},"aspect_ratio":"1:1"}}""", schema);

        result!.ToJsonString().Should().Be("""{"endpoint_id":"e","input":{"image_urls":["a","b"],"aspect_ratio":"1:1"}}""");
        changed.Should().Equal("$.input.image_urls");
    }

    [Fact]
    public void ОбъектСItemИДругимиПолями_НеОбёртка()
    {
        var (result, changed) = Run("""{"x":{"item":["a"],"other":1}}""");

        result!.ToJsonString().Should().Be("""{"x":{"item":["a"],"other":1}}""");
        changed.Should().BeEmpty();
    }

    [Fact]
    public void InputБезОбёрток_ВозвращаетсяТемЖеЭкземпляром()
    {
        var input = JsonNode.Parse("""{"image_urls":["a","b"],"nested":{"k":[{"v":1}]}}""");
        var before = input!.ToJsonString();
        var changed = new List<string>();

        var result = ToolInputNormalizer.Normalize(input, JsonNode.Parse(ArraySchema), changed);

        result.Should().BeSameAs(input);
        result!.ToJsonString().Should().Be(before);
        changed.Should().BeEmpty();
    }

    [Fact]
    public void СхемаAnyOfСМассивом_Разворачивает()
    {
        const string schema = """
            {"type":"object","properties":{"ids":{"anyOf":[{"type":"array","items":{"type":"integer"}},{"type":"null"}]}}}
            """;

        var (result, _) = Run("""{"ids":{"item":[1,2]}}""", schema);

        result!.ToJsonString().Should().Be("""{"ids":[1,2]}""");
    }
}
