using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeHomeServer.Services.AudioEditor;
using ClaudeHomeServer.Services.AudioEditor.Catalog;
using ClaudeHomeServer.Services.AudioEditor.Engines;
using ClaudeHomeServer.Services.AudioEditor.Schema;
using FluentAssertions;

namespace ClaudeHomeServer.AudioEditor.Tests.Schema;

public sealed class AudioSchemaTests
{
    // Форма OpenAPI очереди fal: тело POST — $ref на компонент, вложенный объект — allOf из одной ссылки,
    // необязательное число — anyOf с null
    internal const string OpenApi = """
    {
      "openapi": "3.0.4",
      "paths": {
        "/fal-ai/x": { "post": { "requestBody": { "required": true, "content": { "application/json": {
          "schema": { "$ref": "#/components/schemas/XInput" } } } } } },
        "/fal-ai/x/requests/{request_id}": { "get": {} }
      },
      "components": { "schemas": {
        "XInput": {
          "type": "object",
          "required": ["text"],
          "x-fal-order-properties": ["text", "duration_seconds", "prompt_influence", "voice_setting", "output_format"],
          "properties": {
            "output_format": { "type": "string", "enum": ["mp3_44100_128", "pcm_16000"], "default": "mp3_44100_128" },
            "prompt_influence": { "type": "number", "minimum": 0, "maximum": 1, "default": 0.3, "title": "Prompt Influence" },
            "text": { "type": "string", "maxLength": 450 },
            "duration_seconds": { "anyOf": [ { "type": "number", "minimum": 0.5, "maximum": 22 }, { "type": "null" } ] },
            "voice_setting": { "allOf": [ { "$ref": "#/components/schemas/VoiceSetting" } ], "description": "Голос" }
          }
        },
        "VoiceSetting": { "type": "object", "properties": {
          "speed": { "type": "number", "minimum": 0.5, "maximum": 2, "default": 1 },
          "emotion": { "type": "string", "enum": ["happy", "sad"] }
        } }
      } }
    }
    """;

    [Fact]
    public void FalReader_ReadsInputSchema_RefsNullableNestedAndOrder()
    {
        var fields = FalSchemaReader.Read(JsonDocument.Parse(OpenApi).RootElement)!;

        fields.Select(f => f.Key).Should().Equal("text", "duration_seconds", "prompt_influence", "voice_setting", "output_format");
        var influence = fields.Single(f => f.Key == "prompt_influence");
        influence.Type.Should().Be(AudioParamTypes.Number);
        (influence.Min, influence.Max).Should().Be((0, 1));
        influence.Default!.GetValue<double>().Should().Be(0.3);
        var duration = fields.Single(f => f.Key == "duration_seconds");
        duration.Nullable.Should().BeTrue();
        duration.Max.Should().Be(22);
        var voice = fields.Single(f => f.Key == "voice_setting");
        voice.Type.Should().Be(AudioParamTypes.Object);
        voice.Description.Should().Be("Голос");
        voice.Fields!.Select(f => f.Key).Should().BeEquivalentTo(["speed", "emotion"]);
        fields.Single(f => f.Key == "output_format").Enum!.Select(v => v.GetValue<string>())
            .Should().Equal("mp3_44100_128", "pcm_16000");
        fields.Single(f => f.Key == "text").MaxLength.Should().Be(450);
    }

    [Fact]
    public void FalReader_NoRequestBody_Null()
    {
        FalSchemaReader.Read(JsonDocument.Parse("""{"paths":{}}""").RootElement).Should().BeNull();
    }

    [Fact]
    public void FalReserved_CommonFieldsAndFixed()
    {
        var mm = AudioCatalog.FindFal(AudioCatalog.FalMiniMaxHd)!;
        FalSchemaReader.Reserved(mm.Fields).Should().BeEquivalentTo(["prompt", "language_boost", "output_format"]);
    }

    // ── local ───────────────────────────────────────────────────────────────────

    [Fact]
    public void LocalSchema_EveryModelAndOp_HasSchema_WithReservedCommonFields()
    {
        foreach (var model in AudioCatalog.Local)
        foreach (var op in model.Bindings.Keys)
        {
            var schema = AudioCatalog.LocalSchema(model.Info.Id, op);
            schema.Should().NotBeNull($"{model.Info.Id}/{op}");
            schema!.Source.Should().Be(AudioSchemaSources.LocalCatalog);
            schema.Reserved.Should().Contain(["text", "lyrics", "language", "duration_seconds", "prompt", "seed"]);
            foreach (var fixedArg in model.Bindings[op].Args.Keys) schema.Reserved.Should().Contain(fixedArg);
            schema.Fields.Select(f => f.Key).Should().NotIntersectWith(schema.Reserved, $"{model.Info.Id}/{op}");
            schema.Fields.Select(f => f.Key).Should().OnlyHaveUniqueItems();
        }
        AudioCatalog.LocalSchema(AudioCatalog.Whisper, AudioOp.Speak).Should().BeNull();
    }

    // Скрытые параметры движков (матрица, раздел 2.6) описаны, но честно помечены «пока не передаётся»
    [Theory]
    [InlineData(AudioCatalog.AceStep, AudioOp.Song, "timesignature,cfg_scale,temperature,top_p,steps,cfg,shift")]
    [InlineData(AudioCatalog.AceStep, AudioOp.Cover, "steps,guidance,shift")]
    [InlineData(AudioCatalog.Yue2, AudioOp.Song, "temperature,top_k,repetition_penalty")]
    [InlineData(AudioCatalog.MiniMaxMusic, AudioOp.Song, "cfg,top_k,steps")]
    [InlineData(AudioCatalog.Chatterbox, AudioOp.Speak, "cfg_weight")]
    [InlineData(AudioCatalog.SeedVc, AudioOp.ConvertVoice, "diffusion_steps,convert_style")]
    [InlineData(AudioCatalog.Rvc, AudioOp.TrainVoice, "sample_rate,batch_size")]
    [InlineData(AudioCatalog.Rvc, AudioOp.ConvertVoice, "index_rate")]
    [InlineData(AudioCatalog.DeepFilterNet, AudioOp.Denoise, "atten_db")]
    [InlineData(AudioCatalog.AudioSr, AudioOp.Upsample, "ddim_steps,guidance_scale")]
    [InlineData(AudioCatalog.BasicPitch, AudioOp.ToMidi, "onset_threshold,frame_threshold,min_note_ms")]
    [InlineData(AudioCatalog.Whisper, AudioOp.Transcribe, "beam_size,vad_filter")]
    public void LocalSchema_HiddenParams_DescribedButNotPassed(string model, AudioOp op, string hidden)
    {
        var schema = AudioCatalog.LocalSchema(model, op)!;
        foreach (var key in hidden.Split(','))
        {
            var field = schema.Fields.Should().ContainSingle(f => f.Key == key, $"{model}/{op}").Subject;
            field.Passed.Should().BeFalse();
            field.NotPassed.Should().Be(AudioCatalog.LocalNotPassed);
            AudioParamValidator.Validate(schema, new JsonObject { [key] = 1 })
                .Should().StartWith($"Параметр «{key}» пока не передаётся");
        }
    }

    [Fact]
    public void LocalSchema_PassedParams_ValidatedByRange()
    {
        var song = AudioCatalog.LocalSchema(AudioCatalog.AceStep, AudioOp.Song)!;
        AudioParamValidator.Validate(song, new JsonObject { ["bpm"] = 140, ["key"] = "A minor" }).Should().BeNull();
        AudioParamValidator.Validate(song, new JsonObject { ["bpm"] = 300 }).Should().Be("Параметр «bpm» — от 40 до 220");
        AudioParamValidator.Validate(song, new JsonObject { ["engine"] = "yue2" })
            .Should().Be("Параметр «engine» задаётся общим полем запроса, а не в params");
        AudioParamValidator.Validate(song, new JsonObject { ["strength"] = 0.5 })
            .Should().Be($"Неизвестный параметр «strength» у модели {AudioCatalog.AceStep}");

        var complete = AudioCatalog.LocalSchema(AudioCatalog.AceStep, AudioOp.Complete)!;
        AudioParamValidator.Validate(complete, new JsonObject { ["tracks"] = new JsonArray("drums", "kazoo") })
            .Should().StartWith("Параметр «tracks[1]» — одно из:");
    }

    [Fact]
    public async Task LocalEngine_SchemaFromCatalog()
    {
        var engine = new LocalAudioEngine(null);
        var model = AudioCatalog.FindLocal(AudioCatalog.Chatterbox)!.Info;

        var lookup = await engine.SchemaAsync(model, AudioOp.Speak, CancellationToken.None);
        var miss = await engine.SchemaAsync(model, AudioOp.Song, CancellationToken.None);

        lookup.Schema!.Fields.Select(f => f.Key).Should().Equal("expressiveness", "cfg_weight");
        miss.Schema.Should().BeNull();
        miss.Error.Should().NotBeNullOrEmpty();
    }
}
