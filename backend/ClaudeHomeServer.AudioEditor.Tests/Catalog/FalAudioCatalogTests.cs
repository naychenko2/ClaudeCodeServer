using System.Text.Json.Nodes;
using ClaudeHomeServer.Services.AudioEditor;
using ClaudeHomeServer.Services.AudioEditor.Catalog;
using ClaudeHomeServer.Services.AudioEditor.Engines;
using FluentAssertions;

namespace ClaudeHomeServer.AudioEditor.Tests.Catalog;

public class FalAudioCatalogTests
{
    private static readonly AudioEditScope Personal = new(AudioEditScope.Personal, null);

    // Отбор задачи 3.1–3.2 (карточка 56858361, раздел 2, столбец fal) — по операциям
    [Fact]
    public void Fal_SelectionByOperation()
    {
        string[] Ids(AudioOp op) => [.. AudioCatalog.Fal.Where(m => m.Info.Caps.Ops.Contains(op)).Select(m => m.Info.Id)];

        Ids(AudioOp.Speak).Should().Equal(AudioCatalog.FalMiniMaxHd, AudioCatalog.FalMiniMaxTurbo, AudioCatalog.FalElevenV4,
            AudioCatalog.FalElevenV3, AudioCatalog.FalQwenTts, AudioCatalog.FalKokoro, AudioCatalog.FalInworld,
            AudioCatalog.FalChatterbox);
        Ids(AudioOp.CloneVoice).Should().BeEquivalentTo(AudioCatalog.FalChatterbox, AudioCatalog.FalQwenClone, AudioCatalog.FalMiniMaxClone);
        Ids(AudioOp.DesignVoice).Should().Equal(AudioCatalog.FalQwenDesign);
        Ids(AudioOp.ConvertVoice).Should().Equal(AudioCatalog.FalVoiceChanger);
        Ids(AudioOp.Transcribe).Should().Equal(AudioCatalog.FalWizper, AudioCatalog.FalScribe);
        Ids(AudioOp.Song).Should().Equal(AudioCatalog.FalElevenMusic, AudioCatalog.FalMiniMaxMusic, AudioCatalog.FalLyria,
            AudioCatalog.FalStableAudio, AudioCatalog.FalAceStep, AudioCatalog.FalSonilo);
        Ids(AudioOp.Cover).Should().Equal(AudioCatalog.FalAceCover, AudioCatalog.FalStableCover);
        Ids(AudioOp.Repaint).Should().Equal(AudioCatalog.FalAceInpaint, AudioCatalog.FalStableInpaint);
        Ids(AudioOp.Outpaint).Should().Equal(AudioCatalog.FalAceOutpaint, AudioCatalog.FalStableOutpaint);
        Ids(AudioOp.Separate).Should().Equal(AudioCatalog.FalDemucs);
        Ids(AudioOp.Denoise).Should().Equal(AudioCatalog.FalDeepFilterNet);
        Ids(AudioOp.Sfx).Should().Equal(AudioCatalog.FalElevenSfx, AudioCatalog.FalMirelo, AudioCatalog.FalStableSfx,
            AudioCatalog.FalSoniloSfx);
        AudioCatalog.Fal.Select(m => m.Info.Id).Should().OnlyHaveUniqueItems();
    }

    // Ориентир — за одну нашу единицу и в той же единице, что у модели: на нём стоит FromHint исполнителя
    [Fact]
    public void Fal_HintUnitMatchesCaps_NoAiOpsAbsent()
    {
        foreach (var m in AudioCatalog.Fal)
        {
            m.Info.PriceHint.Should().NotBeNull(m.Info.Id);
            m.Info.PriceHint!.Unit.Should().Be(m.Info.Caps.PriceUnit, m.Info.Id);
            m.Info.Caps.PriceUnit.Should().BeOneOf(AudioPriceUnits.Chars, AudioPriceUnits.Sec, AudioPriceUnits.Min, AudioPriceUnits.Run);
            m.Info.Caps.Ops.Should().NotContain(AudioOps.NoAi, m.Info.Id);
        }
    }

    [Theory]
    [InlineData(AudioCatalog.FalMiniMaxHd, true)]
    [InlineData(AudioCatalog.FalChatterbox, true)]
    [InlineData(AudioCatalog.FalDemucs, true)]
    [InlineData(AudioCatalog.FalKokoro, false)]
    [InlineData(AudioCatalog.FalLyria, false)]
    public void Fal_RuFlag(string id, bool ru) => AudioCatalog.FindFal(id)!.Info.Caps.SpeaksRu.Should().Be(ru);

    [Theory]
    [InlineData(AudioCatalog.FalChatterbox, AudioLicenseKind.Watermark)]
    [InlineData(AudioCatalog.FalLyria, AudioLicenseKind.Watermark)]
    [InlineData(AudioCatalog.FalQwenTts, AudioLicenseKind.Permissive)]
    [InlineData(AudioCatalog.FalElevenV4, AudioLicenseKind.Permissive)]
    public void Fal_Licenses(string id, AudioLicenseKind kind) =>
        AudioCatalog.FindFal(id)!.Info.Caps.License.Kind.Should().Be(kind);

    // Раскладка: каждое общее поле запроса уходит в поле fal эндпоинта (по схемам fal 2026-10-01)
    public static TheoryData<string, AudioOp, string[]> Layouts => new()
    {
        { AudioCatalog.FalElevenV4, AudioOp.Speak, ["text", "language_code", "seed"] },
        { AudioCatalog.FalKokoro, AudioOp.Speak, ["prompt"] },
        { AudioCatalog.FalChatterbox, AudioOp.CloneVoice, ["text", "voice", "seed"] },
        { AudioCatalog.FalMiniMaxClone, AudioOp.CloneVoice, ["text", "audio_url", "model"] },
        { AudioCatalog.FalScribe, AudioOp.Transcribe, ["language_code", "audio_url"] },
        { AudioCatalog.FalElevenMusic, AudioOp.Song, ["prompt", "music_length_ms"] },
        { AudioCatalog.FalAceStep, AudioOp.Song, ["tags", "lyrics", "duration", "seed"] },
        { AudioCatalog.FalAceCover, AudioOp.Cover, ["tags", "lyrics", "audio_url", "seed", "original_tags"] },
        { AudioCatalog.FalAceInpaint, AudioOp.Repaint, ["tags", "lyrics", "start_time", "end_time", "audio_url", "seed"] },
        { AudioCatalog.FalStableInpaint, AudioOp.Repaint, ["prompt", "mask_start_seconds", "mask_end_seconds", "audio_url", "seed"] },
        { AudioCatalog.FalStableOutpaint, AudioOp.Outpaint, ["prompt", "extend_seconds_after", "audio_url", "seed"] },
        { AudioCatalog.FalElevenSfx, AudioOp.Sfx, ["text", "duration_seconds"] },
        { AudioCatalog.FalMirelo, AudioOp.Sfx, ["text_prompt", "duration", "seed", "num_samples"] },
    };

    [Theory]
    [MemberData(nameof(Layouts))]
    public void Fal_LayoutMapsCommonFields(string id, AudioOp op, string[] fields)
    {
        var model = AudioCatalog.FindFal(id)!;
        var req = new AudioRequest(op, id, Personal, Text: op == AudioOp.Sfx ? null : "Текст", Prompt: "lofi", Lyrics: "[verse]",
            Language: "ru", DurationSec: 30, StartSec: 5, EndSec: 10, Source: new AudioBytes([1], "audio/wav"),
            Reference: new AudioBytes([2], "audio/wav"), Seed: 42);

        var body = FalRequestBuilder.Build(model.Fields, req);

        body.Select(p => p.Key).Should().BeEquivalentTo(fields);
    }

    [Fact]
    public void Fal_LanguageForms()
    {
        Body(AudioCatalog.FalQwenTts, "ru")["language"]!.GetValue<string>().Should().Be("Russian");
        Body(AudioCatalog.FalChatterbox, "ru")["voice"]!.GetValue<string>().Should().Be("russian");
        Body(AudioCatalog.FalScribe, "ru")["language_code"]!.GetValue<string>().Should().Be("rus");
        Body(AudioCatalog.FalMiniMaxHd, "yue")["language_boost"]!.GetValue<string>().Should().Be("Chinese,Yue");
        Body(AudioCatalog.FalElevenMusic, "ru").ContainsKey("language").Should().BeFalse();
        Body(AudioCatalog.FalQwenTts, "auto").ContainsKey("language").Should().BeFalse();
        var unknown = () => Body(AudioCatalog.FalQwenTts, "xx");
        unknown.Should().Throw<ArgumentException>().WithMessage("Модель fal не знает язык «xx»");
    }

    [Fact]
    public void Fal_DefaultsYieldToRequest_FixedOverridesParams()
    {
        var song = FalRequestBuilder.Build(AudioCatalog.FindFal(AudioCatalog.FalMiniMaxMusic)!.Fields,
            new AudioRequest(AudioOp.Song, AudioCatalog.FalMiniMaxMusic, Personal, Prompt: "rock"));
        song["lyrics"]!.GetValue<string>().Should().Be("[instrumental]");

        var withLyrics = FalRequestBuilder.Build(AudioCatalog.FindFal(AudioCatalog.FalMiniMaxMusic)!.Fields,
            new AudioRequest(AudioOp.Song, AudioCatalog.FalMiniMaxMusic, Personal, Prompt: "rock", Lyrics: "[verse] ля"));
        withLyrics["lyrics"]!.GetValue<string>().Should().Be("[verse] ля");

        var speech = FalRequestBuilder.Build(AudioCatalog.FindFal(AudioCatalog.FalMiniMaxHd)!.Fields,
            new AudioRequest(AudioOp.Speak, AudioCatalog.FalMiniMaxHd, Personal, Text: "a",
                Params: new JsonObject { ["output_format"] = "hex" }));
        speech["output_format"]!.GetValue<string>().Should().Be("url");
    }

    private static JsonObject Body(string id, string language) =>
        FalRequestBuilder.Build(AudioCatalog.FindFal(id)!.Fields,
            new AudioRequest(AudioCatalog.FindFal(id)!.Info.Caps.Ops[0], id, Personal, Text: "a", Language: language,
                Source: new AudioBytes([1], "audio/wav")));
}
