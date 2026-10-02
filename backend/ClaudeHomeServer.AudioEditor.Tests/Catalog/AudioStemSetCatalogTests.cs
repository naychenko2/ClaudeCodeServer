using System.Text.Json;
using ClaudeHomeServer.Services.AudioEditor;
using ClaudeHomeServer.Services.AudioEditor.Catalog;
using FluentAssertions;

namespace ClaudeHomeServer.AudioEditor.Tests.Catalog;

// caps.stemSet: панель «Звук» в «Стемах» выбирает модель по набору стемов, а не по имени
public class AudioStemSetCatalogTests
{
    private static IEnumerable<AudioModelInfo> All => AudioCatalog.Local.Select(m => m.Info).Concat(AudioCatalog.Fal.Select(m => m.Info));

    [Theory]
    [InlineData(AudioCatalog.BsRoformer, AudioStemSets.Vocals)]
    [InlineData(AudioCatalog.HtDemucs4, AudioStemSets.Four)]
    [InlineData(AudioCatalog.HtDemucs6, AudioStemSets.Six)]
    [InlineData(AudioCatalog.MelRoformer, AudioStemSets.Karaoke)]
    [InlineData(AudioCatalog.FalDemucs, AudioStemSets.Six)]
    public void SeparateModels_HaveStemSet(string id, string stemSet)
    {
        All.Single(m => m.Id == id).Caps.StemSet.Should().Be(stemSet);
    }

    [Fact]
    public void StemSet_OnlyAtSeparateModels()
    {
        All.Where(m => m.Caps.Ops.Contains(AudioOp.Separate)).Should().OnlyContain(m => m.Caps.StemSet != null);
        All.Where(m => !m.Caps.Ops.Contains(AudioOp.Separate)).Should().OnlyContain(m => m.Caps.StemSet == null);
    }

    [Fact]
    public void Local_CoversAllFourStemSets()
    {
        AudioCatalog.Local.Select(m => m.Info.Caps.StemSet).OfType<string>().Should().BeEquivalentTo(
            [AudioStemSets.Vocals, AudioStemSets.Four, AudioStemSets.Six, AudioStemSets.Karaoke]);
    }

    [Fact]
    public void StemSet_SerializedCamelCase()
    {
        var caps = AudioCatalog.Local.Single(m => m.Info.Id == AudioCatalog.HtDemucs4).Info.Caps;
        var json = JsonSerializer.SerializeToElement(caps, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        json.GetProperty("stemSet").GetString().Should().Be("4");
    }
}
