using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeHomeServer.Services.AudioEditor;
using ClaudeHomeServer.Services.AudioEditor.Catalog;
using ClaudeHomeServer.Services.AudioEditor.Schema;
using FluentAssertions;

namespace ClaudeHomeServer.AudioEditor.Tests.Schema;

// Разбор схем на живых ответах fal: GET /v1/models?endpoint_id=…&expand=openapi-3.0, снято 2026-10-01.
// В отличие от синтетики в AudioSchemaTests, вложенный объект здесь — $ref с соседними description/default
// (без allOf), а необязательный объект — anyOf из $ref и null
public sealed class FalLiveSchemaTests
{
    internal static JsonElement LiveOpenApi(string file)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Schema", "Fixtures", file);
        var models = JsonDocument.Parse(File.ReadAllText(path)).RootElement.GetProperty("models");
        return models[0].GetProperty("openapi");
    }

    private static AudioParamField Single(IReadOnlyList<AudioParamField> fields, string key) => fields.Single(f => f.Key == key);

    [Fact]
    public void ElevenLabsSfx_LiveResponse_FieldsTypesBoundsAndOrder()
    {
        var fields = FalSchemaReader.Read(LiveOpenApi("fal-elevenlabs-sound-effects-v2.json"))!;

        fields.Select(f => f.Key).Should().Equal("text", "duration_seconds", "prompt_influence", "output_format", "loop");

        var text = Single(fields, "text");
        text.Type.Should().Be(AudioParamTypes.String);
        text.MaxLength.Should().Be(450);

        var duration = Single(fields, "duration_seconds");
        duration.Type.Should().Be(AudioParamTypes.Number);
        duration.Nullable.Should().BeTrue();
        (duration.Min, duration.Max).Should().Be((0.5, 22));
        duration.Title.Should().Be("Duration Seconds");

        var influence = Single(fields, "prompt_influence");
        (influence.Min, influence.Max).Should().Be((0, 1));
        influence.Default!.GetValue<double>().Should().Be(0.3);

        var format = Single(fields, "output_format");
        format.Default!.GetValue<string>().Should().Be("mp3_44100_128");
        format.Enum!.Select(v => v.GetValue<string>()).Should().Contain(["mp3_44100_128", "pcm_16000", "opus_48000_192"]);

        var loop = Single(fields, "loop");
        loop.Type.Should().Be(AudioParamTypes.Boolean);
        loop.Default!.GetValue<bool>().Should().BeFalse();
    }

    [Fact]
    public void MiniMaxHd_LiveResponse_NestedRefsWithSiblingsAndNullableObjects()
    {
        var fields = FalSchemaReader.Read(LiveOpenApi("fal-minimax-speech-2.8-hd.json"))!;

        fields.Select(f => f.Key).Should().Equal("prompt", "voice_setting", "audio_setting", "language_boost",
            "output_format", "pronunciation_dict", "normalization_setting", "voice_modify");

        // $ref с соседними description и default: описание и умолчание — от поля, поля — от компонента
        var voice = Single(fields, "voice_setting");
        voice.Type.Should().Be(AudioParamTypes.Object);
        voice.Nullable.Should().BeFalse();
        voice.Description.Should().Be("Voice configuration settings");
        voice.Default!["voice_id"]!.GetValue<string>().Should().Be("Wise_Woman");
        voice.Fields!.Select(f => f.Key).Should().Contain(["pitch", "voice_id", "speed", "emotion", "english_normalization", "vol"]);
        var pitch = Single(voice.Fields!, "pitch");
        pitch.Type.Should().Be(AudioParamTypes.Integer);
        (pitch.Min, pitch.Max).Should().Be((-12, 12));
        var emotion = Single(voice.Fields!, "emotion");
        emotion.Nullable.Should().BeTrue();
        emotion.Enum!.Select(v => v.GetValue<string>()).Should().Contain(["happy", "neutral"]);

        var audio = Single(fields, "audio_setting");
        audio.Fields!.Select(f => f.Key).Should().Equal("sample_rate", "bitrate", "format", "channel");
        Single(audio.Fields!, "sample_rate").Enum!.Select(v => v.GetValue<int>()).Should().Contain([32000, 44100]);

        // anyOf из $ref и null — объект с полями, а не Any
        var modify = Single(fields, "voice_modify");
        modify.Type.Should().Be(AudioParamTypes.Object);
        modify.Nullable.Should().BeTrue();
        modify.Fields!.Select(f => f.Key).Should().Equal("pitch", "intensity", "timbre");

        var dict = Single(fields, "pronunciation_dict");
        dict.Nullable.Should().BeTrue();
        var tones = Single(dict.Fields!, "tone_list");
        tones.Type.Should().Be(AudioParamTypes.Array);
        tones.Items!.Type.Should().Be(AudioParamTypes.String);

        var boost = Single(fields, "language_boost");
        boost.Type.Should().Be(AudioParamTypes.String);
        boost.Nullable.Should().BeTrue();
        boost.Enum!.Select(v => v.GetValue<string>()).Should().Contain(["Russian", "auto"]);

        Single(fields, "prompt").MaxLength.Should().Be(10000);
    }

    // Схема живого MiniMax в сборе с каталогом: общие поля закрыты, вложенные значения проверяются по компонентам
    [Fact]
    public void MiniMaxHd_LiveSchema_ValidatesNestedParams()
    {
        var fal = AudioCatalog.FindFal(AudioCatalog.FalMiniMaxHd)!;
        var read = FalSchemaReader.Read(LiveOpenApi("fal-minimax-speech-2.8-hd.json"))!;
        var reserved = FalSchemaReader.Reserved(fal.Fields, fields: read);
        var schema = new AudioParamSchema("fal", fal.Info.Id, AudioSchemaSources.FalOpenApi,
            [.. read.Where(f => !reserved.Contains(f.Key))], reserved);

        AudioParamValidator.Validate(schema, new JsonObject
        {
            ["voice_setting"] = new JsonObject { ["pitch"] = 3, ["emotion"] = "happy" },
            ["audio_setting"] = new JsonObject { ["sample_rate"] = 44100 },
            ["voice_modify"] = null,
        }).Should().BeNull();
        AudioParamValidator.Validate(schema, new JsonObject { ["voice_setting"] = new JsonObject { ["pitch"] = 20 } })
            .Should().Be("Параметр «voice_setting.pitch» — от -12 до 12");
        AudioParamValidator.Validate(schema, new JsonObject { ["audio_setting"] = new JsonObject { ["sample_rate"] = 48000 } })
            .Should().StartWith("Параметр «audio_setting.sample_rate» — одно из:");
        AudioParamValidator.Validate(schema, new JsonObject { ["prompt"] = "x" })
            .Should().Be("Параметр «prompt» задаётся общим полем запроса, а не в params");
    }

    // ── Поля-ссылки ─────────────────────────────────────────────────────────────

    // Живой ElevenLabs с подмешанными полями-ссылками: сверху и внутри вложенного объекта
    internal static string WithLinks()
    {
        var root = JsonNode.Parse(LiveOpenApi("fal-elevenlabs-sound-effects-v2.json").GetRawText())!;
        var props = root["components"]!["schemas"]!["ElevenlabsSoundEffectsV2Input"]!["properties"]!.AsObject();
        foreach (var key in LinkKeys)
            props[key] = new JsonObject { ["type"] = "string" };
        props["extra"] = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["gain"] = new JsonObject { ["type"] = "number" },
                ["url"] = new JsonObject { ["type"] = "string" },
                ["notify_webhook"] = new JsonObject { ["type"] = "string" },
                ["source_uri"] = new JsonObject { ["type"] = "string" },
            },
        };
        return root.ToJsonString();
    }

    internal static readonly string[] LinkKeys =
        ["webhook", "webhook_url", "fal_webhook", "callback_url", "callbackUrl", "image_url", "mask_urls", "redirect_uri",
         "upload_endpoint"];

    [Theory]
    [InlineData("webhook", true)]
    [InlineData("webhook_url", true)]
    [InlineData("fal_webhook", true)]
    [InlineData("callback", true)]
    [InlineData("callbackUrl", true)]
    [InlineData("audio_url", true)]
    [InlineData("image_urls", true)]
    [InlineData("url", true)]
    [InlineData("uri", true)]
    [InlineData("uris", true)]
    [InlineData("redirect_uri", true)]
    [InlineData("Redirect_URI", true)]
    [InlineData("source_uris", true)]
    [InlineData("endpoint", true)]
    [InlineData("endpoints", true)]
    [InlineData("upload_endpoint", true)]
    [InlineData("upload_endpoints", true)]
    [InlineData("curiosity", false)]
    [InlineData("endpointless", false)]
    [InlineData("output_format", false)]
    [InlineData("curly", false)]
    [InlineData("prompt_influence", false)]
    public void IsLink(string key, bool expected) => FalSchemaReader.IsLink(key).Should().Be(expected);

    [Fact]
    public void LinkFields_Reserved_NotInForm_RejectedInParams()
    {
        var fal = AudioCatalog.FindFal(AudioCatalog.FalElevenSfx)!;
        var read = FalSchemaReader.Read(JsonDocument.Parse(WithLinks()).RootElement)!;
        var reserved = FalSchemaReader.Reserved(fal.Fields, fields: read);
        var schema = new AudioParamSchema("fal", fal.Info.Id, AudioSchemaSources.FalOpenApi,
            [.. read.Where(f => !reserved.Contains(f.Key))], reserved);

        schema.Reserved.Should().Contain(LinkKeys);
        schema.Fields.Select(f => f.Key).Should().NotIntersectWith(LinkKeys);
        schema.Fields.Single(f => f.Key == "extra").Fields!.Select(f => f.Key).Should().Equal("gain");

        foreach (var key in LinkKeys)
            AudioParamValidator.Validate(schema, new JsonObject { [key] = "https://evil.example/hook" })
                .Should().Be($"Параметр «{key}» задаётся общим полем запроса, а не в params");
        AudioParamValidator.Validate(schema, new JsonObject { ["extra"] = new JsonObject { ["url"] = "https://evil.example" } })
            .Should().Be("Неизвестный параметр «extra.url»");
        AudioParamValidator.Validate(schema, new JsonObject { ["extra"] = new JsonObject { ["gain"] = 1 } }).Should().BeNull();
    }
}
