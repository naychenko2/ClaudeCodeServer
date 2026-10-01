using System.Text.Json.Nodes;
using ClaudeHomeServer.Services.AudioEditor;
using ClaudeHomeServer.Services.AudioEditor.Schema;
using FluentAssertions;

namespace ClaudeHomeServer.AudioEditor.Tests.Schema;

public sealed class AudioParamValidatorTests
{
    private static readonly AudioParamSchema Schema = new("fal", "fal-ai/x", AudioSchemaSources.FalOpenApi,
    [
        new AudioParamField("prompt_influence", AudioParamTypes.Number, Min: 0, Max: 1),
        new AudioParamField("steps", AudioParamTypes.Integer, Min: 4, Max: 100),
        new AudioParamField("loop", AudioParamTypes.Boolean),
        new AudioParamField("output_format", AudioParamTypes.String, Enum: [JsonValue.Create("mp3")!, JsonValue.Create("wav")!]),
        new AudioParamField("style", AudioParamTypes.String, MaxLength: 5),
        new AudioParamField("duration", AudioParamTypes.Number, Nullable: true),
        new AudioParamField("voice_setting", AudioParamTypes.Object, Fields:
        [
            new AudioParamField("speed", AudioParamTypes.Number, Min: 0.5, Max: 2),
        ]),
        new AudioParamField("stems", AudioParamTypes.Array,
            Items: new AudioParamField("item", AudioParamTypes.String, Enum: [JsonValue.Create("vocals")!, JsonValue.Create("drums")!])),
        new AudioParamField("cfg", AudioParamTypes.Number, Passed: false, NotPassed: "шов не принимает"),
    ],
    ["text", "seed"]);

    private static string? Check(string json) => AudioParamValidator.Validate(Schema, JsonNode.Parse(json)!.AsObject());

    [Fact]
    public void ValidParams_Pass()
    {
        Check("""{"prompt_influence":0.3,"steps":50,"loop":true,"output_format":"wav","style":"ok","duration":null,"voice_setting":{"speed":1.5},"stems":["vocals","drums"]}""")
            .Should().BeNull();
        AudioParamValidator.Validate(Schema, null).Should().BeNull();
    }

    [Fact]
    public void UnknownKey_RefusedWithName()
    {
        Check("""{"prompt_influence":0.3,"temperature":1}""").Should().Be("Неизвестный параметр «temperature» у модели fal-ai/x");
        Check("""{"voice_setting":{"pitch":3}}""").Should().Be("Неизвестный параметр «voice_setting.pitch»");
    }

    [Fact]
    public void CommonField_NotDuplicatedInParams()
    {
        Check("""{"text":"привет"}""").Should().Be("Параметр «text» задаётся общим полем запроса, а не в params");
    }

    [Theory]
    [InlineData("""{"prompt_influence":1.5}""", "Параметр «prompt_influence» — от 0 до 1")]
    [InlineData("""{"prompt_influence":-0.1}""", "Параметр «prompt_influence» — от 0 до 1")]
    [InlineData("""{"steps":3}""", "Параметр «steps» — от 4 до 100")]
    [InlineData("""{"steps":10.5}""", "Параметр «steps» — целое число")]
    [InlineData("""{"steps":"10"}""", "Параметр «steps» — число")]
    [InlineData("""{"voice_setting":{"speed":3}}""", "Параметр «voice_setting.speed» — от 0.5 до 2")]
    public void RangesAndTypes_RefusedWithName(string json, string expected) => Check(json).Should().Be(expected);

    [Theory]
    [InlineData("""{"loop":"yes"}""", "Параметр «loop» — да или нет")]
    [InlineData("""{"output_format":"ogg"}""", "Параметр «output_format» — одно из: mp3, wav")]
    [InlineData("""{"style":"слишком"}""", "Параметр «style» — не длиннее 5 символов")]
    [InlineData("""{"stems":["piano"]}""", "Параметр «stems[0]» — одно из: vocals, drums")]
    [InlineData("""{"stems":"vocals"}""", "Параметр «stems» — список")]
    [InlineData("""{"voice_setting":1}""", "Параметр «voice_setting» — объект")]
    [InlineData("""{"steps":null}""", "Параметр «steps» не может быть пустым")]
    public void EnumsAndShapes_RefusedWithName(string json, string expected) => Check(json).Should().Be(expected);

    [Fact]
    public void NotPassedParam_RefusedHonestly()
    {
        Check("""{"cfg":7}""").Should().Be("Параметр «cfg» пока не передаётся в модель fal-ai/x: шов не принимает");
    }
}
