using ClaudeHomeServer.Services.AudioEditor;
using ClaudeHomeServer.Services.AudioEditor.Catalog;
using ClaudeHomeServer.Services.Media;
using FluentAssertions;

namespace ClaudeHomeServer.AudioEditor.Tests.Catalog;

public class AudioCatalogTests
{
    private static AudioModelInfo Model(string id) => AudioCatalog.Local.Single(m => m.Info.Id == id).Info;

    [Fact]
    public void Local_CoversAllNineLocalMediaOps()
    {
        var covered = AudioCatalog.Local.SelectMany(m => m.Bindings.Values).Select(b => b.Op).ToHashSet();

        covered.Should().BeEquivalentTo(Enum.GetValues<LocalAudioOp>());
        Enum.GetValues<LocalAudioOp>().Should().HaveCount(9);
    }

    [Fact]
    public void Local_ModelsListedInTask_Present()
    {
        AudioCatalog.Local.Select(m => m.Info.Label).Should().Contain(
        [
            "Qwen3-TTS 1.7B", "MOSS-TTS v1.5", "Chatterbox Multilingual", "ACE-Step 1.5 XL", "YuE2-3B", "MiniMax Music 3",
            "DeepFilterNet 3", "Whisper large-v3-turbo", "Seed-VC", "RVC (Applio)",
        ]);
        AudioCatalog.Local.Select(m => m.Info.Id).Should().OnlyHaveUniqueItems();
    }

    [Theory]
    [InlineData(AudioCatalog.Yue2, AudioLicenseKind.NonCommercial, "CC BY-NC 4.0")]
    [InlineData(AudioCatalog.SeedVc, AudioLicenseKind.Copyleft, "GPL-3.0")]
    [InlineData(AudioCatalog.Matchering, AudioLicenseKind.Copyleft, "GPL-3.0")]
    [InlineData(AudioCatalog.MiniMaxMusic, AudioLicenseKind.Unknown, "не указана")]
    [InlineData(AudioCatalog.QwenTts, AudioLicenseKind.Permissive, "Apache-2.0")]
    [InlineData(AudioCatalog.AceStep, AudioLicenseKind.Permissive, "MIT")]
    public void Local_Licenses(string id, AudioLicenseKind kind, string label)
    {
        Model(id).Caps.License.Should().Be(new AudioLicense(label, kind));
    }

    [Fact]
    public void Local_RuFlag_SetForEveryModel()
    {
        // Все локальные модели умеют русский или работают со звуком без языка
        AudioCatalog.Local.Where(m => !m.Info.Caps.SpeaksRu).Select(m => m.Info.Id).Should().BeEmpty();
        Model(AudioCatalog.QwenTts).Caps.SpeaksRu.Should().BeTrue();
        Model(AudioCatalog.BsRoformer).Caps.LanguageNeutral.Should().BeTrue();
        Model(AudioCatalog.QwenTts).Caps.LanguageNeutral.Should().BeFalse();
    }

    [Fact]
    public void SpeaksRu_FalseWithoutRuAndNotNeutral()
    {
        var caps = new AudioCaps([AudioOp.Speak], ["en"], [], [AudioOutputs.Audio], AudioLicenses.Mit, AudioPriceUnits.Free);
        caps.SpeaksRu.Should().BeFalse();
        (caps with { LanguageNeutral = true }).SpeaksRu.Should().BeTrue();
    }

    [Fact]
    public void Local_FreeAndOnlyAiOps_HeavyMatchesLocalMedia()
    {
        AudioCatalog.Local.Should().OnlyContain(m => m.Info.Caps.PriceUnit == AudioPriceUnits.Free);
        AudioCatalog.Local.SelectMany(m => m.Info.Caps.Ops).Should().NotContain(op => AudioOps.IsNoAi(op));
        // Тяжёлые у local-media: обучение голоса и extract/lego/complete (LocalMediaService.IsHeavyAudio)
        var heavy = AudioCatalog.Local.SelectMany(m => m.Info.Caps.Ops.Where(m.Info.Caps.IsHeavy)).ToHashSet();
        heavy.Should().BeEquivalentTo([AudioOp.TrainVoice, AudioOp.Extract, AudioOp.Lego, AudioOp.Complete]);
    }

    [Fact]
    public void NoAiOps_MarkedSeparately()
    {
        AudioOps.NoAi.Should().BeEquivalentTo([AudioOp.Trim, AudioOp.GainFade, AudioOp.Normalize, AudioOp.MixStems, AudioOp.Concat]);
    }

    [Fact]
    public void Resolve_AutoPicksFirstCapable_ExplicitRespected()
    {
        var models = AudioCatalog.Local.Select(m => m.Info).ToList();

        AudioCatalog.Resolve(models, AudioOp.Speak, AudioCatalog.AutoModelId)!.Id.Should().Be(AudioCatalog.QwenTts);
        AudioCatalog.Resolve(models, AudioOp.Speak, AudioCatalog.MossTts)!.Id.Should().Be(AudioCatalog.MossTts);
        AudioCatalog.Resolve(models, AudioOp.Song, AudioCatalog.QwenTts).Should().BeNull();
        AudioCatalog.Resolve(models, AudioOp.Sfx, null).Should().BeNull();
    }

    [Fact]
    public void ProviderOrder_LikeImages()
    {
        AudioCatalog.ProviderOrder.Should().Equal("fal", "higgsfield", "local");
    }
}
