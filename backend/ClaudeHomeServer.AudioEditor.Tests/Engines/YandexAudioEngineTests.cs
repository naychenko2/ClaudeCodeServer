using System.Text.Json.Nodes;
using ClaudeHomeServer.AudioEditor.Tests.Fakes;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.AudioEditor;
using ClaudeHomeServer.Services.AudioEditor.Engines;
using ClaudeHomeServer.Services.Media;
using FluentAssertions;

namespace ClaudeHomeServer.AudioEditor.Tests.Engines;

// Драйвер Яндекса поверх шва ITtsEngine: синтез, отказы до запроса, рубли
public sealed class YandexAudioEngineTests
{
    private static readonly AudioEditScope Scope = new(AudioEditScope.Personal, null);

    private static readonly IProgress<AudioProgress> NoProgress = new Progress<AudioProgress>();

    private static AudioRequest Speak(string text = "Привет", JsonObject? fields = null) =>
        new(AudioOp.Speak, YandexAudioEngine.ModelId, Scope, Text: text, Params: fields);

    [Fact]
    public async Task Run_Synthesizes_Mp3AndRubles()
    {
        var tts = new FakeTts();
        var engine = new YandexAudioEngine(tts);

        var result = await engine.RunAsync(Speak("Привет", new JsonObject { ["voice"] = "Jane", ["role"] = "evil", ["speed"] = 1.4 }),
            NoProgress, CancellationToken.None);

        result.Outcome.Should().Be(AudioOutcome.Ok);
        result.Files.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new AudioFile(AudioOutputs.Audio, [7, 7, 7], "audio/mpeg", ".mp3"));
        result.ActualCost.Should().Be(new AudioCost(0.3252, AudioPriceUnits.Rub));
        result.Charged.Should().BeTrue();
        tts.Calls.Should().Equal(("Привет", "jane", "evil", 1.4));
    }

    [Fact]
    public async Task Run_NoVoice_FirstVoiceNeutral()
    {
        var tts = new FakeTts();

        await new YandexAudioEngine(tts).RunAsync(Speak(), NoProgress, CancellationToken.None);

        tts.Calls.Should().Equal(("Привет", "alena", null, 1.0));
    }

    [Theory]
    [InlineData("""{"voice":"zahar"}""", "У Яндекса нет голоса «zahar»")]
    [InlineData("""{"voice":"filipp","role":"evil"}""", "Голос «Филипп» не умеет амплуа «evil»")]
    [InlineData("""{"speed":5}""", "Скорость 5 вне пределов 0.1–3")]
    public async Task Run_BadVoiceRoleSpeed_RejectedWithoutSynthesis(string fields, string reason)
    {
        var tts = new FakeTts();

        var result = await new YandexAudioEngine(tts).RunAsync(Speak("Привет", (JsonObject)JsonNode.Parse(fields)!), NoProgress,
            CancellationToken.None);

        result.Outcome.Should().Be(AudioOutcome.Rejected);
        result.Error.Should().Be(reason);
        result.Charged.Should().BeFalse();
        tts.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Run_TextOverLimit_Rejected()
    {
        var tts = new FakeTts();

        var result = await new YandexAudioEngine(tts).RunAsync(Speak(new string('а', 3001)), NoProgress, CancellationToken.None);

        result.Outcome.Should().Be(AudioOutcome.Rejected);
        result.Error.Should().Be("Текст длиннее 3000 символов (3001)");
        tts.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Run_TextAtLimit_Synthesized()
    {
        var tts = new FakeTts();

        var result = await new YandexAudioEngine(tts).RunAsync(Speak(new string('а', 3000)), NoProgress, CancellationToken.None);

        result.Outcome.Should().Be(AudioOutcome.Ok);
    }

    [Fact]
    public async Task Run_FailureAfterPaidRequests_ChargedInRubles()
    {
        var tts = new FakeTts { Result = TtsSynthesis.Fail("Яндекс не озвучил текст", rub: 0.1626) };

        var result = await new YandexAudioEngine(tts).RunAsync(Speak(), NoProgress, CancellationToken.None);

        result.Outcome.Should().Be(AudioOutcome.Failed);
        result.Charged.Should().BeTrue();
        result.ActualCost.Should().Be(new AudioCost(0.1626, AudioPriceUnits.Rub));
    }

    [Fact]
    public async Task Run_FailureWithoutPaidRequests_NotCharged()
    {
        var tts = new FakeTts { Result = TtsSynthesis.Fail("сеть") };

        var result = await new YandexAudioEngine(tts).RunAsync(Speak(), NoProgress, CancellationToken.None);

        result.Outcome.Should().Be(AudioOutcome.Failed);
        result.Charged.Should().BeFalse();
        result.ActualCost.Should().BeNull();
    }

    [Fact]
    public async Task NoSeam_Disabled()
    {
        var engine = new YandexAudioEngine(null);

        engine.Enabled.Should().BeFalse();
        engine.Registered.Should().BeFalse();
        engine.Models.Should().BeEmpty();
        (await engine.RunAsync(Speak(), NoProgress, CancellationToken.None)).Outcome.Should().Be(AudioOutcome.Unavailable);
    }

    [Fact]
    public void NotConfigured_RegisteredButDisabled()
    {
        var engine = new YandexAudioEngine(new FakeTts { Configured = false });

        engine.Enabled.Should().BeFalse();
        engine.Registered.Should().BeTrue();
    }

    [Fact]
    public void Model_RussianPresetSpeech_LimitFromSeam_RublesAndTtsSource()
    {
        var engine = new YandexAudioEngine(new FakeTts());

        var model = engine.Models.Should().ContainSingle().Subject;
        model.Caps.Ops.Should().Equal(AudioOp.Speak);
        model.Caps.SpeaksRu.Should().BeTrue();
        model.Caps.MaxTextChars.Should().Be(3000);
        engine.PriceUnit.Should().Be(AudioPriceUnits.Rub);
        engine.SpendSource.Should().Be(SpendSources.Tts);
    }

    // ── LiveTTS: вторая модель того же поставщика ───────────────────────────────

    private static AudioRequest SpeakLive(JsonObject? fields = null) =>
        new(AudioOp.Speak, YandexAudioEngine.LiveTtsModelId, Scope, Text: "Привет", Params: fields);

    [Fact]
    public void LiveTts_TwoModels_SpeechKitFirst()
    {
        // И1: «Авто» берёт первую подходящую модель поставщика — дешёвая обязана стоять первой
        var engine = new YandexAudioEngine(new FakeTts { Voices = FakeTts.WithLiveTts });

        engine.Models.Select(m => m.Id).Should().Equal(YandexAudioEngine.ModelId, YandexAudioEngine.LiveTtsModelId);
        engine.Models.Should().OnlyContain(m => m.Caps.Ops.SequenceEqual(new[] { AudioOp.Speak }) && m.Caps.SpeaksRu);
    }

    [Fact]
    public void LiveTts_SeamWithoutLiveTtsVoices_NoLiveTtsModel()
    {
        // Тумблер выключен — шов не отдаёт голосов LiveTTS, и модели нет
        new YandexAudioEngine(new FakeTts()).Models.Select(m => m.Id).Should().Equal(YandexAudioEngine.ModelId);
    }

    [Fact]
    public async Task LiveTts_EmptyVoice_FirstVoiceOfOwnModel()
    {
        // И2: пустой голос — первый голос модели запроса, а не всего шва
        var tts = new FakeTts { Voices = FakeTts.WithLiveTts };
        var engine = new YandexAudioEngine(tts);

        await engine.RunAsync(Speak(), NoProgress, CancellationToken.None);
        await engine.RunAsync(SpeakLive(), NoProgress, CancellationToken.None);

        tts.Calls.Select(c => c.Voice).Should().Equal("alena", "sofia");
    }

    [Fact]
    public async Task LiveTts_VoiceOfOtherModel_RejectedWithoutSeamCall()
    {
        var tts = new FakeTts { Voices = FakeTts.WithLiveTts };
        var engine = new YandexAudioEngine(tts);

        var inGeneral = await engine.RunAsync(Speak("Привет", new JsonObject { ["voice"] = "sofia" }), NoProgress,
            CancellationToken.None);
        var inLive = await engine.RunAsync(SpeakLive(new JsonObject { ["voice"] = "jane" }), NoProgress,
            CancellationToken.None);

        inGeneral.Outcome.Should().Be(AudioOutcome.Rejected);
        inGeneral.Error.Should().Be("Голос «София» — из LiveTTS, выбери модель «SpeechKit LiveTTS»");
        inLive.Outcome.Should().Be(AudioOutcome.Rejected);
        inLive.Error.Should().Contain("Джейн");
        tts.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task LiveTts_RunsWithOwnVoiceAndRole_ParamsWithoutSpeed()
    {
        var tts = new FakeTts { Voices = FakeTts.WithLiveTts };
        var engine = new YandexAudioEngine(tts);

        var result = await engine.RunAsync(SpeakLive(new JsonObject { ["voice"] = "denis", ["role"] = "formal" }),
            NoProgress, CancellationToken.None);

        result.Outcome.Should().Be(AudioOutcome.Ok);
        tts.Calls.Should().Equal(("Привет", "denis", "formal", 1.0));
        engine.ParamNames(engine.Models[1], AudioOp.Speak).Should().BeEquivalentTo(["voice", "role"]);
        engine.ParamNames(engine.Models[0], AudioOp.Speak).Should().BeEquivalentTo(["voice", "role", "speed"]);
    }

    [Fact]
    public async Task LiveTts_ListVoices_FilteredByModel_AllWithModelMarkWithoutModel()
    {
        var engine = new YandexAudioEngine(new FakeTts { Voices = FakeTts.WithLiveTts });

        var general = await engine.ListVoicesAsync(YandexAudioEngine.ModelId, null, CancellationToken.None);
        var live = await engine.ListVoicesAsync(YandexAudioEngine.LiveTtsModelId, null, CancellationToken.None);
        var all = await engine.ListVoicesAsync(null, null, CancellationToken.None);

        general!.Select(v => v.Id).Should().Equal("alena", "filipp", "jane");
        live!.Select(v => v.Id).Should().Equal("sofia", "denis");
        all!.Should().HaveCount(5);
        all.Single(v => v.Id == "sofia").Name.Should().EndWith("· LiveTTS");
        all.Single(v => v.Id == "jane").Name.Should().Be("Джейн");
    }

    [Fact]
    public async Task Estimate_BadVoice_RefusedAtQuote()
    {
        var engine = new YandexAudioEngine(new FakeTts());
        var act = () => engine.EstimateAsync(engine.Models[0], Speak("Привет", new JsonObject { ["voice"] = "nobody" }),
            CancellationToken.None);

        await act.Should().ThrowAsync<AudioEngineUnavailableException>();
    }
}
